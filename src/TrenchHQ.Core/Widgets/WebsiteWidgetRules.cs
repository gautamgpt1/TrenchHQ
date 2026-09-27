using System;
using System.IO;

namespace TrenchHQ.Core.Widgets
{
    internal static class WebsiteWidgetRules
    {
        internal static bool UseLowMemory(bool hostVisible, bool minimized, bool widgetVisible) =>
            !hostVisible || minimized || !widgetVisible;

        internal static (string Folder, string? ProfileName) BrowserStorage(string localFolder, Guid widgetId, bool hasLegacyFolder) =>
            hasLegacyFolder
                ? (Path.Combine(localFolder, "WebsiteProfiles", widgetId.ToString("N")), null)
                : (Path.Combine(localFolder, "WebsiteBrowser"), "widget_" + widgetId.ToString("N"));

        internal static bool CanLoadInViewport(double width, double height) =>
            double.IsFinite(width) && double.IsFinite(height) && width >= 320 && height >= 200;

        // Status messages overlay the page; only the 44-DIP panel controls consume height.
        internal static (int Width, int Height) MinimumDockedSize(double dpiScale)
        {
            var dpi = double.IsFinite(dpiScale) ? Math.Max(1, dpiScale) : 1;
            return ((int)Math.Ceiling(320 * dpi) + 2, (int)Math.Ceiling(244 * dpi) + 2);
        }

        internal static bool TrySaveUrl(string? value, out string normalized, out string error)
        {
            var candidate = value?.Trim() ?? string.Empty;
            normalized = string.Empty;
            error = string.Empty;
            if (candidate.Length == 0) return true;
            if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "https://" + candidate;
            if (TryUrl(candidate, out normalized)) return true;
            error = "Enter a website address such as example.com, or leave it blank. Only HTTPS pages without embedded credentials are supported.";
            return false;
        }

        internal static bool TryUrl(string? value, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 2048
                || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0
                || string.IsNullOrEmpty(uri.Host)) return false;
            normalized = uri.AbsoluteUri;
            return true;
        }

        internal static double Zoom(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.5, 2) : 1;
    }
}
