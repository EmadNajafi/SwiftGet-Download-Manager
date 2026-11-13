# SwiftGet privacy policy

_Last updated: November 13, 2026_

SwiftGet (the Windows app and the "SwiftGet Download Manager Integration" browser extension) does not collect, store, sell or share personal data. There are no accounts, no analytics, no ads and no tracking.

## What the browser extension does with data

- **Download links.** When you start a download, or choose "Download with SwiftGet", the extension sends the file's address, the page it came from (referrer), the browser's user-agent string and the site's cookies for that address to the SwiftGet app **on your own computer** (`http://127.0.0.1`). The cookies are needed so downloads that require you to be signed in still work. Nothing is sent to any other server.
- **Media on the page.** To list videos and audio you can save, the extension looks at the response headers (content type and size) of media files the page loads. This list is kept in the browser's temporary session storage, only for the open tab, and is deleted when the tab navigates away or closes.
- **Settings.** Your extension settings (file types, minimum size, excluded sites, port) are saved with Chrome's/Edge's own sync storage.

## What the app does with data

The SwiftGet app downloads files from the addresses you give it and saves your download list and settings in `%APPDATA%\SwiftGet` on your computer. It only connects to the servers you download from.

## Permissions used by the extension

| Permission | Why |
|---|---|
| `downloads` | Notice downloads you start and hand them to SwiftGet |
| `cookies` | Send the site's cookies with the download so signed-in downloads work |
| `webRequest` | Detect videos and audio the page plays (headers only) |
| `contextMenus` | "Download with SwiftGet" in the right-click menu |
| `storage` | Save settings and the per-tab media list |
| `tabs` | Know which tab's media to show in the popup |
| `alarms` | Check once a minute whether the SwiftGet app is running |
| Host access (`<all_urls>`) | Read cookies and media headers for the sites you download from |

## Contact

Questions: open an issue at https://github.com/EmadNajafi/SwiftGet-Download-Manager/issues
