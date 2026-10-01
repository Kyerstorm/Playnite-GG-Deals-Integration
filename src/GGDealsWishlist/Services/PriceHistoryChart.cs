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
            Prices = new decimal[0];
            Times = new DateTime[0];
            LowIndex = -1;
        }

        /// <summary>The recorded price behind each entry of <see cref="Markers"/>.</summary>
        public IReadOnlyList<decimal> Prices { get; private set; }

        /// <summary>When each entry of <see cref="Markers"/> was recorded (UTC).</summary>
        public IReadOnlyList<DateTime> Times { get; private set; }

        /// <summary>Index of the cheapest recorded price (the latest one on a tie), or -1 without a line.</summary>
        public int LowIndex { get; private set; }

        /// <summary>
        /// The marker whose price is in effect at <paramref name="x"/> (0..1, clamped). The chart is a step line, so
        /// hovering between two markers reports the earlier one: the price that was being held.
        /// </summary>
        public int StepIndexAt(double x)
        {
            if (Markers.Count == 0)
            {
                return -1;
            }

            for (var i = Markers.Count - 1; i > 0; i--)
            {
                if (Markers[i].X <= x)
                {
                    return i;
                }
            }

            return 0;
        }

        /// <summary>True when both the retail and the keyshop prices have their own line, so the two can be compared.</summary>
        public static bool CanCompare(PriceSeries series)
        {
            return Build(series, PricePreference.Retail).HasHistory && Build(series, PricePreference.Keyshop).HasHistory;
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
            var lowIndex = 0;
            for (var i = 0; i < prices.Count; i++)
            {
                max = Math.Max(max, prices[i]);
                if (prices[i] <= min)
                {
                    min = prices[i];
                    lowIndex = i;
                }
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
                Prices = prices,
                Times = times,
                LowIndex = lowIndex,
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
