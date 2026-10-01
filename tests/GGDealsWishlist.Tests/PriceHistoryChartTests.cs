using System;
using System.Collections.Generic;
using System.Linq;
using GGDealsWishlist.Models;
using GGDealsWishlist.Services;
using Xunit;

namespace GGDealsWishlist.Tests
{
    public sealed class PriceHistoryChartTests
    {
        private static readonly DateTime Day0 = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

        private static PriceSnapshot P(int day, decimal? retail, decimal? keyshops)
        {
            return new PriceSnapshot { AtUtc = Day0.AddDays(day), Retail = retail, Keyshops = keyshops };
        }

        private static PriceSeries Series(DateTime lastSeen, params PriceSnapshot[] points)
        {
            return new PriceSeries
            {
                Currency = "USD",
                Region = "us",
                FirstSeenUtc = points[0].AtUtc,
                LastSeenUtc = lastSeen,
                Points = points.ToList()
            };
        }

        private static double[] Xs(IEnumerable<ChartPoint> points) => points.Select(p => Math.Round(p.X, 3)).ToArray();

        private static double[] Ys(IEnumerable<ChartPoint> points) => points.Select(p => Math.Round(p.Y, 3)).ToArray();

        [Fact]
        public void A_missing_or_single_point_series_has_no_history()
        {
            Assert.False(PriceHistoryChart.Build(null, PricePreference.Retail).HasHistory);
            Assert.False(PriceHistoryChart.Build(Series(Day0, P(0, 19.99m, null)), PricePreference.Retail).HasHistory);
        }

        [Fact]
        public void Prices_become_a_step_line_scaled_by_time_and_value()
        {
            var series = Series(Day0.AddDays(4), P(0, 20m, null), P(1, 10m, null), P(2, 15m, null));

            var chart = PriceHistoryChart.Build(series, PricePreference.Retail);

            Assert.True(chart.HasHistory);
            Assert.Equal(new[] { 0.0, 0.25, 0.5 }, Xs(chart.Markers));
            Assert.Equal(new[] { 0.0, 1.0, 0.5 }, Ys(chart.Markers));
            Assert.Equal(new[] { 0.0, 0.25, 0.25, 0.5, 0.5, 1.0 }, Xs(chart.Line));
            Assert.Equal(new[] { 0.0, 0.0, 1.0, 1.0, 0.5, 0.5 }, Ys(chart.Line));
            Assert.Equal(10m, chart.Min);
            Assert.Equal(20m, chart.Max);
            Assert.Equal(Day0, chart.FromUtc);
            Assert.Equal(Day0.AddDays(4), chart.ToUtc);
        }

        [Fact]
        public void Keyshop_preference_ignores_retail_changes_and_draws_a_flat_line()
        {
            var series = Series(Day0.AddDays(2), P(0, 20m, 12m), P(1, 10m, 12m));

            var chart = PriceHistoryChart.Build(series, PricePreference.Keyshop);

            Assert.True(chart.HasHistory);
            Assert.All(chart.Markers, m => Assert.Equal(0.5, m.Y));
            Assert.Equal(12m, chart.Min);
            Assert.Equal(12m, chart.Max);
        }

        [Fact]
        public void Lowest_of_both_uses_the_cheaper_price_per_snapshot()
        {
            var series = Series(Day0.AddDays(2), P(0, 20m, 15m), P(1, 10m, 15m));

            var chart = PriceHistoryChart.Build(series, PricePreference.LowestOfBoth);

            Assert.Equal(10m, chart.Min);
            Assert.Equal(15m, chart.Max);
        }

        [Fact]
        public void Snapshots_without_the_chosen_price_are_skipped()
        {
            var series = Series(Day0.AddDays(3), P(0, 20m, null), P(1, null, 9m), P(2, 10m, null));

            var chart = PriceHistoryChart.Build(series, PricePreference.Retail);

            Assert.Equal(2, chart.Markers.Count);
            Assert.Equal(10m, chart.Min);
            Assert.Equal(20m, chart.Max);
        }

        [Fact]
        public void Points_recorded_at_the_same_moment_are_spread_evenly()
        {
            var series = Series(Day0, P(0, 20m, null), P(0, 10m, null), P(0, 15m, null));

            var chart = PriceHistoryChart.Build(series, PricePreference.Retail);

            Assert.Equal(new[] { 0.0, 0.5, 1.0 }, Xs(chart.Markers));
        }

        [Fact]
        public void Each_marker_keeps_its_price_and_time_for_hover_labels()
        {
            var series = Series(Day0.AddDays(4), P(0, 20m, null), P(1, 10m, null), P(2, 15m, null));

            var chart = PriceHistoryChart.Build(series, PricePreference.Retail);

            Assert.Equal(new[] { 20m, 10m, 15m }, chart.Prices.ToArray());
            Assert.Equal(new[] { Day0, Day0.AddDays(1), Day0.AddDays(2) }, chart.Times.ToArray());
        }

        [Fact]
        public void The_low_marker_is_the_cheapest_price_and_the_latest_one_on_a_tie()
        {
            var distinct = PriceHistoryChart.Build(Series(Day0.AddDays(4), P(0, 20m, null), P(1, 10m, null), P(2, 15m, null)), PricePreference.Retail);
            var tied = PriceHistoryChart.Build(Series(Day0.AddDays(4), P(0, 10m, null), P(1, 15m, null), P(2, 10m, null)), PricePreference.Retail);

            Assert.Equal(1, distinct.LowIndex);
            Assert.Equal(2, tied.LowIndex);
            Assert.Equal(-1, PriceHistoryChart.Empty.LowIndex);
        }

        [Fact]
        public void Hovering_returns_the_price_in_effect_at_that_point_in_time()
        {
            // Markers at x = 0, 0.25, 0.5; the last price is held to x = 1.
            var chart = PriceHistoryChart.Build(Series(Day0.AddDays(4), P(0, 20m, null), P(1, 10m, null), P(2, 15m, null)), PricePreference.Retail);

            Assert.Equal(0, chart.StepIndexAt(0));
            Assert.Equal(0, chart.StepIndexAt(0.24));
            Assert.Equal(1, chart.StepIndexAt(0.25));
            Assert.Equal(1, chart.StepIndexAt(0.49));
            Assert.Equal(2, chart.StepIndexAt(0.5));
            Assert.Equal(2, chart.StepIndexAt(1));
        }

        [Fact]
        public void Hover_positions_outside_the_chart_are_clamped_and_an_empty_chart_has_no_step()
        {
            var chart = PriceHistoryChart.Build(Series(Day0.AddDays(4), P(0, 20m, null), P(1, 10m, null)), PricePreference.Retail);

            Assert.Equal(0, chart.StepIndexAt(-3));
            Assert.Equal(1, chart.StepIndexAt(7));
            Assert.Equal(-1, PriceHistoryChart.Empty.StepIndexAt(0.5));
        }

        [Fact]
        public void A_series_can_be_charted_for_both_store_types_only_when_each_has_a_line()
        {
            var both = Series(Day0.AddDays(3), P(0, 20m, 15m), P(1, 10m, 12m));
            var retailOnly = Series(Day0.AddDays(3), P(0, 20m, null), P(1, 10m, null));
            var flatKeyshop = Series(Day0.AddDays(3), P(0, 20m, 12m), P(1, 10m, 12m));

            Assert.True(PriceHistoryChart.CanCompare(both));
            Assert.False(PriceHistoryChart.CanCompare(retailOnly));
            Assert.True(PriceHistoryChart.CanCompare(flatKeyshop));
            Assert.False(PriceHistoryChart.CanCompare(null));
        }
    }
}
