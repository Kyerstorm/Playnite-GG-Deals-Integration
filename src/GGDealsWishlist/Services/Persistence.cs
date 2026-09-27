using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GGDealsWishlist.Api;
using GGDealsWishlist.Infrastructure;
using GGDealsWishlist.Models;
using GGDealsWishlist.Querying;

namespace GGDealsWishlist.Services
{
    public sealed class ErrorRecord
    {
        public ApiErrorKind Kind { get; set; }

        public string Message { get; set; }

        public DateTime AtUtc { get; set; }

        public DateTime? RetryAfterUtc { get; set; }

        /// <summary>Name of the failing wishlist source (e.g. "Steam wishlist"); null for GG.deals price errors.</summary>
        public string Source { get; set; }
    }

    /// <summary>Persistent cache of provider data (wishlist + prices). Separate refresh timestamps per data type.</summary>
    public sealed class CacheDocument
    {
        public const int CurrentVersion = 1;

        public int DataVersion { get; set; } = CurrentVersion;

        public string WishlistProviderId { get; set; }

        public List<WishlistEntry> WishlistEntries { get; set; } = new List<WishlistEntry>();

        public DateTime? WishlistUpdatedUtc { get; set; }

        public DateTime? WishlistAttemptUtc { get; set; }

        /// <summary>Lookup key ("app:420") → cached price.</summary>
        public Dictionary<string, PriceData> Prices { get; set; } = new Dictionary<string, PriceData>();

        public DateTime? PricesUpdatedUtc { get; set; }

        public DateTime? PricesAttemptUtc { get; set; }

        public ErrorRecord LastError { get; set; }

        public RateLimitState RateLimit { get; set; }
    }

    public sealed class UiState
    {
        public ViewMode ViewMode { get; set; } = ViewMode.CoverInfo;

        public bool ViewModeChosen { get; set; }

        public SortMode Sort { get; set; } = SortMode.WishlistOrder;

        public FilterState Filter { get; set; } = new FilterState();
    }

    /// <summary>User-owned local organisation data (never sent anywhere).</summary>
    public sealed class LocalStateDocument
    {
        public const int CurrentVersion = 1;

        public int DataVersion { get; set; } = CurrentVersion;

        public List<string> Favourites { get; set; } = new List<string>();

        public List<WishlistCollection> Collections { get; set; } = new List<WishlistCollection>();

        /// <summary>Wishlist item key → collection ids.</summary>
        public Dictionary<string, List<string>> Memberships { get; set; } = new Dictionary<string, List<string>>();

        /// <summary>Wishlist item key → confirmed Playnite game id, or "none" for a rejected match.</summary>
        public Dictionary<string, string> MatchOverrides { get; set; } = new Dictionary<string, string>();

        public UiState Ui { get; set; } = new UiState();

        public void Normalize()
        {
            Favourites = Favourites ?? new List<string>();
            Collections = Collections ?? new List<WishlistCollection>();
            Memberships = Memberships ?? new Dictionary<string, List<string>>();
            MatchOverrides = MatchOverrides ?? new Dictionary<string, string>();
            Ui = Ui ?? new UiState();
            Ui.Filter = Ui.Filter ?? new FilterState();
            Ui.Filter.CollectionIds = Ui.Filter.CollectionIds ?? new List<string>();
        }
    }

    /// <summary>
    /// Versioned JSON file store. Documents carry a DataVersion; older files are migrated step by step,
    /// newer (unknown) files are backed up instead of being misread, and corrupt files are quarantined so a
    /// bad cache can never prevent the extension from starting.
    /// </summary>
    public sealed class VersionedJsonStore<T> where T : class, new()
    {
        private readonly object sync = new object();
        private readonly int currentVersion;
        private readonly IReadOnlyDictionary<int, Action<IDictionary<string, object>>> migrations;

        /// <param name="migrations">Key N migrates a version-N document to version N+1 in place.</param>
        public VersionedJsonStore(string path, int currentVersion, IReadOnlyDictionary<int, Action<IDictionary<string, object>>> migrations = null)
        {
            FilePath = path;
            this.currentVersion = currentVersion;
            this.migrations = migrations ?? new Dictionary<int, Action<IDictionary<string, object>>>();
        }

        public string FilePath { get; }

        public T Load()
        {
            lock (sync)
            {
                try
                {
                    if (!File.Exists(FilePath))
                    {
                        return new T();
                    }

                    var text = File.ReadAllText(FilePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return new T();
                    }

                    var root = Json.AsObject(Json.Parse(text));
                    if (root == null)
                    {
                        Quarantine("not a JSON object");
                        return new T();
                    }

                    var version = Json.GetInt(root, "DataVersion") ?? 1;
                    if (version > currentVersion)
                    {
                        Backup(".v" + version.ToString(CultureInfo.InvariantCulture) + ".bak");
                        Log.Warn(Path.GetFileName(FilePath) + " was written by a newer version (" + version + "); starting fresh.");
                        return new T();
                    }

                    while (version < currentVersion)
                    {
                        if (migrations.TryGetValue(version, out var migrate))
                        {
                            migrate(root);
                        }

                        version++;
                        root["DataVersion"] = version;
                        Log.Info("Migrated " + Path.GetFileName(FilePath) + " to data version " + version);
                    }

                    return Json.ConvertTo<T>(root) ?? new T();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Failed to load " + Path.GetFileName(FilePath));
                    Quarantine("unreadable");
                    return new T();
                }
            }
        }

        public void Save(T document)
        {
            lock (sync)
            {
                try
                {
                    var directory = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var temp = FilePath + ".tmp";
                    File.WriteAllText(temp, Json.Serialize(document), new UTF8Encoding(false));
                    if (File.Exists(FilePath))
                    {
                        File.Replace(temp, FilePath, null);
                    }
                    else
                    {
                        File.Move(temp, FilePath);
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Failed to save " + Path.GetFileName(FilePath));
                }
            }
        }

        public void Delete()
        {
            lock (sync)
            {
                try
                {
                    if (File.Exists(FilePath))
                    {
                        File.Delete(FilePath);
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "Failed to delete " + Path.GetFileName(FilePath));
                }
            }
        }

        private void Quarantine(string reason)
        {
            Log.Warn(Path.GetFileName(FilePath) + " is " + reason + "; it was set aside and a fresh file will be created.");
            Backup(".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        }

        private void Backup(string suffix)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Copy(FilePath, FilePath + suffix, true);
                    File.Delete(FilePath);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to back up " + Path.GetFileName(FilePath));
            }
        }
    }
}
