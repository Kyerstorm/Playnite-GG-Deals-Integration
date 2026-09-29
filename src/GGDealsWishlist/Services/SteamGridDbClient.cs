using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Matching;

namespace GGDealsWishlist.Services
{
    /// <summary>One SteamGridDB image offered in the cover picker.</summary>
    public sealed class GridCandidate
    {
        public GridCandidate(string url, string thumbUrl)
        {
            Url = url;
            ThumbUrl = thumbUrl;
        }

        /// <summary>Full-size image, stored as the cover when chosen.</summary>
        public string Url { get; }

        /// <summary>Small image shown in the picker.</summary>
        public string ThumbUrl { get; }
    }

    /// <summary>
    /// Finds cover art on SteamGridDB (https://www.steamgriddb.com) for games Steam has no suitable artwork for.
    /// Looks up by Steam app id first, then by title. Definite answers (a URL, or "nothing there") are cached on
    /// disk so each game is looked up once; failures such as a bad key or a network error are not cached.
    /// </summary>
    public sealed class SteamGridDbClient
    {
        private const string BaseUrl = "https://www.steamgriddb.com/api/v2";
        private const string PortraitDimensions = "600x900";
        private const string LandscapeDimensions = "460x215,920x430";
        private static readonly TimeSpan MissLifetime = TimeSpan.FromDays(14);

        private readonly Func<string> apiKey;
        private readonly string cachePath;
        private readonly HttpClient http;
        private readonly SemaphoreSlim slots = new SemaphoreSlim(2, 2);
        private readonly object sync = new object();
        private Dictionary<string, CacheEntry> cache;

        public SteamGridDbClient(Func<string> apiKey, string cachePath, HttpMessageHandler handler = null)
        {
            this.apiKey = apiKey;
            this.cachePath = cachePath;
            http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(15) };
        }

        public bool HasKey => !string.IsNullOrWhiteSpace(apiKey?.Invoke());

        /// <summary>Returns a direct image URL, or null when no key is set, nothing matches or the service is unreachable.</summary>
        public async Task<string> FindGridUrlAsync(bool portrait, long? steamAppId, string title)
        {
            var key = apiKey?.Invoke()?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            var cacheKey = (portrait ? "p" : "l") + "|" + steamAppId?.ToString(CultureInfo.InvariantCulture) + "|" + TitleNormalizer.Normalize(title);
            if (TryGetCached(cacheKey, out var cachedUrl))
            {
                return cachedUrl;
            }

            try
            {
                string url = null;
                await slots.WaitAsync().ConfigureAwait(false);
                try
                {
                    url = PickImageUrl(await GetGridRowsAsync(steamAppId, title, portrait, key).ConfigureAwait(false));
                }
                finally
                {
                    slots.Release();
                }

                Store(cacheKey, url);
                return url;
            }
            catch (Exception e)
            {
                Log.Debug("SteamGridDB lookup failed (" + e.GetType().Name + ")");
                return null;
            }
        }

        /// <summary>
        /// Lists several covers for the picker dialog. Unlike <see cref="FindGridUrlAsync"/> this is not cached and lets
        /// failures (bad key, network) propagate so the dialog can say what went wrong. Empty when no key is set.
        /// </summary>
        public async Task<IReadOnlyList<GridCandidate>> FindGridCandidatesAsync(long? steamAppId, string title, bool portrait = true, int max = 24)
        {
            var key = apiKey?.Invoke()?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                return new GridCandidate[0];
            }

            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                return PickCandidates(await GetGridRowsAsync(steamAppId, title, portrait, key).ConfigureAwait(false), max);
            }
            finally
            {
                slots.Release();
            }
        }

        /// <summary>Rows for the Steam app id when it has any usable art, otherwise for the best title match.</summary>
        private async Task<object[]> GetGridRowsAsync(long? steamAppId, string title, bool portrait, string key)
        {
            var dimensions = portrait ? PortraitDimensions : LandscapeDimensions;
            if (steamAppId.HasValue)
            {
                var rows = await GetDataAsync("/grids/steam/" + steamAppId.Value.ToString(CultureInfo.InvariantCulture) + "?types=static&dimensions=" + dimensions, key).ConfigureAwait(false);
                if (PickImageUrl(rows) != null)
                {
                    return rows;
                }
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                var gameId = PickGameId(await GetDataAsync("/search/autocomplete/" + Uri.EscapeDataString(title.Trim()), key).ConfigureAwait(false), title);
                if (gameId != null)
                {
                    return await GetDataAsync("/grids/game/" + gameId + "?types=static&dimensions=" + dimensions, key).ConfigureAwait(false);
                }
            }

            return new object[0];
        }

        internal static List<GridCandidate> PickCandidates(object[] data, int max)
        {
            var result = new List<GridCandidate>();
            foreach (var row in data ?? new object[0])
            {
                if (result.Count >= max)
                {
                    break;
                }

                var obj = Json.AsObject(row);
                var url = PickImageUrl(new object[] { obj });
                if (url == null)
                {
                    continue;
                }

                // The thumbnail is much smaller, but only used when WPF can decode it; otherwise show the full image.
                var thumb = PickImageUrl(new object[] { new Dictionary<string, object> { ["url"] = Json.GetText(obj, "thumb") } });
                result.Add(new GridCandidate(url, thumb ?? url));
            }

            return result;
        }

        /// <summary>Static art only, and only formats WPF can decode (SteamGridDB also serves webp).</summary>
        internal static string PickImageUrl(object[] data)
        {
            if (data == null)
            {
                return null;
            }

            foreach (var row in data)
            {
                var url = Json.GetText(Json.AsObject(row), "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var path = url.Split('?')[0];
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }

            return null;
        }

        /// <summary>Accepts an exact (normalised) title, or one that contains the other; never blindly takes the first hit.</summary>
        internal static string PickGameId(object[] data, string title)
        {
            var wanted = Squash(title);
            if (data == null || string.IsNullOrEmpty(wanted))
            {
                return null;
            }

            string loose = null;
            foreach (var row in data)
            {
                var obj = Json.AsObject(row);
                var id = Json.GetText(obj, "id");
                var name = Squash(Json.GetText(obj, "name"));
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (name == wanted)
                {
                    return id;
                }

                if (loose == null && (name.Contains(wanted) || wanted.Contains(name)))
                {
                    loose = id;
                }
            }

            return loose;
        }

        /// <summary>Normalised title without spaces, so "SaveSync" and "Save Sync" compare equal.</summary>
        private static string Squash(string title) => (TitleNormalizer.Normalize(title) ?? string.Empty).Replace(" ", string.Empty);

        private async Task<object[]> GetDataAsync(string path, string key)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using (var response = await http.SendAsync(request).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return new object[0];
                    }

                    response.EnsureSuccessStatusCode();
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var root = Json.AsObject(Json.Parse(body));
                    return Json.Get(root, "data") as object[] ?? new object[0];
                }
            }
        }

        private bool TryGetCached(string cacheKey, out string url)
        {
            lock (sync)
            {
                EnsureLoaded();
                url = null;
                if (!cache.TryGetValue(cacheKey, out var entry))
                {
                    return false;
                }

                if (string.IsNullOrEmpty(entry.Url))
                {
                    // A cached miss is retried after a while: SteamGridDB gains artwork over time.
                    return DateTime.UtcNow - new DateTime(entry.CheckedUtcTicks, DateTimeKind.Utc) <= MissLifetime;
                }

                url = entry.Url;
                return true;
            }
        }

        private void Store(string cacheKey, string url)
        {
            lock (sync)
            {
                EnsureLoaded();
                cache[cacheKey] = new CacheEntry { Url = url ?? string.Empty, CheckedUtcTicks = DateTime.UtcNow.Ticks };
                try
                {
                    if (!string.IsNullOrEmpty(cachePath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                        File.WriteAllText(cachePath, Json.Serialize(cache));
                    }
                }
                catch (Exception e)
                {
                    Log.Debug("SteamGridDB cache could not be saved (" + e.GetType().Name + ")");
                }
            }
        }

        private void EnsureLoaded()
        {
            if (cache != null)
            {
                return;
            }

            try
            {
                cache = !string.IsNullOrEmpty(cachePath) && File.Exists(cachePath)
                    ? Json.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(cachePath))
                    : null;
            }
            catch (Exception)
            {
                cache = null;
            }

            cache = cache ?? new Dictionary<string, CacheEntry>();
        }

        public sealed class CacheEntry
        {
            public string Url { get; set; }

            public long CheckedUtcTicks { get; set; }
        }
    }
}
