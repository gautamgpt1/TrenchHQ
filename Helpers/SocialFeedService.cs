using TrenchHQ.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrenchHQ.Helpers
{
    internal sealed class SocialFeedService
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _lifecycle = new(1, 1);
        private readonly Dictionary<Guid, string[]> _watches = [];
        private readonly SocialPostCache _cache = new();
        private readonly string _path;
        private SocialProviderSettings _settings = new();
        private CancellationTokenSource? _runCancellation;
        private Task _run = Task.CompletedTask;
        private string _runKey = "";
        private bool _stopped;
        private bool _loadFailed;
        private string _status = "Configure the official X API in API settings, or use a Website widget.";
        internal Task Initialization { get; }
        internal event EventHandler? Changed;
        internal string Status { get { lock (_sync) return _status; } }
        internal string? ActiveProviderId { get { lock (_sync) return _settings.ActiveProviderId; } }

        internal SocialFeedService(string folder)
        {
            _path = Path.Combine(folder, "social_providers.dpapi");
            Initialization = LoadAsync();
        }

        private async Task LoadAsync()
        {
            try { var settings = await SocialProviderStore.LoadAsync(_path); lock (_sync) _settings = settings; }
            catch { _loadFailed = true; SetStatus("Saved X source settings could not be decrypted/read. The existing file has been preserved."); }
        }

        internal SocialProviderConfiguration Configuration(string id)
        {
            lock (_sync)
                return Clone(_settings.Providers.FirstOrDefault(p => p.Id == id)
                    ?? new SocialProviderConfiguration { Id = id, Endpoint = SocialFeedRules.Preset(id).Endpoint });
        }

        internal string[] WatchedHandles()
        { lock (_sync) return _watches.Values.SelectMany(w => w).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(); }

        internal void Watch(Guid owner, string[] handles)
        {
            lock (_sync)
            {
                if (_stopped) return;
                if (handles.Length == 0) _watches.Remove(owner);
                else _watches[owner] = handles;
            }
            _ = ReconcileAsync();
        }

        internal SocialPost[] Snapshot() { lock (_sync) return _cache.Snapshot(); }

        internal async Task TestAsync(SocialProviderConfiguration config, CancellationToken ct)
        {
            await Initialization;
            var error = SocialFeedRules.Validate(config);
            if (error.Length > 0) throw new SocialSourceException(error, true);
            if (ActiveProviderId == config.Id)
                throw new SocialSourceException("Pause the active X source before testing another connection to it.", true);
            using var source = new SocialFeedSources();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(55));
            await source.RunAsync(config, [], _ => { }, _ => { }, timeout.Token, probe: true);
        }

        internal async Task SaveAndUseAsync(SocialProviderConfiguration config)
        {
            await Initialization;
            if (_loadFailed) throw new InvalidOperationException("Existing X source settings are unreadable; preserve and recover the settings file before replacing it.");
            var error = SocialFeedRules.Validate(config);
            if (error.Length > 0) throw new SocialSourceException(error, true);
            await _lifecycle.WaitAsync();
            try
            {
                if (_stopped) throw new InvalidOperationException("X feed has stopped.");
                var next = new SocialProviderSettings
                {
                    ActiveProviderId = config.Id,
                    Providers = [Clone(config)]
                };
                await SocialProviderStore.SaveAsync(_path, next);
                lock (_sync) _settings = next;
                _runKey = "";
            }
            finally { _lifecycle.Release(); }
            await ReconcileAsync();
        }

        internal async Task PauseAsync()
        {
            await Initialization;
            await _lifecycle.WaitAsync();
            try
            {
                var next = new SocialProviderSettings { Providers = _settings.Providers, ActiveProviderId = null };
                if (!_loadFailed) await SocialProviderStore.SaveAsync(_path, next);
                lock (_sync) _settings = next;
            }
            finally { _lifecycle.Release(); }
            await ReconcileAsync();
        }

        internal Task RetryAsync()
        {
            lock (_sync) _runKey = "";
            return ReconcileAsync();
        }

        private async Task ReconcileAsync()
        {
            await Initialization;
            await _lifecycle.WaitAsync();
            try
            {
                var handles = WatchedHandles();
                var active = ActiveProviderId;
                var key = active + "|" + string.Join(',', handles);
                if (_stopped || key == _runKey) return;
                _runKey = key;
                _runCancellation?.Cancel();
                try { await _run; } catch (OperationCanceledException) { }
                _runCancellation?.Dispose();
                _runCancellation = null;
                if (active == null || handles.Length == 0)
                {
                    SetStatus(active == null ? "Configure the official X API in API settings, or use a Website widget." : "X source ready · open an enabled X Tracker to start.");
                    return;
                }
                if (handles.Length > 500)
                {
                    SetStatus("Enabled X Trackers exceed 500 distinct accounts together. Reduce the combined watchlist; no partial subscription was started.");
                    return;
                }
                var config = Configuration(active);
                var error = SocialFeedRules.Validate(config);
                if (error.Length > 0) { SetStatus(error); return; }
                _runCancellation = new CancellationTokenSource();
                var token = _runCancellation.Token;
                _run = Task.Run(() => SuperviseAsync(config, handles, token), token);
            }
            catch { SetStatus("X source could not start. Check its saved settings and retry."); }
            finally { _lifecycle.Release(); }
        }

        private async Task SuperviseAsync(SocialProviderConfiguration config, string[] handles, CancellationToken ct)
        {
            var attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                TimeSpan delay;
                try
                {
                    SetStatus("Connecting · " + SocialFeedRules.Preset(config.Id).Name);
                    using var source = new SocialFeedSources();
                    await source.RunAsync(config, handles, post =>
                    {
                        if (ct.IsCancellationRequested || !handles.Contains(post.Handle, StringComparer.OrdinalIgnoreCase)) return;
                        bool changed;
                        lock (_sync) changed = _cache.Apply(post);
                        if (changed) Changed?.Invoke(this, EventArgs.Empty);
                    }, status => { if (!ct.IsCancellationRequested) { attempt = 0; SetStatus(status); } }, ct);
                    if (ct.IsCancellationRequested) return;
                    throw new SocialSourceException("Source ended; posts during the gap may be missing.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (SocialSourceException error)
                {
                    SetStatus(error.Message);
                    if (error.Fatal) return;
                    delay = error.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(120, Math.Pow(2, Math.Min(++attempt, 7))));
                }
                catch
                {
                    SetStatus("X API disconnected or changed its response format. Retrying; coverage may have a gap. Check the official API settings.");
                    delay = TimeSpan.FromSeconds(Math.Min(120, Math.Pow(2, Math.Min(++attempt, 7))));
                }
                await Task.Delay(delay < TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : delay, ct);
            }
        }

        internal async Task StopAsync()
        {
            await _lifecycle.WaitAsync();
            try
            {
                _stopped = true;
                _runCancellation?.Cancel();
                try { await _run; } catch (OperationCanceledException) { }
                _runCancellation?.Dispose();
                lock (_sync) { _watches.Clear(); _cache.Clear(); }
            }
            finally { _lifecycle.Release(); }
        }

        private void SetStatus(string value)
        { lock (_sync) _status = value; Changed?.Invoke(this, EventArgs.Empty); }
        private static SocialProviderConfiguration Clone(SocialProviderConfiguration value) =>
            JsonSerializer.Deserialize<SocialProviderConfiguration>(JsonSerializer.Serialize(value))!;
    }
}
