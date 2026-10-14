using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GGDealsWishlist.Api;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;
using GGDealsWishlist.Services;

namespace GGDealsWishlist.Settings
{
    public enum WishlistSourceMode
    {
        /// <summary>Official GG.deals wishlist access only (currently unavailable through the API).</summary>
        OfficialOnly = 0,

        /// <summary>Use a manual list of Steam ids while no official wishlist endpoint exists.</summary>
        ManualList = 1,

        /// <summary>Read the user's public Steam wishlist through Steam's official Web API.</summary>
        SteamWishlist = 2
    }

    public enum ThemeMode
    {
        Inherit = 0,
        Light = 1,
        Dark = 2
    }

    public enum CardDensity
    {
        Comfortable = 0,
        Compact = 1
    }

    public enum CoverSize
    {
        Small = 0,
        Medium = 1,
        Large = 2
    }

    /// <summary>Another person's public Steam wishlist the user can switch to. Never combined with any other list.</summary>
    public class SavedWishlist
    {
        /// <summary>Stable scope id (a GUID). All data stored for this wishlist is keyed by it, so it never changes.</summary>
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>SteamID64 (a /profiles/ link is accepted while typing and normalised on save).</summary>
        public string SteamId { get; set; } = string.Empty;

        public SavedWishlist Clone() => new SavedWishlist { Id = Id, Name = Name, SteamId = SteamId };
    }

    /// <summary>
    /// Persisted extension settings (stored by Playnite). The API key is only ever stored DPAPI-encrypted in
    /// <see cref="ProtectedApiKey"/>; the plain key never appears in this object.
    /// </summary>
    public class GGDealsSettings
    {
        public const int CurrentVersion = 3;
        public const string DefaultAccentColor = "#FF8A3D";
        public const string DefaultWishlistUrl = "https://gg.deals/wishlist/";

        public int SettingsVersion { get; set; } = CurrentVersion;

        /// <summary>True once the first-run setup wizard was finished (or the user already had a key when it was introduced).</summary>
        public bool SetupCompleted { get; set; }

        /// <summary>True once the wizard has been opened automatically, so closing it early never brings it back uninvited.</summary>
        public bool SetupPromptShown { get; set; }

        // Account / API
        public string ProtectedApiKey { get; set; }

        public string Region { get; set; } = Regions.DetectDefault();

        // Wishlist source
        public WishlistSourceMode WishlistSource { get; set; } = WishlistSourceMode.OfficialOnly;

        public string ManualWishlist { get; set; } = string.Empty;

        /// <summary>SteamID64 (or /profiles/ link) whose public wishlist is used in <see cref="WishlistSourceMode.SteamWishlist"/> mode.</summary>
        public string SteamId { get; set; } = string.Empty;

        /// <summary>Other people's Steam wishlists the sidebar can switch to (version 3). Always public Steam lists.</summary>
        public List<SavedWishlist> OtherWishlists { get; set; } = new List<SavedWishlist>();

        public string GGDealsWishlistUrl { get; set; } = DefaultWishlistUrl;

        // Refresh
        public bool AutoRefresh { get; set; } = true;

        public int RefreshIntervalMinutes { get; set; } = 60;

        // Appearance
        public ViewMode DefaultViewMode { get; set; } = ViewMode.CoverInfo;

        public ThemeMode Theme { get; set; } = ThemeMode.Inherit;

        public CardDensity Density { get; set; } = CardDensity.Comfortable;

        public CoverSize CoverSize { get; set; } = CoverSize.Medium;

        public string AccentColor { get; set; } = DefaultAccentColor;

        /// <summary>Optional SteamGridDB API key; when set, it supplies covers for games Steam has no artwork for.</summary>
        public string SteamGridDbApiKey { get; set; } = string.Empty;

        // Header information
        public bool ShowGameCount { get; set; } = true;

        public bool ShowOnSaleCount { get; set; } = true;

        public bool ShowHistoricalLowCount { get; set; } = true;

        public bool ShowLastUpdated { get; set; } = true;

        // Card / detail information fields
        public bool ShowCover { get; set; } = true;

        public bool ShowTitle { get; set; } = true;

        public bool ShowCurrentPrice { get; set; } = true;

        public bool ShowDiscount { get; set; } = true;

        public bool ShowPriceSource { get; set; } = true;

        public bool ShowHistoricalLow { get; set; } = true;

        public bool ShowRetailPrice { get; set; } = true;

        public bool ShowKeyshopPrice { get; set; } = true;

        public bool ShowWishlistDate { get; set; } = true;

        public bool ShowRating { get; set; } = true;

        public bool ShowPlatform { get; set; } = true;

        public bool ShowReleaseDate { get; set; } = true;

        public bool ShowStoreCount { get; set; } = true;

        public bool ShowFavouriteStar { get; set; } = true;

        public bool ShowCollections { get; set; } = true;

        /// <summary>Price trend sparkline on cards and the recorded-price chart in the expanded card.</summary>
        public bool ShowPriceHistory { get; set; } = true;

        // Prices
        public PricePreference PricePreference { get; set; } = PricePreference.Retail;

        public bool ShowHistoricalLowBadge { get; set; } = true;

        // Playnite
        public bool HideOwned { get; set; }

        public bool ShowOwnership { get; set; } = true;

        public bool ShowInstalledStatus { get; set; } = true;

        public bool EnableMatching { get; set; } = true;

        public bool TreatExactTitleAsOwned { get; set; } = true;

        public bool SearchIncludesExtra { get; set; }

        public static readonly IReadOnlyList<int> RefreshIntervals = new[] { 15, 30, 60, 180, 360, 720, 1440 };

        public bool HasApiKey => !string.IsNullOrEmpty(ProtectedApiKey);

        /// <summary>Deep copy used for the settings edit transaction (BeginEdit / CancelEdit).</summary>
        public GGDealsSettings Clone()
        {
            var copy = new GGDealsSettings();
            copy.CopyFrom(this);
            return copy;
        }

        /// <summary>Copies every persisted (read/write) property from <paramref name="other"/>.</summary>
        public void CopyFrom(GGDealsSettings other)
        {
            if (other == null)
            {
                return;
            }

            foreach (var property in typeof(GGDealsSettings).GetProperties())
            {
                if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                {
                    property.SetValue(this, property.GetValue(other));
                }
            }

            // The loop above copies the list by reference; the edit transaction needs an independent copy.
            OtherWishlists = other.OtherWishlists?.Select(w => w?.Clone()).Where(w => w != null).ToList() ?? new List<SavedWishlist>();
        }

        public ServiceOptions ToServiceOptions()
        {
            return new ServiceOptions
            {
                Region = Regions.IsValid(Region) ? Region.ToLowerInvariant() : Regions.DefaultCode,
                PricePreference = PricePreference,
                RefreshIntervalMinutes = RefreshIntervalMinutes,
                AutoRefresh = AutoRefresh,
                EnableMatching = EnableMatching,
                TreatExactTitleAsOwned = TreatExactTitleAsOwned,
                HasApiKey = HasApiKey,
                Wishlists = WishlistScope.Build(OtherWishlists?
                    .Where(w => w != null && !string.IsNullOrEmpty(w.Id))
                    .Select(w => new WishlistScope { Id = w.Id, Name = w.Name, SteamId = w.SteamId }))
            };
        }

        /// <summary>
        /// Cleans the saved wishlists: drops entries with an unusable SteamID, repeated IDs and the user's own ID,
        /// and fills in missing names and scope ids. Valid links are stored as the plain SteamID64.
        /// </summary>
        private static List<SavedWishlist> NormalizeOtherWishlists(IEnumerable<SavedWishlist> saved, string ownSteamId)
        {
            var result = new List<SavedWishlist>();
            var seen = new HashSet<ulong>();
            if (SteamIdParser.TryParse(ownSteamId, out var own))
            {
                seen.Add(own);
            }

            var usedScopeIds = new HashSet<string> { WishlistScope.OwnId };
            foreach (var wishlist in saved ?? Enumerable.Empty<SavedWishlist>())
            {
                if (wishlist == null || !SteamIdParser.TryParse(wishlist.SteamId, out var steamId) || !seen.Add(steamId))
                {
                    continue;
                }

                var canonical = steamId.ToString(CultureInfo.InvariantCulture);
                var id = string.IsNullOrWhiteSpace(wishlist.Id) ? System.Guid.NewGuid().ToString("N") : wishlist.Id.Trim();
                if (!usedScopeIds.Add(id))
                {
                    id = System.Guid.NewGuid().ToString("N");
                    usedScopeIds.Add(id);
                }

                var name = wishlist.Name?.Trim();
                result.Add(new SavedWishlist { Id = id, Name = string.IsNullOrEmpty(name) ? canonical : name, SteamId = canonical });
            }

            return result;
        }

        /// <summary>Repairs out-of-range values from hand-edited or older settings files.</summary>
        public void Normalize()
        {
            if (!Regions.IsValid(Region))
            {
                Region = Regions.DetectDefault();
            }

            var interval = RefreshIntervalMinutes;
            if (interval < 15)
            {
                RefreshIntervalMinutes = 15;
            }
            else if (interval > 1440)
            {
                RefreshIntervalMinutes = 1440;
            }

            if (string.IsNullOrWhiteSpace(AccentColor))
            {
                AccentColor = DefaultAccentColor;
            }

            if (string.IsNullOrWhiteSpace(GGDealsWishlistUrl))
            {
                GGDealsWishlistUrl = DefaultWishlistUrl;
            }

            ManualWishlist = ManualWishlist ?? string.Empty;
            SteamId = SteamId?.Trim() ?? string.Empty;
            OtherWishlists = NormalizeOtherWishlists(OtherWishlists, SteamId);

            // Version 2 introduced the setup wizard: anyone who had already entered a key was set up long ago.
            if (SettingsVersion < 2 && HasApiKey)
            {
                SetupCompleted = true;
            }

            SettingsVersion = CurrentVersion;
        }
    }
}
