using System.Collections.Generic;
using System.Linq;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;
using GGDealsWishlist.Settings;
using GGDealsWishlist.ViewModels;
using Xunit;

namespace GGDealsWishlist.Tests
{
    public class SettingsTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("short")]
        [InlineData("has space inside-key")]
        [InlineData("tab\tinside-the-key")]
        [InlineData("ünicode-key-value")]
        public void ValidateKey_RejectsMalformedKeys(string key)
        {
            Assert.NotNull(GGDealsSettingsViewModel.ValidateKey(key));
        }

        [Fact]
        public void ValidateKey_AcceptsPlausibleKey()
        {
            Assert.Null(GGDealsSettingsViewModel.ValidateKey("AbC123-def456_ghi789"));
        }

        [Fact]
        public void ValidationMessages_NeverContainTheKey()
        {
            const string key = "secret key with spaces 123";
            Assert.DoesNotContain(key, GGDealsSettingsViewModel.ValidateKey(key));
        }

        [Fact]
        public void Clone_CopiesPersistedValuesIndependently()
        {
            var original = new GGDealsSettings
            {
                ProtectedApiKey = "blob",
                Region = "de",
                RefreshIntervalMinutes = 180,
                DefaultViewMode = ViewMode.List,
                HideOwned = true,
                AccentColor = "#112233"
            };

            var copy = original.Clone();
            copy.Region = "us";

            Assert.Equal("blob", copy.ProtectedApiKey);
            Assert.Equal(180, copy.RefreshIntervalMinutes);
            Assert.Equal(ViewMode.List, copy.DefaultViewMode);
            Assert.True(copy.HideOwned);
            Assert.Equal("#112233", copy.AccentColor);
            Assert.Equal("de", original.Region);
        }

        [Fact]
        public void Normalize_RepairsOutOfRangeValues()
        {
            var settings = new GGDealsSettings { Region = "xx", RefreshIntervalMinutes = 1, AccentColor = " ", GGDealsWishlistUrl = null, ManualWishlist = null };
            settings.Normalize();

            Assert.NotEqual("xx", settings.Region);
            Assert.Equal(15, settings.RefreshIntervalMinutes);
            Assert.Equal(GGDealsSettings.DefaultAccentColor, settings.AccentColor);
            Assert.Equal(GGDealsSettings.DefaultWishlistUrl, settings.GGDealsWishlistUrl);
            Assert.Equal(string.Empty, settings.ManualWishlist);
        }

        [Fact]
        public void A_settings_file_from_before_other_wishlists_loads_with_none()
        {
            var settings = new GGDealsSettings { SettingsVersion = 2, OtherWishlists = null };
            settings.Normalize();

            Assert.Empty(settings.OtherWishlists);
            Assert.Equal(GGDealsSettings.CurrentVersion, settings.SettingsVersion);
        }

        [Fact]
        public void Normalize_drops_unusable_repeated_and_own_wishlists_and_fills_in_names_and_ids()
        {
            var settings = new GGDealsSettings
            {
                SteamId = "76561198000000001",
                OtherWishlists = new List<SavedWishlist>
                {
                    new SavedWishlist { Id = "a", Name = "  Alex ", SteamId = "https://steamcommunity.com/profiles/76561198000000002" },
                    new SavedWishlist { Id = "b", Name = "Alex again", SteamId = "76561198000000002" },
                    new SavedWishlist { Id = "c", Name = "Me", SteamId = "76561198000000001" },
                    new SavedWishlist { Id = "d", Name = "Vanity", SteamId = "https://steamcommunity.com/id/someone" },
                    new SavedWishlist { Id = "e", Name = "Empty", SteamId = "" },
                    new SavedWishlist { Id = "", Name = " ", SteamId = "76561198000000003" },
                    null
                }
            };

            settings.Normalize();

            Assert.Equal(2, settings.OtherWishlists.Count);
            Assert.Equal("a", settings.OtherWishlists[0].Id);
            Assert.Equal("Alex", settings.OtherWishlists[0].Name);
            Assert.Equal("76561198000000002", settings.OtherWishlists[0].SteamId);

            // A nameless wishlist is shown by its SteamID, and a missing scope id is generated once.
            Assert.Equal("76561198000000003", settings.OtherWishlists[1].Name);
            Assert.False(string.IsNullOrEmpty(settings.OtherWishlists[1].Id));
        }

        [Fact]
        public void Normalize_never_lets_a_saved_wishlist_reuse_the_own_wishlists_scope_id()
        {
            var settings = new GGDealsSettings
            {
                OtherWishlists = new List<SavedWishlist> { new SavedWishlist { Id = WishlistScope.OwnId, Name = "Sneaky", SteamId = "76561198000000002" } }
            };

            settings.Normalize();

            Assert.NotEqual(WishlistScope.OwnId, settings.OtherWishlists.Single().Id);
        }

        [Fact]
        public void Clone_copies_the_saved_wishlists_independently()
        {
            var original = new GGDealsSettings { OtherWishlists = new List<SavedWishlist> { new SavedWishlist { Id = "a", Name = "Alex", SteamId = "76561198000000002" } } };

            var copy = original.Clone();
            copy.OtherWishlists[0].Name = "Changed";
            copy.OtherWishlists.Add(new SavedWishlist { Id = "b" });

            Assert.Equal("Alex", original.OtherWishlists.Single().Name);
            Assert.Single(original.OtherWishlists);
        }

        [Fact]
        public void The_service_options_list_the_own_wishlist_first_then_the_saved_ones_in_order()
        {
            var settings = new GGDealsSettings
            {
                OtherWishlists = new List<SavedWishlist>
                {
                    new SavedWishlist { Id = "a", Name = "Alex", SteamId = "76561198000000002" },
                    new SavedWishlist { Id = "b", Name = "Sam", SteamId = "76561198000000003" }
                }
            };

            var scopes = settings.ToServiceOptions().Wishlists;

            Assert.Equal(new[] { WishlistScope.OwnId, "a", "b" }, scopes.Select(s => s.Id));
            Assert.True(scopes[0].IsOwn);
            Assert.Equal("76561198000000003", scopes[2].SteamId);
        }
    }

    public class WishlistScopeTests
    {
        [Fact]
        public void The_own_wishlist_follows_the_source_chosen_in_settings()
        {
            var own = WishlistScope.Own();

            Assert.True(own.UsesSteam(ownSourceIsSteam: true));
            Assert.False(own.UsesSteam(ownSourceIsSteam: false));
            Assert.True(own.UsesManualList(ownSourceIsManual: true));
            Assert.Equal("111", own.SteamIdFor("111"));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void Another_persons_wishlist_is_always_their_public_steam_list_whatever_the_own_source_is(bool ownIsSteam, bool ownIsManual)
        {
            var friend = new WishlistScope { Id = "f", Name = "Alex", SteamId = "222" };

            Assert.True(friend.UsesSteam(ownIsSteam));
            Assert.False(friend.UsesManualList(ownIsManual));
            Assert.Equal("222", friend.SteamIdFor("111"));
        }

        [Fact]
        public void The_wishlist_check_says_whose_wishlist_it_looked_at()
        {
            var oneGame = new WishlistFetchResult { State = WishlistProviderState.Available, Entries = { new WishlistEntry { Key = "steam:app:1" } } };
            var noGames = new WishlistFetchResult { State = WishlistProviderState.Available };

            var found = SteamCheckResult.From(oneGame, "Alex");
            Assert.True(found.IsOk);
            Assert.Contains("Alex's Steam wishlist", found.Message);
            Assert.DoesNotContain("your", found.Message);

            var empty = SteamCheckResult.From(noGames, "Alex");
            Assert.False(empty.IsOk);
            Assert.Contains("Alex's Steam wishlist", empty.Message);
            Assert.DoesNotContain("your", empty.Message);

            // The setup wizard still talks about the user's own wishlist.
            Assert.Contains("your Steam wishlist", SteamCheckResult.From(oneGame).Message);
        }
    }
}
