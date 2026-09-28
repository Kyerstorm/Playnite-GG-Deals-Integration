using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GGDealsWishlist.Models;
using GGDealsWishlist.Services;
using Xunit;

namespace GGDealsWishlist.Tests
{
    public sealed class PriceHistoryTests
    {
        private static readonly DateTime Start = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

        private static PriceData Price(decimal? retail, decimal? keyshops = null, DateTime? at = null, string currency = "USD", string region = "us", bool found = true)
        {
            return new PriceData
            {
                LookupKey = "app:1",
                Found = found,
                CurrentRetail = retail,
                CurrentKeyshops = keyshops,
                Currency = currency,
                Region = region,
                FetchedUtc = at ?? Start
            };
        }

        [Fact]
        public void First_price_creates_a_series_with_one_point()
        {
            var history = new PriceHistoryDocument();

            var changed = history.Record("app:1", Price(24.99m, 21.49m));

            Assert.True(changed);
            var series = history.Get("app:1");
            Assert.NotNull(series);
            Assert.Equal("USD", series.Currency);
            Assert.Equal("us", series.Region);
            var point = Assert.Single(series.Points);
            Assert.Equal(24.99m, point.Retail);
            Assert.Equal(21.49m, point.Keyshops);
            Assert.Equal(Start, point.AtUtc);
            Assert.Equal(Start, series.FirstSeenUtc);
            Assert.Equal(Start, series.LastSeenUtc);
        }

        [Fact]
        public void An_unchanged_price_adds_no_point_but_advances_last_seen()
        {
            var history = new PriceHistoryDocument();
            history.Record("app:1", Price(24.99m, at: Start));

            var changed = history.Record("app:1", Price(24.99m, at: Start.AddHours(6)));

            Assert.True(changed);
            var series = history.Get("app:1");
            Assert.Single(series.Points);
            Assert.Equal(Start.AddHours(6), series.LastSeenUtc);
        }

        [Fact]
        public void A_change_in_either_retail_or_keyshops_adds_a_point()
        {
            var history = new PriceHistoryDocument();
            history.Record("app:1", Price(24.99m, 21.49m, Start));
            history.Record("app:1", Price(14.99m, 21.49m, Start.AddDays(1)));
            history.Record("app:1", Price(14.99m, 12.99m, Start.AddDays(2)));

            var points = history.Get("app:1").Points;

            Assert.Equal(3, points.Count);
            Assert.Equal(new decimal?[] { 24.99m, 14.99m, 14.99m }, points.Select(p => p.Retail).ToArray());
            Assert.Equal(new decimal?[] { 21.49m, 21.49m, 12.99m }, points.Select(p => p.Keyshops).ToArray());
        }

        [Fact]
        public void Games_that_were_not_found_or_have_no_price_are_ignored()
        {
            var history = new PriceHistoryDocument();

            Assert.False(history.Record("app:1", Price(24.99m, found: false)));
            Assert.False(history.Record("app:2", Price(null, null)));
            Assert.False(history.Record("app:3", null));

            Assert.Empty(history.Series);
        }

        [Fact]
        public void A_change_of_currency_or_region_starts_a_fresh_series()
        {
            var history = new PriceHistoryDocument();
            history.Record("app:1", Price(24.99m, at: Start));
            history.Record("app:1", Price(14.99m, at: Start.AddDays(1)));

            history.Record("app:1", Price(22.99m, currency: "EUR", region: "de", at: Start.AddDays(2)));

            var series = history.Get("app:1");
            Assert.Equal("EUR", series.Currency);
            Assert.Equal("de", series.Region);
            var point = Assert.Single(series.Points);
            Assert.Equal(22.99m, point.Retail);
            Assert.Equal(Start.AddDays(2), series.FirstSeenUtc);
        }

        [Fact]
        public void The_oldest_points_are_dropped_beyond_the_cap()
        {
            var history = new PriceHistoryDocument();
            for (var i = 0; i < 6; i++)
            {
                history.Record("app:1", Price(10m + i, at: Start.AddDays(i)), maxPoints: 4);
            }

            var points = history.Get("app:1").Points;

            Assert.Equal(4, points.Count);
            Assert.Equal(12m, points[0].Retail);
            Assert.Equal(15m, points[3].Retail);
        }

        [Fact]
        public void Prune_removes_series_for_games_no_longer_tracked()
        {
            var history = new PriceHistoryDocument();
            history.Record("app:1", Price(10m));
            history.Record("app:2", Price(20m));
            history.Record("app:3", Price(30m));

            var removed = history.Prune(new[] { "app:1", "app:3" });

            Assert.Equal(1, removed);
            Assert.Null(history.Get("app:2"));
            Assert.NotNull(history.Get("app:1"));
            Assert.NotNull(history.Get("app:3"));
        }

        [Fact]
        public void History_survives_a_save_and_load_round_trip()
        {
            var path = Path.Combine(Path.GetTempPath(), "gg-history-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var store = new VersionedJsonStore<PriceHistoryDocument>(path, PriceHistoryDocument.CurrentVersion);
                var history = new PriceHistoryDocument();
                history.Record("app:1", Price(24.99m, 21.49m, Start));
                history.Record("app:1", Price(14.99m, null, Start.AddDays(3)));
                store.Save(history);

                var loaded = store.Load();

                var series = loaded.Get("app:1");
                Assert.NotNull(series);
                Assert.Equal(2, series.Points.Count);
                Assert.Equal(24.99m, series.Points[0].Retail);
                Assert.Equal(21.49m, series.Points[0].Keyshops);
                Assert.Equal(14.99m, series.Points[1].Retail);
                Assert.Null(series.Points[1].Keyshops);
                Assert.Equal(Start.AddDays(3), series.LastSeenUtc.ToUniversalTime());
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
