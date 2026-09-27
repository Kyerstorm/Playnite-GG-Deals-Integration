using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GGDealsWishlist.Api;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Models;

namespace GGDealsWishlist.Providers
{
    // ---------------------------------------------------------------------------------------------
    // Wishlist acquisition
    // ---------------------------------------------------------------------------------------------

    public enum WishlistProviderState
    {
        /// <summary>The provider can deliver a wishlist.</summary>
        Available = 0,

        /// <summary>No supported mechanism exists (e.g. the official API has no wishlist endpoint).</summary>
        Unavailable = 1,

        /// <summary>The provider exists but needs configuration (credentials, input...).</summary>
        NotConfigured = 2,

        /// <summary>The provider is supported but the last request failed.</summary>
        Error = 3
    }

    [Flags]
    public enum WishlistCapabilities
    {
        None = 0,
        WishlistOrder = 1,
        AddedDate = 2,
        DiscountInformation = 4,
        StoreOffers = 8,
        OfficialGGDealsSource = 16
    }

    public sealed class WishlistFetchResult
    {
        public WishlistProviderState State { get; set; }

        public List<WishlistEntry> Entries { get; set; } = new List<WishlistEntry>();

        public ApiErrorKind Error { get; set; }

        public string Message { get; set; }

        public DateTime? RetrievedUtc { get; set; }

        public static WishlistFetchResult Unavailable(string message) =>
            new WishlistFetchResult { State = WishlistProviderState.Unavailable, Message = message };
    }

    /// <summary>
    /// Source of the user's GG.deals wishlist. The rest of the extension (UI, cache, filtering, matching) only
    /// talks to this abstraction, so an official GG.deals wishlist endpoint can be added later as a new
    /// implementation (e.g. OfficialGGDealsWishlistProvider) without touching anything else.
    /// </summary>
    public interface IGGDealsWishlistProvider
    {
        string Id { get; }

        string DisplayName { get; }

        WishlistCapabilities Capabilities { get; }

        /// <summary>Cheap, synchronous availability check (no network).</summary>
        WishlistProviderState GetState();

        /// <summary>Explains the current state to the user.</summary>
        string StateMessage { get; }

        /// <summary>Returns the wishlist; providers may serve from their own short-lived cache.</summary>
        Task<WishlistFetchResult> GetWishlistAsync(CancellationToken cancellationToken);

        Task<WishlistEntry> GetWishlistItemAsync(string key, CancellationToken cancellationToken);

        /// <summary>Forces a fresh retrieval from the source.</summary>
        Task<WishlistFetchResult> RefreshAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// The current GG.deals API documents price and bundle endpoints only - there is no personal wishlist
    /// endpoint. This provider states that honestly instead of scraping the website or inventing an endpoint.
    /// </summary>
    public sealed class UnavailableWishlistProvider : IGGDealsWishlistProvider
    {
        public const string ProviderId = "unavailable";

        public string Id => ProviderId;

        public string DisplayName => "GG.deals wishlist (not available through the API)";

        public WishlistCapabilities Capabilities => WishlistCapabilities.None;

        public string StateMessage =>
            "Wishlist access is not currently available through the configured GG.deals API. " +
            "The extension can display GG.deals pricing once wishlist access is supported.";

        public WishlistProviderState GetState() => WishlistProviderState.Unavailable;

        public Task<WishlistFetchResult> GetWishlistAsync(CancellationToken cancellationToken) =>
            Task.FromResult(WishlistFetchResult.Unavailable(StateMessage));

        public Task<WishlistEntry> GetWishlistItemAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<WishlistEntry>(null);

        public Task<WishlistFetchResult> RefreshAsync(CancellationToken cancellationToken) => GetWishlistAsync(cancellationToken);
    }

    /// <summary>
    /// Opt-in local list of Steam ids/store links typed by the user in settings. It never reads GG.deals pages;
    /// GG.deals titles and URLs arrive later through the official Prices API. Useful until an official
    /// wishlist endpoint exists.
    /// </summary>
    public sealed class ManualWishlistProvider : IGGDealsWishlistProvider
    {
        public const string ProviderId = "manual";

        private static readonly Regex StoreUrl = new Regex(@"store\.steampowered\.com/(app|sub|bundle)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Prefixed = new Regex(@"^(app|sub|bundle)\s*[:/]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Plain = new Regex(@"^(\d+)(?:\s*$|\s*[|;,\t]|\s+-\s+)", RegexOptions.Compiled);

        private readonly Func<bool> isEnabled;
        private readonly Func<string> getText;
        private readonly IClock clock;

        public ManualWishlistProvider(Func<bool> isEnabled, Func<string> getText, IClock clock = null)
        {
            this.isEnabled = isEnabled;
            this.getText = getText;
            this.clock = clock ?? SystemClock.Instance;
        }

        public string Id => ProviderId;

        public string DisplayName => "Manual list (Steam ids)";

        public WishlistCapabilities Capabilities => WishlistCapabilities.WishlistOrder;

        public string StateMessage => GetState() == WishlistProviderState.Available
            ? "Using the manual wishlist from the extension settings."
            : "The manual wishlist is disabled.";

        public WishlistProviderState GetState() => isEnabled() ? WishlistProviderState.Available : WishlistProviderState.Unavailable;

        public Task<WishlistFetchResult> GetWishlistAsync(CancellationToken cancellationToken)
        {
            if (GetState() != WishlistProviderState.Available)
            {
                return Task.FromResult(WishlistFetchResult.Unavailable(StateMessage));
            }

            return Task.FromResult(new WishlistFetchResult
            {
                State = WishlistProviderState.Available,
                Entries = Parse(getText(), out _),
                RetrievedUtc = clock.UtcNow
            });
        }

        public async Task<WishlistEntry> GetWishlistItemAsync(string key, CancellationToken cancellationToken)
        {
            var result = await GetWishlistAsync(cancellationToken).ConfigureAwait(false);
            return result.Entries.FirstOrDefault(e => e.Key == key);
        }

        public Task<WishlistFetchResult> RefreshAsync(CancellationToken cancellationToken) => GetWishlistAsync(cancellationToken);

        /// <summary>Parses one entry per line: an app id, "sub:123", "bundle:45" or a Steam store URL, optionally followed by "| Title".</summary>
        public static List<WishlistEntry> Parse(string text, out List<string> invalidLines)
        {
            invalidLines = new List<string>();
            var entries = new List<WishlistEntry>();
            var seen = new HashSet<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return entries;
            }

            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                SteamIdType type;
                string idText;
                var match = StoreUrl.Match(line);
                if (!match.Success)
                {
                    match = Prefixed.Match(line);
                }

                if (match.Success)
                {
                    type = ParseType(match.Groups[1].Value);
                    idText = match.Groups[2].Value;
                }
                else
                {
                    match = Plain.Match(line);
                    if (!match.Success)
                    {
                        invalidLines.Add(line);
                        continue;
                    }

                    type = SteamIdType.App;
                    idText = match.Groups[1].Value;
                }

                if (!long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                {
                    invalidLines.Add(line);
                    continue;
                }

                var key = WishlistEntry.BuildKey(null, type, id, null);
                if (!seen.Add(key))
                {
                    continue;
                }

                var pipe = line.IndexOf('|');
                var title = pipe >= 0 ? line.Substring(pipe + 1).Trim() : null;
                entries.Add(new WishlistEntry
                {
                    Key = key,
                    ProviderId = ProviderId,
                    SteamId = id,
                    SteamIdType = type,
                    Title = string.IsNullOrWhiteSpace(title) ? null : title,
                    WishlistPosition = entries.Count + 1
                });
            }

            return entries;
        }

        private static SteamIdType ParseType(string text)
        {
            switch (text.ToLowerInvariant())
            {
                case "sub":
                    return SteamIdType.Sub;
                case "bundle":
                    return SteamIdType.Bundle;
                default:
                    return SteamIdType.App;
            }
        }
    }

    /// <summary>Chooses the most preferred provider that can currently deliver a wishlist.</summary>
    public sealed class WishlistProviderRegistry
    {
        private readonly List<IGGDealsWishlistProvider> providers;

        /// <param name="providersInPreferenceOrder">Official GG.deals providers first; the unavailable provider last.</param>
        public WishlistProviderRegistry(IEnumerable<IGGDealsWishlistProvider> providersInPreferenceOrder)
        {
            providers = providersInPreferenceOrder.ToList();
            if (!providers.Any(p => p is UnavailableWishlistProvider))
            {
                providers.Add(new UnavailableWishlistProvider());
            }
        }

        public IReadOnlyList<IGGDealsWishlistProvider> Providers => providers;

        public IGGDealsWishlistProvider Resolve()
        {
            foreach (var provider in providers)
            {
                try
                {
                    if (provider.GetState() == WishlistProviderState.Available)
                    {
                        return provider;
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Wishlist provider " + provider.Id + " failed its availability check");
                }
            }

            // Prefer a provider that merely needs configuration over the generic "unavailable" message.
            return providers.FirstOrDefault(p => SafeState(p) == WishlistProviderState.NotConfigured)
                ?? providers.Last();
        }

        private static WishlistProviderState SafeState(IGGDealsWishlistProvider provider)
        {
            try { return provider.GetState(); }
            catch (Exception) { return WishlistProviderState.Error; }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Prices
    // ---------------------------------------------------------------------------------------------

    public sealed class PriceBatchProgress
    {
        public int Completed { get; set; }

        public int Total { get; set; }

        public DateTime? WaitingUntilUtc { get; set; }

        public IReadOnlyDictionary<string, PriceData> BatchResults { get; set; }
    }

    public sealed class PriceFetchResult
    {
        public ApiErrorKind Error { get; set; }

        public string Message { get; set; }

        public DateTime? RetryAfterUtc { get; set; }

        public int Requested { get; set; }

        public int Completed { get; set; }

        public bool IsPartial => Completed < Requested;

        /// <summary>Lookup key string → price (Found = false when GG.deals does not know the id).</summary>
        public Dictionary<string, PriceData> Results { get; } = new Dictionary<string, PriceData>();
    }

    public sealed class ConnectionTestResult
    {
        public ApiErrorKind Error { get; set; }

        public bool IsSuccess => Error == ApiErrorKind.None;

        public string Message { get; set; }

        public DateTime? RetryAfterUtc { get; set; }

        public string Currency { get; set; }
    }

    public interface IGGDealsPriceProvider
    {
        /// <summary>Fetches prices in batches, honouring rate limits. Partial results are returned on failure.</summary>
        Task<PriceFetchResult> GetPricesAsync(IReadOnlyList<PriceLookupKey> keys, string region, bool waitForRateLimit, IProgress<PriceBatchProgress> progress, CancellationToken cancellationToken);

        /// <summary>Validates a key with the cheapest possible request (one record).</summary>
        Task<ConnectionTestResult> TestConnectionAsync(string apiKey, string region, CancellationToken cancellationToken);

        RateLimitBudget GetRateLimitStatus();
    }

    /// <summary>Price provider backed by the official GG.deals Prices API.</summary>
    public sealed class GGDealsApiPriceProvider : IGGDealsPriceProvider
    {
        /// <summary>Longest pause the provider will wait in-line for the per-minute window to reopen.</summary>
        public static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(75);

        /// <summary>Steam app used for connection tests (Portal 2) - costs a single record.</summary>
        public const long ConnectionTestAppId = 620;

        private readonly IGGDealsApiClient client;
        private readonly RateLimitTracker tracker;
        private readonly Func<string> apiKeyProvider;
        private readonly IClock clock;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;

        public GGDealsApiPriceProvider(IGGDealsApiClient client, RateLimitTracker tracker, Func<string> apiKeyProvider, IClock clock = null, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            this.client = client;
            this.tracker = tracker;
            this.apiKeyProvider = apiKeyProvider;
            this.clock = clock ?? SystemClock.Instance;
            this.delay = delay ?? ((span, token) => Task.Delay(span, token));
        }

        public RateLimitBudget GetRateLimitStatus() => tracker.GetBudget();

        public async Task<PriceFetchResult> GetPricesAsync(IReadOnlyList<PriceLookupKey> keys, string region, bool waitForRateLimit, IProgress<PriceBatchProgress> progress, CancellationToken cancellationToken)
        {
            var queue = (keys ?? new PriceLookupKey[0]).Distinct().ToList();
            var result = new PriceFetchResult { Requested = queue.Count };
            if (queue.Count == 0)
            {
                return result;
            }

            var apiKey = apiKeyProvider();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.Error = ApiErrorKind.NotConfigured;
                result.Message = ApiErrorMessages.For(ApiErrorKind.NotConfigured, null);
                return result;
            }

            var index = 0;
            var rateLimitRetries = 0;
            try
            {
                while (index < queue.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var budget = tracker.GetBudget();
                    if (budget.Available <= 0)
                    {
                        var next = budget.NextAvailableUtc ?? clock.UtcNow.AddMinutes(1);
                        if (!await TryWaitAsync(next, budget, waitForRateLimit, index, queue.Count, progress, cancellationToken).ConfigureAwait(false))
                        {
                            SetRateLimited(result, next);
                            break;
                        }

                        continue;
                    }

                    // Batch: up to 100 ids of the same endpoint type, never more than the remaining budget
                    // (GG.deals rejects a whole request that exceeds the remaining limit).
                    var take = Math.Min(GGDealsApiClient.MaxIdsPerRequest, budget.Available);
                    var type = queue[index].Type;
                    var chunk = queue.Skip(index).TakeWhile(k => k.Type == type).Take(take).ToList();

                    tracker.RecordUsage(chunk.Count);
                    var response = await client.GetPricesAsync(type, chunk.Select(k => k.Id).ToList(), region, apiKey, cancellationToken).ConfigureAwait(false);
                    tracker.ApplyServerHeaders(response.RateLimit);

                    if (!response.IsSuccess)
                    {
                        if (response.Error == ApiErrorKind.RateLimited)
                        {
                            var retryAt = response.RetryAfterUtc ?? clock.UtcNow.AddMinutes(1);
                            tracker.MarkRateLimited(retryAt);
                            if (rateLimitRetries++ == 0 && await TryWaitAsync(retryAt, tracker.GetBudget(), waitForRateLimit, index, queue.Count, progress, cancellationToken).ConfigureAwait(false))
                            {
                                continue;
                            }

                            SetRateLimited(result, retryAt);
                            break;
                        }

                        result.Error = response.Error;
                        result.Message = response.Message ?? ApiErrorMessages.For(response.Error, null);
                        break;
                    }

                    var batch = new Dictionary<string, PriceData>();
                    var now = clock.UtcNow;
                    foreach (var key in chunk)
                    {
                        response.Items.TryGetValue(key.Id, out var price);
                        price = price ?? new PriceData { LookupKey = key.ToString(), Found = false };

                        // The provider owns cache bookkeeping so staleness checks never depend on the client.
                        price.LookupKey = key.ToString();
                        price.Region = string.IsNullOrWhiteSpace(price.Region) ? region : price.Region;
                        if (price.FetchedUtc == default(DateTime))
                        {
                            price.FetchedUtc = now;
                        }

                        batch[key.ToString()] = price;
                        result.Results[key.ToString()] = price;
                    }

                    index += chunk.Count;
                    result.Completed = index;
                    progress?.Report(new PriceBatchProgress { Completed = index, Total = queue.Count, BatchResults = batch });
                }
            }
            catch (OperationCanceledException)
            {
                result.Error = ApiErrorKind.Cancelled;
                result.Message = ApiErrorMessages.For(ApiErrorKind.Cancelled, null);
            }

            return result;
        }

        public async Task<ConnectionTestResult> TestConnectionAsync(string apiKey, string region, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new ConnectionTestResult { Error = ApiErrorKind.NotConfigured, Message = "Enter an API key first." };
            }

            var budget = tracker.GetBudget();
            if (budget.Available <= 0)
            {
                return new ConnectionTestResult
                {
                    Error = ApiErrorKind.RateLimited,
                    RetryAfterUtc = budget.NextAvailableUtc,
                    Message = ApiErrorMessages.For(ApiErrorKind.RateLimited, budget.NextAvailableUtc)
                };
            }

            tracker.RecordUsage(1);
            var response = await client.GetPricesAsync(SteamIdType.App, new[] { ConnectionTestAppId }, region, apiKey.Trim(), cancellationToken).ConfigureAwait(false);
            tracker.ApplyServerHeaders(response.RateLimit);
            if (response.Error == ApiErrorKind.RateLimited && response.RetryAfterUtc.HasValue)
            {
                tracker.MarkRateLimited(response.RetryAfterUtc.Value);
            }

            if (!response.IsSuccess)
            {
                return new ConnectionTestResult { Error = response.Error, Message = response.Message, RetryAfterUtc = response.RetryAfterUtc };
            }

            response.Items.TryGetValue(ConnectionTestAppId, out var price);
            return new ConnectionTestResult { Message = "✓ Connected to GG.deals", Currency = price?.Currency };
        }

        private async Task<bool> TryWaitAsync(DateTime nextUtc, RateLimitBudget budget, bool waitForRateLimit, int completed, int total, IProgress<PriceBatchProgress> progress, CancellationToken cancellationToken)
        {
            var wait = nextUtc - clock.UtcNow;
            if (!waitForRateLimit || budget.HourRemaining <= 0 || wait > MaxInlineWait)
            {
                return false;
            }

            if (wait < TimeSpan.Zero)
            {
                wait = TimeSpan.Zero;
            }

            progress?.Report(new PriceBatchProgress { Completed = completed, Total = total, WaitingUntilUtc = nextUtc });
            await delay(wait + TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            return true;
        }

        private static void SetRateLimited(PriceFetchResult result, DateTime retryAt)
        {
            result.Error = ApiErrorKind.RateLimited;
            result.RetryAfterUtc = retryAt;
            result.Message = ApiErrorMessages.For(ApiErrorKind.RateLimited, retryAt);
        }
    }
}
