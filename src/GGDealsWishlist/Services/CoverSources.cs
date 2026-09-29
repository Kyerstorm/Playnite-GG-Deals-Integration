using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GGDealsWishlist.Services
{
    /// <summary>
    /// A cover is described as an ordered, newline-separated list of candidates that the image loader tries in turn:
    /// a file path, an image URL, or a deferred SteamGridDB lookup (<c>steamgriddb:p:{appId}:{title}</c>).
    /// </summary>
    internal static class CoverSources
    {
        private const char Separator = '\n';
        private const string GridPrefix = "steamgriddb:";

        public static string Join(params string[] candidates)
        {
            var list = candidates.Where(c => !string.IsNullOrWhiteSpace(c)).ToArray();
            return list.Length == 0 ? null : string.Join(Separator.ToString(), list);
        }

        public static IReadOnlyList<string> Split(string source)
        {
            return string.IsNullOrWhiteSpace(source)
                ? new string[0]
                : source.Split(new[] { Separator }, StringSplitOptions.RemoveEmptyEntries);
        }

        public static string SteamGridDb(bool portrait, long? steamAppId, string title)
        {
            if (string.IsNullOrWhiteSpace(title) && !steamAppId.HasValue)
            {
                return null;
            }

            var cleanTitle = (title ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ');
            return GridPrefix + (portrait ? "p" : "l") + ":" + steamAppId?.ToString(CultureInfo.InvariantCulture) + ":" + cleanTitle;
        }

        public static bool TryParseSteamGridDb(string candidate, out bool portrait, out long? steamAppId, out string title)
        {
            portrait = true;
            steamAppId = null;
            title = null;
            if (candidate == null || !candidate.StartsWith(GridPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var parts = candidate.Substring(GridPrefix.Length).Split(new[] { ':' }, 3);
            if (parts.Length != 3 || (parts[0] != "p" && parts[0] != "l"))
            {
                return false;
            }

            portrait = parts[0] == "p";
            if (long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                steamAppId = id;
            }

            title = parts[2];
            return true;
        }
    }
}
