using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

    /// <summary>One wishlist's cached entries and refresh bookkeeping. Each wishlist has its own snapshot; they are never merged.</summary>
    public sealed class WishlistSnapshot
    {
        public string ProviderId { get; set; }

        /// <summary>SteamID64 these entries were loaded for (saved wishlists only). If it changes, the entries are discarded.</summary>
        public string SteamId { get; set; }

        public List<WishlistEntry> Entries { get; set; } = new List<WishlistEntry>();

        public DateTime? UpdatedUtc { get; set; }

        public DateTime? AttemptUtc { get; set; }

        /// <summary>Error from this wishlist's last refresh (wishlist or prices); null after a clean refresh.</summary>
        public ErrorRecord LastError { get; set; }

        public void Normalize()
        {
            Entries = Entries ?? new List<WishlistEntry>();
        }
    }

    /// <summary>Persistent cache of provider data (wishlists + prices). Separate refresh timestamps per data type.</summary>
    public sealed class CacheDocument
    {
        /// <summary>Version 2 keys the wishlist data by scope id; prices stay shared because a price does not depend on whose list a game is on.</summary>
        public const int CurrentVersion = 2;

        public int DataVersion { get; set; } = CurrentVersion;

        /// <summary>Scope id ("own" or a saved wishlist id) → that wishlist's snapshot.</summary>
        public Dictionary<string, WishlistSnapshot> Wishlists { get; set; } = new Dictionary<string, WishlistSnapshot>();

        /// <summary>Lookup key ("app:420") → cached price.</summary>
        public Dictionary<string, PriceData> Prices { get; set; } = new Dictionary<string, PriceData>();

        public DateTime? PricesUpdatedUtc { get; set; }

        public DateTime? PricesAttemptUtc { get; set; }

        public RateLimitState RateLimit { get; set; }

        /// <summary>Version 1 → 2: the single wishlist becomes the user's own wishlist snapshot.</summary>
        public static readonly IReadOnlyDictionary<int, Action<IDictionary<string, object>>> Migrations =
            new Dictionary<int, Action<IDictionary<string, object>>>
            {
                {
                    1, root =>
                    {
                        var own = new Dictionary<string, object>();
                        StoreMigration.Move(root, "WishlistProviderId", own, "ProviderId");
                        StoreMigration.Move(root, "WishlistEntries", own, "Entries");
                        StoreMigration.Move(root, "WishlistUpdatedUtc", own, "UpdatedUtc");
                        StoreMigration.Move(root, "WishlistAttemptUtc", own, "AttemptUtc");
                        StoreMigration.Move(root, "LastError", own, "LastError");
                        root["Wishlists"] = new Dictionary<string, object> { { WishlistScope.OwnId, own } };
                    }
                }
            };

        public void Normalize()
        {
            Prices = Prices ?? new Dictionary<string, PriceData>();
            Wishlists = Wishlists ?? new Dictionary<string, WishlistSnapshot>();
            foreach (var key in Wishlists.Keys.ToList())
            {
                if (Wishlists[key] == null)
                {
                    Wishlists[key] = new WishlistSnapshot();
                }

                Wishlists[key].Normalize();
            }
        }
    }

    internal static class StoreMigration
    {
        /// <summary>Moves a value between loose JSON objects, keeping an explicit null; does nothing when the key is absent.</summary>
        public static void Move(IDictionary<string, object> from, string fromKey, IDictionary<string, object> to, string toKey)
        {
            if (from.TryGetValue(fromKey, out var value))
            {
                to[toKey] = value;
                from.Remove(fromKey);
            }
        }
    }

    public sealed class UiState
    {
        /// <summary>Scope id of the wishlist being viewed; null or unknown means the user's own wishlist.</summary>
        public string ActiveWishlistId { get; set; }

        public ViewMode ViewMode { get; set; } = ViewMode.CoverInfo;

        public bool ViewModeChosen { get; set; }

        public SortMode Sort { get; set; } = SortMode.WishlistOrder;

        public FilterState Filter { get; set; } = new FilterState();
    }

    /// <summary>Favourites and collections of one wishlist. A game starred on one wishlist is not starred on another.</summary>
    public sealed class ScopeLocalState
    {
        public List<string> Favourites { get; set; } = new List<string>();

        public List<WishlistCollection> Collections { get; set; } = new List<WishlistCollection>();

        /// <summary>Wishlist item key → collection ids.</summary>
        public Dictionary<string, List<string>> Memberships { get; set; } = new Dictionary<string, List<string>>();

        public void Normalize()
        {
            Favourites = Favourites ?? new List<string>();
            Collections = Collections ?? new List<WishlistCollection>();
            Memberships = Memberships ?? new Dictionary<string, List<string>>();
        }
    }

    /// <summary>User-owned local organisation data (never sent anywhere).</summary>
    public sealed class LocalStateDocument
    {
        /// <summary>Version 2 keys favourites, collections and memberships by wishlist scope id.</summary>
        public const int CurrentVersion = 2;

        public int DataVersion { get; set; } = CurrentVersion;

        /// <summary>Scope id ("own" or a saved wishlist id) → favourites and collections of that wishlist.</summary>
        public Dictionary<string, ScopeLocalState> Scopes { get; set; } = new Dictionary<string, ScopeLocalState>();

        /// <summary>Version 1 → 2: the existing favourites and collections become the user's own wishlist's.</summary>
        public static readonly IReadOnlyDictionary<int, Action<IDictionary<string, object>>> Migrations =
            new Dictionary<int, Action<IDictionary<string, object>>>
            {
                {
                    1, root =>
                    {
                        var own = new Dictionary<string, object>();
                        StoreMigration.Move(root, "Favourites", own, "Favourites");
                        StoreMigration.Move(root, "Collections", own, "Collections");
                        StoreMigration.Move(root, "Memberships", own, "Memberships");
                        root["Scopes"] = new Dictionary<string, object> { { WishlistScope.OwnId, own } };
                    }
                }
            };

        /// <summary>The state of one wishlist, created on first use.</summary>
        public ScopeLocalState Scope(string scopeId)
        {
            if (!Scopes.TryGetValue(scopeId, out var scope) || scope == null)
            {
                scope = new ScopeLocalState();
                Scopes[scopeId] = scope;
            }

            return scope;
        }

        /// <summary>Wishlist item key → confirmed Playnite game id, or "none" for a rejected match.</summary>
        public Dictionary<string, string> MatchOverrides { get; set; } = new Dictionary<string, string>();

        /// <summary>Wishlist item key → user-chosen cover (local file path or web address); wins over automatic covers.</summary>
        public Dictionary<string, string> CoverOverrides { get; set; } = new Dictionary<string, string>();

        public UiState Ui { get; set; } = new UiState();

        public void Normalize()
        {
            CoverOverrides = CoverOverrides ?? new Dictionary<string, string>();
            Scopes = Scopes ?? new Dictionary<string, ScopeLocalState>();
            foreach (var key in Scopes.Keys.ToList())
            {
                Scopes[key] = Scopes[key] ?? new ScopeLocalState();
                Scopes[key].Normalize();
            }

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
