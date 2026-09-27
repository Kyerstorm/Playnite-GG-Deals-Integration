using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GGDealsWishlist.Api;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;
using Xunit;

namespace GGDealsWishlist.Tests
{
    public sealed class FakeClock : IClock
    {
        public FakeClock(DateTime start) => UtcNow = start;

        public DateTime UtcNow { get; set; }

        public void Advance(TimeSpan span) => UtcNow += span;
    }

    public sealed class CapturingSink : ILogSink
    {
        public List<string> Messages { get; } = new List<string>();

        public void Debug(string message) => Messages.Add(message);
        public void Info(string message) => Messages.Add(message);
        public void Warn(string message) => Messages.Add(message);
        public void Error(string message) => Messages.Add(message);
    }

    public sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = respond;

        public List<Uri> Requests { get; } = new List<Uri>();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>API client double returning GG.deals-shaped results and recording batches.</summary>
    public sealed class FakeApiClient : IGGDealsApiClient
    {
        public List<List<long>> Batches { get; } = new List<List<long>>();

        public Func<IReadOnlyCollection<long>, PriceApiResult> Responder { get; set; }

        public Task<PriceApiResult> GetPricesAsync(SteamIdType type, IReadOnlyCollection<long> ids, string region, string apiKey, CancellationToken cancellationToken)
        {
            Batches.Add(ids.ToList());
            if (Responder != null)
            {
                return Task.FromResult(Responder(ids));
            }

            var result = new PriceApiResult();
            foreach (var id in ids)
            {
                // Ids that are multiples of 10 (except the connection-test app) simulate games GG.deals doesn't know.
                result.Items[id] = id % 10 == 0 && id != GGDealsApiPriceProvider.ConnectionTestAppId
                    ? null
                    : new PriceData { LookupKey = "app:" + id, Found = true, GGDealsTitle = "Game " + id, CurrentRetail = 10m + id % 7, HistoricalRetail = 5m, Currency = "GBP", Region = region };
            }

            return Task.FromResult(result);
        }
    }

    public class ApiClientTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private const string DocExample = @"{
  ""success"": true,
  ""data"": {
    ""1"": null,
    ""420"": {
      ""title"": ""Half-Life 2: Episode Two"",
      ""url"": ""https://gg.deals/game/half-life-2-episode-two/"",
      ""prices"": {
        ""currentRetail"": ""91.99"",
        ""currentKeyshops"": ""24.30"",
        ""historicalRetail"": ""2.89"",
        ""historicalKeyshops"": ""5.61"",
        ""currency"": ""PLN""
      }
    }
  }
}";

        [Fact]
        public void Parses_documented_example_response()
        {
            var result = GGDealsApiClient.Interpret(200, DocExample, null, Now, new long[] { 1, 420 }, "pl", SteamIdType.App);

            Assert.True(result.IsSuccess);
            Assert.Null(result.Items[1]);
            var price = result.Items[420];
            Assert.Equal("Half-Life 2: Episode Two", price.GGDealsTitle);
            Assert.Equal("https://gg.deals/game/half-life-2-episode-two/", price.GGDealsUrl);
            Assert.Equal(91.99m, price.CurrentRetail);
            Assert.Equal(24.30m, price.CurrentKeyshops);
            Assert.Equal(2.89m, price.HistoricalRetail);
            Assert.Equal(5.61m, price.HistoricalKeyshops);
            Assert.Equal("PLN", price.Currency);
            Assert.Equal("pl", price.Region);
            Assert.Equal("app:420", price.LookupKey);
        }

        [Fact]
        public void Null_price_values_are_preserved_as_missing()
        {
            var body = @"{""success"":true,""data"":{""7"":{""title"":""Pack"",""url"":""u"",""prices"":{""currentRetail"":""35.99"",""currentKeyshops"":null,""historicalRetail"":null,""historicalKeyshops"":""6.48"",""currency"":""EUR""}}}}";
            var result = GGDealsApiClient.Interpret(200, body, null, Now, new long[] { 7 }, "eu", SteamIdType.Sub);

            var price = result.Items[7];
            Assert.Equal(35.99m, price.CurrentRetail);
            Assert.Null(price.CurrentKeyshops);
            Assert.Null(price.HistoricalRetail);
            Assert.Equal("sub:7", price.LookupKey);
        }

        [Fact]
        public void Malformed_item_does_not_break_the_batch()
        {
            var body = @"{""success"":true,""data"":{""5"":""garbage"",""6"":{""title"":""Ok"",""prices"":{""currentRetail"":""not-a-number"",""currency"":""USD""}}}}";
            var result = GGDealsApiClient.Interpret(200, body, null, Now, new long[] { 5, 6 }, "us", SteamIdType.App);

            Assert.True(result.IsSuccess);
            Assert.Null(result.Items[5]);
            Assert.Equal("Ok", result.Items[6].GGDealsTitle);
            Assert.Null(result.Items[6].CurrentRetail);
        }

        [Fact]
        public void Missing_requested_ids_are_reported_as_not_found()
        {
            var result = GGDealsApiClient.Interpret(200, @"{""success"":true,""data"":[]}", null, Now, new long[] { 99 }, "us", SteamIdType.App);

            Assert.True(result.IsSuccess);
            Assert.True(result.Items.ContainsKey(99));
            Assert.Null(result.Items[99]);
        }

        [Theory]
        [InlineData(401, "", ApiErrorKind.InvalidApiKey)]
        [InlineData(403, "", ApiErrorKind.InvalidApiKey)]
        [InlineData(400, @"{""success"":false,""data"":{""message"":""Invalid API key""}}", ApiErrorKind.InvalidApiKey)]
        [InlineData(400, @"{""success"":false,""data"":{""message"":""ids too long""}}", ApiErrorKind.UnexpectedResponse)]
        [InlineData(500, "", ApiErrorKind.Server)]
        [InlineData(503, "<html>", ApiErrorKind.Server)]
        [InlineData(200, "{not json", ApiErrorKind.UnexpectedResponse)]
        [InlineData(200, @"{""success"":false}", ApiErrorKind.UnexpectedResponse)]
        [InlineData(200, @"{""success"":true,""data"":42}", ApiErrorKind.UnexpectedResponse)]
        [InlineData(302, "", ApiErrorKind.UnexpectedResponse)]
        public void Maps_http_failures(int status, string body, ApiErrorKind expected)
        {
            var result = GGDealsApiClient.Interpret(status, body, null, Now, new long[] { 1 }, "us", SteamIdType.App);
            Assert.Equal(expected, result.Error);
            Assert.False(string.IsNullOrEmpty(result.Message));
        }

        [Fact]
        public void Rate_limit_uses_reset_header()
        {
            var headers = new RateLimitHeaders { Remaining = 0, ResetUtc = Now.AddSeconds(42) };
            var body = @"{""success"":false,""data"":{""name"":""Too Many Requests"",""message"":""You can fetch prices info only for 100 games per minute."",""code"":429,""status"":429}}";
            var result = GGDealsApiClient.Interpret(429, body, headers, Now, new long[] { 1 }, "us", SteamIdType.App);

            Assert.Equal(ApiErrorKind.RateLimited, result.Error);
            Assert.Equal(Now.AddSeconds(42), result.RetryAfterUtc);
            Assert.Contains("rate limit", result.Message);
        }

        [Fact]
        public void Rate_limit_without_header_backs_off_one_minute()
        {
            var result = GGDealsApiClient.Interpret(429, "", null, Now, new long[] { 1 }, "us", SteamIdType.App);
            Assert.Equal(Now.AddSeconds(60), result.RetryAfterUtc);
        }

        [Theory]
        [InlineData("30", 30)]
        [InlineData("1790000000", -1)]
        [InlineData("1790000000000", -2)]
        public void Parses_reset_header_variants(string text, int expectation)
        {
            var parsed = GGDealsApiClient.ParseReset(text, Now);
            Assert.True(parsed.HasValue);
            if (expectation > 0)
            {
                Assert.Equal(Now.AddSeconds(expectation), parsed.Value);
            }
            else
            {
                Assert.Equal(new DateTime(2026, 9, 21, 14, 13, 20, DateTimeKind.Utc), parsed.Value);
            }
        }

        [Fact]
        public void Builds_documented_endpoint_urls()
        {
            var uri = GGDealsApiClient.BuildUri(SteamIdType.Bundle, new long[] { 1, 232 }, "PL", "k e y");
            Assert.Equal("https://api.gg.deals/v1/prices/by-steam-bundle-id/?ids=1,232&key=k%20e%20y&region=pl", uri.AbsoluteUri);
            Assert.Contains("by-steam-app-id", GGDealsApiClient.BuildUri(SteamIdType.App, new long[] { 1 }, null, "k").AbsoluteUri);
            Assert.Contains("by-steam-sub-id", GGDealsApiClient.BuildUri(SteamIdType.Sub, new long[] { 1 }, null, "k").AbsoluteUri);
        }

        [Fact]
        public async Task End_to_end_request_reads_headers_and_never_logs_the_key()
        {
            var sink = new CapturingSink();
            Log.SetSink(sink);
            const string key = "super-secret-api-key-123";
            var handler = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };
                response.Headers.Add("x-ratelimit-remaining", "77");
                response.Headers.Add("x-ratelimit-limit", "100");
                return response;
            });

            using (var client = new GGDealsApiClient(handler, new FakeClock(Now)))
            {
                var result = await client.GetPricesAsync(SteamIdType.App, new long[] { 420 }, "gb", key, CancellationToken.None);
                Assert.Equal(ApiErrorKind.Server, result.Error);
                Assert.Equal(77, result.RateLimit.Remaining);
                Assert.Equal(100, result.RateLimit.Limit);
            }

            Assert.Contains("key=super-secret-api-key-123", handler.Requests.Single().Query);
            Assert.DoesNotContain(sink.Messages, m => m.Contains(key));
            Log.SetSink(null);
        }

        [Fact]
        public async Task Network_failure_is_reported_without_exception()
        {
            var handler = new StubHandler(_ => throw new HttpRequestException("DNS failure for https://api.gg.deals/?key=abc"));
            using (var client = new GGDealsApiClient(handler))
            {
                var result = await client.GetPricesAsync(SteamIdType.App, new long[] { 1 }, "us", "abcd", CancellationToken.None);
                Assert.Equal(ApiErrorKind.Network, result.Error);
                Assert.Equal("⚠ Unable to connect to GG.deals.", result.Message);
            }
        }

        [Fact]
        public async Task Missing_key_is_not_configured_and_sends_nothing()
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using (var client = new GGDealsApiClient(handler))
            {
                var result = await client.GetPricesAsync(SteamIdType.App, new long[] { 1 }, "us", " ", CancellationToken.None);
                Assert.Equal(ApiErrorKind.NotConfigured, result.Error);
                Assert.Empty(handler.Requests);
            }
        }

        [Fact]
        public void Redactor_removes_registered_keys_and_key_query_values()
        {
            SecretRedactor.Register("MyKey1234");
            Assert.Equal("value [REDACTED] here", SecretRedactor.Redact("value MyKey1234 here"));
            Assert.Equal("https://x/?ids=1&key=[REDACTED]&region=gb", SecretRedactor.Redact("https://x/?ids=1&key=other&region=gb"));
        }
    }

    public class RateLimitTrackerTests
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Enforces_per_minute_and_per_hour_windows()
        {
            var clock = new FakeClock(Start);
            var tracker = new RateLimitTracker(clock);
            Assert.Equal(100, tracker.GetBudget().Available);

            tracker.RecordUsage(100);
            var budget = tracker.GetBudget();
            Assert.Equal(0, budget.Available);
            Assert.Equal(Start.AddMinutes(1), budget.NextAvailableUtc);

            for (var i = 0; i < 9; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(61));
                Assert.Equal(100, tracker.GetBudget().Available);
                tracker.RecordUsage(100);
            }

            clock.Advance(TimeSpan.FromSeconds(61));
            budget = tracker.GetBudget();
            Assert.Equal(0, budget.HourRemaining);
            Assert.Equal(0, budget.Available);
            Assert.Equal(Start.AddHours(1), budget.NextAvailableUtc);
        }

        [Fact]
        public void Server_remaining_caps_the_budget_until_reset()
        {
            var clock = new FakeClock(Start);
            var tracker = new RateLimitTracker(clock);
            tracker.ApplyServerHeaders(new RateLimitHeaders { Remaining = 12, ResetUtc = Start.AddSeconds(30) });
            Assert.Equal(12, tracker.GetBudget().Available);

            clock.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(100, tracker.GetBudget().Available);
        }

        [Fact]
        public void Blocked_after_429_until_reset()
        {
            var clock = new FakeClock(Start);
            var tracker = new RateLimitTracker(clock);
            tracker.MarkRateLimited(Start.AddMinutes(5));
            var budget = tracker.GetBudget();
            Assert.True(budget.IsBlocked);
            Assert.Equal(0, budget.Available);
            Assert.Equal(Start.AddMinutes(5), budget.NextAvailableUtc);

            clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
            Assert.False(tracker.GetBudget().IsBlocked);
        }

        [Fact]
        public void State_survives_export_and_import()
        {
            var clock = new FakeClock(Start);
            var tracker = new RateLimitTracker(clock);
            tracker.RecordUsage(60);
            var restored = new RateLimitTracker(clock, Json.Deserialize<RateLimitState>(Json.Serialize(tracker.Export())));
            Assert.Equal(40, restored.GetBudget().Available);
        }
    }

    public class PriceProviderTests
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private static List<PriceLookupKey> AppKeys(int count) =>
            Enumerable.Range(1, count).Select(i => new PriceLookupKey(SteamIdType.App, i)).ToList();

        [Fact]
        public async Task Batches_350_games_into_100_100_100_50_within_rate_limits()
        {
            var clock = new FakeClock(Start);
            var api = new FakeApiClient();
            var provider = new GGDealsApiPriceProvider(api, new RateLimitTracker(clock), () => "key", clock,
                (span, token) => { clock.Advance(span); return Task.CompletedTask; });

            var progressReports = new List<PriceBatchProgress>();
            var result = await provider.GetPricesAsync(AppKeys(350), "gb", true, new InlineProgress<PriceBatchProgress>(progressReports.Add), CancellationToken.None);

            Assert.Equal(ApiErrorKind.None, result.Error);
            Assert.Equal(new[] { 100, 100, 100, 50 }, api.Batches.Select(b => b.Count));
            Assert.Equal(350, result.Results.Count);
            Assert.Equal(350, result.Completed);
            Assert.False(result.Results["app:10"].Found);
            Assert.True(result.Results["app:11"].Found);
            Assert.Contains(progressReports, p => p.WaitingUntilUtc.HasValue);
            Assert.True(clock.UtcNow >= Start.AddMinutes(3), "Must wait for the minute window between batches.");
        }

        [Fact]
        public async Task Deduplicates_ids_and_uses_budget_sized_batches()
        {
            var clock = new FakeClock(Start);
            var tracker = new RateLimitTracker(clock);
            tracker.RecordUsage(70);
            var api = new FakeApiClient();
            var provider = new GGDealsApiPriceProvider(api, tracker, () => "key", clock, (s, t) => { clock.Advance(s); return Task.CompletedTask; });

            var keys = AppKeys(50).Concat(AppKeys(50)).ToList();
            var result = await provider.GetPricesAsync(keys, "us", true, null, CancellationToken.None);

            Assert.Equal(50, result.Requested);
            Assert.Equal(30, api.Batches[0].Count);
            Assert.Equal(20, api.Batches[1].Count);
        }

        [Fact]
        public async Task Stops_with_partial_results_when_hourly_budget_is_exhausted()
        {
            var clock = new FakeClock(Start);
            var tracker = new RateLimitTracker(clock);
            for (var i = 0; i < 9; i++)
            {
                tracker.RecordUsage(100);
                clock.Advance(TimeSpan.FromSeconds(61));
            }

            var api = new FakeApiClient();
            var provider = new GGDealsApiPriceProvider(api, tracker, () => "key", clock, (s, t) => { clock.Advance(s); return Task.CompletedTask; });
            var result = await provider.GetPricesAsync(AppKeys(250), "us", true, null, CancellationToken.None);

            Assert.Equal(ApiErrorKind.RateLimited, result.Error);
            Assert.True(result.IsPartial);
            Assert.Equal(100, result.Completed);
            Assert.NotNull(result.RetryAfterUtc);
        }

        [Fact]
        public async Task Does_not_retry_repeatedly_after_429()
        {
            var clock = new FakeClock(Start);
            var api = new FakeApiClient { Responder = ids => new PriceApiResult { Error = ApiErrorKind.RateLimited, RetryAfterUtc = clock.UtcNow.AddMinutes(10) } };
            var provider = new GGDealsApiPriceProvider(api, new RateLimitTracker(clock), () => "key", clock, (s, t) => { clock.Advance(s); return Task.CompletedTask; });

            var result = await provider.GetPricesAsync(AppKeys(5), "us", true, null, CancellationToken.None);

            Assert.Equal(ApiErrorKind.RateLimited, result.Error);
            Assert.Single(api.Batches);
            Assert.Equal(Start.AddMinutes(10), result.RetryAfterUtc);
            Assert.True(provider.GetRateLimitStatus().IsBlocked);
        }

        [Fact]
        public async Task Invalid_key_stops_immediately()
        {
            var clock = new FakeClock(Start);
            var api = new FakeApiClient { Responder = ids => new PriceApiResult { Error = ApiErrorKind.InvalidApiKey, Message = "✕ API key is invalid." } };
            var provider = new GGDealsApiPriceProvider(api, new RateLimitTracker(clock), () => "bad", clock);

            var result = await provider.GetPricesAsync(AppKeys(250), "us", true, null, CancellationToken.None);

            Assert.Equal(ApiErrorKind.InvalidApiKey, result.Error);
            Assert.Single(api.Batches);
            Assert.Equal(0, result.Completed);
        }

        [Fact]
        public async Task Missing_key_makes_no_requests()
        {
            var api = new FakeApiClient();
            var provider = new GGDealsApiPriceProvider(api, new RateLimitTracker(), () => null);
            var result = await provider.GetPricesAsync(AppKeys(3), "us", true, null, CancellationToken.None);
            Assert.Equal(ApiErrorKind.NotConfigured, result.Error);
            Assert.Empty(api.Batches);
        }

        [Fact]
        public async Task Groups_batches_by_endpoint_type()
        {
            var clock = new FakeClock(Start);
            var api = new FakeApiClient();
            var provider = new GGDealsApiPriceProvider(api, new RateLimitTracker(clock), () => "key", clock);
            var keys = new List<PriceLookupKey>
            {
                new PriceLookupKey(SteamIdType.App, 1),
                new PriceLookupKey(SteamIdType.App, 2),
                new PriceLookupKey(SteamIdType.Sub, 3),
                new PriceLookupKey(SteamIdType.App, 4)
            };

            await provider.GetPricesAsync(keys, "us", false, null, CancellationToken.None);
            Assert.Equal(3, api.Batches.Count);
        }

        [Fact]
        public async Task Connection_test_costs_one_record()
        {
            var clock = new FakeClock(Start);
            var tracker = new RateLimitTracker(clock);
            var api = new FakeApiClient();
            var provider = new GGDealsApiPriceProvider(api, tracker, () => "key", clock);

            var result = await provider.TestConnectionAsync("key", "gb", CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal("GBP", result.Currency);
            Assert.Equal(1, tracker.GetBudget().MinuteUsed);
            Assert.Equal(new long[] { GGDealsApiPriceProvider.ConnectionTestAppId }, api.Batches.Single());
        }

        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> action;

            public InlineProgress(Action<T> action) => this.action = action;

            public void Report(T value) => action(value);
        }
    }

    public class ManualWishlistTests
    {
        [Fact]
        public void Parses_ids_urls_prefixes_and_titles()
        {
            var text = "# comment\n620\nhttps://store.steampowered.com/app/1145360/Hades/\nsub:469\nbundle/232 | Valve Complete Pack\n620\nnonsense\n1091500 | Cyberpunk 2077";
            var entries = ManualWishlistProvider.Parse(text, out var invalid);

            Assert.Equal(5, entries.Count);
            Assert.Equal("steam:app:620", entries[0].Key);
            Assert.Equal(1145360, entries[1].SteamId);
            Assert.Equal(SteamIdType.Sub, entries[2].SteamIdType);
            Assert.Equal(SteamIdType.Bundle, entries[3].SteamIdType);
            Assert.Equal("Valve Complete Pack", entries[3].Title);
            Assert.Equal("Cyberpunk 2077", entries[4].Title);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, entries.Select(e => e.WishlistPosition.Value));
            Assert.Equal(new[] { "nonsense" }, invalid);
        }

        [Fact]
        public void Registry_prefers_available_provider_and_falls_back_to_unavailable()
        {
            var enabled = false;
            var manual = new ManualWishlistProvider(() => enabled, () => "620");
            var registry = new WishlistProviderRegistry(new IGGDealsWishlistProvider[] { manual });

            Assert.IsType<UnavailableWishlistProvider>(registry.Resolve());
            enabled = true;
            Assert.Same(manual, registry.Resolve());
        }
    }

    public class PriceLogicTests
    {
        private static PriceData Price(decimal? retail, decimal? keyshop, decimal? histRetail, decimal? histKeyshop) =>
            new PriceData { Found = true, CurrentRetail = retail, CurrentKeyshops = keyshop, HistoricalRetail = histRetail, HistoricalKeyshops = histKeyshop, Currency = "GBP" };

        [Fact]
        public void Retail_preference_never_uses_keyshop_price()
        {
            var selection = PriceLogic.Select(Price(null, 5m, null, 4m), PricePreference.Retail);
            Assert.Null(selection.Current);
            Assert.Null(selection.Category);
        }

        [Fact]
        public void Lowest_of_both_identifies_the_source()
        {
            var selection = PriceLogic.Select(Price(9.99m, 7.49m, 7m, 7.49m), PricePreference.LowestOfBoth);
            Assert.Equal(7.49m, selection.Current);
            Assert.Equal(PriceCategory.Keyshop, selection.Category);
            Assert.True(selection.IsHistoricalLow);
        }

        [Fact]
        public void Historical_low_requires_both_values_of_the_same_category()
        {
            Assert.True(PriceLogic.Select(Price(7.49m, null, 7.49m, null), PricePreference.Retail).IsHistoricalLow);
            Assert.False(PriceLogic.Select(Price(7.49m, null, null, 7.49m), PricePreference.Retail).IsHistoricalLow);
            Assert.False(PriceLogic.Select(Price(9.99m, null, 7.49m, null), PricePreference.Retail).IsHistoricalLow);
            Assert.False(PriceLogic.Select(new PriceData { Found = false }, PricePreference.Retail).IsHistoricalLow);
        }

        [Theory]
        [InlineData(9.99, "GBP", "£9.99")]
        [InlineData(91.99, "PLN", "91.99 zł")]
        [InlineData(5, "USD", "$5.00")]
        [InlineData(12.5, "XYZ", "12.50 XYZ")]
        public void Formats_using_returned_currency(double amount, string currency, string expected)
        {
            Assert.Equal(expected, CurrencyFormatter.Format((decimal)amount, currency));
        }

        [Fact]
        public void Region_list_matches_documentation()
        {
            var documented = "au, be, br, ca, ch, de, dk, es, eu, fi, fr, gb, ie, it, nl, no, pl, se, us".Split(new[] { ", " }, StringSplitOptions.None);
            Assert.Equal(documented.OrderBy(c => c), Regions.All.Select(r => r.Code).OrderBy(c => c));
        }
    }
}
