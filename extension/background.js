// SwiftGet browser integration (Chrome, Edge, Brave, Opera — Manifest V3).
// Talks to the SwiftGet app on http://127.0.0.1:<port> (default 47321).

const DEFAULTS = {
  enabled: true,
  port: 47321,
  // file types that are sent to SwiftGet (empty list = everything)
  extensions:
    "zip rar 7z tar gz bz2 xz iso exe msi apk dmg deb rpm msix appx " +
    "mp4 mkv avi mov wmv flv webm m4v 3gp mpg mpeg ts " +
    "mp3 wav flac aac ogg m4a wma opus " +
    "pdf doc docx xls xlsx ppt pptx epub " +
    "bin img torrent",
  minSizeKB: 0,
  excludedSites: "",
  contextMenu: true,
  detectMedia: true,
  floatingButton: true,
};

const MEDIA_EXT = /\.(mp4|m4v|webm|mkv|mov|avi|flv|3gp|mp3|m4a|aac|ogg|oga|opus|wav|flac)(\?|#|$)/i;
const MIN_MEDIA_BYTES = 512 * 1024;
// video ads (pre-roll etc.) are not what the user wants to download
const AD_URL = /doubleclick\.net|2mdn\.net|video_ads|aparat-ads|\/\/ads?\.|googlesyndication|googleads|imasdk\.googleapis|\/pagead\/|adservice|adserver|moatads|innovid|springserve|spotxchange|adsrvr|serving-sys|tremorhub|teads\.tv|vidazoo|\/vast\/|[?&]ad_type=/i;

// pages on these sites hold a video only SwiftGet's video downloader (yt-dlp) can get: the button sends
// the page and SwiftGet asks which quality to download
const SITE_HOSTS = ["youtube.com", "youtu.be", "youtube-nocookie.com", "vimeo.com", "dailymotion.com", "dai.ly", "instagram.com",
  "twitter.com", "x.com", "facebook.com", "fb.watch", "tiktok.com", "twitch.tv", "soundcloud.com", "reddit.com", "bilibili.com",
  "ok.ru", "vk.com", "rumble.com", "streamable.com", "bitchute.com", "telewebion.com"];
function isSitePage(url) {
  try {
    const u = new URL(url);
    const h = u.hostname.toLowerCase();
    return /^https?:$/.test(u.protocol) && u.pathname.length > 1 && SITE_HOSTS.some((s) => h === s || h.endsWith("." + s));
  } catch (_) {
    return false;
  }
}

// ---------------------------------------------------------------- settings

async function settings() {
  return { ...DEFAULTS, ...(await chrome.storage.sync.get(Object.keys(DEFAULTS))) };
}

const appUrl = (s) => `http://127.0.0.1:${s.port || 47321}`;

function extOf(url) {
  try {
    const name = decodeURIComponent(new URL(url).pathname.split("/").pop() || "");
    const dot = name.lastIndexOf(".");
    return dot > 0 ? name.slice(dot + 1).toLowerCase() : "";
  } catch (_) {
    return "";
  }
}

function hostOf(url) {
  try { return new URL(url).hostname.toLowerCase(); } catch (_) { return ""; }
}

function isExcluded(s, ...urls) {
  const sites = s.excludedSites.split(/[\s,]+/).map((x) => x.trim().toLowerCase()).filter(Boolean);
  return urls.some((u) => {
    const h = hostOf(u || "");
    return h && sites.some((site) => h === site || h.endsWith("." + site));
  });
}

// ---------------------------------------------------------------- app connection

async function appIsRunning() {
  try {
    const r = await fetchWithTimeout(appUrl(await settings()) + "/ping?from=extension", { method: "GET" }, 1500);
    return r.ok;
  } catch (_) {
    return false;
  }
}

async function sendToApp(url, referrer, fileName, site, title) {
  let cookies = "";
  try {
    const list = await chrome.cookies.getAll({ url });
    cookies = list.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch (_) {}

  try {
    const r = await fetchWithTimeout(appUrl(await settings()) + "/add?from=extension", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url, referrer: referrer || "", cookies, userAgent: navigator.userAgent, fileName: fileName || "", site: !!site, title: title || "" }),
    }, 3000);
    return r.ok;
  } catch (_) {
    return false;
  }
}

function fetchWithTimeout(resource, options, ms) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), ms);
  return fetch(resource, { ...options, signal: ctrl.signal }).finally(() => clearTimeout(timer));
}

// ---------------------------------------------------------------- badge and context menu

async function updateBadge(tabId) {
  const s = await settings();
  if (!s.enabled) {
    await chrome.action.setBadgeBackgroundColor({ color: "#6B7280" });
    await chrome.action.setBadgeText({ text: "OFF" });
    return;
  }
  await chrome.action.setBadgeText({ text: "" });
  if (tabId != null) {
    const media = await mediaFor(tabId);
    await chrome.action.setBadgeBackgroundColor({ color: "#2563EB", tabId });
    await chrome.action.setBadgeText({ text: media.length ? String(media.length) : "", tabId });
  }
}

async function setupMenus() {
  await chrome.contextMenus.removeAll();
  const s = await settings();
  if (!s.contextMenu) return;
  chrome.contextMenus.create({ id: "swiftget-link", title: chrome.i18n.getMessage("menuDownload"), contexts: ["link", "video", "audio", "image"] });
  chrome.contextMenus.create({ id: "swiftget-all", title: chrome.i18n.getMessage("menuDownloadAll"), contexts: ["page", "selection"] });
}

chrome.runtime.onInstalled.addListener(async () => {
  await setupMenus();
  await updateBadge();
  chrome.alarms.create("ping", { periodInMinutes: 1 });
  appIsRunning();   // let SwiftGet show "connected" right away
});
chrome.runtime.onStartup.addListener(async () => {
  await setupMenus();
  await updateBadge();
  chrome.alarms.create("ping", { periodInMinutes: 1 });
  appIsRunning();
});

// lets the app show "extension connected"
chrome.alarms.onAlarm.addListener((a) => { if (a.name === "ping") appIsRunning(); });

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== "sync") return;
  if ("contextMenu" in changes) setupMenus();
  if ("enabled" in changes) updateBadge();
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const url = info.linkUrl || info.srcUrl;
  if (info.menuItemId === "swiftget-link" && url && /^(https?|ftp):/i.test(url)) await sendToApp(url, tab && tab.url);
  if (info.menuItemId === "swiftget-all" && tab?.id != null) await sendAllLinks(tab, info.frameId || 0);
});

/// Collects the page's links (or the selected ones) and lets SwiftGet show them in its link picker.
async function sendAllLinks(tab, frameId) {
  let res;
  try { res = await chrome.tabs.sendMessage(tab.id, { type: "collectLinks" }, { frameId }); } catch (_) { return false; }
  const seen = new Set();
  const links = (res?.links || []).filter((l) => !seen.has(l.href) && seen.add(l.href));
  if (!links.length) return false;

  // cookies per site, so links that need a login still work
  const cookiesByHost = {};
  for (const host of [...new Set(links.map((l) => hostOf(l.href)))].slice(0, 30)) {
    try {
      const list = await chrome.cookies.getAll({ domain: host });
      cookiesByHost[host] = list.map((c) => `${c.name}=${c.value}`).join("; ");
    } catch (_) {}
  }
  try {
    const r = await fetchWithTimeout(appUrl(await settings()) + "/addmany?from=extension", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        items: links.slice(0, 5000).map((l) => ({ url: l.href, cookies: cookiesByHost[hostOf(l.href)] || "", fileName: "" })),
        referrer: tab.url, userAgent: navigator.userAgent, title: res.title || tab.title || "",
      }),
    }, 5000);
    return r.ok;
  } catch (_) {
    return false;
  }
}
// ---------------------------------------------------------------- catching browser downloads

chrome.downloads.onCreated.addListener(async (item) => {
  const s = await settings();
  if (!s.enabled) return;
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return; // blob:, data:, file: stay in the browser
  if (isExcluded(s, url, item.referrer)) return;

  const allowed = s.extensions.split(/[\s,]+/).map((x) => x.replace(/^\./, "").toLowerCase()).filter(Boolean);
  const ext = extOf(item.filename || url);
  if (allowed.length && ext && !allowed.includes(ext)) return;
  if (s.minSizeKB > 0 && item.fileSize > 0 && item.fileSize < s.minSizeKB * 1024) return;

  if (!(await appIsRunning())) return;

  try { await chrome.downloads.pause(item.id); } catch (_) {}
  const ok = await sendToApp(url, item.referrer);
  if (ok) {
    try { await chrome.downloads.cancel(item.id); } catch (_) {}
    try { await chrome.downloads.erase({ id: item.id }); } catch (_) {}
  } else {
    try { await chrome.downloads.resume(item.id); } catch (_) {}
  }
});

// ---------------------------------------------------------------- media detection (videos / audio on the page)

const mediaKey = (tabId) => `media:${tabId}`;

async function mediaFor(tabId) {
  const v = await chrome.storage.session.get(mediaKey(tabId));
  return v[mediaKey(tabId)] || [];
}

chrome.webRequest.onHeadersReceived.addListener(async (d) => {
  if (d.tabId < 0 || d.statusCode >= 400) return;
  const s = await settings();
  if (!s.detectMedia) return;

  const header = (n) => (d.responseHeaders || []).find((h) => h.name.toLowerCase() === n)?.value || "";
  const type = header("content-type").toLowerCase();
  const isHls = type.includes("mpegurl") || /\.m3u8(\?|#|$)/i.test(d.url);
  const isMedia = isHls || type.startsWith("video/") || type.startsWith("audio/") || MEDIA_EXT.test(d.url);
  if (!isMedia || AD_URL.test(d.url) || AD_URL.test(d.initiator || "") || /\.mpd(\?|#|$)/i.test(d.url) || /\.(ts|m4s)(\?|#|$)/i.test(d.url)) return; // DASH manifests and stream parts aren't offered

  // total size: Content-Range "bytes a-b/total" for partial responses, otherwise Content-Length
  const range = header("content-range").match(/\/(\d+)$/);
  const size = range ? Number(range[1]) : Number(header("content-length")) || 0;
  if (!isHls && size && size < MIN_MEDIA_BYTES) return;

  const base = d.url.split("#")[0];
  const entry = { url: base, type: type.split(";")[0] || extOf(base), size: isHls ? 0 : size, kind: isHls ? "hls" : type.startsWith("audio") || /\.(mp3|m4a|aac|ogg|oga|opus|wav|flac)(\?|$)/i.test(base) ? "audio" : "video", time: Date.now() };
  if (isHls) {
    if (d.method && d.method !== "GET") return;
    if ((await mediaFor(d.tabId)).some((m) => m.url === base)) return; // live streams reload their playlist every few seconds
    Object.assign(entry, await readPlaylist(base));
  }
  await addMedia(d.tabId, entry);
}, { urls: ["<all_urls>"], types: ["media", "xmlhttprequest", "other", "object"] }, ["responseHeaders"]);

// Several requests can finish at once; adding one at a time keeps them from overwriting each other.
let mediaQueue = Promise.resolve();
function addMedia(tabId, entry) {
  mediaQueue = mediaQueue.then(async () => {
    const list = await mediaFor(tabId);
    if (list.some((m) => m.url === entry.url)) return;
    list.unshift(entry);
    await chrome.storage.session.set({ [mediaKey(tabId)]: list.slice(0, 30) });
    updateBadge(tabId);
  }).catch(() => {});
  return mediaQueue;
}

// HLS: a master playlist lists the qualities (and separate audio). Only the master is offered;
// the qualities it lists are hidden, so the video shows up once (SwiftGet picks the best quality).
const noQuery = (u) => u.split(/[?#]/)[0];
async function readPlaylist(url) {
  try {
    const r = await fetchWithTimeout(url, { credentials: "include" }, 5000);
    const text = await r.text();
    if (!text.includes("#EXT-X-STREAM-INF")) return { master: false };
    const parts = [];
    let best = 0;
    const lines = text.split(/\r?\n/);
    lines.forEach((line, i) => {
      if (line.startsWith("#EXT-X-STREAM-INF")) {
        const h = Number((line.match(/RESOLUTION=\d+x(\d+)/) || [])[1] || 0);
        best = Math.max(best, h);
        const next = lines.slice(i + 1).find((l) => l && !l.startsWith("#"));
        if (next) parts.push(noQuery(new URL(next.trim(), url).href));
      }
      const uri = line.startsWith("#EXT-X-MEDIA") && line.match(/URI="([^"]+)"/);
      if (uri) parts.push(noQuery(new URL(uri[1], url).href));
    });
    return { master: true, parts, quality: best };
  } catch (_) {
    return {};
  }
}

// forget a tab's media when it navigates or closes
chrome.tabs.onUpdated.addListener(async (tabId, change) => {
  if (change.status === "loading" && change.url) {
    await chrome.storage.session.remove(mediaKey(tabId));
    updateBadge(tabId);
  }
});
chrome.tabs.onRemoved.addListener((tabId) => chrome.storage.session.remove(mediaKey(tabId)));

// ---------------------------------------------------------------- media names

function mediaName(m, title) {
  if (m.kind !== "hls") {
    try {
      const last = decodeURIComponent(new URL(m.url).pathname.split("/").pop() || "");
      if (/\.[a-z0-9]{2,4}$/i.test(last) && !/^(file|video|videoplayback|index|media|stream|master|playlist)\./i.test(last)) return last;
    } catch (_) {}
  }
  const ext = m.kind === "hls" ? "mp4" : m.kind === "audio" ? "mp3" : ((m.type || "").split("/")[1] || "mp4").replace("x-matroska", "mkv");
  const base = (title || "video").replace(/[\\/:*?"<>|]+/g, " ").replace(/\s+/g, " ").trim().slice(0, 90) || "video";
  return `${base}.${ext}`;
}

/// The page itself, for SwiftGet's video downloader (YouTube etc., or when nothing else was found).
function siteEntry(pageUrl, title) {
  return { kind: "site", url: pageUrl, name: title || pageUrl, label: chrome.i18n.getMessage("mediaSite"), size: 0, site: true };
}

function withSite(media, pageUrl, title) {
  if (!pageUrl || !/^https?:/i.test(pageUrl)) return media;
  if (isSitePage(pageUrl)) return [siteEntry(pageUrl, title), ...media.filter((m) => m.kind !== "video" && m.kind !== "audio" || m.size > 0)];
  return media.length ? media : [siteEntry(pageUrl, title)];
}

function describe(list, title) {
  // hide the qualities/audio of a master playlist we also have
  const hidden = new Set(list.filter((m) => m.master).flatMap((m) => m.parts || []));
  for (const m of list) if (m.kind === "hls" && !m.quality) m.quality = Number((m.url.match(/[-_\/](\d{3,4})p[._\/-]/) || [])[1] || 0);
  const shown = list.filter((m) => !(m.kind === "hls" && !m.master && hidden.has(noQuery(m.url))) && !AD_URL.test(m.url));
  // main video first: streams with several qualities, then the biggest files
  const rank = (m) => (m.master ? 3 : m.kind === "hls" ? 2 : m.kind === "video" ? 1 : 0);
  shown.sort((a, b) => rank(b) - rank(a) || (b.quality || 0) - (a.quality || 0) || (b.size || 0) - (a.size || 0));
  const used = {};
  return shown.map((m) => {
    let name = mediaName(m, title);
    if (used[name] && m.quality) {
      const dot = name.lastIndexOf(".");
      name = `${name.slice(0, dot)} ${m.quality}p${name.slice(dot)}`;
    }
    if (used[name]) {
      const dot = name.lastIndexOf(".");
      name = `${name.slice(0, dot)} (${++used[name]})${name.slice(dot)}`;
    } else used[name] = 1;
    const label = m.kind === "hls" ? chrome.i18n.getMessage("mediaHls") + (m.quality ? ` · ${m.quality}p` : "") : m.type;
    return { ...m, name, label };
  });
}

// ---------------------------------------------------------------- popup messages

chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  (async () => {
    switch (msg.type) {
      case "state": {
        const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
        reply({
          running: await appIsRunning(),
          settings: await settings(),
          media: tab ? withSite(describe(await mediaFor(tab.id), tab.title), tab.url, tab.title) : [],
          tabUrl: tab?.url || "",
          tabTitle: tab?.title || "",
        });
        break;
      }
      case "download":
        reply(await sendToApp(msg.url, msg.referrer, msg.fileName, msg.site, msg.title));
        break;
      case "pageMedia": {
        // from the floating button in a page
        const tabId = _sender.tab?.id;
        let list = tabId != null ? await mediaFor(tabId) : [];
        if (msg.direct && !list.some((m) => m.url === msg.direct)) list = [{ url: msg.direct, type: "video/mp4", size: 0, kind: "video" }, ...list];
        // inside an embedded player (youtube.com/embed/…) the frame's own address is the video page
        const page = msg.page && isSitePage(msg.page) ? msg.page : _sender.tab?.url;
        reply({ running: await appIsRunning(), media: withSite(describe(list, _sender.tab?.title), page, _sender.tab?.title) });
        break;
      }
      default:
        reply(null);
    }
  })();
  return true; // async reply
});
