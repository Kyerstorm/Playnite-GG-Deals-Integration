using GGDealsWishlist.Models;
using GGDealsWishlist.Settings;
using Xunit;

namespace GGDealsWishlist.Tests
{
    public class SettingsTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("short")]
        [InlineData("has space inside-key")]
        [InlineData("tab\tinside-the-key")]
        [InlineData("ünicode-key-value")]
        public void ValidateKey_RejectsMalformedKeys(string key)
        {
            Assert.NotNull(GGDealsSettingsViewModel.ValidateKey(key));
        }

        [Fact]
        public void ValidateKey_AcceptsPlausibleKey()
        {
            Assert.Null(GGDealsSettingsViewModel.ValidateKey("AbC123-def456_ghi789"));
        }

        [Fact]
        public void ValidationMessages_NeverContainTheKey()
        {
            const string key = "secret key with spaces 123";
            Assert.DoesNotContain(key, GGDealsSettingsViewModel.ValidateKey(key));
        }

        [Fact]
        public void Clone_CopiesPersistedValuesIndependently()
        {
            var original = new GGDealsSettings
            {
                ProtectedApiKey = "blob",
                Region = "de",
                RefreshIntervalMinutes = 180,
                DefaultViewMode = ViewMode.List,
                HideOwned = true,
                AccentColor = "#112233"
            };

            var copy = original.Clone();
            copy.Region = "us";

            Assert.Equal("blob", copy.ProtectedApiKey);
            Assert.Equal(180, copy.RefreshIntervalMinutes);
            Assert.Equal(ViewMode.List, copy.DefaultViewMode);
            Assert.True(copy.HideOwned);
            Assert.Equal("#112233", copy.AccentColor);
            Assert.Equal("de", original.Region);
        }

        [Fact]
        public void Normalize_RepairsOutOfRangeValues()
        {
            var settings = new GGDealsSettings { Region = "xx", RefreshIntervalMinutes = 1, AccentColor = " ", GGDealsWishlistUrl = null, ManualWishlist = null };
            settings.Normalize();

            Assert.NotEqual("xx", settings.Region);
            Assert.Equal(15, settings.RefreshIntervalMinutes);
            Assert.Equal(GGDealsSettings.DefaultAccentColor, settings.AccentColor);
            Assert.Equal(GGDealsSettings.DefaultWishlistUrl, settings.GGDealsWishlistUrl);
            Assert.Equal(string.Empty, settings.ManualWishlist);
        }
    }
}
