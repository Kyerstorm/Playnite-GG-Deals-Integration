using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GGDealsWishlist.Models;

namespace GGDealsWishlist.Api
{
    public sealed class GGDealsRegion
    {
        public GGDealsRegion(string code, string name, string currency)
        {
            Code = code;
            Name = name;
            Currency = currency;
        }

        public string Code { get; }

        public string Name { get; }

        public string Currency { get; }

        public string DisplayName => Name + " (" + CurrencyFormatter.Symbol(Currency) + ")";

        public override string ToString() => DisplayName;
    }

    /// <summary>Regions documented by the GG.deals Prices API (the region query parameter).</summary>
    public static class Regions
    {
        public const string DefaultCode = "us";

        public static readonly IReadOnlyList<GGDealsRegion> All = new List<GGDealsRegion>
        {
            new GGDealsRegion("au", "Australia", "AUD"),
            new GGDealsRegion("be", "Belgium", "EUR"),
            new GGDealsRegion("br", "Brazil", "BRL"),
            new GGDealsRegion("ca", "Canada", "CAD"),
            new GGDealsRegion("dk", "Denmark", "DKK"),
            new GGDealsRegion("eu", "Europe", "EUR"),
            new GGDealsRegion("fi", "Finland", "EUR"),
            new GGDealsRegion("fr", "France", "EUR"),
            new GGDealsRegion("de", "Germany", "EUR"),
            new GGDealsRegion("ie", "Ireland", "EUR"),
            new GGDealsRegion("it", "Italy", "EUR"),
            new GGDealsRegion("nl", "Netherlands", "EUR"),
            new GGDealsRegion("no", "Norway", "NOK"),
            new GGDealsRegion("pl", "Poland", "PLN"),
            new GGDealsRegion("es", "Spain", "EUR"),
            new GGDealsRegion("se", "Sweden", "SEK"),
            new GGDealsRegion("ch", "Switzerland", "CHF"),
            new GGDealsRegion("gb", "United Kingdom", "GBP"),
            new GGDealsRegion("us", "United States", "USD")
        };

        public static bool IsValid(string code) => All.Any(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));

        public static GGDealsRegion Find(string code) => All.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));

        /// <summary>Picks the GG.deals region matching the Windows region when one exists.</summary>
        public static string DetectDefault()
        {
            try
            {
                var iso = RegionInfo.CurrentRegion.TwoLetterISORegionName.ToLowerInvariant();
                if (iso == "uk")
                {
                    iso = "gb";
                }

                if (IsValid(iso))
                {
                    return iso;
                }

                var euro = new[] { "at", "pt", "gr", "lu", "sk", "si", "ee", "lv", "lt", "cy", "mt", "hr" };
                return euro.Contains(iso) ? "eu" : DefaultCode;
            }
            catch (Exception)
            {
                return DefaultCode;
            }
        }
    }

    /// <summary>Formats prices using the currency code GG.deals returned (never assumes a currency).</summary>
    public static class CurrencyFormatter
    {
        private static readonly Dictionary<string, string> PrefixSymbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "USD", "$" }, { "GBP", "£" }, { "EUR", "€" }, { "AUD", "A$" }, { "CAD", "CA$" }, { "BRL", "R$" }
        };

        private static readonly Dictionary<string, string> SuffixSymbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "PLN", "zł" }, { "DKK", "kr." }, { "NOK", "kr" }, { "SEK", "kr" }, { "CHF", "CHF" }
        };

        public static string Symbol(string currency)
        {
            if (string.IsNullOrEmpty(currency))
            {
                return string.Empty;
            }

            if (PrefixSymbols.TryGetValue(currency, out var prefix))
            {
                return prefix;
            }

            return SuffixSymbols.TryGetValue(currency, out var suffix) ? suffix : currency.ToUpperInvariant();
        }

        public static string Format(decimal? amount, string currency)
        {
            if (!amount.HasValue)
            {
                return null;
            }

            var number = amount.Value.ToString("0.00", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(currency))
            {
                return number;
            }

            if (PrefixSymbols.TryGetValue(currency, out var prefix))
            {
                return prefix + number;
            }

            return number + " " + (SuffixSymbols.TryGetValue(currency, out var suffix) ? suffix : currency.ToUpperInvariant());
        }
    }

    public struct PriceSelection
    {
        public decimal? Current { get; set; }

        public PriceCategory? Category { get; set; }

        /// <summary>Historical low of the same category as <see cref="Current"/>.</summary>
        public decimal? HistoricalLow { get; set; }

        /// <summary>True only when both values exist for the same category and current ≤ historical low.</summary>
        public bool IsHistoricalLow { get; set; }
    }

    /// <summary>Applies the user's price preference without ever mixing retail and keyshop values.</summary>
    public static class PriceLogic
    {
        public static PriceSelection Select(PriceData price, PricePreference preference)
        {
            if (price == null || !price.Found)
            {
                return new PriceSelection();
            }

            switch (preference)
            {
                case PricePreference.Keyshop:
                    return Build(price.CurrentKeyshops, price.HistoricalKeyshops, PriceCategory.Keyshop);
                case PricePreference.LowestOfBoth:
                    if (price.CurrentKeyshops.HasValue && (!price.CurrentRetail.HasValue || price.CurrentKeyshops.Value < price.CurrentRetail.Value))
                    {
                        return Build(price.CurrentKeyshops, price.HistoricalKeyshops, PriceCategory.Keyshop);
                    }

                    return Build(price.CurrentRetail, price.HistoricalRetail, PriceCategory.Retail);
                default:
                    return Build(price.CurrentRetail, price.HistoricalRetail, PriceCategory.Retail);
            }
        }

        private static PriceSelection Build(decimal? current, decimal? historical, PriceCategory category)
        {
            if (!current.HasValue)
            {
                return new PriceSelection { HistoricalLow = historical };
            }

            return new PriceSelection
            {
                Current = current,
                Category = category,
                HistoricalLow = historical,
                // Only flagged when the API explicitly supplies both values for the same category.
                IsHistoricalLow = historical.HasValue && current.Value <= historical.Value
            };
        }
    }
}
