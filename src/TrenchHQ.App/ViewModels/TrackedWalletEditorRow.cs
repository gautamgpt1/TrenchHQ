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
using TrenchHQ.ViewModels;
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

namespace TrenchHQ.ViewModels
{
    internal sealed class TrackedWalletEditorRow
    {
        internal TrackedWalletEditorRow(SavedTrackedWallet wallet)
        {
            Wallet = wallet;
            Icon = new SvgImageSource { UriSource = new Uri(wallet.ChainNamespace == ChainNamespaces.Solana
                ? "ms-appx:///Assets/Chains/solana.svg"
                : wallet.ChainId switch
                {
                    EvmChainDefinitions.EthereumMainnetChainId => "ms-appx:///Assets/Chains/ethereum.svg",
                    EvmChainDefinitions.BaseMainnetChainId => "ms-appx:///Assets/Chains/base.svg",
                    EvmChainDefinitions.BnbMainnetChainId => "ms-appx:///Assets/Chains/bnb-chain.svg",
                    EvmChainDefinitions.RobinhoodMainnetChainId => "ms-appx:///Assets/Chains/robinhood-chain.svg",
                    _ => "ms-appx:///Assets/Chains/ethereum.svg"
                }) };
        }

        internal SavedTrackedWallet Wallet { get; }
        public string Key => SavedWidgetCatalogRules.GetWalletKey(Wallet);
        public string Label => Wallet.Label;
        public string ChainLabel => SavedWidgetCatalogRules.GetWalletChainDisplayName(Wallet);
        public string Address => Wallet.Address;
        public SvgImageSource Icon { get; }
    }

}
