using TrenchHQ.Core.OnChain;
using TrenchHQ.Core.Providers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TrenchHQ.Infrastructure.Providers
{
    internal sealed class OnChainUsageBudgetException() : InvalidOperationException(
        "This provider is paused by its usage guard or quota response. Check Usage in APIs.");

    internal sealed class OnChainUsageGroup
    {
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public bool? Automatic { get; set; }
        public Dictionary<string, DateTimeOffset> QuotaPauses { get; set; } = new(StringComparer.Ordinal);
        public bool Enabled { get; set; }
        public decimal Limit { get; set; }
        public bool Daily { get; set; }
        public DateTimeOffset AnchorUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset PeriodStartUtc { get; set; } = DateTimeOffset.UtcNow;
        public decimal Used { get; set; }
        public bool UnknownCost { get; set; }
        public bool Blocked { get; set; }
        public long RpcRequests { get; set; }
        public long StreamMessages { get; set; }
        public long StreamBytes { get; set; }
        public Dictionary<string, long> Methods { get; set; } = new(StringComparer.Ordinal);
    }

    // Local, conservative estimates. No endpoints, credentials, account IDs or payloads are persisted.
    internal sealed class OnChainProviderUsage : IDisposable
    {
        internal const string FileName = "onchain_usage.json";
        private readonly object _sync = new();
        private readonly Func<DateTimeOffset> _now;
        private readonly string? _path;
        private readonly Timer? _timer;
        private Dictionary<string, OnChainUsageGroup> _groups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (DateTimeOffset Start, decimal Units)> _rates = new();
        private readonly HashSet<string> _infuraBlocks = new(StringComparer.Ordinal);
        private readonly Queue<string> _infuraBlockOrder = new();
        private bool _dirty;
        private bool _availabilityPending;

        internal OnChainProviderUsage(string? folder = null, Func<DateTimeOffset>? now = null)
        {
            _now = now ?? (() => DateTimeOffset.UtcNow);
            if (folder != null)
            {
                _path = Path.Combine(folder, FileName);
                if (File.Exists(_path))
                {
                    _groups = JsonSerializer.Deserialize<Dictionary<string, OnChainUsageGroup>>(
                        File.ReadAllText(_path)) ?? throw new InvalidDataException("The usage ledger is invalid.");
                }
                _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
        }

        internal event EventHandler? AvailabilityChanged;
        internal string? PersistenceError { get; private set; }

        internal OnChainUsageScope CreateScope(OnChainProviderConfiguration configuration, bool validation = false) => new(this, configuration, validation);

        internal static string GroupKey(OnChainProviderConfiguration configuration, bool grpc = false)
        {
            var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
            var family = preset.ProviderFamily;
            var key = OnChainProviderCatalog.IsPublicEvaluationProvider(configuration.ProviderType)
                ? configuration.ProviderType
                : string.IsNullOrEmpty(family) || family == "custom"
                    ? configuration.ProviderType + ":" + configuration.Id : family;
            return key + (grpc && family != "helius" ? ":grpc" : ":rpc");
        }

        internal string[] Register(OnChainProviderConfiguration configuration)
        {
            var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
            var grpc = preset.StreamTransport == OnChainStreamTransport.YellowstoneGrpc;
            var keys = grpc ? new[] { GroupKey(configuration), GroupKey(configuration, true) }.Distinct().ToArray()
                : [GroupKey(configuration)];
            lock (_sync)
            {
                foreach (var key in keys)
                {
                    if (_groups.TryGetValue(key, out var existing))
                    {
                        existing.Automatic ??= !existing.Enabled;
                        if (existing.Automatic == true)
                        {
                            existing.Enabled = false;
                            existing.Blocked = false;
                            existing.Limit = 0;
                            existing.Automatic = false;
                            _dirty = true;
                        }
                        continue;
                    }
                    var bandwidth = key.EndsWith(":grpc", StringComparison.Ordinal);
                    var unit = bandwidth ? "MiB" : preset.ProviderFamily switch
                    {
                        "alchemy" or "drpc" => "CU",
                        "helius" or "quicknode" or "infura" => "credits",
                        "chainstack" => "RU",
                        _ => "requests / notifications"
                    };
                    _groups[key] = new OnChainUsageGroup
                    {
                        Name = (string.IsNullOrEmpty(preset.ProviderFamily) || preset.ProviderFamily == "custom"
                            ? preset.DisplayName : preset.ProviderFamily) + (bandwidth ? " gRPC" : string.Empty),
                        Unit = unit,
                        Automatic = false,
                        AnchorUtc = _now(),
                        PeriodStartUtc = _now()
                    };
                    _dirty = true;
                }
            }
            return keys;
        }

        internal decimal Headroom(OnChainProviderConfiguration configuration)
        {
            var groups = Register(configuration).Select(Snapshot).Where(group => group.Enabled).ToArray();
            return groups.Length == 0 ? 1 : groups.Min(group => Math.Max(0, 1 - group.Used / group.Limit));
        }

        internal decimal PollingCapacity(OnChainProviderConfiguration configuration)
        {
            var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
            var group = Snapshot(Register(configuration)[0]);
            if (!group.Enabled) return 0;
            var cost = preset.ChainNamespace == ChainNamespaces.Solana
                ? RpcCost(preset, "getMultipleAccounts")
                : RpcCost(preset, "eth_getBlockByNumber") + RpcCost(preset, "eth_call");
            return cost > 0 ? Math.Max(0, group.Limit - group.Used) / cost.Value : 0;
        }

        private static string QuotaKey(OnChainProviderConfiguration configuration)
        {
            // Linked chains reuse a credential reference; unrelated keys at the same company do not.
            // Store only its hash, never the credential reference or endpoint.
            var identity = OnChainProviderCatalog.IsPublicEvaluationProvider(configuration.ProviderType)
                ? configuration.ProviderType
                : string.IsNullOrEmpty(configuration.CredentialReference) ? configuration.Id : configuration.CredentialReference;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }

        internal DateTimeOffset? QuotaPauseUntil(OnChainProviderConfiguration configuration)
        {
            lock (_sync)
            {
                var pauses = Register(configuration).Select(key => Snapshot(key).QuotaPauses.GetValueOrDefault(QuotaKey(configuration)))
                    .Where(until => until > _now()).ToArray();
                return pauses.Length == 0 ? null : pauses.Max();
            }
        }

        internal void PauseForQuota(OnChainProviderConfiguration configuration, TimeSpan? retryAfter)
        {
            lock (_sync)
            {
                foreach (var key in Register(configuration))
                {
                    // Infura documents a UTC-midnight daily reset. Other billing periods are unknown.
                    var delay = retryAfter ?? (OnChainProviderCatalog.Get(configuration.ProviderType).ProviderFamily == "infura"
                        ? new DateTimeOffset(_now().UtcDateTime.Date.AddDays(1), TimeSpan.Zero) - _now()
                        : TimeSpan.FromHours(1));
                    _groups[key].QuotaPauses[QuotaKey(configuration)] = _now() + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
                    _dirty = true;
                }
            }
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }

        internal void CredentialValidated(OnChainProviderConfiguration configuration)
        {
            lock (_sync)
            {
                foreach (var key in Register(configuration))
                    _dirty |= _groups[key].QuotaPauses.Remove(QuotaKey(configuration));
            }
        }

        internal static bool IsQuotaMessage(string? message) => message != null && new[]
        {
            "quota exceeded", "quota has been exceeded", "daily request count exceeded", "monthly capacity",
            "credits exhausted", "credits have been exhausted", "insufficient credits", "credit limit",
            "monthly limit", "daily limit", "compute units limit", "payment required"
        }.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));

        internal static bool IsQuotaError(JsonElement error) => error.ValueKind == JsonValueKind.Object
            && ((error.TryGetProperty("code", out var code) && code.TryGetInt32(out var number) && number == 402)
                || (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String && IsQuotaMessage(message.GetString()))
                || (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.String && IsQuotaMessage(details.GetString())));

        internal OnChainUsageGroup Snapshot(string key)
        {
            lock (_sync)
            {
                AdvancePeriod(key);
                return JsonSerializer.Deserialize<OnChainUsageGroup>(JsonSerializer.Serialize(_groups[key]))!;
            }
        }

        internal decimal? UnitsPerHour(string key)
        {
            lock (_sync)
            {
                if (!_rates.TryGetValue(key, out var rate) || (_now() - rate.Start).TotalSeconds < 30) return null;
                return rate.Units / (decimal)(_now() - rate.Start).TotalHours;
            }
        }

        internal void Configure(string key, bool enabled, decimal limit, bool daily,
            DateTimeOffset anchorUtc, decimal alreadyUsed, decimal? observedUsed = null)
        {
            if (limit < 0 || alreadyUsed < 0 || (enabled && limit <= 0) || anchorUtc > _now())
                throw new ArgumentException("Enter a positive allowance and a cycle start that is not in the future.");
            lock (_sync)
            {
                var group = _groups[key];
                group.Automatic = false;
                group.Enabled = enabled;
                group.Limit = limit;
                group.Daily = daily;
                group.AnchorUtc = anchorUtc.ToUniversalTime();
                group.PeriodStartUtc = anchorUtc.ToUniversalTime();
                AdvancePeriod(key);
                group.Used = observedUsed.HasValue ? Math.Max(0, group.Used + alreadyUsed - observedUsed.Value) : alreadyUsed;
                group.Blocked = enabled && (group.Used >= limit || group.UnknownCost);
                _rates.Remove(key);
                _dirty = true;
                FlushLocked();
            }
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }

        internal bool IsBlocked(OnChainProviderConfiguration configuration)
        {
            lock (_sync)
            {
                return Register(configuration).Any(key =>
                {
                    AdvancePeriod(key);
                    var group = _groups[key];
                    return group.QuotaPauses.GetValueOrDefault(QuotaKey(configuration)) > _now() || group.Enabled && (group.Blocked || group.UnknownCost || group.Used >= group.Limit
                        || PersistenceError != null);
                });
            }
        }

        internal void Record(OnChainProviderConfiguration configuration, string? method,
            int bytes = 0, bool grpc = false, bool notification = false, string? infuraBlock = null,
            bool head = false, bool archive = false, bool validation = false)
        {
            Register(configuration);
            var key = GroupKey(configuration, grpc);
            bool blocked;
            bool changed;
            lock (_sync)
            {
                AdvancePeriod(key);
                var group = _groups[key];
                var wasLow = group.Enabled && group.Used >= group.Limit * 0.8m;
                var wasBlocked = group.Enabled && (group.Blocked || group.UnknownCost || group.Used >= group.Limit);
                var preset = OnChainProviderCatalog.Get(configuration.ProviderType);
                decimal? cost = method != null ? RpcCost(preset, method, archive)
                    : StreamCost(preset, bytes, grpc, notification, head);
                if (preset.ProviderFamily == "infura" && infuraBlock != null)
                {
                    var identity = configuration.Id + ":" + infuraBlock;
                    if (!_infuraBlocks.Add(identity)) cost = 0;
                    else
                    {
                        _infuraBlockOrder.Enqueue(identity);
                        if (_infuraBlockOrder.Count > 4096) _infuraBlocks.Remove(_infuraBlockOrder.Dequeue());
                    }
                }
                group.UnknownCost |= !cost.HasValue;
                blocked = !validation && group.Enabled && (wasBlocked || group.UnknownCost || PersistenceError != null
                    || group.Used + (cost ?? 0) > group.Limit);
                // Stream bytes have already arrived. HTTP requests are stopped before sending.
                if (!blocked || method == null)
                {
                    group.Used += cost ?? 0;
                    if (method != null)
                    {
                        group.RpcRequests++;
                        group.Methods[method] = group.Methods.GetValueOrDefault(method) + 1;
                    }
                    else
                    {
                        group.StreamMessages++;
                        group.StreamBytes += bytes;
                    }
                    var rate = _rates.GetValueOrDefault(key, (_now(), 0m));
                    _rates[key] = (rate.Item1, rate.Item2 + (cost ?? 0));
                }
                group.Blocked = group.Enabled && (blocked || group.Used >= group.Limit);
                changed = group.Blocked != wasBlocked
                    || (!wasLow && group.Enabled && group.Used >= group.Limit * 0.8m);
                _dirty = true;
            }
            if (changed) AvailabilityChanged?.Invoke(this, EventArgs.Empty);
            if (blocked) throw new OnChainUsageBudgetException();
        }

        internal static decimal? RpcCost(OnChainProviderPreset preset, string method, bool archive = false)
        {
            var solana = preset.ChainNamespace == ChainNamespaces.Solana;
            var subscription = method.EndsWith("Subscribe", StringComparison.Ordinal) || method == "eth_subscribe";
            return preset.ProviderFamily switch
            {
                "alchemy" when solana => subscription ? 0 : method switch
                {
                    "getAccountInfo" or "getTokenAccountsByOwner" => 10,
                    "getTransaction" or "getSignaturesForAddress" => 40,
                    "getMultipleAccounts" or "getSlot" or "getProgramAccounts"
                        or "getSignatureStatuses" => 20, _ => null
                },
                "alchemy" => method switch
                {
                    "eth_chainId" => 0, "eth_blockNumber" or "eth_subscribe" => 10,
                    "eth_call" => 26, "eth_getLogs" => 60, "trace_filter" => 40,
                    "eth_getBlockByNumber" or "eth_getBlockByHash" or "eth_getBlockReceipts" or "eth_getCode" or "eth_getBalance"
                        or "eth_getTransactionReceipt" or "eth_getTransactionByHash" => 20, _ => null
                },
                "helius" => subscription ? 0 : method == "getProgramAccounts" ? 10 : 1,
                "drpc" => method == "eth_chainId" ? 0 : 20,
                "infura" => method switch
                {
                    "eth_chainId" or "eth_subscribe" => 5, "eth_getLogs" => 255,
                    "trace_filter" => 300, "eth_getBlockReceipts" => 1000, _ => 80
                },
                "chainstack" => archive || method.StartsWith("trace_", StringComparison.Ordinal)
                    || method == "getSignaturesForAddress" ? 2 : 1,
                "quicknode" => solana ? (subscription ? 0 : 30) : method == "trace_filter" ? 40 : 20,
                _ => 1
            };
        }

        private static decimal StreamCost(OnChainProviderPreset preset, int bytes, bool grpc, bool notification, bool head)
        {
            if (grpc && preset.ProviderFamily != "helius") return bytes / 1048576m;
            if (preset.ProviderFamily == "alchemy")
                return bytes * (preset.ChainNamespace == ChainNamespaces.Solana ? 0.0002m : 0.04m);
            if (preset.ProviderFamily == "helius") return bytes * 2m / 100000m;
            if (preset.ProviderFamily == "quicknode" && preset.ChainNamespace == ChainNamespaces.Solana)
                return bytes * 15m / 100000m;
            return !notification ? 0 : preset.ProviderFamily switch
            {
                "drpc" or "quicknode" => 20,
                "infura" => head ? 50 : 300,
                _ => 1
            };
        }

        private bool AdvancePeriod(string key)
        {
            var group = _groups[key];
            var now = _now();
            foreach (var expired in group.QuotaPauses.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            {
                group.QuotaPauses.Remove(expired);
                _dirty = _availabilityPending = true;
            }
            if (!group.Enabled) return false;
            var periods = group.Daily ? Math.Max(0, (int)(now - group.AnchorUtc).TotalDays)
                : Math.Max(0, (now.Year - group.AnchorUtc.Year) * 12 + now.Month - group.AnchorUtc.Month);
            var start = group.Daily ? group.AnchorUtc.AddDays(periods) : group.AnchorUtc.AddMonths(periods);
            if (start > now) start = group.Daily ? group.AnchorUtc.AddDays(--periods) : group.AnchorUtc.AddMonths(--periods);
            if (start <= group.PeriodStartUtc) return false;
            group.PeriodStartUtc = start;
            group.Used = 0;
            group.UnknownCost = false;
            group.Blocked = false;
            group.RpcRequests = group.StreamMessages = group.StreamBytes = 0;
            group.Methods.Clear();
            _rates.Remove(key);
            _dirty = true;
            _availabilityPending = true;
            return true;
        }

        internal void Tick()
        {
            bool reset;
            lock (_sync)
            {
                foreach (var key in _groups.Keys.ToArray()) AdvancePeriod(key);
                reset = _availabilityPending || PersistenceError != null;
                _availabilityPending = false;
                try { FlushLocked(); PersistenceError = null; }
                catch (IOException) { PersistenceError = "Usage could not be saved; configured budgets are paused."; }
                catch (UnauthorizedAccessException) { PersistenceError = "Usage could not be saved; configured budgets are paused."; }
            }
            if (reset || PersistenceError != null) AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }

        private void FlushLocked()
        {
            if (!_dirty || _path == null) return;
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_groups));
            File.Move(_path + ".tmp", _path, overwrite: true);
            _dirty = false;
        }

        public void Dispose()
        {
            _timer?.Dispose();
            lock (_sync) FlushLocked();
        }
    }

    internal sealed class OnChainUsageScope(OnChainProviderUsage ledger, OnChainProviderConfiguration configuration, bool validation = false) : IOnChainUsageScope
    {
        public void QuotaExceeded(TimeSpan? retryAfter = null)
        {
            ledger.PauseForQuota(configuration, retryAfter);
            throw new OnChainUsageBudgetException();
        }
        public void EnsureAvailable()
        {
            if (!validation && ledger.IsBlocked(configuration)) throw new OnChainUsageBudgetException();
        }
        public void Rpc(string method, bool archive = false)
        {
            EnsureAvailable();
            ledger.Record(configuration, method, archive: archive, validation: validation);
        }
        public void Grpc(int bytes) => ledger.Record(configuration, null, bytes, grpc: true, notification: true);
        public void WebSocket(byte[] message)
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            var notification = root.TryGetProperty("method", out _);
            string? infuraBlock = null;
            var head = false;
            if (configuration.ProviderType.StartsWith("infura", StringComparison.Ordinal)
                && root.TryGetProperty("params", out var parameters)
                && parameters.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object)
            {
                head = result.TryGetProperty("number", out var block);
                if (head || result.TryGetProperty("blockNumber", out block))
                    infuraBlock = parameters.GetProperty("subscription").GetString() + ":" + block.GetString();
            }
            ledger.Record(configuration, null, message.Length, notification: notification,
                infuraBlock: infuraBlock, head: head);
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                if (OnChainProviderUsage.IsQuotaError(error)) QuotaExceeded();
                var code = error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out var number) ? number : 0;
                var detail = error.TryGetProperty("message", out var messageText) && messageText.ValueKind == JsonValueKind.String
                    ? messageText.GetString() : null;
                var auth = code is 401 or 403 || detail != null && new[] { "unauthorized", "forbidden", "invalid api key", "invalid access token" }
                    .Any(marker => detail.Contains(marker, StringComparison.OrdinalIgnoreCase));
                var limited = code == 429 || detail?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true;
                if (auth || limited)
                    throw new OnChainProviderResponseException(auth ? OnChainProviderFailureKind.Authentication
                        : OnChainProviderFailureKind.RateLimited);
            }
        }
    }
}
