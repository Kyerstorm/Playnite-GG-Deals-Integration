using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using GGDealsWishlist.Api;
using GGDealsWishlist.Models;
using GGDealsWishlist.Providers;
using GGDealsWishlist.Settings;
using GGDealsWishlist.ViewModels;
using Xunit;

namespace GGDealsWishlist.Tests
{
    public sealed class SetupWizardTests
    {
        private const string SteamId = "76561198000000000";

        private sealed class FakeHost : IHostServices
        {
            public FakeHost(GGDealsSettings settings) => Settings = settings;

            public event EventHandler SettingsChanged;

            public Dispatcher Dispatcher => Dispatcher.CurrentDispatcher;

            public GGDealsSettings Settings { get; }

            public string SavedKey { get; private set; }

            public void UpdateSettings(Action<GGDealsSettings> change)
            {
                change(Settings);
                Settings.Normalize();
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }

            public void SaveApiKey(string plainKey)
            {
                SavedKey = plainKey;
                UpdateSettings(s => s.ProtectedApiKey = "protected:" + plainKey);
            }

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

            public bool Confirm(string message, string caption) => false;

            public string PickImageFile() => null;

            public string PickSteamGridDbCover(long? steamAppId, string title) => null;

            public void ShowCollectionsManager()
            {
            }

            public void ShowSetupWizard()
            {
            }
        }

        private sealed class Harness
        {
            public Harness(GGDealsSettings settings = null)
            {
                Host = new FakeHost(settings ?? new GGDealsSettings { Region = "us" });
                KeyResult = new ConnectionTestResult();
                SteamResult = new WishlistFetchResult { State = WishlistProviderState.Available, Entries = new List<WishlistEntry> { new WishlistEntry(), new WishlistEntry() } };
                ViewModel = new SetupWizardViewModel(
                    Host,
                    (key, region) =>
                    {
                        TestedKeys.Add(key + "@" + region);
                        return Task.FromResult(KeyResult);
                    },
                    id =>
                    {
                        CheckedIds.Add(id);
                        return Task.FromResult(SteamResult);
                    },
                    () => Refreshes++);
                ViewModel.CloseRequested += (s, e) => Closed++;
            }

            public FakeHost Host { get; }

            public SetupWizardViewModel ViewModel { get; }

            public ConnectionTestResult KeyResult { get; set; }

            public WishlistFetchResult SteamResult { get; set; }

            public List<string> TestedKeys { get; } = new List<string>();

            public List<string> CheckedIds { get; } = new List<string>();

            public int Refreshes { get; private set; }

            public int Closed { get; private set; }

            public async Task AcceptKeyAsync(string key = "secret-key")
            {
                ViewModel.ApiKey = key;
                await ViewModel.TestKeyAsync();
            }
        }

        // ------------------------------------------------------------------ settings migration

        [Fact]
        public void A_fresh_install_has_not_completed_setup()
        {
            var settings = new GGDealsSettings();
            settings.Normalize();

            Assert.False(settings.SetupCompleted);
            Assert.False(settings.SetupPromptShown);
        }

        [Fact]
        public void Existing_users_with_a_key_are_marked_as_set_up_when_settings_are_upgraded()
        {
            var old = new GGDealsSettings { SettingsVersion = 1, ProtectedApiKey = "protected" };

            old.Normalize();

            Assert.True(old.SetupCompleted);
            Assert.Equal(GGDealsSettings.CurrentVersion, old.SettingsVersion);
        }

        [Fact]
        public void Old_settings_without_a_key_still_need_setup()
        {
            var old = new GGDealsSettings { SettingsVersion = 1 };

            old.Normalize();

            Assert.False(old.SetupCompleted);
        }

        [Fact]
        public void Upgrading_only_happens_once()
        {
            var settings = new GGDealsSettings { SettingsVersion = 1, ProtectedApiKey = "protected" };
            settings.Normalize();
            settings.SetupCompleted = false;

            settings.Normalize();

            Assert.False(settings.SetupCompleted);
        }

        // ------------------------------------------------------------------ steam check

        [Fact]
        public void A_steam_wishlist_with_games_is_reported_with_its_size()
        {
            var result = SteamCheckResult.From(new WishlistFetchResult { State = WishlistProviderState.Available, Entries = new List<WishlistEntry> { new WishlistEntry() } });

            Assert.True(result.IsOk);
            Assert.Equal("✓ Found 1 game on your Steam wishlist.", result.Message);
        }

        [Fact]
        public void An_empty_steam_wishlist_is_explained_not_called_a_success()
        {
            var result = SteamCheckResult.From(new WishlistFetchResult { State = WishlistProviderState.Available, Message = "Your Steam wishlist is empty or private." });

            Assert.False(result.IsOk);
            Assert.Equal("Your Steam wishlist is empty or private.", result.Message);
        }

        [Fact]
        public void A_steam_failure_shows_the_providers_message_or_a_fallback()
        {
            Assert.Equal("Steam is limiting requests right now.", SteamCheckResult.From(new WishlistFetchResult { State = WishlistProviderState.Error, Message = "Steam is limiting requests right now." }).Message);
            Assert.False(SteamCheckResult.From(new WishlistFetchResult { State = WishlistProviderState.Error }).IsOk);
            Assert.False(string.IsNullOrEmpty(SteamCheckResult.From(null).Message));
        }

        // ------------------------------------------------------------------ key step

        [Fact]
        public void The_wizard_starts_on_the_key_step_and_cannot_continue_without_a_key()
        {
            var h = new Harness();

            Assert.Equal(SetupStep.Key, h.ViewModel.Step);
            Assert.False(h.ViewModel.CanGoNext);
            Assert.False(h.ViewModel.CanGoBack);
        }

        [Fact]
        public void A_key_that_is_already_saved_lets_the_user_continue_straight_away()
        {
            var h = new Harness(new GGDealsSettings { Region = "us", ProtectedApiKey = "protected:old" });

            Assert.True(h.ViewModel.IsKeyAccepted);
            Assert.True(h.ViewModel.CanGoNext);
        }

        [Fact]
        public async Task A_working_key_is_tested_in_the_chosen_region_then_saved_and_unlocks_next()
        {
            var h = new Harness(new GGDealsSettings { Region = "gb" });

            await h.AcceptKeyAsync("  my-key  ");

            Assert.Equal(new[] { "my-key@gb" }, h.TestedKeys);
            Assert.Equal("my-key", h.Host.SavedKey);
            Assert.True(h.ViewModel.IsKeyAccepted);
            Assert.Null(h.ViewModel.ApiKey);
            Assert.Equal("✓ Connected to GG.deals", h.ViewModel.KeyStatus);
            Assert.True(h.ViewModel.CanGoNext);
        }

        [Fact]
        public async Task A_rejected_key_is_not_saved_and_explains_why()
        {
            var h = new Harness();
            h.KeyResult = new ConnectionTestResult { Error = ApiErrorKind.InvalidApiKey, Message = "That key was not accepted." };

            await h.AcceptKeyAsync();

            Assert.Null(h.Host.SavedKey);
            Assert.False(h.ViewModel.IsKeyAccepted);
            Assert.Equal("That key was not accepted.", h.ViewModel.KeyStatus);
            Assert.False(h.ViewModel.CanGoNext);
        }

        [Fact]
        public async Task Testing_with_a_blank_key_does_nothing()
        {
            var h = new Harness();
            h.ViewModel.ApiKey = "   ";

            await h.ViewModel.TestKeyAsync();

            Assert.Empty(h.TestedKeys);
        }

        // ------------------------------------------------------------------ navigation and saving

        [Fact]
        public async Task The_region_is_saved_when_leaving_the_region_step_and_back_returns_without_losing_it()
        {
            var h = new Harness();
            await h.AcceptKeyAsync();

            h.ViewModel.Next();
            Assert.Equal(SetupStep.Region, h.ViewModel.Step);
            h.ViewModel.SelectedRegion = Regions.Find("de");
            h.ViewModel.Next();

            Assert.Equal("de", h.Host.Settings.Region);
            Assert.Equal(SetupStep.Source, h.ViewModel.Step);

            h.ViewModel.Back();
            Assert.Equal(SetupStep.Region, h.ViewModel.Step);
            Assert.Equal("de", h.ViewModel.SelectedRegion.Code);
        }

        private static async Task<Harness> AtSourceStepAsync()
        {
            var h = new Harness();
            await h.AcceptKeyAsync();
            h.ViewModel.Next();
            h.ViewModel.Next();
            Assert.Equal(SetupStep.Source, h.ViewModel.Step);
            return h;
        }

        [Fact]
        public async Task A_steam_source_needs_a_valid_id_and_is_saved_on_next()
        {
            var h = await AtSourceStepAsync();

            Assert.True(h.ViewModel.UseSteam);
            Assert.False(h.ViewModel.CanGoNext);

            h.ViewModel.SteamIdText = "steamcommunity.com/id/someone";
            Assert.False(h.ViewModel.CanGoNext);
            Assert.Contains("17-digit", h.ViewModel.SteamHint);

            h.ViewModel.SteamIdText = " " + SteamId + " ";
            Assert.True(h.ViewModel.CanGoNext);
            Assert.Null(h.ViewModel.SteamHint);

            h.ViewModel.Next();

            Assert.Equal(WishlistSourceMode.SteamWishlist, h.Host.Settings.WishlistSource);
            Assert.Equal(SteamId, h.Host.Settings.SteamId);
            Assert.Equal(SetupStep.Covers, h.ViewModel.Step);
        }

        [Fact]
        public async Task A_manual_source_needs_at_least_one_entry_and_is_saved_on_next()
        {
            var h = await AtSourceStepAsync();

            h.ViewModel.UseSteam = false;
            Assert.True(h.ViewModel.UseManual);
            Assert.False(h.ViewModel.CanGoNext);

            h.ViewModel.ManualText = "620\r\n367520";
            Assert.True(h.ViewModel.CanGoNext);
            h.ViewModel.Next();

            Assert.Equal(WishlistSourceMode.ManualList, h.Host.Settings.WishlistSource);
            Assert.Equal("620\r\n367520", h.Host.Settings.ManualWishlist);
        }

        [Fact]
        public async Task Checking_a_steam_id_reports_the_result_without_saving_anything()
        {
            var h = await AtSourceStepAsync();
            h.ViewModel.SteamIdText = SteamId;

            await h.ViewModel.CheckSteamAsync();

            Assert.Equal(new[] { SteamId }, h.CheckedIds);
            Assert.Equal("✓ Found 2 games on your Steam wishlist.", h.ViewModel.SteamStatus);
            Assert.True(h.ViewModel.SteamStatusIsOk);
            Assert.Equal(WishlistSourceMode.OfficialOnly, h.Host.Settings.WishlistSource);
        }

        [Fact]
        public async Task Checking_an_invalid_id_does_not_contact_steam()
        {
            var h = await AtSourceStepAsync();
            h.ViewModel.SteamIdText = "nonsense";

            await h.ViewModel.CheckSteamAsync();

            Assert.Empty(h.CheckedIds);
        }

        [Fact]
        public async Task Skipping_the_source_step_leaves_the_current_source_alone()
        {
            var h = await AtSourceStepAsync();
            Assert.True(h.ViewModel.CanSkip);

            h.ViewModel.Skip();

            Assert.Equal(SetupStep.Covers, h.ViewModel.Step);
            Assert.Equal(WishlistSourceMode.OfficialOnly, h.Host.Settings.WishlistSource);
            Assert.Equal(string.Empty, h.Host.Settings.SteamId);
        }

        [Fact]
        public async Task The_key_and_region_steps_cannot_be_skipped()
        {
            var h = new Harness();
            Assert.False(h.ViewModel.CanSkip);
            await h.AcceptKeyAsync();
            h.ViewModel.Next();

            Assert.Equal(SetupStep.Region, h.ViewModel.Step);
            Assert.False(h.ViewModel.CanSkip);
        }

        [Fact]
        public async Task Finishing_marks_setup_complete_starts_a_refresh_and_closes()
        {
            var h = await AtSourceStepAsync();
            h.ViewModel.SteamIdText = SteamId;
            h.ViewModel.Next();
            Assert.Equal(SetupStep.Covers, h.ViewModel.Step);
            h.ViewModel.SteamGridDbKey = "  grid-key ";
            h.ViewModel.Next();

            Assert.Equal(SetupStep.Done, h.ViewModel.Step);
            Assert.Equal("grid-key", h.Host.Settings.SteamGridDbApiKey);
            Assert.False(h.Host.Settings.SetupCompleted);

            h.ViewModel.Finish();

            Assert.True(h.Host.Settings.SetupCompleted);
            Assert.Equal(1, h.Refreshes);
            Assert.Equal(1, h.Closed);
        }

        [Fact]
        public async Task Cancelling_closes_without_completing_setup_or_refreshing()
        {
            var h = await AtSourceStepAsync();

            h.ViewModel.Cancel();

            Assert.False(h.Host.Settings.SetupCompleted);
            Assert.Equal(0, h.Refreshes);
            Assert.Equal(1, h.Closed);
        }

        [Fact]
        public async Task The_summary_describes_what_was_chosen()
        {
            var h = await AtSourceStepAsync();
            h.ViewModel.SteamIdText = SteamId;
            h.ViewModel.Next();
            h.ViewModel.Next();

            Assert.Equal(SetupStep.Done, h.ViewModel.Step);
            Assert.Contains("API key: saved", h.ViewModel.SummaryLines);
            Assert.Contains("Region: United States ($)", h.ViewModel.SummaryLines);
            Assert.Contains("Wishlist: your Steam wishlist", h.ViewModel.SummaryLines);
            Assert.Contains("Covers: Steam artwork only", h.ViewModel.SummaryLines);
        }

        [Fact]
        public void Steps_are_numbered_for_the_progress_text()
        {
            var h = new Harness();

            Assert.Equal("Step 1 of 4", h.ViewModel.StepText);
        }
    }
}
