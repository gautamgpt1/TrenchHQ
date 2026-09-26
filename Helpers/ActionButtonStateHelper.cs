using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TrenchHQ.Helpers
{
    internal static class ActionButtonStateHelper
    {
        private static readonly Color ActiveBackgroundColor = Color.FromArgb(0xFF, 0x4C, 0xE5, 0xC1);
        private static readonly Color ActiveForegroundColor = Color.FromArgb(0xFF, 0x0B, 0x0E, 0x13);
        private static readonly Color InactiveBackgroundColor = Color.FromArgb(0xFF, 0x13, 0x2F, 0x2A);
        private static readonly Color InactiveForegroundColor = Color.FromArgb(0xFF, 0x6D, 0x92, 0x8A);

        public static void Apply(Button? button, bool isDirty)
        {
            if (button == null)
            {
                return;
            }

            var background = isDirty ? ActiveBackgroundColor : InactiveBackgroundColor;
            var foreground = isDirty ? ActiveForegroundColor : InactiveForegroundColor;

            button.Background = new SolidColorBrush(background);
            button.Foreground = new SolidColorBrush(foreground);
            button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(background);
            button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(background);
            button.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(background);
            button.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(background);
            button.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(foreground);
            button.Resources["ButtonForegroundPressed"] = new SolidColorBrush(foreground);
        }
    }
}
