const DEFAULT_TYPES =
  "zip rar 7z tar gz bz2 xz iso exe msi apk dmg deb rpm msix appx " +
  "mp4 mkv avi mov wmv flv webm m4v 3gp mpg mpeg ts " +
  "mp3 wav flac aac ogg m4a wma opus " +
  "pdf doc docx xls xlsx ppt pptx epub " +
  "bin img torrent";

const DEFAULTS = {
  enabled: true, contextMenu: true, detectMedia: true,
  extensions: DEFAULT_TYPES, minSizeKB: 0, excludedSites: "", port: 47321,
};

const t = (key) => chrome.i18n.getMessage(key) || key;
document.documentElement.dir = chrome.i18n.getMessage("@@bidi_dir");
document.title = t("optTitle");
document.querySelectorAll("[data-i18n]").forEach((el) => (el.textContent = t(el.dataset.i18n)));

const $ = (id) => document.getElementById(id);

async function load() {
  const s = { ...DEFAULTS, ...(await chrome.storage.sync.get(Object.keys(DEFAULTS))) };
  $("enabled").checked = s.enabled;
  $("contextMenu").checked = s.contextMenu;
  $("detectMedia").checked = s.detectMedia;
  $("extensions").value = s.extensions;
  $("minSizeKB").value = s.minSizeKB;
  $("excludedSites").value = s.excludedSites;
  $("port").value = s.port;
}

$("resetTypes").addEventListener("click", () => ($("extensions").value = DEFAULT_TYPES));

$("save").addEventListener("click", async () => {
  const port = Math.min(65535, Math.max(1024, Number($("port").value) || 47321));
  await chrome.storage.sync.set({
    enabled: $("enabled").checked,
    contextMenu: $("contextMenu").checked,
    detectMedia: $("detectMedia").checked,
    extensions: $("extensions").value.trim().replace(/\s+/g, " "),
    minSizeKB: Math.max(0, Number($("minSizeKB").value) || 0),
    excludedSites: $("excludedSites").value.trim(),
    port,
  });
  $("port").value = port;
  $("saved").classList.add("show");
  setTimeout(() => $("saved").classList.remove("show"), 1800);
});

load();
