using System;
using System.Collections.Generic;

namespace GGDealsWishlist.Models
{
    /// <summary>
    /// One wishlist the user can switch to. Wishlists are never combined: everything that belongs to a wishlist
    /// (its entries, favourites, collections and refresh state) is stored under its scope id.
    /// </summary>
    public sealed class WishlistScope
    {
        /// <summary>Scope id of the user's own wishlist. Stable forever: persisted data is keyed by it.</summary>
        public const string OwnId = "own";

        public const string OwnName = "My wishlist";

        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>Canonical SteamID64 of another person's wishlist; null for the user's own wishlist.</summary>
        public string SteamId { get; set; }

        public bool IsOwn => string.Equals(Id, OwnId, StringComparison.Ordinal);

        public static WishlistScope Own() => new WishlistScope { Id = OwnId, Name = OwnName };

        // Which source reads this wishlist. The user's own wishlist follows the source chosen in settings; another
        // person's wishlist is always their public Steam wishlist, whatever the user's own source is.

        public bool UsesSteam(bool ownSourceIsSteam) => !IsOwn || ownSourceIsSteam;

        public bool UsesManualList(bool ownSourceIsManual) => IsOwn && ownSourceIsManual;

        /// <summary>The SteamID to read: the user's configured one for their own wishlist, otherwise this wishlist's.</summary>
        public string SteamIdFor(string ownSteamId) => IsOwn ? ownSteamId : SteamId;

        /// <summary>The user's own wishlist first, followed by the saved ones in the order given.</summary>
        public static List<WishlistScope> Build(IEnumerable<WishlistScope> others)
        {
            var list = new List<WishlistScope> { Own() };
            if (others != null)
            {
                list.AddRange(others);
            }

            return list;
        }
    }
}
