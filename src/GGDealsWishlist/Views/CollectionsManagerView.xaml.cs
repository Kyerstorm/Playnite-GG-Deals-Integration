using System.Windows.Controls;
using GGDealsWishlist.Settings;
using GGDealsWishlist.ViewModels;

namespace GGDealsWishlist.Views
{
    public partial class CollectionsManagerView : UserControl
    {
        public CollectionsManagerView(CollectionsManagerViewModel viewModel, ThemeMode theme)
        {
            InitializeComponent();
            DataContext = viewModel;
            ThemeManager.Apply(this, theme);
            Unloaded += (s, e) => viewModel.Dispose();
        }
    }
}
