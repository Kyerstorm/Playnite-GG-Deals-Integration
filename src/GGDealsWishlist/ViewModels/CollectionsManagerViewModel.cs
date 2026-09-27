using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Services;

namespace GGDealsWishlist.ViewModels
{
    public sealed class CollectionRowViewModel : ObservableBase
    {
        private string name;
        private string icon;

        public CollectionRowViewModel(string id, string name, string icon, int count)
        {
            Id = id;
            this.name = name;
            this.icon = icon;
            Count = count;
        }

        public string Id { get; }

        public string Name
        {
            get => name;
            set => SetValue(ref name, value);
        }

        public string Icon
        {
            get => icon;
            set => SetValue(ref icon, value);
        }

        public int Count { get; }

        public string CountText => Count == 1 ? "1 game" : Count + " games";
    }

    /// <summary>
    /// Create / rename / delete local wishlist collections. Collections are stored only in the extension's
    /// local state file; they never modify the GG.deals wishlist or Playnite's own categories.
    /// </summary>
    public sealed class CollectionsManagerViewModel : ObservableBase, IDisposable
    {
        public static readonly string[] SuggestedIcons = { "", "⭐", "🔥", "🎮", "💰", "⏳", "🧩", "⚔", "🏎", "👻", "❤", "📌" };

        private readonly WishlistDataService service;
        private readonly IHostServices host;
        private CollectionRowViewModel selected;
        private string newName;
        private string newIcon = string.Empty;
        private string errorText;

        public CollectionsManagerViewModel(WishlistDataService service, IHostServices host)
        {
            this.service = service;
            this.host = host;
            AddCommand = new RelayCommand(Add, () => !string.IsNullOrWhiteSpace(NewName));
            // The row parameter can still be unresolved when WPF first queries CanExecute inside a template,
            // so only a known-empty name disables Save; both actions ignore a missing row.
            SaveCommand = new RelayCommand(p => Save(p as CollectionRowViewModel), p => !(p is CollectionRowViewModel row) || !string.IsNullOrWhiteSpace(row.Name));
            DeleteCommand = new RelayCommand(p => Delete(p as CollectionRowViewModel));
            service.DataChanged += OnDataChanged;
            Reload();
        }

        public ObservableCollection<CollectionRowViewModel> Rows { get; } = new ObservableCollection<CollectionRowViewModel>();

        public string[] Icons => SuggestedIcons;

        public bool IsEmpty => Rows.Count == 0;

        public CollectionRowViewModel Selected
        {
            get => selected;
            set => SetValue(ref selected, value);
        }

        public string NewName
        {
            get => newName;
            set
            {
                if (SetValue(ref newName, value))
                {
                    ErrorText = null;
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string NewIcon
        {
            get => newIcon;
            set => SetValue(ref newIcon, value ?? string.Empty);
        }

        public string ErrorText
        {
            get => errorText;
            private set => SetValue(ref errorText, value);
        }

        public ICommand AddCommand { get; }

        public ICommand SaveCommand { get; }

        public ICommand DeleteCommand { get; }

        private void Add()
        {
            var name = NewName?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            if (Rows.Any(r => string.Equals(r.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                ErrorText = "A collection with this name already exists.";
                return;
            }

            service.CreateCollection(name, string.IsNullOrEmpty(NewIcon) ? null : NewIcon);
            NewName = string.Empty;
            NewIcon = string.Empty;
            Reload();
        }

        private void Save(CollectionRowViewModel row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Name))
            {
                return;
            }

            if (Rows.Any(r => r != row && string.Equals(r.Name.Trim(), row.Name.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            {
                ErrorText = "A collection with this name already exists.";
                return;
            }

            ErrorText = null;
            service.RenameCollection(row.Id, row.Name.Trim(), string.IsNullOrEmpty(row.Icon) ? null : row.Icon);
        }

        private void Delete(CollectionRowViewModel row)
        {
            if (row == null)
            {
                return;
            }

            var message = row.Count > 0
                ? "Delete the collection \"" + row.Name + "\"? " + row.CountText + " will be removed from it. The games stay on your wishlist."
                : "Delete the collection \"" + row.Name + "\"?";
            if (host.Confirm(message, "Delete collection"))
            {
                service.DeleteCollection(row.Id);
                Reload();
            }
        }

        private void OnDataChanged(object sender, EventArgs e) => host.Dispatcher.BeginInvoke(new Action(Reload), DispatcherPriority.Background);

        private void Reload()
        {
            var selectedId = selected?.Id;
            Rows.Clear();
            foreach (var collection in service.Collections)
            {
                Rows.Add(new CollectionRowViewModel(collection.Id, collection.Name, collection.Icon ?? string.Empty, service.CountInCollection(collection.Id)));
            }

            Selected = Rows.FirstOrDefault(r => r.Id == selectedId);
            OnPropertyChanged(nameof(IsEmpty));
        }

        public void Dispose() => service.DataChanged -= OnDataChanged;
    }
}
