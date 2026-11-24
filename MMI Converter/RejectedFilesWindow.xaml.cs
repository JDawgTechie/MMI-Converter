using MmiConverter.Services;
using System.Windows;

namespace MmiConverter;

internal sealed record RejectedFile(string Path, string Reason);

public partial class RejectedFilesWindow : Window
{
    internal RejectedFilesWindow(IReadOnlyList<RejectedFile> rejected)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        Explanation.Text = rejected.Count == 1
            ? "This file was not opened"
            : "These files were not opened";
        RejectedList.ItemsSource = rejected;
    }
}
