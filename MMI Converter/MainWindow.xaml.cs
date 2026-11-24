using Microsoft.Win32;
using MmiConverter.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MmiConverter;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DiscRow> rows = [];
    private readonly NativeTools native = new();
    private readonly DiscConverter converter;
    private readonly SemaphoreSlim estimateSlots = new(2);
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<Task> estimates = [];
    private readonly string[] initialFiles;
    private readonly string preference = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MmiConverter", "window.json");
    private CancellationTokenSource? conversion;
    private Task? batch;
    private bool initialized, closeAfterCleanup, checkingFiles;

    public MainWindow(string[] files)
    {
        initialFiles = files;
        converter = new DiscConverter(native);
        InitializeComponent();
        WindowChromeService.Attach(this, WindowFrame);
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        ThemeToggle.IsChecked = ThemeService.IsDark;
        ThemeText.Text = ThemeService.IsDark ? "Dark" : "Light";
        Queue.ItemsSource = rows;
        RestorePlacement();
        initialized = true;
        if (initialFiles.Length > 0) Loaded += async (_, _) => await AddFilesAsync(initialFiles);
    }

    private int SelectedQuality => (int)Quality.Value;

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (!CanAdd) return;
        var picker = new OpenFileDialog
        {
            Filter = "LaserActive MMI (*.mmi)|*.mmi|All files (*.*)|*.*",
            Multiselect = true, CheckFileExists = true, Title = "Open MMI files"
        };
        if (picker.ShowDialog(this) == true) await AddFilesAsync(picker.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanAdd && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private bool CanAdd => conversion is null && !checkingFiles && MainPage.Visibility == Visibility.Visible && !shutdown.IsCancellationRequested;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (CanAdd && e.Data.GetData(DataFormats.FileDrop) is string[] files) await AddFilesAsync(files);
    }

    private async Task AddFilesAsync(string[] files)
    {
        if (!CanAdd) return;
        checkingFiles = true;
        Refresh();
        var token = shutdown.Token;
        try
        {
            var checkedFiles = await Task.Run(() => files.Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
            {
                token.ThrowIfCancellationRequested();
                string? error = DiscConverter.CheckInput(path, out long size);
                return (Path: error is null ? Path.GetFullPath(path) : path, Size: size, Error: error);
            }).ToArray(), token);
            token.ThrowIfCancellationRequested();
            var rejected = new List<RejectedFile>();
            foreach (var file in checkedFiles)
            {
                if (file.Error is { } reason)
                {
                    rejected.Add(new RejectedFile(file.Path, reason));
                    continue;
                }
                if (rows.Any(row => row.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase))) continue;
                var row = new DiscRow(file.Path, file.Size, token);
                rows.Add(row);
                estimates.RemoveAll(task => task.IsCompleted);
                row.AnalysisTask = AnalyzeAsync(row);
                estimates.Add(row.AnalysisTask);
            }
            if (rejected.Count > 0)
                new RejectedFilesWindow(rejected) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            checkingFiles = false;
            if (!token.IsCancellationRequested) Refresh();
        }
    }

    private async Task AnalyzeAsync(DiscRow row)
    {
        try
        {
            await estimateSlots.WaitAsync(row.Analysis.Token);
            try { row.Profile = await Task.Run(() => converter.AnalyzeAsync(row.Path, row.Analysis.Token), row.Analysis.Token); }
            finally { estimateSlots.Release(); }
        }
        catch (OperationCanceledException) when (row.Analysis.IsCancellationRequested) { }
        catch (Exception ex) { row.AnalysisError = ex.Message; }
        finally
        {
            if (conversion is null && !shutdown.IsCancellationRequested) Refresh();
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        if (conversion is not null) return;
        foreach (DiscRow row in Queue.SelectedItems.Cast<DiscRow>().ToArray())
        {
            row.Analysis.Cancel();
            rows.Remove(row);
            _ = DisposeRemovedAnalysisAsync(row);
        }
        Refresh();
    }

    private static async Task DisposeRemovedAnalysisAsync(DiscRow row)
    {
        await row.AnalysisTask;
        row.Analysis.Dispose();
    }

    private void OnSelection(object sender, SelectionChangedEventArgs e) =>
        RemoveButton.IsEnabled = conversion is null && Queue.SelectedItems.Count > 0;

    private void OnQuality(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (initialized) Refresh();
    }

    private void Refresh(bool keepStatus = false)
    {
        QualityValue.Text = SelectedQuality.ToString();
        DropHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int ready = 0, pending = 0, failed = 0;
        long saved = 0;
        foreach (var row in rows)
        {
            row.Refresh(SelectedQuality);
            if (row.Profile is null)
            {
                if (row.AnalysisError is null) pending++; else failed++;
                continue;
            }
            ready++;
            saved += row.OriginalSize - row.SizeAt(SelectedQuality);
        }
        SavingsText.Text = ready == 0 ? "—" : (pending > 0 ? "~" : "") + DiscRow.FormatSize(saved);
        OpenButton.IsEnabled = conversion is null && !checkingFiles;
        Quality.IsEnabled = conversion is null;
        RemoveButton.IsEnabled = conversion is null && Queue.SelectedItems.Count > 0;
        ConvertButton.IsEnabled = conversion is null && !checkingFiles && ready > 0 && pending == 0;
        CancelButton.Visibility = conversion is null ? Visibility.Collapsed : Visibility.Visible;
        if (keepStatus || conversion is not null) return;
        StatusText.Text = checkingFiles ? "Checking selected files…"
            : pending > 0 ? $"Estimating sizes · {ready + failed} of {rows.Count}"
            : failed > 0 ? $"{failed} unsupported or unreadable · See the affected rows"
            : "Converted discs are saved beside their source files";
    }

    private async void OnConvert(object sender, RoutedEventArgs e)
    {
        if (conversion is not null || checkingFiles) return;
        var queue = rows.Where(row => row.Profile is not null && !row.HasOutput(SelectedQuality)).ToArray();
        if (queue.Length == 0)
        {
            StatusText.Text = "These discs are already converted at this quality";
            return;
        }
        int quality = SelectedQuality;
        using var current = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        conversion = current;
        Refresh();
        ProgressArea.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;
        batch = ConvertBatchAsync(queue, quality, current.Token);
        try { await batch; }
        finally
        {
            batch = null;
            conversion = null;
            if (!shutdown.IsCancellationRequested) Refresh(keepStatus: true);
        }
    }

    private async Task ConvertBatchAsync(DiscRow[] queue, int quality, CancellationToken stop)
    {
        int complete = 0, failed = 0;
        for (int i = 0; i < queue.Length && !stop.IsCancellationRequested; i++)
        {
            DiscRow row = queue[i];
            int item = i + 1;
            row.Active = true;
            row.ConversionError = null;
            row.Refresh(quality);
            Queue.ScrollIntoView(row);
            FileProgress.Value = 0;
            PercentText.Text = "0%";
            ProgressText.Text = $"{item} of {queue.Length} · {row.Name}";
            ProgressText.ToolTip = row.Path;
            int lastPercent = -1;
            try
            {
                string output = await Task.Run(() => converter.ConvertAsync(row.Path, row.Profile, quality, (stage, percent) =>
                {
                    int rounded = (int)percent;
                    int old = Volatile.Read(ref lastPercent);
                    if (rounded <= old || Interlocked.CompareExchange(ref lastPercent, rounded, old) != old) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!row.Active || rounded < FileProgress.Value) return;
                        ProgressText.Text = $"{item} of {queue.Length} · {stage} · {row.Name}";
                        FileProgress.Value = rounded;
                        PercentText.Text = $"{rounded}%";
                    });
                }, stop), stop);
                row.Results[quality] = output;
                complete++;
                FileProgress.Value = 100;
                PercentText.Text = "100%";
                Refresh(keepStatus: true);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                row.ConversionError = ex.Message;
                failed++;
            }
            finally
            {
                row.Active = false;
                row.Refresh(quality);
            }
        }
        if (stop.IsCancellationRequested)
            ProgressText.Text = $"Canceled · {complete} completed";
        else
            ProgressText.Text = failed == 0 ? $"{complete} completed" : $"{complete} completed · {failed} failed";
        if (stop.IsCancellationRequested || failed > 0) PercentText.Text = "";
        StatusText.Text = failed == 0 ? "Converted discs are saved beside their source files" : "See affected rows for conversion errors";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        conversion?.Cancel();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        bool show = SettingsPage.Visibility != Visibility.Visible;
        SettingsPage.Visibility = InformationButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MainPage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SettingsButton.Content = show ? "\uE72B" : "\uE713";
        SettingsButton.ToolTip = show ? "Back" : "Settings";
        AutomationProperties.SetName(SettingsButton, (string)SettingsButton.ToolTip);
    }

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        bool dark = ThemeToggle.IsChecked == true;
        ThemeText.Text = dark ? "Dark" : "Light";
        ThemeService.Apply(dark);
    }

    private void OnLicenses(object sender, RoutedEventArgs e) => new LicensesWindow { Owner = this }.ShowDialog();
    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaximizeButton is null) return;
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control && CanAdd)
        {
            OnOpen(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Queue.IsKeyboardFocusWithin && CanAdd)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || closeAfterCleanup) return;
        e.Cancel = true;
        if (shutdown.IsCancellationRequested) return;
        shutdown.Cancel();
        StorePlacement();
        await System.Windows.Threading.Dispatcher.Yield();
        await Task.WhenAll(estimates.Append(batch ?? Task.CompletedTask));
        foreach (var row in rows) row.Analysis.Dispose();
        native.Dispose();
        estimateSlots.Dispose();
        shutdown.Dispose();
        closeAfterCleanup = true;
        Close();
    }

    private sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized);

    private void RestorePlacement()
    {
        try
        {
            if (!File.Exists(preference)) return;
            var saved = JsonSerializer.Deserialize<Placement>(File.ReadAllText(preference));
            if (saved is null || !double.IsFinite(saved.Left) || !double.IsFinite(saved.Top) ||
                !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height)) return;
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var bounds = new Rect(saved.Left, saved.Top, Math.Max(MinWidth, saved.Width), Math.Max(MinHeight, saved.Height));
            var visible = Rect.Intersect(screen, bounds);
            if (visible.IsEmpty || visible.Width < 80 || visible.Height < 80) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
            if (saved.Maximized) WindowState = WindowState.Maximized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void StorePlacement()
    {
        try
        {
            Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (bounds.IsEmpty) return;
            Directory.CreateDirectory(Path.GetDirectoryName(preference)!);
            string pending = preference + "." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(pending, JsonSerializer.Serialize(new Placement(bounds.Left, bounds.Top, bounds.Width,
                    bounds.Height, WindowState == WindowState.Maximized)));
                File.Move(pending, preference, overwrite: true);
            }
            finally { File.Delete(pending); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed class DiscRow(string path, long original, CancellationToken stop) : INotifyPropertyChanged
{
    private bool active;
    public bool Active
    {
        get => active;
        set
        {
            if (active == value) return;
            active = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
        }
    }
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public long OriginalSize { get; } = original;
    public string OriginalText => FormatSize(OriginalSize);
    public string EstimateText { get; private set; } = "Estimating…";
    public string Detail { get; private set; } = path;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal CancellationTokenSource Analysis { get; } = CancellationTokenSource.CreateLinkedTokenSource(stop);
    internal DiscProfile? Profile { get; set; }
    internal Task AnalysisTask { get; set; } = Task.CompletedTask;
    internal string? AnalysisError { get; set; }
    internal string? ConversionError { get; set; }
    internal Dictionary<int, string> Results { get; } = [];

    internal bool HasOutput(int quality) => Results.TryGetValue(quality, out string? output) && File.Exists(output);
    internal long SizeAt(int quality) => HasOutput(quality) ? new FileInfo(Results[quality]).Length : Profile!.Estimate(quality);

    internal void Refresh(int quality)
    {
        bool complete = HasOutput(quality);
        EstimateText = AnalysisError is not null ? "Unavailable"
            : ConversionError is not null ? "Failed"
            : Profile is null ? "Estimating…"
            : (complete ? "" : "~") + FormatSize(SizeAt(quality));
        Detail = Path + Environment.NewLine + (AnalysisError ?? ConversionError ??
            (complete ? $"Saved to {Results[quality]}" : $"{OriginalSize:N0} bytes"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EstimateText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
    }

    internal static string FormatSize(long size)
    {
        double magnitude = Math.Abs((double)size);
        if (magnitude >= 1e12) return $"{size / 1e12:N2} TB";
        if (magnitude >= 1e9) return $"{size / 1e9:N2} GB";
        if (magnitude >= 1e6) return $"{size / 1e6:N1} MB";
        if (magnitude >= 1e3) return $"{size / 1e3:N1} kB";
        return $"{size:N0} B";
    }
}
