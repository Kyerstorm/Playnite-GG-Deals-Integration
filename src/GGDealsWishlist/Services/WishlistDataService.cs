using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GGDealsWishlist.Api;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Matching;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;

namespace GGDealsWishlist.Services
{
    /// <summary>Read access to the Playnite library as lightweight projections.</summary>
    public interface ILibrarySource
    {
        IReadOnlyList<LibraryGameInfo> GetGames();
    }

    /// <summary>Snapshot of the settings the data layer needs (the service never touches the settings UI).</summary>
    public sealed class ServiceOptions
    {
        public string Region { get; set; } = Regions.DefaultCode;

        public PricePreference PricePreference { get; set; } = PricePreference.Retail;

        public int RefreshIntervalMinutes { get; set; } = 60;

        public bool AutoRefresh { get; set; } = true;

        public bool EnableMatching { get; set; } = true;

        public bool TreatExactTitleAsOwned { get; set; } = true;

        public bool HasApiKey { get; set; }

        /// <summary>Every wishlist the user can switch to, the user's own first. Wishlists are never combined.</summary>
        public List<WishlistScope> Wishlists { get; set; } = new List<WishlistScope> { WishlistScope.Own() };
    }

    public enum RefreshTrigger
    {
        Startup = 0,
        Automatic = 1,
        Manual = 2
    }

    public enum RefreshOutcome
    {
        Completed = 0,
        AlreadyRunning = 1,
        NothingToDo = 2,
        Failed = 3,
        Partial = 4
    }

    public sealed class ServiceStatus
    {
        public bool IsInitialized { get; set; }

        public bool IsRefreshing { get; set; }

        public string ProgressText { get; set; }

        public int ProgressDone { get; set; }

        public int ProgressTotal { get; set; }

        public DateTime? WishlistUpdatedUtc { get; set; }

        public DateTime? PricesUpdatedUtc { get; set; }

        public DateTime? LastAttemptUtc { get; set; }

        public DateTime? LastSuccessfulRefreshUtc { get; set; }

        /// <summary>Last refresh failure; cleared after a successful refresh.</summary>
        public ErrorRecord LastError { get; set; }

        public WishlistProviderState ProviderState { get; set; }

        public string ProviderId { get; set; }

        public string ProviderName { get; set; }

        public string ProviderMessage { get; set; }

        public WishlistCapabilities ProviderCapabilities { get; set; }

        public bool HasApiKey { get; set; }

        public bool HasCachedData { get; set; }

        public DateTime? RetryAfterUtc { get; set; }

        public string LastInfoMessage { get; set; }

        public ServiceStatus Clone() => (ServiceStatus)MemberwiseClone();
    }

    /// <summary>
    /// Central data service. Combines the wishlist provider, price provider, persistent cache, Playnite
    /// matching and local organisation state into immutable <see cref="WishlistItem"/> snapshots.
    /// Wishlist refresh and price refresh are tracked separately. All network work happens off the UI thread;
    /// consumers receive <see cref="DataChanged"/>/<see cref="StatusChanged"/> events on arbitrary threads.
    /// </summary>
    public sealed class WishlistDataService : IDisposable
    {
        public static readonly TimeSpan ManualRefreshMinimumAge = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan NotFoundRecheckInterval = TimeSpan.FromHours(24);

        private readonly object sync = new object();
        private readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1, 1);
        private readonly WishlistProviderRegistry registry;
        private readonly IGGDealsPriceProvider priceProvider;
        private readonly RateLimitTracker rateLimits;
        private readonly VersionedJsonStore<CacheDocument> cacheStore;
        private readonly VersionedJsonStore<LocalStateDocument> stateStore;
        private readonly VersionedJsonStore<PriceHistoryDocument> historyStore;
        private readonly ILibrarySource library;
        private readonly Func<ServiceOptions> options;
        private readonly IClock clock;

        private CacheDocument cache = new CacheDocument();
        private PriceHistoryDocument history = new PriceHistoryDocument();
        private bool historyDirty;
        private LocalStateDocument state = new LocalStateDocument();
        private PlayniteMatcher matcher = new PlayniteMatcher(null);
        private Dictionary<string, MatchResult> matchCache = new Dictionary<string, MatchResult>();
        private IReadOnlyList<WishlistItem> items = new WishlistItem[0];
        private readonly ServiceStatus status = new ServiceStatus();
        private CancellationTokenSource refreshCancellation;
        private Timer stateSaveTimer;

        // The wishlist being viewed. Everything below the scope id (entries, favourites, collections, back-off)
        // is looked up through it, so two wishlists can never leak into each other.
        private string activeScopeId = WishlistScope.OwnId;
        private int switchVersion;
        private Task switchRefreshTask = Task.CompletedTask;
        private readonly Dictionary<string, ScopeRuntime> runtime = new Dictionary<string, ScopeRuntime>();

        /// <summary>The refresh <see cref="SwitchTo"/> started for a wishlist that needed one; tests await it.</summary>
        internal Task SwitchRefreshTask => switchRefreshTask;

        /// <summary>Refresh back-off of one wishlist, so a failing list cannot block another.</summary>
        private sealed class ScopeRuntime
        {
            public DateTime NextAutoRefreshAllowedUtc = DateTime.MinValue;

            public int ConsecutiveFailures;
        }

        public WishlistDataService(
            WishlistProviderRegistry registry,
            IGGDealsPriceProvider priceProvider,
            RateLimitTracker rateLimits,
            VersionedJsonStore<CacheDocument> cacheStore,
            VersionedJsonStore<LocalStateDocument> stateStore,
            ILibrarySource library,
            Func<ServiceOptions> options,
            IClock clock = null,
            VersionedJsonStore<PriceHistoryDocument> historyStore = null)
        {
            this.registry = registry;
            this.priceProvider = priceProvider;
            this.rateLimits = rateLimits;
            this.cacheStore = cacheStore;
            this.stateStore = stateStore;
            this.historyStore = historyStore;
            this.library = library;
            this.options = options;
            this.clock = clock ?? SystemClock.Instance;
        }

        public event EventHandler DataChanged;

        public event EventHandler StatusChanged;

        public IReadOnlyList<WishlistItem> Items => items;

        public ServiceStatus Status
        {
            get
            {
                lock (sync)
                {
                    return status.Clone();
                }
            }
        }

        public IReadOnlyList<WishlistCollection> Collections
        {
            get
            {
                lock (sync)
                {
                    return state.Scope(activeScopeId).Collections.OrderBy(c => c.Order).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Select(c => new WishlistCollection { Id = c.Id, Name = c.Name, Icon = c.Icon, Order = c.Order }).ToList();
                }
            }
        }

        public UiState GetUiState()
        {
            lock (sync)
            {
                var ui = state.Ui;
                return new UiState { ViewMode = ui.ViewMode, ViewModeChosen = ui.ViewModeChosen, Sort = ui.Sort, Filter = ui.Filter.Clone(), ActiveWishlistId = ui.ActiveWishlistId };
            }
        }

        // -----------------------------------------------------------------------------------------
        // Wishlist scopes (own + other people's); exactly one is active, they are never combined
        // -----------------------------------------------------------------------------------------

        public string ActiveScopeId
        {
            get
            {
                lock (sync)
                {
                    return activeScopeId;
                }
            }
        }

        /// <summary>The wishlist being viewed (falls back to the user's own when the active one no longer exists).</summary>
        public WishlistScope ActiveScope
        {
            get
            {
                var id = ActiveScopeId;
                return Scopes.FirstOrDefault(s => s.Id == id) ?? WishlistScope.Own();
            }
        }

        /// <summary>All wishlists the user can switch to, the user's own first.</summary>
        public IReadOnlyList<WishlistScope> Scopes
        {
            get
            {
                var scopes = options().Wishlists ?? new List<WishlistScope>();
                return scopes.Any(s => s != null && s.IsOwn) ? scopes : WishlistScope.Build(scopes);
            }
        }

        /// <summary>Makes another wishlist the one being viewed. Its own data is shown, nothing from the previous one.</summary>
        public bool SwitchTo(string scopeId)
        {
            if (string.IsNullOrEmpty(scopeId) || Scopes.All(s => s.Id != scopeId))
            {
                return false;
            }

            bool neverFetched;
            lock (sync)
            {
                if (activeScopeId == scopeId)
                {
                    return true;
                }

                ActivateScope(scopeId);
                neverFetched = !Snapshot(scopeId).AttemptUtc.HasValue;
            }

            ScheduleStateSave();
            UpdateProviderStatus();
            RecomputeMatches();
            Rebuild();
            RaiseStatusChanged();

            // A wishlist that was never loaded would otherwise stay empty until the next scheduled refresh.
            if (neverFetched || IsAutoRefreshDue())
            {
                switchRefreshTask = Task.Run(async () =>
                {
                    try
                    {
                        await RefreshAsync(neverFetched ? RefreshTrigger.Manual : RefreshTrigger.Automatic).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Refresh after switching wishlist failed");
                    }
                });
            }

            return true;
        }

        /// <summary>Points the service at <paramref name="scopeId"/>. Call with <c>sync</c> held.</summary>
        private void ActivateScope(string scopeId)
        {
            activeScopeId = scopeId;
            switchVersion++;
            state.Ui.ActiveWishlistId = scopeId == WishlistScope.OwnId ? null : scopeId;

            // Collections belong to one wishlist, so a collection filter cannot carry over to another.
            state.Ui.Filter.CollectionIds.Clear();

            var snapshot = Snapshot(scopeId);
            status.LastError = snapshot.LastError;
            status.RetryAfterUtc = snapshot.LastError?.RetryAfterUtc;
            status.LastInfoMessage = null;
            status.WishlistUpdatedUtc = snapshot.UpdatedUtc;
        }

        /// <summary>The cached data of one wishlist, created empty on first use. Call with <c>sync</c> held.</summary>
        private WishlistSnapshot Snapshot(string scopeId)
        {
            if (!cache.Wishlists.TryGetValue(scopeId, out var snapshot) || snapshot == null)
            {
                snapshot = new WishlistSnapshot();
                cache.Wishlists[scopeId] = snapshot;
            }

            snapshot.Entries = snapshot.Entries ?? new List<WishlistEntry>();
            return snapshot;
        }

        private ScopeRuntime Runtime(string scopeId)
        {
            if (!runtime.TryGetValue(scopeId, out var scopeRuntime))
            {
                scopeRuntime = new ScopeRuntime();
                runtime[scopeId] = scopeRuntime;
            }

            return scopeRuntime;
        }

        // -----------------------------------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------------------------------

        /// <summary>Loads the persistent cache so the sidebar can display data immediately (no network).</summary>
        public Task InitializeAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var loadedCache = cacheStore.Load();
                    loadedCache.Normalize();
                    var loadedState = stateStore.Load();
                    loadedState.Normalize();
                    var loadedHistory = historyStore?.Load() ?? new PriceHistoryDocument();
                    loadedHistory.Normalize();
                    rateLimits.Import(loadedCache.RateLimit);
                    var knownScopes = Scopes;

                    lock (sync)
                    {
                        cache = loadedCache;
                        state = loadedState;
                        history = loadedHistory;

                        // Reopen the wishlist that was being viewed, unless it was removed in the meantime.
                        var saved = loadedState.Ui.ActiveWishlistId;
                        activeScopeId = !string.IsNullOrEmpty(saved) && knownScopes.Any(s => s.Id == saved) ? saved : WishlistScope.OwnId;
                        state.Ui.ActiveWishlistId = activeScopeId == WishlistScope.OwnId ? null : activeScopeId;

                        var snapshot = Snapshot(activeScopeId);
                        status.LastError = snapshot.LastError;
                        status.RetryAfterUtc = snapshot.LastError?.RetryAfterUtc;
                        if (status.RetryAfterUtc > clock.UtcNow)
                        {
                            Runtime(activeScopeId).NextAutoRefreshAllowedUtc = status.RetryAfterUtc.Value;
                        }
                    }

                    RebuildMatcher();
                    UpdateProviderStatus();
                    lock (sync)
                    {
                        status.IsInitialized = true;
                    }

                    Rebuild();
                    RaiseStatusChanged();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Failed to initialise the GG.deals wishlist cache");
                    lock (sync)
                    {
                        status.IsInitialized = true;
                    }

                    RaiseStatusChanged();
                }
            });
        }

        public void Shutdown()
        {
            try
            {
                refreshCancellation?.Cancel();
                FlushState();
                SaveCache();
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to persist wishlist data on shutdown");
            }
        }

        public void Dispose()
        {
            stateSaveTimer?.Dispose();
            refreshCancellation?.Dispose();
            refreshGate.Dispose();
        }

        // -----------------------------------------------------------------------------------------
        // Refresh
        // -----------------------------------------------------------------------------------------

        public void CancelRefresh() => refreshCancellation?.Cancel();

        /// <summary>
        /// Refreshes the wishlist and then the prices that actually need it. Never blanks existing data:
        /// cached entries remain visible and failures are recorded as a non-blocking error.
        /// </summary>
        public async Task<RefreshOutcome> RefreshAsync(RefreshTrigger trigger, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!await refreshGate.WaitAsync(0).ConfigureAwait(false))
            {
                return RefreshOutcome.AlreadyRunning;
            }

            var outcome = RefreshOutcome.Completed;
            try
            {
                refreshCancellation?.Dispose();
                refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var token = refreshCancellation.Token;
                var opts = options();
                var started = clock.UtcNow;
                SetProgress(true, "Updating…", 0, 0);
                ErrorRecord error = null;

                // The refresh belongs to the wishlist that is active right now, even if the user switches meanwhile.
                string scopeId;
                int version;
                lock (sync)
                {
                    scopeId = activeScopeId;
                    version = switchVersion;
                }

                // 1) Wishlist.
                var wishlistError = await RefreshWishlistAsync(token, scopeId, version).ConfigureAwait(false);
                error = wishlistError;

                // 2) Prices.
                List<PriceLookupKey> keys;
                lock (sync)
                {
                    keys = SelectKeysNeedingRefresh(trigger, opts, clock.UtcNow, scopeId);
                }

                string info = null;
                if (keys.Count == 0)
                {
                    info = HasAnyPriceableEntries(scopeId) ? "Prices are up to date." : null;
                    if (error == null && trigger != RefreshTrigger.Manual)
                    {
                        outcome = RefreshOutcome.NothingToDo;
                    }
                }
                else if (!opts.HasApiKey)
                {
                    error = error ?? NewError(ApiErrorKind.NotConfigured, null);
                }
                else
                {
                    var priceError = await RefreshPricesAsync(keys, opts, token).ConfigureAwait(false);
                    error = priceError ?? error;
                    if (priceError != null && priceError.Kind == ApiErrorKind.RateLimited)
                    {
                        outcome = RefreshOutcome.Partial;
                    }
                }

                lock (sync)
                {
                    cache.PricesAttemptUtc = started;
                    Snapshot(scopeId).LastError = error;
                    cache.RateLimit = rateLimits.Export();

                    // The status line describes the wishlist on screen; a refresh of another one must not overwrite it.
                    if (scopeId == activeScopeId)
                    {
                        status.LastAttemptUtc = started;
                        status.LastError = error;
                        status.RetryAfterUtc = error?.RetryAfterUtc;
                        status.LastInfoMessage = info;
                        if (error == null)
                        {
                            status.LastSuccessfulRefreshUtc = clock.UtcNow;
                        }
                    }
                }

                ScheduleNextAutomaticRefresh(error, opts, scopeId);
                SaveCache();
                SaveHistory();
                if (error != null && outcome == RefreshOutcome.Completed)
                {
                    outcome = RefreshOutcome.Failed;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Wishlist refresh failed");
                lock (sync)
                {
                    status.LastError = NewError(ApiErrorKind.UnexpectedResponse, null);
                }

                outcome = RefreshOutcome.Failed;
            }
            finally
            {
                SetProgress(false, null, 0, 0);
                refreshGate.Release();
                Rebuild();
            }

            return outcome;
        }

        private async Task<ErrorRecord> RefreshWishlistAsync(CancellationToken token, string scopeId, int version)
        {
            var provider = registry.Resolve();
            WishlistFetchResult result;
            try
            {
                result = provider.GetState() == WishlistProviderState.Available
                    ? await provider.RefreshAsync(token).ConfigureAwait(false)
                    : WishlistFetchResult.Unavailable(provider.StateMessage);
            }
            catch (OperationCanceledException)
            {
                return NewError(ApiErrorKind.Cancelled, null);
            }
            catch (Exception e)
            {
                Log.Error(e, "Wishlist provider " + provider.Id + " failed");
                result = new WishlistFetchResult { State = WishlistProviderState.Error, Error = ApiErrorKind.UnexpectedResponse };
            }

            var now = clock.UtcNow;
            ErrorRecord error = null;
            var entriesChanged = false;
            var scope = Scopes.FirstOrDefault(s => s.Id == scopeId);
            lock (sync)
            {
                if (version != switchVersion)
                {
                    // The user switched wishlists while this one was loading, so the provider may have read the other
                    // one. Drop the result rather than risk putting one person's games on another person's list.
                    Log.Info("Discarded a wishlist refresh because the wishlist was switched while it loaded.");
                    return null;
                }

                var snapshot = Snapshot(scopeId);
                snapshot.AttemptUtc = now;
                if (result.State == WishlistProviderState.Available)
                {
                    snapshot.Entries = SanitizeEntries(result.Entries, provider.Id);
                    snapshot.ProviderId = provider.Id;
                    snapshot.SteamId = scope?.SteamId;
                    snapshot.UpdatedUtc = now;
                    entriesChanged = true;
                }
                else if (result.State == WishlistProviderState.Error)
                {
                    // Keep the cached wishlist visible.
                    error = NewError(result.Error == ApiErrorKind.None ? ApiErrorKind.UnexpectedResponse : result.Error, null, result.Message);
                    error.Source = provider.DisplayName;
                }
                else if (snapshot.ProviderId != provider.Id || snapshot.Entries.Count > 0)
                {
                    // The source is no longer available: do not keep showing entries from a different source.
                    snapshot.Entries = new List<WishlistEntry>();
                    snapshot.ProviderId = provider.Id;
                    entriesChanged = true;
                }

                status.ProviderId = provider.Id;
                status.ProviderName = provider.DisplayName;
                status.ProviderState = result.State;
                status.ProviderMessage = result.Message ?? provider.StateMessage;
                status.ProviderCapabilities = provider.Capabilities;
                status.WishlistUpdatedUtc = snapshot.UpdatedUtc;
            }

            if (entriesChanged)
            {
                RecomputeMatches();
            }

            Rebuild();
            return error;
        }

        private async Task<ErrorRecord> RefreshPricesAsync(List<PriceLookupKey> keys, ServiceOptions opts, CancellationToken token)
        {
            SetProgress(true, "Updating prices…", 0, keys.Count);
            var progress = new SynchronousProgress<PriceBatchProgress>(p =>
            {
                if (p.BatchResults != null && p.BatchResults.Count > 0)
                {
                    lock (sync)
                    {
                        foreach (var pair in p.BatchResults)
                        {
                            cache.Prices[pair.Key] = pair.Value;
                            if (history.Record(pair.Key, pair.Value))
                            {
                                historyDirty = true;
                            }
                        }

                        cache.PricesUpdatedUtc = clock.UtcNow;
                    }

                    RecomputeMatches();
                    Rebuild();
                }

                var text = p.WaitingUntilUtc.HasValue
                    ? "Waiting for API limit (resumes " + p.WaitingUntilUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) + ")…"
                    : "Updating prices… " + p.Completed + " / " + p.Total;
                SetProgress(true, text, p.Completed, p.Total);
            });

            PriceFetchResult result;
            try
            {
                result = await priceProvider.GetPricesAsync(keys, opts.Region, true, progress, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Error(e, "Price refresh failed");
                return NewError(ApiErrorKind.UnexpectedResponse, null);
            }

            lock (sync)
            {
                status.PricesUpdatedUtc = cache.PricesUpdatedUtc;
            }

            if (result.Error == ApiErrorKind.None)
            {
                return null;
            }

            return NewError(result.Error, result.RetryAfterUtc, result.Message);
        }

        /// <summary>Ids whose price is missing, from another region, or older than the refresh threshold - oldest first.</summary>
        internal List<PriceLookupKey> SelectKeysNeedingRefresh(RefreshTrigger trigger, ServiceOptions opts, DateTime nowUtc, string scopeId = null)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(15, opts.RefreshIntervalMinutes));
            var threshold = trigger == RefreshTrigger.Manual
                ? (interval < ManualRefreshMinimumAge ? interval : ManualRefreshMinimumAge)
                : interval - TimeSpan.FromMinutes(1);

            var candidates = new List<KeyValuePair<PriceLookupKey, DateTime>>();
            var seen = new HashSet<PriceLookupKey>();
            foreach (var entry in Snapshot(scopeId ?? activeScopeId).Entries)
            {
                var key = entry.PriceKey;
                if (!key.HasValue || !seen.Add(key.Value))
                {
                    continue;
                }

                cache.Prices.TryGetValue(key.Value.ToString(), out var price);
                if (price == null || !string.Equals(price.Region, opts.Region, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(new KeyValuePair<PriceLookupKey, DateTime>(key.Value, DateTime.MinValue));
                    continue;
                }

                var age = nowUtc - price.FetchedUtc;
                if ((price.Found && age >= threshold) || (!price.Found && age >= NotFoundRecheckInterval))
                {
                    candidates.Add(new KeyValuePair<PriceLookupKey, DateTime>(key.Value, price.FetchedUtc));
                }
            }

            return candidates.OrderBy(c => c.Value).Select(c => c.Key).ToList();
        }

        /// <summary>True when automatic refresh is enabled, allowed by back-off/rate limits and there is stale data.</summary>
        public bool IsAutoRefreshDue()
        {
            var opts = options();
            if (!opts.AutoRefresh)
            {
                return false;
            }

            lock (sync)
            {
                var now = clock.UtcNow;
                if (!status.IsInitialized || status.IsRefreshing || now < Runtime(activeScopeId).NextAutoRefreshAllowedUtc)
                {
                    return false;
                }

                // Only the wishlist on screen refreshes automatically; the others wait until they are switched to.
                var interval = TimeSpan.FromMinutes(Math.Max(15, opts.RefreshIntervalMinutes));
                var attempt = Snapshot(activeScopeId).AttemptUtc;
                var wishlistDue = !attempt.HasValue || now - attempt.Value >= interval;
                var provider = registry.Resolve();
                var providerUsable = provider.GetState() == WishlistProviderState.Available;
                if (providerUsable && wishlistDue)
                {
                    return true;
                }

                return opts.HasApiKey && SelectKeysNeedingRefresh(RefreshTrigger.Automatic, opts, now).Count > 0;
            }
        }

        private void ScheduleNextAutomaticRefresh(ErrorRecord error, ServiceOptions opts, string scopeId)
        {
            var now = clock.UtcNow;
            lock (sync)
            {
                var scopeRuntime = Runtime(scopeId);
                if (error == null || error.Kind == ApiErrorKind.Cancelled)
                {
                    scopeRuntime.ConsecutiveFailures = 0;
                    scopeRuntime.NextAutoRefreshAllowedUtc = now.AddMinutes(1);
                    return;
                }

                switch (error.Kind)
                {
                    case ApiErrorKind.RateLimited:
                        scopeRuntime.NextAutoRefreshAllowedUtc = error.RetryAfterUtc ?? now.AddMinutes(5);
                        break;
                    case ApiErrorKind.InvalidApiKey:
                    case ApiErrorKind.NotConfigured:
                        // Wait for the user to fix the key; OnOptionsChanged re-enables automatic refresh.
                        scopeRuntime.NextAutoRefreshAllowedUtc = DateTime.MaxValue;
                        break;
                    default:
                        scopeRuntime.ConsecutiveFailures++;
                        var backoff = TimeSpan.FromMinutes(Math.Min(Math.Max(15, opts.RefreshIntervalMinutes), 5 * Math.Pow(2, scopeRuntime.ConsecutiveFailures - 1)));
                        scopeRuntime.NextAutoRefreshAllowedUtc = now + backoff;
                        break;
                }
            }
        }

        // -----------------------------------------------------------------------------------------
        // Options / library / cache
        // -----------------------------------------------------------------------------------------

        /// <summary>Call after settings are saved: re-evaluates provider, prices and matches.</summary>
        public void OnOptionsChanged()
        {
            var scopes = Scopes;
            lock (sync)
            {
                ReconcileScopes(scopes);

                // A fixed key or settings change lets every wishlist try again.
                foreach (var scopeRuntime in runtime.Values)
                {
                    scopeRuntime.NextAutoRefreshAllowedUtc = DateTime.MinValue;
                    scopeRuntime.ConsecutiveFailures = 0;
                }

                foreach (var snapshot in cache.Wishlists.Values)
                {
                    if (snapshot?.LastError != null && (snapshot.LastError.Kind == ApiErrorKind.NotConfigured || snapshot.LastError.Kind == ApiErrorKind.InvalidApiKey))
                    {
                        snapshot.LastError = null;
                    }
                }

                if (status.LastError != null && (status.LastError.Kind == ApiErrorKind.NotConfigured || status.LastError.Kind == ApiErrorKind.InvalidApiKey))
                {
                    status.LastError = null;
                }
            }

            ScheduleStateSave();
            UpdateProviderStatus();
            RecomputeMatches();
            Rebuild();
            RaiseStatusChanged();
        }

        /// <summary>
        /// Brings stored data in line with the configured wishlists: forgets removed ones, forgets the cached entries of
        /// one whose SteamID was edited, and returns to the user's own wishlist when the active one is gone. Call with <c>sync</c> held.
        /// </summary>
        private void ReconcileScopes(IReadOnlyList<WishlistScope> scopes)
        {
            var ids = new HashSet<string>(scopes.Select(s => s.Id)) { WishlistScope.OwnId };
            foreach (var key in cache.Wishlists.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                cache.Wishlists.Remove(key);
            }

            foreach (var key in state.Scopes.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                state.Scopes.Remove(key);
            }

            foreach (var key in runtime.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                runtime.Remove(key);
            }

            foreach (var scope in scopes.Where(s => !s.IsOwn))
            {
                if (cache.Wishlists.TryGetValue(scope.Id, out var snapshot) && snapshot != null
                    && snapshot.SteamId != null && !string.Equals(snapshot.SteamId, scope.SteamId, StringComparison.Ordinal))
                {
                    // Another person's list under the same name: never keep showing the previous person's games.
                    cache.Wishlists[scope.Id] = new WishlistSnapshot();
                }
            }

            if (!ids.Contains(activeScopeId))
            {
                ActivateScope(WishlistScope.OwnId);
            }
        }

        public void OnLibraryChanged()
        {
            RebuildMatcher();
            Rebuild();
        }

        public void ClearCache()
        {
            lock (sync)
            {
                var previous = cache;
                cache = new CacheDocument { RateLimit = rateLimits.Export() };

                // Entries and prices go; which source and SteamID each wishlist was loaded from is kept.
                foreach (var pair in previous.Wishlists)
                {
                    cache.Wishlists[pair.Key] = new WishlistSnapshot { ProviderId = pair.Value?.ProviderId, SteamId = pair.Value?.SteamId };
                }

                status.WishlistUpdatedUtc = null;
                status.PricesUpdatedUtc = null;
                status.LastError = null;
                foreach (var scopeRuntime in runtime.Values)
                {
                    scopeRuntime.NextAutoRefreshAllowedUtc = DateTime.MinValue;
                }
            }

            SaveCache();
            RecomputeMatches();
            Rebuild();
            RaiseStatusChanged();
        }

        /// <summary>Forgets every price this extension recorded. Separate from <see cref="ClearCache"/>, which keeps history.</summary>
        public void ClearPriceHistory()
        {
            lock (sync)
            {
                history = new PriceHistoryDocument();
                historyDirty = true;
            }

            SaveHistory();
            Rebuild();
        }

        public Task<ConnectionTestResult> TestConnectionAsync(string apiKey, string region)
        {
            return Task.Run(async () =>
            {
                var result = await priceProvider.TestConnectionAsync(apiKey, region, CancellationToken.None).ConfigureAwait(false);
                lock (sync)
                {
                    cache.RateLimit = rateLimits.Export();
                }

                RaiseStatusChanged();
                return result;
            });
        }

        public RateLimitBudget GetRateLimitStatus() => priceProvider.GetRateLimitStatus();

        // -----------------------------------------------------------------------------------------
        // Local organisation state (favourites, collections, match overrides, UI)
        // -----------------------------------------------------------------------------------------

        public void SetFavourite(string key, bool favourite)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            lock (sync)
            {
                var favourites = state.Scope(activeScopeId).Favourites;
                favourites.Remove(key);
                if (favourite)
                {
                    favourites.Add(key);
                }
            }

            ScheduleStateSave();
            Rebuild();
        }

        public WishlistCollection CreateCollection(string name, string icon)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            WishlistCollection collection;
            lock (sync)
            {
                var collections = state.Scope(activeScopeId).Collections;
                collection = new WishlistCollection
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name.Trim(),
                    Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim(),
                    Order = collections.Count == 0 ? 0 : collections.Max(c => c.Order) + 1
                };
                collections.Add(collection);
            }

            ScheduleStateSave();
            Rebuild();
            return collection;
        }

        public void RenameCollection(string id, string name, string icon)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            lock (sync)
            {
                var collection = state.Scope(activeScopeId).Collections.FirstOrDefault(c => c.Id == id);
                if (collection == null)
                {
                    return;
                }

                collection.Name = name.Trim();
                collection.Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();
            }

            ScheduleStateSave();
            Rebuild();
        }

        public void DeleteCollection(string id)
        {
            lock (sync)
            {
                var scopeState = state.Scope(activeScopeId);
                scopeState.Collections.RemoveAll(c => c.Id == id);
                foreach (var list in scopeState.Memberships.Values)
                {
                    list.Remove(id);
                }

                state.Ui.Filter.CollectionIds.Remove(id);
            }

            ScheduleStateSave();
            Rebuild();
        }

        public void SetCollectionMembership(IEnumerable<string> keys, string collectionId, bool member)
        {
            lock (sync)
            {
                var scopeState = state.Scope(activeScopeId);
                if (scopeState.Collections.All(c => c.Id != collectionId))
                {
                    return;
                }

                foreach (var key in keys.Where(k => !string.IsNullOrEmpty(k)))
                {
                    if (!scopeState.Memberships.TryGetValue(key, out var list))
                    {
                        list = new List<string>();
                        scopeState.Memberships[key] = list;
                    }

                    list.Remove(collectionId);
                    if (member)
                    {
                        list.Add(collectionId);
                    }

                    if (list.Count == 0)
                    {
                        scopeState.Memberships.Remove(key);
                    }
                }
            }

            ScheduleStateSave();
            Rebuild();
        }

        public int CountInCollection(string collectionId)
        {
            lock (sync)
            {
                var keys = new HashSet<string>(Snapshot(activeScopeId).Entries.Select(e => e.Key));
                return state.Scope(activeScopeId).Memberships.Count(m => keys.Contains(m.Key) && m.Value.Contains(collectionId));
            }
        }

        /// <param name="value">Playnite game id to confirm, <see cref="PlayniteMatcher.RejectedOverride"/> to reject, or null to reset.</param>
        public void SetMatchOverride(string key, string value)
        {
            lock (sync)
            {
                if (string.IsNullOrEmpty(value))
                {
                    state.MatchOverrides.Remove(key);
                }
                else
                {
                    state.MatchOverrides[key] = value;
                }
            }

            ScheduleStateSave();
            RecomputeMatches();
            Rebuild();
        }

        /// <param name="value">File path or web address of the chosen cover, or null to go back to automatic covers.</param>
        public void SetCoverOverride(string key, string value)
        {
            lock (sync)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    state.CoverOverrides.Remove(key);
                }
                else
                {
                    state.CoverOverrides[key] = value.Trim();
                }
            }

            ScheduleStateSave();
            Rebuild();
        }

        public void SaveUiState(UiState ui)
        {
            if (ui == null)
            {
                return;
            }

            lock (sync)
            {
                // The active wishlist is owned by the service (SwitchTo); a caller saving the view settings must not reset it.
                state.Ui = new UiState { ViewMode = ui.ViewMode, ViewModeChosen = ui.ViewModeChosen, Sort = ui.Sort, Filter = (ui.Filter ?? new Querying.FilterState()).Clone(), ActiveWishlistId = state.Ui.ActiveWishlistId };
            }

            ScheduleStateSave();
        }

        public void FlushState()
        {
            LocalStateDocument copy;
            lock (sync)
            {
                copy = Json.Deserialize<LocalStateDocument>(Json.Serialize(state));
            }

            stateStore.Save(copy);
        }

        // -----------------------------------------------------------------------------------------
        // Snapshot building
        // -----------------------------------------------------------------------------------------

        private void RebuildMatcher()
        {
            IReadOnlyList<LibraryGameInfo> games;
            try
            {
                games = library?.GetGames() ?? new LibraryGameInfo[0];
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to read the Playnite library for matching");
                games = new LibraryGameInfo[0];
            }

            var newMatcher = new PlayniteMatcher(games);
            lock (sync)
            {
                matcher = newMatcher;
            }

            RecomputeMatches();
        }

        private void RecomputeMatches()
        {
            var opts = options();
            lock (sync)
            {
                var result = new Dictionary<string, MatchResult>();
                foreach (var entry in Snapshot(activeScopeId).Entries)
                {
                    if (entry?.Key == null || result.ContainsKey(entry.Key))
                    {
                        continue;
                    }

                    if (!opts.EnableMatching)
                    {
                        result[entry.Key] = MatchResult.NoMatch;
                        continue;
                    }

                    try
                    {
                        state.MatchOverrides.TryGetValue(entry.Key, out var overrideValue);
                        result[entry.Key] = matcher.Match(entry, ResolveTitle(entry, PriceFor(entry)), overrideValue, opts.TreatExactTitleAsOwned);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Matching failed for " + entry.Key);
                        result[entry.Key] = MatchResult.NoMatch;
                    }
                }

                matchCache = result;
            }
        }

        private void Rebuild()
        {
            try
            {
                var opts = options();
                var now = clock.UtcNow;
                List<WishlistItem> built;
                lock (sync)
                {
                    var snapshot = Snapshot(activeScopeId);
                    var scopeState = state.Scope(activeScopeId);
                    var favourites = new HashSet<string>(scopeState.Favourites);
                    var staleAfter = TimeSpan.FromMinutes(Math.Max(15, opts.RefreshIntervalMinutes) * 2);
                    built = new List<WishlistItem>(snapshot.Entries.Count);
                    foreach (var entry in snapshot.Entries)
                    {
                        try
                        {
                            matchCache.TryGetValue(entry.Key, out var match);
                            scopeState.Memberships.TryGetValue(entry.Key, out var memberships);
                            var resolved = WishlistItemFactory.Create(entry, PriceFor(entry), match, favourites.Contains(entry.Key), memberships, opts, now, staleAfter);
                            resolved.CoverOverride = state.CoverOverrides.TryGetValue(entry.Key, out var customCover) ? customCover : null;
                            resolved.PriceHistory = entry.PriceKey.HasValue ? history.Get(entry.PriceKey.Value.ToString())?.Snapshot() : null;
                            built.Add(resolved);
                        }
                        catch (Exception e)
                        {
                            // One malformed item must not prevent the rest of the wishlist from displaying.
                            Log.Error(e, "Failed to build wishlist item " + entry?.Key);
                        }
                    }

                    status.HasCachedData = snapshot.Entries.Count > 0;
                    status.HasApiKey = opts.HasApiKey;
                    status.WishlistUpdatedUtc = snapshot.UpdatedUtc;
                    status.PricesUpdatedUtc = cache.PricesUpdatedUtc;
                }

                items = built;
                RaiseDataChanged();
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to rebuild the wishlist snapshot");
            }
        }

        private PriceData PriceFor(WishlistEntry entry)
        {
            var key = entry.PriceKey;
            return key.HasValue && cache.Prices.TryGetValue(key.Value.ToString(), out var price) ? price : null;
        }

        internal static string ResolveTitle(WishlistEntry entry, PriceData price)
        {
            if (!string.IsNullOrWhiteSpace(entry.Title))
            {
                return entry.Title.Trim();
            }

            if (!string.IsNullOrWhiteSpace(price?.GGDealsTitle))
            {
                return price.GGDealsTitle.Trim();
            }

            return null;
        }

        private void UpdateProviderStatus()
        {
            var provider = registry.Resolve();
            lock (sync)
            {
                status.ProviderId = provider.Id;
                status.ProviderName = provider.DisplayName;
                status.ProviderState = provider.GetState();
                status.ProviderMessage = provider.StateMessage;
                status.ProviderCapabilities = provider.Capabilities;
                status.HasApiKey = options().HasApiKey;
            }
        }

        private bool HasAnyPriceableEntries(string scopeId)
        {
            lock (sync)
            {
                return Snapshot(scopeId).Entries.Any(e => e.PriceKey.HasValue);
            }
        }

        private static List<WishlistEntry> SanitizeEntries(IEnumerable<WishlistEntry> entries, string providerId)
        {
            var result = new List<WishlistEntry>();
            var seen = new HashSet<string>();
            foreach (var entry in entries ?? Enumerable.Empty<WishlistEntry>())
            {
                if (entry == null)
                {
                    continue;
                }

                entry.Key = entry.Key ?? WishlistEntry.BuildKey(entry.GGDealsId, entry.SteamIdType, entry.SteamId, entry.GGDealsUrl);
                if (entry.Key == null || !seen.Add(entry.Key))
                {
                    continue;
                }

                entry.ProviderId = entry.ProviderId ?? providerId;
                result.Add(entry);
            }

            return result;
        }

        private ErrorRecord NewError(ApiErrorKind kind, DateTime? retryAfterUtc, string message = null)
        {
            return new ErrorRecord { Kind = kind, AtUtc = clock.UtcNow, RetryAfterUtc = retryAfterUtc, Message = message ?? ApiErrorMessages.For(kind, retryAfterUtc) };
        }

        private void SetProgress(bool refreshing, string text, int done, int total)
        {
            lock (sync)
            {
                status.IsRefreshing = refreshing;
                status.ProgressText = text;
                status.ProgressDone = done;
                status.ProgressTotal = total;
            }

            RaiseStatusChanged();
        }

        private void SaveCache()
        {
            CacheDocument copy;
            lock (sync)
            {
                cache.RateLimit = rateLimits.Export();
                copy = Json.Deserialize<CacheDocument>(Json.Serialize(cache));
            }

            cacheStore.Save(copy);
        }

        /// <summary>
        /// Prices this extension recorded for a lookup key ("app:420"), or null when none. Returns a copy so callers
        /// on other threads can read it freely.
        /// </summary>
        public PriceSeries GetPriceSeries(string lookupKey)
        {
            lock (sync)
            {
                return history.Get(lookupKey)?.Snapshot();
            }
        }

        private void SaveHistory()
        {
            if (historyStore == null)
            {
                return;
            }

            PriceHistoryDocument copy;
            lock (sync)
            {
                // Games removed from every wishlist stop accumulating history. A game that is only on another saved
                // wishlist is still tracked, so switching wishlists never costs a game its price history.
                var tracked = new HashSet<string>(cache.Wishlists.Values
                    .Where(w => w?.Entries != null)
                    .SelectMany(w => w.Entries)
                    .Where(e => e.PriceKey.HasValue)
                    .Select(e => e.PriceKey.Value.ToString()));
                if (tracked.Count > 0 && history.Prune(tracked) > 0)
                {
                    historyDirty = true;
                }

                if (!historyDirty)
                {
                    return;
                }

                historyDirty = false;
                copy = Json.Deserialize<PriceHistoryDocument>(Json.Serialize(history));
            }

            historyStore.Save(copy);
        }

        private void ScheduleStateSave()
        {
            lock (sync)
            {
                if (stateSaveTimer == null)
                {
                    stateSaveTimer = new Timer(_ => FlushStateSafe(), null, 500, Timeout.Infinite);
                }
                else
                {
                    stateSaveTimer.Change(500, Timeout.Infinite);
                }
            }
        }

        private void FlushStateSafe()
        {
            try { FlushState(); }
            catch (Exception e) { Log.Error(e, "Failed to save local wishlist state"); }
        }

        private void RaiseDataChanged()
        {
            try { DataChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception e) { Log.Error(e, "DataChanged handler failed"); }
        }

        private void RaiseStatusChanged()
        {
            try { StatusChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception e) { Log.Error(e, "StatusChanged handler failed"); }
        }

        /// <summary>IProgress that invokes the callback inline (Progress&lt;T&gt; would post to a captured context).</summary>
        private sealed class SynchronousProgress<T> : IProgress<T>
        {
            private readonly Action<T> handler;

            public SynchronousProgress(Action<T> handler) => this.handler = handler;

            public void Report(T value)
            {
                try { handler(value); }
                catch (Exception e) { Log.Error(e, "Progress handler failed"); }
            }
        }
    }

    /// <summary>Creates the resolved UI model for one entry.</summary>
    public static class WishlistItemFactory
    {
        public static WishlistItem Create(WishlistEntry entry, PriceData price, MatchResult match, bool isFavourite, IEnumerable<string> collections, ServiceOptions opts, DateTime nowUtc, TimeSpan staleAfter)
        {
            match = match ?? MatchResult.NoMatch;
            var title = WishlistDataService.ResolveTitle(entry, price)
                ?? (match.IsOwned ? match.Game.Name : null)
                ?? FallbackTitle(entry);
            var selection = PriceLogic.Select(price, opts.PricePreference);
            var regionMismatch = price != null && !string.Equals(price.Region, opts.Region, StringComparison.OrdinalIgnoreCase);

            var extra = new List<string>();
            if (match.Game != null && match.IsOwned)
            {
                extra.Add(match.Game.Developers);
                extra.Add(match.Game.Publishers);
                extra.Add(match.Game.SourceName);
            }

            if (selection.Category.HasValue)
            {
                extra.Add(selection.Category == PriceCategory.Keyshop ? "keyshop" : "retail");
            }

            extra.Add(price?.CheapestStoreName);
            if (price?.Offers != null)
            {
                extra.AddRange(price.Offers.Select(o => o.StoreName));
            }

            return new WishlistItem
            {
                Key = entry.Key,
                Entry = entry,
                Price = price,
                Match = match,
                Title = title,
                GGDealsUrl = !string.IsNullOrWhiteSpace(entry.GGDealsUrl) ? entry.GGDealsUrl : price?.GGDealsUrl,
                IsFavourite = isFavourite,
                CollectionIds = collections?.ToList() ?? new List<string>(),
                DisplayPrice = selection.Current,
                DisplayCategory = selection.Category,
                DisplayHistoricalLow = selection.HistoricalLow,
                IsHistoricalLow = selection.IsHistoricalLow && !regionMismatch,
                DiscountPercent = price != null && price.Found ? price.DiscountPercent : null,
                Currency = price?.Currency,
                LastPriceUpdateUtc = price?.FetchedUtc,
                IsPriceStale = price == null || regionMismatch || nowUtc - price.FetchedUtc > staleAfter,
                SearchTitle = title.ToLowerInvariant(),
                SearchExtra = string.Join(" ", extra.Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant()
            };
        }

        private static string FallbackTitle(WishlistEntry entry)
        {
            if (entry.SteamId.HasValue)
            {
                var kind = entry.SteamIdType == SteamIdType.App ? "App" : entry.SteamIdType == SteamIdType.Sub ? "Package" : "Bundle";
                return "Steam " + kind + " " + entry.SteamId.Value.ToString(CultureInfo.InvariantCulture);
            }

            return entry.GGDealsId ?? entry.Key;
        }
    }

    /// <summary>Checks once a minute whether an automatic refresh is due. Never runs on the UI thread.</summary>
    public sealed class RefreshScheduler : IDisposable
    {
        private readonly WishlistDataService service;
        private Timer timer;
        private int running;

        public RefreshScheduler(WishlistDataService service)
        {
            this.service = service;
        }

        public void Start(TimeSpan initialDelay)
        {
            timer?.Dispose();
            timer = new Timer(_ => Tick(), null, initialDelay, TimeSpan.FromMinutes(1));
        }

        public void Stop()
        {
            timer?.Dispose();
            timer = null;
        }

        private async void Tick()
        {
            if (Interlocked.Exchange(ref running, 1) == 1)
            {
                return;
            }

            try
            {
                if (service.IsAutoRefreshDue())
                {
                    await service.RefreshAsync(RefreshTrigger.Automatic).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Automatic refresh failed");
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
            }
        }

        public void Dispose() => Stop();
    }
}
