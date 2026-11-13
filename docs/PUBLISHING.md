# Publishing the browser extension

Chrome and Edge only let an installer add an extension automatically when the extension is in their store.
After it's published, the SwiftGet installer registers it and the browser shows **"New extension added"** at its next start: one click and it's on.

Everything needed is already prepared:

| File | Use |
|---|---|
| `dist/SwiftGet-Extension-x.y.z.zip` | The package to upload (made by `build.ps1`, without the developer `key`) |
| `docs/store/screenshot-1.png` … `screenshot-3.png` | Screenshots, 1280×800 |
| `docs/store/promo-440x280.png` | Small promo tile |
| `docs/store/icon-128.png` | Store icon |
| `docs/PRIVACY.md` | Privacy policy. Use its GitHub address as the privacy policy URL: `https://github.com/EmadNajafi/SwiftGet-Download-Manager/blob/main/docs/PRIVACY.md` |

## 1. Microsoft Edge Add-ons (free)

1. Sign in at https://partner.microsoft.com/dashboard/microsoftedge and register as a developer (free).
2. **Create new extension** → upload `SwiftGet-Extension-x.y.z.zip`.
3. Fill in the listing with the texts below, upload the screenshots and logo, and paste the privacy policy URL.
4. Submit. Review usually takes a few days.
5. When it's live, copy the extension's **ID** from the dashboard.

## 2. Chrome Web Store (one-time US$5 registration)

1. Register at https://chrome.google.com/webstore/devconsole.
2. **New item** → upload the same zip.
3. **Store listing**: texts and images below. **Privacy**: single purpose and permission justifications below, privacy policy URL, and tick "I do not sell or transfer user data…".
4. Submit for review. When it's published, copy the **item ID** (32 letters, in the item's URL).

## 3. Connect the IDs to SwiftGet

Put the IDs in two places and rebuild:

- `SwiftGet/Core/BrowserIntegration.cs` → `ChromeStoreId` and `EdgeStoreId`
- `installer/SwiftGet.iss` → `#define ChromeStoreId` and `#define EdgeStoreId`

Then `.\build.ps1 -Version x.y.z` (or push a `vx.y.z` tag). From then on:

- the installer registers the extension in Chrome and Edge (the browser asks the user to turn it on), and
- **Options → Browser → Add to Chrome/Edge** opens the store page instead of the manual steps.

## Listing texts

**Name:** SwiftGet Download Manager Integration

**Short description (132 characters max):**
Send browser downloads to the free SwiftGet download manager and save videos and audio from web pages.

**Detailed description:**

> SwiftGet is a fast, free and open-source download manager for Windows. This extension connects your browser to it.
>
> • Downloads you start in the browser go to SwiftGet automatically: multiple connections, pause and resume, queues and scheduling.
> • Right-click any link → "Download with SwiftGet".
> • Finds videos and audio playing on the page; download them with one click from the toolbar button.
> • Choose which file types to catch, a minimum size and sites to skip.
> • English and Persian, light and dark themes.
>
> Requires the free SwiftGet app for Windows: https://github.com/EmadNajafi/SwiftGet-Download-Manager
> If SwiftGet isn't running, downloads simply stay in the browser.
> No accounts, no tracking, no ads. Everything stays on your computer.

**Category:** Productivity (Chrome) / Productivity (Edge)

**Single purpose (Chrome):**
Hand downloads from the browser to the SwiftGet download manager app running on the same computer.

**Permission justifications (Chrome):**

- `downloads`: to notice downloads the user starts and hand them to the SwiftGet app.
- `cookies`: to pass the site's cookies with a download, so downloads that need the user to be signed in work in SwiftGet.
- `webRequest`: to detect video and audio files the current page plays (only response headers are read) so the user can save them.
- `contextMenus`: to add "Download with SwiftGet" to the right-click menu.
- `storage`: to save the user's settings and the per-tab list of detected media.
- `tabs`: to show the detected media of the active tab in the popup.
- `alarms`: to check once a minute whether the SwiftGet app is running, so the app can show that the extension is connected.
- Host permissions (`<all_urls>`): downloads and media can come from any site; host access is needed to read their cookies and media headers.

**Remote code:** No, the extension doesn't use remote code.
