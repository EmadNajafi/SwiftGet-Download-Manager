using System.Diagnostics;
using Microsoft.Win32;

namespace SwiftGet.Core;


public sealed record BrowserInfo(string Key, string Name, string ExePath, string ExtensionsPage, string? StoreUrl);

/// <summary>Finds installed browsers and helps add the SwiftGet extension to them.</summary>
public static class BrowserIntegration
{
    /// <summary>ID of the unpacked extension (fixed by the "key" in manifest.json).</summary>
    public const string UnpackedId = "lgehkcolbjfhjobojcdhedbanmdljdee";

    /// <summary>
    /// Store IDs. Fill these in after publishing the extension; the app then opens the store page
    /// instead of the manual steps, and the installer registers the extension so the browser offers it.
    /// </summary>
    public const string ChromeStoreId = "";
    public const string EdgeStoreId = "";

    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");
    public static string FirefoxFolder => Path.Combine(AppContext.BaseDirectory, "extension-firefox");
    static string FolderFor(BrowserInfo b) => b.Key == "firefox" && Directory.Exists(FirefoxFolder) ? FirefoxFolder : ExtensionFolder;

    public static IReadOnlyList<BrowserInfo> Installed()
    {
        var list = new List<BrowserInfo>();
        void Add(string key, string name, string exe, string page, string? store)
        {
            var path = AppPath(exe);
            if (path != null) list.Add(new BrowserInfo(key, name, path, page, store));
        }
        Add("chrome", "Google Chrome", "chrome.exe", "chrome://extensions/",
            ChromeStoreId.Length > 0 ? $"https://chromewebstore.google.com/detail/{ChromeStoreId}" : null);
        Add("edge", "Microsoft Edge", "msedge.exe", "edge://extensions/",
            EdgeStoreId.Length > 0 ? $"https://microsoftedge.microsoft.com/addons/detail/{EdgeStoreId}"
            : ChromeStoreId.Length > 0 ? $"https://chromewebstore.google.com/detail/{ChromeStoreId}" : null);
        Add("brave", "Brave", "brave.exe", "brave://extensions/",
            ChromeStoreId.Length > 0 ? $"https://chromewebstore.google.com/detail/{ChromeStoreId}" : null);
        Add("firefox", "Mozilla Firefox", "firefox.exe", "about:debugging#/runtime/this-firefox", null);
        Add("opera", "Opera", "opera.exe", "opera://extensions",
            ChromeStoreId.Length > 0 ? $"https://chromewebstore.google.com/detail/{ChromeStoreId}" : null);
        return list;
    }

    /// <summary>Path of a browser from the "App Paths" registry key Windows uses for Run / Start.</summary>
    static string? AppPath(string exe)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                if (key?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Opens the browser where the extension can be added: the store page when published,
    /// otherwise the browser's extensions page next to an Explorer window with the extension folder,
    /// so the folder can be dragged onto the page.
    /// </summary>
    public static void BeginInstall(BrowserInfo browser)
    {
        if (browser.StoreUrl != null)
        {
            Launch(browser.ExePath, browser.StoreUrl);
            return;
        }
        // Browsers don't open their internal pages (chrome://extensions) from the command line,
        // so: copy that address, open a short guide page in the browser, and show the folder to drag.
        try { System.Windows.Clipboard.SetText(browser.ExtensionsPage.TrimEnd('/')); } catch { }
        var guide = Path.Combine(AppContext.BaseDirectory, "guide", "extension-guide.html");
        if (File.Exists(guide))
            Launch(browser.ExePath, new Uri(guide).AbsoluteUri + $"?lang={Loc.I.Language}&browser={browser.Key}");
        if (Directory.Exists(AppContext.BaseDirectory))
        {
            var folder = FolderFor(browser);
            // Firefox loads an add-on by its manifest file; Chromium browsers take the folder
            var select = browser.Key == "firefox" ? Path.Combine(folder, "manifest.json") : folder;
            try { Process.Start("explorer.exe", $"/select,\"{select}\""); } catch { }
        }
    }

    static void Launch(string exe, string url)
    {
        try { Process.Start(new ProcessStartInfo(exe, $"\"{url}\"") { UseShellExecute = false }); } catch { }
    }
}
