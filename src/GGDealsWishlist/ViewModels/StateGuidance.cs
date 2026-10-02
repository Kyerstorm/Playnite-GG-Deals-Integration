using System;
using System.Collections.Generic;
using System.Globalization;
using GGDealsWishlist.Models;

namespace GGDealsWishlist.ViewModels
{
    public enum StateActionKind
    {
        Refresh,
        OpenSettings,
        OpenApiInfo,
        OpenGGDeals,
        OpenSteamPrivacy,
        ClearSearch,
        ClearFilters
    }

    /// <summary>One button on an empty or error card.</summary>
    public sealed class StateAction
    {
        public StateAction(StateActionKind kind, string label, bool isPrimary = false, bool isEnabled = true)
        {
            Kind = kind;
            Label = label;
            IsPrimary = isPrimary;
            IsEnabled = isEnabled;
        }

        public StateActionKind Kind { get; }

        public string Label { get; }

        public bool IsPrimary { get; }

        public bool IsEnabled { get; }
    }

    /// <summary>Everything <see cref="StateGuidance"/> needs to decide what to offer; plain values so it is easy to test.</summary>
    public sealed class StateGuidanceInput
    {
        public SidebarState State { get; set; }

        public ApiErrorKind ErrorKind { get; set; }

        public DateTime? RetryAfterUtc { get; set; }

        public DateTime NowUtc { get; set; }

        public bool IsSteamSource { get; set; }

        public string SearchText { get; set; }

        /// <summary>Number of active filters, not counting the search box.</summary>
        public int FilterCount { get; set; }
    }

    /// <summary>
    /// What a non-content sidebar state should say and offer. Pure logic: the view model supplies the current facts
    /// and the card simply renders <see cref="Actions"/>.
    /// </summary>
    public sealed class StateGuidance
    {
        public const string SteamPrivacyUrl = "https://steamcommunity.com/my/edit/settings";

        private static readonly IReadOnlyList<StateAction> NoActions = new StateAction[0];

        private StateGuidance(string message, IReadOnlyList<StateAction> actions, bool needsCountdown = false)
        {
            Message = message;
            Actions = actions;
            NeedsCountdown = needsCountdown;
        }

        /// <summary>Replaces the card's usual text when set (used for "no results"); null keeps the existing text.</summary>
        public string Message { get; }

        public IReadOnlyList<StateAction> Actions { get; }

        /// <summary>True while a retry label is counting down, so the owner should re-evaluate once a second.</summary>
        public bool NeedsCountdown { get; }

        public static StateGuidance For(StateGuidanceInput input)
        {
            switch (input.State)
            {
                case SidebarState.Error:
                    return ForError(input);
                case SidebarState.EmptyWishlist:
                    return new StateGuidance(null, new[]
                    {
                        new StateAction(StateActionKind.Refresh, "Refresh", isPrimary: true),
                        input.IsSteamSource ? SteamPrivacy() : GGDeals(),
                        WishlistSettings()
                    });
                case SidebarState.WishlistUnavailable:
                    return input.IsSteamSource
                        ? new StateGuidance(null, new[] { new StateAction(StateActionKind.Refresh, "Refresh", isPrimary: true), SteamPrivacy(), WishlistSettings() })
                        : new StateGuidance(null, new[] { new StateAction(StateActionKind.OpenGGDeals, "Open GG.deals", isPrimary: true), WishlistSettings() });
                case SidebarState.NoResults:
                    return ForNoResults(input);
                default:
                    return new StateGuidance(null, NoActions);
            }
        }

        private static StateGuidance ForError(StateGuidanceInput input)
        {
            if (input.ErrorKind == ApiErrorKind.InvalidApiKey || input.ErrorKind == ApiErrorKind.NotConfigured)
            {
                // Retrying cannot help until the key changes.
                return new StateGuidance(null, new[]
                {
                    new StateAction(StateActionKind.OpenSettings, "Fix API key", isPrimary: true),
                    new StateAction(StateActionKind.OpenApiInfo, "Learn more")
                });
            }

            if (input.ErrorKind == ApiErrorKind.RateLimited)
            {
                var remaining = input.RetryAfterUtc.HasValue ? (input.RetryAfterUtc.Value - input.NowUtc).TotalSeconds : 0;
                if (remaining > 0)
                {
                    return new StateGuidance(null, new[] { new StateAction(StateActionKind.Refresh, RetryIn(remaining), isPrimary: true, isEnabled: false) }, needsCountdown: true);
                }

                return new StateGuidance(null, new[] { new StateAction(StateActionKind.Refresh, "Retry", isPrimary: true) });
            }

            return new StateGuidance(null, new[]
            {
                new StateAction(StateActionKind.Refresh, "Retry", isPrimary: true),
                new StateAction(StateActionKind.OpenSettings, "Open settings")
            });
        }

        private static StateGuidance ForNoResults(StateGuidanceInput input)
        {
            var search = input.SearchText?.Trim();
            var hasSearch = !string.IsNullOrEmpty(search);
            var filters = Math.Max(0, input.FilterCount);
            var hasFilters = filters > 0;

            string message;
            if (hasSearch && hasFilters)
            {
                message = "No games match “" + search + "” with " + Filters(filters) + " applied.";
            }
            else if (hasSearch)
            {
                message = "No games match “" + search + "”.";
            }
            else
            {
                message = "No games match your " + (filters == 1 ? "filter" : Filters(filters)) + ".";
            }

            var actions = new List<StateAction>();
            if (hasSearch)
            {
                actions.Add(new StateAction(StateActionKind.ClearSearch, "Clear search", isPrimary: true));
            }

            if (hasFilters)
            {
                actions.Add(new StateAction(StateActionKind.ClearFilters, "Clear filters", isPrimary: !hasSearch));
            }

            return new StateGuidance(message, actions);
        }

        private static string Filters(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " filter" : " filters");

        private static string RetryIn(double seconds)
        {
            return seconds < 60
                ? "Retry in " + Math.Ceiling(seconds).ToString(CultureInfo.InvariantCulture) + " s"
                : "Retry in " + Math.Ceiling(seconds / 60).ToString(CultureInfo.InvariantCulture) + " min";
        }

        private static StateAction SteamPrivacy() => new StateAction(StateActionKind.OpenSteamPrivacy, "Steam privacy settings");

        private static StateAction GGDeals() => new StateAction(StateActionKind.OpenGGDeals, "Open GG.deals");

        private static StateAction WishlistSettings() => new StateAction(StateActionKind.OpenSettings, "Wishlist settings");
    }
}
