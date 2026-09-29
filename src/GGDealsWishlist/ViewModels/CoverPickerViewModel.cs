using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Services;

namespace GGDealsWishlist.ViewModels
{
    /// <summary>
    /// Backs the "Choose from SteamGridDB" dialog: loads candidate covers for one game and reports the one the
    /// user clicks. The dialog closes itself when <see cref="CloseRequested"/> fires.
    /// </summary>
    public sealed class CoverPickerViewModel : ObservableBase
    {
        private readonly SteamGridDbClient client;
        private readonly long? steamAppId;
        private readonly string title;
        private bool isLoading;
        private string statusText;

        public CoverPickerViewModel(SteamGridDbClient client, long? steamAppId, string title)
        {
            this.client = client;
            this.steamAppId = steamAppId;
            this.title = title;
            ChooseCommand = new RelayCommand(p =>
            {
                if (p is GridCandidate candidate)
                {
                    ChosenUrl = candidate.Url;
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                }
            });
            CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        }

        public event EventHandler CloseRequested;

        public string Heading => "Choose a cover for " + (string.IsNullOrWhiteSpace(title) ? "this game" : title);

        public ObservableCollection<GridCandidate> Candidates { get; } = new ObservableCollection<GridCandidate>();

        public ICommand ChooseCommand { get; }

        public ICommand CancelCommand { get; }

        /// <summary>Full-size URL of the chosen image; null when the dialog was cancelled.</summary>
        public string ChosenUrl { get; private set; }

        public bool IsLoading
        {
            get => isLoading;
            private set => SetValue(ref isLoading, value);
        }

        /// <summary>Progress, empty-result or error message; empty while candidates are shown.</summary>
        public string StatusText
        {
            get => statusText;
            private set => SetValue(ref statusText, value);
        }

        public async Task LoadAsync()
        {
            Candidates.Clear();
            IsLoading = true;
            StatusText = "Loading covers from SteamGridDB…";
            try
            {
                var found = await client.FindGridCandidatesAsync(steamAppId, title).ConfigureAwait(true);
                foreach (var candidate in found)
                {
                    Candidates.Add(candidate);
                }

                StatusText = found.Count > 0
                    ? string.Empty
                    : client.HasKey
                        ? "SteamGridDB has no usable covers for this game."
                        : "Add a SteamGridDB API key in the extension settings to browse covers.";
            }
            catch (Exception e)
            {
                Log.Debug("SteamGridDB picker failed (" + e.GetType().Name + ")");
                StatusText = "Could not load covers from SteamGridDB. Check your API key and internet connection.";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
