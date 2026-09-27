using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage;
using global::Windows.Foundation.Collections;

namespace TrenchHQ.Presentation
{
    internal sealed class UiSettings
    {
        public bool LaunchOnStartup { get; set; }
    }

    internal static class SettingsService
    {
        private const string LaunchOnStartupKey = "LaunchOnStartup";
        private const string MainNavigationOpenKey = "MainNavigationOpen";

        private static readonly UiSettings DefaultUiSettings = new()
        {
            LaunchOnStartup = false
        };

        public static Task InitializeAsync()
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            EnsureUiDefaults(localSettings.Values);

            return Task.CompletedTask;
        }

        public static UiSettings ReadUiSettings()
        {
            var values = ApplicationData.Current.LocalSettings.Values;

            return new UiSettings
            {
                LaunchOnStartup = ReadBool(values, LaunchOnStartupKey, DefaultUiSettings.LaunchOnStartup)
            };
        }

        public static void SaveLaunchOnStartupSnapshot(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[LaunchOnStartupKey] = enabled;
        }

        public static bool ReadMainNavigationOpen()
        {
            return ReadBool(ApplicationData.Current.LocalSettings.Values, MainNavigationOpenKey, true);
        }

        public static void SaveMainNavigationOpen(bool isOpen)
        {
            ApplicationData.Current.LocalSettings.Values[MainNavigationOpenKey] = isOpen;
        }

        private static bool ReadBool(IPropertySet values, string key, bool fallback)
        {
            if (values.TryGetValue(key, out var value) && value is bool stored)
            {
                return stored;
            }

            return fallback;
        }

        private static void EnsureUiDefaults(IPropertySet values)
        {
            if (!values.ContainsKey(LaunchOnStartupKey))
            {
                values[LaunchOnStartupKey] = DefaultUiSettings.LaunchOnStartup;
            }

        }
    }
}
