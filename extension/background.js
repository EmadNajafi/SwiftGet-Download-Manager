// SwiftGet browser integration (Chrome, Edge, Brave, Opera — Manifest V3).
// APP must match "Browser connection port" in SwiftGet settings (default 47321).
const APP = "http://127.0.0.1:47321";

async function isEnabled() {
  const v = await chrome.storage.local.get({ enabled: true });
  return v.enabled;
}

async function updateBadge() {
  const on = await isEnabled();
  await chrome.action.setBadgeText({ text: on ? "" : "OFF" });
  await chrome.action.setBadgeBackgroundColor({ color: "#6B7280" });
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: "swiftget-link",
    title: chrome.i18n.getMessage("menuDownload"),
    contexts: ["link", "video", "audio", "image"],
  });
  updateBadge();
});
chrome.runtime.onStartup.addListener(updateBadge);

// messages from the popup
chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg.type === "ping") {
    appIsRunning().then(reply);
    return true; // async reply
  }
  if (msg.type === "setEnabled") {
    chrome.storage.local.set({ enabled: msg.enabled }).then(updateBadge);
  }
  return false;
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const url = info.linkUrl || info.srcUrl;
  if (!url || !/^https?:/i.test(url)) return;
  await sendToApp(url, tab && tab.url);
});

// Take over downloads the browser starts. If SwiftGet is not running, the browser keeps the download.
chrome.downloads.onCreated.addListener(async (item) => {
  if (!(await isEnabled())) return;
  const url = item.finalUrl || item.url;
  if (!/^https?:/i.test(url)) return; // blob:, data:, file: stay in the browser
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

async function appIsRunning() {
  try {
    const r = await fetchWithTimeout(APP + "/ping", { method: "GET" }, 1500);
    return r.ok;
  } catch (_) {
    return false;
  }
}

async function sendToApp(url, referrer) {
  let cookies = "";
  try {
    const list = await chrome.cookies.getAll({ url });
    cookies = list.map((c) => `${c.name}=${c.value}`).join("; ");
  } catch (_) {}

  try {
    const r = await fetchWithTimeout(APP + "/add", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url, referrer: referrer || "", cookies, userAgent: navigator.userAgent, fileName: "" }),
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
