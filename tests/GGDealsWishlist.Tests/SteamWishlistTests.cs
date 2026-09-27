using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GGDealsWishlist.Api;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;
using GGDealsWishlist.Services;
using Xunit;

namespace GGDealsWishlist.Tests
{
    public class SteamIdParserTests
    {
        [Theory]
        [InlineData("76561197960287930", 76561197960287930UL)]
        [InlineData("  76561197960287930  ", 76561197960287930UL)]
        [InlineData("https://steamcommunity.com/profiles/76561197960287930/", 76561197960287930UL)]
        [InlineData("steamcommunity.com/profiles/76561197960287930", 76561197960287930UL)]
        public void Accepts_ids_and_profile_links(string input, ulong expected)
        {
            Assert.True(SteamIdParser.TryParse(input, out var id));
            Assert.Equal(expected, id);
            Assert.Null(SteamIdParser.Describe(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("12345")]
        [InlineData("76561197960265728")] // the base value itself is not an individual account
        [InlineData("96561197960287930")]
        [InlineData("https://steamcommunity.com/id/gabelogannewell")]
        public void Rejects_invalid_input(string input)
        {
            Assert.False(SteamIdParser.TryParse(input, out _));
        }

        [Fact]
        public void Explains_custom_profile_links()
        {
            var message = SteamIdParser.Describe("https://steamcommunity.com/id/someone/");
            Assert.Contains("SteamID64", message);
            Assert.Contains("custom profile links", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    public class SteamWishlistProviderTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        private const string SteamId = "76561197960287930";

        private const string Body = @"{""response"":{""items"":[
            {""appid"":620,""priority"":2,""date_added"":1700000000},
            {""appid"":1145360,""priority"":1,""date_added"":1710000000},
            {""appid"":413150,""priority"":0,""date_added"":1690000000},
            {""appid"":367520,""priority"":0,""date_added"":1720000000},
            {""appid"":620,""priority"":5,""date_added"":1700000000},
            {""appid"":""oops""}
        ]}}";

        [Fact]
        public void Interpret_orders_by_priority_then_unranked_newest_first()
        {
            var result = SteamWishlistProvider.Interpret(HttpStatusCode.OK, Body, Now);

            Assert.Equal(WishlistProviderState.Available, result.State);
            Assert.Equal(new long?[] { 1145360, 620, 367520, 413150 }, result.Entries.Select(e => e.SteamId).ToArray());
            Assert.Equal(new int?[] { 1, 2, 3, 4 }, result.Entries.Select(e => e.WishlistPosition).ToArray());
            Assert.All(result.Entries, e =>
            {
                Assert.Equal(SteamIdType.App, e.SteamIdType);
                Assert.Equal(SteamWishlistProvider.ProviderId, e.ProviderId);
                Assert.Equal("steam:app:" + e.SteamId, e.Key);
                Assert.Null(e.Title);
            });
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1710000000).UtcDateTime, result.Entries[0].AddedUtc);
            Assert.Null(result.Message);
        }

        [Fact]
        public void Interpret_treats_empty_response_as_empty_or_private()
        {
            var result = SteamWishlistProvider.Interpret(HttpStatusCode.OK, @"{""response"":{}}", Now);

            Assert.Equal(WishlistProviderState.Available, result.State);
            Assert.Empty(result.Entries);
            Assert.Contains("private", result.Message);
        }

        [Theory]
        [InlineData(429, ApiErrorKind.RateLimited)]
        [InlineData(500, ApiErrorKind.Server)]
        [InlineData(503, ApiErrorKind.Server)]
        [InlineData(403, ApiErrorKind.UnexpectedResponse)]
        public void Interpret_maps_http_failures(int status, ApiErrorKind kind)
        {
            var result = SteamWishlistProvider.Interpret((HttpStatusCode)status, "", Now);

            Assert.Equal(WishlistProviderState.Error, result.State);
            Assert.Equal(kind, result.Error);
            Assert.Contains("Steam", result.Message);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData(@"{""other"":1}")]
        public void Interpret_rejects_malformed_bodies(string body)
        {
            var result = SteamWishlistProvider.Interpret(HttpStatusCode.OK, body, Now);

            Assert.Equal(WishlistProviderState.Error, result.State);
            Assert.Equal(ApiErrorKind.UnexpectedResponse, result.Error);
        }

        [Fact]
        public void State_follows_settings()
        {
            var enabled = false;
            var id = "";
            var provider = new SteamWishlistProvider(() => enabled, () => id, new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

            Assert.Equal(WishlistProviderState.Unavailable, provider.GetState());
            enabled = true;
            Assert.Equal(WishlistProviderState.NotConfigured, provider.GetState());
            id = "https://steamcommunity.com/id/custom";
            Assert.Equal(WishlistProviderState.NotConfigured, provider.GetState());
            id = SteamId;
            Assert.Equal(WishlistProviderState.Available, provider.GetState());
        }

        // Steam store names for the ids in Body; 413150 is reported unknown (success 15), 367520 is omitted.
        private const string NamesBody = @"{""response"":{""store_items"":[
            {""item_type"":0,""id"":620,""success"":1,""name"":""Portal 2"",""appid"":620},
            {""item_type"":0,""id"":1145360,""success"":1,""name"":""Hades"",""appid"":1145360,""best_purchase_option"":{""final_price_in_cents"":""624""}},
            {""item_type"":0,""id"":413150,""success"":15,""name"":"""",""appid"":0}
        ]}}";

        private static bool IsNamesRequest(HttpRequestMessage request) => request.RequestUri.AbsolutePath.Contains("IStoreBrowseService");

        private static StubHandler SteamStub(Func<bool> wishlistDown = null, Func<bool> namesDown = null) => new StubHandler(request =>
        {
            if (IsNamesRequest(request))
            {
                return namesDown?.Invoke() == true ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(NamesBody);
            }

            return wishlistDown?.Invoke() == true ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(Body);
        });

        [Fact]
        public async Task Fetch_calls_official_endpoints_and_caches_briefly()
        {
            var clock = new FakeClock(Now);
            var handler = SteamStub();
            var provider = new SteamWishlistProvider(() => true, () => SteamId, handler, clock);
            int WishlistCalls() => handler.Requests.Count(u => u.AbsolutePath.Contains("IWishlistService"));
            int NameCalls() => handler.Requests.Count(u => u.AbsolutePath.Contains("IStoreBrowseService"));

            var first = await provider.GetWishlistAsync(CancellationToken.None);
            await provider.GetWishlistAsync(CancellationToken.None);
            Assert.Equal(1, WishlistCalls());
            Assert.Equal(1, NameCalls());
            Assert.Equal(SteamWishlistProvider.Endpoint + "?steamid=" + SteamId, handler.Requests[0].ToString());
            Assert.Equal(4, first.Entries.Count);

            // Names are remembered per app (including "unknown" and missing answers), so refreshes don't re-ask.
            await provider.RefreshAsync(CancellationToken.None);
            Assert.Equal(2, WishlistCalls());
            Assert.Equal(1, NameCalls());

            clock.Advance(TimeSpan.FromMinutes(6));
            await provider.GetWishlistAsync(CancellationToken.None);
            Assert.Equal(3, WishlistCalls());
            Assert.Equal(1, NameCalls());
        }

        [Fact]
        public async Task Fetch_names_games_from_the_steam_store()
        {
            var provider = new SteamWishlistProvider(() => true, () => SteamId, SteamStub());

            var result = await provider.RefreshAsync(CancellationToken.None);

            var titles = result.Entries.ToDictionary(e => e.SteamId.Value, e => e.Title);
            Assert.Equal("Hades", titles[1145360]);
            Assert.Equal("Portal 2", titles[620]);
            Assert.Null(titles[413150]); // unknown to Steam: falls back to the GG.deals title later
            Assert.Null(titles[367520]); // not in Steam's answer
        }

        [Fact]
        public async Task Name_lookup_failure_still_returns_the_wishlist()
        {
            var provider = new SteamWishlistProvider(() => true, () => SteamId, SteamStub(namesDown: () => true));

            var result = await provider.RefreshAsync(CancellationToken.None);

            Assert.Equal(WishlistProviderState.Available, result.State);
            Assert.Equal(4, result.Entries.Count);
            Assert.All(result.Entries, e => Assert.Null(e.Title));
        }

        [Fact]
        public async Task Name_lookup_is_retried_after_a_failure()
        {
            var namesDown = true;
            var handler = SteamStub(namesDown: () => namesDown);
            var provider = new SteamWishlistProvider(() => true, () => SteamId, handler);

            await provider.RefreshAsync(CancellationToken.None);
            namesDown = false;
            var result = await provider.RefreshAsync(CancellationToken.None);

            Assert.Equal("Hades", result.Entries.Single(e => e.SteamId == 1145360).Title);
        }

        [Fact]
        public async Task Name_lookups_are_batched_by_100()
        {
            var items = string.Join(",", Enumerable.Range(1, 250).Select(i => "{\"appid\":" + i + ",\"priority\":" + i + "}"));
            var handler = new StubHandler(request => IsNamesRequest(request)
                ? Json(@"{""response"":{""store_items"":[]}}")
                : Json(@"{""response"":{""items"":[" + items + "]}}"));
            var provider = new SteamWishlistProvider(() => true, () => SteamId, handler);

            await provider.RefreshAsync(CancellationToken.None);

            Assert.Equal(3, handler.Requests.Count(u => u.AbsolutePath.Contains("IStoreBrowseService")));
        }

        [Fact]
        public void Store_items_uri_is_escaped_json()
        {
            var uri = new Uri(SteamWishlistProvider.BuildStoreItemsUri(new long[] { 620, 1145360 }));
            var input = Uri.UnescapeDataString(uri.Query.Substring("?input_json=".Length));

            Assert.StartsWith(SteamWishlistProvider.StoreItemsEndpoint, uri.ToString());
            Assert.Equal(@"{""ids"":[{""appid"":620},{""appid"":1145360}],""context"":{""language"":""english"",""country_code"":""US""}}", input);
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData(@"{""nope"":1}")]
        public void ParseStoreNames_rejects_unreadable_bodies(string body)
        {
            Assert.Null(SteamWishlistProvider.ParseStoreNames(body));
        }

        [Fact]
        public async Task Fetch_reports_network_failures_without_throwing()
        {
            var provider = new SteamWishlistProvider(() => true, () => SteamId, new StubHandler(_ => throw new HttpRequestException("boom")));

            var result = await provider.RefreshAsync(CancellationToken.None);

            Assert.Equal(WishlistProviderState.Error, result.State);
            Assert.Equal(ApiErrorKind.Network, result.Error);
        }

        [Fact]
        public async Task Service_uses_steam_games_with_ggdeals_prices_and_keeps_them_when_steam_fails()
        {
            using (var dir = new TempDir())
            {
                var clock = new FakeClock(Now);
                var steamDown = false;
                var handler = SteamStub(wishlistDown: () => steamDown);
                var api = new FakeApiClient();
                var tracker = new RateLimitTracker(clock);
                var prices = new GGDealsApiPriceProvider(api, tracker, () => "key", clock, (s, t) => Task.CompletedTask);
                var registry = new WishlistProviderRegistry(new IGGDealsWishlistProvider[] { new SteamWishlistProvider(() => true, () => SteamId, handler, clock) });
                var service = new WishlistDataService(registry, prices, tracker,
                    new VersionedJsonStore<CacheDocument>(dir.File("cache.json"), 1),
                    new VersionedJsonStore<LocalStateDocument>(dir.File("state.json"), 1),
                    new EmptyLibrary(), () => new ServiceOptions { Region = "gb", HasApiKey = true }, clock);

                await service.InitializeAsync();
                await service.RefreshAsync(RefreshTrigger.Manual);

                // Wishlist and names from Steam, prices from GG.deals.
                Assert.Equal(4, service.Items.Count);
                Assert.Contains(api.Batches.SelectMany(b => b), id => id == 620);
                var portal = service.Items.Single(i => i.Key == "steam:app:620");
                Assert.Equal("Portal 2", portal.Title);
                Assert.NotNull(portal.DisplayPrice);
                Assert.NotNull(portal.Entry.AddedUtc);

                // The fake GG.deals API doesn't know 1145360, but it still has its Steam name (and no price).
                var hades = service.Items.Single(i => i.Key == "steam:app:1145360");
                Assert.Equal("Hades", hades.Title);
                Assert.Null(hades.DisplayPrice);

                // Neither Steam nor the fake GG.deals API knows 413150: the placeholder title remains.
                Assert.Equal("Steam App 413150", service.Items.Single(i => i.Key == "steam:app:413150").Title);

                steamDown = true;
                clock.Advance(TimeSpan.FromHours(2));
                await service.RefreshAsync(RefreshTrigger.Manual);

                Assert.Equal(4, service.Items.Count);
                var error = service.Status.LastError;
                Assert.NotNull(error);
                Assert.Equal("Steam wishlist", error.Source);
                Assert.Contains("Steam", error.Message);
                service.Dispose();
            }
        }

        private static HttpResponseMessage Json(string body) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class EmptyLibrary : ILibrarySource
        {
            public IReadOnlyList<LibraryGameInfo> GetGames() => new List<LibraryGameInfo>();
        }
    }
}
