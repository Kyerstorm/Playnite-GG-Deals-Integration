using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GGDealsWishlist.Settings;
using GGDealsWishlist.ViewModels;

namespace GGDealsWishlist.Views
{
    /// <summary>The first-run setup dialog. Only the PasswordBox needs code-behind, because it cannot be bound.</summary>
    public partial class SetupWizardView : UserControl
    {
        private readonly SetupWizardViewModel viewModel;

        public SetupWizardView(SetupWizardViewModel viewModel, ThemeMode theme)
        {
            this.viewModel = viewModel;
            InitializeComponent();
            DataContext = viewModel;
            ThemeManager.Apply(this, theme);
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Unloaded += (s, e) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        private void OnKeyChanged(object sender, RoutedEventArgs e)
        {
            viewModel.ApiKey = KeyBox.Password;
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // The box is emptied once the key was accepted and stored.
            if (e.PropertyName == nameof(SetupWizardViewModel.ApiKey) && string.IsNullOrEmpty(viewModel.ApiKey) && KeyBox.Password.Length > 0)
            {
                KeyBox.Clear();
            }
        }
    }
}
