using System;
using System.Collections.Generic;
using System.Linq;
using GGDealsWishlist.Models;

namespace GGDealsWishlist.Models
{
    /// <summary>One price the extension saw for a game. Only ever written from a real API response.</summary>
    public sealed class PriceSnapshot
    {
        public DateTime AtUtc { get; set; }

        public decimal? Retail { get; set; }

        public decimal? Keyshops { get; set; }
    }

    /// <summary>
    /// Prices recorded for one lookup key. A point is only added when a price changed, so the series is a step
    /// history; <see cref="LastSeenUtc"/> says how recently the current price was confirmed.
    /// </summary>
    public sealed class PriceSeries
    {
        public string Currency { get; set; }

        public string Region { get; set; }

        public DateTime FirstSeenUtc { get; set; }

        public DateTime LastSeenUtc { get; set; }

        public List<PriceSnapshot> Points { get; set; } = new List<PriceSnapshot>();

        /// <summary>Independent copy for readers on other threads. Snapshots themselves are never mutated once added.</summary>
        public PriceSeries Snapshot()
        {
            return new PriceSeries
            {
                Currency = Currency,
                Region = Region,
                FirstSeenUtc = FirstSeenUtc,
                LastSeenUtc = LastSeenUtc,
                Points = Points == null ? new List<PriceSnapshot>() : new List<PriceSnapshot>(Points)
            };
        }
    }
}

namespace GGDealsWishlist.Services
{
    /// <summary>
    /// Local-only price history built from the extension's own refreshes (the GG.deals Prices API exposes no history).
    /// Kept in its own file so "Clear cached data" does not throw it away.
    /// </summary>
    public sealed class PriceHistoryDocument
    {
        public const int CurrentVersion = 1;
        public const int DefaultMaxPoints = 200;

        public int DataVersion { get; set; } = CurrentVersion;

        /// <summary>Lookup key ("app:420") → recorded series.</summary>
        public Dictionary<string, PriceSeries> Series { get; set; } = new Dictionary<string, PriceSeries>();

        public PriceSeries Get(string key)
        {
            return key != null && Series != null && Series.TryGetValue(key, out var series) ? series : null;
        }

        /// <summary>
        /// Records a freshly fetched price. Returns true when the document changed and should be saved.
        /// Prices are never mixed across currencies or regions: a change of either starts a new series.
        /// </summary>
        public bool Record(string key, PriceData price, int maxPoints = DefaultMaxPoints)
        {
            if (string.IsNullOrEmpty(key) || price == null || !price.Found || (!price.CurrentRetail.HasValue && !price.CurrentKeyshops.HasValue))
            {
                return false;
            }

            Series = Series ?? new Dictionary<string, PriceSeries>();
            var at = price.FetchedUtc;

            if (!Series.TryGetValue(key, out var series) || series.Points == null || series.Points.Count == 0
                || !string.Equals(series.Currency, price.Currency, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(series.Region, price.Region, StringComparison.OrdinalIgnoreCase))
            {
                Series[key] = new PriceSeries
                {
                    Currency = price.Currency,
                    Region = price.Region,
                    FirstSeenUtc = at,
                    LastSeenUtc = at,
                    Points = new List<PriceSnapshot> { NewPoint(price) }
                };
                return true;
            }

            var last = series.Points[series.Points.Count - 1];
            if (last.Retail != price.CurrentRetail || last.Keyshops != price.CurrentKeyshops)
            {
                series.Points.Add(NewPoint(price));
                var excess = series.Points.Count - Math.Max(1, maxPoints);
                if (excess > 0)
                {
                    series.Points.RemoveRange(0, excess);
                }
            }

            if (at > series.LastSeenUtc)
            {
                series.LastSeenUtc = at;
            }

            return true;
        }

        /// <summary>Drops series whose key is no longer tracked. Returns how many were removed.</summary>
        public int Prune(IEnumerable<string> keysToKeep)
        {
            if (Series == null || keysToKeep == null)
            {
                return 0;
            }

            var keep = new HashSet<string>(keysToKeep);
            var stale = Series.Keys.Where(k => !keep.Contains(k)).ToList();
            foreach (var key in stale)
            {
                Series.Remove(key);
            }

            return stale.Count;
        }

        public void Normalize()
        {
            Series = Series ?? new Dictionary<string, PriceSeries>();
            foreach (var series in Series.Values)
            {
                series.Points = series.Points ?? new List<PriceSnapshot>();
            }
        }

        private static PriceSnapshot NewPoint(PriceData price)
        {
            return new PriceSnapshot { AtUtc = price.FetchedUtc, Retail = price.CurrentRetail, Keyshops = price.CurrentKeyshops };
        }
    }
}
