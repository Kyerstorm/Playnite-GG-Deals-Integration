using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Providers;
using GGDealsWishlist.ViewModels;
using RelayCommand = GGDealsWishlist.Infrastructure.RelayCommand;

namespace GGDealsWishlist.Settings
{
    /// <summary>One editable line of "Other people's wishlists" on the settings page.</summary>
    public sealed class SavedWishlistRow : ObservableBase
    {
        private readonly Func<string, Task<WishlistFetchResult>> checkSteam;
        private string name;
        private string steamId;
        private string checkStatus;
        private bool checkSucceeded;
        private bool isChecking;

        public SavedWishlistRow(SavedWishlist source, Func<string, Task<WishlistFetchResult>> checkSteam, Action<SavedWishlistRow> remove)
        {
            this.checkSteam = checkSteam;
            Id = string.IsNullOrWhiteSpace(source?.Id) ? Guid.NewGuid().ToString("N") : source.Id;
            name = source?.Name ?? string.Empty;
            steamId = source?.SteamId ?? string.Empty;
            CheckCommand = new AsyncRelayCommand(CheckAsync, () => this.checkSteam != null && !isChecking && SteamIdParser.TryParse(steamId, out _));
            RemoveCommand = new RelayCommand(() => remove?.Invoke(this));
        }

        /// <summary>Stable scope id; everything stored for this wishlist is keyed by it.</summary>
        public string Id { get; }

        public string Name
        {
            get => name;
            set => SetValue(ref name, value ?? string.Empty);
        }

        public string SteamId
        {
            get => steamId;
            set
            {
                if (SetValue(ref steamId, value ?? string.Empty))
                {
                    CheckStatus = null;
                    OnPropertyChanged(nameof(ValidationText));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>Why the SteamID cannot be used; null when it is fine.</summary>
        public string ValidationText => string.IsNullOrWhiteSpace(steamId)
            ? "Enter this person's SteamID64."
            : SteamIdParser.Describe(steamId);

        public string CheckStatus
        {
            get => checkStatus;
            private set => SetValue(ref checkStatus, value);
        }

        public bool CheckSucceeded
        {
            get => checkSucceeded;
            private set => SetValue(ref checkSucceeded, value);
        }

        public ICommand CheckCommand { get; }

        public ICommand RemoveCommand { get; }

        public SavedWishlist ToSaved() => new SavedWishlist { Id = Id, Name = (name ?? string.Empty).Trim(), SteamId = (steamId ?? string.Empty).Trim() };

        /// <summary>Label used in messages: the name when there is one, otherwise the SteamID.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(name) ? (steamId ?? string.Empty).Trim() : name.Trim();

        private async Task CheckAsync()
        {
            if (!SteamIdParser.TryParse(steamId, out var id))
            {
                return;
            }

            isChecking = true;
            CheckSucceeded = false;
            CheckStatus = "Checking…";
            CommandManager.InvalidateRequerySuggested();
            try
            {
                var result = SteamCheckResult.From(await checkSteam(id.ToString(CultureInfo.InvariantCulture)), DisplayName);
                CheckSucceeded = result.IsOk;
                CheckStatus = result.Message;
            }
            catch (Exception e)
            {
                Log.Error(e, "Checking another person's Steam wishlist failed");
                CheckStatus = SteamCheckResult.From(null, DisplayName).Message;
            }
            finally
            {
                isChecking = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
