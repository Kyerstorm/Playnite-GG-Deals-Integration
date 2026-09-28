using System;
using System.Collections.Generic;
using GGDealsWishlist.Models;

namespace GGDealsWishlist.Services
{
    /// <summary>A point in unit space: X 0 (oldest) → 1 (latest), Y 0 (highest price) → 1 (lowest price).</summary>
    public struct ChartPoint
    {
        public ChartPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }

        public double Y { get; }
    }

    /// <summary>
    /// Turns a recorded <see cref="PriceSeries"/> into a step chart in unit space, so the same geometry can be scaled
    /// for a small sparkline or a larger chart. Pure logic; the WPF layer only maps the points to its own coordinates.
    /// </summary>
    public sealed class PriceHistoryChart
    {
        private static readonly IReadOnlyList<ChartPoint> None = new ChartPoint[0];

        /// <summary>Below this span between the first and last point, points are spaced evenly instead of by time.</summary>
        private const double MinTimeSpanSeconds = 60;

        public static readonly PriceHistoryChart Empty = new PriceHistoryChart();

        private PriceHistoryChart()
        {
            Line = None;
            Markers = None;
        }

        /// <summary>The step outline: each price is held until the next change, then the line runs on to the latest check.</summary>
        public IReadOnlyList<ChartPoint> Line { get; private set; }

        /// <summary>One point per recorded price.</summary>
        public IReadOnlyList<ChartPoint> Markers { get; private set; }

        public decimal? Min { get; private set; }

        public decimal? Max { get; private set; }

        public DateTime? FromUtc { get; private set; }

        public DateTime? ToUtc { get; private set; }

        /// <summary>A line needs at least two recorded prices; one price alone is just "tracking started".</summary>
        public bool HasHistory => Markers.Count >= 2;

        public static PriceHistoryChart Build(PriceSeries series, PricePreference preference)
        {
            if (series?.Points == null)
            {
                return Empty;
            }

            var times = new List<DateTime>();
            var prices = new List<decimal>();
            foreach (var point in series.Points)
            {
                var price = Pick(point, preference);
                if (price.HasValue)
                {
                    times.Add(point.AtUtc);
                    prices.Add(price.Value);
                }
            }

            if (prices.Count < 2)
            {
                return Empty;
            }

            var min = prices[0];
            var max = prices[0];
            foreach (var price in prices)
            {
                min = Math.Min(min, price);
                max = Math.Max(max, price);
            }

            var from = times[0];
            var to = times[times.Count - 1] > series.LastSeenUtc ? times[times.Count - 1] : series.LastSeenUtc;
            var span = (to - from).TotalSeconds;
            var byTime = span >= MinTimeSpanSeconds;

            var markers = new List<ChartPoint>();
            var line = new List<ChartPoint>();
            for (var i = 0; i < prices.Count; i++)
            {
                var x = byTime ? (times[i] - from).TotalSeconds / span : (double)i / (prices.Count - 1);
                var y = max == min ? 0.5 : (double)((max - prices[i]) / (max - min));
                if (i > 0)
                {
                    line.Add(new ChartPoint(x, markers[i - 1].Y));
                }

                var marker = new ChartPoint(x, y);
                markers.Add(marker);
                line.Add(marker);
            }

            if (line[line.Count - 1].X < 1)
            {
                line.Add(new ChartPoint(1, markers[markers.Count - 1].Y));
            }

            return new PriceHistoryChart
            {
                Line = line,
                Markers = markers,
                Min = min,
                Max = max,
                FromUtc = from,
                ToUtc = to
            };
        }

        /// <summary>Mirrors <see cref="PriceLogic"/>: retail and keyshop are never blended, except for "lowest of both".</summary>
        private static decimal? Pick(PriceSnapshot snapshot, PricePreference preference)
        {
            switch (preference)
            {
                case PricePreference.Keyshop:
                    return snapshot.Keyshops;
                case PricePreference.LowestOfBoth:
                    if (snapshot.Keyshops.HasValue && (!snapshot.Retail.HasValue || snapshot.Keyshops.Value < snapshot.Retail.Value))
                    {
                        return snapshot.Keyshops;
                    }

                    return snapshot.Retail;
                default:
                    return snapshot.Retail;
            }
        }
    }
}
