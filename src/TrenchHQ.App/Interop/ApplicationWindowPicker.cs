using TrenchHQ.Infrastructure.Windows;
using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TrenchHQ.Interop
{
    internal static class ApplicationWindowPicker
    {
        internal static async Task<ApplicationWindowSelection?> ShowAsync(XamlRoot root)
        {
            var picker = new ListView
            {
                DisplayMemberPath = nameof(ApplicationWindowChoice.Label),
                SelectionMode = ListViewSelectionMode.Single, MaxHeight = 300
            };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var refresh = new Button { Content = "Refresh list" };
            var method = new ComboBox { Header = "Pinning method", HorizontalAlignment = HorizontalAlignment.Stretch };
            method.Items.Add("Native lock (default)");
            var embeddedMethod = new ComboBoxItem { Content = "Embedded + lock (experimental)" };
            method.Items.Add(embeddedMethod);
            method.SelectedIndex = 0;
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(method);
            content.Children.Add(new TextBlock
            {
                Text = "Start with Native lock. If an app doesn't work properly, unpin it and try the other method.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12
            });
            content.Children.Add(refresh);
            content.Children.Add(picker);
            content.Children.Add(status);
            var dialog = new ContentDialog
            {
                XamlRoot = root, Title = "Pin application window", Content = content,
                PrimaryButtonText = "Pin", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false
            };
            void Refresh()
            {
                picker.SelectedItem = null;
                var choices = ApplicationWindowPin.GetWindows();
                picker.ItemsSource = choices;
                dialog.IsPrimaryButtonEnabled = false;
                status.Text = choices.Length == 0 ? "No supported windows available. Open a resizable, non-admin x64 app, then refresh."
                    : "Choose an app. Maximized and minimized windows are restored automatically.";
            }
            picker.SelectionChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = picker.SelectedItem is ApplicationWindowChoice;
                var canEmbed = picker.SelectedItem is not ApplicationWindowChoice selected || ApplicationWindowPin.SupportsEmbedding(selected.Handle);
                embeddedMethod.IsEnabled = canEmbed;
                if (!canEmbed && method.SelectedIndex == 1) method.SelectedIndex = 0;
                if (picker.SelectedItem is ApplicationWindowChoice)
                    status.Text = canEmbed ? string.Empty : "This Windows-hosted app uses Native lock.";
            };
            refresh.Click += (_, _) => Refresh();
            Refresh();
            return await dialog.ShowAsync() == ContentDialogResult.Primary && picker.SelectedItem is ApplicationWindowChoice selected
                ? new ApplicationWindowSelection(selected, (ApplicationWindowPinMode)method.SelectedIndex) : null;
        }
    }
}
