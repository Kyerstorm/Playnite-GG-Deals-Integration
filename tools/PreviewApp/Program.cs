using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GGDealsWishlist.Api;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;
using GGDealsWishlist.Querying;
using GGDealsWishlist.Services;
using GGDealsWishlist.Settings;
using GGDealsWishlist.ViewModels;
using GGDealsWishlist.Views;

namespace PreviewApp
{
    /// <summary>
    /// Renders the real extension views with fake data to PNG files. Usage: PreviewApp.exe &lt;output dir&gt;
    /// </summary>
    public static class Program
    {
        private const string PreviewKey = "preview-api-key-0001";

        [STAThread]
        public static int Main(string[] args)
        {
            var output = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "preview");
            Directory.CreateDirectory(output);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                Run(output);
                return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(output, "error.txt"), e.ToString());
                return 1;
            }
            finally
            {
                app.Shutdown();
            }
        }

        private static void Run(string output)
        {
            var dark = Color.FromRgb(0x1B, 0x1D, 0x24);
            var light = Color.FromRgb(0xF4, 0xF5, 0xF7);

            // Main content scenarios.
            using (var s = Scenario.Create(withKey: true, manual: true))
            {
                s.RenderSidebar(output, "01-cover-dark-340", 340, 1000, ViewMode.CoverInfo, ThemeMode.Dark, dark);
                s.RenderSidebar(output, "02-compact-dark-340", 340, 900, ViewMode.Compact, ThemeMode.Dark, dark);
                s.RenderSidebar(output, "03-list-dark-300", 300, 700, ViewMode.List, ThemeMode.Dark, dark);
                s.RenderSidebar(output, "04-cover-light-460", 460, 1000, ViewMode.CoverInfo, ThemeMode.Light, light);
                s.RenderSidebar(output, "05-cover-narrow-220", 220, 700, ViewMode.CoverInfo, ThemeMode.Dark, dark);
                s.RenderSidebar(output, "06-details-dark-360", 360, 1300, ViewMode.CoverInfo, ThemeMode.Dark, dark, vm =>
                    vm.OpenDetails(vm.Items.FirstOrDefault(i => i.IsHistoricalLow && i.Title.StartsWith("Hollow")) ?? vm.Items.First()));
                s.RenderSidebar(output, "07-details-potential-360", 360, 1100, ViewMode.CoverInfo, ThemeMode.Dark, dark, vm =>
                    vm.OpenDetails(vm.Items.FirstOrDefault(i => i.IsPotentialMatch) ?? vm.Items.First()));
                s.RenderSidebar(output, "08-search-noresults-340", 340, 500, ViewMode.CoverInfo, ThemeMode.Dark, dark, vm => vm.SearchText = "zzzz");
                s.RenderSidebar(output, "09-filter-historical-340", 340, 800, ViewMode.Compact, ThemeMode.Dark, dark, vm => vm.Quick = QuickFilter.HistoricalLow);

                // Unable to refresh with cached data.
                s.Clock.Advance(TimeSpan.FromHours(3));
                s.Api.FailWith = ApiErrorKind.RateLimited;
                s.Service.RefreshAsync(RefreshTrigger.Manual).GetAwaiter().GetResult();
                s.RenderSidebar(output, "10-ratelimited-stale-340", 340, 700, ViewMode.Compact, ThemeMode.Dark, dark);

                s.RenderControl(output, "11-settings", 760, 2900, () =>
                {
                    var vm = new GGDealsSettingsViewModel(s.Service, s.Host);
                    vm.BeginEdit();
                    return new GGDealsSettingsView(vm);
                }, dark);
                s.RenderControl(output, "12-collections", 560, 420, () => new CollectionsManagerView(new CollectionsManagerViewModel(s.Service, s.Host), ThemeMode.Dark), dark);
            }

            // Price history: the first scenario has a single recorded price per game ("tracking started").
            using (var s = Scenario.Create(withKey: true, manual: true))
            {
                s.RenderSidebar(output, "19-cover-expanded-new-360", 360, 800, ViewMode.CoverInfo, ThemeMode.Dark, dark, vm => vm.ToggleExpanded(vm.Items.First()));

                // Three more refreshes over nine days with prices moving, so every game has a line to draw.
                foreach (var scale in new[] { 0.95m, 0.95m, 0.85m })
                {
                    s.Clock.Advance(TimeSpan.FromDays(3));
                    s.Api.PriceScale = scale;
                    s.Service.RefreshAsync(RefreshTrigger.Manual).GetAwaiter().GetResult();
                }

                s.RenderSidebar(output, "20-cover-history-360", 360, 800, ViewMode.CoverInfo, ThemeMode.Dark, dark);
                s.RenderSidebar(output, "21-cover-expanded-history-360", 360, 1000, ViewMode.CoverInfo, ThemeMode.Dark, dark, vm => vm.ToggleExpanded(vm.Items.First(i => i.Title.StartsWith("Hollow"))));
                s.RenderSidebar(output, "22-grid-dark-360", 360, 900, ViewMode.Grid, ThemeMode.Dark, dark);
                s.RenderSidebar(output, "23-grid-expanded-360", 360, 1100, ViewMode.Grid, ThemeMode.Dark, dark, vm => vm.ToggleExpanded(vm.Items.First(i => i.Title.StartsWith("Hollow"))));
                s.RenderSidebar(output, "24-grid-wide-680", 680, 900, ViewMode.Grid, ThemeMode.Dark, dark);
                s.RenderSidebar(output, "25-grid-light-360", 360, 900, ViewMode.Grid, ThemeMode.Light, light);
                s.RenderSidebar(output, "29-cover-columns-1000", 1000, 900, ViewMode.CoverInfo, ThemeMode.Dark, dark);
                s.RenderSidebar(output, "30-cover-columns-1800", 1800, 900, ViewMode.CoverInfo, ThemeMode.Dark, dark);
                s.RenderSidebar(output, "27-cover-expanded-wide-1800", 1800, 900, ViewMode.CoverInfo, ThemeMode.Dark, dark, vm => vm.ToggleExpanded(vm.Items.First(i => i.Title.StartsWith("Hollow"))));
                s.RenderSidebar(output, "28-grid-expanded-wide-1800", 1800, 900, ViewMode.Grid, ThemeMode.Dark, dark, vm => vm.ToggleExpanded(vm.Items.First(i => i.Title.StartsWith("Terraria"))));
                s.RenderSidebar(output, "26-details-history-360", 360, 1500, ViewMode.CoverInfo, ThemeMode.Dark, dark, vm => vm.OpenDetails(vm.Items.First(i => i.Title.StartsWith("Hollow"))));
            }

            using (var s = Scenario.Create(withKey: false, manual: false))
            {
                s.RenderSidebar(output, "13-first-run-340", 340, 700, ViewMode.CoverInfo, ThemeMode.Dark, dark);
            }

            using (var s = Scenario.Create(withKey: true, manual: false))
            {
                s.RenderSidebar(output, "14-wishlist-unavailable-340", 340, 700, ViewMode.CoverInfo, ThemeMode.Dark, dark);
            }

            using (var s = Scenario.Create(withKey: true, manual: true, failFirst: ApiErrorKind.InvalidApiKey))
            {
                s.RenderSidebar(output, "15-invalid-key-340", 340, 700, ViewMode.CoverInfo, ThemeMode.Light, light);
            }

            // Steam wishlist source: not configured, then configured (stubbed Steam response), then settings.
            using (var s = Scenario.Create(withKey: true, manual: false))
            {
                s.Host.Settings.WishlistSource = WishlistSourceMode.SteamWishlist;
                s.Service.OnOptionsChanged();
                s.RenderSidebar(output, "16-steam-not-configured-340", 340, 600, ViewMode.CoverInfo, ThemeMode.Dark, dark);

                s.Host.Settings.SteamId = "https://steamcommunity.com/profiles/76561197960287930/";
                s.Service.OnOptionsChanged();
                s.Service.RefreshAsync(RefreshTrigger.Manual).GetAwaiter().GetResult();
                s.RenderSidebar(output, "17-steam-wishlist-340", 340, 900, ViewMode.Compact, ThemeMode.Dark, dark);

                s.Host.Settings.SteamId = "https://steamcommunity.com/id/someone";
                s.RenderControl(output, "18-settings-steam", 760, 900, () =>
                {
                    var vm = new GGDealsSettingsViewModel(s.Service, s.Host);
                    vm.BeginEdit();
                    return new GGDealsSettingsView(vm);
                }, dark);
            }
        }

        // =========================================================================================

        private sealed class Scenario : IDisposable
        {
            private readonly string directory;

            private Scenario(string directory)
            {
                this.directory = directory;
            }

            public PreviewClock Clock { get; private set; }

            public PreviewApiClient Api { get; private set; }

            public PreviewHost Host { get; private set; }

            public WishlistDataService Service { get; private set; }

            public static Scenario Create(bool withKey, bool manual, ApiErrorKind failFirst = ApiErrorKind.None)
            {
                var dir = Path.Combine(Path.GetTempPath(), "ggdeals-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var scenario = new Scenario(dir);
                var settings = new GGDealsSettings
                {
                    Region = "gb",
                    ProtectedApiKey = withKey ? SecretProtector.Protect(PreviewKey) : null,
                    WishlistSource = manual ? WishlistSourceMode.ManualList : WishlistSourceMode.OfficialOnly,
                    ManualWishlist = manual ? ManualList : string.Empty
                };

                scenario.Clock = new PreviewClock(DateTime.UtcNow);
                scenario.Api = new PreviewApiClient { FailWith = failFirst };
                scenario.Host = new PreviewHost(settings);
                var tracker = new RateLimitTracker(scenario.Clock);
                var prices = new GGDealsApiPriceProvider(scenario.Api, tracker, () => withKey ? PreviewKey : null, scenario.Clock, (t, c) => Task.CompletedTask);
                var registry = new WishlistProviderRegistry(new IGGDealsWishlistProvider[]
                {
                    new SteamWishlistProvider(() => settings.WishlistSource == WishlistSourceMode.SteamWishlist, () => settings.SteamId, new FakeSteamHandler(), scenario.Clock),
                    new ManualWishlistProvider(() => settings.WishlistSource == WishlistSourceMode.ManualList, () => settings.ManualWishlist, scenario.Clock),
                    new UnavailableWishlistProvider()
                });
                scenario.Service = new WishlistDataService(
                    registry,
                    prices,
                    tracker,
                    new VersionedJsonStore<CacheDocument>(Path.Combine(dir, "cache.json"), CacheDocument.CurrentVersion),
                    new VersionedJsonStore<LocalStateDocument>(Path.Combine(dir, "state.json"), LocalStateDocument.CurrentVersion),
                    new PreviewLibrary(),
                    () => settings.ToServiceOptions(),
                    scenario.Clock,
                    new VersionedJsonStore<PriceHistoryDocument>(Path.Combine(dir, "price-history.json"), PriceHistoryDocument.CurrentVersion));
                scenario.Host.Service = scenario.Service;

                scenario.Service.InitializeAsync().GetAwaiter().GetResult();
                if (withKey)
                {
                    scenario.Service.RefreshAsync(RefreshTrigger.Manual).GetAwaiter().GetResult();
                }

                if (manual && failFirst == ApiErrorKind.None)
                {
                    var retro = scenario.Service.CreateCollection("Retro", "⭐");
                    var coop = scenario.Service.CreateCollection("Co-op nights", "🎮");
                    scenario.Service.SetCollectionMembership(new[] { "steam:app:367520", "steam:app:1145360" }, retro.Id, true);
                    scenario.Service.SetCollectionMembership(new[] { "steam:app:1145360", "steam:app:105600", "steam:app:413150" }, coop.Id, true);
                    scenario.Service.SetFavourite("steam:app:1145360", true);
                    scenario.Service.SetFavourite("steam:app:1091500", true);
                }

                return scenario;
            }

            public void RenderSidebar(string output, string name, int width, int height, ViewMode mode, ThemeMode theme, Color background, Action<SidebarViewModel> setup = null)
            {
                Host.Settings.Theme = theme;
                Host.Settings.DefaultViewMode = mode;
                var vm = new SidebarViewModel(Service, Host);
                vm.ViewMode = mode;
                vm.ClearFilters();
                vm.SearchText = string.Empty;
                Pump(250);
                setup?.Invoke(vm);
                RenderControl(output, name, width, height, () => new SidebarView(vm, theme), background);
                vm.Dispose();
            }

            public void RenderControl(string output, string name, int width, int height, Func<FrameworkElement> factory, Color background)
            {
                var content = factory();
                var window = new Window
                {
                    Width = width,
                    Height = height,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Left = -20000,
                    Top = 0,
                    Background = new SolidColorBrush(background),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13,
                    Content = content
                };
                TextElement.SetForeground(window, new SolidColorBrush(background.R < 128 ? Color.FromRgb(0xE8, 0xE9, 0xED) : Color.FromRgb(0x1F, 0x23, 0x28)));
                window.Show();
                Pump(700);

                var root = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
                    dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                }

                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(output, name + ".png")))
                {
                    encoder.Save(stream);
                }

                window.Close();
            }

            public void Dispose()
            {
                try
                {
                    Service.Shutdown();
                    Service.Dispose();
                    Directory.Delete(directory, true);
                }
                catch
                {
                    // Temp cleanup only.
                }
            }
        }

        private static void Pump(int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(15);
            }
        }

        private const string ManualList = @"1145360 | Hades
367520 | Hollow Knight
1091500 | Cyberpunk 2077
292030 | The Witcher 3: Wild Hunt
620 | Portal 2
105600 | Terraria
413150 | Stardew Valley
1245620 | Elden Ring
990080 | Hogwarts Legacy
1086940 | Baldur's Gate 3
588650 | Dead Cells
1794680 | Vampire Survivors
504230 | Celeste
250900 | The Binding of Isaac: Rebirth
2379780 | Balatro
this is not a steam id";

        // =========================================================================================

        private sealed class PreviewClock : IClock
        {
            public PreviewClock(DateTime now) => UtcNow = now;

            public DateTime UtcNow { get; private set; }

            public void Advance(TimeSpan by) => UtcNow += by;
        }

        private sealed class PreviewApiClient : IGGDealsApiClient
        {
            // current retail, current keyshop, historical retail, historical keyshop
            private static readonly Dictionary<long, decimal?[]> Prices = new Dictionary<long, decimal?[]>
            {
                [1145360] = new decimal?[] { 9.99m, 7.45m, 9.99m, 6.10m },
                [367520] = new decimal?[] { 5.39m, 4.10m, 5.39m, 3.95m },
                [1091500] = new decimal?[] { 24.99m, 19.80m, 14.99m, 12.20m },
                [292030] = new decimal?[] { 6.99m, 3.49m, 5.99m, 2.80m },
                [620] = new decimal?[] { 1.49m, 0.99m, 0.89m, 0.79m },
                [105600] = new decimal?[] { 6.99m, 4.60m, 3.49m, 2.99m },
                [413150] = new decimal?[] { 10.99m, 7.95m, 6.59m, 5.40m },
                [1245620] = new decimal?[] { 39.99m, 31.50m, 29.99m, 24.10m },
                [990080] = new decimal?[] { 19.99m, 16.40m, 19.99m, 15.90m },
                [1086940] = new decimal?[] { 49.99m, 41.30m, 44.99m, 38.00m },
                [588650] = new decimal?[] { 7.49m, 5.80m, 5.99m, 4.20m },
                [1794680] = new decimal?[] { 3.99m, 2.10m, 2.99m, 1.80m },
                [504230] = new decimal?[] { 3.99m, null, 3.99m, null },
                [2379780] = new decimal?[] { 11.39m, 9.20m, 9.99m, 8.60m }
            };

            // The real Prices API returns each game's title; mirror that so Steam-sourced entries get names.
            private static readonly Dictionary<long, string> Titles = new Dictionary<long, string>
            {
                [1086940] = "Baldur's Gate 3",
                [2379780] = "Balatro",
                [1245620] = "Elden Ring",
                [588650] = "Dead Cells",
                [504230] = "Celeste",
                [620] = "Portal 2"
            };

            public ApiErrorKind FailWith { get; set; }

            /// <summary>Multiplies current prices so a scenario can build a price history across several refreshes.</summary>
            public decimal PriceScale { get; set; } = 1m;

            private decimal? Scaled(decimal? price) => price.HasValue ? Math.Round(price.Value * PriceScale, 2) : (decimal?)null;

            public Task<PriceApiResult> GetPricesAsync(SteamIdType type, IReadOnlyCollection<long> ids, string region, string apiKey, CancellationToken cancellationToken)
            {
                if (FailWith != ApiErrorKind.None)
                {
                    return Task.FromResult(new PriceApiResult { Error = FailWith, RetryAfterUtc = FailWith == ApiErrorKind.RateLimited ? DateTime.UtcNow.AddMinutes(12) : (DateTime?)null });
                }

                var result = new PriceApiResult();
                foreach (var id in ids)
                {
                    if (!Prices.TryGetValue(id, out var p))
                    {
                        result.Items[id] = null;
                        continue;
                    }

                    result.Items[id] = new PriceData
                    {
                        Found = true,
                        GGDealsTitle = Titles.TryGetValue(id, out var title) ? title : null,
                        GGDealsUrl = "https://gg.deals/game/" + id + "/",
                        CurrentRetail = Scaled(p[0]),
                        CurrentKeyshops = Scaled(p[1]),
                        HistoricalRetail = p[2],
                        HistoricalKeyshops = p[3],
                        Currency = "GBP",
                        Region = region
                    };
                }

                return Task.FromResult(result);
            }
        }

        /// <summary>Stands in for Steam's IWishlistService/GetWishlist so the preview makes no network calls.</summary>
        private sealed class FakeSteamHandler : System.Net.Http.HttpMessageHandler
        {
            private const string Body = @"{""response"":{""items"":[
                {""appid"":1086940,""priority"":1,""date_added"":1719000000},
                {""appid"":2379780,""priority"":2,""date_added"":1722000000},
                {""appid"":1245620,""priority"":3,""date_added"":1690000000},
                {""appid"":588650,""priority"":0,""date_added"":1725000000},
                {""appid"":504230,""priority"":0,""date_added"":1700000000},
                {""appid"":620,""priority"":0,""date_added"":1650000000},
                {""appid"":999999,""priority"":0,""date_added"":1640000000}]}}";

            // IStoreBrowseService/GetItems: Steam knows 999999 here even though GG.deals does not.
            private const string NamesBody = @"{""response"":{""store_items"":[
                {""id"":1086940,""success"":1,""name"":""Baldur's Gate 3""},
                {""id"":2379780,""success"":1,""name"":""Balatro""},
                {""id"":1245620,""success"":1,""name"":""ELDEN RING""},
                {""id"":588650,""success"":1,""name"":""Dead Cells""},
                {""id"":504230,""success"":1,""name"":""Celeste""},
                {""id"":620,""success"":1,""name"":""Portal 2""},
                {""id"":999999,""success"":1,""name"":""Tiny Indie Game (Early Access)""}]}}";

            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.RequestUri.AbsolutePath.Contains("IStoreBrowseService") ? NamesBody : Body;
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(body)
                });
            }
        }

        private sealed class PreviewLibrary : ILibrarySource
        {
            private static readonly Guid SteamPlugin = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");
            private static readonly List<LibraryGameInfo> Games = new List<LibraryGameInfo>
            {
                new LibraryGameInfo { Id = Guid.NewGuid(), Name = "Portal 2", PluginId = SteamPlugin, GameId = "620", IsInstalled = true, Developers = "Valve", Publishers = "Valve", SourceName = "Steam" },
                new LibraryGameInfo { Id = Guid.NewGuid(), Name = "Terraria", PluginId = Guid.NewGuid(), GameId = "terraria", IsInstalled = false, SourceName = "GOG" },
                new LibraryGameInfo { Id = Guid.NewGuid(), Name = "The Witcher 3: Wild Hunt - Game of the Year Edition", PluginId = Guid.NewGuid(), GameId = "witcher3", SourceName = "GOG" }
            };

            public IReadOnlyList<LibraryGameInfo> GetGames() => Games;
        }

        private sealed class PreviewHost : IHostServices
        {
            public PreviewHost(GGDealsSettings settings) => Settings = settings;

            public event EventHandler SettingsChanged;

            public WishlistDataService Service { get; set; }

            public Dispatcher Dispatcher => Dispatcher.CurrentDispatcher;

            public GGDealsSettings Settings { get; }

            public void UpdateSettings(Action<GGDealsSettings> change)
            {
                change(Settings);
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }

            public void SaveApiKey(string plainKey) => UpdateSettings(s => s.ProtectedApiKey = SecretProtector.Protect(plainKey));

            public void OpenUrl(string url)
            {
            }

            public void CopyToClipboard(string text)
            {
            }

            public bool OpenPlayniteGame(Guid gameId) => true;

            public void OpenSettings()
            {
            }

            public string PromptText(string message, string caption, string defaultValue) => null;

            public string PickImageFile() => null;

            public string PickSteamGridDbCover(long? steamAppId, string title) => null;

            public bool Confirm(string message, string caption) => false;

            public void ShowCollectionsManager()
            {
            }
        }
    }
}
