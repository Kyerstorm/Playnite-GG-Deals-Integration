using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using GGDealsWishlist.Api;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Providers;
using GGDealsWishlist.Services;
using GGDealsWishlist.Settings;
using GGDealsWishlist.ViewModels;
using GGDealsWishlist.Views;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;

namespace GGDealsWishlist
{
    /// <summary>
    /// Composition root. Wires the GG.deals API client, providers, persistence and the native sidebar into
    /// Playnite. Nothing here performs network I/O on the UI thread.
    /// </summary>
    public class GGDealsWishlistPlugin : GenericPlugin
    {
        public static readonly Guid PluginId = Guid.Parse("42520b12-2e2a-4c08-a4ec-bcdf317f93f5");

        private const string MenuSection = "@GG.deals Wishlist";
        private static readonly ILogger PlayniteLogger = LogManager.GetLogger();

        private readonly GGDealsSettings settings;
        private readonly PlayniteHost host;
        private readonly GGDealsApiClient apiClient;
        private readonly SteamWishlistProvider steamWishlist;
        private readonly WishlistDataService service;
        private readonly RefreshScheduler scheduler;
        private readonly Timer libraryDebounce;
        private GGDealsSettingsViewModel settingsViewModel;
        private SidebarViewModel sidebarViewModel;
        private SidebarView sidebarView;
        private string cachedProtectedKey;
        private string cachedPlainKey;

        public GGDealsWishlistPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = true };
            Log.SetSink(new PlayniteLogSink(PlayniteLogger));

            // .NET Framework 4.6.2 may default to older TLS versions; GG.deals requires TLS 1.2+.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            settings = LoadPluginSettings<GGDealsSettings>() ?? new GGDealsSettings();
            settings.Normalize();

            var dataDirectory = GetPluginUserDataPath();
            CoverImage.DiskCacheDirectory = Path.Combine(dataDirectory, "covers");

            host = new PlayniteHost(this, api, settings, () => service);
            host.RegisterStoredKey();

            var rateLimits = new RateLimitTracker();
            apiClient = new GGDealsApiClient();
            var priceProvider = new GGDealsApiPriceProvider(apiClient, rateLimits, GetApiKey);
            steamWishlist = new SteamWishlistProvider(() => settings.WishlistSource == WishlistSourceMode.SteamWishlist, () => settings.SteamId);
            var registry = new WishlistProviderRegistry(new IGGDealsWishlistProvider[]
            {
                // Preference order: an official provider will be inserted first once GG.deals offers one.
                // Only the source selected in settings reports itself available.
                steamWishlist,
                new ManualWishlistProvider(() => settings.WishlistSource == WishlistSourceMode.ManualList, () => settings.ManualWishlist),
                new UnavailableWishlistProvider()
            });

            service = new WishlistDataService(
                registry,
                priceProvider,
                rateLimits,
                new VersionedJsonStore<CacheDocument>(Path.Combine(dataDirectory, "cache.json"), CacheDocument.CurrentVersion),
                new VersionedJsonStore<LocalStateDocument>(Path.Combine(dataDirectory, "state.json"), LocalStateDocument.CurrentVersion),
                new PlayniteLibrarySource(api),
                () => settings.ToServiceOptions(),
                historyStore: new VersionedJsonStore<PriceHistoryDocument>(Path.Combine(dataDirectory, "price-history.json"), PriceHistoryDocument.CurrentVersion));

            scheduler = new RefreshScheduler(service);
            libraryDebounce = new Timer(_ => SafeRun(service.OnLibraryChanged, "Library update failed"), null, Timeout.Infinite, Timeout.Infinite);
            host.SettingsChanged += OnSettingsChanged;
        }

        public override Guid Id => PluginId;

        // =========================================================================================
        // Lifecycle
        // =========================================================================================

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            PlayniteApi.Database.Games.ItemCollectionChanged += (s, e) => ScheduleLibraryUpdate();
            PlayniteApi.Database.Games.ItemUpdated += (s, e) => ScheduleLibraryUpdate();

            // Load cached data in the background, then let the scheduler decide whether a refresh is due.
            service.InitializeAsync().ContinueWith(_ => scheduler.Start(TimeSpan.FromSeconds(15)));
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            scheduler.Dispose();
            libraryDebounce.Dispose();
            service.Shutdown();
            sidebarViewModel?.Dispose();
            apiClient.Dispose();
            steamWishlist.Dispose();
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args) => ScheduleLibraryUpdate();

        // =========================================================================================
        // Settings
        // =========================================================================================

        public override ISettings GetSettings(bool firstRunSettings) => settingsViewModel ?? (settingsViewModel = new GGDealsSettingsViewModel(service, host));

        public override UserControl GetSettingsView(bool firstRunSettings) => new GGDealsSettingsView((GGDealsSettingsViewModel)GetSettings(firstRunSettings));

        // =========================================================================================
        // Sidebar and menus
        // =========================================================================================

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = "GG.deals Wishlist",
                Type = SiderbarItemType.View,
                Icon = CreateSidebarIcon(),
                Opened = OpenSidebar,
                Closed = CloseSidebar
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Refresh wishlist prices",
                Action = args => { _ = service.RefreshAsync(RefreshTrigger.Manual); }
            };
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Open GG.deals wishlist",
                Action = _ => host.OpenUrl(settings.GGDealsWishlistUrl)
            };
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Manage collections…",
                Action = _ => host.ShowCollectionsManager()
            };
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Settings…",
                Action = _ => host.OpenSettings()
            };
        }

        private Control OpenSidebar()
        {
            try
            {
                if (sidebarViewModel == null)
                {
                    sidebarViewModel = new SidebarViewModel(service, host);
                }

                // A fresh view each time avoids re-parenting issues; the view model (and its state) is kept.
                sidebarView?.Detach();
                sidebarView = new SidebarView(sidebarViewModel, settings.Theme);
                return sidebarView;
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not open the GG.deals sidebar");
                return new UserControl
                {
                    Content = new TextBlock
                    {
                        Text = "The GG.deals Wishlist could not be displayed. Details were written to the Playnite extension log.",
                        TextWrapping = System.Windows.TextWrapping.Wrap,
                        Margin = new System.Windows.Thickness(16)
                    }
                };
            }
        }

        private void CloseSidebar()
        {
            sidebarView?.Detach();
            sidebarView = null;
            service.FlushState();
        }

        private static object CreateSidebarIcon()
        {
            var iconPath = Path.Combine(Path.GetDirectoryName(typeof(GGDealsWishlistPlugin).Assembly.Location) ?? string.Empty, "icon.png");
            if (File.Exists(iconPath) && new FileInfo(iconPath).Length > 0)
            {
                return iconPath;
            }

            return new TextBlock { Text = "GG", FontWeight = System.Windows.FontWeights.Bold, FontSize = 15, Foreground = Brushes.Orange };
        }

        // =========================================================================================
        // Helpers
        // =========================================================================================

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            SafeRun(service.OnOptionsChanged, "Applying settings failed");
            host.Dispatcher.BeginInvoke(new Action(() => sidebarView?.ApplyTheme(settings.Theme)));
        }

        /// <summary>Decrypts the stored key on demand (cached per encrypted value); never logged.</summary>
        private string GetApiKey()
        {
            var protectedKey = settings.ProtectedApiKey;
            if (!string.Equals(protectedKey, cachedProtectedKey, StringComparison.Ordinal))
            {
                cachedPlainKey = SecretProtector.Unprotect(protectedKey);
                cachedProtectedKey = protectedKey;
                if (!string.IsNullOrEmpty(cachedPlainKey))
                {
                    SecretRedactor.Register(cachedPlainKey);
                }
            }

            return cachedPlainKey;
        }

        private void ScheduleLibraryUpdate()
        {
            try
            {
                libraryDebounce.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        }

        private static void SafeRun(Action action, string failureMessage)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Error(e, failureMessage);
            }
        }
    }
}
