using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Models;

namespace GGDealsWishlist.Providers
{
    /// <summary>Parses the ways users identify their Steam account (SteamID64 or a /profiles/ link).</summary>
    public static class SteamIdParser
    {
        // Individual-account SteamID64s start at 76561197960265728 (universe 1, type 1).
        private const ulong IndividualBase = 76561197960265728UL;
        private static readonly Regex ProfileUrl = new Regex(@"steamcommunity\.com/profiles/(\d{17})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VanityUrl = new Regex(@"steamcommunity\.com/id/[^/\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryParse(string text, out ulong steamId)
        {
            steamId = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var value = text.Trim();
            var match = ProfileUrl.Match(value);
            if (match.Success)
            {
                value = match.Groups[1].Value;
            }

            return value.Length == 17
                && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out steamId)
                && steamId > IndividualBase
                && steamId < IndividualBase + uint.MaxValue;
        }

        /// <summary>Explains why <paramref name="text"/> is not usable; null when it is valid or empty.</summary>
        public static string Describe(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || TryParse(text, out _))
            {
                return null;
            }

            return VanityUrl.IsMatch(text)
                ? "Custom profile links (steamcommunity.com/id/…) can't be used. Copy your 17-digit SteamID64 instead (Steam → Account details, or a site such as steamid.io)."
                : "Enter your 17-digit SteamID64 (it starts with 7656119) or a steamcommunity.com/profiles/… link.";
        }
    }

    /// <summary>
    /// Reads a public Steam wishlist through Valve's official Web API (IWishlistService/GetWishlist) and names
    /// the games via IStoreBrowseService/GetItems. Only the list and names come from Steam; all prices still
    /// come from the GG.deals Prices API. No Steam login or key is used, so private wishlists cannot be read.
    /// </summary>
    public sealed class SteamWishlistProvider : IGGDealsWishlistProvider, IDisposable
    {
        public const string ProviderId = "steam";
        internal const string Endpoint = "https://api.steampowered.com/IWishlistService/GetWishlist/v1/";
        internal const string StoreItemsEndpoint = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/";
        internal const int NameBatchSize = 100;

        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

        // appid → Steam store name; null marks apps Steam reported as unknown so they aren't requested again.
        private readonly Dictionary<long, string> names = new Dictionary<long, string>();

        private readonly Func<bool> isEnabled;
        private readonly Func<string> getSteamId;
        private readonly IClock clock;
        private readonly HttpClient http;
        private readonly object sync = new object();
        private WishlistFetchResult cached;
        private ulong cachedFor;

        public SteamWishlistProvider(Func<bool> isEnabled, Func<string> getSteamId, HttpMessageHandler handler = null, IClock clock = null)
        {
            this.isEnabled = isEnabled;
            this.getSteamId = getSteamId;
            this.clock = clock ?? SystemClock.Instance;
            http = handler == null ? new HttpClient() : new HttpClient(handler, false);
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Playnite-GGDealsWishlist/1.0");
        }

        public string Id => ProviderId;

        public string DisplayName => "Steam wishlist";

        public WishlistCapabilities Capabilities => WishlistCapabilities.WishlistOrder | WishlistCapabilities.AddedDate;

        public string StateMessage
        {
            get
            {
                switch (GetState())
                {
                    case WishlistProviderState.Available:
                        return "Using your public Steam wishlist. Prices come from GG.deals.";
                    case WishlistProviderState.NotConfigured:
                        return "Add your SteamID64 in the extension settings (Wishlist source) to load your Steam wishlist.";
                    default:
                        return "The Steam wishlist source is turned off.";
                }
            }
        }

        public WishlistProviderState GetState()
        {
            if (!isEnabled())
            {
                return WishlistProviderState.Unavailable;
            }

            return SteamIdParser.TryParse(getSteamId(), out _) ? WishlistProviderState.Available : WishlistProviderState.NotConfigured;
        }

        public Task<WishlistFetchResult> GetWishlistAsync(CancellationToken cancellationToken) => FetchAsync(false, cancellationToken);

        public Task<WishlistFetchResult> RefreshAsync(CancellationToken cancellationToken) => FetchAsync(true, cancellationToken);

        public async Task<WishlistEntry> GetWishlistItemAsync(string key, CancellationToken cancellationToken)
        {
            var result = await GetWishlistAsync(cancellationToken).ConfigureAwait(false);
            return result.Entries.FirstOrDefault(e => e.Key == key);
        }

        private async Task<WishlistFetchResult> FetchAsync(bool force, CancellationToken cancellationToken)
        {
            var state = GetState();
            if (state != WishlistProviderState.Available)
            {
                return new WishlistFetchResult { State = state, Message = StateMessage };
            }

            SteamIdParser.TryParse(getSteamId(), out var steamId);
            lock (sync)
            {
                // A short cache avoids asking Steam twice when refreshes happen back-to-back.
                if (!force && cached != null && cachedFor == steamId && cached.RetrievedUtc.HasValue && clock.UtcNow - cached.RetrievedUtc.Value < CacheLifetime)
                {
                    return cached;
                }
            }

            var uri = Endpoint + "?steamid=" + steamId.ToString(CultureInfo.InvariantCulture);
            WishlistFetchResult result;
            try
            {
                using (var response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false))
                {
                    var body = response.Content == null ? null : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    result = Interpret(response.StatusCode, body, clock.UtcNow);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is WebException)
            {
                Log.Warn("Steam wishlist request failed: " + e.GetType().Name);
                result = Failure(ApiErrorKind.Network, "Steam could not be reached right now. Showing the most recent cached wishlist.");
            }

            if (result.State == WishlistProviderState.Available)
            {
                await AddStoreNamesAsync(result.Entries, cancellationToken).ConfigureAwait(false);
                lock (sync)
                {
                    cached = result;
                    cachedFor = steamId;
                }
            }

            return result;
        }

        /// <summary>
        /// Best-effort: fills <see cref="WishlistEntry.Title"/> from Steam's store (IStoreBrowseService/GetItems)
        /// so games show their names before GG.deals prices arrive, and even when GG.deals doesn't list them.
        /// Only names are used; Steam's own prices in that response are ignored. Failures never fail the wishlist.
        /// </summary>
        private async Task AddStoreNamesAsync(List<WishlistEntry> entries, CancellationToken cancellationToken)
        {
            List<long> missing;
            lock (sync)
            {
                missing = entries.Where(e => e.SteamId.HasValue && !names.ContainsKey(e.SteamId.Value)).Select(e => e.SteamId.Value).Distinct().ToList();
            }

            for (var offset = 0; offset < missing.Count; offset += NameBatchSize)
            {
                var batch = missing.Skip(offset).Take(NameBatchSize).ToList();
                try
                {
                    using (var response = await http.GetAsync(BuildStoreItemsUri(batch), cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            Log.Warn("Steam store names unavailable (HTTP " + (int)response.StatusCode + ")");
                            break;
                        }

                        var parsed = ParseStoreNames(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                        if (parsed == null)
                        {
                            Log.Warn("Steam store names response was not understood");
                            break;
                        }

                        lock (sync)
                        {
                            foreach (var id in batch)
                            {
                                names[id] = parsed.TryGetValue(id, out var name) ? name : null;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Log.Warn("Steam store names request failed: " + e.GetType().Name);
                    break;
                }
            }

            lock (sync)
            {
                foreach (var entry in entries)
                {
                    if (entry.SteamId.HasValue && names.TryGetValue(entry.SteamId.Value, out var name) && !string.IsNullOrWhiteSpace(name))
                    {
                        entry.Title = name;
                    }
                }
            }
        }

        internal static string BuildStoreItemsUri(IEnumerable<long> appIds)
        {
            var ids = string.Join(",", appIds.Select(id => "{\"appid\":" + id.ToString(CultureInfo.InvariantCulture) + "}"));
            var input = "{\"ids\":[" + ids + "],\"context\":{\"language\":\"english\",\"country_code\":\"US\"}}";
            return StoreItemsEndpoint + "?input_json=" + Uri.EscapeDataString(input);
        }

        /// <summary>Returns appid → name (null when Steam doesn't know the app), or null for an unreadable body.</summary>
        internal static Dictionary<long, string> ParseStoreNames(string body)
        {
            IDictionary<string, object> response;
            try
            {
                response = Json.AsObject(Json.Get(Json.AsObject(Json.Parse(body)), "response"));
            }
            catch (Exception)
            {
                return null;
            }

            if (response == null)
            {
                return null;
            }

            var result = new Dictionary<long, string>();
            if (Json.Get(response, "store_items") is IEnumerable items)
            {
                foreach (var item in items.OfType<IDictionary<string, object>>())
                {
                    // "id" echoes the requested id even for unknown apps (whose "appid" is 0).
                    if (!long.TryParse(Json.GetText(item, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                    {
                        continue;
                    }

                    var name = Json.GetText(item, "name")?.Trim();
                    result[id] = Json.GetInt(item, "success") == 1 && !string.IsNullOrEmpty(name) ? name : null;
                }
            }

            return result;
        }

        /// <summary>Maps an HTTP response from IWishlistService/GetWishlist to a fetch result.</summary>
        internal static WishlistFetchResult Interpret(HttpStatusCode status, string body, DateTime nowUtc)
        {
            var code = (int)status;
            if (code == 429)
            {
                return Failure(ApiErrorKind.RateLimited, "Steam is limiting requests right now. The wishlist will update on a later refresh.");
            }

            if (code >= 500)
            {
                return Failure(ApiErrorKind.Server, "Steam is currently unavailable. Showing the most recent cached wishlist.");
            }

            if (code < 200 || code > 299)
            {
                return Failure(ApiErrorKind.UnexpectedResponse, "Steam returned an unexpected response (HTTP " + code + ").");
            }

            IDictionary<string, object> response;
            try
            {
                response = Json.AsObject(Json.Get(Json.AsObject(Json.Parse(body)), "response"));
            }
            catch (Exception)
            {
                response = null;
            }

            if (response == null)
            {
                return Failure(ApiErrorKind.UnexpectedResponse, "Steam returned an unexpected response.");
            }

            var raw = new List<RawItem>();
            if (Json.Get(response, "items") is IEnumerable items)
            {
                foreach (var item in items.OfType<IDictionary<string, object>>())
                {
                    if (!long.TryParse(Json.GetText(item, "appid"), NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
                    {
                        continue;
                    }

                    DateTime? added = null;
                    if (long.TryParse(Json.GetText(item, "date_added"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                    {
                        added = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                    }

                    raw.Add(new RawItem { AppId = appId, Priority = Json.GetInt(item, "priority") ?? 0, Added = added });
                }
            }

            // Steam ranks with priority 1..n; unranked items (0) go last, newest first.
            var ordered = raw
                .GroupBy(r => r.AppId)
                .Select(g => g.First())
                .OrderBy(r => r.Priority <= 0 ? int.MaxValue : r.Priority)
                .ThenByDescending(r => r.Added ?? DateTime.MinValue)
                .ToList();

            var entries = ordered.Select((r, index) => new WishlistEntry
            {
                Key = WishlistEntry.BuildKey(null, SteamIdType.App, r.AppId, null),
                ProviderId = ProviderId,
                SteamId = r.AppId,
                SteamIdType = SteamIdType.App,
                WishlistPosition = index + 1,
                AddedUtc = r.Added
            }).ToList();

            return new WishlistFetchResult
            {
                State = WishlistProviderState.Available,
                Entries = entries,
                RetrievedUtc = nowUtc,
                Message = entries.Count == 0
                    ? "Your Steam wishlist is empty or private. To use it here, set \"Game details\" to Public in your Steam profile's privacy settings."
                    : null
            };
        }

        private static WishlistFetchResult Failure(ApiErrorKind kind, string message) =>
            new WishlistFetchResult { State = WishlistProviderState.Error, Error = kind, Message = message };

        public void Dispose() => http.Dispose();

        private sealed class RawItem
        {
            public long AppId { get; set; }

            public int Priority { get; set; }

            public DateTime? Added { get; set; }
        }
    }
}
