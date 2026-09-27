using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Settings;

namespace GGDealsWishlist.Views
{
    /// <summary>bool / non-empty string / non-null → Visible. Set Invert to flip.</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return IsTruthy(value) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

        internal static bool IsTruthy(object value)
        {
            switch (value)
            {
                case null:
                    return false;
                case bool b:
                    return b;
                case string s:
                    return !string.IsNullOrWhiteSpace(s);
                case int i:
                    return i != 0;
                default:
                    return true;
            }
        }
    }

    /// <summary>Visible only when every bound value is truthy.</summary>
    public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return values.All(BoolToVisibilityConverter.IsTruthy) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
    }

    /// <summary>value.ToString() == parameter. ConvertBack turns a checked radio button into the enum value.</summary>
    public sealed class EqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var equal = value != null && parameter != null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
            if (targetType == typeof(Visibility))
            {
                return equal ? Visibility.Visible : Visibility.Collapsed;
            }

            return equal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null)
            {
                var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
                return type.IsEnum ? Enum.Parse(type, parameter.ToString(), true) : parameter;
            }

            return Binding.DoNothing;
        }
    }

    /// <summary>Visible when value.ToString() is one of the comma-separated parameter values.</summary>
    public sealed class InSetToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var options = (parameter as string ?? string.Empty).Split(',').Select(s => s.Trim());
            return value != null && options.Contains(value.ToString(), StringComparer.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>
    /// Lazily loads cover art off the UI thread with decode-size limiting, an in-memory LRU cache and an optional
    /// on-disk cache for remote images. Failures leave the placeholder visible - a missing cover never breaks a card.
    /// </summary>
    public static class CoverImage
    {
        private const int MemoryCacheCapacity = 400;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, ImageSource>>> CacheIndex = new Dictionary<string, LinkedListNode<KeyValuePair<string, ImageSource>>>();
        private static readonly LinkedList<KeyValuePair<string, ImageSource>> CacheOrder = new LinkedList<KeyValuePair<string, ImageSource>>();
        private static readonly HashSet<string> Failed = new HashSet<string>();
        private static readonly SemaphoreSlim DownloadSlots = new SemaphoreSlim(4, 4);
        private static readonly Lazy<HttpClient> Http = new Lazy<HttpClient>(() => new HttpClient { Timeout = TimeSpan.FromSeconds(20) });

        public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
            "Source", typeof(string), typeof(CoverImage), new PropertyMetadata(null, OnSourceChanged));

        public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
            "DecodeWidth", typeof(int), typeof(CoverImage), new PropertyMetadata(240));

        /// <summary>Folder for downloaded remote artwork. Remote images are not cached on disk when null.</summary>
        public static string DiskCacheDirectory { get; set; }

        public static string GetSource(DependencyObject obj) => (string)obj.GetValue(SourceProperty);

        public static void SetSource(DependencyObject obj, string value) => obj.SetValue(SourceProperty, value);

        public static int GetDecodeWidth(DependencyObject obj) => (int)obj.GetValue(DecodeWidthProperty);

        public static void SetDecodeWidth(DependencyObject obj, int value) => obj.SetValue(DecodeWidthProperty, value);

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is Image image))
            {
                return;
            }

            var source = e.NewValue as string;
            image.Tag = source;
            image.Source = null;
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            var width = Math.Max(32, GetDecodeWidth(image));
            var key = source + "|" + width;
            var cached = TryGetCached(key);
            if (cached != null)
            {
                image.Source = cached;
                return;
            }

            lock (Sync)
            {
                if (Failed.Contains(source))
                {
                    return;
                }
            }

            Task.Run(async () =>
            {
                var bitmap = await LoadAsync(source, width).ConfigureAwait(false);
                if (bitmap == null)
                {
                    return;
                }

                AddCached(key, bitmap);
                _ = image.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Containers are recycled while scrolling: only apply if the image still wants this source.
                    if (Equals(image.Tag, source))
                    {
                        image.Source = bitmap;
                    }
                }));
            });
        }

        private static async Task<ImageSource> LoadAsync(string source, int width)
        {
            try
            {
                byte[] bytes;
                if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    bytes = await LoadRemoteAsync(source).ConfigureAwait(false);
                }
                else
                {
                    bytes = File.Exists(source) ? File.ReadAllBytes(source) : null;
                }

                if (bytes == null || bytes.Length == 0)
                {
                    MarkFailed(source);
                    return null;
                }

                using (var stream = new MemoryStream(bytes))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bitmap.DecodePixelWidth = width;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch (Exception e)
            {
                Log.Debug("Cover could not be loaded (" + e.GetType().Name + ")");
                MarkFailed(source);
                return null;
            }
        }

        private static async Task<byte[]> LoadRemoteAsync(string url)
        {
            string diskPath = null;
            if (!string.IsNullOrEmpty(DiskCacheDirectory))
            {
                diskPath = Path.Combine(DiskCacheDirectory, Hash(url) + ".img");
                if (File.Exists(diskPath))
                {
                    return File.ReadAllBytes(diskPath);
                }
            }

            await DownloadSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var response = await Http.Value.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (diskPath != null)
                    {
                        Directory.CreateDirectory(DiskCacheDirectory);
                        File.WriteAllBytes(diskPath, bytes);
                    }

                    return bytes;
                }
            }
            finally
            {
                DownloadSlots.Release();
            }
        }

        public static void ClearMemoryCache()
        {
            lock (Sync)
            {
                CacheIndex.Clear();
                CacheOrder.Clear();
                Failed.Clear();
            }
        }

        private static ImageSource TryGetCached(string key)
        {
            lock (Sync)
            {
                if (!CacheIndex.TryGetValue(key, out var node))
                {
                    return null;
                }

                CacheOrder.Remove(node);
                CacheOrder.AddFirst(node);
                return node.Value.Value;
            }
        }

        private static void AddCached(string key, ImageSource image)
        {
            lock (Sync)
            {
                if (CacheIndex.ContainsKey(key))
                {
                    return;
                }

                CacheIndex[key] = CacheOrder.AddFirst(new KeyValuePair<string, ImageSource>(key, image));
                while (CacheOrder.Count > MemoryCacheCapacity)
                {
                    var last = CacheOrder.Last;
                    CacheOrder.RemoveLast();
                    CacheIndex.Remove(last.Value.Key);
                }
            }
        }

        private static void MarkFailed(string source)
        {
            lock (Sync)
            {
                Failed.Add(source);
            }
        }

        private static string Hash(string text)
        {
            using (var sha = SHA1.Create())
            {
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }
    }

    /// <summary>
    /// Maps the extension's semantic brush keys (GG.*) either onto the active Playnite theme (Inherit) or onto a
    /// built-in light/dark palette. Views reference only the GG.* keys via DynamicResource.
    /// </summary>
    public static class ThemeManager
    {
        private static readonly Color DarkText = Color.FromRgb(0xE8, 0xE9, 0xED);
        private static readonly Color LightText = Color.FromRgb(0x1F, 0x23, 0x28);

        public static void Apply(FrameworkElement root, ThemeMode mode)
        {
            try
            {
                Color text;
                Color muted;
                Brush background;
                Brush popup;
                switch (mode)
                {
                    case ThemeMode.Light:
                        text = LightText;
                        muted = Color.FromRgb(0x5E, 0x65, 0x70);
                        background = Solid(Color.FromRgb(0xF4, 0xF5, 0xF7));
                        popup = Solid(Colors.White);
                        break;
                    case ThemeMode.Dark:
                        text = DarkText;
                        muted = Color.FromRgb(0xA0, 0xA4, 0xAE);
                        background = Solid(Color.FromRgb(0x1B, 0x1D, 0x24));
                        popup = Solid(Color.FromRgb(0x26, 0x29, 0x32));
                        break;
                    default:
                        text = ColorOf(root.TryFindResource("TextBrush")) ?? DarkText;
                        muted = ColorOf(root.TryFindResource("TextBrushDarker")) ?? WithAlpha(text, 0xB0);
                        background = Brushes.Transparent;
                        popup = root.TryFindResource("PopupBackgroundBrush") as Brush
                            ?? root.TryFindResource("WindowBackgourndBrush") as Brush
                            ?? Solid(IsLight(text) ? Color.FromRgb(0x26, 0x29, 0x32) : Colors.White);
                        break;
                }

                var resources = root.Resources;
                resources["GG.Text"] = Solid(text);
                resources["GG.TextMuted"] = Solid(muted);
                resources["GG.Background"] = background;
                resources["GG.Popup"] = popup;
                resources["GG.Card"] = Solid(WithAlpha(text, 0x10));
                resources["GG.CardHover"] = Solid(WithAlpha(text, 0x1E));
                resources["GG.Placeholder"] = Solid(WithAlpha(text, 0x16));
                resources["GG.Border"] = Solid(WithAlpha(text, 0x2A));
                resources["GG.Warning"] = Solid(Color.FromRgb(0xF2, 0xB1, 0x34));
                resources["GG.WarningBackground"] = Solid(Color.FromArgb(0x26, 0xF2, 0xB1, 0x34));
                resources["GG.Error"] = Solid(Color.FromRgb(0xE5, 0x53, 0x4B));
                resources["GG.Success"] = Solid(Color.FromRgb(0x3F, 0xB9, 0x50));
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to apply theme");
            }
        }

        private static bool IsLight(Color color) => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255 > 0.5;

        private static Color? ColorOf(object resource)
        {
            if (resource is SolidColorBrush brush)
            {
                return brush.Color;
            }

            if (resource is Color color)
            {
                return color;
            }

            return null;
        }

        private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

        private static Brush Solid(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
