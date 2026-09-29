using System;
using System.Windows.Controls;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Settings;
using GGDealsWishlist.ViewModels;

namespace GGDealsWishlist.Views
{
    public partial class CoverPickerView : UserControl
    {
        public CoverPickerView(CoverPickerViewModel viewModel, ThemeMode theme)
        {
            InitializeComponent();
            DataContext = viewModel;
            ThemeManager.Apply(this, theme);
            Loaded += async (s, e) =>
            {
                if (viewModel.Candidates.Count > 0 || viewModel.IsLoading)
                {
                    return;
                }

                try
                {
                    await viewModel.LoadAsync();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Cover picker failed to load");
                }
            };
        }
    }
}
