const t = (key) => chrome.i18n.getMessage(key);

document.documentElement.dir = chrome.i18n.getMessage("@@bidi_dir");
document.querySelectorAll("[data-i18n]").forEach((el) => (el.textContent = t(el.dataset.i18n)));

const box = document.getElementById("enabled");
chrome.storage.local.get({ enabled: true }, (v) => (box.checked = v.enabled));
box.addEventListener("change", () => chrome.runtime.sendMessage({ type: "setEnabled", enabled: box.checked }));

chrome.runtime.sendMessage({ type: "ping" }, (running) => {
  const status = document.getElementById("status");
  status.classList.add(running ? "ok" : "off");
  document.getElementById("statusText").textContent = t(running ? "popupConnected" : "popupDisconnected");
  document.getElementById("help").hidden = !!running;
});
