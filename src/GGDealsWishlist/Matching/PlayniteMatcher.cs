using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GGDealsWishlist.Models;

namespace GGDealsWishlist.Matching
{
    /// <summary>Title normalisation used for fallback matching.</summary>
    public static class TitleNormalizer
    {
        private static readonly Regex NonAlphaNumeric = new Regex(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

        private static readonly string[] EditionPhrases =
        {
            "game of the year edition", "game of the year", "goty edition", "goty", "definitive edition", "complete edition",
            "deluxe edition", "ultimate edition", "gold edition", "enhanced edition", "special edition", "standard edition",
            "collectors edition", "anniversary edition", "remastered", "directors cut", "digital deluxe edition", "premium edition",
            "legendary edition", "royal edition", "edition"
        };

        /// <summary>Strict normalisation: case, punctuation, trademark symbols and diacritics are ignored.</summary>
        public static string Normalize(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var text = title.Replace("™", " ").Replace("®", " ").Replace("©", " ").Replace("&", " and ").Replace("'", string.Empty).Replace("’", string.Empty);
            text = RemoveDiacritics(text).ToLowerInvariant();
            text = NonAlphaNumeric.Replace(text, " ").Trim();
            return Regex.Replace(text, @"\s+", " ");
        }

        /// <summary>Loose normalisation for "potential" matches: drops editions, subtitles and a leading article.</summary>
        public static string NormalizeLoose(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var cut = title;
            var separators = new[] { ":", " - ", " – ", " — " };
            foreach (var separator in separators)
            {
                var index = cut.IndexOf(separator, StringComparison.Ordinal);
                if (index > 0)
                {
                    cut = cut.Substring(0, index);
                }
            }

            var text = " " + Normalize(cut) + " ";
            foreach (var phrase in EditionPhrases)
            {
                text = text.Replace(" " + phrase + " ", " ");
            }

            text = text.Trim();
            if (text.StartsWith("the ", StringComparison.Ordinal))
            {
                text = text.Substring(4);
            }

            return text.Trim();
        }

        private static string RemoveDiacritics(string text)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }

    /// <summary>
    /// Matches wishlist entries against the existing Playnite library. It never creates library entries.
    /// Reliable identifiers win; title matching is a carefully limited fallback and ambiguous/loose title
    /// matches are reported as "potential" only - they never count as owned without user confirmation.
    /// </summary>
    public sealed class PlayniteMatcher
    {
        public static readonly Guid SteamLibraryPluginId = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");
        public const string RejectedOverride = "none";

        private static readonly Regex SteamAppLink = new Regex(@"store\.steampowered\.com/app/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Dictionary<Guid, LibraryGameInfo> byId = new Dictionary<Guid, LibraryGameInfo>();
        private readonly Dictionary<long, List<LibraryGameInfo>> bySteamLibraryId = new Dictionary<long, List<LibraryGameInfo>>();
        private readonly Dictionary<long, List<LibraryGameInfo>> bySteamLink = new Dictionary<long, List<LibraryGameInfo>>();
        private readonly Dictionary<string, List<LibraryGameInfo>> byTitle = new Dictionary<string, List<LibraryGameInfo>>();
        private readonly Dictionary<string, List<LibraryGameInfo>> byLooseTitle = new Dictionary<string, List<LibraryGameInfo>>();
        private readonly List<KeyValuePair<string, LibraryGameInfo>> looseTitles = new List<KeyValuePair<string, LibraryGameInfo>>();

        public PlayniteMatcher(IEnumerable<LibraryGameInfo> games)
        {
            foreach (var game in games ?? Enumerable.Empty<LibraryGameInfo>())
            {
                if (game == null || game.Id == Guid.Empty)
                {
                    continue;
                }

                byId[game.Id] = game;
                if (game.PluginId == SteamLibraryPluginId && long.TryParse(game.GameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
                {
                    Add(bySteamLibraryId, appId, game);
                }

                foreach (var url in game.LinkUrls ?? new List<string>())
                {
                    var match = url == null ? System.Text.RegularExpressions.Match.Empty : SteamAppLink.Match(url);
                    if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var linkedId))
                    {
                        Add(bySteamLink, linkedId, game);
                    }
                }

                var title = TitleNormalizer.Normalize(game.Name);
                if (title.Length > 0)
                {
                    Add(byTitle, title, game);
                }

                var loose = TitleNormalizer.NormalizeLoose(game.Name);
                if (loose.Length > 0)
                {
                    Add(byLooseTitle, loose, game);
                    looseTitles.Add(new KeyValuePair<string, LibraryGameInfo>(loose, game));
                }
            }
        }

        public int GameCount => byId.Count;

        public bool Contains(Guid gameId) => byId.ContainsKey(gameId);

        /// <param name="entry">The wishlist entry.</param>
        /// <param name="resolvedTitle">Best known title (GG.deals title when available).</param>
        /// <param name="overrideValue">User decision: a Playnite game id (confirmed) or <see cref="RejectedOverride"/>.</param>
        /// <param name="treatExactTitleAsOwned">When false, exact title matches are downgraded to potential matches.</param>
        public MatchResult Match(WishlistEntry entry, string resolvedTitle, string overrideValue, bool treatExactTitleAsOwned = true)
        {
            if (entry == null)
            {
                return MatchResult.NoMatch;
            }

            if (!string.IsNullOrEmpty(overrideValue))
            {
                if (overrideValue == RejectedOverride)
                {
                    return new MatchResult(MatchKind.UserRejected, null, 0);
                }

                if (Guid.TryParse(overrideValue, out var confirmedId) && byId.TryGetValue(confirmedId, out var confirmed))
                {
                    return new MatchResult(MatchKind.UserConfirmed, confirmed, 1);
                }
                // The confirmed game was deleted from Playnite: fall through to automatic matching.
            }

            var appId = entry.SteamAppId;
            if (appId.HasValue)
            {
                if (bySteamLibraryId.TryGetValue(appId.Value, out var steamGames))
                {
                    return new MatchResult(MatchKind.SteamAppId, Best(steamGames), steamGames.Count);
                }

                if (bySteamLink.TryGetValue(appId.Value, out var linkedGames))
                {
                    return new MatchResult(MatchKind.StoreLink, Best(linkedGames), linkedGames.Count);
                }
            }

            var title = TitleNormalizer.Normalize(resolvedTitle ?? entry.Title);
            if (title.Length == 0)
            {
                return MatchResult.NoMatch;
            }

            if (byTitle.TryGetValue(title, out var exact))
            {
                // Several library entries with the same exact title are copies of the same game on different
                // stores, which still proves ownership.
                return new MatchResult(treatExactTitleAsOwned ? MatchKind.ExactTitle : MatchKind.PotentialTitle, Best(exact), exact.Count);
            }

            var loose = TitleNormalizer.NormalizeLoose(resolvedTitle ?? entry.Title);
            if (loose.Length < 3)
            {
                return MatchResult.NoMatch;
            }

            if (byLooseTitle.TryGetValue(loose, out var looseMatches))
            {
                return new MatchResult(MatchKind.PotentialTitle, Best(looseMatches), looseMatches.Count);
            }

            // Word-boundary prefix match, e.g. "The Witcher 3" vs "The Witcher 3 Wild Hunt".
            var candidates = looseTitles
                .Where(p => IsWordPrefix(loose, p.Key) || IsWordPrefix(p.Key, loose))
                .Select(p => p.Value)
                .Distinct()
                .ToList();
            return candidates.Count > 0
                ? new MatchResult(MatchKind.PotentialTitle, Best(candidates), candidates.Count)
                : MatchResult.NoMatch;
        }

        private static bool IsWordPrefix(string shorter, string longer)
        {
            return shorter.Length >= 4
                && longer.Length > shorter.Length
                && longer.StartsWith(shorter, StringComparison.Ordinal)
                && longer[shorter.Length] == ' ';
        }

        private static LibraryGameInfo Best(List<LibraryGameInfo> games)
        {
            return games.OrderByDescending(g => g.IsInstalled).ThenBy(g => g.Hidden).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).First();
        }

        private static void Add<TKey>(Dictionary<TKey, List<LibraryGameInfo>> map, TKey key, LibraryGameInfo game)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<LibraryGameInfo>();
                map[key] = list;
            }

            if (!list.Contains(game))
            {
                list.Add(game);
            }
        }
    }
}
