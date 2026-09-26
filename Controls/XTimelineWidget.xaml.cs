using TrenchHQ.Helpers;
using TrenchHQ.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Windows.System;

namespace TrenchHQ.Controls
{
    public sealed partial class XTimelineWidget : UserControl, IDisposable
    {
        private readonly Guid _owner = Guid.NewGuid();
        private readonly ObservableCollection<XTimelineRow> _rows = [];
        private readonly DispatcherTimer _expiry = new() { Interval = TimeSpan.FromSeconds(30) };
        private SocialFeedService? _service;
        private SavedWidgetDefinition? _definition;
        private bool _disposed;
        private bool _horizontal;
        private double _contentScale = 1d;
        private int _refreshQueued;
        private WidgetContentStatus _contentStatus = new(WidgetContentState.Empty, "Not configured");

        internal WidgetContentStatus ContentStatus => _contentStatus;
        internal event EventHandler? ContentStatusChanged;

        public XTimelineWidget()
        {
            InitializeComponent();
            PostItems.ItemsSource = _rows;
            if (Application.Current is App app) _service = app.SocialFeed;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            _expiry.Tick += (_, _) => Refresh();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_service == null || _disposed) return;
            _service.Changed -= OnChanged;
            _service.Changed += OnChanged;
            Register();
            _expiry.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _expiry.Stop();
            if (_service == null) return;
            _service.Changed -= OnChanged;
            _service.Watch(_owner, []);
        }

        internal void Configure(
            SavedWidgetDefinition? definition,
            PanelWidgetHostContext? context = null,
            double contentScale = 1d)
        {
            _definition = definition?.Type == PanelWidgetTypes.XTimeline ? definition : null;
            var host = context ?? PanelWidgetHostRules.ForOverlay();
            _horizontal = host.Orientation == PanelWidgetOrientation.Horizontal;
            var normalizedScale = PanelContentSizeRules.NormalizeScale(contentScale);
            if (Math.Abs(_contentScale - normalizedScale) >= 0.001)
            {
                _contentScale = normalizedScale;
                _rows.Clear();
            }
            WidgetRoot.Margin = _horizontal
                ? new Thickness(0, 0, 120 * _contentScale, 0)
                : new Thickness(0, 0, 0, 0);
            FeedScroller.Visibility = _horizontal ? Visibility.Collapsed : Visibility.Visible;
            LatestText.FontSize = 14 * _contentScale;
            LatestButton.Padding = new Thickness(
                8 * _contentScale,
                2 * _contentScale,
                8 * _contentScale,
                2 * _contentScale);
            if (IsLoaded) Register();
            Refresh();
        }

        private void Register()
        {
            _service?.Watch(_owner, _definition?.XExternalContentConsent == true ? _definition.XHandles : []);
            Refresh();
        }

        internal void InvalidateContentMeasure() => WidgetRoot.InvalidateMeasure();

        private void OnChanged(object? sender, EventArgs e)
        {
            if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
            if (!DispatcherQueue.TryEnqueue(() => { Interlocked.Exchange(ref _refreshQueued, 0); if (!_disposed) Refresh(); }))
                Interlocked.Exchange(ref _refreshQueued, 0);
        }

        private void Refresh()
        {
            var definition = _definition;
            var enabled = definition?.XExternalContentConsent == true;
            var status = !enabled ? "X Tracker is paused. Enable loading in Widgets."
                : definition!.XHandles.Length == 0 ? "Add accounts in Widgets." : _service?.Status ?? "X service unavailable.";
            SetContentStatus(GetContentStatus(enabled, definition, status));
            var posts = enabled && _service != null ? _service.Snapshot()
                .Where(p => definition!.XHandles.Contains(p.Handle, StringComparer.OrdinalIgnoreCase))
                .Where(p => p.Kind switch { "reply" => definition!.XIncludeReplies, "repost" => definition!.XIncludeReposts,
                    "quote" => definition!.XIncludeQuotes, _ => definition!.XIncludePosts })
                .Where(p => string.IsNullOrWhiteSpace(definition!.XTextFilter) || p.Text.Contains(definition.XTextFilter, StringComparison.OrdinalIgnoreCase))
                .Take(_horizontal ? 1 : 100).ToArray() : [];
            // Keep unchanged card objects and selection stable when only health changes.
            if (!_rows.Select(static row => row.Post).SequenceEqual(posts))
            {
                _rows.Clear();
                foreach (var post in posts) _rows.Add(new XTimelineRow(post, _contentScale));
            }
            LatestButton.Visibility = _horizontal && posts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            LatestText.Text = posts.Length > 0 ? $"X · @{posts[0].Handle}: {posts[0].Text}" : string.Empty;
            LatestButton.Tag = posts.FirstOrDefault()?.Url;
        }

        private static WidgetContentStatus GetContentStatus(
            bool enabled,
            SavedWidgetDefinition? definition,
            string status)
        {
            if (!enabled || definition?.XHandles.Length == 0)
                return new WidgetContentStatus(WidgetContentState.Empty, "Not configured");
            if (status.StartsWith("Connected", StringComparison.OrdinalIgnoreCase))
                return new WidgetContentStatus(WidgetContentState.Live, "Live");
            if (status.StartsWith("Connecting", StringComparison.OrdinalIgnoreCase)
                || status.Contains("Retrying", StringComparison.OrdinalIgnoreCase))
                return new WidgetContentStatus(WidgetContentState.Loading, "Connecting");
            if (status.Contains("Configure", StringComparison.OrdinalIgnoreCase)
                || status.Contains("ready", StringComparison.OrdinalIgnoreCase))
                return new WidgetContentStatus(WidgetContentState.Empty, "Not configured");
            return new WidgetContentStatus(WidgetContentState.Unavailable, "Unavailable");
        }

        private void SetContentStatus(WidgetContentStatus status)
        {
            if (status == _contentStatus) return;
            _contentStatus = status;
            ContentStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private async void OnOpenPostClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: Uri uri } && uri.Scheme == "https" && uri.Host == "x.com")
                await Launcher.LaunchUriAsync(uri);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _expiry.Stop();
            if (_service != null) { _service.Changed -= OnChanged; _service.Watch(_owner, []); }
            _rows.Clear();
        }
    }

    internal sealed class XTimelineRow
    {
        internal XTimelineRow(SocialPost post, double scale)
        {
            Post = post;
            var normalized = PanelContentSizeRules.NormalizeScale(scale);
            Padding = new Thickness(12 * normalized, 10 * normalized, 12 * normalized, 10 * normalized);
            Spacing = 5 * normalized;
            HeadingFontSize = 14 * normalized;
            BodyFontSize = 14 * normalized;
            DetailFontSize = 10 * normalized;
        }

        internal SocialPost Post { get; }
        public string Heading => Post.Heading;
        public string Text => Post.Text;
        public string Detail => Post.Detail;
        public Uri Url => Post.Url;
        public Thickness Padding { get; }
        public double Spacing { get; }
        public double HeadingFontSize { get; }
        public double BodyFontSize { get; }
        public double DetailFontSize { get; }
    }
}
