using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Matching;
using GGDealsWishlist.Settings;
using GGDealsWishlist.ViewModels;

namespace GGDealsWishlist.Views
{
    /// <summary>
    /// Native WPF sidebar view. The code-behind only covers interactions that are awkward in pure XAML
    /// (dynamic context menus, PasswordBox, click-vs-button disambiguation); all logic lives in the view model.
    /// </summary>
    public partial class SidebarView : UserControl
    {
        private readonly SidebarViewModel viewModel;

        public SidebarView(SidebarViewModel viewModel, ThemeMode theme)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = viewModel;
            ApplyTheme(theme);

            SizeChanged += (s, e) => viewModel.AvailableWidth = e.NewSize.Width;
            viewModel.QueryChanged += OnQueryChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Unloaded += (s, e) => viewModel.IsFilterPanelOpen = false;
        }

        public void ApplyTheme(ThemeMode theme) => ThemeManager.Apply(this, theme);

        public void Detach()
        {
            viewModel.QueryChanged -= OnQueryChanged;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // The PasswordBox cannot be bound; clear it after the key was accepted.
            if (e.PropertyName == nameof(SidebarViewModel.SetupApiKey) && string.IsNullOrEmpty(viewModel.SetupApiKey) && SetupKeyBox.Password.Length > 0)
            {
                SetupKeyBox.Clear();
            }
        }

        private void OnQueryChanged(object sender, EventArgs e)
        {
            try
            {
                if (WishlistList.Items.Count > 0)
                {
                    WishlistList.ScrollIntoView(WishlistList.Items[0]);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Could not scroll the wishlist to the top");
            }
        }

        private void OnSetupKeyChanged(object sender, RoutedEventArgs e)
        {
            viewModel.SetupApiKey = SetupKeyBox.Password;
        }

        // =========================================================================================
        // Item interaction
        // =========================================================================================

        private void OnItemMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is ListBoxItem container) || !(container.DataContext is WishlistItemViewModel item))
            {
                return;
            }

            // Modifier clicks are for multi-selection; clicks on inner buttons (the star) run their own command.
            if (Keyboard.Modifiers != ModifierKeys.None || IsInsideButton(e.OriginalSource as DependencyObject, container))
            {
                return;
            }

            viewModel.OpenDetails(item);
        }

        private void OnItemRightMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem container && !container.IsSelected)
            {
                WishlistList.SelectedItems.Clear();
                container.IsSelected = true;
            }
        }

        private void OnItemKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is ListBoxItem container && container.DataContext is WishlistItemViewModel item)
            {
                viewModel.OpenDetails(item);
                e.Handled = true;
            }
        }

        private static bool IsInsideButton(DependencyObject source, DependencyObject stopAt)
        {
            while (source != null && !ReferenceEquals(source, stopAt))
            {
                if (source is ButtonBase)
                {
                    return true;
                }

                source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }

            return false;
        }

        // =========================================================================================
        // Context menu
        // =========================================================================================

        private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var selected = WishlistList.SelectedItems.OfType<WishlistItemViewModel>().ToList();
            if (selected.Count == 0 || WishlistList.ContextMenu == null)
            {
                e.Handled = true;
                return;
            }

            var menu = WishlistList.ContextMenu;
            menu.Items.Clear();
            BuildItemMenu(menu.Items, selected);
        }

        private void OnDetailCollectionsClick(object sender, RoutedEventArgs e)
        {
            var item = viewModel.SelectedDetail;
            if (item == null || !(sender is FrameworkElement anchor))
            {
                return;
            }

            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            AddCollectionItems(menu.Items, new List<WishlistItemViewModel> { item });
            menu.IsOpen = true;
        }

        private void BuildItemMenu(ItemCollection items, IReadOnlyList<WishlistItemViewModel> selected)
        {
            var single = selected.Count == 1 ? selected[0] : null;

            if (single != null)
            {
                items.Add(CreateMenuItem("Open GG.deals", () => viewModel.OpenUrl(single.GGDealsUrl), single.HasGGDealsUrl));
                items.Add(CreateMenuItem("Open Cheapest Store", () => viewModel.OpenUrl(single.StoreUrl), single.CanOpenStore));
                if (single.CanOpenInPlaynite)
                {
                    items.Add(CreateMenuItem("Open in Playnite", () => viewModel.OpenInPlaynite(single)));
                }

                items.Add(new Separator());
            }

            var allFavourite = selected.All(i => i.IsFavourite);
            items.Add(CreateMenuItem(allFavourite ? "☆ Remove from Favourites" : "★ Add to Favourites", () => viewModel.SetFavourite(selected, !allFavourite)));

            var collectionMenu = new MenuItem { Header = "Collection" };
            AddCollectionItems(collectionMenu.Items, selected);
            items.Add(collectionMenu);

            var memberOf = viewModel.Collections.Where(c => selected.Any(i => i.Item.CollectionIds.Contains(c.Id))).ToList();
            if (memberOf.Count > 0)
            {
                var removeMenu = new MenuItem { Header = "Remove from Collection" };
                foreach (var collection in memberOf)
                {
                    var id = collection.Id;
                    removeMenu.Items.Add(CreateMenuItem(CollectionLabel(collection.Name, collection.Icon), () => viewModel.SetCollection(selected, id, false)));
                }

                items.Add(removeMenu);
            }

            items.Add(new Separator());
            if (single != null)
            {
                items.Add(CreateMenuItem("Copy GG.deals Link", () => viewModel.CopyLink(single), single.HasGGDealsUrl));
                if (single.IsPotentialMatch)
                {
                    items.Add(new Separator());
                    items.Add(CreateMenuItem("Mark as Owned (" + single.LibraryGameName + ")", () => viewModel.ConfirmMatch(single)));
                    items.Add(CreateMenuItem("Not the Same Game", () => viewModel.SetMatch(single, PlayniteMatcher.RejectedOverride)));
                }
                else if (single.HasMatchOverride)
                {
                    items.Add(new Separator());
                    items.Add(CreateMenuItem("Reset Ownership Decision", () => viewModel.SetMatch(single, null)));
                }
            }
            else
            {
                var links = selected.Where(i => i.HasGGDealsUrl).Select(i => i.GGDealsUrl).ToList();
                items.Add(CreateMenuItem("Copy GG.deals Links (" + links.Count + ")", () => CopyText(string.Join(Environment.NewLine, links)), links.Count > 0));
            }
        }

        private void AddCollectionItems(ItemCollection items, IReadOnlyList<WishlistItemViewModel> selected)
        {
            foreach (var collection in viewModel.Collections)
            {
                var id = collection.Id;
                var count = selected.Count(i => i.Item.CollectionIds.Contains(id));
                var menuItem = new MenuItem
                {
                    Header = CollectionLabel(collection.Name, collection.Icon),
                    IsCheckable = true,
                    IsChecked = count == selected.Count
                };

                // Mixed or empty selection: a click adds every selected game; a full selection removes them.
                var add = count != selected.Count;
                menuItem.Click += (s, e) => viewModel.SetCollection(selected, id, add);
                items.Add(menuItem);
            }

            if (viewModel.Collections.Count > 0)
            {
                items.Add(new Separator());
            }

            items.Add(CreateMenuItem("New collection…", () => viewModel.CreateCollectionFor(selected.ToList())));
            items.Add(CreateMenuItem("Manage collections…", () => viewModel.ManageCollectionsCommand.Execute(null)));
        }

        private static string CollectionLabel(string name, string icon) => string.IsNullOrEmpty(icon) ? name : icon + "  " + name;

        private static MenuItem CreateMenuItem(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (s, e) =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Context menu action failed");
                }
            };
            return item;
        }

        private static void CopyText(string text)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Clipboard is unavailable");
            }
        }
    }
}
