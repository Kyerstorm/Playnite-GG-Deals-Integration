using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GGDealsWishlist.Api;
using GGDealsWishlist.Matching;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;
using GGDealsWishlist.Querying;
using GGDealsWishlist.Services;
using Xunit;

namespace GGDealsWishlist.Tests
{
    public class MatcherTests
    {
        private static LibraryGameInfo Steam(string name, long appId, bool installed = false) => new LibraryGameInfo
        {
            Id = Guid.NewGuid(), Name = name, PluginId = PlayniteMatcher.SteamLibraryPluginId, GameId = appId.ToString(), IsInstalled = installed
        };

        private static LibraryGameInfo Other(string name, bool installed = false, params string[] links) => new LibraryGameInfo
        {
            Id = Guid.NewGuid(), Name = name, PluginId = Guid.NewGuid(), GameId = "x", IsInstalled = installed, LinkUrls = links.ToList()
        };

        private static WishlistEntry App(long id, string title = null) => new WishlistEntry { Key = "steam:app:" + id, SteamId = id, SteamIdType = SteamIdType.App, Title = title };

        [Fact]
        public void Steam_app_id_is_the_primary_match()
        {
            var hades = Steam("Hades", 1145360, installed: true);
            var matcher = new PlayniteMatcher(new[] { hades, Other("Something else") });

            var result = matcher.Match(App(1145360, "Totally different title"), null, null);

            Assert.Equal(MatchKind.SteamAppId, result.Kind);
            Assert.True(result.IsOwned);
            Assert.True(result.IsInstalled);
            Assert.Same(hades, result.Game);
        }

        [Fact]
        public void Owned_does_not_mean_installed()
        {
            var matcher = new PlayniteMatcher(new[] { Steam("Elden Ring", 1245620, installed: false) });
            var result = matcher.Match(App(1245620), "ELDEN RING", null);
            Assert.True(result.IsOwned);
            Assert.False(result.IsInstalled);
        }

        [Fact]
        public void Steam_store_link_on_a_non_steam_game_counts_as_owned()
        {
            var gog = Other("The Witcher 3: Wild Hunt", false, "https://store.steampowered.com/app/292030/");
            var result = new PlayniteMatcher(new[] { gog }).Match(App(292030), "The Witcher 3", null);
            Assert.Equal(MatchKind.StoreLink, result.Kind);
            Assert.True(result.IsOwned);
        }

        [Fact]
        public void Exact_normalised_title_is_owned_and_prefers_installed_copy()
        {
            var epic = Other("Cyberpunk 2077", installed: false);
            var gog = Other("Cyberpunk 2077™", installed: true);
            var result = new PlayniteMatcher(new[] { epic, gog }).Match(App(1), "CYBERPUNK 2077", null);

            Assert.Equal(MatchKind.ExactTitle, result.Kind);
            Assert.Same(gog, result.Game);
            Assert.Equal(2, result.CandidateCount);
        }

        [Fact]
        public void Ambiguous_or_partial_titles_are_only_potential_matches()
        {
            var matcher = new PlayniteMatcher(new[] { Other("The Witcher 3: Wild Hunt") });
            var result = matcher.Match(App(1, "The Witcher 3"), null, null);

            Assert.Equal(MatchKind.PotentialTitle, result.Kind);
            Assert.False(result.IsOwned);
            Assert.True(result.IsPotential);
        }

        [Fact]
        public void Dlc_is_not_classified_as_the_base_game()
        {
            var matcher = new PlayniteMatcher(new[] { Other("Hades") });
            var result = matcher.Match(App(2, "Hades - Original Soundtrack"), null, null);
            Assert.False(result.IsOwned);
        }

        [Fact]
        public void Unrelated_titles_do_not_match()
        {
            var matcher = new PlayniteMatcher(new[] { Other("Portal"), Other("Doom") });
            Assert.Equal(MatchKind.None, matcher.Match(App(3, "Hollow Knight"), null, null).Kind);
        }

        [Fact]
        public void Exact_title_can_be_downgraded_by_setting()
        {
            var matcher = new PlayniteMatcher(new[] { Other("Hades") });
            Assert.False(matcher.Match(App(4, "Hades"), null, null, treatExactTitleAsOwned: false).IsOwned);
        }

        [Fact]
        public void User_overrides_confirm_reject_and_fall_back_when_game_deleted()
        {
            var game = Other("The Witcher 3: Wild Hunt");
            var matcher = new PlayniteMatcher(new[] { game });

            Assert.Equal(MatchKind.UserConfirmed, matcher.Match(App(5, "The Witcher 3"), null, game.Id.ToString()).Kind);
            Assert.Equal(MatchKind.UserRejected, matcher.Match(App(5, "The Witcher 3"), null, PlayniteMatcher.RejectedOverride).Kind);
            Assert.Equal(MatchKind.PotentialTitle, matcher.Match(App(5, "The Witcher 3"), null, Guid.NewGuid().ToString()).Kind);
        }

        [Theory]
        [InlineData("DOOM Eternal™", "doom eternal")]
        [InlineData("Tom Clancy's Rainbow Six® Siege", "tom clancys rainbow six siege")]
        [InlineData("Pokémon  —  Edition", "pokemon edition")]
        public void Normalizes_titles(string input, string expected)
        {
            Assert.Equal(expected, TitleNormalizer.Normalize(input));
        }

        [Fact]
        public void Handles_large_libraries_quickly()
        {
            var games = Enumerable.Range(1, 5000).Select(i => Steam("Game number " + i, i)).ToList();
            var matcher = new PlayniteMatcher(games);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++)
            {
                matcher.Match(App(100000 + i, "Unknown title " + i), null, null);
            }

            Assert.True(watch.ElapsedMilliseconds < 5000, "Matching 1,000 entries took " + watch.ElapsedMilliseconds + "ms");
        }
    }

    public class QueryEngineTests
    {
        private static WishlistItem Item(string key, string title, decimal? price = null, int? discount = null, bool historicalLow = false,
            bool owned = false, bool favourite = false, string[] collections = null, int? position = null, DateTime? added = null,
            decimal? keyshop = null, decimal? historical = null, DateTime? discountStarted = null)
        {
            var match = owned ? new MatchResult(MatchKind.SteamAppId, new LibraryGameInfo { Id = Guid.NewGuid(), Name = title }, 1) : MatchResult.NoMatch;
            return new WishlistItem
            {
                Key = key,
                Title = title,
                SearchTitle = title.ToLowerInvariant(),
                SearchExtra = "retail",
                Entry = new WishlistEntry { Key = key, WishlistPosition = position, AddedUtc = added },
                Price = new PriceData { Found = true, CurrentRetail = price, CurrentKeyshops = keyshop, DiscountStartedUtc = discountStarted },
                DisplayPrice = price,
                DisplayHistoricalLow = historical,
                DiscountPercent = discount,
                IsHistoricalLow = historicalLow,
                Match = match,
                IsFavourite = favourite,
                CollectionIds = collections ?? new string[0]
            };
        }

        private static readonly List<WishlistItem> Sample = new List<WishlistItem>
        {
            Item("a", "Hades", 9.99m, 60, historicalLow: true, favourite: true, collections: new[] { "rpg" }, position: 2, added: new DateTime(2026, 1, 1), historical: 7.49m, discountStarted: new DateTime(2026, 9, 1)),
            Item("b", "Cyberpunk 2077", 19.99m, 50, position: 1, added: new DateTime(2026, 3, 1), keyshop: 12m, historical: 15m, discountStarted: new DateTime(2026, 9, 20)),
            Item("c", "Elden Ring", 34.99m, 30, owned: true, position: 3, historical: 30m),
            Item("d", "Stardew Valley", 4.49m, null, collections: new[] { "cosy", "rpg" }, position: 4),
            Item("e", "No Price Game", null, null, position: 5),
            Item("a", "Duplicate of Hades", 1m),
            Item("", "Invalid", 1m),
            null
        };

        private static List<string> Keys(FilterState filter, SortMode sort = SortMode.WishlistOrder, QueryOptions options = null) =>
            WishlistQueryEngine.Apply(Sample, filter, sort, options).Select(i => i.Key).ToList();

        [Fact]
        public void Removes_invalid_and_duplicate_items()
        {
            Assert.Equal(new[] { "b", "a", "c", "d", "e" }, Keys(new FilterState()));
        }

        [Theory]
        [InlineData(PriceFilter.Under5, new[] { "d" })]
        [InlineData(PriceFilter.Under10, new[] { "a", "d" })]
        [InlineData(PriceFilter.Under20, new[] { "b", "a", "d" })]
        public void Filters_by_price(PriceFilter filter, string[] expected)
        {
            Assert.Equal(expected, Keys(new FilterState { Price = filter }));
        }

        [Fact]
        public void Custom_price_is_inclusive()
        {
            Assert.Equal(new[] { "b", "a", "d" }, Keys(new FilterState { Price = PriceFilter.Custom, CustomMaxPrice = 19.99m }));
        }

        [Theory]
        [InlineData(10, new[] { "b", "a", "c" })]
        [InlineData(50, new[] { "b", "a" })]
        [InlineData(75, new string[0])]
        public void Filters_by_discount(int min, string[] expected)
        {
            Assert.Equal(expected, Keys(new FilterState { MinDiscount = min }));
        }

        [Fact]
        public void Filters_by_store_category_sale_status_and_historical_low()
        {
            Assert.Equal(new[] { "b" }, Keys(new FilterState { Store = StoreFilter.Keyshop }));
            Assert.Equal(new[] { "b", "a", "c", "d" }, Keys(new FilterState { Store = StoreFilter.Retail }));
            Assert.Equal(new[] { "b", "a", "c" }, Keys(new FilterState { SaleStatus = SaleStatusFilter.OnSale }));
            Assert.Equal(new[] { "d", "e" }, Keys(new FilterState { SaleStatus = SaleStatusFilter.NotOnSale }));
            Assert.Equal(new[] { "a" }, Keys(new FilterState { HistoricalLowOnly = true }));
        }

        [Fact]
        public void Filters_by_ownership_and_hide_owned_setting()
        {
            Assert.Equal(new[] { "c" }, Keys(new FilterState { Ownership = OwnershipFilter.Owned }));
            Assert.Equal(new[] { "b", "a", "d", "e" }, Keys(new FilterState { Ownership = OwnershipFilter.NotOwned }));
            Assert.Equal(new[] { "b", "a", "d", "e" }, Keys(new FilterState(), options: new QueryOptions { HideOwned = true }));
            Assert.Equal(new[] { "c" }, Keys(new FilterState { Ownership = OwnershipFilter.Owned }, options: new QueryOptions { HideOwned = true }));
        }

        [Fact]
        public void Filters_by_favourites_and_collections()
        {
            Assert.Equal(new[] { "a" }, Keys(new FilterState { Favourites = FavouriteFilter.Favourites }));
            Assert.Equal(new[] { "b", "c", "d", "e" }, Keys(new FilterState { Favourites = FavouriteFilter.NonFavourites }));
            Assert.Equal(new[] { "a", "d" }, Keys(new FilterState { CollectionIds = new List<string> { "rpg" } }));
            Assert.Equal(new[] { "d" }, Keys(new FilterState { CollectionIds = new List<string> { "cosy" } }));
        }

        [Fact]
        public void Searches_locally_with_all_tokens()
        {
            Assert.Equal(new[] { "d" }, Keys(new FilterState { SearchText = "valley STAR" }));
            Assert.Empty(Keys(new FilterState { SearchText = "retail" }));
            Assert.Equal(5, Keys(new FilterState { SearchText = "retail" }, options: new QueryOptions { SearchIncludesExtra = true }).Count);
        }

        [Fact]
        public void Combined_filters_apply_together()
        {
            var filter = new FilterState { Price = PriceFilter.Under20, MinDiscount = 25, Ownership = OwnershipFilter.NotOwned, SearchText = "a" };
            Assert.Equal(new[] { "a" }, Keys(filter));
        }

        [Theory]
        [InlineData(SortMode.WishlistOrder, new[] { "b", "a", "c", "d", "e" })]
        [InlineData(SortMode.PriceAscending, new[] { "d", "a", "b", "c", "e" })]
        [InlineData(SortMode.PriceDescending, new[] { "c", "b", "a", "d", "e" })]
        [InlineData(SortMode.DiscountDescending, new[] { "a", "b", "c", "d", "e" })]
        [InlineData(SortMode.DiscountAscending, new[] { "c", "b", "a", "d", "e" })]
        [InlineData(SortMode.HistoricalLow, new[] { "a", "b", "c", "d", "e" })]
        [InlineData(SortMode.RecentlyAdded, new[] { "b", "a", "c", "d", "e" })]
        [InlineData(SortMode.RecentlyDiscounted, new[] { "b", "a", "c", "d", "e" })]
        [InlineData(SortMode.NameAscending, new[] { "b", "c", "a", "e", "d" })]
        [InlineData(SortMode.NameDescending, new[] { "d", "e", "a", "c", "b" })]
        public void Sorts_locally(SortMode sort, string[] expected)
        {
            Assert.Equal(expected, Keys(new FilterState(), sort));
        }

        [Fact]
        public void Summarises_counts()
        {
            var summary = WishlistQueryEngine.Summarize(Sample);
            Assert.Equal(5, summary.Total);
            Assert.Equal(3, summary.OnSale);
            Assert.Equal(1, summary.HistoricalLows);
            Assert.Equal(1, summary.Owned);
            Assert.True(summary.HasDiscountData);
            Assert.Equal(4, WishlistQueryEngine.Summarize(Sample, new QueryOptions { HideOwned = true }).Total);
        }

        [Fact]
        public void Filters_thousands_of_items_quickly()
        {
            var many = Enumerable.Range(0, 5000).Select(i => Item("k" + i, "Game " + i, i % 50, i % 90, i % 7 == 0, i % 3 == 0, i % 5 == 0, null, i)).ToList();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                WishlistQueryEngine.Apply(many, new FilterState { Price = PriceFilter.Under20, SearchText = "game 1" }, SortMode.PriceAscending, new QueryOptions { HideOwned = true });
            }

            Assert.True(watch.ElapsedMilliseconds < 2000);
        }
    }

    public sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ggdeals-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (IOException) { }
        }
    }

    public class PersistenceTests
    {
        [Fact]
        public void Round_trips_cache_and_local_state()
        {
            using (var dir = new TempDir())
            {
                var cacheStore = new VersionedJsonStore<CacheDocument>(dir.File("cache.json"), CacheDocument.CurrentVersion);
                var doc = new CacheDocument
                {
                    Wishlists = { { WishlistScope.OwnId, new WishlistSnapshot { Entries = { new WishlistEntry { Key = "steam:app:1", SteamId = 1, Title = "One", WishlistPosition = 1 } } } } },
                    Prices = { { "app:1", new PriceData { LookupKey = "app:1", Found = true, CurrentRetail = 9.99m, Currency = "GBP", Region = "gb", FetchedUtc = new DateTime(2026, 9, 27, 13, 42, 0, DateTimeKind.Utc) } } },
                    PricesUpdatedUtc = new DateTime(2026, 9, 27, 13, 42, 0, DateTimeKind.Utc)
                };
                cacheStore.Save(doc);
                var loaded = cacheStore.Load();

                Assert.Equal("One", loaded.Wishlists[WishlistScope.OwnId].Entries.Single().Title);
                Assert.Equal(9.99m, loaded.Prices["app:1"].CurrentRetail);
                Assert.Equal(doc.PricesUpdatedUtc, loaded.PricesUpdatedUtc);
                Assert.Equal(DateTimeKind.Utc, loaded.Prices["app:1"].FetchedUtc.Kind);

                var stateStore = new VersionedJsonStore<LocalStateDocument>(dir.File("state.json"), LocalStateDocument.CurrentVersion);
                var state = new LocalStateDocument
                {
                    Scopes =
                    {
                        {
                            WishlistScope.OwnId, new ScopeLocalState
                            {
                                Favourites = { "steam:app:1" },
                                Collections = { new WishlistCollection { Id = "c", Name = "Buy Soon", Icon = "🎯" } },
                                Memberships = { { "steam:app:1", new List<string> { "c" } } }
                            }
                        }
                    },
                    Ui = new UiState { ViewMode = ViewMode.List, Sort = SortMode.PriceAscending, Filter = new FilterState { MinDiscount = 50, CollectionIds = { "c" } } }
                };
                stateStore.Save(state);
                var loadedState = stateStore.Load();

                Assert.Equal("🎯", loadedState.Scopes[WishlistScope.OwnId].Collections.Single().Icon);
                Assert.Equal(ViewMode.List, loadedState.Ui.ViewMode);
                Assert.Equal(SortMode.PriceAscending, loadedState.Ui.Sort);
                Assert.Equal(50, loadedState.Ui.Filter.MinDiscount);
                Assert.Equal("c", loadedState.Scopes[WishlistScope.OwnId].Memberships["steam:app:1"].Single());
            }
        }

        [Fact]
        public void Corrupt_file_is_quarantined_and_replaced_with_defaults()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("cache.json"), "{ this is not json");
                var store = new VersionedJsonStore<CacheDocument>(dir.File("cache.json"), 1);

                var loaded = store.Load();

                Assert.Empty(loaded.Wishlists);
                Assert.False(File.Exists(dir.File("cache.json")));
                Assert.Single(Directory.GetFiles(dir.Path, "cache.json.corrupt-*"));
            }
        }

        [Fact]
        public void Newer_data_version_is_backed_up_not_misread()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("state.json"), "{\"DataVersion\":7,\"Favourites\":[\"x\"]}");
                var loaded = new VersionedJsonStore<LocalStateDocument>(dir.File("state.json"), 1).Load();

                Assert.Empty(loaded.Scopes);
                Assert.True(File.Exists(dir.File("state.json.v7.bak")));
            }
        }

        [Fact]
        public void Older_versions_are_migrated_step_by_step()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("state.json"), "{\"DataVersion\":1,\"Favs\":[\"legacy\"]}");
                var migrations = new Dictionary<int, Action<IDictionary<string, object>>>
                {
                    {
                        1, doc =>
                        {
                            doc["Scopes"] = new Dictionary<string, object> { { WishlistScope.OwnId, new Dictionary<string, object> { { "Favourites", doc["Favs"] } } } };
                            doc.Remove("Favs");
                        }
                    }
                };
                var loaded = new VersionedJsonStore<LocalStateDocument>(dir.File("state.json"), 2, migrations).Load();

                Assert.Equal(new[] { "legacy" }, loaded.Scope(WishlistScope.OwnId).Favourites);
                Assert.Equal(2, loaded.DataVersion);
            }
        }

        [Fact]
        public void A_version_1_state_file_becomes_the_own_wishlists_state()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("state.json"),
                    "{\"DataVersion\":1,\"Favourites\":[\"steam:app:1\"],\"Collections\":[{\"Id\":\"c\",\"Name\":\"Buy Soon\"}],"
                    + "\"Memberships\":{\"steam:app:1\":[\"c\"]},\"MatchOverrides\":{\"steam:app:2\":\"none\"}}");

                var loaded = new VersionedJsonStore<LocalStateDocument>(dir.File("state.json"), LocalStateDocument.CurrentVersion, LocalStateDocument.Migrations).Load();

                var own = loaded.Scope(WishlistScope.OwnId);
                Assert.Equal(new[] { "steam:app:1" }, own.Favourites);
                Assert.Equal("Buy Soon", own.Collections.Single().Name);
                Assert.Equal("c", own.Memberships["steam:app:1"].Single());

                // A match override describes the game, not a wishlist, so it stays shared.
                Assert.Equal("none", loaded.MatchOverrides["steam:app:2"]);
                Assert.Equal(LocalStateDocument.CurrentVersion, loaded.DataVersion);
            }
        }

        [Fact]
        public void A_version_1_cache_file_becomes_the_own_wishlists_snapshot()
        {
            using (var dir = new TempDir())
            {
                File.WriteAllText(dir.File("cache.json"),
                    "{\"DataVersion\":1,\"WishlistProviderId\":\"manual\",\"WishlistEntries\":[{\"Key\":\"steam:app:1\",\"SteamId\":1,\"Title\":\"One\"}],"
                    + "\"Prices\":{\"app:1\":{\"LookupKey\":\"app:1\",\"Found\":true,\"CurrentRetail\":9.99}}}");

                var loaded = new VersionedJsonStore<CacheDocument>(dir.File("cache.json"), CacheDocument.CurrentVersion, CacheDocument.Migrations).Load();

                var own = loaded.Wishlists[WishlistScope.OwnId];
                Assert.Equal("manual", own.ProviderId);
                Assert.Equal("One", own.Entries.Single().Title);

                // A price does not depend on whose wishlist a game is on, so prices stay shared.
                Assert.Equal(9.99m, loaded.Prices["app:1"].CurrentRetail);
                Assert.Equal(CacheDocument.CurrentVersion, loaded.DataVersion);
            }
        }
    }

    public class DataServiceTests
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private sealed class FakeLibrary : ILibrarySource
        {
            public List<LibraryGameInfo> Games { get; } = new List<LibraryGameInfo>();

            public IReadOnlyList<LibraryGameInfo> GetGames() => Games;
        }

        private sealed class Harness : IDisposable
        {
            public readonly TempDir Dir = new TempDir();
            public readonly FakeClock Clock = new FakeClock(Start);
            public readonly FakeApiClient Api = new FakeApiClient();
            public readonly FakeLibrary Library = new FakeLibrary();
            public readonly ServiceOptions Options = new ServiceOptions { Region = "gb", HasApiKey = true };
            public string ManualText = "1\n2\n3 | Custom Title\n10";
            public bool ManualEnabled = true;
            public WishlistDataService Service;

            public Harness()
            {
                Service = Create();
            }

            /// <summary>Games per wishlist (manual-list text by scope id); a scope without an entry uses <see cref="ManualText"/>.</summary>
            public readonly Dictionary<string, string> ScopeTexts = new Dictionary<string, string>();

            /// <summary>Another person's wishlist the user can switch to, next to their own.</summary>
            public void AddFriend(string steamId = "76561198000000001")
            {
                Options.Wishlists = WishlistScope.Build(new[] { new WishlistScope { Id = "friend", Name = "Alex", SteamId = steamId } });
            }

            public WishlistDataService Create()
            {
                var tracker = new RateLimitTracker(Clock);
                var prices = new GGDealsApiPriceProvider(Api, tracker, () => Options.HasApiKey ? "key" : null, Clock, (s, t) => { Clock.Advance(s); return Task.CompletedTask; });
                WishlistDataService created = null;
                var registry = new WishlistProviderRegistry(new IGGDealsWishlistProvider[]
                {
                    new ManualWishlistProvider(() => ManualEnabled, () => TextFor(created?.ActiveScopeId), Clock)
                });
                created = new WishlistDataService(registry, prices, tracker,
                    new VersionedJsonStore<CacheDocument>(Dir.File("cache.json"), CacheDocument.CurrentVersion, CacheDocument.Migrations),
                    new VersionedJsonStore<LocalStateDocument>(Dir.File("state.json"), LocalStateDocument.CurrentVersion, LocalStateDocument.Migrations),
                    Library, () => Options, Clock,
                    new VersionedJsonStore<PriceHistoryDocument>(Dir.File("price-history.json"), PriceHistoryDocument.CurrentVersion));
                return created;
            }

            private string TextFor(string scopeId) => scopeId != null && ScopeTexts.TryGetValue(scopeId, out var text) ? text : ManualText;

            public void Dispose()
            {
                Service.Dispose();
                Dir.Dispose();
            }
        }

        [Fact]
        public async Task Refresh_builds_items_with_prices_titles_and_ownership()
        {
            using (var h = new Harness())
            {
                h.Library.Games.Add(new LibraryGameInfo { Id = Guid.NewGuid(), Name = "Owned", PluginId = PlayniteMatcher.SteamLibraryPluginId, GameId = "2", IsInstalled = true });
                await h.Service.InitializeAsync();
                var outcome = await h.Service.RefreshAsync(RefreshTrigger.Manual);

                Assert.Equal(RefreshOutcome.Completed, outcome);
                var items = h.Service.Items;
                Assert.Equal(4, items.Count);
                Assert.Equal("Game 1", items[0].Title);
                Assert.Equal("Custom Title", items[2].Title);
                Assert.Equal("Steam App 10", items[3].Title);
                Assert.False(items[3].Price.Found);
                Assert.True(items[1].IsOwned);
                Assert.True(items[1].IsInstalled);
                Assert.Equal("GBP", items[0].Currency);
                Assert.Single(h.Api.Batches);
                Assert.Null(h.Service.Status.LastError);
            }
        }

        [Fact]
        public async Task Fresh_prices_are_not_requested_again()
        {
            using (var h = new Harness())
            {
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                h.Clock.Advance(TimeSpan.FromMinutes(5));
                await h.Service.RefreshAsync(RefreshTrigger.Manual);

                Assert.Single(h.Api.Batches);
                Assert.Equal("Prices are up to date.", h.Service.Status.LastInfoMessage);
                Assert.False(h.Service.IsAutoRefreshDue());

                h.Clock.Advance(TimeSpan.FromMinutes(60));
                Assert.True(h.Service.IsAutoRefreshDue());
            }
        }

        private static void PricedAt(Harness h, decimal retail)
        {
            h.Api.Responder = ids =>
            {
                var result = new PriceApiResult();
                foreach (var id in ids)
                {
                    result.Items[id] = new PriceData { LookupKey = "app:" + id, Found = true, GGDealsTitle = "Game " + id, CurrentRetail = retail, HistoricalRetail = 5m, Currency = "GBP", Region = "gb" };
                }

                return result;
            };
        }

        [Fact]
        public async Task Refreshes_record_price_history_that_survives_a_restart()
        {
            using (var h = new Harness())
            {
                await h.Service.InitializeAsync();
                PricedAt(h, 20m);
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                h.Clock.Advance(TimeSpan.FromMinutes(20));
                PricedAt(h, 15m);
                await h.Service.RefreshAsync(RefreshTrigger.Manual);

                var series = h.Service.GetPriceSeries("app:1");
                Assert.Equal(new decimal?[] { 20m, 15m }, series.Points.Select(p => p.Retail).ToArray());
                Assert.Equal(2, h.Service.Items[0].PriceHistory.Points.Count);

                var restarted = h.Create();
                await restarted.InitializeAsync();
                Assert.Equal(2, restarted.Items[0].PriceHistory.Points.Count);
                restarted.Dispose();
            }
        }

        [Fact]
        public async Task An_unchanged_price_does_not_add_a_history_point()
        {
            using (var h = new Harness())
            {
                await h.Service.InitializeAsync();
                PricedAt(h, 20m);
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                h.Clock.Advance(TimeSpan.FromMinutes(20));
                await h.Service.RefreshAsync(RefreshTrigger.Manual);

                var series = h.Service.GetPriceSeries("app:1");
                Assert.Single(series.Points);
                Assert.Equal(Start.AddMinutes(20), series.LastSeenUtc);
            }
        }

        [Fact]
        public async Task Clearing_the_cache_keeps_history_but_clearing_history_keeps_prices()
        {
            using (var h = new Harness())
            {
                await h.Service.InitializeAsync();
                PricedAt(h, 20m);
                await h.Service.RefreshAsync(RefreshTrigger.Manual);

                h.Service.ClearCache();
                Assert.NotNull(h.Service.GetPriceSeries("app:1"));

                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                h.Service.ClearPriceHistory();

                Assert.Null(h.Service.GetPriceSeries("app:1"));
                Assert.Null(h.Service.Items[0].PriceHistory);
                Assert.NotNull(h.Service.Items[0].Price);

                var restarted = h.Create();
                await restarted.InitializeAsync();
                Assert.Null(restarted.GetPriceSeries("app:1"));
                restarted.Dispose();
            }
        }

        [Fact]
        public async Task Games_removed_from_the_wishlist_lose_their_history()
        {
            using (var h = new Harness())
            {
                await h.Service.InitializeAsync();
                PricedAt(h, 20m);
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                Assert.NotNull(h.Service.GetPriceSeries("app:2"));

                h.ManualText = "1";
                h.Clock.Advance(TimeSpan.FromMinutes(20));
                await h.Service.RefreshAsync(RefreshTrigger.Manual);

                Assert.NotNull(h.Service.GetPriceSeries("app:1"));
                Assert.Null(h.Service.GetPriceSeries("app:2"));
            }
        }

        // -----------------------------------------------------------------------------------------
        // Several wishlists: one is shown at a time and they are never combined
        // -----------------------------------------------------------------------------------------

        private static string[] Keys(WishlistDataService service) => service.Items.Select(i => i.Key).ToArray();

        [Fact]
        public async Task Switching_wishlists_shows_only_that_wishlists_games()
        {
            using (var h = new Harness())
            {
                h.AddFriend();
                h.ScopeTexts[WishlistScope.OwnId] = "1\n2";
                h.ScopeTexts["friend"] = "3";
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                Assert.Equal(new[] { "steam:app:1", "steam:app:2" }, Keys(h.Service));

                Assert.True(h.Service.SwitchTo("friend"));
                await h.Service.SwitchRefreshTask;
                Assert.Equal(new[] { "steam:app:3" }, Keys(h.Service));

                Assert.True(h.Service.SwitchTo(WishlistScope.OwnId));
                await h.Service.SwitchRefreshTask;
                Assert.Equal(new[] { "steam:app:1", "steam:app:2" }, Keys(h.Service));
            }
        }

        [Fact]
        public async Task Switching_to_a_wishlist_that_does_not_exist_changes_nothing()
        {
            using (var h = new Harness())
            {
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);

                Assert.False(h.Service.SwitchTo("nobody"));
                Assert.Equal(WishlistScope.OwnId, h.Service.ActiveScopeId);
                Assert.Equal(4, h.Service.Items.Count);
            }
        }

        [Fact]
        public async Task Favourites_and_collections_belong_to_one_wishlist()
        {
            using (var h = new Harness())
            {
                h.AddFriend();
                h.ScopeTexts[WishlistScope.OwnId] = "1\n2";
                h.ScopeTexts["friend"] = "1";
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);

                h.Service.SetFavourite("steam:app:1", true);
                var collection = h.Service.CreateCollection("Buy Soon", null);
                h.Service.SetCollectionMembership(new[] { "steam:app:1" }, collection.Id, true);
                Assert.True(h.Service.Items.Single(i => i.Key == "steam:app:1").IsFavourite);

                // The same game on the other wishlist is not starred and is in no collection.
                h.Service.SwitchTo("friend");
                await h.Service.SwitchRefreshTask;
                var shared = h.Service.Items.Single();
                Assert.False(shared.IsFavourite);
                Assert.Empty(shared.CollectionIds);
                Assert.Empty(h.Service.Collections);

                h.Service.SwitchTo(WishlistScope.OwnId);
                Assert.True(h.Service.Items.Single(i => i.Key == "steam:app:1").IsFavourite);
                Assert.Single(h.Service.Collections);
            }
        }

        [Fact]
        public async Task Switching_wishlists_does_not_delete_the_price_history_of_the_other_wishlists_games()
        {
            using (var h = new Harness())
            {
                h.AddFriend();
                h.ScopeTexts[WishlistScope.OwnId] = "1\n2";
                h.ScopeTexts["friend"] = "3";
                await h.Service.InitializeAsync();
                PricedAt(h, 20m);
                await h.Service.RefreshAsync(RefreshTrigger.Manual);

                h.Service.SwitchTo("friend");
                await h.Service.SwitchRefreshTask;
                Assert.NotNull(h.Service.GetPriceSeries("app:1"));
                Assert.NotNull(h.Service.GetPriceSeries("app:3"));

                // Refreshing your own wishlist again must not drop the history of a game only the friend has.
                h.Service.SwitchTo(WishlistScope.OwnId);
                h.Clock.Advance(TimeSpan.FromMinutes(20));
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                Assert.NotNull(h.Service.GetPriceSeries("app:3"));
            }
        }

        [Fact]
        public async Task The_wishlist_being_viewed_is_remembered_across_a_restart()
        {
            using (var h = new Harness())
            {
                h.AddFriend();
                h.ScopeTexts[WishlistScope.OwnId] = "1\n2";
                h.ScopeTexts["friend"] = "3";
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                h.Service.SwitchTo("friend");
                await h.Service.SwitchRefreshTask;
                h.Service.FlushState();

                var restarted = h.Create();
                await restarted.InitializeAsync();

                Assert.Equal("friend", restarted.ActiveScopeId);
                Assert.Equal(new[] { "steam:app:3" }, Keys(restarted));
                restarted.Dispose();
            }
        }

        [Fact]
        public async Task Removing_the_wishlist_being_viewed_returns_to_the_own_wishlist()
        {
            using (var h = new Harness())
            {
                h.AddFriend();
                h.ScopeTexts[WishlistScope.OwnId] = "1\n2";
                h.ScopeTexts["friend"] = "3";
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                h.Service.SwitchTo("friend");
                await h.Service.SwitchRefreshTask;

                h.Options.Wishlists = WishlistScope.Build(null);
                h.Service.OnOptionsChanged();

                Assert.Equal(WishlistScope.OwnId, h.Service.ActiveScopeId);
                Assert.Equal(new[] { "steam:app:1", "steam:app:2" }, Keys(h.Service));
            }
        }

        [Fact]
        public async Task Pointing_a_saved_wishlist_at_another_person_drops_the_previous_persons_games()
        {
            using (var h = new Harness())
            {
                h.AddFriend();
                h.ScopeTexts["friend"] = "3";
                await h.Service.InitializeAsync();
                h.Service.SwitchTo("friend");
                await h.Service.SwitchRefreshTask;
                Assert.Equal(new[] { "steam:app:3" }, Keys(h.Service));

                h.AddFriend("76561198000000002");
                h.Service.OnOptionsChanged();

                Assert.Empty(h.Service.Items);
            }
        }

        [Fact]
        public async Task A_refresh_that_finishes_after_switching_wishlists_is_dropped_not_filed_under_the_wrong_one()
        {
            using (var dir = new TempDir())
            {
                var clock = new FakeClock(Start);
                var tracker = new RateLimitTracker(clock);
                var prices = new GGDealsApiPriceProvider(new FakeApiClient(), tracker, () => "key", clock, (s, t) => { clock.Advance(s); return Task.CompletedTask; });
                var gated = new GatedProvider();
                var options = new ServiceOptions
                {
                    Region = "gb",
                    HasApiKey = true,
                    Wishlists = WishlistScope.Build(new[] { new WishlistScope { Id = "friend", Name = "Alex", SteamId = "76561198000000001" } })
                };
                using (var service = new WishlistDataService(
                    new WishlistProviderRegistry(new IGGDealsWishlistProvider[] { gated }), prices, tracker,
                    new VersionedJsonStore<CacheDocument>(dir.File("cache.json"), CacheDocument.CurrentVersion, CacheDocument.Migrations),
                    new VersionedJsonStore<LocalStateDocument>(dir.File("state.json"), LocalStateDocument.CurrentVersion, LocalStateDocument.Migrations),
                    new FakeLibrary(), () => options, clock))
                {
                    await service.InitializeAsync();
                    var ownRefresh = service.RefreshAsync(RefreshTrigger.Manual);

                    // The user switches while the first wishlist is still loading; the provider could have read either one.
                    service.SwitchTo("friend");
                    gated.Gate.SetResult(new WishlistFetchResult
                    {
                        State = WishlistProviderState.Available,
                        Entries = { new WishlistEntry { Key = "steam:app:1", SteamId = 1, Title = "One", WishlistPosition = 1 } }
                    });
                    await ownRefresh;
                    await service.SwitchRefreshTask;

                    Assert.Equal("friend", service.ActiveScopeId);
                    Assert.Empty(service.Items);
                }
            }
        }

        private sealed class GatedProvider : IGGDealsWishlistProvider
        {
            public readonly TaskCompletionSource<WishlistFetchResult> Gate = new TaskCompletionSource<WishlistFetchResult>();

            public string Id => "gated";

            public string DisplayName => "Gated";

            public WishlistCapabilities Capabilities => WishlistCapabilities.None;

            public string StateMessage => "Waiting";

            public WishlistProviderState GetState() => WishlistProviderState.Available;

            public Task<WishlistFetchResult> GetWishlistAsync(System.Threading.CancellationToken cancellationToken) => Gate.Task;

            public Task<WishlistEntry> GetWishlistItemAsync(string key, System.Threading.CancellationToken cancellationToken) => Task.FromResult<WishlistEntry>(null);

            public Task<WishlistFetchResult> RefreshAsync(System.Threading.CancellationToken cancellationToken) => Gate.Task;
        }

        [Fact]
        public async Task Region_change_triggers_price_refresh()
        {
            using (var h = new Harness())
            {
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                h.Options.Region = "us";
                h.Service.OnOptionsChanged();
                Assert.True(h.Service.Items.All(i => i.IsPriceStale));

                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                Assert.Equal(2, h.Api.Batches.Count);
            }
        }

        [Fact]
        public async Task Failure_keeps_cached_data_and_records_error()
        {
            using (var h = new Harness())
            {
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                h.Api.Responder = ids => new PriceApiResult { Error = ApiErrorKind.Network, Message = "⚠ Unable to connect to GG.deals." };
                h.Clock.Advance(TimeSpan.FromHours(2));

                var outcome = await h.Service.RefreshAsync(RefreshTrigger.Manual);

                Assert.Equal(RefreshOutcome.Failed, outcome);
                Assert.Equal(ApiErrorKind.Network, h.Service.Status.LastError.Kind);
                Assert.Equal(4, h.Service.Items.Count);
                Assert.Equal("Game 1", h.Service.Items[0].Title);
                Assert.False(h.Service.IsAutoRefreshDue());
            }
        }

        [Fact]
        public async Task Cache_and_local_state_survive_restart()
        {
            using (var h = new Harness())
            {
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                var collection = h.Service.CreateCollection("Buy Soon", "🎯");
                h.Service.SetCollectionMembership(new[] { "steam:app:1" }, collection.Id, true);
                h.Service.SetFavourite("steam:app:3", true);
                h.Service.SaveUiState(new UiState { ViewMode = ViewMode.Compact, Sort = SortMode.NameAscending });
                h.Service.Shutdown();

                h.ManualEnabled = false;
                var restarted = h.Create();
                await restarted.InitializeAsync();

                Assert.Equal(4, restarted.Items.Count);
                Assert.Equal(11m, restarted.Items[0].DisplayPrice);
                Assert.Contains(collection.Id, restarted.Items[0].CollectionIds);
                Assert.True(restarted.Items[2].IsFavourite);
                Assert.Equal(1, restarted.CountInCollection(collection.Id));
                Assert.Equal(ViewMode.Compact, restarted.GetUiState().ViewMode);
                restarted.Dispose();
            }
        }

        [Fact]
        public async Task Unavailable_provider_shows_no_entries_and_reports_state()
        {
            using (var h = new Harness())
            {
                h.ManualEnabled = false;
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);

                Assert.Empty(h.Service.Items);
                Assert.Equal(WishlistProviderState.Unavailable, h.Service.Status.ProviderState);
                Assert.Empty(h.Api.Batches);
            }
        }

        [Fact]
        public async Task Missing_api_key_reports_not_configured_without_requests()
        {
            using (var h = new Harness())
            {
                h.Options.HasApiKey = false;
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);

                Assert.Equal(ApiErrorKind.NotConfigured, h.Service.Status.LastError.Kind);
                Assert.Equal(4, h.Service.Items.Count);
                Assert.Empty(h.Api.Batches);
            }
        }

        [Fact]
        public async Task Match_override_and_collection_deletion_update_items()
        {
            using (var h = new Harness())
            {
                var game = new LibraryGameInfo { Id = Guid.NewGuid(), Name = "Game 1: Remastered", PluginId = Guid.NewGuid() };
                h.Library.Games.Add(game);
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);

                Assert.True(h.Service.Items[0].Match.IsPotential);
                h.Service.SetMatchOverride("steam:app:1", game.Id.ToString());
                Assert.True(h.Service.Items[0].IsOwned);

                var collection = h.Service.CreateCollection("RPG", null);
                h.Service.SetCollectionMembership(new[] { "steam:app:1", "steam:app:2" }, collection.Id, true);
                Assert.Equal(2, h.Service.CountInCollection(collection.Id));
                h.Service.DeleteCollection(collection.Id);
                Assert.Empty(h.Service.Items[0].CollectionIds);
            }
        }

        [Fact]
        public async Task Cover_override_is_applied_reset_and_survives_restart()
        {
            using (var h = new Harness())
            {
                await h.Service.InitializeAsync();
                await h.Service.RefreshAsync(RefreshTrigger.Manual);
                Assert.Null(h.Service.Items[0].CoverOverride);

                h.Service.SetCoverOverride("steam:app:1", "  https://example.com/cover.png ");
                Assert.Equal("https://example.com/cover.png", h.Service.Items[0].CoverOverride);
                h.Service.Shutdown();

                h.ManualEnabled = false;
                var restarted = h.Create();
                await restarted.InitializeAsync();
                Assert.Equal("https://example.com/cover.png", restarted.Items[0].CoverOverride);

                restarted.SetCoverOverride("steam:app:1", null);
                Assert.Null(restarted.Items[0].CoverOverride);
                restarted.Dispose();
            }
        }

        [Fact]
        public async Task Large_wishlist_refreshes_in_batches()
        {
            using (var h = new Harness())
            {
                h.ManualText = string.Join("\n", Enumerable.Range(1, 1000));
                await h.Service.InitializeAsync();
                var outcome = await h.Service.RefreshAsync(RefreshTrigger.Manual);

                Assert.Equal(RefreshOutcome.Completed, outcome);
                Assert.Equal(1000, h.Service.Items.Count);
                Assert.Equal(10, h.Api.Batches.Count);
                Assert.All(h.Api.Batches, b => Assert.True(b.Count <= 100));
            }
        }
    }
}
