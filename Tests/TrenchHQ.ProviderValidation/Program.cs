using System.Diagnostics;
using TrenchHQ.Helpers;
using TrenchHQ.Models;

var requestedIds = args
    .Where(static value => !string.IsNullOrWhiteSpace(value))
    .Select(static value => value.Trim())
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var exchanges = CertifiedExchangeCatalog.GetAll()
    .Where(exchange => requestedIds.Count == 0 || requestedIds.Contains(exchange.Id))
    .ToArray();
if (exchanges.Length == 0)
{
    Console.Error.WriteLine("No certified exchanges matched the requested IDs.");
    return 3;
}

var results = new List<ProviderValidationResult>();
Console.WriteLine($"TrenchHQ provider validation started {DateTimeOffset.UtcNow:O}");
Console.WriteLine("Status | Exchange | Pair | Markets | Transport | Price | Duration | Detail");

foreach (var exchange in exchanges)
{
    var result = await ValidateProviderAsync(exchange);
    results.Add(result);
    Console.WriteLine(
        $"{result.Status} | {result.Name} ({result.Id}) | {result.Pair} | {result.MarketCount} | "
        + $"{result.Transport} | {result.Price} | {result.DurationSeconds:0.0}s | {result.Detail}");
}

var passed = results.Count(result => result.Status == "PASS");
Console.WriteLine($"Summary: {passed}/{results.Count} providers passed representative spot discovery and ongoing price delivery.");
return passed == results.Count ? 0 : 2;

static async Task<ProviderValidationResult> ValidateProviderAsync(CertifiedExchange exchange)
{
    var client = new MarketSidecarClient();
    var stopwatch = Stopwatch.StartNew();
    var marketCount = 0;
    var selectedPair = "-";
    var transport = "-";
    var priceText = "-";
    var failureStage = "startup";
    TaskCompletionSource<MarketPriceUpdatedEventArgs>? ongoingPrice = null;

    void OnPriceUpdated(object? sender, MarketPriceUpdatedEventArgs args)
    {
        if (ongoingPrice == null
            || string.Equals(args.Update.Source.Transport, "seed", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(args.Update.Market.Venue.Id, exchange.Id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(args.Update.Market.Symbol, selectedPair, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ongoingPrice.TrySetResult(args);
    }

    client.PriceUpdated += OnPriceUpdated;
    try
    {
        await client.StartAsync();
        failureStage = "market discovery";
        var markets = await client.GetMarketsAsync(exchange.Id, forceRefresh: true);
        marketCount = markets.Length;
        selectedPair = SelectRepresentativePair(markets);
        if (string.IsNullOrWhiteSpace(selectedPair))
        {
            throw new InvalidOperationException("No representative spot market was returned.");
        }

        failureStage = "ongoing ticker";
        ongoingPrice = new TaskCompletionSource<MarketPriceUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        await client.SetSubscriptionsAsync(
        [
            new MarketSidecarSubscription
            {
                VenueId = exchange.Id,
                Symbol = selectedPair
            }
        ]);

        var update = await ongoingPrice.Task.WaitAsync(TimeSpan.FromSeconds(45));
        if (!double.IsFinite(update.Update.Value) || update.Update.Value <= 0)
        {
            throw new InvalidOperationException("Ticker returned a non-positive or non-finite price.");
        }

        transport = string.IsNullOrWhiteSpace(update.Update.Source.Transport) ? "unknown" : update.Update.Source.Transport;
        priceText = update.Update.Value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
        return new ProviderValidationResult(
            exchange.Id,
            exchange.Name,
            "PASS",
            selectedPair,
            marketCount,
            transport,
            priceText,
            stopwatch.Elapsed.TotalSeconds,
            "Representative spot market verified.");
    }
    catch (Exception exception)
    {
        return new ProviderValidationResult(
            exchange.Id,
            exchange.Name,
            "FAIL",
            selectedPair,
            marketCount,
            transport,
            priceText,
            stopwatch.Elapsed.TotalSeconds,
            $"{failureStage}: {SingleLine(exception.Message)}");
    }
    finally
    {
        client.PriceUpdated -= OnPriceUpdated;
        try
        {
            await client.StopAsync();
        }
        catch
        {
        }
    }
}

static string SelectRepresentativePair(FeedMarketIdentity[] markets)
{
    var preferred = new[] { "BTC/USDT", "BTC/USD", "ETH/USDT", "ETH/USD" };
    foreach (var candidate in preferred)
    {
        var match = markets.FirstOrDefault(market => string.Equals(market.Symbol, candidate, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match.Symbol;
        }
    }

    return markets.FirstOrDefault(market => market.Symbol.EndsWith("/USDT", StringComparison.OrdinalIgnoreCase))?.Symbol
        ?? markets.FirstOrDefault()?.Symbol
        ?? string.Empty;
}

static string SingleLine(string message)
{
    return message.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

internal sealed record ProviderValidationResult(
    string Id,
    string Name,
    string Status,
    string Pair,
    int MarketCount,
    string Transport,
    string Price,
    double DurationSeconds,
    string Detail);
