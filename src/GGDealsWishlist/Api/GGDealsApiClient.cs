using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Models;

namespace GGDealsWishlist.Api
{
    /// <summary>Values from the documented x-ratelimit-* response headers.</summary>
    public sealed class RateLimitHeaders
    {
        public int? Limit { get; set; }

        public int? Remaining { get; set; }

        public DateTime? ResetUtc { get; set; }

        public bool HasValues => Limit.HasValue || Remaining.HasValue || ResetUtc.HasValue;
    }

    public sealed class PriceApiResult
    {
        public ApiErrorKind Error { get; set; }

        public bool IsSuccess => Error == ApiErrorKind.None;

        /// <summary>User-safe text; never contains the API key or raw exception details.</summary>
        public string Message { get; set; }

        public int? HttpStatus { get; set; }

        public RateLimitHeaders RateLimit { get; set; }

        public DateTime? RetryAfterUtc { get; set; }

        /// <summary>Requested id → price data, or null when GG.deals does not know the id.</summary>
        public Dictionary<long, PriceData> Items { get; } = new Dictionary<long, PriceData>();
    }

    public interface IGGDealsApiClient
    {
        Task<PriceApiResult> GetPricesAsync(SteamIdType type, IReadOnlyCollection<long> ids, string region, string apiKey, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Client for the official GG.deals Prices API (https://gg.deals/api/prices/).
    /// The key is a query parameter, so request URIs are never logged and all diagnostics pass through
    /// <see cref="SecretRedactor"/>.
    /// </summary>
    public sealed class GGDealsApiClient : IGGDealsApiClient, IDisposable
    {
        public const int MaxIdsPerRequest = 100;
        public const string ApiHost = "https://api.gg.deals/v1/prices/";

        private readonly HttpClient http;
        private readonly IClock clock;

        public GGDealsApiClient(HttpMessageHandler handler = null, IClock clock = null)
        {
            this.clock = clock ?? SystemClock.Instance;
            http = handler == null ? new HttpClient() : new HttpClient(handler, false);
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Playnite-GGDealsWishlist", typeof(GGDealsApiClient).Assembly.GetName().Version.ToString(3)));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public static string EndpointFor(SteamIdType type)
        {
            switch (type)
            {
                case SteamIdType.Sub:
                    return "by-steam-sub-id/";
                case SteamIdType.Bundle:
                    return "by-steam-bundle-id/";
                default:
                    return "by-steam-app-id/";
            }
        }

        internal static Uri BuildUri(SteamIdType type, IEnumerable<long> ids, string region, string apiKey)
        {
            var query = "ids=" + string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)))
                + "&key=" + Uri.EscapeDataString(apiKey);
            if (!string.IsNullOrWhiteSpace(region))
            {
                query += "&region=" + Uri.EscapeDataString(region.Trim().ToLowerInvariant());
            }

            return new Uri(ApiHost + EndpointFor(type) + "?" + query);
        }

        public async Task<PriceApiResult> GetPricesAsync(SteamIdType type, IReadOnlyCollection<long> ids, string region, string apiKey, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Failure(ApiErrorKind.NotConfigured, null, null);
            }

            if (ids == null || ids.Count == 0)
            {
                return new PriceApiResult();
            }

            if (ids.Count > MaxIdsPerRequest)
            {
                throw new ArgumentException("GG.deals accepts at most " + MaxIdsPerRequest + " ids per request.", nameof(ids));
            }

            SecretRedactor.Register(apiKey);
            var uri = BuildUri(type, ids, region, apiKey);
            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(ApiErrorKind.Cancelled, null, null);
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is WebException || e is InvalidOperationException)
            {
                // TaskCanceledException without our token being cancelled means the request timed out.
                Log.Warn(e, "GG.deals request failed (" + type + ", " + ids.Count + " ids)");
                return Failure(ApiErrorKind.Network, null, null);
            }

            using (response)
            {
                var headers = ReadRateLimitHeaders(response, clock.UtcNow);
                string body;
                try
                {
                    body = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Log.Warn(e, "Failed to read GG.deals response body");
                    return Failure(ApiErrorKind.Network, (int)response.StatusCode, headers);
                }

                var result = Interpret((int)response.StatusCode, body, headers, clock.UtcNow, ids, region, type);
                if (!result.IsSuccess)
                {
                    Log.Warn("GG.deals request returned " + result.Error + " (HTTP " + result.HttpStatus + ")");
                }

                return result;
            }
        }

        /// <summary>Maps an HTTP status + body to a typed result. Pure, for unit testing.</summary>
        internal static PriceApiResult Interpret(int status, string body, RateLimitHeaders headers, DateTime nowUtc, IEnumerable<long> requestedIds, string region, SteamIdType type)
        {
            IDictionary<string, object> root = null;
            try
            {
                root = Json.AsObject(string.IsNullOrWhiteSpace(body) ? null : Json.Parse(body));
            }
            catch (Exception)
            {
                root = null;
            }

            var errorData = Json.AsObject(Json.Get(root, "data"));
            var serverMessage = Json.GetText(errorData, "message") ?? string.Empty;

            if (status == 429)
            {
                var result = Failure(ApiErrorKind.RateLimited, status, headers);
                result.RetryAfterUtc = headers?.ResetUtc > nowUtc ? headers.ResetUtc : nowUtc.AddSeconds(60);
                result.Message = ApiErrorMessages.For(ApiErrorKind.RateLimited, result.RetryAfterUtc);
                return result;
            }

            if (status == 401 || status == 403)
            {
                return Failure(ApiErrorKind.InvalidApiKey, status, headers);
            }

            if (status == 400 || status == 422)
            {
                var looksLikeKey = serverMessage.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0
                    || serverMessage.IndexOf("credential", StringComparison.OrdinalIgnoreCase) >= 0;
                return Failure(looksLikeKey ? ApiErrorKind.InvalidApiKey : ApiErrorKind.UnexpectedResponse, status, headers);
            }

            if (status >= 500)
            {
                return Failure(ApiErrorKind.Server, status, headers);
            }

            if (status < 200 || status >= 300)
            {
                return Failure(ApiErrorKind.UnexpectedResponse, status, headers);
            }

            if (root == null || Json.GetBool(root, "success") != true)
            {
                return Failure(ApiErrorKind.UnexpectedResponse, status, headers);
            }

            var success = new PriceApiResult { HttpStatus = status, RateLimit = headers };
            var data = Json.Get(root, "data");
            if (data is object[] emptyArray && emptyArray.Length == 0)
            {
                data = new Dictionary<string, object>();
            }

            if (!(data is IDictionary<string, object> items))
            {
                return Failure(ApiErrorKind.UnexpectedResponse, status, headers);
            }

            var regionCode = string.IsNullOrWhiteSpace(region) ? "us" : region.Trim().ToLowerInvariant();
            foreach (var pair in items)
            {
                if (!long.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    continue;
                }

                try
                {
                    success.Items[id] = MapGamePrices(new PriceLookupKey(type, id), pair.Value, regionCode, nowUtc);
                }
                catch (Exception e)
                {
                    // One malformed record must not break the rest of the batch.
                    Log.Warn(e, "Skipping malformed GG.deals record " + id);
                }
            }

            foreach (var id in requestedIds)
            {
                if (!success.Items.ContainsKey(id))
                {
                    success.Items[id] = null;
                }
            }

            return success;
        }

        private static PriceData MapGamePrices(PriceLookupKey key, object value, string region, DateTime nowUtc)
        {
            if (value == null)
            {
                return null;
            }

            var obj = Json.AsObject(value) ?? throw new FormatException("Game record is not an object.");
            var prices = Json.AsObject(Json.Get(obj, "prices"));
            return new PriceData
            {
                LookupKey = key.ToString(),
                Found = true,
                GGDealsTitle = Json.GetText(obj, "title"),
                GGDealsUrl = Json.GetText(obj, "url"),
                CurrentRetail = ParsePrice(Json.GetText(prices, "currentRetail")),
                CurrentKeyshops = ParsePrice(Json.GetText(prices, "currentKeyshops")),
                HistoricalRetail = ParsePrice(Json.GetText(prices, "historicalRetail")),
                HistoricalKeyshops = ParsePrice(Json.GetText(prices, "historicalKeyshops")),
                Currency = Json.GetText(prices, "currency"),
                Region = region,
                FetchedUtc = nowUtc
            };
        }

        internal static decimal? ParsePrice(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : (decimal?)null;
        }

        internal static RateLimitHeaders ReadRateLimitHeaders(HttpResponseMessage response, DateTime nowUtc)
        {
            string First(string name)
            {
                return response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
            }

            return ParseRateLimitHeaders(First("x-ratelimit-limit") ?? First("x-rate-limit-limit"),
                First("x-ratelimit-remaining") ?? First("x-rate-limit-remaining"),
                First("x-ratelimit-reset") ?? First("x-rate-limit-reset"),
                nowUtc);
        }

        internal static RateLimitHeaders ParseRateLimitHeaders(string limit, string remaining, string reset, DateTime nowUtc)
        {
            var headers = new RateLimitHeaders();
            if (int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                headers.Limit = l;
            }

            if (int.TryParse(remaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
            {
                headers.Remaining = Math.Max(0, r);
            }

            headers.ResetUtc = ParseReset(reset, nowUtc);
            return headers;
        }

        /// <summary>
        /// The docs describe x-ratelimit-reset as a timestamp. Some frameworks send seconds-until-reset instead,
        /// so small numbers are treated as a relative delay and large ones as a Unix time (seconds or ms).
        /// </summary>
        internal static DateTime? ParseReset(string text, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                if (number > 1e12)
                {
                    return epoch.AddMilliseconds(number);
                }

                if (number > 1e9)
                {
                    return epoch.AddSeconds(number);
                }

                return number >= 0 ? nowUtc.AddSeconds(number) : (DateTime?)null;
            }

            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : (DateTime?)null;
        }

        private static PriceApiResult Failure(ApiErrorKind kind, int? status, RateLimitHeaders headers)
        {
            return new PriceApiResult
            {
                Error = kind,
                HttpStatus = status,
                RateLimit = headers,
                Message = ApiErrorMessages.For(kind, null)
            };
        }

        public void Dispose() => http.Dispose();
    }

    /// <summary>User-facing texts for API failures. Deliberately free of technical detail.</summary>
    public static class ApiErrorMessages
    {
        public static string For(ApiErrorKind kind, DateTime? retryAfterUtc)
        {
            switch (kind)
            {
                case ApiErrorKind.None:
                    return null;
                case ApiErrorKind.NotConfigured:
                    return "Add your GG.deals API key in the extension settings.";
                case ApiErrorKind.InvalidApiKey:
                    return "✕ API key is invalid.";
                case ApiErrorKind.RateLimited:
                    return retryAfterUtc.HasValue
                        ? "⚠ GG.deals API rate limit reached. Please try again after " + retryAfterUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) + "."
                        : "⚠ GG.deals API rate limit reached.";
                case ApiErrorKind.Network:
                    return "⚠ Unable to connect to GG.deals.";
                case ApiErrorKind.Server:
                    return "⚠ GG.deals is currently unavailable.";
                case ApiErrorKind.Cancelled:
                    return "Refresh cancelled.";
                default:
                    return "⚠ GG.deals returned an unexpected response.";
            }
        }
    }
}
