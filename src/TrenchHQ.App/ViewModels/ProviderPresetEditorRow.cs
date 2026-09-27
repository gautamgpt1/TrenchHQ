using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.Providers;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
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

namespace TrenchHQ.ViewModels
{
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

}
