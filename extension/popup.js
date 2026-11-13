const t = (key, ...subs) => chrome.i18n.getMessage(key, subs) || key;

document.documentElement.dir = chrome.i18n.getMessage("@@bidi_dir");
document.documentElement.lang = chrome.i18n.getUILanguage();
document.querySelectorAll("[data-i18n]").forEach((el) => (el.textContent = t(el.dataset.i18n)));
document.getElementById("version").textContent = "v" + chrome.runtime.getManifest().version;
document.getElementById("openOptions").addEventListener("click", () => chrome.runtime.openOptionsPage());

const ICONS = {
  video: '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="6" width="13" height="12" rx="2"/><path d="m16 10 5-3v10l-5-3"/></svg>',
  audio: '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18V5l11-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="17" cy="16" r="3"/></svg>',
  download: '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4v11"/><path d="m7 11 5 5 5-5"/><path d="M5 20h14"/></svg>',
  check: '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"><path d="m5 12 5 5 9-10"/></svg>',
  cross: '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round"><path d="M6 6l12 12M18 6 6 18"/></svg>',
  empty: '<svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="m10 8.5 5 3.5-5 3.5z"/></svg>',
};

function formatSize(bytes) {
  if (!bytes) return "";
  const units = ["B", "KB", "MB", "GB"];
  let v = bytes, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return (v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2)) + " " + units[i];
}

function fileName(url, tabTitle, type) {
  try {
    const last = decodeURIComponent(new URL(url).pathname.split("/").pop() || "");
    if (/\.[a-z0-9]{2,4}$/i.test(last)) return last;
  } catch (_) {}
  const ext = (type.split("/")[1] || "mp4").replace("mpeg", "mp3").replace("x-matroska", "mkv");
  const base = (tabTitle || "media").replace(/[\\/:*?"<>|]+/g, " ").trim().slice(0, 80) || "media";
  return `${base}.${ext}`;
}

function render(state) {
  const status = document.getElementById("status");
  status.classList.toggle("ok", state.running);
  status.classList.toggle("off", !state.running);
  document.getElementById("statusText").textContent = t(state.running ? "popupConnected" : "popupDisconnected");
  document.getElementById("offline").hidden = state.running;
  document.getElementById("enabled").checked = state.settings.enabled;

  const list = document.getElementById("mediaList");
  const count = document.getElementById("mediaCount");
  list.replaceChildren();
  count.textContent = state.media.length ? String(state.media.length) : "";

  if (!state.media.length) {
    const empty = document.createElement("div");
    empty.className = "empty";
    empty.innerHTML = ICONS.empty;
    empty.append(t(state.settings.detectMedia ? "popupMediaEmpty" : "popupMediaOff"));
    list.append(empty);
    return;
  }

  for (const m of state.media) {
    const isAudio = m.type.startsWith("audio") || /\.(mp3|m4a|aac|ogg|oga|opus|wav|flac)(\?|$)/i.test(m.url);
    const name = fileName(m.url, state.tabTitle, m.type || (isAudio ? "audio/mp3" : "video/mp4"));

    const row = document.createElement("div");
    row.className = "item";
    row.innerHTML = `
      <span class="item-icon ${isAudio ? "audio" : ""}">${isAudio ? ICONS.audio : ICONS.video}</span>
      <span class="item-text"><div class="item-name"></div><div class="item-meta"></div></span>
      <button class="dl">${ICONS.download}</button>`;
    row.querySelector(".item-name").textContent = name;
    row.querySelector(".item-name").title = m.url;
    row.querySelector(".item-meta").textContent = [m.type, formatSize(m.size)].filter(Boolean).join("  ·  ");

    const btn = row.querySelector(".dl");
    btn.title = t("popupDownload");
    btn.disabled = !state.running;
    btn.addEventListener("click", async () => {
      btn.disabled = true;
      const ok = await chrome.runtime.sendMessage({ type: "download", url: m.url, referrer: state.tabUrl, fileName: name });
      btn.classList.add(ok ? "done" : "fail");
      btn.innerHTML = ok ? ICONS.check : ICONS.cross;
      btn.title = t(ok ? "popupSent" : "popupFailed");
    });
    list.append(row);
  }
}

document.getElementById("enabled").addEventListener("change", (e) => {
  chrome.storage.sync.set({ enabled: e.target.checked });
});

chrome.runtime.sendMessage({ type: "state" }, render);
