using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using GGDealsWishlist.Api;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Matching;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;
using GGDealsWishlist.Querying;
using GGDealsWishlist.Services;

namespace GGDealsWishlist.ViewModels
{
    public enum SidebarState
    {
        Loading = 0,
        NeedsApiKey = 1,
        WishlistUnavailable = 2,
        EmptyWishlist = 3,
        NoResults = 4,
        Content = 5,
        Error = 6
    }

    public enum StatusKind
    {
        None = 0,
        Success = 1,
        Warning = 2,
        Error = 3,
        Busy = 4
    }

    /// <summary>
    /// View model for the GG.deals sidebar. Search, filtering and sorting run locally over the service's
    /// snapshot; only the explicit refresh command (or the scheduler) ever reaches the network.
    /// </summary>
    public sealed class SidebarViewModel : ObservableBase, IDisposable
    {
        public const string GGDealsHomeUrl = "https://gg.deals/";
        public const string ApiInfoUrl = "https://gg.deals/api/";

        private readonly WishlistDataService service;
        private readonly IHostServices host;
        private readonly Dictionary<string, WishlistItemViewModel> itemCache = new Dictionary<string, WishlistItemViewModel>();
        private readonly DispatcherTimer searchTimer;
        private readonly DispatcherTimer clockTimer;
        private FilterState filter;
        private SortMode sort;
        private ViewMode viewMode;
        private bool viewModeChosen;
        private string searchText;
        private SidebarState state = SidebarState.Loading;
        private WishlistItemViewModel selectedDetail;
        private double availableWidth = 320;
        private bool isFilterPanelOpen;
        private string customMaxPriceText;
        private WishlistSummary summary = new WishlistSummary();
        private ServiceStatus status = new ServiceStatus();
        private string setupApiKey;
        private string setupStatus;
        private StatusKind setupStatusKind;
        private bool isTestingSetupKey;
        private bool justRefreshed;
        private int visibleCount;
        private IReadOnlyList<WishlistCollection> collections = new List<WishlistCollection>();

        public SidebarViewModel(WishlistDataService service, IHostServices host)
        {
            this.service = service;
            this.host = host;

            var ui = service.GetUiState();
            filter = ui.Filter ?? new FilterState();
            sort = ui.Sort;
            viewModeChosen = ui.ViewModeChosen;
            viewMode = viewModeChosen ? ui.ViewMode : host.Settings.DefaultViewMode;
            searchText = filter.SearchText;
            customMaxPriceText = filter.CustomMaxPrice?.ToString("0.##", CultureInfo.CurrentCulture);
            Display.Update(host.Settings, availableWidth);
            UpdateLayout();

            searchTimer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
            searchTimer.Tick += (s, e) =>
            {
                searchTimer.Stop();
                filter.SearchText = searchText;
                ApplyQuery(true);
                SaveUiState();
            };

            clockTimer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
            clockTimer.Tick += (s, e) => UpdateStatus();
            clockTimer.Start();

            RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => !IsRefreshing);
            RetryCommand = RefreshCommand;
            OpenSettingsCommand = new RelayCommand(() => host.OpenSettings());
            CycleViewModeCommand = new RelayCommand(() => ViewMode = (ViewMode)(((int)ViewMode + 1) % 4));
            ToggleExpandedCommand = new RelayCommand(p => ToggleExpanded(p as WishlistItemViewModel));
            SetViewModeCommand = new RelayCommand(p => { if (p is ViewMode mode) { ViewMode = mode; } else if (Enum.TryParse(p as string, out ViewMode parsed)) { ViewMode = parsed; } });
            SetQuickFilterCommand = new RelayCommand(p => { if (Enum.TryParse(p as string, out QuickFilter quick)) { Quick = quick; } else if (p is QuickFilter q) { Quick = q; } });
            ClearFiltersCommand = new RelayCommand(ClearFilters);
            ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
            ToggleFilterPanelCommand = new RelayCommand(() => IsFilterPanelOpen = !IsFilterPanelOpen);
            OpenDetailsCommand = new RelayCommand(p => OpenDetails(p as WishlistItemViewModel));
            CloseDetailsCommand = new RelayCommand(() => SelectedDetail = null);
            ToggleFavouriteCommand = new RelayCommand(p => ToggleFavourite(p as WishlistItemViewModel));
            OpenGGDealsCommand = new RelayCommand(p => OpenUrl((p as WishlistItemViewModel)?.GGDealsUrl), p => (p as WishlistItemViewModel)?.HasGGDealsUrl == true);
            OpenStoreCommand = new RelayCommand(p => OpenUrl((p as WishlistItemViewModel)?.StoreUrl), p => (p as WishlistItemViewModel)?.CanOpenStore == true);
            OpenSteamCommand = new RelayCommand(p => OpenUrl((p as WishlistItemViewModel)?.SteamStoreUrl), p => (p as WishlistItemViewModel)?.HasSteamStoreUrl == true);
            OpenInPlayniteCommand = new RelayCommand(p => OpenInPlaynite(p as WishlistItemViewModel), p => (p as WishlistItemViewModel)?.CanOpenInPlaynite == true);
            CopyLinkCommand = new RelayCommand(p => CopyLink(p as WishlistItemViewModel), p => (p as WishlistItemViewModel)?.HasGGDealsUrl == true);
            ConfirmMatchCommand = new RelayCommand(p => ConfirmMatch(p as WishlistItemViewModel));
            RejectMatchCommand = new RelayCommand(p => SetMatch(p as WishlistItemViewModel, PlayniteMatcher.RejectedOverride));
            ResetMatchCommand = new RelayCommand(p => SetMatch(p as WishlistItemViewModel, null));
            OpenWishlistCommand = new RelayCommand(() => OpenUrl(host.Settings.GGDealsWishlistUrl));
            OpenAttributionCommand = new RelayCommand(p => OpenUrl((p as WishlistItemViewModel)?.GGDealsUrl ?? GGDealsHomeUrl));
            OpenApiInfoCommand = new RelayCommand(() => OpenUrl(ApiInfoUrl));
            ManageCollectionsCommand = new RelayCommand(() => host.ShowCollectionsManager());
            NewCollectionForItemCommand = new RelayCommand(p => CreateCollectionFor(p is WishlistItemViewModel vm ? new[] { vm } : new WishlistItemViewModel[0]));
            TestSetupKeyCommand = new AsyncRelayCommand(TestSetupKeyAsync, () => !IsTestingSetupKey && !string.IsNullOrWhiteSpace(SetupApiKey));

            service.DataChanged += OnServiceDataChanged;
            service.StatusChanged += OnServiceStatusChanged;
            host.SettingsChanged += OnSettingsChanged;

            OnDataChanged();
            OnStatusChanged();
        }

        // =========================================================================================
        // Collections exposed to the view
        // =========================================================================================

        public BulkObservableCollection<WishlistItemViewModel> Items { get; } = new BulkObservableCollection<WishlistItemViewModel>();

        public ObservableCollection<CollectionFilterItem> CollectionFilters { get; } = new ObservableCollection<CollectionFilterItem>();

        public IReadOnlyList<WishlistCollection> Collections => collections;

        public bool HasCollections => CollectionFilters.Count > 0;

        public DisplayOptions Display { get; } = new DisplayOptions();

        /// <summary>Raised after the visible list changes because of a filter/sort change (the view scrolls to top).</summary>
        public event EventHandler QueryChanged;

        // =========================================================================================
        // Page state
        // =========================================================================================

        public SidebarState State
        {
            get => state;
            private set
            {
                if (SetValue(ref state, value))
                {
                    OnPropertyChanged(nameof(IsContentVisible));
                    OnPropertyChanged(nameof(ShowToolbar));
                }
            }
        }

        public bool IsContentVisible => State == SidebarState.Content || State == SidebarState.NoResults;

        public bool ShowToolbar => IsContentVisible;

        public string ProviderMessage => status.ProviderMessage;

        /// <summary>Card text when there is nothing to show; comes from the active wishlist source when it has a specific reason.</summary>
        public string UnavailableMessage => status.ProviderId == UnavailableWishlistProvider.ProviderId || string.IsNullOrEmpty(status.ProviderMessage)
            ? "Wishlist access is not currently available through the configured GG.deals API."
            : status.ProviderMessage;

        public string EmptyMessage => status.ProviderId == SteamWishlistProvider.ProviderId && !string.IsNullOrEmpty(status.ProviderMessage)
            ? status.ProviderMessage
            : "Your wishlist appears to be empty.";

        public bool IsSteamSource => status.ProviderId == SteamWishlistProvider.ProviderId;

        public bool HasApiKey => host.Settings.HasApiKey;

        // =========================================================================================
        // Header
        // =========================================================================================

        public string GameCountText => summary.Total == 1 ? "1 game" : summary.Total.ToString("N0", CultureInfo.CurrentCulture) + " games";

        public string OnSaleText => summary.OnSale.ToString("N0", CultureInfo.CurrentCulture) + " on sale";

        public string HistoricalLowCountText => summary.HistoricalLows == 1 ? "1 historical low" : summary.HistoricalLows.ToString("N0", CultureInfo.CurrentCulture) + " historical lows";

        public bool ShowGameCount => host.Settings.ShowGameCount;

        /// <summary>"On sale" is only shown when the provider actually supplies discount data.</summary>
        public bool ShowOnSaleCount => host.Settings.ShowOnSaleCount && summary.HasDiscountData;

        public bool ShowHistoricalLowCount => host.Settings.ShowHistoricalLowCount;

        public bool ShowSummary => ShowGameCount || ShowOnSaleCount || ShowHistoricalLowCount;

        public string FilteredCountText => visibleCount == summary.Total ? null : "Showing " + visibleCount.ToString("N0", CultureInfo.CurrentCulture) + " of " + summary.Total.ToString("N0", CultureInfo.CurrentCulture);

        public bool IsRefreshing => status.IsRefreshing;

        public bool ShowLastUpdated => host.Settings.ShowLastUpdated;

        public string StatusText
        {
            get
            {
                if (status.IsRefreshing)
                {
                    return "↻ " + (status.ProgressText ?? "Updating…");
                }

                var last = LatestUpdateUtc;
                if (!last.HasValue)
                {
                    return "Not updated yet";
                }

                var age = DateTime.UtcNow - last.Value;
                if (justRefreshed && age < TimeSpan.FromMinutes(1))
                {
                    return "Updated just now";
                }

                return "Last updated " + FormatTime(last.Value);
            }
        }

        public string StatusTooltip
        {
            get
            {
                var lines = new List<string>
                {
                    "Wishlist: " + (status.WishlistUpdatedUtc.HasValue ? FormatTime(status.WishlistUpdatedUtc.Value) : "never"),
                    "Prices: " + (status.PricesUpdatedUtc.HasValue ? FormatTime(status.PricesUpdatedUtc.Value) : "never"),
                    "Source: " + (status.ProviderName ?? "—")
                };
                if (!string.IsNullOrEmpty(status.LastInfoMessage))
                {
                    lines.Add(status.LastInfoMessage);
                }

                return string.Join(Environment.NewLine, lines);
            }
        }

        public double ProgressValue => status.ProgressTotal > 0 ? (double)status.ProgressDone / status.ProgressTotal : 0;

        public bool HasProgress => status.IsRefreshing && status.ProgressTotal > 0;

        private DateTime? LatestUpdateUtc
        {
            get
            {
                var a = status.PricesUpdatedUtc;
                var b = status.WishlistUpdatedUtc;
                if (!a.HasValue)
                {
                    return b;
                }

                return !b.HasValue || a.Value > b.Value ? a : b;
            }
        }

        // =========================================================================================
        // Non-blocking warning / unable-to-refresh card
        // =========================================================================================

        public bool HasWarning => status.LastError != null && status.LastError.Kind != ApiErrorKind.Cancelled && !status.IsRefreshing && State != SidebarState.NeedsApiKey && State != SidebarState.Error;

        public string WarningTitle
        {
            get
            {
                if (!string.IsNullOrEmpty(status.LastError?.Source))
                {
                    return "⚠ Unable to refresh your " + status.LastError.Source;
                }

                switch (status.LastError?.Kind ?? ApiErrorKind.None)
                {
                    case ApiErrorKind.RateLimited:
                        return "⚠ GG.deals API rate limit reached";
                    case ApiErrorKind.InvalidApiKey:
                        return "✕ API key is invalid";
                    case ApiErrorKind.NotConfigured:
                        return "Add your GG.deals API key";
                    case ApiErrorKind.Server:
                        return "⚠ GG.deals is currently unavailable";
                    case ApiErrorKind.UnexpectedResponse:
                        return "⚠ GG.deals returned an unexpected response";
                    default:
                        return "⚠ Unable to refresh";
                }
            }
        }

        public string WarningMessage
        {
            get
            {
                var error = status.LastError;
                if (error == null)
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(error.Source) && !string.IsNullOrEmpty(error.Message))
                {
                    return error.Message;
                }

                switch (error.Kind)
                {
                    case ApiErrorKind.RateLimited:
                        return error.RetryAfterUtc.HasValue
                            ? "Please try again after " + error.RetryAfterUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) + "."
                            : "Please try again later.";
                    case ApiErrorKind.Network:
                        return "GG.deals could not be reached right now.";
                    case ApiErrorKind.InvalidApiKey:
                        return "Replace the key in the extension settings.";
                    case ApiErrorKind.NotConfigured:
                        return "Prices appear once an API key is configured.";
                    default:
                        return "Showing the most recent cached data.";
                }
            }
        }

        public string LastSuccessText => status.PricesUpdatedUtc.HasValue
            ? "Last successful update: " + status.PricesUpdatedUtc.Value.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)
            : null;

        public bool CanRetry
        {
            get
            {
                var kind = status.LastError?.Kind ?? ApiErrorKind.None;
                if (kind == ApiErrorKind.InvalidApiKey || kind == ApiErrorKind.NotConfigured)
                {
                    return false;
                }

                return kind != ApiErrorKind.RateLimited || !status.LastError.RetryAfterUtc.HasValue || status.LastError.RetryAfterUtc.Value <= DateTime.UtcNow;
            }
        }

        public bool ShowSettingsInWarning => status.LastError?.Kind == ApiErrorKind.InvalidApiKey || status.LastError?.Kind == ApiErrorKind.NotConfigured;

        public bool IsShowingStaleData => status.LastError != null && status.HasCachedData;

        // =========================================================================================
        // First run
        // =========================================================================================

        public string SetupApiKey
        {
            get => setupApiKey;
            set
            {
                if (SetValue(ref setupApiKey, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string SetupStatus
        {
            get => setupStatus;
            private set => SetValue(ref setupStatus, value);
        }

        public StatusKind SetupStatusKind
        {
            get => setupStatusKind;
            private set => SetValue(ref setupStatusKind, value);
        }

        public bool IsTestingSetupKey
        {
            get => isTestingSetupKey;
            private set => SetValue(ref isTestingSetupKey, value);
        }

        // =========================================================================================
        // View mode / responsive layout
        // =========================================================================================

        public ViewMode ViewMode
        {
            get => viewMode;
            set
            {
                if (SetValue(ref viewMode, value))
                {
                    viewModeChosen = true;
                    OnPropertyChanged(nameof(EffectiveViewMode));
                    OnPropertyChanged(nameof(ViewModeGlyph));
                    OnPropertyChanged(nameof(ViewModeTooltip));
                    UpdateLayout();
                    SaveUiState();
                }
            }
        }

        /// <summary>Cover cards fall back to compact rows when the sidebar is too narrow for them.</summary>
        public ViewMode EffectiveViewMode => (viewMode == ViewMode.CoverInfo || viewMode == ViewMode.Grid) && availableWidth < 230 ? ViewMode.Compact : viewMode;

        public string ViewModeGlyph => viewMode == ViewMode.CoverInfo ? "▦" : viewMode == ViewMode.Compact ? "☷" : viewMode == ViewMode.List ? "☰" : "⊞";

        public string ViewModeTooltip => "View: " + (viewMode == ViewMode.CoverInfo ? "Cover + information" : viewMode == ViewMode.Compact ? "Compact" : viewMode == ViewMode.List ? "List" : "Grid") + " (click to change)";

        private string layoutKey;

        /// <summary>
        /// Tells the display options which layout is showing and, when the number of columns (or the switch between
        /// stacked and wrapped) changes, refreshes every item so its slot width follows.
        /// </summary>
        private void UpdateLayout()
        {
            Display.SetLayout(EffectiveViewMode);
            var key = Display.IsWrapped.ToString() + ":" + Display.ColumnCount.ToString(CultureInfo.InvariantCulture);
            var columnsChanged = key != layoutKey;
            layoutKey = key;
            foreach (var vm in itemCache.Values)
            {
                if (columnsChanged)
                {
                    vm.RefreshDisplay();
                }
                else if (Display.IsWrapped)
                {
                    vm.RefreshLayout();
                }
            }
        }

        /// <summary>Cover cards and grid tiles open in place; the compact modes have no room, so they open the full details.</summary>
        public bool SupportsInlineExpand => EffectiveViewMode == ViewMode.CoverInfo || EffectiveViewMode == ViewMode.Grid;

        /// <summary>What a click or Enter on a wishlist row does in the current view.</summary>
        public void ActivateItem(WishlistItemViewModel item)
        {
            if (SupportsInlineExpand)
            {
                ToggleExpanded(item);
            }
            else
            {
                OpenDetails(item);
            }
        }

        /// <summary>Opens a card in place (closing any other open card); a second toggle closes it again.</summary>
        public void ToggleExpanded(WishlistItemViewModel item)
        {
            if (item == null)
            {
                return;
            }

            var open = !item.IsExpanded;
            foreach (var other in itemCache.Values)
            {
                other.IsExpanded = false;
            }

            item.IsExpanded = open;
        }

        public double AvailableWidth
        {
            get => availableWidth;
            set
            {
                if (Math.Abs(availableWidth - value) < 1)
                {
                    return;
                }

                var oldNarrow = Display.IsNarrow;
                var oldWide = Display.IsWide;
                availableWidth = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectiveViewMode));
                Display.Update(host.Settings, value);
                if (oldNarrow != Display.IsNarrow || oldWide != Display.IsWide)
                {
                    foreach (var vm in itemCache.Values)
                    {
                        vm.RefreshDisplay();
                    }
                }

                UpdateLayout();
            }
        }

        // =========================================================================================
        // Search / sort / filters
        // =========================================================================================

        public string SearchText
        {
            get => searchText;
            set
            {
                if (SetValue(ref searchText, value))
                {
                    OnPropertyChanged(nameof(HasSearchText));
                    searchTimer.Stop();
                    searchTimer.Start();
                }
            }
        }

        public bool HasSearchText => !string.IsNullOrEmpty(searchText);

        public SortMode Sort
        {
            get => sort;
            set
            {
                if (SetValue(ref sort, value))
                {
                    ApplyQuery(true);
                    SaveUiState();
                }
            }
        }

        public IReadOnlyList<Option<SortMode>> SortOptions => new List<Option<SortMode>>
        {
            new Option<SortMode>(SortMode.WishlistOrder, "Wishlist order"),
            new Option<SortMode>(SortMode.PriceAscending, "Price: low to high"),
            new Option<SortMode>(SortMode.PriceDescending, "Price: high to low"),
            new Option<SortMode>(SortMode.DiscountDescending, summary.HasDiscountData ? "Discount: highest" : "Discount: highest (no data)", summary.HasDiscountData || sort == SortMode.DiscountDescending),
            new Option<SortMode>(SortMode.DiscountAscending, summary.HasDiscountData ? "Discount: lowest" : "Discount: lowest (no data)", summary.HasDiscountData || sort == SortMode.DiscountAscending),
            new Option<SortMode>(SortMode.HistoricalLow, "Historical low price"),
            new Option<SortMode>(SortMode.RecentlyAdded, summary.HasAddedDates ? "Recently added" : "Recently added (no data)", summary.HasAddedDates || sort == SortMode.RecentlyAdded),
            new Option<SortMode>(SortMode.RecentlyDiscounted, summary.HasDiscountDates ? "Recently discounted" : "Recently discounted (no data)", summary.HasDiscountDates || sort == SortMode.RecentlyDiscounted),
            new Option<SortMode>(SortMode.NameAscending, "Name: A–Z"),
            new Option<SortMode>(SortMode.NameDescending, "Name: Z–A")
        };

        public IReadOnlyList<Option<PriceFilter>> PriceOptions => new List<Option<PriceFilter>>
        {
            new Option<PriceFilter>(PriceFilter.Any, "Any price"),
            new Option<PriceFilter>(PriceFilter.Under5, "Under " + FormatThreshold(5)),
            new Option<PriceFilter>(PriceFilter.Under10, "Under " + FormatThreshold(10)),
            new Option<PriceFilter>(PriceFilter.Under20, "Under " + FormatThreshold(20)),
            new Option<PriceFilter>(PriceFilter.Custom, "Custom…")
        };

        public IReadOnlyList<Option<int>> DiscountOptions { get; } = new List<Option<int>>
        {
            new Option<int>(0, "Any discount"),
            new Option<int>(10, "10%+"),
            new Option<int>(25, "25%+"),
            new Option<int>(50, "50%+"),
            new Option<int>(75, "75%+")
        };

        public IReadOnlyList<Option<StoreFilter>> StoreOptions { get; } = new List<Option<StoreFilter>>
        {
            new Option<StoreFilter>(StoreFilter.Any, "Any store"),
            new Option<StoreFilter>(StoreFilter.Retail, "Retail stores"),
            new Option<StoreFilter>(StoreFilter.Keyshop, "Keyshops")
        };

        public IReadOnlyList<Option<SaleStatusFilter>> SaleStatusOptions { get; } = new List<Option<SaleStatusFilter>>
        {
            new Option<SaleStatusFilter>(SaleStatusFilter.All, "All"),
            new Option<SaleStatusFilter>(SaleStatusFilter.OnSale, "On sale"),
            new Option<SaleStatusFilter>(SaleStatusFilter.NotOnSale, "Not on sale")
        };

        public IReadOnlyList<Option<OwnershipFilter>> OwnershipOptions { get; } = new List<Option<OwnershipFilter>>
        {
            new Option<OwnershipFilter>(OwnershipFilter.All, "All"),
            new Option<OwnershipFilter>(OwnershipFilter.Owned, "Owned"),
            new Option<OwnershipFilter>(OwnershipFilter.NotOwned, "Not owned")
        };

        public IReadOnlyList<Option<FavouriteFilter>> FavouriteOptions { get; } = new List<Option<FavouriteFilter>>
        {
            new Option<FavouriteFilter>(FavouriteFilter.All, "All"),
            new Option<FavouriteFilter>(FavouriteFilter.Favourites, "Favourites"),
            new Option<FavouriteFilter>(FavouriteFilter.NonFavourites, "Non-favourites")
        };

        public PriceFilter PriceFilter
        {
            get => filter.Price;
            set => UpdateFilter(() => filter.Price = value);
        }

        public bool IsCustomPrice => filter.Price == PriceFilter.Custom;

        public string CustomMaxPriceText
        {
            get => customMaxPriceText;
            set
            {
                if (SetValue(ref customMaxPriceText, value))
                {
                    decimal parsed;
                    var ok = decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed)
                        || decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed);
                    UpdateFilter(() => filter.CustomMaxPrice = ok && parsed >= 0 ? parsed : (decimal?)null);
                }
            }
        }

        public int MinDiscount
        {
            get => filter.MinDiscount;
            set => UpdateFilter(() => filter.MinDiscount = value);
        }

        public StoreFilter StoreFilter
        {
            get => filter.Store;
            set => UpdateFilter(() => filter.Store = value);
        }

        public SaleStatusFilter SaleStatus
        {
            get => filter.SaleStatus;
            set => UpdateFilter(() => filter.SaleStatus = value);
        }

        public bool HistoricalLowOnly
        {
            get => filter.HistoricalLowOnly;
            set => UpdateFilter(() => filter.HistoricalLowOnly = value);
        }

        public OwnershipFilter Ownership
        {
            get => filter.Ownership;
            set => UpdateFilter(() => filter.Ownership = value);
        }

        public FavouriteFilter Favourites
        {
            get => filter.Favourites;
            set => UpdateFilter(() => filter.Favourites = value);
        }

        /// <summary>ALL | ON SALE | HISTORICAL LOW quick tabs.</summary>
        public QuickFilter Quick
        {
            get => filter.Quick;
            set => UpdateFilter(() =>
            {
                filter.HistoricalLowOnly = value == QuickFilter.HistoricalLow;
                filter.SaleStatus = value == QuickFilter.OnSale ? SaleStatusFilter.OnSale : SaleStatusFilter.All;
            });
        }

        public bool IsOnSaleAvailable => summary.HasDiscountData;

        public string OnSaleTabTooltip => summary.HasDiscountData
            ? "Games with a current discount"
            : "The GG.deals Prices API does not report discounts, so sale status is not available yet.";

        /// <summary>Playnite setting shortcut: "Hide games already owned".</summary>
        public bool HideOwned
        {
            get => host.Settings.HideOwned;
            set
            {
                if (host.Settings.HideOwned != value)
                {
                    host.UpdateSettings(s => s.HideOwned = value);
                }
            }
        }

        public int ActiveFilterCount => filter.ActiveCount;

        public bool HasActiveFilters => filter.ActiveCount > 0 || HasSearchText;

        public string FilterButtonText => filter.ActiveCount > 0 ? "Filter (" + filter.ActiveCount + ")" : "Filter";

        public bool IsFilterPanelOpen
        {
            get => isFilterPanelOpen;
            set => SetValue(ref isFilterPanelOpen, value);
        }

        // =========================================================================================
        // Details
        // =========================================================================================

        public WishlistItemViewModel SelectedDetail
        {
            get => selectedDetail;
            set
            {
                if (SetValue(ref selectedDetail, value))
                {
                    OnPropertyChanged(nameof(IsDetailOpen));
                }
            }
        }

        public bool IsDetailOpen => selectedDetail != null;

        // =========================================================================================
        // Commands
        // =========================================================================================

        public ICommand RefreshCommand { get; }
        public ICommand RetryCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand CycleViewModeCommand { get; }
        public ICommand ToggleExpandedCommand { get; }
        public ICommand SetViewModeCommand { get; }
        public ICommand SetQuickFilterCommand { get; }
        public ICommand ClearFiltersCommand { get; }
        public ICommand ClearSearchCommand { get; }
        public ICommand ToggleFilterPanelCommand { get; }
        public ICommand OpenDetailsCommand { get; }
        public ICommand CloseDetailsCommand { get; }
        public ICommand ToggleFavouriteCommand { get; }
        public ICommand OpenGGDealsCommand { get; }
        public ICommand OpenStoreCommand { get; }
        public ICommand OpenSteamCommand { get; }
        public ICommand OpenInPlayniteCommand { get; }
        public ICommand CopyLinkCommand { get; }
        public ICommand ConfirmMatchCommand { get; }
        public ICommand RejectMatchCommand { get; }
        public ICommand ResetMatchCommand { get; }
        public ICommand OpenWishlistCommand { get; }
        public ICommand OpenAttributionCommand { get; }
        public ICommand OpenApiInfoCommand { get; }
        public ICommand ManageCollectionsCommand { get; }
        public ICommand NewCollectionForItemCommand { get; }
        public ICommand TestSetupKeyCommand { get; }

        // =========================================================================================
        // Actions
        // =========================================================================================

        public Task RefreshAsync()
        {
            justRefreshed = false;
            return Task.Run(async () =>
            {
                var outcome = await service.RefreshAsync(RefreshTrigger.Manual).ConfigureAwait(false);
                if (outcome == RefreshOutcome.Completed || outcome == RefreshOutcome.NothingToDo)
                {
                    justRefreshed = true;
                }

                _ = host.Dispatcher.BeginInvoke(new Action(UpdateStatus));
            });
        }

        public void OpenDetails(WishlistItemViewModel item)
        {
            if (item != null)
            {
                IsFilterPanelOpen = false;
                SelectedDetail = item;
            }
        }

        public void ToggleFavourite(WishlistItemViewModel item)
        {
            if (item != null)
            {
                service.SetFavourite(item.Key, !item.IsFavourite);
            }
        }

        public void SetFavourite(IEnumerable<WishlistItemViewModel> items, bool favourite)
        {
            foreach (var item in items.Where(i => i != null))
            {
                service.SetFavourite(item.Key, favourite);
            }
        }

        public void SetCollection(IEnumerable<WishlistItemViewModel> items, string collectionId, bool member)
        {
            service.SetCollectionMembership(items.Where(i => i != null).Select(i => i.Key).ToList(), collectionId, member);
        }

        public void CreateCollectionFor(IReadOnlyCollection<WishlistItemViewModel> items)
        {
            var name = host.PromptText("Name of the new collection:", "New collection", string.Empty);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var collection = service.CreateCollection(name, null);
            if (collection != null && items.Count > 0)
            {
                SetCollection(items, collection.Id, true);
            }
        }

        public void OpenUrl(string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                host.OpenUrl(url);
            }
        }

        public void OpenInPlaynite(WishlistItemViewModel item)
        {
            var id = item?.Item.PlayniteGameId;
            if (id.HasValue)
            {
                host.OpenPlayniteGame(id.Value);
            }
        }

        public void CopyLink(WishlistItemViewModel item)
        {
            if (item?.HasGGDealsUrl == true)
            {
                host.CopyToClipboard(item.GGDealsUrl);
            }
        }

        public void ConfirmMatch(WishlistItemViewModel item)
        {
            var game = item?.Item.Match?.Game;
            if (game != null)
            {
                SetMatch(item, game.Id.ToString());
            }
        }

        public void SetMatch(WishlistItemViewModel item, string value)
        {
            if (item != null)
            {
                service.SetMatchOverride(item.Key, value);
            }
        }

        public string DescribeCollections(IEnumerable<string> ids)
        {
            var names = ids.Select(id => collections.FirstOrDefault(c => c.Id == id))
                .Where(c => c != null)
                .Select(c => (string.IsNullOrEmpty(c.Icon) ? string.Empty : c.Icon + " ") + c.Name);
            return string.Join("  ·  ", names);
        }

        public void ClearFilters()
        {
            filter = new FilterState { SearchText = filter.SearchText };
            customMaxPriceText = null;
            foreach (var item in CollectionFilters)
            {
                item.SetSelectedSilently(false);
            }

            OnFilterPropertiesChanged();
            ApplyQuery(true);
            SaveUiState();
        }

        private async Task TestSetupKeyAsync()
        {
            var key = SetupApiKey?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            IsTestingSetupKey = true;
            SetupStatusKind = StatusKind.Busy;
            SetupStatus = "Testing connection…";
            try
            {
                var result = await service.TestConnectionAsync(key, host.Settings.Region);
                if (result.IsSuccess)
                {
                    host.SaveApiKey(key);
                    SetupApiKey = null;
                    SetupStatusKind = StatusKind.Success;
                    SetupStatus = "✓ Connected to GG.deals";
                    await RefreshAsync();
                }
                else
                {
                    SetupStatusKind = result.Error == ApiErrorKind.RateLimited || result.Error == ApiErrorKind.Network || result.Error == ApiErrorKind.Server ? StatusKind.Warning : StatusKind.Error;
                    SetupStatus = result.Message ?? ApiErrorMessages.For(result.Error, result.RetryAfterUtc);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Connection test failed");
                SetupStatusKind = StatusKind.Error;
                SetupStatus = ApiErrorMessages.For(ApiErrorKind.UnexpectedResponse, null);
            }
            finally
            {
                IsTestingSetupKey = false;
            }
        }

        // =========================================================================================
        // Data flow
        // =========================================================================================

        private void OnServiceDataChanged(object sender, EventArgs e) => host.Dispatcher.BeginInvoke(new Action(OnDataChanged), DispatcherPriority.Background);

        private void OnServiceStatusChanged(object sender, EventArgs e) => host.Dispatcher.BeginInvoke(new Action(OnStatusChanged), DispatcherPriority.Background);

        private void OnSettingsChanged(object sender, EventArgs e) => host.Dispatcher.BeginInvoke(new Action(ApplySettings));

        private void ApplySettings()
        {
            Display.Update(host.Settings, availableWidth);
            if (!viewModeChosen)
            {
                viewMode = host.Settings.DefaultViewMode;
                OnPropertyChanged(nameof(ViewMode));
                OnPropertyChanged(nameof(EffectiveViewMode));
                OnPropertyChanged(nameof(ViewModeGlyph));
            }

            foreach (var vm in itemCache.Values)
            {
                vm.RefreshDisplay();
            }

            UpdateLayout();
            OnPropertyChanged(nameof(HideOwned));
            OnPropertyChanged(nameof(HasApiKey));
            OnPropertyChanged(nameof(PriceOptions));
            ApplyQuery(false);
            UpdateStatus();
        }

        private void OnDataChanged()
        {
            try
            {
                collections = service.Collections;
                RebuildCollectionFilters();
                var snapshot = service.Items;
                var liveKeys = new HashSet<string>();
                foreach (var item in snapshot)
                {
                    if (item?.Key == null)
                    {
                        continue;
                    }

                    liveKeys.Add(item.Key);
                    if (itemCache.TryGetValue(item.Key, out var vm))
                    {
                        if (!ReferenceEquals(vm.Item, item))
                        {
                            vm.Update(item);
                        }
                    }
                    else
                    {
                        itemCache[item.Key] = new WishlistItemViewModel(item, Display, DescribeCollections);
                    }
                }

                foreach (var stale in itemCache.Keys.Where(k => !liveKeys.Contains(k)).ToList())
                {
                    itemCache.Remove(stale);
                }

                if (selectedDetail != null && !itemCache.ContainsKey(selectedDetail.Key))
                {
                    SelectedDetail = null;
                }

                ApplyQuery(false);
                OnPropertyChanged(nameof(Collections));
                OnPropertyChanged(nameof(PriceOptions));
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to update the sidebar");
            }
        }

        private void OnStatusChanged()
        {
            status = service.Status;
            UpdateState();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            OnPropertyChanged(nameof(IsRefreshing));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusTooltip));
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(HasProgress));
            OnPropertyChanged(nameof(HasWarning));
            OnPropertyChanged(nameof(WarningTitle));
            OnPropertyChanged(nameof(WarningMessage));
            OnPropertyChanged(nameof(LastSuccessText));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(ShowSettingsInWarning));
            OnPropertyChanged(nameof(IsShowingStaleData));
            OnPropertyChanged(nameof(ProviderMessage));
            OnPropertyChanged(nameof(UnavailableMessage));
            OnPropertyChanged(nameof(EmptyMessage));
            OnPropertyChanged(nameof(IsSteamSource));
            OnPropertyChanged(nameof(ShowLastUpdated));
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>Runs the local pipeline: raw snapshot → filters → search → sort → virtualised list.</summary>
        public void ApplyQuery(bool userInitiated)
        {
            var options = new QueryOptions { HideOwned = host.Settings.HideOwned, SearchIncludesExtra = host.Settings.SearchIncludesExtra };
            var snapshot = service.Items;
            summary = WishlistQueryEngine.Summarize(snapshot, options);
            var results = WishlistQueryEngine.Apply(snapshot, filter, sort, options);
            var vms = new List<WishlistItemViewModel>(results.Count);
            foreach (var item in results)
            {
                if (itemCache.TryGetValue(item.Key, out var vm))
                {
                    vms.Add(vm);
                }
            }

            if (!vms.SequenceEqual(Items))
            {
                Items.ReplaceAll(vms);
            }

            visibleCount = vms.Count;
            OnPropertyChanged(nameof(GameCountText));
            OnPropertyChanged(nameof(OnSaleText));
            OnPropertyChanged(nameof(HistoricalLowCountText));
            OnPropertyChanged(nameof(ShowGameCount));
            OnPropertyChanged(nameof(ShowOnSaleCount));
            OnPropertyChanged(nameof(ShowHistoricalLowCount));
            OnPropertyChanged(nameof(ShowSummary));
            OnPropertyChanged(nameof(FilteredCountText));
            OnPropertyChanged(nameof(SortOptions));
            OnPropertyChanged(nameof(IsOnSaleAvailable));
            OnPropertyChanged(nameof(OnSaleTabTooltip));
            OnPropertyChanged(nameof(HasActiveFilters));
            OnPropertyChanged(nameof(ActiveFilterCount));
            OnPropertyChanged(nameof(FilterButtonText));
            UpdateState();
            if (userInitiated)
            {
                QueryChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void UpdateState()
        {
            var hasItems = service.Items.Count > 0;
            SidebarState next;
            if (!status.IsInitialized)
            {
                next = SidebarState.Loading;
            }
            else if (!host.Settings.HasApiKey && !hasItems)
            {
                next = SidebarState.NeedsApiKey;
            }
            else if (!hasItems && status.ProviderState != WishlistProviderState.Available && status.ProviderState != WishlistProviderState.Error)
            {
                next = SidebarState.WishlistUnavailable;
            }
            else if (!hasItems && status.LastError != null && status.LastError.Kind != ApiErrorKind.NotConfigured && !status.WishlistUpdatedUtc.HasValue)
            {
                next = SidebarState.Error;
            }
            else if (!hasItems)
            {
                next = SidebarState.EmptyWishlist;
            }
            else
            {
                next = Items.Count == 0 ? SidebarState.NoResults : SidebarState.Content;
            }

            State = next;
            OnPropertyChanged(nameof(HasWarning));
        }

        private void RebuildCollectionFilters()
        {
            var selected = new HashSet<string>(filter.CollectionIds ?? new List<string>());
            filter.CollectionIds = (filter.CollectionIds ?? new List<string>()).Where(id => collections.Any(c => c.Id == id)).ToList();
            CollectionFilters.Clear();
            foreach (var collection in collections)
            {
                CollectionFilters.Add(new CollectionFilterItem(collection, service.CountInCollection(collection.Id), selected.Contains(collection.Id), OnCollectionFilterChanged));
            }

            OnPropertyChanged(nameof(HasCollections));
        }

        private void OnCollectionFilterChanged()
        {
            filter.CollectionIds = CollectionFilters.Where(c => c.IsSelected).Select(c => c.Collection.Id).ToList();
            ApplyQuery(true);
            SaveUiState();
        }

        private void UpdateFilter(Action change)
        {
            change();
            OnFilterPropertiesChanged();
            ApplyQuery(true);
            SaveUiState();
        }

        private void OnFilterPropertiesChanged()
        {
            OnPropertyChanged(nameof(PriceFilter));
            OnPropertyChanged(nameof(IsCustomPrice));
            OnPropertyChanged(nameof(CustomMaxPriceText));
            OnPropertyChanged(nameof(MinDiscount));
            OnPropertyChanged(nameof(StoreFilter));
            OnPropertyChanged(nameof(SaleStatus));
            OnPropertyChanged(nameof(HistoricalLowOnly));
            OnPropertyChanged(nameof(Ownership));
            OnPropertyChanged(nameof(Favourites));
            OnPropertyChanged(nameof(Quick));
        }

        private void SaveUiState()
        {
            service.SaveUiState(new UiState { ViewMode = viewMode, ViewModeChosen = viewModeChosen, Sort = sort, Filter = filter.Clone() });
        }

        private string FormatThreshold(int amount)
        {
            var currency = service.Items.Select(i => i.Currency).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? Regions.Find(host.Settings.Region)?.Currency;
            var text = CurrencyFormatter.Format(amount, currency);
            return text?.Replace(".00", string.Empty);
        }

        private static string FormatTime(DateTime utc)
        {
            var local = utc.ToLocalTime();
            return local.Date == DateTime.Today
                ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
                : local.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);
        }

        public void Dispose()
        {
            searchTimer.Stop();
            clockTimer.Stop();
            service.DataChanged -= OnServiceDataChanged;
            service.StatusChanged -= OnServiceStatusChanged;
            host.SettingsChanged -= OnSettingsChanged;
        }
    }
}
