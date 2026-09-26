using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using TrenchHQ.Helpers;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace TrenchHQ.Views
{
    public sealed partial class HelpTabView : UserControl
    {
        public HelpTabView()
        {
            InitializeComponent();
            var version = typeof(App).Assembly.GetName().Version!;
            VersionText.Text = $"TrenchHQ {version} · x64 · Read-only market monitoring";
        }

        private async void OnPrivacyClick(object sender, RoutedEventArgs e)
        {
            PrivacyButton.IsEnabled = false;
            try
            {
                var policy = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Privacy.txt"));
                await new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "Privacy", CloseButtonText = "Close",
                    Content = new ScrollViewer { MaxHeight = 480, Content = new TextBlock
                    { Text = policy, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }
                }.ShowAsync();
            }
            catch { SupportStatus.Text = "Could not open the bundled privacy policy. See the public privacy page before using online features."; }
            finally { PrivacyButton.IsEnabled = true; }
        }

        private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
        {
            ExportDiagnosticsButton.IsEnabled = false;
            try
            {
                var picker = new FileSavePicker { SuggestedFileName = "TrenchHQ-diagnostics" };
                picker.FileTypeChoices.Add("JSON diagnostics", new[] { ".json" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;
                await FileIO.WriteTextAsync(file, ReleaseDiagnostics.Create(
                    typeof(App).Assembly.GetName().Version!, OnChainPipelineDiagnostics.Snapshot()));
                SupportStatus.Text = "Diagnostics saved locally. Review the file before sharing it with support.";
            }
            catch { SupportStatus.Text = "Could not save diagnostics. Choose a writable folder and try again."; }
            finally { ExportDiagnosticsButton.IsEnabled = true; }
        }
    }
}
