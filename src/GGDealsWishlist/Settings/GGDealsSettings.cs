using System.Collections.Generic;
using GGDealsWishlist.Api;
using GGDealsWishlist.Models;
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

    /// <summary>
    /// Persisted extension settings (stored by Playnite). The API key is only ever stored DPAPI-encrypted in
    /// <see cref="ProtectedApiKey"/>; the plain key never appears in this object.
    /// </summary>
    public class GGDealsSettings
    {
        public const int CurrentVersion = 1;
        public const string DefaultAccentColor = "#FF8A3D";
        public const string DefaultWishlistUrl = "https://gg.deals/wishlist/";

        public int SettingsVersion { get; set; } = CurrentVersion;

        // Account / API
        public string ProtectedApiKey { get; set; }

        public string Region { get; set; } = Regions.DetectDefault();

        // Wishlist source
        public WishlistSourceMode WishlistSource { get; set; } = WishlistSourceMode.OfficialOnly;

        public string ManualWishlist { get; set; } = string.Empty;

        /// <summary>SteamID64 (or /profiles/ link) whose public wishlist is used in <see cref="WishlistSourceMode.SteamWishlist"/> mode.</summary>
        public string SteamId { get; set; } = string.Empty;

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
                HasApiKey = HasApiKey
            };
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
            SettingsVersion = CurrentVersion;
        }
    }
}
