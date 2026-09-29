using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GGDealsWishlist.Api;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Models;
using GGDealsWishlist.Services;
using GGDealsWishlist.Settings;

namespace GGDealsWishlist.ViewModels
{
    /// <summary>Everything the UI needs from the host application (Playnite, or a fake in the preview app).</summary>
    public interface IHostServices
    {
        Dispatcher Dispatcher { get; }

        /// <summary>The saved settings (read-only use; change via <see cref="UpdateSettings"/>).</summary>
        GGDealsSettings Settings { get; }

        event EventHandler SettingsChanged;

        void UpdateSettings(Action<GGDealsSettings> change);

        /// <summary>Encrypts, stores and applies a new API key.</summary>
        void SaveApiKey(string plainKey);

        void OpenUrl(string url);

        void CopyToClipboard(string text);

        bool OpenPlayniteGame(Guid gameId);

        void OpenSettings();

        /// <summary>Returns null when cancelled.</summary>
        string PromptText(string message, string caption, string defaultValue);

        bool Confirm(string message, string caption);

        /// <summary>Asks the user for an image file; null when cancelled.</summary>
        string PickImageFile();

        /// <summary>Shows the SteamGridDB thumbnail picker; returns the chosen image URL, or null when cancelled.</summary>
        string PickSteamGridDbCover(long? steamAppId, string title);

        void ShowCollectionsManager();
    }

    public sealed class Option<T>
    {
        public Option(T value, string label, bool isEnabled = true)
        {
            Value = value;
            Label = label;
            IsEnabled = isEnabled;
        }

        public T Value { get; }

        public string Label { get; }

        public bool IsEnabled { get; }

        public override string ToString() => Label;
    }

    public sealed class CollectionFilterItem : ObservableBase
    {
        private bool isSelected;
        private readonly Action changed;

        public CollectionFilterItem(WishlistCollection collection, int count, bool selected, Action changed)
        {
            Collection = collection;
            Count = count;
            isSelected = selected;
            this.changed = changed;
        }

        public WishlistCollection Collection { get; }

        public int Count { get; }

        public string Label => (string.IsNullOrEmpty(Collection.Icon) ? string.Empty : Collection.Icon + " ") + Collection.Name;

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (SetValue(ref isSelected, value))
                {
                    changed?.Invoke();
                }
            }
        }

        /// <summary>Updates the checkbox without notifying the owner (used when clearing all filters at once).</summary>
        public void SetSelectedSilently(bool value) => SetValue(ref isSelected, value, nameof(IsSelected));
    }

    /// <summary>Display preferences shared by every card (field visibility, sizing, accent colour).</summary>
    public sealed class DisplayOptions : ObservableBase
    {
        private GGDealsSettings settings = new GGDealsSettings();
        private ViewMode layoutMode = ViewMode.CoverInfo;
        private double width = 320;

        public void Update(GGDealsSettings newSettings, double availableWidth)
        {
            settings = newSettings ?? new GGDealsSettings();
            width = availableWidth > 0 ? availableWidth : width;
            var accent = ParseColor(settings.AccentColor) ?? (Color)ColorConverter.ConvertFromString(GGDealsSettings.DefaultAccentColor);
            AccentBrush = Freeze(new SolidColorBrush(accent));
            AccentSoftBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B)));
            var luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
            AccentForegroundBrush = luminance > 0.6 ? Brushes.Black : Brushes.White;
            OnAllPropertiesChanged();
        }

        public GGDealsSettings Settings => settings;

        public double Width => width;

        public bool IsNarrow => width < 300;

        public bool IsWide => width >= 440;

        public bool IsCompactDensity => settings.Density == CardDensity.Compact;

        public bool ShowCover => settings.ShowCover;
        public bool ShowTitle => settings.ShowTitle;
        public bool ShowCurrentPrice => settings.ShowCurrentPrice;
        public bool ShowDiscount => settings.ShowDiscount;
        public bool ShowPriceSource => settings.ShowPriceSource && !IsNarrow;
        public bool ShowHistoricalLow => settings.ShowHistoricalLow;
        public bool ShowHistoricalLowBadge => settings.ShowHistoricalLowBadge;
        public bool ShowRetailPrice => settings.ShowRetailPrice;
        public bool ShowKeyshopPrice => settings.ShowKeyshopPrice;
        public bool ShowWishlistDate => settings.ShowWishlistDate;
        public bool ShowRating => settings.ShowRating;
        public bool ShowPlatform => settings.ShowPlatform;
        public bool ShowReleaseDate => settings.ShowReleaseDate;
        public bool ShowStoreCount => settings.ShowStoreCount;
        public bool ShowFavouriteStar => settings.ShowFavouriteStar;
        public bool ShowCollections => settings.ShowCollections;
        public bool ShowOwnership => settings.ShowOwnership && settings.EnableMatching;
        public bool ShowInstalledStatus => settings.ShowInstalledStatus && settings.EnableMatching;
        public bool ShowPriceHistory => settings.ShowPriceHistory;

        /// <summary>Extra price breakdown on cards is only shown when the sidebar is wide.</summary>
        public bool ShowCardPriceBreakdown => IsWide && (settings.ShowRetailPrice || settings.ShowKeyshopPrice || settings.ShowHistoricalLow);

        public double CoverWidth
        {
            get
            {
                double baseWidth;
                switch (settings.CoverSize)
                {
                    case CoverSize.Small:
                        baseWidth = 56;
                        break;
                    case CoverSize.Large:
                        baseWidth = 100;
                        break;
                    default:
                        baseWidth = 76;
                        break;
                }

                if (IsNarrow)
                {
                    baseWidth *= 0.8;
                }
                else if (IsWide)
                {
                    baseWidth *= 1.2;
                }

                return Math.Round(baseWidth);
            }
        }

        /// <summary>Covers use the standard 2:3 box-art aspect ratio.</summary>
        public double CoverHeight => Math.Round(CoverWidth * 1.5);

        public double CompactCoverWidth => IsCompactDensity ? 24 : 30;

        public double CompactCoverHeight => CompactCoverWidth * 1.5;

        public double DetailCoverWidth => Math.Min(220, Math.Max(120, width * 0.5));

        public double DetailCoverHeight => DetailCoverWidth * 1.5;

        /// <summary>Width available to list items: the sidebar minus the list padding and the scrollbar.</summary>
        public double ListContentWidth => Math.Max(160, width - 36);

        /// <summary>One tile per row when narrow, otherwise as many roughly 200px columns as fit (two at least, eight at most).</summary>
        public int GridColumns => width < 300 ? 1 : Math.Min(8, Math.Max(2, (int)(ListContentWidth / 200)));

        /// <summary>Cover cards stay a single column until the sidebar is roughly two card-widths wide (440px each, six at most).</summary>
        public int CardColumns => width < 900 ? 1 : Math.Min(6, Math.Max(2, (int)(ListContentWidth / 440)));

        /// <summary>The layout the list is currently using; set by the sidebar view model.</summary>
        public ViewMode LayoutMode => layoutMode;

        public void SetLayout(ViewMode mode)
        {
            if (layoutMode != mode)
            {
                layoutMode = mode;
                OnAllPropertiesChanged();
            }
        }

        /// <summary>Grid tiles, and cover cards once there is room for several columns, wrap left to right instead of stacking.</summary>
        public bool IsWrapped => layoutMode == ViewMode.Grid || (layoutMode == ViewMode.CoverInfo && CardColumns > 1);

        public int ColumnCount => layoutMode == ViewMode.Grid ? GridColumns : layoutMode == ViewMode.CoverInfo ? CardColumns : 1;

        /// <summary>Width of one column, margins included.</summary>
        public double SlotWidth => Math.Floor(ListContentWidth / ColumnCount);

        /// <summary>Tiles carry a 4px margin on every side, so two neighbours are 8px apart.</summary>
        public double GridTileWidth => SlotWidth - 8;

        /// <summary>Steam header art is 460x215; the same ratio keeps Steam and library covers looking alike.</summary>
        public double GridCoverHeight => Math.Round(GridTileWidth * 215 / 460);

        /// <summary>An expanded item takes a whole row of the wrapping layout.</summary>
        public double ExpandedSlotWidth => ListContentWidth;

        public Thickness CardPadding => IsCompactDensity ? new Thickness(6) : new Thickness(10);

        /// <summary>Wrapped cards get a 4px side margin so neighbours are 8px apart, like grid tiles.</summary>
        public Thickness CardMargin
        {
            get
            {
                var side = IsWrapped ? 4 : 0;
                return new Thickness(side, 0, side, IsCompactDensity ? 4 : 8);
            }
        }

        public Thickness RowPadding => IsCompactDensity ? new Thickness(6, 3, 6, 3) : new Thickness(8, 5, 8, 5);

        public double TitleFontSize => IsNarrow || IsCompactDensity ? 13 : 14;

        public double PriceFontSize => IsNarrow ? 15 : 17;

        public double SmallFontSize => IsNarrow ? 10.5 : 11.5;

        public Brush AccentBrush { get; private set; }

        public Brush AccentSoftBrush { get; private set; }

        public Brush AccentForegroundBrush { get; private set; }

        public static Color? ParseColor(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                var value = text.Trim();
                if (!value.StartsWith("#", StringComparison.Ordinal))
                {
                    value = "#" + value;
                }

                return (Color)ColorConverter.ConvertFromString(value);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>Presentation wrapper for one wishlist row. Pure formatting - never touches the network.</summary>
    public sealed class WishlistItemViewModel : ObservableBase
    {
        public const string SteamArtworkTemplate = "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900.jpg";
        public const string SteamCapsuleTemplate = "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/header.jpg";

        private readonly Func<IEnumerable<string>, string> describeCollections;
        private WishlistItem item;
        private PriceHistoryChart historyChart;
        private bool isExpanded;

        public WishlistItemViewModel(WishlistItem item, DisplayOptions display, Func<IEnumerable<string>, string> describeCollections)
        {
            this.item = item;
            Display = display;
            this.describeCollections = describeCollections;
        }

        public WishlistItem Item => item;

        public DisplayOptions Display { get; }

        public string Key => item.Key;

        public string Title => item.Title;

        public void Update(WishlistItem newItem)
        {
            item = newItem;
            historyChart = null;
            OnAllPropertiesChanged();
        }

        public void RefreshDisplay()
        {
            historyChart = null;
            OnAllPropertiesChanged();
        }

        /// <summary>Whether the card is opened in place to show its price history and store-type prices.</summary>
        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                if (SetValue(ref isExpanded, value))
                {
                    OnPropertyChanged(nameof(ExpandGlyph));
                    OnPropertyChanged(nameof(ExpandTooltip));
                    OnPropertyChanged(nameof(ItemWidth));
                }
            }
        }

        /// <summary>
        /// Width of this item's slot in a wrapping layout: one column normally, a whole row when expanded.
        /// NaN (automatic) in the stacked layouts, where items simply stretch.
        /// </summary>
        public double ItemWidth => !Display.IsWrapped ? double.NaN : IsExpanded ? Display.ExpandedSlotWidth : Display.SlotWidth;

        /// <summary>Re-reads only the slot width; used while a wrapped layout is being resized.</summary>
        public void RefreshLayout() => OnPropertyChanged(nameof(ItemWidth));

        // ---------------------------------------------------------------- artwork

        /// <summary>
        /// Playnite artwork for reliable matches; otherwise Steam's portrait cover for Steam apps (wishlist games are
        /// usually not in the library, so this is the normal case), then Steam's landscape header (cropped to fit, since
        /// many apps have no portrait art), then SteamGridDB when the user has set a key; otherwise the placeholder.
        /// The loader tries these in order.
        /// </summary>
        public string CoverSource
        {
            get
            {
                if (item.IsOwned && !string.IsNullOrEmpty(item.Match.Game.CoverPath))
                {
                    return item.Match.Game.CoverPath;
                }

                var appId = item.Entry?.SteamAppId;
                return CoverSources.Join(
                    item.CoverOverride,
                    appId.HasValue ? string.Format(CultureInfo.InvariantCulture, SteamArtworkTemplate, appId.Value) : null,
                    appId.HasValue ? string.Format(CultureInfo.InvariantCulture, SteamCapsuleTemplate, appId.Value) : null,
                    CoverSources.SteamGridDb(true, appId, Title));
            }
        }

        /// <summary>Landscape art for the grid view: Steam's header image, or the library cover for owned games.</summary>
        public string CapsuleSource
        {
            get
            {
                if (item.IsOwned && !string.IsNullOrEmpty(item.Match.Game.CoverPath))
                {
                    return item.Match.Game.CoverPath;
                }

                var appId = item.Entry?.SteamAppId;
                return CoverSources.Join(
                    item.CoverOverride,
                    appId.HasValue ? string.Format(CultureInfo.InvariantCulture, SteamCapsuleTemplate, appId.Value) : null,
                    CoverSources.SteamGridDb(false, appId, Title));
            }
        }

        public string Initials
        {
            get
            {
                var words = (Title ?? string.Empty).Split(new[] { ' ', ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
                return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
            }
        }

        // ---------------------------------------------------------------- prices

        public bool HasPrice => item.DisplayPrice.HasValue;

        public string PriceText
        {
            get
            {
                if (item.DisplayPrice.HasValue)
                {
                    return CurrencyFormatter.Format(item.DisplayPrice, item.Currency);
                }

                if (item.Price == null)
                {
                    return item.Entry?.PriceKey == null ? "No Steam id" : "Price pending";
                }

                if (!item.Price.Found)
                {
                    return "Not on GG.deals";
                }

                switch (Display.Settings.PricePreference)
                {
                    case PricePreference.Keyshop:
                        return "No keyshop offer";
                    case PricePreference.LowestOfBoth:
                        return "No current offer";
                    default:
                        return "No retail offer";
                }
            }
        }

        /// <summary>Always tells the user whether a price comes from a retail store or a keyshop.</summary>
        public string PriceSourceText
        {
            get
            {
                if (!item.DisplayCategory.HasValue)
                {
                    return null;
                }

                var category = item.DisplayCategory == PriceCategory.Keyshop ? "Keyshop" : "Retail";
                return string.IsNullOrWhiteSpace(item.Price?.CheapestStoreName) ? category : item.Price.CheapestStoreName + " · " + category;
            }
        }

        public bool IsKeyshopPrice => item.DisplayCategory == PriceCategory.Keyshop;

        public bool HasDiscount => item.DiscountPercent.HasValue && item.DiscountPercent.Value > 0;

        public string DiscountText => HasDiscount ? "-" + item.DiscountPercent.Value.ToString(CultureInfo.InvariantCulture) + "%" : null;

        public bool IsHistoricalLow => item.IsHistoricalLow;

        public bool HasHistoricalLow => item.DisplayHistoricalLow.HasValue;

        public string HistoricalLowText => CurrencyFormatter.Format(item.DisplayHistoricalLow, item.Currency);

        public string CurrentRetailText => Format(item.Price?.CurrentRetail);

        public string CurrentKeyshopText => Format(item.Price?.CurrentKeyshops);

        public string HistoricalRetailText => Format(item.Price?.HistoricalRetail);

        public string HistoricalKeyshopText => Format(item.Price?.HistoricalKeyshops);

        public string RegularPriceText => item.Price?.RegularPrice.HasValue == true ? Format(item.Price.RegularPrice) : null;

        public string Currency => item.Currency;

        public bool IsStale => item.IsPriceStale && item.Price != null;

        public string PriceUpdatedText => item.LastPriceUpdateUtc.HasValue && item.Price != null
            ? "Price updated " + item.LastPriceUpdateUtc.Value.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.CurrentCulture)
            : "Price not retrieved yet";

        public IReadOnlyList<StoreOffer> RetailOffers => item.Price?.Offers?.Where(o => o.Category == PriceCategory.Retail).OrderBy(o => o.Price).ToList() ?? new List<StoreOffer>();

        public IReadOnlyList<StoreOffer> KeyshopOffers => item.Price?.Offers?.Where(o => o.Category == PriceCategory.Keyshop).OrderBy(o => o.Price).ToList() ?? new List<StoreOffer>();

        public bool HasOffers => item.Price?.Offers != null && item.Price.Offers.Count > 0;

        public bool HasRetailOffers => RetailOffers.Count > 0;

        public bool HasKeyshopOffers => KeyshopOffers.Count > 0;

        public string StoreCountText => item.Price?.StoreCount.HasValue == true ? item.Price.StoreCount.Value + " stores" : null;

        // ---------------------------------------------------------------- price history (recorded by this extension)

        private const double ChartWidth = 380;
        private const double ChartHeight = 70;
        private const double SparkWidth = 80;
        private const double SparkHeight = 24;

        private PriceHistoryChart HistoryChart => historyChart ?? (historyChart = PriceHistoryChart.Build(item.PriceHistory, Display.Settings.PricePreference));

        /// <summary>True when there are at least two recorded prices to draw a line through.</summary>
        public bool HasPriceHistory => Display.ShowPriceHistory && HistoryChart.HasHistory;

        /// <summary>The game is tracked, but its price has not changed yet, so there is nothing to draw.</summary>
        public bool IsNewInHistory => Display.ShowPriceHistory && !HistoryChart.HasHistory;

        public PointCollection SparklinePoints => Scale(HistoryChart.Line, SparkWidth, SparkHeight, 2, 3);

        public PointCollection HistoryLinePoints => Scale(HistoryChart.Line, ChartWidth, ChartHeight, 6, 8);

        public Geometry HistoryMarkers
        {
            get
            {
                var group = new GeometryGroup();
                foreach (var marker in HistoryChart.Markers)
                {
                    group.Children.Add(new EllipseGeometry(new Point(6 + marker.X * (ChartWidth - 12), 8 + marker.Y * (ChartHeight - 16)), 3.2, 3.2));
                }

                group.Freeze();
                return group;
            }
        }

        public string HistoryMaxText => Format(HistoryChart.Max);

        public string HistoryMinText => Format(HistoryChart.Min);

        public string HistoryRangeText
        {
            get
            {
                var chart = HistoryChart;
                if (!chart.HasHistory)
                {
                    return null;
                }

                return chart.FromUtc.Value.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture)
                    + " – " + chart.ToUtc.Value.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture)
                    + " · " + chart.Markers.Count.ToString(CultureInfo.InvariantCulture) + " prices";
            }
        }

        /// <summary>Says plainly why there is no line yet, and that only this extension's own observations are shown.</summary>
        public string HistoryEmptyText
        {
            get
            {
                var series = item.PriceHistory;
                if (series == null || series.Points == null || series.Points.Count == 0)
                {
                    return "Not tracked yet. The price is recorded on the next refresh.";
                }

                return "Tracking since " + series.FirstSeenUtc.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture)
                    + ". The line appears once the price changes.";
            }
        }

        public string HistoryCaption => "Prices seen by this extension on each refresh";

        public string ExpandGlyph => IsExpanded ? "▴" : "▾";

        public string ExpandTooltip => IsExpanded ? "Hide price details" : "Show price details";

        private static PointCollection Scale(IReadOnlyList<ChartPoint> points, double width, double height, double padX, double padY)
        {
            var collection = new PointCollection();
            foreach (var point in points)
            {
                collection.Add(new Point(padX + point.X * (width - 2 * padX), padY + point.Y * (height - 2 * padY)));
            }

            collection.Freeze();
            return collection;
        }

        // ---------------------------------------------------------------- Playnite

        public bool IsOwned => item.IsOwned;

        public bool IsInstalled => item.IsInstalled;

        public bool IsPotentialMatch => item.Match != null && item.Match.IsPotential;

        public string OwnershipText => item.IsOwned ? "✓ Owned" : "🛒 Wishlist";

        public string InstalledText => item.IsInstalled ? "● Installed" : "○ Not installed";

        public string PotentialMatchText => IsPotentialMatch ? "Possibly in your library: " + item.Match.Game.Name : null;

        public string MatchDescription
        {
            get
            {
                switch (item.Match?.Kind ?? MatchKind.None)
                {
                    case MatchKind.SteamAppId:
                        return "Matched by Steam App ID";
                    case MatchKind.StoreLink:
                        return "Matched by Steam store link";
                    case MatchKind.ExactTitle:
                        return "Matched by exact title";
                    case MatchKind.UserConfirmed:
                        return "Confirmed by you";
                    case MatchKind.UserRejected:
                        return "Marked as not owned by you";
                    case MatchKind.PotentialTitle:
                        return "Similar title found - not counted as owned";
                    default:
                        return "Not found in your Playnite library";
                }
            }
        }

        public string LibraryGameName => item.IsOwned ? item.Match.Game.Name : null;

        public bool HasCoverOverride => !string.IsNullOrWhiteSpace(item.CoverOverride);

        public bool HasMatchOverride => item.Match != null && (item.Match.Kind == MatchKind.UserConfirmed || item.Match.Kind == MatchKind.UserRejected);

        // ---------------------------------------------------------------- local organisation

        public bool IsFavourite => item.IsFavourite;

        public string FavouriteGlyph => item.IsFavourite ? "★" : "☆";

        public string FavouriteTooltip => item.IsFavourite ? "Remove from favourites" : "Add to favourites";

        public bool HasCollections => item.CollectionIds != null && item.CollectionIds.Count > 0;

        public string CollectionsText => HasCollections ? describeCollections?.Invoke(item.CollectionIds) : null;

        // ---------------------------------------------------------------- metadata

        public string AddedDateText => item.Entry?.AddedUtc.HasValue == true ? "Added " + item.Entry.AddedUtc.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : null;

        public DateTime? ReleaseDate => item.Entry?.ReleaseDate ?? (item.IsOwned ? item.Match.Game.ReleaseDate : null);

        public string ReleaseDateText => ReleaseDate.HasValue ? "Released " + ReleaseDate.Value.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : null;

        public string PlatformText => !string.IsNullOrWhiteSpace(item.Entry?.Platform) ? item.Entry.Platform : item.IsOwned ? item.Match.Game.Platforms : null;

        public string RatingText => item.Entry?.Rating.HasValue == true ? "GG.deals rating " + item.Entry.Rating.Value.ToString("0.#", CultureInfo.CurrentCulture) : null;

        public bool HasMetadata => AddedDateText != null || ReleaseDateText != null || PlatformText != null || RatingText != null || StoreCountText != null;

        // ---------------------------------------------------------------- links

        public string GGDealsUrl => item.GGDealsUrl;

        public bool HasGGDealsUrl => !string.IsNullOrWhiteSpace(item.GGDealsUrl);

        /// <summary>Direct store URL, only when the provider supplies one (the current Prices API does not).</summary>
        public string StoreUrl => item.Price?.CheapestStoreUrl;

        public bool CanOpenStore => !string.IsNullOrWhiteSpace(StoreUrl);

        public string SteamStoreUrl
        {
            get
            {
                var entry = item.Entry;
                if (entry?.SteamId == null)
                {
                    return null;
                }

                var kind = entry.SteamIdType == SteamIdType.App ? "app" : entry.SteamIdType == SteamIdType.Sub ? "sub" : "bundle";
                return "https://store.steampowered.com/" + kind + "/" + entry.SteamId.Value.ToString(CultureInfo.InvariantCulture) + "/";
            }
        }

        public bool HasSteamStoreUrl => SteamStoreUrl != null;

        public bool CanOpenInPlaynite => item.IsOwned;

        public string Tooltip
        {
            get
            {
                var parts = new List<string> { Title };
                if (HasPrice)
                {
                    parts.Add(PriceText + " (" + PriceSourceText + ")");
                }

                if (IsHistoricalLow)
                {
                    parts.Add("At its historical low");
                }

                parts.Add(item.IsOwned ? (item.IsInstalled ? "Owned · Installed" : "Owned · Not installed") : "Not in your Playnite library");
                return string.Join(Environment.NewLine, parts);
            }
        }

        private string Format(decimal? value) => value.HasValue ? CurrencyFormatter.Format(value, item.Currency) : "—";
    }
}
