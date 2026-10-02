using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;
using GGDealsWishlist.Services;
using GGDealsWishlist.Settings;
using GGDealsWishlist.ViewModels;
using GGDealsWishlist.Views;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace GGDealsWishlist
{
    /// <summary>Forwards the extension's (already redacted) log lines to Playnite's extension log.</summary>
    internal sealed class PlayniteLogSink : ILogSink
    {
        private readonly ILogger logger;

        public PlayniteLogSink(ILogger logger) => this.logger = logger;

        public void Debug(string message) => logger.Debug(message);

        public void Info(string message) => logger.Info(message);

        public void Warn(string message) => logger.Warn(message);

        public void Error(string message) => logger.Error(message);
    }

    /// <summary>Projects the Playnite database into lightweight <see cref="LibraryGameInfo"/> records.</summary>
    internal sealed class PlayniteLibrarySource : ILibrarySource
    {
        private readonly IPlayniteAPI api;

        public PlayniteLibrarySource(IPlayniteAPI api) => this.api = api;

        public IReadOnlyList<LibraryGameInfo> GetGames()
        {
            var result = new List<LibraryGameInfo>();
            try
            {
                foreach (var game in api.Database.Games.ToList())
                {
                    try
                    {
                        result.Add(Project(game));
                    }
                    catch (Exception e)
                    {
                        Log.Warn(e, "Skipped a library game that could not be read");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not read the Playnite library");
            }

            return result;
        }

        private LibraryGameInfo Project(Game game)
        {
            string cover = null;
            if (!string.IsNullOrEmpty(game.CoverImage))
            {
                cover = game.CoverImage.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? game.CoverImage
                    : api.Database.GetFullFilePath(game.CoverImage);
            }

            return new LibraryGameInfo
            {
                Id = game.Id,
                Name = game.Name,
                PluginId = game.PluginId,
                GameId = game.GameId,
                IsInstalled = game.IsInstalled,
                Hidden = game.Hidden,
                LinkUrls = game.Links?.Where(l => !string.IsNullOrEmpty(l?.Url)).Select(l => l.Url).ToList() ?? new List<string>(),
                CoverPath = cover,
                Developers = JoinNames(game.Developers?.Select(d => d?.Name)),
                Publishers = JoinNames(game.Publishers?.Select(p => p?.Name)),
                Platforms = JoinNames(game.Platforms?.Select(p => p?.Name)),
                ReleaseDate = game.ReleaseDate?.Date,
                SourceName = game.Source?.Name
            };
        }

        private static string JoinNames(IEnumerable<string> names)
        {
            if (names == null)
            {
                return null;
            }

            var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            return list.Count == 0 ? null : string.Join(", ", list);
        }
    }

    /// <summary>Playnite-backed implementation of the services the view models need.</summary>
    internal sealed class PlayniteHost : IHostServices
    {
        private readonly GGDealsWishlistPlugin plugin;
        private readonly IPlayniteAPI api;
        private readonly GGDealsSettings settings;
        private readonly Func<WishlistDataService> service;
        private readonly object settingsLock = new object();

        public PlayniteHost(GGDealsWishlistPlugin plugin, IPlayniteAPI api, GGDealsSettings settings, Func<WishlistDataService> service)
        {
            this.plugin = plugin;
            this.api = api;
            this.settings = settings;
            this.service = service;
        }

        public event EventHandler SettingsChanged;

        public Dispatcher Dispatcher => Application.Current?.Dispatcher ?? api.MainView.UIDispatcher;

        public GGDealsSettings Settings => settings;

        public void UpdateSettings(Action<GGDealsSettings> change)
        {
            lock (settingsLock)
            {
                change(settings);
                settings.Normalize();
                plugin.SavePluginSettings(settings);
            }

            RegisterStoredKey();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SaveApiKey(string plainKey)
        {
            var key = plainKey?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            SecretRedactor.Register(key);
            UpdateSettings(s => s.ProtectedApiKey = SecretProtector.Protect(key));
        }

        /// <summary>Makes sure the decrypted key is known to the log redactor.</summary>
        public void RegisterStoredKey()
        {
            var key = SecretProtector.Unprotect(settings.ProtectedApiKey);
            if (!string.IsNullOrEmpty(key))
            {
                SecretRedactor.Register(key);
            }
        }

        public void OpenUrl(string url)
        {
            // Only web links are opened; anything else (file paths, custom schemes) is ignored.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                Log.Warn("Ignored a link that is not a web address.");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not open the browser");
                api.Dialogs.ShowErrorMessage("The link could not be opened in your browser.", "GG.deals Wishlist");
            }
        }

        public void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception e)
            {
                Log.Warn(e, "Clipboard is unavailable");
            }
        }

        public bool OpenPlayniteGame(Guid gameId)
        {
            if (api.Database.Games.Get(gameId) == null)
            {
                return false;
            }

            api.MainView.SwitchToLibraryView();
            api.MainView.SelectGame(gameId);
            return true;
        }

        public void OpenSettings() => api.MainView.OpenPluginSettings(plugin.Id);

        public string PromptText(string message, string caption, string defaultValue)
        {
            var result = api.Dialogs.SelectString(message, caption, defaultValue ?? string.Empty);
            return result != null && result.Result ? result.SelectedString?.Trim() : null;
        }

        public string PickImageFile() => api.Dialogs.SelectFile("Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif");

        /// <summary>Set by the plugin; the picker needs it to search SteamGridDB.</summary>
        public SteamGridDbClient GridClient { get; set; }

        public string PickSteamGridDbCover(long? steamAppId, string title)
        {
            if (GridClient == null)
            {
                return null;
            }

            try
            {
                var viewModel = new CoverPickerViewModel(GridClient, steamAppId, title);
                var window = api.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowCloseButton = true,
                    ShowMaximizeButton = false,
                    ShowMinimizeButton = false
                });
                window.Title = "GG.deals Wishlist – Change cover";
                window.Width = 720;
                window.Height = 620;
                window.Owner = api.Dialogs.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.Content = new CoverPickerView(viewModel, settings.Theme);
                viewModel.CloseRequested += (s, e) => window.Close();
                window.ShowDialog();
                return viewModel.ChosenUrl;
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not open the cover picker");
                return null;
            }
        }

        public bool Confirm(string message, string caption)
        {
            return api.Dialogs.ShowMessage(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        public void ShowSetupWizard()
        {
            try
            {
                var current = service();
                var viewModel = new SetupWizardViewModel(
                    this,
                    (key, region) => current.TestConnectionAsync(key, region),
                    CheckSteamWishlistAsync,
                    () => { _ = current.RefreshAsync(RefreshTrigger.Manual); });
                var window = api.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowCloseButton = true,
                    ShowMaximizeButton = false,
                    ShowMinimizeButton = false
                });
                window.Title = "GG.deals Wishlist – Set up";
                window.Width = 580;
                window.Height = 560;
                window.Owner = api.Dialogs.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.Content = new SetupWizardView(viewModel, settings.Theme);
                viewModel.CloseRequested += (s, e) => window.Close();
                window.ShowDialog();
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not open the setup wizard");
            }
        }

        /// <summary>Reads the given public Steam wishlist once, with a throwaway provider, so the wizard can say what it found.</summary>
        private static async Task<WishlistFetchResult> CheckSteamWishlistAsync(string steamId)
        {
            using (var provider = new SteamWishlistProvider(() => true, () => steamId))
            {
                return await provider.RefreshAsync(CancellationToken.None);
            }
        }

        public void ShowCollectionsManager()
        {
            try
            {
                var window = api.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowCloseButton = true,
                    ShowMaximizeButton = false,
                    ShowMinimizeButton = false
                });
                window.Title = "GG.deals Wishlist – Collections";
                window.Width = 560;
                window.Height = 520;
                window.Owner = api.Dialogs.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.Content = new CollectionsManagerView(new CollectionsManagerViewModel(service(), this), settings.Theme);
                window.ShowDialog();
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not open the collections manager");
            }
        }
    }
}
