using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace GGDealsWishlist.Models
{
    public enum SteamIdType
    {
        App = 0,
        Sub = 1,
        Bundle = 2
    }

    /// <summary>GG.deals distinguishes official retail stores from keyshops; the extension never merges the two silently.</summary>
    public enum PriceCategory
    {
        Retail = 0,
        Keyshop = 1
    }

    public enum PricePreference
    {
        Retail = 0,
        Keyshop = 1,
        LowestOfBoth = 2
    }

    public enum ViewMode
    {
        CoverInfo = 0,
        Compact = 1,
        List = 2,
        Grid = 3
    }

    public enum SortMode
    {
        WishlistOrder = 0,
        PriceAscending = 1,
        PriceDescending = 2,
        DiscountDescending = 3,
        DiscountAscending = 4,
        HistoricalLow = 5,
        RecentlyAdded = 6,
        RecentlyDiscounted = 7,
        NameAscending = 8,
        NameDescending = 9
    }

    public enum ApiErrorKind
    {
        None = 0,
        NotConfigured = 1,
        InvalidApiKey = 2,
        RateLimited = 3,
        Network = 4,
        Server = 5,
        UnexpectedResponse = 6,
        Cancelled = 7
    }

    /// <summary>Identifier understood by the GG.deals Prices API (endpoint type + numeric Steam id).</summary>
    public struct PriceLookupKey : IEquatable<PriceLookupKey>
    {
        public PriceLookupKey(SteamIdType type, long id)
        {
            Type = type;
            Id = id;
        }

        public SteamIdType Type { get; }

        public long Id { get; }

        public override string ToString()
        {
            var prefix = Type == SteamIdType.App ? "app" : Type == SteamIdType.Sub ? "sub" : "bundle";
            return prefix + ":" + Id.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string text, out PriceLookupKey key)
        {
            key = default(PriceLookupKey);
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var parts = text.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return false;
            }

            switch (parts[0])
            {
                case "app":
                    key = new PriceLookupKey(SteamIdType.App, id);
                    return true;
                case "sub":
                    key = new PriceLookupKey(SteamIdType.Sub, id);
                    return true;
                case "bundle":
                    key = new PriceLookupKey(SteamIdType.Bundle, id);
                    return true;
                default:
                    return false;
            }
        }

        public bool Equals(PriceLookupKey other) => Type == other.Type && Id == other.Id;

        public override bool Equals(object obj) => obj is PriceLookupKey other && Equals(other);

        public override int GetHashCode() => ((int)Type * 397) ^ Id.GetHashCode();
    }

    /// <summary>A single store offer. Only populated when a provider genuinely returns per-store data.</summary>
    public sealed class StoreOffer
    {
        public string StoreName { get; set; }

        public PriceCategory Category { get; set; }

        public decimal? Price { get; set; }

        public string Url { get; set; }
    }

    /// <summary>
    /// Cached price information for one lookup key. The first block mirrors the documented GG.deals Prices API
    /// fields exactly; the optional block is only filled by providers that actually return such data
    /// (the current Prices API does not), so the UI never displays inferred values.
    /// </summary>
    public sealed class PriceData
    {
        public string LookupKey { get; set; }

        /// <summary>False when GG.deals returned null for the id (game not in its database).</summary>
        public bool Found { get; set; }

        public string GGDealsTitle { get; set; }

        public string GGDealsUrl { get; set; }

        public decimal? CurrentRetail { get; set; }

        public decimal? CurrentKeyshops { get; set; }

        public decimal? HistoricalRetail { get; set; }

        public decimal? HistoricalKeyshops { get; set; }

        public string Currency { get; set; }

        public string Region { get; set; }

        public DateTime FetchedUtc { get; set; }

        public decimal? RegularPrice { get; set; }

        public int? DiscountPercent { get; set; }

        public DateTime? DiscountStartedUtc { get; set; }

        public string CheapestStoreName { get; set; }

        public string CheapestStoreUrl { get; set; }

        public int? StoreCount { get; set; }

        public List<StoreOffer> Offers { get; set; }
    }

    /// <summary>A wishlist entry as delivered by a wishlist provider. Persisted in the cache.</summary>
    public sealed class WishlistEntry
    {
        public string Key { get; set; }

        public string ProviderId { get; set; }

        public string GGDealsId { get; set; }

        public long? SteamId { get; set; }

        public SteamIdType SteamIdType { get; set; }

        public string Title { get; set; }

        public string GGDealsUrl { get; set; }

        public int? WishlistPosition { get; set; }

        public DateTime? AddedUtc { get; set; }

        public double? Rating { get; set; }

        public string Platform { get; set; }

        public DateTime? ReleaseDate { get; set; }

        public Dictionary<string, string> Metadata { get; set; }

        [ScriptIgnore]
        public PriceLookupKey? PriceKey => SteamId.HasValue && SteamId.Value > 0
            ? new PriceLookupKey(SteamIdType, SteamId.Value)
            : (PriceLookupKey?)null;

        [ScriptIgnore]
        public long? SteamAppId => SteamIdType == SteamIdType.App ? SteamId : null;

        /// <summary>Builds a stable identity used for favourites, collections and match overrides.</summary>
        public static string BuildKey(string ggDealsId, SteamIdType type, long? steamId, string url)
        {
            if (steamId.HasValue && steamId.Value > 0)
            {
                return "steam:" + new PriceLookupKey(type, steamId.Value);
            }

            if (!string.IsNullOrWhiteSpace(ggDealsId))
            {
                return "ggdeals:" + ggDealsId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                return "url:" + url.Trim().TrimEnd('/').ToLowerInvariant();
            }

            return null;
        }
    }

    /// <summary>Minimal projection of a Playnite game. Avoids holding or duplicating full Playnite objects.</summary>
    public sealed class LibraryGameInfo
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public Guid PluginId { get; set; }

        public string GameId { get; set; }

        public bool IsInstalled { get; set; }

        public bool Hidden { get; set; }

        public List<string> LinkUrls { get; set; } = new List<string>();

        public string CoverPath { get; set; }

        public string Developers { get; set; }

        public string Publishers { get; set; }

        public string Platforms { get; set; }

        public DateTime? ReleaseDate { get; set; }

        public string SourceName { get; set; }
    }

    public enum MatchKind
    {
        None = 0,
        SteamAppId = 1,
        StoreLink = 2,
        ExactTitle = 3,
        UserConfirmed = 4,
        PotentialTitle = 5,
        UserRejected = 6
    }

    public sealed class MatchResult
    {
        public static readonly MatchResult NoMatch = new MatchResult(MatchKind.None, null, 0);

        public MatchResult(MatchKind kind, LibraryGameInfo game, int candidateCount)
        {
            Kind = kind;
            Game = game;
            CandidateCount = candidateCount;
        }

        public MatchKind Kind { get; }

        /// <summary>The owned game, or for a potential match the best candidate.</summary>
        public LibraryGameInfo Game { get; }

        public int CandidateCount { get; }

        /// <summary>Owned = exists anywhere in the Playnite library via a reliable match. Installation is irrelevant.</summary>
        public bool IsOwned => Game != null && (Kind == MatchKind.SteamAppId || Kind == MatchKind.StoreLink || Kind == MatchKind.ExactTitle || Kind == MatchKind.UserConfirmed);

        public bool IsPotential => Kind == MatchKind.PotentialTitle && Game != null;

        public bool IsInstalled => IsOwned && Game.IsInstalled;
    }

    /// <summary>A user-defined local collection (never synchronised to GG.deals).</summary>
    public sealed class WishlistCollection
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string Icon { get; set; }

        public int Order { get; set; }
    }

    /// <summary>
    /// Fully resolved wishlist row consumed by the filter pipeline and UI. Built in memory from cached
    /// provider data, price data, Playnite matching and local organisation state; never persisted itself.
    /// </summary>
    public sealed class WishlistItem
    {
        public string Key { get; set; }

        public WishlistEntry Entry { get; set; }

        public PriceData Price { get; set; }

        /// <summary>Prices this extension recorded across refreshes; null until a price has been fetched.</summary>
        public PriceSeries PriceHistory { get; set; }

        public MatchResult Match { get; set; } = MatchResult.NoMatch;

        public string Title { get; set; }

        public string GGDealsUrl { get; set; }

        public bool IsFavourite { get; set; }

        public IReadOnlyList<string> CollectionIds { get; set; } = new string[0];

        public decimal? DisplayPrice { get; set; }

        public PriceCategory? DisplayCategory { get; set; }

        public decimal? DisplayHistoricalLow { get; set; }

        public bool IsHistoricalLow { get; set; }

        public int? DiscountPercent { get; set; }

        public bool IsOnSale => DiscountPercent.HasValue && DiscountPercent.Value > 0;

        public string Currency { get; set; }

        public DateTime? LastPriceUpdateUtc { get; set; }

        public bool IsPriceStale { get; set; }

        public bool IsOwned => Match != null && Match.IsOwned;

        public bool IsInstalled => Match != null && Match.IsInstalled;

        public Guid? PlayniteGameId => IsOwned ? Match.Game.Id : (Guid?)null;

        /// <summary>Lower-cased title used by local search.</summary>
        public string SearchTitle { get; set; }

        /// <summary>Lower-cased developer/publisher/store text used when extended search is enabled.</summary>
        public string SearchExtra { get; set; }
    }
}
