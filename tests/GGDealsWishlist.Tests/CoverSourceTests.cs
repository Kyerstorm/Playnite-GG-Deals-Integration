using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Services;
using GGDealsWishlist.ViewModels;
using Xunit;

namespace GGDealsWishlist.Tests
{
    public class CoverSourceTests
    {
        [Fact]
        public void Join_skips_blank_candidates_and_returns_null_when_empty()
        {
            Assert.Null(CoverSources.Join(null, " ", ""));
            Assert.Equal(new[] { "a", "b" }, CoverSources.Split(CoverSources.Join("a", null, "b")));
        }

        [Fact]
        public void SteamGridDb_candidate_round_trips_including_colons_in_titles()
        {
            var candidate = CoverSources.SteamGridDb(true, 42, "Ready or Not: Deluxe");

            Assert.True(CoverSources.TryParseSteamGridDb(candidate, out var portrait, out var appId, out var title));
            Assert.True(portrait);
            Assert.Equal(42, appId);
            Assert.Equal("Ready or Not: Deluxe", title);
        }

        [Fact]
        public void SteamGridDb_candidate_without_app_id_parses_with_null_id()
        {
            Assert.True(CoverSources.TryParseSteamGridDb(CoverSources.SteamGridDb(false, null, "Some Game"), out var portrait, out var appId, out var title));
            Assert.False(portrait);
            Assert.Null(appId);
            Assert.Equal("Some Game", title);
        }

        [Fact]
        public void Plain_urls_are_not_steamgriddb_candidates()
        {
            Assert.False(CoverSources.TryParseSteamGridDb("https://example.com/a.jpg", out _, out _, out _));
        }

        [Fact]
        public void PickImageUrl_skips_webp_and_takes_first_decodable_image()
        {
            var data = Rows(new { url = "https://cdn/x.webp" }, new { url = "https://cdn/y.png?v=1" }, new { url = "https://cdn/z.jpg" });

            Assert.Equal("https://cdn/y.png?v=1", SteamGridDbClient.PickImageUrl(data));
            Assert.Null(SteamGridDbClient.PickImageUrl(Rows(new { url = "https://cdn/x.webp" })));
        }

        [Fact]
        public void PickGameId_prefers_exact_title_and_rejects_unrelated_hits()
        {
            var data = Rows(new { id = 1, name = "Crimson Desert Collector Bundle" }, new { id = 2, name = "Crimson Desert" });
            Assert.Equal("2", SteamGridDbClient.PickGameId(data, "Crimson Desert"));

            Assert.Null(SteamGridDbClient.PickGameId(Rows(new { id = 9, name = "Totally Different" }), "Crimson Desert"));
        }

        [Fact]
        public async Task Lookup_returns_null_without_a_key_and_makes_no_request()
        {
            var handler = new StubHandler();
            var client = new SteamGridDbClient(() => "  ", null, handler);

            Assert.Null(await client.FindGridUrlAsync(true, 1, "Game"));
            Assert.Equal(0, handler.Calls);
        }

        [Fact]
        public async Task Lookup_uses_steam_id_first_and_caches_the_result()
        {
            var handler = new StubHandler();
            handler.Add("/grids/steam/1234", HttpStatusCode.OK, "{\"success\":true,\"data\":[{\"url\":\"https://cdn/grid.png\"}]}");
            var client = new SteamGridDbClient(() => "key", null, handler);

            Assert.Equal("https://cdn/grid.png", await client.FindGridUrlAsync(true, 1234, "Game"));
            Assert.Equal("https://cdn/grid.png", await client.FindGridUrlAsync(true, 1234, "Game"));
            Assert.Equal(1, handler.Calls);
            Assert.Equal("Bearer key", handler.LastAuthorization);
        }

        [Fact]
        public async Task Lookup_falls_back_to_title_search_when_steam_id_has_no_art()
        {
            var handler = new StubHandler();
            handler.Add("/grids/steam/5", HttpStatusCode.NotFound, "{}");
            handler.Add("/search/autocomplete/", HttpStatusCode.OK, "{\"success\":true,\"data\":[{\"id\":77,\"name\":\"Save Sync\"}]}");
            handler.Add("/grids/game/77", HttpStatusCode.OK, "{\"success\":true,\"data\":[{\"url\":\"https://cdn/found.jpg\"}]}");
            var client = new SteamGridDbClient(() => "key", null, handler);

            Assert.Equal("https://cdn/found.jpg", await client.FindGridUrlAsync(true, 5, "SaveSync"));
        }

        [Fact]
        public async Task Errors_are_not_cached_but_definite_misses_are()
        {
            var handler = new StubHandler();
            handler.Add("/grids/steam/9", HttpStatusCode.Unauthorized, "{}");
            var client = new SteamGridDbClient(() => "key", null, handler);

            Assert.Null(await client.FindGridUrlAsync(true, 9, null));
            Assert.Null(await client.FindGridUrlAsync(true, 9, null));
            Assert.Equal(2, handler.Calls);

            handler.Add("/grids/steam/10", HttpStatusCode.NotFound, "{}");
            Assert.Null(await client.FindGridUrlAsync(true, 10, null));
            Assert.Null(await client.FindGridUrlAsync(true, 10, null));
            Assert.Equal(3, handler.Calls);
        }

        [Fact]
        public void PickCandidates_keeps_decodable_images_with_thumbnails_and_respects_max()
        {
            var data = Rows(
                new { url = "https://cdn/a.png", thumb = "https://cdn/thumb/a.webp" },
                new { url = "https://cdn/b.webp", thumb = "https://cdn/thumb/b.jpg" },
                new { url = "https://cdn/c.jpg", thumb = "https://cdn/thumb/c.jpg" },
                new { url = "https://cdn/d.png", thumb = "https://cdn/thumb/d.png" });

            var all = SteamGridDbClient.PickCandidates(data, 10);
            Assert.Equal(new[] { "https://cdn/a.png", "https://cdn/c.jpg", "https://cdn/d.png" }, all.Select(c => c.Url));
            Assert.Equal("https://cdn/a.png", all[0].ThumbUrl);
            Assert.Equal("https://cdn/thumb/c.jpg", all[1].ThumbUrl);

            Assert.Equal(2, SteamGridDbClient.PickCandidates(data, 2).Count);
        }

        [Fact]
        public async Task Picker_lists_candidates_and_reports_the_chosen_cover()
        {
            var handler = new StubHandler();
            handler.Add("/grids/steam/8", HttpStatusCode.OK, "{\"data\":[{\"url\":\"https://cdn/1.png\",\"thumb\":\"https://cdn/t1.jpg\"},{\"url\":\"https://cdn/2.png\"}]}");
            var viewModel = new CoverPickerViewModel(new SteamGridDbClient(() => "key", null, handler), 8, "Game");
            var closed = 0;
            viewModel.CloseRequested += (s, e) => closed++;

            await viewModel.LoadAsync();

            Assert.Equal(2, viewModel.Candidates.Count);
            Assert.Equal(string.Empty, viewModel.StatusText);
            Assert.False(viewModel.IsLoading);

            viewModel.ChooseCommand.Execute(viewModel.Candidates[1]);
            Assert.Equal("https://cdn/2.png", viewModel.ChosenUrl);
            Assert.Equal(1, closed);
        }

        [Fact]
        public async Task Picker_explains_missing_key_and_failures_and_cancel_chooses_nothing()
        {
            var noKey = new CoverPickerViewModel(new SteamGridDbClient(() => "", null, new StubHandler()), 1, "Game");
            await noKey.LoadAsync();
            Assert.Empty(noKey.Candidates);
            Assert.Contains("API key", noKey.StatusText);

            var handler = new StubHandler();
            handler.Add("/grids/steam/2", HttpStatusCode.Unauthorized, "{}");
            var failing = new CoverPickerViewModel(new SteamGridDbClient(() => "bad", null, handler), 2, null);
            await failing.LoadAsync();
            Assert.Contains("Could not load", failing.StatusText);

            failing.CancelCommand.Execute(null);
            Assert.Null(failing.ChosenUrl);
        }

        [Fact]
        public async Task Cache_survives_a_new_client_instance()
        {
            var path = Path.Combine(Path.GetTempPath(), "ggdeals-sgdb-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var first = new StubHandler();
                first.Add("/grids/steam/3", HttpStatusCode.OK, "{\"data\":[{\"url\":\"https://cdn/c.png\"}]}");
                await new SteamGridDbClient(() => "key", path, first).FindGridUrlAsync(true, 3, "C");

                var second = new StubHandler();
                Assert.Equal("https://cdn/c.png", await new SteamGridDbClient(() => "key", path, second).FindGridUrlAsync(true, 3, "C"));
                Assert.Equal(0, second.Calls);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static object[] Rows(params object[] rows)
        {
            var list = new List<object>();
            foreach (var row in rows)
            {
                list.Add(Json.Parse(Json.Serialize(row)));
            }

            return list.ToArray();
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly List<KeyValuePair<string, KeyValuePair<HttpStatusCode, string>>> routes = new List<KeyValuePair<string, KeyValuePair<HttpStatusCode, string>>>();

            public int Calls { get; private set; }

            public string LastAuthorization { get; private set; }

            public void Add(string pathContains, HttpStatusCode status, string body)
            {
                routes.Add(new KeyValuePair<string, KeyValuePair<HttpStatusCode, string>>(pathContains, new KeyValuePair<HttpStatusCode, string>(status, body)));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                LastAuthorization = request.Headers.Authorization?.ToString();
                var url = request.RequestUri.ToString();
                foreach (var route in routes)
                {
                    if (url.Contains(route.Key))
                    {
                        return Task.FromResult(new HttpResponseMessage(route.Value.Key) { Content = new StringContent(route.Value.Value) });
                    }
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            }
        }
    }
}
