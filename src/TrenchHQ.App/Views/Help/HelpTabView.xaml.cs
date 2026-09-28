using TrenchHQ.Infrastructure.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Linq;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace TrenchHQ.Views
{
    public sealed record HelpArticle(string Topic, string Question, string Answer, bool StartExpanded = false);

    public sealed record HelpSection(string Title, HelpArticle[] Articles);

    public sealed partial class HelpTabView : UserControl
    {
        private static readonly HelpArticle[] Articles =
        [
            new("Getting started", "What is TrenchHQ?",
                """
                TrenchHQ puts the information you watch on your Windows desktop: crypto prices, public wallet activity, X posts, websites and selected application windows.

                Choose what to watch in Widgets, then choose where it appears in Desktop Setup. Panels can float over the desktop or sit along a screen edge.

                TrenchHQ's market and wallet tools are read-only. They cannot trade, sign transactions or hold your funds. Websites and applications you display keep their own features.
                """, true),
            new("Getting started", "How do I put my first price ticker on the desktop?",
                """
                1. Open Widgets, select + and choose Price Ticker.
                2. Search for a coin or pair, select a market from a public exchange and select Save.
                3. Open Desktop Setup and add an Overlay or a screen-edge panel.
                4. Choose your ticker under Panel content, then Save and Show.

                No API key is needed for public exchange prices. To open several saved panels together, select them under Desktop display on Dashboard and select Start Desktop Display.
                """),
            new("Getting started", "What is the difference between a widget and a panel?",
                """
                A widget is what you watch: a list of markets, a wallet watchlist, an X feed or a website.

                A panel is where you see it. It has its own monitor, position, size and appearance. An Overlay is a floating panel; a screen-edge panel reserves space along an edge.

                You can put the same widget in several panels. Saving changes to the widget updates all of them; each panel keeps its own layout. Application window is chosen directly as panel content.
                """),
            new("Getting started", "Do I need an account, an API key or a paid plan?",
                """
                You do not need a TrenchHQ account. Public exchange prices work without an exchange login or API key.

                On Ethereum, Base, BNB Smart Chain and Robinhood Chain, a shared public service called PublicNode can supply on-chain data without a key. Solana on-chain data needs a configured provider. Free provider plans have limits.

                X Tracker needs official paid X API access. Website widgets follow the website's own login and subscription requirements. TrenchHQ does not include third-party subscriptions.
                """),

            new("Widgets", "How do I choose the prices in a Price Ticker?",
                """
                Open Widgets, add or select a Price Ticker, then search by token name, symbol, pair, exchange or contract address. Select the markets you want, remove any you no longer need, and Save. Cancel discards your unsaved edits.

                An exchange market is a pair such as BTC/USDT on a particular exchange. An on-chain pool is a market on a blockchain. Check the chain, contract, pool and quote currency before selecting it: tokens can share names, and different markets can have different prices.
                """),
            new("Widgets", "Should I choose Every second or Live streaming?",
                """
                Every second (polling) is the default for on-chain prices. It checks a pool's current price, waits for the request to finish, then waits one second before checking again. It suits watching the current price; it does not show every trade.

                Live streaming follows incoming pool events through WebSocket or, where supported, gRPC. Choose it when you want updates between polling samples. Some pools only provide trade-based prices and require Live streaming.

                Change the selector beside Save in the Price Ticker editor, then Save. It applies to that widget's on-chain pools; exchange markets use their own feed. Costs depend on the provider, number of markets and activity. Streaming is not always more expensive, and polling is not always cheaper.
                """),
            new("Widgets", "What can Wallet Watcher show, and how do I add a wallet?",
                """
                Add a Wallet Watcher in Widgets. Choose the chain, enter a public wallet address and optionally add a label. Save the watchlist, then assign it to a panel in Desktop Setup.

                Wallet Watcher follows supported public activity for those addresses. It is a monitoring feed, not a complete account statement or a portfolio valuation. Activity can arrive later than the original transaction.

                You do not connect a wallet or give TrenchHQ signing permission. Never enter a seed phrase or private key. On-chain provider requirements also apply to wallet activity.
                """),
            new("Widgets", "How do I show and use a website?",
                """
                Add a Website widget, enter its HTTPS address and Save. Choose that widget as a panel's content in Desktop Setup, then Show.

                Click, type, scroll and sign in directly on the page. Set page Zoom in Widgets and the panel's size in Desktop Setup. Reload refreshes the page; Open in browser opens it in your normal browser.

                Lock blocks clicks and typing inside the page while it keeps running. Unlock restores interaction. This does not lock the panel's position or make it click-through.
                """),
            new("Widgets", "How do I follow accounts with X Tracker?",
                """
                Configure and test your official X developer app bearer token under APIs. In Widgets, add X Tracker, add the accounts you want to follow, choose post types or a text filter, and enable Load posts through the official X API. Save and assign it to a panel.

                X Tracker displays a native feed from X's paid API. Posts can take several seconds to arrive. Text and post-type filters control what you see; they do not reduce X API charges.

                To display x.com or another existing tracker instead, use Website. That shows the site's interface and uses its own login; it does not create a native X Tracker feed.
                """),
            new("Widgets", "Which exchanges and chains are supported?",
                """
                Public spot exchanges: Binance, Bybit, OKX, Gate, KuCoin, Bitget, BingX, HTX, MEXC, Crypto.com, CoinEx, HashKey Global and WOO X.

                On-chain tickers and Wallet Watcher support Solana, Ethereum mainnet, Base, BNB Smart Chain and Robinhood Chain.

                Only supported pool types and deployments can be watched. Finding a token does not mean every pool for it is supported. Market availability also depends on your region, provider access, outages and exchange listings.
                """),

            new("Desktop panels", "Should I use an Overlay or a screen-edge panel?",
                """
                Use an Overlay for a floating window you can position on your desktop. Use a screen-edge panel when you want content along the top, bottom, left or right edge, with desktop space reserved for it.

                In Desktop Setup, choose the panel, monitor, position and Panel content. Save your changes and select Show. You can use several monitors and reuse one widget in multiple panels.
                """),
            new("Desktop panels", "How do I change size, colors and price flashes?",
                """
                Select the panel in Desktop Setup. Adjust Width and Height for an Overlay, or Thickness for a screen-edge panel. Content size changes how much room text and controls use. Background, text color and opacity settings change the panel's appearance.

                Price Ticker height follows its rows. Other content uses the saved area and scrolls when needed. Screen-edge panels can use up to 30% of a monitor; websites and pinned windows also need enough room to work.

                Enable Price flash on change to show upward price changes in green and downward changes in red. Each panel keeps its own settings, even when it shares a widget.
                """),
            new("Desktop panels", "What do Start, Minimize, Close, Stop and Delete do?",
                """
                Start Desktop Display opens the panels selected on Dashboard. Show opens one saved panel from Desktop Setup.

                Minimize hides a panel so you can bring it back. A minimized screen-edge panel gives its reserved space back to the desktop.

                Close ends that running panel but keeps its saved setup. Use Show or Start to open it again. Stop Desktop Display closes all panels and releases pinned applications.

                Delete in Desktop Setup removes a saved panel. Deleting a widget in Widgets removes the content definition used by its panels. Quit TrenchHQ closes the app and all its panels.
                """),
            new("Desktop panels", "Can I use shortcuts and start with Windows?",
                """
                Set a panel shortcut in Desktop Setup. While TrenchHQ and that panel are running, the shortcut toggles it between visible and minimized. It cannot reopen a closed panel or one stopped with Stop Desktop Display.

                Enable Launch on startup on Dashboard to start TrenchHQ when you sign in to Windows. Windows may disable startup access; check the status beside the setting and Windows Settings > Apps > Startup if it does not run.

                Closing the dashboard leaves TrenchHQ available in the Windows notification area. Use its icon to reopen the dashboard or quit the app.
                """),
            new("Desktop panels", "What happens when I watch a video or play a game fullscreen?",
                """
                Panels move behind a true fullscreen application so they do not cover it. They return when fullscreen ends.

                A normally maximized window is different: it uses the desktop space left by screen-edge panels. Minimize or close those panels when you want that space back.
                """),
            new("Desktop panels", "How do I pin another application's window?",
                """
                In Desktop Setup, choose Application window as Panel content, then Save and Show. Select Pin window, choose a running application and select Pin.

                Start with Native lock. Embedded + lock is experimental and also clips the application to the panel area. Both use a small helper loaded into the app you select and require a compatible, resizable, non-admin x64 window.

                Unpin, minimize or close the panel to release the app and restore its original window state. The application keeps running. Pinning is temporary: TrenchHQ does not remember or automatically repin your choice.
                """),

            new("Connections & usage", "What is a provider, and which key do I enter?",
                """
                A provider supplies blockchain data to TrenchHQ. Its API key identifies your project so the provider can apply your plan's limits. You may see these described as RPC or data-provider credentials.

                Under APIs, choose your provider, enter its project key and Save. Some providers also need the endpoint URL from your project dashboard. Use the fields for the appropriate chain and connection type.

                These are not wallet private keys, seed phrases or exchange-account keys. X Tracker has a separate field for its official X bearer token.
                """),
            new("Connections & usage", "What happens if I add several providers?",
                """
                Save the providers you want to make available under APIs. TrenchHQ chooses a compatible provider for each chain automatically. You do not need to keep choosing one manually.

                It switches when a connection fails or a usage limit you enabled is reached, and retries eligible providers after a wait or reset. The widget keeps its polling or streaming mode. A backup must support the same chain and mode.

                PublicNode is the final key-free fallback for the four supported EVM chains. Solana has no equivalent built-in key-free fallback. If no compatible route remains, the feed pauses until one is available.
                """),
            new("Connections & usage", "Can TrenchHQ guarantee I stay within a free allowance?",
                """
                No. TrenchHQ estimates its own usage. It does not impose a daily allowance or reserve part of your plan. You can enable your own limit under APIs > Usage.

                These figures are not your provider's actual remaining balance. Another app may use the same key, and billing rules or custom plans can differ. Check the provider dashboard for the account's usage and billing controls.

                Your limit can trigger an automatic switch to an available backup. It cannot guarantee that a provider will never charge you.
                """),
            new("Connections & usage", "How can I reduce provider usage?",
                """
                Start with Every second (polling) for current-price watching, then compare actual usage in your provider dashboard. Busy streams and idle streams can have very different costs.

                Watch only the markets and wallets you need. Reuse a saved widget when you want the same content in several panels; TrenchHQ shares matching feeds. Close unused panels or use Stop Desktop Display. Minimizing a panel is not an assurance that its feed has stopped.

                Public exchange prices do not use your blockchain-provider allowance. Website and X activity follow those services' own limits.
                """),

            new("Troubleshooting", "I saved a widget. Why is nothing on my desktop?",
                """
                A saved widget needs a panel. Open Desktop Setup, add or select a panel, choose the widget under Panel content, then Save and Show.

                Start Desktop Display opens only the panels selected under Desktop display on Dashboard. Check that selection if Start opens nothing. For an empty ticker, also check that you selected and saved at least one market in Widgets.
                """),
            new("Troubleshooting", "How do I bring back a missing panel or dashboard?",
                """
                Open TrenchHQ from its Windows notification-area icon to get back to the dashboard.

                For a minimized panel, use its shortcut or Show in Desktop Setup. A closed panel needs Show or Start Desktop Display; its shortcut alone cannot recreate it.

                After changing monitors, check the panel's monitor and position in Desktop Setup. If the display is still misplaced, Stop Desktop Display, correct the saved setup and start it again.
                """),
            new("Troubleshooting", "Why did updates stop, and what does the status dot mean?",
                """
                Green means the active content is live and current. Amber means it is connecting, recovering or stale. Red means it is unavailable or has an error. Grey means it is stopped or not configured.

                Check your internet connection, then APIs for invalid credentials, provider errors or exhausted usage guards. TrenchHQ automatically tries compatible backups; correct a rejected key before it can be used again.

                A stale value is not a current price. If one market fails while others work, check that market's availability and support rather than replacing every connection.
                """),
            new("Troubleshooting", "Why is a price different from another app?",
                """
                Compare the exact exchange or pool, trading pair and quote currency. Different venues and pools can have different prices; a token name alone is not enough to identify a market.

                Polling samples current pool state, while a trade feed can show the last execution price. Timing, liquidity, quote conversion and a stale connection can also cause differences. Check the panel's status before treating a displayed value as current.
                """),
            new("Troubleshooting", "Why will a website not load or let me sign in?",
                """
                Check its HTTPS address and your connection, then select Reload. Try Open in browser if the site still fails.

                Some sites block embedded browsers or require popups for sign-in. Website panels block popup windows, downloads, device permissions and external-app launches, so not every site feature will work.

                Your normal browser uses a separate login. If TrenchHQ reports a missing WebView2 Runtime, install or repair Microsoft's Evergreen WebView2 Runtime from Microsoft.
                """),
            new("Troubleshooting", "Why can I not pin an application?",
                """
                The selected window must be a compatible, resizable, non-admin x64 application. Fixed-size windows, elevated apps and apps that reject the helper cannot be pinned.

                Make the panel large enough, then try Native lock. If the app behaves incorrectly, unpin it before trying Embedded + lock or a larger Overlay. Some custom windows are incompatible with both methods.

                If the application is not responding, window restoration may have to wait until it responds again.
                """),
            new("Troubleshooting", "Why is TrenchHQ using memory or network in the background?",
                """
                Open feeds and websites still need resources. Locking a website only blocks interaction; it keeps running. Minimizing it can lower memory use but does not pause its scripts or connections.

                Close panels you are not using, reduce the number of watched markets and wallets, or use Stop Desktop Display. Quit TrenchHQ to stop the application and its panels entirely.

                Pinning an application does not reduce that application's own memory use.
                """),

            new("Privacy & support", "What does TrenchHQ store or send?",
                """
                Settings are stored for your Windows user. Provider and X credentials are protected using Windows data protection (DPAPI). Wallet addresses and labels, website addresses and panel settings are ordinary local configuration.

                TrenchHQ contacts the exchanges, blockchain providers, discovery services, X and websites needed for the features you use. Those services receive the requests and apply their own policies.

                TrenchHQ does not automatically upload diagnostics or send publisher analytics. Select Privacy below for the full policy, including third-party requests and local data.
                """),
            new("Privacy & support", "Are website logins shared, and how do I delete my data?",
                """
                Each saved Website widget has its own local browser profile. Panels using that same widget share its login. TrenchHQ does not import the login from your normal browser.

                Closing a panel or deleting its widget does not erase its browser profile. Sign out on the website before deleting the widget if you want to end that session. Revoke a provider or X key in that service's dashboard when you no longer want it used.

                Windows app reset or uninstall can remove package data, but cached helpers, exported diagnostics and data held by third parties may remain. Read Privacy for the complete deletion and uninstall details.
                """),
            new("Privacy & support", "How do I report a problem or suggest a feature?",
                """
                Select Report a problem below to open TrenchHQ's GitHub Issues. Describe what you were trying to do, the steps to repeat it, what you expected and what happened. Include the app version shown below and your Windows version.

                For a bug, select Save diagnostics to create a local file, review it, then attach it if useful. It contains version, platform, memory and aggregate feed counters. It excludes credentials, settings, wallet addresses, website URLs, browser data and raw errors. Nothing is sent automatically.

                Do not attach private settings, tokens or screenshots containing personal information. Report security vulnerabilities privately through the repository's Security tab.
                """),
            new("Privacy & support", "Which Windows versions and updates can I use?",
                """
                TrenchHQ currently targets Windows x64. ARM64 and 32-bit Windows are not supported.

                This is pre-release software. Official signed downloads and the list of tested Windows versions will be announced with a release. Use the repository's announcements for release information, and do not install unsigned packages from unofficial sources.

                The version below identifies your running build and helps when reporting a problem.
                """)
        ];

        public HelpTabView()
        {
            InitializeComponent();
            var version = typeof(App).Assembly.GetName().Version!;
            VersionText.Text = $"TrenchHQ {version} · Windows x64 · Pre-release";
            HelpSections.ItemsSource = Articles.GroupBy(article => article.Topic)
                .Select(group => new HelpSection(group.Key, group.ToArray())).ToArray();
        }

        private void SetSupportStatus(string message)
        {
            SupportStatus.Text = message;
            SupportStatus.Visibility = Visibility.Visible;
        }

        private async void OnPrivacyClick(object sender, RoutedEventArgs e)
        {
            PrivacyButton.IsEnabled = false;
            try
            {
                var policy = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Privacy.txt"));
                await new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "Privacy", CloseButtonText = "Close",
                    Content = new ScrollViewer { MaxHeight = 480, Content = new TextBlock
                    { Text = policy, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }
                }.ShowAsync();
            }
            catch { SetSupportStatus("Could not open the privacy policy. Select Report a problem to contact support."); }
            finally { PrivacyButton.IsEnabled = true; }
        }

        private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
        {
            ExportDiagnosticsButton.IsEnabled = false;
            try
            {
                var picker = new FileSavePicker { SuggestedFileName = "TrenchHQ-diagnostics" };
                picker.FileTypeChoices.Add("JSON diagnostics", new[] { ".json" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;
                await FileIO.WriteTextAsync(file, ReleaseDiagnostics.Create(
                    typeof(App).Assembly.GetName().Version!, OnChainPipelineDiagnostics.Snapshot()));
                SetSupportStatus("Diagnostics saved locally. Review the file before sharing it with support.");
            }
            catch { SetSupportStatus("Could not save diagnostics. Choose a writable folder and try again."); }
            finally { ExportDiagnosticsButton.IsEnabled = true; }
        }
    }
}
