# GG.deals Wishlist for Playnite

A Playnite extension that shows your wishlist with live GG.deals prices in a native Playnite sidebar.

## What you get

- A native WPF sidebar. It is not an embedded browser. It has four views:
  - **Cover + information** (the default)
  - **Compact**
  - **List**
  - **Grid**, with landscape cover tiles: two per row, three in a wide sidebar, one in a narrow one

  Below 230 px wide, the cover cards and the grid switch automatically to compact rows. In a wide sidebar (900 px and up) the cover cards flow into two or more columns, and the grid adds a column for roughly every 200 px. An expanded card always takes a full row.
- Click a card or tile (or press Enter) to open it in place. The expanded card shows the price history and the current and lowest-ever prices for official stores and keyshops, with buttons for GG.deals and the full details page. Only one card is open at a time. In the compact views a click opens the full details page instead.
- Price history recorded by the extension itself. The GG.deals Prices API has no history endpoint, so each refresh saves the prices it sees, and a point is added only when a price changes:
  - The history starts empty and only covers the time the extension has been running.
  - It is stored locally in `price-history.json`, capped at 200 changes per game, and dropped when a game leaves the wishlist.
  - A change of region or currency starts a new line, so currencies are never mixed.
  - *Settings → Advanced → Clear price history* deletes it. *Clear cached data* keeps it.
  - A small trend line appears on cover cards, and a chart in the expanded card and the details page. It can be hidden under *Information shown → Price history*.
- A header summary showing the number of games, games on sale and historical lows, plus the last update time.
- Local search, sorting and filters. None of these make network calls:
  - **Sorting:** 10 sort orders.
  - **Filters:** price, discount, store type, sale status, historical low, ownership, favourites and collections.
  - **Quick tabs:** ALL, ON SALE and HISTORICAL LOW.
- A details page with current, historical, retail and keyshop prices, and buttons to open the game on GG.deals, the store, Steam or Playnite.
- A context menu for one or several games:
  - Mark games as favourites (★).
  - Add them to local collections, or remove them.
  - Copy GG.deals links.
  - **Change Cover** for a single game: pick from SteamGridDB, choose an image file, or paste a web address. Reset returns to automatic covers.
- Automatic covers: your Playnite cover for owned games, then Steam's portrait art, then Steam's header image. With an optional free SteamGridDB API key (extension settings), games Steam has no artwork for are filled in from SteamGridDB.
- Playnite library matching. The extension checks the Steam App ID first, then a store link, then an exact title:
  - Similar titles are only suggested as possible matches. They never count as owned until you confirm them.
  - "Owned" means the game is anywhere in your Playnite library, installed or not.
  - The extension never creates library entries.
- Prices refresh automatically (every 15 minutes to 24 hours; the default is 1 hour) or when you refresh manually:
  - Prices are requested in batches of up to 100 IDs.
  - Requests stay within the GG.deals limits of 100 per minute and 1,000 per hour.
  - Cached prices stay visible when a refresh fails, with a clear warning.
- Light, dark or inherited Playnite theme, a custom accent colour, and a choice of which information fields to show.
- Attribution on every screen: "Prices powered by GG.deals" links to GG.deals.

## Limitations

| Topic | Status |
|---|---|
| **Wishlist access** | GG.deals does not currently expose wishlists through its public API. The extension does **not** scrape gg.deals pages. It shows a clear "wishlist access unavailable" state instead. Until an official endpoint exists, choose another source under *Settings → Wishlist source*. Prices for those games always come from the official GG.deals Prices API. |
| **Steam wishlist** | Choose **My Steam wishlist** and enter your SteamID64 or a `steamcommunity.com/profiles/…` link. The game list is read with Steam's official Web API (`IWishlistService/GetWishlist`), in your Steam wishlist order and with the dates you added each game. Game names come from Steam's store API (`IStoreBrowseService/GetItems`), so every game is named even before its prices load or when GG.deals doesn't list it. Steam's own prices in that response are ignored. No Steam login is used, so your wishlist must be public (Steam → Edit Profile → Privacy Settings → Game details: Public). Custom `/id/name` links aren't supported, because resolving them requires a Steam Web API key. Only Steam games are included. |
| **Manual list** | Choose **Manual list** and enter Steam app IDs or store links, one per line. |
| **Discounts, store names, ratings, platforms** | The Prices API returns current and historical retail and keyshop prices only. These fields, and the ON SALE tab, stay hidden or disabled until a provider supplies the data. They are never fabricated. |
| **Price history** | GG.deals offers no price-history data through its API. The extension only charts the prices it saw itself, from the day it started tracking each game. It never shows an estimated or back-filled history. |
| **Out of scope** | Price-drop notifications, purchasing, deal scores, recommendations, platform and genre filters, and changing the wishlist on GG.deals. |

When GG.deals adds a wishlist endpoint, the only change needed is a new `IGGDealsWishlistProvider` implementation registered first in `GGDealsWishlistPlugin`. The UI, cache and price layers stay as they are.

## API key security

- You enter the key in the extension settings, in a masked field. You can replace it, remove it or test it.
- The key is stored encrypted with Windows DPAPI for your Windows account. The settings file never contains it in plain text.
- The key is registered with a log redactor. Every log line and exception message passes through the redactor before it reaches Playnite's log. Request URLs are never logged.

## Install

1. Download the `.pext` file from the [latest release](https://github.com/Kyerstorm/Playnite-GG-Deals-Integration/releases/latest).
2. Drag the `.pext` file onto Playnite, or open it with Playnite.
3. Open **GG.deals Wishlist** in the sidebar and paste your API key from your GG.deals account.
4. Optional: in the extension settings, set **Wishlist source** to **My Steam wishlist** and enter your SteamID64.

To build it yourself, run `powershell -ExecutionPolicy Bypass -File build/pack.ps1`. This runs the tests, builds the Release version and writes `dist/*.pext`.

## Development

```
src/GGDealsWishlist/          the extension (net462, WPF, PlayniteSDK 6.11)
  Api/                        Prices API client, rate-limit tracker, regions and currencies
  Providers/                  wishlist providers (unavailable, manual) and the price provider
  Matching/                   Playnite library matching
  Querying/                   local filter, search and sort pipeline
  Services/                   data service, refresh scheduler, versioned JSON persistence
  ViewModels/ Views/          the sidebar, details page and collections manager
  Settings/                   the settings model, view model and view
tests/GGDealsWishlist.Tests/  xunit tests
tools/PreviewApp/             renders the real views with fake data to PNG files (for development only)
```

- Build and test: `dotnet test GGDealsWishlist.sln`
- Visual check: `dotnet build tools/PreviewApp` then `tools/PreviewApp/bin/Debug/net462/PreviewApp.exe <output folder>`

The extension's data files live in its Playnite user-data folder:

- `cache.json`: the wishlist, prices, rate-limit usage and the last error.
- `state.json`: favourites, collections, ownership decisions, custom covers and UI state.
- `covers/`: the image cache.
- `steamgriddb.json`: cached SteamGridDB lookups.

Both JSON files carry a `DataVersion`. A corrupt file is quarantined and never crashes Playnite. A file from a newer version is backed up before it is replaced.

## License

[MIT](LICENSE). Not affiliated with GG.deals or Valve.
