using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using GGDealsWishlist.Api;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;
using GGDealsWishlist.Settings;

namespace GGDealsWishlist.ViewModels
{
    public enum SetupStep
    {
        Key = 0,
        Region = 1,
        Source = 2,
        Covers = 3,
        Done = 4
    }

    /// <summary>What a "check my Steam wishlist" attempt found, in words the wizard can show as-is.</summary>
    public sealed class SteamCheckResult
    {
        public bool IsOk { get; private set; }

        public string Message { get; private set; }

        public static SteamCheckResult From(WishlistFetchResult result)
        {
            if (result != null && result.State == WishlistProviderState.Available)
            {
                var count = result.Entries?.Count ?? 0;
                if (count > 0)
                {
                    return new SteamCheckResult
                    {
                        IsOk = true,
                        Message = "✓ Found " + count.ToString(CultureInfo.CurrentCulture) + (count == 1 ? " game" : " games") + " on your Steam wishlist."
                    };
                }

                // Steam answers the same way for an empty list and a private profile, so say both.
                return new SteamCheckResult
                {
                    Message = result.Message ?? "Your Steam wishlist is empty or private. Set \"Game details\" to Public in your Steam privacy settings."
                };
            }

            return new SteamCheckResult { Message = result?.Message ?? "Steam could not be checked right now." };
        }
    }

    /// <summary>
    /// The first-run setup dialog: API key, region, wishlist source, optional covers. Network checks are passed in as
    /// delegates so the flow is testable without Playnite; each step saves its own settings when the user moves on.
    /// </summary>
    public sealed class SetupWizardViewModel : ObservableBase
    {
        public const string SteamGridDbKeyUrl = "https://www.steamgriddb.com/profile/preferences/api";

        private const int NumberedSteps = 4;

        private readonly IHostServices host;
        private readonly Func<string, string, Task<ConnectionTestResult>> testKey;
        private readonly Func<string, Task<WishlistFetchResult>> checkSteam;
        private readonly Action startRefresh;

        private SetupStep step = SetupStep.Key;
        private bool isBusy;
        private string apiKey;
        private bool isKeyAccepted;
        private string keyStatus;
        private StatusKind keyStatusKind;
        private GGDealsRegion selectedRegion;
        private bool useSteam;
        private string steamIdText;
        private string manualText;
        private string steamStatus;
        private bool steamStatusIsOk;
        private string steamGridDbKey;

        public SetupWizardViewModel(
            IHostServices host,
            Func<string, string, Task<ConnectionTestResult>> testKey,
            Func<string, Task<WishlistFetchResult>> checkSteam,
            Action startRefresh)
        {
            this.host = host;
            this.testKey = testKey;
            this.checkSteam = checkSteam;
            this.startRefresh = startRefresh;

            var settings = host.Settings;
            isKeyAccepted = settings.HasApiKey;
            keyStatus = isKeyAccepted ? "An API key is already saved. Paste a new one to replace it, or continue." : null;
            keyStatusKind = isKeyAccepted ? StatusKind.Success : StatusKind.None;
            selectedRegion = Regions.Find(settings.Region) ?? Regions.Find(Regions.DefaultCode);
            useSteam = settings.WishlistSource != WishlistSourceMode.ManualList;
            steamIdText = settings.SteamId;
            manualText = settings.ManualWishlist;
            steamGridDbKey = settings.SteamGridDbApiKey;

            NextCommand = new RelayCommand(Next, () => CanGoNext);
            BackCommand = new RelayCommand(Back, () => CanGoBack);
            SkipCommand = new RelayCommand(Skip, () => CanSkip);
            CancelCommand = new RelayCommand(Cancel);
            TestKeyCommand = new AsyncRelayCommand(TestKeyAsync, () => !isBusy && !string.IsNullOrWhiteSpace(apiKey));
            CheckSteamCommand = new AsyncRelayCommand(CheckSteamAsync, () => !isBusy && SteamIdParser.TryParse(steamIdText, out _));
            OpenApiInfoCommand = new RelayCommand(() => host.OpenUrl(SidebarViewModel.ApiInfoUrl));
            OpenSteamPrivacyCommand = new RelayCommand(() => host.OpenUrl(StateGuidance.SteamPrivacyUrl));
            OpenSteamGridDbCommand = new RelayCommand(() => host.OpenUrl(SteamGridDbKeyUrl));
        }

        /// <summary>Raised when the dialog should close (finished or cancelled).</summary>
        public event EventHandler CloseRequested;

        public ICommand NextCommand { get; }

        public ICommand BackCommand { get; }

        public ICommand SkipCommand { get; }

        public ICommand CancelCommand { get; }

        public ICommand TestKeyCommand { get; }

        public ICommand CheckSteamCommand { get; }

        public ICommand OpenApiInfoCommand { get; }

        public ICommand OpenSteamPrivacyCommand { get; }

        public ICommand OpenSteamGridDbCommand { get; }

        // ---------------------------------------------------------------- steps

        public SetupStep Step
        {
            get => step;
            private set
            {
                if (SetValue(ref step, value))
                {
                    foreach (var name in new[]
                    {
                        nameof(IsKeyStep), nameof(IsRegionStep), nameof(IsSourceStep), nameof(IsCoversStep), nameof(IsDoneStep),
                        nameof(Title), nameof(Subtitle), nameof(StepText), nameof(NextText), nameof(SummaryLines)
                    })
                    {
                        OnPropertyChanged(name);
                    }

                    RaiseNavigationChanged();
                }
            }
        }

        public bool IsKeyStep => step == SetupStep.Key;

        public bool IsRegionStep => step == SetupStep.Region;

        public bool IsSourceStep => step == SetupStep.Source;

        public bool IsCoversStep => step == SetupStep.Covers;

        public bool IsDoneStep => step == SetupStep.Done;

        public string StepText => step == SetupStep.Done ? "All done" : "Step " + ((int)step + 1).ToString(CultureInfo.InvariantCulture) + " of " + NumberedSteps.ToString(CultureInfo.InvariantCulture);

        public string Title
        {
            get
            {
                switch (step)
                {
                    case SetupStep.Key:
                        return "Connect to GG.deals";
                    case SetupStep.Region:
                        return "Choose your region";
                    case SetupStep.Source:
                        return "Where is your wishlist?";
                    case SetupStep.Covers:
                        return "Better covers (optional)";
                    default:
                        return "You're all set";
                }
            }
        }

        public string Subtitle
        {
            get
            {
                switch (step)
                {
                    case SetupStep.Key:
                        return "Paste your GG.deals API key. It is stored encrypted for your Windows account and is never shown or logged.";
                    case SetupStep.Region:
                        return "Prices and currency come from this region. You can change it later in the extension settings.";
                    case SetupStep.Source:
                        return "GG.deals doesn't offer wishlist access through its API yet, so pick another source. Prices still come from GG.deals.";
                    case SetupStep.Covers:
                        return "Steam has no artwork for some games. A free SteamGridDB key lets the extension fill those gaps.";
                    default:
                        return "This is what will be used. You can change any of it in the extension settings.";
                }
            }
        }

        public string NextText => step == SetupStep.Done ? "Finish" : "Next";

        public bool CanGoNext
        {
            get
            {
                if (isBusy)
                {
                    return false;
                }

                switch (step)
                {
                    case SetupStep.Key:
                        return isKeyAccepted;
                    case SetupStep.Region:
                        return selectedRegion != null;
                    case SetupStep.Source:
                        return useSteam ? SteamIdParser.TryParse(steamIdText, out _) : !string.IsNullOrWhiteSpace(manualText);
                    default:
                        return true;
                }
            }
        }

        public bool CanGoBack => step != SetupStep.Key && !isBusy;

        /// <summary>The source and covers steps are optional; the key and region are not.</summary>
        public bool CanSkip => !isBusy && (step == SetupStep.Source || step == SetupStep.Covers);

        public bool IsBusy
        {
            get => isBusy;
            private set
            {
                if (SetValue(ref isBusy, value))
                {
                    RaiseNavigationChanged();
                }
            }
        }

        public void Next()
        {
            if (!CanGoNext)
            {
                return;
            }

            switch (step)
            {
                case SetupStep.Key:
                    Step = SetupStep.Region;
                    break;
                case SetupStep.Region:
                    var region = selectedRegion.Code;
                    host.UpdateSettings(s => s.Region = region);
                    Step = SetupStep.Source;
                    break;
                case SetupStep.Source:
                    SaveSource();
                    Step = SetupStep.Covers;
                    break;
                case SetupStep.Covers:
                    var gridKey = steamGridDbKey?.Trim() ?? string.Empty;
                    host.UpdateSettings(s => s.SteamGridDbApiKey = gridKey);
                    Step = SetupStep.Done;
                    break;
                default:
                    Finish();
                    break;
            }
        }

        public void Back()
        {
            if (CanGoBack)
            {
                Step = (SetupStep)((int)step - 1);
            }
        }

        public void Skip()
        {
            if (CanSkip)
            {
                Step = (SetupStep)((int)step + 1);
            }
        }

        public void Finish()
        {
            host.UpdateSettings(s =>
            {
                s.SetupCompleted = true;
                s.SetupPromptShown = true;
            });
            startRefresh?.Invoke();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        public void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

        private void SaveSource()
        {
            if (useSteam)
            {
                SteamIdParser.TryParse(steamIdText, out var id);
                var text = id.ToString(CultureInfo.InvariantCulture);
                host.UpdateSettings(s =>
                {
                    s.WishlistSource = WishlistSourceMode.SteamWishlist;
                    s.SteamId = text;
                });
            }
            else
            {
                var list = manualText.Trim();
                host.UpdateSettings(s =>
                {
                    s.WishlistSource = WishlistSourceMode.ManualList;
                    s.ManualWishlist = list;
                });
            }
        }

        private void RaiseNavigationChanged()
        {
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanSkip));
            CommandManager.InvalidateRequerySuggested();
        }

        // ---------------------------------------------------------------- key step

        /// <summary>The key being typed. The PasswordBox cannot be bound, so the view copies it here.</summary>
        public string ApiKey
        {
            get => apiKey;
            set
            {
                if (SetValue(ref apiKey, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsKeyAccepted
        {
            get => isKeyAccepted;
            private set
            {
                if (SetValue(ref isKeyAccepted, value))
                {
                    RaiseNavigationChanged();
                }
            }
        }

        public string KeyStatus
        {
            get => keyStatus;
            private set => SetValue(ref keyStatus, value);
        }

        public StatusKind KeyStatusKind
        {
            get => keyStatusKind;
            private set => SetValue(ref keyStatusKind, value);
        }

        public async Task TestKeyAsync()
        {
            var key = apiKey?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            IsBusy = true;
            KeyStatusKind = StatusKind.Busy;
            KeyStatus = "Testing connection…";
            try
            {
                var result = await testKey(key, selectedRegion?.Code ?? host.Settings.Region);
                if (result.IsSuccess)
                {
                    host.SaveApiKey(key);
                    ApiKey = null;
                    IsKeyAccepted = true;
                    KeyStatusKind = StatusKind.Success;
                    KeyStatus = "✓ Connected to GG.deals";
                }
                else
                {
                    KeyStatusKind = result.Error == ApiErrorKind.RateLimited || result.Error == ApiErrorKind.Network || result.Error == ApiErrorKind.Server ? StatusKind.Warning : StatusKind.Error;
                    KeyStatus = result.Message ?? ApiErrorMessages.For(result.Error, result.RetryAfterUtc);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Connection test failed");
                KeyStatusKind = StatusKind.Error;
                KeyStatus = ApiErrorMessages.For(ApiErrorKind.UnexpectedResponse, null);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ---------------------------------------------------------------- region step

        public IReadOnlyList<GGDealsRegion> RegionOptions => Regions.All;

        public GGDealsRegion SelectedRegion
        {
            get => selectedRegion;
            set
            {
                if (SetValue(ref selectedRegion, value))
                {
                    RaiseNavigationChanged();
                }
            }
        }

        // ---------------------------------------------------------------- source step

        public bool UseSteam
        {
            get => useSteam;
            set
            {
                if (SetValue(ref useSteam, value))
                {
                    OnPropertyChanged(nameof(UseManual));
                    RaiseNavigationChanged();
                }
            }
        }

        public bool UseManual
        {
            get => !useSteam;
            set => UseSteam = !value;
        }

        public string SteamIdText
        {
            get => steamIdText;
            set
            {
                if (SetValue(ref steamIdText, value))
                {
                    SteamStatus = null;
                    OnPropertyChanged(nameof(SteamHint));
                    RaiseNavigationChanged();
                }
            }
        }

        /// <summary>Why the typed Steam id can't be used; null when it is empty or valid.</summary>
        public string SteamHint => SteamIdParser.Describe(steamIdText);

        public string ManualText
        {
            get => manualText;
            set
            {
                if (SetValue(ref manualText, value))
                {
                    RaiseNavigationChanged();
                }
            }
        }

        public string SteamStatus
        {
            get => steamStatus;
            private set
            {
                if (SetValue(ref steamStatus, value))
                {
                    OnPropertyChanged(nameof(HasSteamStatus));
                }
            }
        }

        public bool HasSteamStatus => !string.IsNullOrEmpty(steamStatus);

        public bool SteamStatusIsOk
        {
            get => steamStatusIsOk;
            private set => SetValue(ref steamStatusIsOk, value);
        }

        public async Task CheckSteamAsync()
        {
            if (!SteamIdParser.TryParse(steamIdText, out var id))
            {
                return;
            }

            IsBusy = true;
            SteamStatusIsOk = false;
            SteamStatus = "Checking your Steam wishlist…";
            try
            {
                var result = SteamCheckResult.From(await checkSteam(id.ToString(CultureInfo.InvariantCulture)));
                SteamStatusIsOk = result.IsOk;
                SteamStatus = result.Message;
            }
            catch (Exception e)
            {
                Log.Error(e, "Steam wishlist check failed");
                SteamStatus = SteamCheckResult.From(null).Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ---------------------------------------------------------------- covers step

        public string SteamGridDbKey
        {
            get => steamGridDbKey;
            set => SetValue(ref steamGridDbKey, value);
        }

        // ---------------------------------------------------------------- summary

        /// <summary>One line per choice, read back from the saved settings.</summary>
        public IReadOnlyList<string> SummaryLines
        {
            get
            {
                var settings = host.Settings;
                var lines = new List<string>
                {
                    settings.HasApiKey ? "API key: saved" : "API key: not set",
                    "Region: " + (Regions.Find(settings.Region)?.DisplayName ?? settings.Region)
                };

                switch (settings.WishlistSource)
                {
                    case WishlistSourceMode.SteamWishlist:
                        lines.Add("Wishlist: your Steam wishlist");
                        break;
                    case WishlistSourceMode.ManualList:
                        var count = (settings.ManualWishlist ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Count(l => !string.IsNullOrWhiteSpace(l));
                        lines.Add("Wishlist: manual list (" + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " entry)" : " entries)"));
                        break;
                    default:
                        lines.Add("Wishlist: not chosen yet (pick a source in the settings)");
                        break;
                }

                lines.Add(string.IsNullOrWhiteSpace(settings.SteamGridDbApiKey) ? "Covers: Steam artwork only" : "Covers: Steam artwork + SteamGridDB");
                return lines;
            }
        }
    }
}
