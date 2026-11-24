using Microsoft.Win32;
using MmiConverter.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MmiConverter;

public partial class LicensesWindow : Window
{
    public LicensesWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyWindowChrome(this);
        string[] texts = ["Application-LICENSE.txt", "Notices.txt", "FFmpeg-LICENSE.txt", "FFmpeg-LGPL.txt", "FFmpeg-LGPLv3.txt", "Jpeg-LICENSE.txt", "Jpeg-IJG.txt"];
        LicenseText.Text = string.Join(Environment.NewLine + Environment.NewLine, texts.Select(ReadText));
    }

    private static string ReadText(string name)
    {
        using var reader = new StreamReader(NativeTools.Resource("ThirdParty." + name));
        return reader.ReadToEnd();
    }

    private async void OnSaveSource(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog
        {
            FileName = "FFmpeg-9.1-source.zip", Filter = "Source archive (*.zip)|*.zip",
            DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true
        };
        if (picker.ShowDialog(this) != true) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            using Stream source = NativeTools.Resource("ThirdParty.FFmpeg-source.zip");
            using var destination = File.Create(picker.FileName);
            await source.CopyToAsync(destination);
            SaveStatus.Text = "FFmpeg source saved";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveStatus.Text = ex.Message;
        }
        finally { button.IsEnabled = true; }
    }
}
