using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace TrenchHQ.Helpers
{
    internal sealed class SavedWidgetCatalogService
    {
        private SavedWidgetCatalog _catalog = new();
        private bool _initialized;

        internal event EventHandler? Changed;

        internal async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            var folderPath = ApplicationData.Current.LocalFolder.Path;
            var loaded = await SavedWidgetCatalogStore.LoadAsync(folderPath);
            _catalog = loaded.Catalog ?? new SavedWidgetCatalog();
            SavedWidgetCatalogRules.Normalize(_catalog);
            if (loaded.RequiresSave || loaded.WasRejected)
            {
                await SavedWidgetCatalogStore.SaveAsync(folderPath, _catalog);
            }

            _initialized = true;
        }

        internal SavedWidgetDefinition[] GetWidgets()
        {
            return _catalog.Widgets.Select(Clone).ToArray();
        }

        internal SavedWidgetDefinition? Find(string id)
        {
            var widget = _catalog.Widgets.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            return widget == null ? null : Clone(widget);
        }

        internal SavedWidgetDefinition? FindFirst(IEnumerable<string>? widgetIds)
        {
            foreach (var id in widgetIds ?? [])
            {
                var widget = _catalog.Widgets.FirstOrDefault(item =>
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (widget != null)
                {
                    return Clone(widget);
                }
            }

            return null;
        }

        internal async Task ImportAsync(IEnumerable<SavedWidgetDefinition> definitions)
        {
            var changed = false;
            foreach (var definition in definitions)
            {
                if (_catalog.Widgets.Any(item =>
                        string.Equals(item.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _catalog.Widgets = [.. _catalog.Widgets, Clone(definition)];
                changed = true;
            }

            if (changed)
            {
                await NormalizeAndSaveAsync();
            }
        }

        internal async Task<SavedWidgetDefinition> EnsurePriceTickerAsync(SavedTickerInstrument[]? instruments)
        {
            var normalizedInstruments = SavedWidgetCatalogRules.Normalize(new SavedWidgetCatalog
            {
                Widgets = [new SavedWidgetDefinition { Instruments = instruments ?? [] }]
            }).Catalog.Widgets[0].Instruments;
            var existing = _catalog.Widgets.FirstOrDefault(widget =>
                string.Equals(widget.Type, PanelWidgetTypes.PriceTicker, StringComparison.OrdinalIgnoreCase)
                && widget.Instruments.Select(SavedWidgetCatalogRules.GetInstrumentKey)
                    .SequenceEqual(
                        normalizedInstruments.Select(SavedWidgetCatalogRules.GetInstrumentKey),
                        StringComparer.OrdinalIgnoreCase));
            if (existing != null)
            {
                return Clone(existing);
            }

            var widget = new SavedWidgetDefinition
            {
                Type = PanelWidgetTypes.PriceTicker,
                Instruments = normalizedInstruments
            };
            _catalog.Widgets = [.. _catalog.Widgets, widget];
            await NormalizeAndSaveAsync();
            return Clone(_catalog.Widgets[^1]);
        }

        internal async Task<SavedWidgetDefinition> AddPriceTickerAsync()
        {
            var widget = new SavedWidgetDefinition
            {
                Type = PanelWidgetTypes.PriceTicker,
                Instruments = []
            };
            _catalog.Widgets = [.. _catalog.Widgets, widget];
            await NormalizeAndSaveAsync();
            return Clone(_catalog.Widgets[^1]);
        }

        internal async Task<SavedWidgetDefinition> AddWalletActivityAsync()
        {
            var widget = new SavedWidgetDefinition
            {
                Type = PanelWidgetTypes.WalletActivity,
                Instruments = [],
                Wallets = []
            };
            _catalog.Widgets = [.. _catalog.Widgets, widget];
            await NormalizeAndSaveAsync();
            return Clone(_catalog.Widgets[^1]);
        }

        internal async Task<SavedWidgetDefinition> AddXTimelineAsync()
        {
            var widget = new SavedWidgetDefinition
            {
                Type = PanelWidgetTypes.XTimeline,
                Instruments = [],
                Wallets = []
            };
            _catalog.Widgets = [.. _catalog.Widgets, widget];
            await NormalizeAndSaveAsync();
            return Clone(_catalog.Widgets[^1]);
        }

        internal async Task<SavedWidgetDefinition> AddWebsiteAsync()
        {
            var widget = new SavedWidgetDefinition { Type = PanelWidgetTypes.Website };
            _catalog.Widgets = [.. _catalog.Widgets, widget];
            await NormalizeAndSaveAsync();
            return Clone(_catalog.Widgets[^1]);
        }

        internal async Task<bool> SaveAsync(SavedWidgetDefinition definition)
        {
            var index = Array.FindIndex(
                _catalog.Widgets,
                item => string.Equals(item.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            _catalog.Widgets[index] = Clone(definition);
            await NormalizeAndSaveAsync();
            return true;
        }

        internal async Task<bool> DeleteAsync(string id)
        {
            var remaining = _catalog.Widgets
                .Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (remaining.Length == _catalog.Widgets.Length)
            {
                return false;
            }

            _catalog.Widgets = remaining;
            await NormalizeAndSaveAsync();
            return true;
        }

        private async Task NormalizeAndSaveAsync()
        {
            SavedWidgetCatalogRules.Normalize(_catalog);
            await SavedWidgetCatalogStore.SaveAsync(ApplicationData.Current.LocalFolder.Path, _catalog);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static SavedWidgetDefinition Clone(SavedWidgetDefinition source)
        {
            return new SavedWidgetDefinition
            {
                Id = source.Id,
                Name = source.Name,
                Type = source.Type,
                OnChainPriceMode = source.OnChainPriceMode,
                Instruments = (source.Instruments ?? []).Select(Clone).ToArray(),
                Wallets = (source.Wallets ?? []).Select(Clone).ToArray(),
                WebsiteUrl = source.WebsiteUrl,
                WebsiteEnabled = source.WebsiteEnabled,
                WebsiteZoom = source.WebsiteZoom,
                XTimelineUrl = source.XTimelineUrl,
                XExternalContentConsent = source.XExternalContentConsent,
                XHandles = [.. source.XHandles],
                XTextFilter = source.XTextFilter,
                XIncludePosts = source.XIncludePosts,
                XIncludeReplies = source.XIncludeReplies,
                XIncludeReposts = source.XIncludeReposts,
                XIncludeQuotes = source.XIncludeQuotes
            };
        }

        private static SavedTrackedWallet Clone(SavedTrackedWallet source)
        {
            return new SavedTrackedWallet
            {
                ChainNamespace = source.ChainNamespace,
                ChainId = source.ChainId,
                Address = source.Address,
                Label = source.Label
            };
        }

        private static SavedTickerInstrument Clone(SavedTickerInstrument source)
        {
            return new SavedTickerInstrument
            {
                Kind = source.Kind,
                DisplayLabel = source.DisplayLabel,
                VenueId = source.VenueId,
                Symbol = source.Symbol,
                SelectedMint = source.SelectedMint,
                AssetName = source.AssetName,
                AssetSymbol = source.AssetSymbol,
                QuoteSymbol = source.QuoteSymbol,
                IconUri = source.IconUri,
                Pool = source.Pool == null ? null : Clone(source.Pool)
            };
        }

        private static OnChainPoolDescriptor Clone(OnChainPoolDescriptor source)
        {
            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = new OnChainDeploymentKey
                    {
                        ChainNamespace = source.PoolKey.DeploymentKey.ChainNamespace,
                        ChainId = source.PoolKey.DeploymentKey.ChainId,
                        ProtocolId = source.PoolKey.DeploymentKey.ProtocolId,
                        ContractAddress = source.PoolKey.DeploymentKey.ContractAddress
                    },
                    PoolId = source.PoolKey.PoolId
                },
                PoolType = source.PoolType,
                ProgramId = source.ProgramId,
                BaseMint = source.BaseMint,
                QuoteMint = source.QuoteMint,
                BaseDecimals = source.BaseDecimals,
                QuoteDecimals = source.QuoteDecimals,
                BaseVault = source.BaseVault,
                QuoteVault = source.QuoteVault,
                ProtocolAccounts = source.ProtocolAccounts
                    .Select(static account => new OnChainProtocolAccount
                    {
                        Role = account.Role,
                        Address = account.Address
                    })
                    .ToArray(),
                PairOrientation = source.PairOrientation,
                DiscoveredAtSlot = source.DiscoveredAtSlot,
                DiscoverySource = source.DiscoverySource,
                SupportStatus = source.SupportStatus,
                SupportReason = source.SupportReason,
                Asset0 = Clone(source.Asset0),
                Asset1 = Clone(source.Asset1),
                ValidationBlock = source.ValidationBlock,
                ValidationBlockHash = source.ValidationBlockHash,
                FeeTier = source.FeeTier,
                TickSpacing = source.TickSpacing,
                BinStep = source.BinStep,
                HookAddress = source.HookAddress,
                PricingMode = source.PricingMode
            };
        }

        private static OnChainAssetKey? Clone(OnChainAssetKey? source)
        {
            return source == null
                ? null
                : new OnChainAssetKey
                {
                    ChainNamespace = source.ChainNamespace,
                    ChainId = source.ChainId,
                    Address = source.Address
                };
        }
    }
}
