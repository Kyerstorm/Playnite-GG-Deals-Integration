using System;
using System.Collections.Generic;
using System.Linq;
using GGDealsWishlist.Models;

namespace GGDealsWishlist.Querying
{
    public enum PriceFilter
    {
        Any = 0,
        Under5 = 1,
        Under10 = 2,
        Under20 = 3,
        Custom = 4
    }

    /// <summary>GG.deals price categories exposed by the API.</summary>
    public enum StoreFilter
    {
        Any = 0,
        Retail = 1,
        Keyshop = 2
    }

    public enum SaleStatusFilter
    {
        All = 0,
        OnSale = 1,
        NotOnSale = 2
    }

    public enum OwnershipFilter
    {
        All = 0,
        Owned = 1,
        NotOwned = 2
    }

    public enum FavouriteFilter
    {
        All = 0,
        Favourites = 1,
        NonFavourites = 2
    }

    public enum QuickFilter
    {
        All = 0,
        OnSale = 1,
        HistoricalLow = 2
    }

    /// <summary>Filter selections. Persisted with the UI state so they survive restarts.</summary>
    public sealed class FilterState
    {
        public PriceFilter Price { get; set; }

        public decimal? CustomMaxPrice { get; set; }

        public int MinDiscount { get; set; }

        public StoreFilter Store { get; set; }

        public SaleStatusFilter SaleStatus { get; set; }

        public bool HistoricalLowOnly { get; set; }

        public OwnershipFilter Ownership { get; set; }

        public FavouriteFilter Favourites { get; set; }

        public List<string> CollectionIds { get; set; } = new List<string>();

        public string SearchText { get; set; }

        public QuickFilter Quick => HistoricalLowOnly ? QuickFilter.HistoricalLow : SaleStatus == SaleStatusFilter.OnSale ? QuickFilter.OnSale : QuickFilter.All;

        /// <summary>Number of active filters, excluding search (shown on the filter button).</summary>
        public int ActiveCount =>
            (Price != PriceFilter.Any ? 1 : 0)
            + (MinDiscount > 0 ? 1 : 0)
            + (Store != StoreFilter.Any ? 1 : 0)
            + (SaleStatus != SaleStatusFilter.All ? 1 : 0)
            + (HistoricalLowOnly ? 1 : 0)
            + (Ownership != OwnershipFilter.All ? 1 : 0)
            + (Favourites != FavouriteFilter.All ? 1 : 0)
            + ((CollectionIds?.Count ?? 0) > 0 ? 1 : 0);

        public FilterState Clone()
        {
            var copy = (FilterState)MemberwiseClone();
            copy.CollectionIds = new List<string>(CollectionIds ?? new List<string>());
            return copy;
        }

        public decimal? MaxPrice
        {
            get
            {
                switch (Price)
                {
                    case PriceFilter.Under5:
                        return 5m;
                    case PriceFilter.Under10:
                        return 10m;
                    case PriceFilter.Under20:
                        return 20m;
                    case PriceFilter.Custom:
                        return CustomMaxPrice;
                    default:
                        return null;
                }
            }
        }
    }

    public sealed class QueryOptions
    {
        /// <summary>"Hide games already owned" (Playnite settings). An explicit "Owned" ownership filter overrides it.</summary>
        public bool HideOwned { get; set; }

        /// <summary>Include developer/publisher/store text in local search.</summary>
        public bool SearchIncludesExtra { get; set; }
    }

    public sealed class WishlistSummary
    {
        public int Total { get; set; }

        public int OnSale { get; set; }

        public int HistoricalLows { get; set; }

        public int Owned { get; set; }

        /// <summary>False when no provider supplied discount data, so "on sale" cannot be determined.</summary>
        public bool HasDiscountData { get; set; }

        public bool HasOrderData { get; set; }

        public bool HasAddedDates { get; set; }

        public bool HasDiscountDates { get; set; }
    }

    /// <summary>
    /// Local search/filter/sort pipeline. Runs entirely in memory - it never triggers API requests - and
    /// follows the fixed order: invalid → ownership → collection → price → discount → store → sale status →
    /// historical low → favourites → search → sort.
    /// </summary>
    public static class WishlistQueryEngine
    {
        public static List<WishlistItem> Apply(IEnumerable<WishlistItem> source, FilterState filter, SortMode sort, QueryOptions options)
        {
            filter = filter ?? new FilterState();
            options = options ?? new QueryOptions();
            IEnumerable<WishlistItem> query = RemoveInvalid(source ?? Enumerable.Empty<WishlistItem>());

            // Ownership.
            if (filter.Ownership == OwnershipFilter.Owned)
            {
                query = query.Where(i => i.IsOwned);
            }
            else if (filter.Ownership == OwnershipFilter.NotOwned || options.HideOwned)
            {
                query = query.Where(i => !i.IsOwned);
            }

            // Collections (a game matches when it belongs to any selected collection).
            if (filter.CollectionIds != null && filter.CollectionIds.Count > 0)
            {
                var wanted = new HashSet<string>(filter.CollectionIds);
                query = query.Where(i => i.CollectionIds != null && i.CollectionIds.Any(wanted.Contains));
            }

            var maxPrice = filter.MaxPrice;
            if (maxPrice.HasValue)
            {
                var strict = filter.Price != PriceFilter.Custom;
                query = query.Where(i => i.DisplayPrice.HasValue && (strict ? i.DisplayPrice.Value < maxPrice.Value : i.DisplayPrice.Value <= maxPrice.Value));
            }

            if (filter.MinDiscount > 0)
            {
                query = query.Where(i => i.DiscountPercent.HasValue && i.DiscountPercent.Value >= filter.MinDiscount);
            }

            if (filter.Store == StoreFilter.Retail)
            {
                query = query.Where(i => i.Price != null && i.Price.CurrentRetail.HasValue);
            }
            else if (filter.Store == StoreFilter.Keyshop)
            {
                query = query.Where(i => i.Price != null && i.Price.CurrentKeyshops.HasValue);
            }

            if (filter.SaleStatus == SaleStatusFilter.OnSale)
            {
                query = query.Where(i => i.IsOnSale);
            }
            else if (filter.SaleStatus == SaleStatusFilter.NotOnSale)
            {
                query = query.Where(i => !i.IsOnSale);
            }

            if (filter.HistoricalLowOnly)
            {
                query = query.Where(i => i.IsHistoricalLow);
            }

            if (filter.Favourites == FavouriteFilter.Favourites)
            {
                query = query.Where(i => i.IsFavourite);
            }
            else if (filter.Favourites == FavouriteFilter.NonFavourites)
            {
                query = query.Where(i => !i.IsFavourite);
            }

            var tokens = Tokenize(filter.SearchText);
            if (tokens.Length > 0)
            {
                query = query.Where(i => MatchesSearch(i, tokens, options.SearchIncludesExtra));
            }

            var list = query.ToList();
            Sort(list, sort);
            return list;
        }

        public static WishlistSummary Summarize(IEnumerable<WishlistItem> items, QueryOptions options = null)
        {
            var valid = RemoveInvalid(items ?? Enumerable.Empty<WishlistItem>()).ToList();
            var visible = options != null && options.HideOwned ? valid.Where(i => !i.IsOwned).ToList() : valid;
            return new WishlistSummary
            {
                Total = visible.Count,
                OnSale = visible.Count(i => i.IsOnSale),
                HistoricalLows = visible.Count(i => i.IsHistoricalLow),
                Owned = valid.Count(i => i.IsOwned),
                HasDiscountData = valid.Any(i => i.DiscountPercent.HasValue),
                HasOrderData = valid.Any(i => i.Entry?.WishlistPosition != null),
                HasAddedDates = valid.Any(i => i.Entry?.AddedUtc != null),
                HasDiscountDates = valid.Any(i => i.Price?.DiscountStartedUtc != null)
            };
        }

        /// <summary>Drops entries without identity/title and duplicate keys (the first occurrence wins).</summary>
        public static IEnumerable<WishlistItem> RemoveInvalid(IEnumerable<WishlistItem> items)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Title))
                {
                    continue;
                }

                if (seen.Add(item.Key))
                {
                    yield return item;
                }
            }
        }

        public static string[] Tokenize(string text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? new string[0]
                : text.ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool MatchesSearch(WishlistItem item, string[] tokens, bool includeExtra)
        {
            var title = item.SearchTitle ?? item.Title?.ToLowerInvariant() ?? string.Empty;
            var extra = includeExtra ? item.SearchExtra ?? string.Empty : string.Empty;
            foreach (var token in tokens)
            {
                if (title.IndexOf(token, StringComparison.Ordinal) < 0 && extra.IndexOf(token, StringComparison.Ordinal) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        public static void Sort(List<WishlistItem> list, SortMode sort)
        {
            Comparison<WishlistItem> primary;
            switch (sort)
            {
                case SortMode.PriceAscending:
                    primary = (a, b) => NullsLast(a.DisplayPrice, b.DisplayPrice, false);
                    break;
                case SortMode.PriceDescending:
                    primary = (a, b) => NullsLast(a.DisplayPrice, b.DisplayPrice, true);
                    break;
                case SortMode.DiscountDescending:
                    primary = (a, b) => NullsLast(a.DiscountPercent, b.DiscountPercent, true);
                    break;
                case SortMode.DiscountAscending:
                    primary = (a, b) => NullsLast(a.DiscountPercent, b.DiscountPercent, false);
                    break;
                case SortMode.HistoricalLow:
                    primary = (a, b) => NullsLast(a.DisplayHistoricalLow, b.DisplayHistoricalLow, false);
                    break;
                case SortMode.RecentlyAdded:
                    primary = (a, b) => NullsLast(a.Entry?.AddedUtc, b.Entry?.AddedUtc, true);
                    break;
                case SortMode.RecentlyDiscounted:
                    primary = (a, b) => NullsLast(a.Price?.DiscountStartedUtc, b.Price?.DiscountStartedUtc, true);
                    break;
                case SortMode.NameAscending:
                    primary = (a, b) => CompareTitles(a, b);
                    break;
                case SortMode.NameDescending:
                    primary = (a, b) => CompareTitles(b, a);
                    break;
                default:
                    primary = (a, b) => 0;
                    break;
            }

            // Tie-breaks give a total, deterministic order: wishlist position, then title, then key.
            list.Sort((a, b) =>
            {
                var result = primary(a, b);
                if (result != 0)
                {
                    return result;
                }

                result = NullsLast(a.Entry?.WishlistPosition, b.Entry?.WishlistPosition, false);
                if (result != 0)
                {
                    return result;
                }

                result = CompareTitles(a, b);
                return result != 0 ? result : string.CompareOrdinal(a.Key, b.Key);
            });
        }

        private static int CompareTitles(WishlistItem a, WishlistItem b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);

        private static int NullsLast<T>(T? a, T? b, bool descending) where T : struct, IComparable<T>
        {
            if (!a.HasValue && !b.HasValue)
            {
                return 0;
            }

            if (!a.HasValue)
            {
                return 1;
            }

            if (!b.HasValue)
            {
                return -1;
            }

            var result = a.Value.CompareTo(b.Value);
            return descending ? -result : result;
        }
    }
}
