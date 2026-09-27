using System;
using System.Windows;
using System.Windows.Controls;
using GGDealsWishlist.Views;

namespace GGDealsWishlist.Settings
{
    public partial class GGDealsSettingsView : UserControl
    {
        private readonly GGDealsSettingsViewModel viewModel;
        private bool clearing;

        public GGDealsSettingsView(GGDealsSettingsViewModel viewModel)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = viewModel;

            // The settings page always follows the Playnite theme so it matches the surrounding window.
            ThemeManager.Apply(this, ThemeMode.Inherit);
            Loaded += (s, e) => viewModel.PendingKeyCleared += OnPendingKeyCleared;
            Unloaded += (s, e) => viewModel.PendingKeyCleared -= OnPendingKeyCleared;
        }

        private void OnApiKeyChanged(object sender, RoutedEventArgs e)
        {
            if (!clearing)
            {
                viewModel.PendingApiKey = ApiKeyBox.Password;
            }
        }

        private void OnPendingKeyCleared(object sender, EventArgs e)
        {
            if (ApiKeyBox.Password.Length == 0)
            {
                return;
            }

            clearing = true;
            try
            {
                ApiKeyBox.Clear();
            }
            finally
            {
                clearing = false;
            }
        }
    }
}
