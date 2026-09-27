using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using GGDealsWishlist.Api;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;
using GGDealsWishlist.Services;
using GGDealsWishlist.ViewModels;
using Playnite.SDK;
using RelayCommand = GGDealsWishlist.Infrastructure.RelayCommand;

namespace GGDealsWishlist.Settings
{
    /// <summary>
    /// Settings page view model. Playnite drives the edit transaction (BeginEdit → VerifySettings → EndEdit /
    /// CancelEdit); everything is edited on a copy so cancelling never leaks half-applied values.
    /// The plain API key only lives in <see cref="PendingApiKey"/> until it is encrypted on save.
    /// </summary>
    public sealed class GGDealsSettingsViewModel : ObservableBase, ISettings
    {
        private const int MinKeyLength = 8;
        private const int MaxKeyLength = 256;

        private readonly WishlistDataService service;
        private readonly IHostServices host;
        private GGDealsSettings settings;
        private string pendingApiKey;
        private bool removeKeyOnSave;
        private string connectionStatus;
        private StatusKind connectionStatusKind;
        private bool isTesting;
        private string manualListInfo;
        private string actionStatus;

        public GGDealsSettingsViewModel(WishlistDataService service, IHostServices host)
        {
            this.service = service;
            this.host = host;
            settings = host.Settings.Clone();

            TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => !IsTesting && (HasPendingKey || (HasStoredKey && !removeKeyOnSave)));
            RemoveKeyCommand = new RelayCommand(RemoveKey, () => (HasStoredKey && !removeKeyOnSave) || HasPendingKey);
            UndoRemoveKeyCommand = new RelayCommand(() => IsKeyMarkedForRemoval = false);
            RefreshNowCommand = new AsyncRelayCommand(RefreshNowAsync, () => !service.Status.IsRefreshing);
            ClearCacheCommand = new RelayCommand(ClearCache);
            ManageCollectionsCommand = new RelayCommand(() => host.ShowCollectionsManager());
            OpenApiInfoCommand = new RelayCommand(() => host.OpenUrl(SidebarViewModel.ApiInfoUrl));
            OpenWishlistCommand = new RelayCommand(() => host.OpenUrl(settings.GGDealsWishlistUrl));
            OpenAttributionCommand = new RelayCommand(() => host.OpenUrl(SidebarViewModel.GGDealsHomeUrl));
            ResetAccentCommand = new RelayCommand(() => AccentColor = GGDealsSettings.DefaultAccentColor);
            ResetFieldsCommand = new RelayCommand(ResetInformationFields);
            UpdateUsageCommand = new RelayCommand(UpdateUsage);
            UpdateManualListInfo();
            UpdateUsage();
        }

        /// <summary>The copy being edited. Simple toggles bind straight to it.</summary>
        public GGDealsSettings Settings
        {
            get => settings;
            private set => SetValue(ref settings, value);
        }

        // =========================================================================================
        // Option lists
        // =========================================================================================

        public IReadOnlyList<GGDealsRegion> RegionOptions => Regions.All;

        public IReadOnlyList<Option<int>> RefreshIntervalOptions { get; } = GGDealsSettings.RefreshIntervals
            .Select(m => new Option<int>(m, m < 60 ? m + " minutes" : m == 60 ? "1 hour" : (m / 60) + " hours"))
            .ToList();

        public IReadOnlyList<Option<ViewMode>> ViewModeOptions { get; } = new List<Option<ViewMode>>
        {
            new Option<ViewMode>(ViewMode.CoverInfo, "Cover + information"),
            new Option<ViewMode>(ViewMode.Compact, "Compact"),
            new Option<ViewMode>(ViewMode.List, "List")
        };

        public IReadOnlyList<Option<ThemeMode>> ThemeOptions { get; } = new List<Option<ThemeMode>>
        {
            new Option<ThemeMode>(ThemeMode.Inherit, "Use Playnite theme"),
            new Option<ThemeMode>(ThemeMode.Dark, "Dark"),
            new Option<ThemeMode>(ThemeMode.Light, "Light")
        };

        public IReadOnlyList<Option<CardDensity>> DensityOptions { get; } = new List<Option<CardDensity>>
        {
            new Option<CardDensity>(CardDensity.Comfortable, "Comfortable"),
            new Option<CardDensity>(CardDensity.Compact, "Compact")
        };

        public IReadOnlyList<Option<CoverSize>> CoverSizeOptions { get; } = new List<Option<CoverSize>>
        {
            new Option<CoverSize>(CoverSize.Small, "Small"),
            new Option<CoverSize>(CoverSize.Medium, "Medium"),
            new Option<CoverSize>(CoverSize.Large, "Large")
        };

        public IReadOnlyList<Option<PricePreference>> PricePreferenceOptions { get; } = new List<Option<PricePreference>>
        {
            new Option<PricePreference>(PricePreference.Retail, "Official stores (retail)"),
            new Option<PricePreference>(PricePreference.Keyshop, "Keyshops"),
            new Option<PricePreference>(PricePreference.LowestOfBoth, "Lowest of both")
        };

        public IReadOnlyList<Option<WishlistSourceMode>> WishlistSourceOptions { get; } = new List<Option<WishlistSourceMode>>
        {
            new Option<WishlistSourceMode>(WishlistSourceMode.OfficialOnly, "GG.deals wishlist (official API)"),
            new Option<WishlistSourceMode>(WishlistSourceMode.SteamWishlist, "My Steam wishlist"),
            new Option<WishlistSourceMode>(WishlistSourceMode.ManualList, "Manual list of Steam games")
        };

        // =========================================================================================
        // Account / API key
        // =========================================================================================

        public bool HasStoredKey => host.Settings.HasApiKey;

        public bool HasPendingKey => !string.IsNullOrWhiteSpace(pendingApiKey);

        /// <summary>Set by the view from the PasswordBox; never bound to a visible control.</summary>
        public string PendingApiKey
        {
            get => pendingApiKey;
            set
            {
                if (SetValue(ref pendingApiKey, value))
                {
                    if (HasPendingKey)
                    {
                        SecretRedactor.Register(value.Trim());
                        removeKeyOnSave = false;
                    }

                    ConnectionStatus = null;
                    RaiseKeyState();
                }
            }
        }

        public bool IsKeyMarkedForRemoval
        {
            get => removeKeyOnSave;
            private set
            {
                if (SetValue(ref removeKeyOnSave, value))
                {
                    RaiseKeyState();
                }
            }
        }

        public string ApiKeyStateText
        {
            get
            {
                if (removeKeyOnSave)
                {
                    return "The saved API key will be removed when you save.";
                }

                if (HasPendingKey)
                {
                    return HasStoredKey ? "The saved key will be replaced when you save." : "The key will be stored when you save.";
                }

                return HasStoredKey ? "API key saved  ••••••••••••" : "No API key saved.";
            }
        }

        public string ApiKeyValidationText => HasPendingKey ? ValidateKey(pendingApiKey.Trim()) : null;

        public string ConnectionStatus
        {
            get => connectionStatus;
            private set => SetValue(ref connectionStatus, value);
        }

        public StatusKind ConnectionStatusKind
        {
            get => connectionStatusKind;
            private set => SetValue(ref connectionStatusKind, value);
        }

        public bool IsTesting
        {
            get => isTesting;
            private set
            {
                if (SetValue(ref isTesting, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string Region
        {
            get => settings.Region;
            set
            {
                if (settings.Region != value && Regions.IsValid(value))
                {
                    settings.Region = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RegionCurrencyText));
                }
            }
        }

        public string RegionCurrencyText
        {
            get
            {
                var region = Regions.Find(settings.Region);
                return region == null ? null : "Prices are shown in " + region.Currency + ". Changing the region refreshes prices on the next update.";
            }
        }

        // API usage (local tracking of the documented 100/minute and 1,000/hour limits)
        public string UsageMinuteText { get; private set; }

        public string UsageHourText { get; private set; }

        public string UsageResetText { get; private set; }

        // =========================================================================================
        // Wishlist source
        // =========================================================================================

        public string ProviderStatusText => service.Status.ProviderMessage;

        public WishlistSourceMode WishlistSource
        {
            get => settings.WishlistSource;
            set
            {
                if (settings.WishlistSource != value)
                {
                    settings.WishlistSource = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsManualList));
                    OnPropertyChanged(nameof(IsSteamWishlist));
                    OnPropertyChanged(nameof(IsOfficialOnly));
                    OnPropertyChanged(nameof(SteamIdValidationText));
                    UpdateManualListInfo();
                }
            }
        }

        public bool IsManualList => settings.WishlistSource == WishlistSourceMode.ManualList;

        public bool IsSteamWishlist => settings.WishlistSource == WishlistSourceMode.SteamWishlist;

        public bool IsOfficialOnly => settings.WishlistSource == WishlistSourceMode.OfficialOnly;

        public string SteamId
        {
            get => settings.SteamId;
            set
            {
                if (settings.SteamId != value)
                {
                    settings.SteamId = value ?? string.Empty;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SteamIdValidationText));
                }
            }
        }

        public string SteamIdValidationText => !IsSteamWishlist
            ? null
            : string.IsNullOrWhiteSpace(settings.SteamId)
                ? "Enter your SteamID64 to load your Steam wishlist."
                : SteamIdParser.Describe(settings.SteamId);

        public string ManualWishlist
        {
            get => settings.ManualWishlist;
            set
            {
                if (settings.ManualWishlist != value)
                {
                    settings.ManualWishlist = value ?? string.Empty;
                    OnPropertyChanged();
                    UpdateManualListInfo();
                }
            }
        }

        public string ManualListInfo
        {
            get => manualListInfo;
            private set => SetValue(ref manualListInfo, value);
        }

        // =========================================================================================
        // Appearance
        // =========================================================================================

        public string AccentColor
        {
            get => settings.AccentColor;
            set
            {
                if (settings.AccentColor != value)
                {
                    settings.AccentColor = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AccentPreview));
                    OnPropertyChanged(nameof(AccentValidationText));
                }
            }
        }

        public Brush AccentPreview
        {
            get
            {
                var color = DisplayOptions.ParseColor(settings.AccentColor);
                return color.HasValue ? new SolidColorBrush(color.Value) : Brushes.Transparent;
            }
        }

        public string AccentValidationText => DisplayOptions.ParseColor(settings.AccentColor).HasValue ? null : "Use a colour like #FF8A3D.";

        public string ActionStatus
        {
            get => actionStatus;
            private set => SetValue(ref actionStatus, value);
        }

        // =========================================================================================
        // Commands
        // =========================================================================================

        public ICommand TestConnectionCommand { get; }
        public ICommand RemoveKeyCommand { get; }
        public ICommand UndoRemoveKeyCommand { get; }
        public ICommand RefreshNowCommand { get; }
        public ICommand ClearCacheCommand { get; }
        public ICommand ManageCollectionsCommand { get; }
        public ICommand OpenApiInfoCommand { get; }
        public ICommand OpenWishlistCommand { get; }
        public ICommand OpenAttributionCommand { get; }
        public ICommand ResetAccentCommand { get; }
        public ICommand ResetFieldsCommand { get; }
        public ICommand UpdateUsageCommand { get; }

        /// <summary>Raised when the view should clear its PasswordBox.</summary>
        public event EventHandler PendingKeyCleared;

        // =========================================================================================
        // ISettings
        // =========================================================================================

        public void BeginEdit()
        {
            Settings = host.Settings.Clone();
            ClearPendingKey();
            removeKeyOnSave = false;
            ConnectionStatus = null;
            ActionStatus = null;
            UpdateManualListInfo();
            UpdateUsage();
            OnAllPropertiesChanged();
        }

        public void CancelEdit()
        {
            ClearPendingKey();
            removeKeyOnSave = false;
            Settings = host.Settings.Clone();
        }

        public void EndEdit()
        {
            var edited = settings.Clone();
            edited.Normalize();
            if (removeKeyOnSave)
            {
                edited.ProtectedApiKey = null;
            }
            else if (HasPendingKey && ValidateKey(pendingApiKey.Trim()) == null)
            {
                edited.ProtectedApiKey = SecretProtector.Protect(pendingApiKey.Trim());
            }
            else
            {
                edited.ProtectedApiKey = host.Settings.ProtectedApiKey;
            }

            ClearPendingKey();
            removeKeyOnSave = false;
            host.UpdateSettings(s => s.CopyFrom(edited));
            Settings = host.Settings.Clone();
            OnAllPropertiesChanged();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (HasPendingKey)
            {
                var keyError = ValidateKey(pendingApiKey.Trim());
                if (keyError != null)
                {
                    errors.Add(keyError);
                }
            }

            if (!Regions.IsValid(settings.Region))
            {
                errors.Add("Select a valid GG.deals region.");
            }

            if (!GGDealsSettings.RefreshIntervals.Contains(settings.RefreshIntervalMinutes))
            {
                errors.Add("Select a refresh interval between 15 minutes and 24 hours.");
            }

            if (IsSteamWishlist && !string.IsNullOrWhiteSpace(settings.SteamId) && SteamIdParser.Describe(settings.SteamId) != null)
            {
                errors.Add(SteamIdParser.Describe(settings.SteamId));
            }

            if (AccentValidationText != null)
            {
                errors.Add("The accent colour is not valid. " + AccentValidationText);
            }

            if (!Uri.TryCreate(settings.GGDealsWishlistUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                errors.Add("The GG.deals wishlist link must be a web address.");
            }

            return errors.Count == 0;
        }

        /// <summary>Format check only; whether GG.deals accepts the key is verified by "Test connection".</summary>
        internal static string ValidateKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "Enter an API key.";
            }

            if (key.Length < MinKeyLength || key.Length > MaxKeyLength)
            {
                return "The API key does not look complete. Copy it again from your GG.deals account.";
            }

            if (key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c > 0x7E))
            {
                return "The API key contains characters that are not allowed.";
            }

            return null;
        }

        // =========================================================================================
        // Actions
        // =========================================================================================

        private async Task TestConnectionAsync()
        {
            var key = HasPendingKey ? pendingApiKey.Trim() : SecretProtector.Unprotect(host.Settings.ProtectedApiKey);
            var formatError = ValidateKey(key);
            if (formatError != null)
            {
                ConnectionStatusKind = StatusKind.Error;
                ConnectionStatus = HasPendingKey || key == null ? formatError : "✕ API key is invalid.";
                return;
            }

            IsTesting = true;
            ConnectionStatusKind = StatusKind.Busy;
            ConnectionStatus = "Testing connection…";
            try
            {
                var result = await service.TestConnectionAsync(key, settings.Region);
                if (result.IsSuccess)
                {
                    ConnectionStatusKind = StatusKind.Success;
                    ConnectionStatus = HasPendingKey ? "✓ Connected to GG.deals. Save to keep this key." : "✓ Connected to GG.deals";
                }
                else
                {
                    ConnectionStatusKind = result.Error == ApiErrorKind.InvalidApiKey || result.Error == ApiErrorKind.UnexpectedResponse ? StatusKind.Error : StatusKind.Warning;
                    ConnectionStatus = result.Message ?? ApiErrorMessages.For(result.Error, result.RetryAfterUtc);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Connection test failed");
                ConnectionStatusKind = StatusKind.Error;
                ConnectionStatus = ApiErrorMessages.For(ApiErrorKind.UnexpectedResponse, null);
            }
            finally
            {
                IsTesting = false;
                UpdateUsage();
            }
        }

        private void RemoveKey()
        {
            ClearPendingKey();
            if (HasStoredKey)
            {
                IsKeyMarkedForRemoval = true;
            }

            ConnectionStatus = null;
            RaiseKeyState();
        }

        private async Task RefreshNowAsync()
        {
            ActionStatus = "Refreshing…";
            var outcome = await Task.Run(() => service.RefreshAsync(RefreshTrigger.Manual));
            switch (outcome)
            {
                case RefreshOutcome.Completed:
                    ActionStatus = "✓ Wishlist and prices updated.";
                    break;
                case RefreshOutcome.NothingToDo:
                    ActionStatus = "Prices are already up to date.";
                    break;
                default:
                    var error = service.Status.LastError;
                    ActionStatus = error != null ? ApiErrorMessages.For(error.Kind, error.RetryAfterUtc) : "Refresh did not complete.";
                    break;
            }

            UpdateUsage();
        }

        private void ClearCache()
        {
            if (!host.Confirm("Clear cached wishlist and price data? Favourites and collections are kept. Prices are downloaded again on the next refresh.", "Clear cache"))
            {
                return;
            }

            service.ClearCache();
            ActionStatus = "✓ Cache cleared.";
        }

        private void ResetInformationFields()
        {
            var defaults = new GGDealsSettings();
            settings.ShowCover = defaults.ShowCover;
            settings.ShowTitle = defaults.ShowTitle;
            settings.ShowCurrentPrice = defaults.ShowCurrentPrice;
            settings.ShowDiscount = defaults.ShowDiscount;
            settings.ShowPriceSource = defaults.ShowPriceSource;
            settings.ShowHistoricalLow = defaults.ShowHistoricalLow;
            settings.ShowRetailPrice = defaults.ShowRetailPrice;
            settings.ShowKeyshopPrice = defaults.ShowKeyshopPrice;
            settings.ShowWishlistDate = defaults.ShowWishlistDate;
            settings.ShowRating = defaults.ShowRating;
            settings.ShowPlatform = defaults.ShowPlatform;
            settings.ShowReleaseDate = defaults.ShowReleaseDate;
            settings.ShowStoreCount = defaults.ShowStoreCount;
            settings.ShowFavouriteStar = defaults.ShowFavouriteStar;
            settings.ShowCollections = defaults.ShowCollections;
            settings.ShowGameCount = defaults.ShowGameCount;
            settings.ShowOnSaleCount = defaults.ShowOnSaleCount;
            settings.ShowHistoricalLowCount = defaults.ShowHistoricalLowCount;
            settings.ShowLastUpdated = defaults.ShowLastUpdated;

            // The toggles bind through Settings.*, which is a plain POCO, so re-announce the object.
            var current = settings;
            Settings = null;
            Settings = current;
        }

        private void UpdateUsage()
        {
            try
            {
                var budget = service.GetRateLimitStatus();
                UsageMinuteText = budget.MinuteUsed.ToString(CultureInfo.CurrentCulture) + " / " + RateLimitTracker.RecordsPerMinute + " in the last minute";
                UsageHourText = budget.HourUsed.ToString(CultureInfo.CurrentCulture) + " / " + RateLimitTracker.RecordsPerHour.ToString("N0", CultureInfo.CurrentCulture) + " in the last hour";
                if (budget.IsBlocked && budget.NextAvailableUtc.HasValue)
                {
                    UsageResetText = "Rate limited until " + budget.NextAvailableUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
                }
                else if (budget.HourResetUtc.HasValue && budget.HourUsed > 0)
                {
                    UsageResetText = "Hourly usage resets by " + budget.HourResetUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
                }
                else
                {
                    UsageResetText = null;
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not read API usage");
                UsageMinuteText = UsageHourText = UsageResetText = null;
            }

            OnPropertyChanged(nameof(UsageMinuteText));
            OnPropertyChanged(nameof(UsageHourText));
            OnPropertyChanged(nameof(UsageResetText));
        }

        private void UpdateManualListInfo()
        {
            if (!IsManualList)
            {
                ManualListInfo = null;
                return;
            }

            var entries = ManualWishlistProvider.Parse(settings.ManualWishlist, out var invalid);
            var text = entries.Count == 1 ? "1 game" : entries.Count + " games";
            if (invalid.Count > 0)
            {
                text += " · " + (invalid.Count == 1 ? "1 line was" : invalid.Count + " lines were") + " not recognised: " + string.Join(", ", invalid.Take(3).Select(l => "\"" + Truncate(l, 30) + "\""));
            }

            ManualListInfo = text;
        }

        private void ClearPendingKey()
        {
            if (pendingApiKey != null)
            {
                pendingApiKey = null;
                OnPropertyChanged(nameof(PendingApiKey));
            }

            PendingKeyCleared?.Invoke(this, EventArgs.Empty);
            RaiseKeyState();
        }

        private void RaiseKeyState()
        {
            OnPropertyChanged(nameof(HasStoredKey));
            OnPropertyChanged(nameof(HasPendingKey));
            OnPropertyChanged(nameof(IsKeyMarkedForRemoval));
            OnPropertyChanged(nameof(ApiKeyStateText));
            OnPropertyChanged(nameof(ApiKeyValidationText));
            CommandManager.InvalidateRequerySuggested();
        }

        private static string Truncate(string text, int max) => text.Length <= max ? text : text.Substring(0, max) + "…";
    }
}
