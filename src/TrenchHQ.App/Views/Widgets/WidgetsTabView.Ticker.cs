using TrenchHQ.ViewModels;
using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.OnChain.Evm;
using TrenchHQ.Core.OnChain.Solana;
using TrenchHQ.Core.Providers;
using TrenchHQ.Core.Social;
using TrenchHQ.Core.Widgets;
using TrenchHQ.Infrastructure.Markets;
using TrenchHQ.Infrastructure.OnChain;
using TrenchHQ.Infrastructure.OnChain.Evm;
using TrenchHQ.Infrastructure.OnChain.Solana;
using TrenchHQ.Infrastructure.Providers;
using TrenchHQ.Infrastructure.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.System;

namespace TrenchHQ.Views
{
    public sealed partial class WidgetsTabView
    {
        private void OnMarketSelectionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox checkBox
                || checkBox.Tag is not string pair
                || _selectedWidget == null)
            {
                return;
            }

            var row = _allMarketRows.FirstOrDefault(item =>
                string.Equals(item.TagValue, pair, StringComparison.OrdinalIgnoreCase));
            if (row == null)
            {
                return;
            }
            SetInstrumentSelected(
                new SavedTickerInstrument
                {
                    Kind = TickerInstrumentTypes.CentralizedMarket,
                    DisplayLabel = row.Symbol,
                    VenueId = row.VenueId,
                    Symbol = row.Symbol
                },
                checkBox.IsChecked == true);
        }

        private void OnRemoveInstrumentClick(object sender, RoutedEventArgs e)
        {
            var instrument = (sender as FrameworkElement)?.Tag as SavedTickerInstrument;
            if (_selectedWidget == null || instrument == null)
            {
                return;
            }

            SetInstrumentSelected(instrument, false);
        }

        private async void OnInstrumentSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            var nextFilter = InstrumentSearchBox?.Text?.Trim() ?? string.Empty;
            if (!string.Equals(nextFilter, _currentFilter, StringComparison.OrdinalIgnoreCase))
            {
                _tokenSearchVersion++;
                if (!InstrumentSearchButton.IsEnabled)
                {
                    InstrumentSearchButton.IsEnabled = true;
                    PoolSearchProgress.IsActive = false;
                    PoolSearchProgress.Visibility = Visibility.Collapsed;
                }
            }
            _currentFilter = nextFilter;
            if (EvmAddress.TryNormalize(_currentFilter, out var ethereumAddress))
            {
                if (InstrumentSearchButton.IsEnabled
                    && !string.Equals(
                        ethereumAddress,
                        _displayedOnChainMint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    await SearchEvmPoolsAsync(ethereumAddress);
                }
                return;
            }
            if (!string.Equals(
                    _currentFilter,
                    _displayedOnChainMint,
                    StringComparison.OrdinalIgnoreCase))
            {
                ShowExchangeResults();
            }
            ApplyMarketFilter();
        }

        private async void OnInstrumentSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }
            e.Handled = true;
            await SearchCurrentQueryAsync();
        }

        private async void OnInstrumentSearchClick(object sender, RoutedEventArgs e)
        {
            await SearchCurrentQueryAsync();
        }

        private void OnManageProvidersClick(object sender, RoutedEventArgs e)
        {
            MainWindow.Current?.OpenProviders();
        }

        private async Task SearchCurrentQueryAsync()
        {
            if (!InstrumentSearchButton.IsEnabled)
            {
                return;
            }
            var query = InstrumentSearchBox.Text.Trim();
            _currentFilter = query;
            if (IsSolanaAddress(query))
            {
                await SearchSolanaPoolsAsync(query);
                return;
            }
            if (EvmAddress.TryNormalize(query, out var ethereumAddress))
            {
                await SearchEvmPoolsAsync(ethereumAddress);
                return;
            }
            if (query.Length == 0)
            {
                ShowExchangeResults();
                ApplyMarketFilter();
                return;
            }

            await SearchTokenPoolsAsync(query);
        }

        private async Task SearchTokenPoolsAsync(string query)
        {
            var searchVersion = ++_tokenSearchVersion;
            _displayedOnChainMint = query;
            ShowOnChainResults();
            InstrumentSearchButton.IsEnabled = false;
            PoolSearchProgress.IsActive = true;
            PoolSearchProgress.Visibility = Visibility.Visible;
            SetPoolSearchStatus(null);
            _poolRows.Clear();
            UpdateOnChainEmptyState();

            try
            {
                var result = await DexScreenerPoolCatalogClient.Current.SearchTokensAsync(query);
                if (searchVersion != _tokenSearchVersion
                    || !string.Equals(InstrumentSearchBox.Text.Trim(), query, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (Application.Current is not App app)
                {
                    throw new InvalidOperationException("The application provider service is unavailable.");
                }
                if (result.Tokens.Length == 0)
                {
                    SetPoolSearchStatus("No pools found.");
                    return;
                }

                using var validationGate = new System.Threading.SemaphoreSlim(3);
                var pendingValidations = result.Tokens.Select(async token =>
                {
                    await validationGate.WaitAsync();
                    try
                    {
                        return await TokenPoolSearchService.ValidateNamedTokenAsync(token, app.OnChainProviders);
                    }
                    finally
                    {
                        validationGate.Release();
                    }
                }).ToList();
                while (pendingValidations.Count > 0)
                {
                    var completed = await Task.WhenAny(pendingValidations);
                    pendingValidations.Remove(completed);
                    var validation = await completed;
                    if (searchVersion != _tokenSearchVersion)
                    {
                        return;
                    }

                    PopulateValidatedTokenPoolRows(validation);
                    UpdateOnChainEmptyState();
                }
                SetPoolSearchStatus(_poolRows.Count == 0 ? "No supported pools found." : null);
            }
            catch
            {
                if (searchVersion == _tokenSearchVersion)
                {
                    SetPoolSearchStatus("Pool search is unavailable. Try again.");
                }
            }
            finally
            {
                if (searchVersion == _tokenSearchVersion)
                {
                    InstrumentSearchButton.IsEnabled = true;
                    PoolSearchProgress.IsActive = false;
                    PoolSearchProgress.Visibility = Visibility.Collapsed;
                    UpdateOnChainEmptyState();
                }
            }
        }

        private async Task SearchSolanaPoolsAsync(string mint)
        {
            _displayedOnChainMint = mint;
            ShowOnChainResults();
            ProviderRequiredPanel.Visibility = Visibility.Collapsed;
            InstrumentSearchButton.IsEnabled = false;
            PoolSearchProgress.IsActive = true;
            PoolSearchProgress.Visibility = Visibility.Visible;
            SetPoolSearchStatus(null);
            _poolRows.Clear();
            UpdateOnChainEmptyState();
            PoolCatalogSearchResult? catalogResult = null;
            OnChainPoolDiscoveryResult? discoveryResult = null;
            Exception? catalogError = null;
            Exception? discoveryError = null;
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var discovery = new OnChainPoolDiscoveryService(
                new SolanaRpcClient(httpClient, OnChainPoolDiscoveryService.PublicMainnetRpcEndpoint),
                OnChainEngineClient.Current);
            var catalogTask = DexScreenerPoolCatalogClient.Current.SearchAsync("solana", mint);
            var meteoraV1CatalogTask = MeteoraDammV1PoolCatalogClient.Current.SearchAsync(mint);
            var meteoraCatalogTask = MeteoraDammV2PoolCatalogClient.Current.SearchAsync(mint);
            var manifestCatalogTask = ManifestPoolCatalogClient.Current.SearchAsync(mint);
            try
            {
                catalogResult = await catalogTask;
                PopulatePoolRows(
                    ChainNamespaces.Solana,
                    "mainnet-beta",
                    mint,
                    catalogResult,
                    null,
                    "On-chain validation is still in progress.");
                SetPoolSearchStatus(null);
                UpdateOnChainEmptyState();
            }
            catch (Exception exception)
            {
                catalogError = exception;
            }
            try
            {
                var meteoraV1Catalog = await meteoraV1CatalogTask;
                catalogResult = TokenPoolSearchService.MergeCatalogResults(catalogResult, meteoraV1Catalog, mint);
            }
            catch (Exception exception)
            {
                catalogError = catalogError == null
                    ? exception
                    : new AggregateException(catalogError, exception);
            }
            try
            {
                var meteoraCatalog = await meteoraCatalogTask;
                catalogResult = TokenPoolSearchService.MergeCatalogResults(catalogResult, meteoraCatalog, mint);
            }
            catch (Exception exception)
            {
                catalogError = catalogError == null
                    ? exception
                    : new AggregateException(catalogError, exception);
            }
            try
            {
                var manifestCatalog = await manifestCatalogTask;
                catalogResult = TokenPoolSearchService.MergeCatalogResults(catalogResult, manifestCatalog, mint);
            }
            catch (Exception exception)
            {
                catalogError = catalogError == null
                    ? exception
                    : new AggregateException(catalogError, exception);
            }
            try
            {
                discoveryResult = await discovery.DiscoverAsync(
                    mint,
                    OnChainCommitment.Confirmed,
                    catalogResult?.Pools.Select(static pool => pool.PoolAddress).ToArray());
            }
            catch (Exception exception)
            {
                discoveryError = exception;
            }
            try
            {
                PopulatePoolRows(
                    ChainNamespaces.Solana,
                    "mainnet-beta",
                    mint,
                    catalogResult,
                    discoveryResult,
                    discoveryResult == null
                        ? discoveryError?.Message ?? "On-chain validation is unavailable."
                        : null);
                SetPoolSearchStatus(BuildPoolSearchStatus(
                    catalogResult,
                    discoveryResult,
                    catalogError,
                    discoveryError));
            }
            catch (Exception)
            {
                SetPoolSearchStatus("Pool results could not be displayed. Try again.");
            }
            finally
            {
                InstrumentSearchButton.IsEnabled = true;
                PoolSearchProgress.IsActive = false;
                PoolSearchProgress.Visibility = Visibility.Collapsed;
                UpdateOnChainEmptyState();
            }
        }

        private static PoolCatalogSearchResult CreateCatalogResult(TokenCatalogSearchEntry token)
        {
            return new PoolCatalogSearchResult
            {
                SourceId = "dexscreener",
                ChainId = token.ChainId,
                AssetAddress = token.Address,
                AssetName = token.Name,
                AssetSymbol = token.Symbol,
                IconUri = token.IconUri,
                Pools = token.Pools
            };
        }

        private void PopulateValidatedTokenPoolRows(NamedTokenPoolValidation validation)
        {
            var supportedPools = validation.Discovery?.Pools
                .Where(static pool => pool.SupportStatus == OnChainSupportStatus.Supported)
                .ToArray() ?? [];
            if (supportedPools.Length == 0)
            {
                return;
            }

            var comparer = string.Equals(validation.ChainNamespace, ChainNamespaces.Eip155, StringComparison.Ordinal)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var supportedAddresses = supportedPools
                .Select(static pool => pool.PoolKey.PoolAddress)
                .ToHashSet(comparer);
            var catalog = CreateCatalogResult(validation.Token);
            catalog.Pools = catalog.Pools
                .Where(pool => supportedAddresses.Contains(pool.PoolAddress))
                .ToArray();
            var discovery = new OnChainPoolDiscoveryResult
            {
                Mint = validation.Discovery!.Mint,
                Pools = supportedPools,
                Truncated = validation.Discovery.Truncated,
                Warnings = validation.Discovery.Warnings
            };
            PopulatePoolRows(
                validation.ChainNamespace,
                validation.ChainId,
                validation.Token.Address,
                catalog,
                discovery,
                null,
                append: true);
        }

        private async Task SearchEvmPoolsAsync(string tokenAddress)
        {
            _displayedOnChainMint = tokenAddress;
            ShowOnChainResults();
            ProviderRequiredPanel.Visibility = Visibility.Collapsed;
            InstrumentSearchButton.IsEnabled = false;
            PoolSearchProgress.IsActive = true;
            PoolSearchProgress.Visibility = Visibility.Visible;
            SetPoolSearchStatus(null);
            _poolRows.Clear();
            UpdateOnChainEmptyState();
            try
            {
                if (Application.Current is not App app)
                {
                    throw new InvalidOperationException("The application provider service is unavailable.");
                }
                var results = await Task.WhenAll(
                    TokenPoolSearchService.SearchEvmChainAsync(
                        tokenAddress,
                        EvmChainDefinitions.EthereumMainnet,
                        EthereumDeploymentRegistry.Catalog,
                        app.OnChainProviders),
                    TokenPoolSearchService.SearchEvmChainAsync(
                        tokenAddress,
                        EvmChainDefinitions.BaseMainnet,
                        BaseDeploymentRegistry.Catalog,
                        app.OnChainProviders),
                    TokenPoolSearchService.SearchEvmChainAsync(
                        tokenAddress,
                        EvmChainDefinitions.BnbMainnet,
                        BnbDeploymentRegistry.Catalog,
                        app.OnChainProviders),
                    TokenPoolSearchService.SearchEvmChainAsync(
                        tokenAddress,
                        EvmChainDefinitions.RobinhoodMainnet,
                        RobinhoodDeploymentRegistry.Catalog,
                        app.OnChainProviders));
                foreach (var result in results)
                {
                    PopulatePoolRows(
                        result.Chain.ChainNamespace,
                        result.Chain.ChainId,
                        tokenAddress,
                        result.Catalog,
                        result.Discovery,
                        result.Discovery == null
                            ? result.DiscoveryError?.Message ?? "On-chain validation is unavailable."
                            : null,
                        append: true);
                }
                SetPoolSearchStatus(_poolRows.Count == 0 ? "No supported pools found." : null);
            }
            catch (Exception)
            {
                SetPoolSearchStatus("Pool results could not be displayed. Try again.");
            }
            finally
            {
                InstrumentSearchButton.IsEnabled = true;
                PoolSearchProgress.IsActive = false;
                PoolSearchProgress.Visibility = Visibility.Collapsed;
                UpdateOnChainEmptyState();
            }
        }

        private void OnPoolSelectionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox checkBox
                || checkBox.Tag is not OnChainPoolResultRow row
                || !row.IsSelectable)
            {
                return;
            }
            if (checkBox.IsChecked != true)
            {
                SetInstrumentSelected(new SavedTickerInstrument
                {
                    Kind = TickerInstrumentTypes.OnChainPool,
                    Pool = row.Descriptor
                }, false);
                return;
            }
            if (Application.Current is not App app
                || app.OnChainProviders.GetSelectedConfiguration(
                    row.Descriptor.PoolKey.ChainNamespace,
                    row.Descriptor.PoolKey.ChainId) == null)
            {
                row.IsSelected = false;
                checkBox.IsChecked = false;
                ProviderRequiredPanel.Visibility = Visibility.Visible;
                return;
            }
            ProviderRequiredPanel.Visibility = Visibility.Collapsed;
            SetInstrumentSelected(new SavedTickerInstrument
            {
                Kind = TickerInstrumentTypes.OnChainPool,
                DisplayLabel = row.SelectionDisplayLabel,
                SelectedMint = row.SelectedMint,
                AssetName = row.AssetName,
                AssetSymbol = row.AssetSymbol,
                QuoteSymbol = row.QuoteSymbol,
                IconUri = row.IconUri,
                Pool = row.Descriptor
            }, checkBox.IsChecked == true);
        }

        private void PopulatePoolRows(
            string chainNamespace,
            string chainId,
            string mint,
            PoolCatalogSearchResult? catalogResult,
            OnChainPoolDiscoveryResult? discoveryResult,
            string? unvalidatedReason,
            bool append = false)
        {
            if (!append)
            {
                _poolRows.Clear();
            }
            var comparer = string.Equals(chainNamespace, ChainNamespaces.Eip155, StringComparison.Ordinal)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var directPools = (discoveryResult?.Pools ?? [])
                .GroupBy(static pool => pool.PoolKey.PoolAddress, comparer)
                .ToDictionary(static group => group.Key, static group => group.First(), comparer);
            var matched = new HashSet<string>(comparer);
            var rows = new List<OnChainPoolResultRow>();
            foreach (var catalogPool in catalogResult?.Pools ?? [])
            {
                var descriptor = directPools.TryGetValue(catalogPool.PoolAddress, out var direct)
                    ? direct
                    : CreateCatalogDescriptor(
                        chainNamespace,
                        chainId,
                        mint,
                        catalogPool,
                        unvalidatedReason);
                matched.Add(catalogPool.PoolAddress);
                rows.Add(CreatePoolRow(descriptor, mint, catalogPool, catalogResult));
            }
            foreach (var descriptor in directPools.Values.Where(pool => !matched.Contains(pool.PoolKey.PoolAddress)))
            {
                rows.Add(CreatePoolRow(descriptor, mint, null, catalogResult));
            }

            rows = rows
                .Where(static row => row.IsSelectable)
                .OrderByDescending(static row => row.IsSelectable)
                .ThenByDescending(static row => row.LiquidityUsd ?? double.MinValue)
                .ThenBy(static row => row.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static row => row.Descriptor.PoolKey.PoolAddress, comparer)
                .ToList();
            foreach (var row in rows)
            {
                _poolRows.Add(row);
            }
            var metadataChanged = false;
            foreach (var row in rows)
            {
                metadataChanged |= EnrichSelectedInstrumentMetadata(row);
            }
            if (metadataChanged)
            {
                UpdateSelectedInstrumentPresentation();
                SetDirty(true);
            }
        }

        private bool EnrichSelectedInstrumentMetadata(OnChainPoolResultRow row)
        {
            if (!row.IsSelected || row.CatalogPool == null)
            {
                return false;
            }
            for (var index = 0; index < SelectedInstrumentsChips.Items.Count; index++)
            {
                if (SelectedInstrumentsChips.Items[index] is not SavedTickerInstrument instrument
                    || !string.Equals(
                        SavedWidgetCatalogRules.GetInstrumentKey(instrument),
                        row.Key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var changed = !string.Equals(instrument.DisplayLabel, row.SelectionDisplayLabel, StringComparison.Ordinal)
                              || !string.Equals(instrument.AssetName, row.AssetName, StringComparison.Ordinal)
                              || !string.Equals(instrument.AssetSymbol, row.AssetSymbol, StringComparison.Ordinal)
                              || !string.Equals(instrument.QuoteSymbol, row.QuoteSymbol, StringComparison.Ordinal)
                              || !string.Equals(instrument.IconUri, row.IconUri, StringComparison.Ordinal);
                if (!changed)
                {
                    return false;
                }
                instrument.DisplayLabel = row.SelectionDisplayLabel;
                instrument.AssetName = row.AssetName;
                instrument.AssetSymbol = row.AssetSymbol;
                instrument.QuoteSymbol = row.QuoteSymbol;
                instrument.IconUri = row.IconUri;
                SelectedInstrumentsChips.Items.RemoveAt(index);
                SelectedInstrumentsChips.Items.Insert(index, instrument);
                return true;
            }
            return false;
        }

        private OnChainPoolResultRow CreatePoolRow(
            OnChainPoolDescriptor descriptor,
            string mint,
            PoolCatalogEntry? catalogPool,
            PoolCatalogSearchResult? catalogResult)
        {
            return new OnChainPoolResultRow(descriptor, mint, catalogPool, catalogResult)
            {
                IsSelected = ContainsInstrumentKey(SavedWidgetCatalogRules.GetInstrumentKey(
                    new SavedTickerInstrument
                    {
                        Kind = TickerInstrumentTypes.OnChainPool,
                        Pool = descriptor
                    }))
            };
        }

        private static OnChainPoolDescriptor CreateCatalogDescriptor(
            string chainNamespace,
            string chainId,
            string mint,
            PoolCatalogEntry pool,
            string? unvalidatedReason)
        {
            return new OnChainPoolDescriptor
            {
                PoolKey = new OnChainPoolKey
                {
                    DeploymentKey = new OnChainDeploymentKey
                    {
                        ChainNamespace = chainNamespace,
                        ChainId = chainId,
                        ProtocolId = pool.ProtocolId
                    },
                    PoolId = pool.PoolAddress
                },
                PoolType = pool.ProtocolId,
                BaseMint = pool.BaseAsset.Address,
                QuoteMint = pool.QuoteAsset.Address,
                PairOrientation = string.Equals(pool.BaseAsset.Address, mint, StringComparison.OrdinalIgnoreCase)
                    ? "selectedAsBase"
                    : "selectedAsQuote",
                DiscoverySource = pool.SourceId,
                SupportStatus = unvalidatedReason == null
                    ? OnChainSupportStatus.DiscoveredUnsupported
                    : OnChainSupportStatus.TemporarilyUnavailable,
                SupportReason = null
            };
        }

        private static string BuildPoolSearchStatus(
            PoolCatalogSearchResult? catalog,
            OnChainPoolDiscoveryResult? discovery,
            Exception? catalogError,
            Exception? discoveryError)
        {
            if ((catalog?.Pools.Length ?? 0) > 0 || (discovery?.Pools.Length ?? 0) > 0)
            {
                return string.Empty;
            }
            return catalogError != null || discoveryError != null
                ? "Pool search is temporarily unavailable. Try again."
                : "No pools were found.";
        }

        private void SetPoolSearchStatus(string? message)
        {
            var hasMessage = !string.IsNullOrWhiteSpace(message);
            PoolSearchStatusText.Text = hasMessage ? message : string.Empty;
            PoolSearchStatusText.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string FormatMetric(double? value)
        {
            if (value is not double number)
            {
                return "--";
            }
            return number switch
            {
                >= 1_000_000_000 => $"${number / 1_000_000_000:0.##}B",
                >= 1_000_000 => $"${number / 1_000_000:0.##}M",
                >= 1_000 => $"${number / 1_000:0.##}K",
                >= 0 => $"${number:0.##}",
                _ => "--"
            };
        }

        private static string FormatAge(long createdAtUnixMs)
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(createdAtUnixMs);
            return age.TotalDays >= 1
                ? $"{Math.Max(0, (int)age.TotalDays)}d"
                : age.TotalHours >= 1
                    ? $"{Math.Max(0, (int)age.TotalHours)}h"
                    : $"{Math.Max(1, (int)age.TotalMinutes)}m";
        }

    }
}
