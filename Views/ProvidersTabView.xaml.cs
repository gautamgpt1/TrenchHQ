using TrenchHQ.Helpers;
using TrenchHQ.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace TrenchHQ.Views
{
    internal sealed record ProviderBadge(
        string Text,
        SolidColorBrush Background,
        SolidColorBrush Foreground,
        SolidColorBrush BorderBrush);

    internal sealed class ProviderPresetEditorRow : INotifyPropertyChanged
    {
        private static readonly string[] CommitmentValues = ["Processed", "Confirmed", "Finalized"];
        private bool _isConfigured;
        private bool _isActive;
        private string _routeStatus = "Ready \u00b7 automatic";
        private bool _isExpanded;
        private string _streamEndpoint;
        private string _rpcEndpoint;
        private string _credential = string.Empty;
        private int _commitmentIndex;
        private string _statusText = string.Empty;
        private string? _reusableCredentialConfigurationId;
        private string _reuseCredentialText = string.Empty;
        private string _baselineStreamEndpoint = string.Empty;
        private string _baselineRpcEndpoint = string.Empty;
        private string _baselineCredential = string.Empty;
        private int _baselineCommitmentIndex;
        private bool _isDirty;

        internal ProviderPresetEditorRow(
            OnChainProviderPreset preset,
            OnChainProviderConfiguration? configuration,
            bool isActive,
            string routeStatus)
        {
            Preset = preset;
            _streamEndpoint = preset.DefaultStreamEndpoint;
            _rpcEndpoint = preset.DefaultRpcEndpoint;
            if (configuration != null)
            {
                ApplyConfiguration(configuration);
            }
            Badges = CreateBadges(preset.Badges ?? []);
            CaptureBaseline();
            SetRouteState(isActive, routeStatus);
        }

        internal OnChainProviderPreset Preset { get; }
        internal string? ConfigurationId { get; private set; }
        internal string? Region { get; private set; }
        internal OnChainProviderCapabilitySnapshot? CapabilitySnapshot { get; private set; }
        public string ProviderType => Preset.ProviderType;
        public string DisplayName => Preset.DisplayName;
        public IReadOnlyList<ProviderBadge> Badges { get; }
        public string ChainDisplayName => OnChainProviderCatalog.GetChainDisplayName(Preset);
        public string SupportedChainsText => OnChainProviderCatalog.GetSupportedChainsText(Preset);
        public string RpcEndpointHeader => Preset.ChainNamespace == ChainNamespaces.Eip155
            ? $"{ChainDisplayName} HTTP endpoint"
            : "Solana RPC endpoint";
        public string CredentialPlaceholder => Preset.RequiresCredential
            ? "Enter API key or access token"
            : "Optional for this provider";
        public string CredentialStorageText => OnChainProviderCatalog.SharesCredentialAcrossChains(Preset)
            ? "This encrypted provider credential is stored once and shared by its linked TrenchHQ chain configurations. Changing it here updates every linked configuration."
            : "The credential is encrypted for this Windows account. TrenchHQ never stores it in the endpoint fields.";
        public Visibility CommitmentVisibility => Preset.ChainNamespace == ChainNamespaces.Solana
            ? Visibility.Visible
            : Visibility.Collapsed;
        public string SetupInstructions => Preset.SetupInstructions;
        public string CredentialLabel => Preset.CredentialLabel;
        public string SetupLinkLabel => Preset.SetupLinkLabel;
        public Uri? SetupUri => string.IsNullOrWhiteSpace(Preset.SetupUrl) ? null : new Uri(Preset.SetupUrl);
        public Visibility SetupLinkVisibility => SetupUri == null ? Visibility.Collapsed : Visibility.Visible;
        public string ReuseCredentialText => _reuseCredentialText;
        public Visibility ReuseCredentialVisibility => _reusableCredentialConfigurationId == null
            ? Visibility.Collapsed
            : Visibility.Visible;
        public IReadOnlyList<string> CommitmentOptions => CommitmentValues;
        public Visibility ConfiguredVisibility => IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NotConfiguredVisibility => IsConfigured ? Visibility.Collapsed : Visibility.Visible;
        public string ConnectionRoleText => _routeStatus;
        public Visibility RemoveVisibility => IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EditorVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        public string ChevronGlyph => IsExpanded ? "\uE70E" : "\uE70D";
        public bool IsDirty
        {
            get => _isDirty;
            private set => SetField(ref _isDirty, value);
        }

        public bool IsConfigured
        {
            get => _isConfigured;
            private set
            {
                if (_isConfigured == value)
                {
                    return;
                }
                _isConfigured = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ConfiguredVisibility));
                OnPropertyChanged(nameof(NotConfiguredVisibility));
                OnPropertyChanged(nameof(RemoveVisibility));
                OnPropertyChanged(nameof(ConnectionRoleText));
            }
        }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value)
                {
                    return;
                }
                _isActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ConfiguredVisibility));
                OnPropertyChanged(nameof(ConnectionRoleText));
            }
        }

        internal void SetRouteState(bool isActive, string routeStatus)
        {
            _isActive = isActive;
            _routeStatus = routeStatus;
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ConnectionRoleText));
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                {
                    return;
                }
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EditorVisibility));
                OnPropertyChanged(nameof(ChevronGlyph));
            }
        }

        public string StreamEndpoint
        {
            get => _streamEndpoint;
            set
            {
                SetField(ref _streamEndpoint, value ?? string.Empty);
                UpdateDirty();
            }
        }

        public string RpcEndpoint
        {
            get => _rpcEndpoint;
            set
            {
                SetField(ref _rpcEndpoint, value ?? string.Empty);
                UpdateDirty();
            }
        }

        public string Credential
        {
            get => _credential;
            set
            {
                SetField(ref _credential, value ?? string.Empty);
                UpdateDirty();
            }
        }

        public int CommitmentIndex
        {
            get => _commitmentIndex;
            set
            {
                SetField(ref _commitmentIndex, Math.Clamp(value, 0, CommitmentValues.Length - 1));
                UpdateDirty();
            }
        }

        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value ?? string.Empty);
        }

        internal OnChainProviderConfiguration CreateConfiguration()
        {
            return new OnChainProviderConfiguration
            {
                Id = ConfigurationId ?? Guid.NewGuid().ToString("N"),
                ChainNamespace = Preset.ChainNamespace,
                ChainId = Preset.ChainId,
                ProviderType = Preset.ProviderType,
                FriendlyName = DisplayName,
                StreamEndpoint = StreamEndpoint.Trim(),
                RpcEndpoint = RpcEndpoint.Trim(),
                Region = Region,
                Commitment = (OnChainCommitment)CommitmentIndex,
                ReplayEnabled = Preset.ReplayEnabled,
                CapabilitySnapshot = CapabilitySnapshot
            };
        }

        internal void ApplyConfiguration(OnChainProviderConfiguration configuration)
        {
            ConfigurationId = configuration.Id;
            Region = configuration.Region;
            StreamEndpoint = configuration.StreamEndpoint;
            RpcEndpoint = configuration.RpcEndpoint;
            CommitmentIndex = (int)configuration.Commitment;
            CapabilitySnapshot = configuration.CapabilitySnapshot;
            IsConfigured = true;
            CaptureBaseline();
        }

        internal void SetLoadedCredential(string credential)
        {
            SetField(ref _credential, credential ?? string.Empty, nameof(Credential));
            _baselineCredential = _credential;
            UpdateDirty();
        }

        internal void ClearCredentialEditor()
        {
            SetField(ref _credential, string.Empty, nameof(Credential));
            _baselineCredential = string.Empty;
            UpdateDirty();
        }

        internal void SetReusableCredential(string? configurationId, string? displayName)
        {
            _reusableCredentialConfigurationId = configurationId;
            _reuseCredentialText = configurationId == null
                ? string.Empty
                : $"Reuse saved {displayName} key";
            OnPropertyChanged(nameof(ReuseCredentialText));
            OnPropertyChanged(nameof(ReuseCredentialVisibility));
        }

        internal string? GetReusableCredentialConfigurationId()
        {
            return _reusableCredentialConfigurationId;
        }

        internal void CancelEdits()
        {
            SetField(ref _streamEndpoint, _baselineStreamEndpoint, nameof(StreamEndpoint));
            SetField(ref _rpcEndpoint, _baselineRpcEndpoint, nameof(RpcEndpoint));
            SetField(ref _credential, _baselineCredential, nameof(Credential));
            SetField(ref _commitmentIndex, _baselineCommitmentIndex, nameof(CommitmentIndex));
            StatusText = string.Empty;
            UpdateDirty();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void CaptureBaseline()
        {
            _baselineStreamEndpoint = _streamEndpoint;
            _baselineRpcEndpoint = _rpcEndpoint;
            _baselineCredential = _credential;
            _baselineCommitmentIndex = _commitmentIndex;
            UpdateDirty();
        }

        private void UpdateDirty()
        {
            IsDirty = !string.Equals(_streamEndpoint, _baselineStreamEndpoint, StringComparison.Ordinal)
                || !string.Equals(_rpcEndpoint, _baselineRpcEndpoint, StringComparison.Ordinal)
                || !string.Equals(_credential, _baselineCredential, StringComparison.Ordinal)
                || _commitmentIndex != _baselineCommitmentIndex;
        }

        private static IReadOnlyList<ProviderBadge> CreateBadges(IReadOnlyList<string> badges)
        {
            return badges.Select(CreateBadge).ToArray();
        }

        private static ProviderBadge CreateBadge(string text)
        {
            if (text.Contains("Free", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Recommended", StringComparison.OrdinalIgnoreCase))
            {
                return Badge(text, 0x20, 0x4C, 0xE5, 0xC1);
            }
            if (text.Contains("trial", StringComparison.OrdinalIgnoreCase)
                || text.Contains("paid", StringComparison.OrdinalIgnoreCase)
                || text.Contains("PAYG", StringComparison.OrdinalIgnoreCase)
                || text.Contains('$', StringComparison.Ordinal))
            {
                return Badge(text, 0x20, 0xF0, 0xA5, 0x55);
            }
            if (text.Contains("Replay", StringComparison.OrdinalIgnoreCase))
            {
                return Badge(text, 0x20, 0xB6, 0x8C, 0xFF);
            }
            return Badge(text, 0x20, 0x69, 0xB7, 0xFF);
        }

        private static ProviderBadge Badge(string text, byte alpha, byte red, byte green, byte blue)
        {
            return new ProviderBadge(
                text,
                new SolidColorBrush(Color.FromArgb(alpha, red, green, blue)),
                new SolidColorBrush(Color.FromArgb(0xFF, red, green, blue)),
                new SolidColorBrush(Color.FromArgb(0x58, red, green, blue)));
        }

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }
            field = value;
            OnPropertyChanged(propertyName);
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed partial class ProvidersTabView : UserControl
    {
        private async void OnUsageBudgetsClick(object sender, RoutedEventArgs e)
        {
            var providers = ((App)Application.Current).OnChainProviders;
            var usage = providers.Usage;
            var keys = providers.GetConfigurations()
                .Where(profile => !OnChainProviderCatalog.IsPublicEvaluationProvider(profile.ProviderType))
                .SelectMany(usage.Register).Distinct().ToArray();
            var selector = new ComboBox { Header = "Provider allowance", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var key in keys) selector.Items.Add(usage.Snapshot(key).Name);
            var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var enabled = new CheckBox { Content = "Apply a local usage limit (advanced override)" };
            var limit = new NumberBox { Header = "Allowance assigned to TrenchHQ", Minimum = 0, SmallChange = 1000 };
            var used = new NumberBox { Header = "Usage already counted in this cycle", Minimum = 0, SmallChange = 1000 };
            var cycle = new ComboBox { Header = "Reset cycle", ItemsSource = new[] { "Monthly", "Daily" }, SelectedIndex = 0 };
            var start = new TextBox { Header = "Cycle start (UTC)", PlaceholderText = "2026-09-01T00:00:00Z" };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 12, MinWidth = 400, MaxWidth = 520 };
            panel.Children.Add(new TextBlock
            {
                Text = "Selection and fallback are automatic. Known free plans use a daily TrenchHQ guard: 90% of the monthly allowance divided by 31, or 90% of a daily allowance. These local estimates cannot see other apps or the actual account balance. Unknown plans rely on failure/quota responses. Optional overrides below are shared across this provider's chains; polling/event modes stay as selected.",
                TextWrapping = TextWrapping.Wrap
            });
            foreach (var control in new UIElement[] { selector, summary, enabled, limit, used, cycle, start, error }) panel.Children.Add(control);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Usage", CloseButtonText = "Close",
                PrimaryButtonText = "Save override", IsPrimaryButtonEnabled = keys.Length > 0,
                Content = new ScrollViewer { Content = panel, MaxHeight = 580 }
            };
            decimal loadedUsed = 0;
            void Refresh()
            {
                if (selector.SelectedIndex < 0) { summary.Text = "Add a provider connection to track its usage."; return; }
                var key = keys[selector.SelectedIndex];
                var snapshot = usage.Snapshot(key);
                var rate = usage.UnitsPerHour(key);
                var status = snapshot.QuotaPauseUntilUtc.HasValue ? $"Provider quota pause until {snapshot.QuotaPauseUntilUtc:u}."
                    : snapshot.Enabled ? snapshot.Blocked ? "Local allowance reached; switched or paused."
                    : snapshot.Automatic == true ? "Automatic daily guard is on." : "Custom allowance is on."
                    : "Account allowance unknown; automatic health/quota fallback remains on.";
                summary.Text = $"{snapshot.Used:N2} {snapshot.Unit} this cycle · {snapshot.RpcRequests:N0} RPC requests\n"
                    + $"{snapshot.StreamMessages:N0} stream messages · {snapshot.StreamBytes / 1048576d:N2} MiB received\n"
                    + (rate.HasValue ? $"Recent session rate: {rate:N2} {snapshot.Unit}/hour\n" : "Session rate needs 30 seconds of traffic.\n")
                    + (snapshot.Enabled && rate > 0 ? $"At this rate: {Math.Max(0, snapshot.Limit - snapshot.Used) / rate.Value:N1} hours until the local guard\n" : string.Empty)
                    + status + (snapshot.UnknownCost ? " Some method costs are unknown; budgets pause this provider." : string.Empty)
                    + (usage.PersistenceError == null ? string.Empty : "\n" + usage.PersistenceError);
            }
            selector.SelectionChanged += (_, _) =>
            {
                if (selector.SelectedIndex < 0) return;
                var snapshot = usage.Snapshot(keys[selector.SelectedIndex]);
                enabled.IsChecked = snapshot.Enabled;
                limit.Value = (double)snapshot.Limit;
                limit.Header = $"Allowance assigned to TrenchHQ ({snapshot.Unit})";
                used.Value = (double)(loadedUsed = snapshot.Used);
                cycle.SelectedIndex = snapshot.Daily ? 1 : 0;
                start.Text = snapshot.AnchorUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
                error.Text = string.Empty;
                Refresh();
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    if (!double.IsFinite(limit.Value) || !double.IsFinite(used.Value)
                        || !DateTimeOffset.TryParse(start.Text, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal, out var anchor))
                        throw new ArgumentException("Enter an allowance, usage count and valid UTC cycle start.");
                    var key = keys[selector.SelectedIndex];
                    // Preserve traffic recorded while the dialog was open.
                    usage.Configure(key, enabled.IsChecked == true, (decimal)limit.Value, cycle.SelectedIndex == 1,
                        anchor, (decimal)used.Value, loadedUsed);
                }
                catch (Exception exception) when (exception is ArgumentException or OverflowException or System.IO.IOException or UnauthorizedAccessException)
                {
                    args.Cancel = true;
                    error.Text = "Budget could not be saved. Check the values and local storage access.";
                }
            };
            selector.SelectedIndex = keys.Length > 0 ? 0 : -1;
            Refresh();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => Refresh();
            timer.Start();
            try { await dialog.ShowAsync(); }
            finally { timer.Stop(); }
        }

        private readonly ObservableCollection<ProviderPresetEditorRow> _solanaFreeRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _solanaPaidRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _solanaCustomRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _ethereumFreeRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _ethereumPaidRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _ethereumCustomRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _baseFreeRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _baseCustomRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _bnbFreeRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _bnbCustomRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _robinhoodFreeRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _robinhoodPaidRows = [];
        private readonly ObservableCollection<ProviderPresetEditorRow> _robinhoodCustomRows = [];
        private bool _providerEventsSubscribed;

        public ProvidersTabView()
        {
            InitializeComponent();
            SolanaFreeProviderRows.ItemsSource = _solanaFreeRows;
            SolanaPaidProviderRows.ItemsSource = _solanaPaidRows;
            SolanaCustomProviderRows.ItemsSource = _solanaCustomRows;
            EthereumFreeProviderRows.ItemsSource = _ethereumFreeRows;
            EthereumPaidProviderRows.ItemsSource = _ethereumPaidRows;
            EthereumCustomProviderRows.ItemsSource = _ethereumCustomRows;
            BaseFreeProviderRows.ItemsSource = _baseFreeRows;
            BaseCustomProviderRows.ItemsSource = _baseCustomRows;
            BnbFreeProviderRows.ItemsSource = _bnbFreeRows;
            BnbCustomProviderRows.ItemsSource = _bnbCustomRows;
            RobinhoodFreeProviderRows.ItemsSource = _robinhoodFreeRows;
            RobinhoodPaidProviderRows.ItemsSource = _robinhoodPaidRows;
            RobinhoodCustomProviderRows.ItemsSource = _robinhoodCustomRows;
            ApiSectionList.SelectedIndex = 0;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_providerEventsSubscribed || Application.Current is not App app)
            {
                return;
            }
            app.OnChainProviders.Changed += OnProviderConfigurationsChanged;
            _providerEventsSubscribed = true;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_providerEventsSubscribed || Application.Current is not App app)
            {
                return;
            }
            app.OnChainProviders.Changed -= OnProviderConfigurationsChanged;
            _providerEventsSubscribed = false;
        }

        private void OnProviderConfigurationsChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Application.Current is App app)
                {
                    RefreshRouteIndicators(app);
                }
            });
        }

        private void OnApiSectionSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ApiSectionList.SelectedItem is not ListViewItem { Tag: string selected }) return;
            // Keep editors alive so switching sections does not discard drafts or change activation.
            foreach (var section in new[] { SolanaApiSection, EthereumApiSection, BaseApiSection,
                         BnbApiSection, RobinhoodApiSection, XApiSection })
            {
                section.Visibility = section.Name == selected + "ApiSection"
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            ApiDetailsScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        }

        internal async Task ActivateAsync()
        {
            if (Application.Current is not App app)
            {
                return;
            }

            await app.EnsureInitializedAsync();
            var expandedType = AllRows().FirstOrDefault(static row => row.IsExpanded)?.ProviderType;
            PopulateRows(
                app.OnChainProviders.GetConfigurations(),
                GetActiveConfigurationIds(app),
                expandedType);
        }

        private async void OnProviderRowClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: ProviderPresetEditorRow row })
            {
                return;
            }

            if (row.IsExpanded)
            {
                row.IsExpanded = false;
                row.ClearCredentialEditor();
                row.StatusText = string.Empty;
                return;
            }

            foreach (var other in AllRows())
            {
                other.IsExpanded = ReferenceEquals(other, row);
                if (!ReferenceEquals(other, row))
                {
                    other.ClearCredentialEditor();
                    other.StatusText = string.Empty;
                }
            }
            row.IsExpanded = true;
            row.StatusText = string.Empty;

            if (!row.IsConfigured || Application.Current is not App app)
            {
                return;
            }

            var configuration = app.OnChainProviders.GetConfigurations().FirstOrDefault(configuration =>
                string.Equals(configuration.Id, row.ConfigurationId, StringComparison.OrdinalIgnoreCase));
            if (configuration == null)
            {
                row.StatusText = "The saved configuration is unavailable.";
                return;
            }
            try
            {
                row.SetLoadedCredential(await app.OnChainProviders.GetCredentialAsync(configuration));
            }
            catch (Exception exception)
            {
                row.StatusText = exception.Message;
            }
        }

        private async void OnSaveProviderClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ProviderPresetEditorRow row } button
                || Application.Current is not App app)
            {
                return;
            }

            button.IsEnabled = false;
            row.StatusText = "Saving provider...";
            try
            {
                var configuration = row.CreateConfiguration();
                var credentialDraft = row.Credential;
                var credential = credentialDraft;
                string? reuseCredentialReference = null;
                if (string.IsNullOrWhiteSpace(credential)
                    && row.GetReusableCredentialConfigurationId() is string sourceId)
                {
                    var source = app.OnChainProviders.GetConfigurations().FirstOrDefault(item =>
                        string.Equals(item.Id, sourceId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("The shared provider credential is unavailable.");
                    credential = await app.OnChainProviders.GetCredentialAsync(source);
                    reuseCredentialReference = source.CredentialReference;
                }
                row.StatusText = $"Testing {row.ChainDisplayName} API endpoints...";
                await ProbeConfigurationAsync(configuration, credential);
                await app.OnChainProviders.UpsertAsync(
                    configuration,
                    string.IsNullOrWhiteSpace(credentialDraft) ? null : credentialDraft,
                    reuseCredentialReference);
                PopulateRows(
                    app.OnChainProviders.GetConfigurations(),
                    GetActiveConfigurationIds(app),
                    row.ProviderType);
                var savedRow = AllRows().First(item => string.Equals(
                    item.ProviderType,
                    row.ProviderType,
                    StringComparison.Ordinal));
                savedRow.StatusText = OnChainProviderCatalog.SharesCredentialAcrossChains(row.Preset)
                    ? "Saved. Linked chains now use this key automatically when suitable."
                    : "Saved securely. TrenchHQ now selects this provider automatically when suitable.";
            }
            catch (Exception exception)
            {
                row.StatusText = exception.Message;
            }
            finally
            {
                button.IsEnabled = row.IsDirty;
            }
        }

        private void OnCancelProviderClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ProviderPresetEditorRow row })
            {
                row.CancelEdits();
            }
        }

        private async void OnReuseCredentialClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ProviderPresetEditorRow row } button
                || Application.Current is not App app
                || row.GetReusableCredentialConfigurationId() is not string sourceId)
            {
                return;
            }

            button.IsEnabled = false;
            row.StatusText = "Testing the target endpoint with the saved same-family key...";
            try
            {
                var source = app.OnChainProviders.GetConfigurations().FirstOrDefault(configuration =>
                    string.Equals(configuration.Id, sourceId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("The source credential configuration is unavailable.");
                var credential = await app.OnChainProviders.GetCredentialAsync(source);
                var configuration = row.CreateConfiguration();
                await ProbeConfigurationAsync(configuration, credential);
                await app.OnChainProviders.UpsertAsync(
                    configuration,
                    null,
                    source.CredentialReference);
                PopulateRows(
                    app.OnChainProviders.GetConfigurations(),
                    GetActiveConfigurationIds(app),
                    row.ProviderType);
                var savedRow = AllRows().First(item => string.Equals(
                    item.ProviderType,
                    row.ProviderType,
                    StringComparison.Ordinal));
                savedRow.StatusText = "Configuration saved with the existing shared provider credential.";
            }
            catch (Exception exception)
            {
                row.StatusText = exception.Message;
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private async void OnRemoveProviderClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: ProviderPresetEditorRow row }
                || Application.Current is not App app
                || string.IsNullOrWhiteSpace(row.ConfigurationId))
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Remove {row.DisplayName}?",
                Content = "This chain connection will be removed. A shared encrypted provider key is deleted only when no other connection uses it. TrenchHQ automatically selects another eligible provider, or pauses the chain if none is available.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await app.OnChainProviders.DeleteAsync(row.ConfigurationId);
            PopulateRows(
                app.OnChainProviders.GetConfigurations(),
                GetActiveConfigurationIds(app),
                row.ProviderType);
            var removedRow = AllRows().FirstOrDefault(item => string.Equals(
                item.ProviderType,
                row.ProviderType,
                StringComparison.Ordinal));
            if (removedRow != null)
            {
                removedRow.StatusText = "Saved configuration removed.";
            }
        }

        private static async Task ProbeConfigurationAsync(
            OnChainProviderConfiguration configuration,
            string credential)
        {
            configuration.Usage = ((App)Application.Current).OnChainProviders.Usage.CreateScope(configuration, validation: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var httpClient = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(25)
            };
            if (configuration.ChainNamespace == ChainNamespaces.Eip155)
            {
                var chainId = await new EvmJsonRpcClient(httpClient, configuration, credential).GetChainIdAsync(timeout.Token);
                if (chainId.ToString(System.Globalization.CultureInfo.InvariantCulture) != configuration.ChainId)
                    throw new InvalidOperationException("The HTTP endpoint is for a different chain.");
                configuration.CapabilitySnapshot = new OnChainProviderCapabilitySnapshot
                {
                    ObservedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ChainId = OnChainProviderCapabilityState.Supported
                };
            }
            else
            {
                await new SolanaRpcClient(httpClient, configuration, credential).GetSlotAsync(configuration.Commitment, timeout.Token);
                configuration.CapabilitySnapshot = null;
            }
            ((App)Application.Current).OnChainProviders.Usage.CredentialValidated(configuration);
        }

        private void PopulateRows(
            IReadOnlyCollection<OnChainProviderConfiguration> configurations,
            IReadOnlySet<string> activeConfigurationIds,
            string? expandedType)
        {
            _solanaFreeRows.Clear();
            _solanaPaidRows.Clear();
            _solanaCustomRows.Clear();
            _ethereumFreeRows.Clear();
            _ethereumPaidRows.Clear();
            _ethereumCustomRows.Clear();
            _baseFreeRows.Clear();
            _baseCustomRows.Clear();
            _bnbFreeRows.Clear();
            _bnbCustomRows.Clear();
            _robinhoodFreeRows.Clear();
            _robinhoodPaidRows.Clear();
            _robinhoodCustomRows.Clear();
            var app = Application.Current as App;
            foreach (var preset in OnChainProviderCatalog.AllPresets)
            {
                if (OnChainProviderCatalog.IsPublicEvaluationProvider(preset.ProviderType))
                {
                    continue;
                }
                var configuration = configurations.FirstOrDefault(item => string.Equals(
                    item.ProviderType,
                    preset.ProviderType,
                    StringComparison.Ordinal));
                var row = new ProviderPresetEditorRow(
                    preset,
                    configuration,
                    configuration != null && activeConfigurationIds.Contains(configuration.Id),
                    configuration == null ? "Enter key to enable automatic selection"
                        : app?.OnChainProviders.GetRouteStatus(configuration) ?? "Ready \u00b7 automatic")
                {
                    IsExpanded = string.Equals(preset.ProviderType, expandedType, StringComparison.Ordinal)
                };
                if (preset.RequiresCredential
                    && OnChainProviderCatalog.SharesCredentialAcrossChains(preset))
                {
                    var source = configurations.FirstOrDefault(item =>
                        !string.Equals(item.ProviderType, preset.ProviderType, StringComparison.Ordinal)
                        && OnChainProviderConfigurationStore.CanReuseCredential(item, preset));
                    row.SetReusableCredential(source?.Id, source?.FriendlyName ?? source?.ProviderType);
                }
                AddRowByAccessCategory(preset, row);
            }
        }

        private void AddRowByAccessCategory(
            OnChainProviderPreset preset,
            ProviderPresetEditorRow row)
        {
            var target = (preset.ChainNamespace, preset.ChainId, preset.AccessCategory) switch
            {
                (ChainNamespaces.Solana, _, OnChainProviderAccessCategory.FreeTierAvailable) => _solanaFreeRows,
                (ChainNamespaces.Solana, _, OnChainProviderAccessCategory.TrialOrPaid) => _solanaPaidRows,
                (ChainNamespaces.Solana, _, OnChainProviderAccessCategory.CustomEndpoint) => _solanaCustomRows,
                (ChainNamespaces.Eip155, "1", OnChainProviderAccessCategory.FreeTierAvailable) => _ethereumFreeRows,
                (ChainNamespaces.Eip155, "1", OnChainProviderAccessCategory.TrialOrPaid) => _ethereumPaidRows,
                (ChainNamespaces.Eip155, "1", OnChainProviderAccessCategory.CustomEndpoint) => _ethereumCustomRows,
                (ChainNamespaces.Eip155, EvmChainDefinitions.BaseMainnetChainId, OnChainProviderAccessCategory.FreeTierAvailable) => _baseFreeRows,
                (ChainNamespaces.Eip155, EvmChainDefinitions.BaseMainnetChainId, OnChainProviderAccessCategory.CustomEndpoint) => _baseCustomRows,
                (ChainNamespaces.Eip155, EvmChainDefinitions.BnbMainnetChainId, OnChainProviderAccessCategory.FreeTierAvailable) => _bnbFreeRows,
                (ChainNamespaces.Eip155, EvmChainDefinitions.BnbMainnetChainId, OnChainProviderAccessCategory.CustomEndpoint) => _bnbCustomRows,
                (ChainNamespaces.Eip155, EvmChainDefinitions.RobinhoodMainnetChainId, OnChainProviderAccessCategory.FreeTierAvailable) => _robinhoodFreeRows,
                (ChainNamespaces.Eip155, EvmChainDefinitions.RobinhoodMainnetChainId, OnChainProviderAccessCategory.TrialOrPaid) => _robinhoodPaidRows,
                (ChainNamespaces.Eip155, EvmChainDefinitions.RobinhoodMainnetChainId, OnChainProviderAccessCategory.CustomEndpoint) => _robinhoodCustomRows,
                _ => throw new InvalidOperationException("The provider access category has no API section.")
            };
            target.Add(row);
        }

        private void RefreshRouteIndicators(App app)
        {
            var configurations = app.OnChainProviders.GetConfigurations();
            var activeIds = GetActiveConfigurationIds(app);
            foreach (var row in AllRows())
            {
                var configuration = configurations.FirstOrDefault(item => item.Id == row.ConfigurationId);
                row.SetRouteState(configuration != null && activeIds.Contains(configuration.Id),
                    configuration == null ? "Enter key to enable automatic selection" : app.OnChainProviders.GetRouteStatus(configuration));
            }
        }

        private IEnumerable<ProviderPresetEditorRow> AllRows()
        {
            return _solanaFreeRows
                .Concat(_solanaPaidRows)
                .Concat(_solanaCustomRows)
                .Concat(_ethereumFreeRows)
                .Concat(_ethereumPaidRows)
                .Concat(_ethereumCustomRows)
                .Concat(_baseFreeRows)
                .Concat(_baseCustomRows)
                .Concat(_bnbFreeRows)
                .Concat(_bnbCustomRows)
                .Concat(_robinhoodFreeRows)
                .Concat(_robinhoodPaidRows)
                .Concat(_robinhoodCustomRows);
        }

        private static HashSet<string> GetActiveConfigurationIds(App app)
        {
            return new[]
                {
                    app.OnChainProviders.GetSelectedConfiguration(ChainNamespaces.Solana, "mainnet-beta"),
                    app.OnChainProviders.GetSelectedConfiguration(
                        ChainNamespaces.Eip155,
                        EvmChainDefinitions.EthereumMainnet.ChainId),
                    app.OnChainProviders.GetSelectedConfiguration(
                        ChainNamespaces.Eip155,
                        EvmChainDefinitions.BaseMainnet.ChainId),
                    app.OnChainProviders.GetSelectedConfiguration(
                        ChainNamespaces.Eip155,
                        EvmChainDefinitions.BnbMainnet.ChainId),
                    app.OnChainProviders.GetSelectedConfiguration(
                        ChainNamespaces.Eip155,
                        EvmChainDefinitions.RobinhoodMainnet.ChainId)
                }
                .OfType<OnChainProviderConfiguration>()
                .Select(static configuration => configuration.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

    }
}
