using System;
using GGDealsWishlist.Models;
using GGDealsWishlist.Settings;
using GGDealsWishlist.ViewModels;
using Xunit;

namespace GGDealsWishlist.Tests
{
    public sealed class DisplayLayoutTests
    {
        private static DisplayOptions Display(double width, ViewMode mode)
        {
            var display = new DisplayOptions();
            display.Update(new GGDealsSettings(), width);
            display.SetLayout(mode);
            return display;
        }

        [Theory]
        [InlineData(320, 1)]
        [InlineData(899, 1)]
        [InlineData(900, 2)]
        [InlineData(1000, 2)]
        [InlineData(1400, 3)]
        [InlineData(1800, 4)]
        [InlineData(4000, 6)]
        public void Cover_cards_gain_columns_as_the_sidebar_widens(double width, int columns)
        {
            var display = Display(width, ViewMode.CoverInfo);

            Assert.Equal(columns, display.ColumnCount);
            Assert.Equal(columns > 1, display.IsWrapped);
        }

        [Theory]
        [InlineData(250, 1)]
        [InlineData(360, 2)]
        [InlineData(680, 3)]
        [InlineData(1800, 8)]
        [InlineData(6000, 8)]
        public void Grid_columns_follow_the_width_and_are_capped(double width, int columns)
        {
            var display = Display(width, ViewMode.Grid);

            Assert.Equal(columns, display.ColumnCount);
            Assert.True(display.IsWrapped);
        }

        [Theory]
        [InlineData(ViewMode.Compact)]
        [InlineData(ViewMode.List)]
        public void The_compact_modes_never_wrap(ViewMode mode)
        {
            var display = Display(1800, mode);

            Assert.False(display.IsWrapped);
            Assert.Equal(1, display.ColumnCount);
        }

        [Theory]
        [InlineData(1000, ViewMode.CoverInfo)]
        [InlineData(1800, ViewMode.CoverInfo)]
        [InlineData(360, ViewMode.Grid)]
        [InlineData(1800, ViewMode.Grid)]
        public void All_columns_fit_inside_the_list(double width, ViewMode mode)
        {
            var display = Display(width, mode);

            Assert.True(display.SlotWidth * display.ColumnCount <= display.ListContentWidth);
            Assert.True(display.SlotWidth > 0);
        }

        [Fact]
        public void An_item_takes_its_column_width_or_a_whole_row_when_expanded()
        {
            var display = Display(1800, ViewMode.CoverInfo);
            var item = new WishlistItemViewModel(new WishlistItem { Key = "k", Title = "Game" }, display, null);

            Assert.Equal(display.SlotWidth, item.ItemWidth);

            item.IsExpanded = true;

            Assert.Equal(display.ListContentWidth, item.ItemWidth);
        }

        [Fact]
        public void Stacked_layouts_leave_the_item_width_automatic()
        {
            var display = Display(340, ViewMode.CoverInfo);
            var item = new WishlistItemViewModel(new WishlistItem { Key = "k", Title = "Game" }, display, null);

            Assert.True(double.IsNaN(item.ItemWidth));

            item.IsExpanded = true;

            Assert.True(double.IsNaN(item.ItemWidth));
        }

        [Fact]
        public void Wrapped_cards_get_side_margins_and_stacked_cards_do_not()
        {
            Assert.Equal(4, Display(1800, ViewMode.CoverInfo).CardMargin.Left);
            Assert.Equal(0, Display(340, ViewMode.CoverInfo).CardMargin.Left);
        }
    }
}
