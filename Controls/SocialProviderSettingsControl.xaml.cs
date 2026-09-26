using TrenchHQ.Helpers;
using TrenchHQ.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;

namespace TrenchHQ.Controls
{
    public sealed partial class SocialProviderSettingsControl : UserControl
    {
        private SocialFeedService? _service;
        private SocialProviderConfiguration? _editing;
        private CancellationTokenSource? _test;
        private bool _applying;
        private int _editVersion;

        public SocialProviderSettingsControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += (_, _) => { _test?.Cancel(); if (_service != null) _service.Changed -= OnServiceChanged; };
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Application.Current is not App app) return;
            _service = app.SocialFeed;
            await _service.Initialization;
            if (!IsLoaded) return;
            _service.Changed -= OnServiceChanged;
            _service.Changed += OnServiceChanged;
            _editing = _service.Configuration("xapi");
            _applying = true;
            SecretBox.Password = _editing.Secret;
            _applying = false;
            InvalidateTest();
            RefreshState();
        }

        private SocialProviderConfiguration Read()
        {
            if (_editing == null) throw new InvalidOperationException();
            return new SocialProviderConfiguration
            {
                Id = _editing.Id, InstallationId = _editing.InstallationId,
                Endpoint = "https://api.x.com", Secret = SecretBox.Password.Trim()
            };
        }

        private void OnSecretEdited(object sender, RoutedEventArgs e) => InvalidateTest();
        private void InvalidateTest()
        {
            if (_applying || UseButton == null) return;
            _test?.Cancel();
            ++_editVersion;
            UseButton.IsEnabled = false;
            ResultText.Text = "";
        }

        private async void OnTestClick(object sender, RoutedEventArgs e)
        {
            if (_service == null) return;
            _test?.Cancel();
            _test = new CancellationTokenSource();
            var version = _editVersion;
            TestButton.IsEnabled = false;
            UseButton.IsEnabled = false;
            ResultText.Text = "Testing official X API…";
            try
            {
                await _service.TestAsync(Read(), _test.Token);
                if (version != _editVersion || !IsLoaded) return;
                UseButton.IsEnabled = true;
                ResultText.Text = "Connection check passed. Save and use to activate your watchlist. This does not verify latency or complete coverage.";
            }
            catch (SocialSourceException error) { if (version == _editVersion) ResultText.Text = error.Message; }
            catch (OperationCanceledException) { if (version == _editVersion) ResultText.Text = "Test cancelled or timed out."; }
            catch { if (version == _editVersion) ResultText.Text = "Connection check failed. Verify the source setup, endpoint and credentials."; }
            finally { TestButton.IsEnabled = true; }
        }

        private async void OnUseClick(object sender, RoutedEventArgs e)
        {
            if (_service == null) return;
            UseButton.IsEnabled = false;
            try { await _service.SaveAndUseAsync(Read()); ResultText.Text = "Saved. The official X API will serve enabled X Trackers."; }
            catch (SocialSourceException error) { ResultText.Text = error.Message; }
            catch { ResultText.Text = "Could not save X source settings. Existing settings were preserved where possible."; }
        }

        private async void OnPauseClick(object sender, RoutedEventArgs e)
        {
            _test?.Cancel();
            if (_service == null) return;
            try { await _service.PauseAsync(); ResultText.Text = "Feed paused. Your X API token is retained."; }
            catch { ResultText.Text = "Could not save the paused state."; }
        }

        private void OnServiceChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshState);
        private void RefreshState() { if (_service != null) ActiveText.Text = $"Active: {_service.ActiveProviderId ?? "none"} · {_service.Status}"; }
    }
}
