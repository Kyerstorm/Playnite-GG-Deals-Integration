using System;
using System.Linq;
using GGDealsWishlist.Models;
using GGDealsWishlist.ViewModels;
using Xunit;

namespace GGDealsWishlist.Tests
{
    public sealed class StateGuidanceTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        private static StateGuidance For(SidebarState state, ApiErrorKind error = ApiErrorKind.None, DateTime? retryAfter = null, bool steam = false, string search = null, int filters = 0)
        {
            return StateGuidance.For(new StateGuidanceInput
            {
                State = state,
                ErrorKind = error,
                RetryAfterUtc = retryAfter,
                NowUtc = Now,
                IsSteamSource = steam,
                SearchText = search,
                FilterCount = filters
            });
        }

        private static string[] Kinds(StateGuidance guidance) => guidance.Actions.Select(a => a.Kind.ToString()).ToArray();

        [Theory]
        [InlineData(SidebarState.Loading)]
        [InlineData(SidebarState.NeedsApiKey)]
        [InlineData(SidebarState.Content)]
        public void States_with_their_own_layout_get_no_actions(SidebarState state)
        {
            var guidance = For(state);

            Assert.Empty(guidance.Actions);
            Assert.Null(guidance.Message);
        }

        [Theory]
        [InlineData(ApiErrorKind.InvalidApiKey)]
        [InlineData(ApiErrorKind.NotConfigured)]
        public void A_bad_or_missing_key_points_to_the_settings_instead_of_retrying(ApiErrorKind kind)
        {
            var guidance = For(SidebarState.Error, kind);

            Assert.Equal(new[] { "OpenSettings", "OpenApiInfo" }, Kinds(guidance));
            Assert.True(guidance.Actions[0].IsPrimary);
            Assert.Equal("Fix API key", guidance.Actions[0].Label);
        }

        [Fact]
        public void A_network_failure_offers_retry_first_and_settings_second()
        {
            var guidance = For(SidebarState.Error, ApiErrorKind.Network);

            Assert.Equal(new[] { "Refresh", "OpenSettings" }, Kinds(guidance));
            Assert.True(guidance.Actions[0].IsPrimary);
            Assert.True(guidance.Actions[0].IsEnabled);
            Assert.Equal("Retry", guidance.Actions[0].Label);
            Assert.False(guidance.NeedsCountdown);
        }

        [Fact]
        public void A_rate_limit_counts_down_in_seconds_and_stays_disabled()
        {
            var guidance = For(SidebarState.Error, ApiErrorKind.RateLimited, Now.AddSeconds(41.2));

            var retry = guidance.Actions.Single();
            Assert.Equal("Refresh", retry.Kind.ToString());
            Assert.Equal("Retry in 42 s", retry.Label);
            Assert.False(retry.IsEnabled);
            Assert.True(guidance.NeedsCountdown);
        }

        [Fact]
        public void A_longer_rate_limit_counts_down_in_whole_minutes_rounded_up()
        {
            var guidance = For(SidebarState.Error, ApiErrorKind.RateLimited, Now.AddMinutes(2).AddSeconds(5));

            Assert.Equal("Retry in 3 min", guidance.Actions.Single().Label);
        }

        [Fact]
        public void Once_the_rate_limit_has_passed_retry_is_available_again()
        {
            var guidance = For(SidebarState.Error, ApiErrorKind.RateLimited, Now.AddSeconds(-1));

            var retry = guidance.Actions.Single();
            Assert.Equal("Retry", retry.Label);
            Assert.True(retry.IsEnabled);
            Assert.False(guidance.NeedsCountdown);
        }

        [Fact]
        public void A_rate_limit_without_a_retry_time_can_be_retried_right_away()
        {
            var retry = For(SidebarState.Error, ApiErrorKind.RateLimited).Actions.Single();

            Assert.Equal("Retry", retry.Label);
            Assert.True(retry.IsEnabled);
        }

        [Fact]
        public void An_empty_steam_wishlist_points_at_the_steam_privacy_settings()
        {
            var guidance = For(SidebarState.EmptyWishlist, steam: true);

            Assert.Equal(new[] { "Refresh", "OpenSteamPrivacy", "OpenSettings" }, Kinds(guidance));
            Assert.Equal("Refresh", guidance.Actions[0].Label);
        }

        [Fact]
        public void An_empty_wishlist_from_another_source_offers_gg_deals_instead()
        {
            Assert.Equal(new[] { "Refresh", "OpenGGDeals", "OpenSettings" }, Kinds(For(SidebarState.EmptyWishlist, steam: false)));
        }

        [Fact]
        public void An_unavailable_wishlist_leads_with_the_most_useful_action_for_the_source()
        {
            Assert.Equal(new[] { "Refresh", "OpenSteamPrivacy", "OpenSettings" }, Kinds(For(SidebarState.WishlistUnavailable, steam: true)));
            Assert.Equal(new[] { "OpenGGDeals", "OpenSettings" }, Kinds(For(SidebarState.WishlistUnavailable, steam: false)));
        }

        [Fact]
        public void No_results_from_a_search_alone_offers_to_clear_the_search()
        {
            var guidance = For(SidebarState.NoResults, search: "hollow");

            Assert.Equal("No games match “hollow”.", guidance.Message);
            Assert.Equal(new[] { "ClearSearch" }, Kinds(guidance));
            Assert.True(guidance.Actions[0].IsPrimary);
        }

        [Fact]
        public void No_results_from_filters_alone_offers_to_clear_the_filters()
        {
            Assert.Equal("No games match your filter.", For(SidebarState.NoResults, filters: 1).Message);
            Assert.Equal("No games match your 3 filters.", For(SidebarState.NoResults, filters: 3).Message);
            Assert.Equal(new[] { "ClearFilters" }, Kinds(For(SidebarState.NoResults, filters: 3)));
        }

        [Fact]
        public void No_results_from_both_names_both_and_offers_each_clear_separately()
        {
            var guidance = For(SidebarState.NoResults, search: "hollow", filters: 2);

            Assert.Equal("No games match “hollow” with 2 filters applied.", guidance.Message);
            Assert.Equal(new[] { "ClearSearch", "ClearFilters" }, Kinds(guidance));
            Assert.True(guidance.Actions[0].IsPrimary);
            Assert.False(guidance.Actions[1].IsPrimary);
        }

        [Fact]
        public void A_whitespace_search_is_not_reported_as_a_search()
        {
            var guidance = For(SidebarState.NoResults, search: "   ", filters: 1);

            Assert.Equal(new[] { "ClearFilters" }, Kinds(guidance));
        }
    }
}
