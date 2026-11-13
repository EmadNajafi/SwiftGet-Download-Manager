// SwiftGet content script: a "Download this video" button over videos, and link collection for
// "Download all links with SwiftGet". Runs in every frame of every page; does nothing until needed.
(() => {
  if (window.__swiftget) return;
  window.__swiftget = true;

  const t = (k, ...a) => chrome.i18n.getMessage(k, a) || k;
  const MIN_W = 240, MIN_H = 135;

  // ---------------------------------------------------------------- link collection

  chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
    if (msg.type !== "collectLinks") return false;
    const sel = window.getSelection();
    let anchors = [...document.querySelectorAll("a[href]")];
    if (sel && !sel.isCollapsed && sel.rangeCount) {
      const range = sel.getRangeAt(0);
      const inSelection = anchors.filter((a) => range.intersectsNode(a));
      if (inSelection.length) anchors = inSelection;
    }
    const media = [...document.querySelectorAll("video[src], audio[src], source[src]")].map((m) => ({ href: m.src, text: "" }));
    const links = [...anchors.map((a) => ({ href: a.href, text: (a.textContent || "").trim().slice(0, 120) })), ...media]
      .filter((l) => /^(https?|ftp):/i.test(l.href));
    reply({ links, title: document.title });
    return false;
  });

  // ---------------------------------------------------------------- floating video button

  let host, root, button, menu, current, hideTimer, enabled = true;

  chrome.storage.sync.get({ enabled: true, floatingButton: true }, (s) => (enabled = s.enabled && s.floatingButton));
  chrome.storage.onChanged.addListener((c, area) => {
    if (area === "sync" && ("enabled" in c || "floatingButton" in c))
      chrome.storage.sync.get({ enabled: true, floatingButton: true }, (s) => { enabled = s.enabled && s.floatingButton; if (!enabled) hide(); });
  });

  function ensureUi() {
    if (host) return;
    host = document.createElement("swiftget-overlay");
    host.style.cssText = "all:initial;position:fixed;z-index:2147483647;top:0;left:0;width:0;height:0;";
    root = host.attachShadow({ mode: "closed" });
    root.innerHTML = `
      <style>
        :host { all: initial; }
        .btn { position: fixed; display: none; align-items: center; gap: 7px; padding: 7px 12px 7px 10px;
               border: none; border-radius: 18px; cursor: pointer; color: #fff; font: 600 13px/1 "Segoe UI", system-ui, sans-serif;
               background: linear-gradient(135deg, #3b82f6, #4f46e5); box-shadow: 0 4px 14px rgba(0,0,0,.35); opacity: .93;
               transition: opacity .15s, transform .15s; }
        .btn:hover { opacity: 1; transform: translateY(-1px); }
        .btn svg { flex: none; }
        .tip { position: fixed; display: none; max-width: 280px; padding: 8px 12px; border-radius: 8px; background: #1f2937; color: #fff;
               font: 12.5px/1.4 "Segoe UI", system-ui, sans-serif; box-shadow: 0 4px 14px rgba(0,0,0,.35); }
        .menu { position: fixed; display: none; min-width: 260px; max-width: 380px; max-height: 300px; overflow: auto; padding: 6px;
                border-radius: 10px; background: #fff; color: #0f172a; box-shadow: 0 8px 28px rgba(0,0,0,.35);
                font: 13px/1.4 "Segoe UI", system-ui, sans-serif; }
        .item { display: block; width: 100%; text-align: start; border: none; background: none; padding: 8px 10px; border-radius: 7px; cursor: pointer; color: inherit; font: inherit; }
        .item:hover { background: #e3ecfd; }
        .name { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; direction: ltr; }
        .meta { color: #5b6474; font-size: 11.5px; direction: ltr; }
        @media (prefers-color-scheme: dark) {
          .menu { background: #1c1f26; color: #f1f3f7; } .item:hover { background: #1e2a45; } .meta { color: #a2aab8; }
        }
      </style>
      <button class="btn" part="btn">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4v11"/><path d="m7 11 5 5 5-5"/><path d="M5 20h14"/></svg>
        <span></span>
      </button>
      <div class="tip"></div>
      <div class="menu"></div>`;
    button = root.querySelector(".btn");
    menu = root.querySelector(".menu");
    button.querySelector("span").textContent = t("btnDownloadVideo");
    button.addEventListener("click", onButtonClick);
    button.addEventListener("mouseenter", () => clearTimeout(hideTimer));
    button.addEventListener("mouseleave", scheduleHide);
    menu.addEventListener("mouseenter", () => clearTimeout(hideTimer));
    menu.addEventListener("mouseleave", scheduleHide);
    (document.fullscreenElement || document.documentElement).appendChild(host);
  }

  function place() {
    if (!current || !button) return;
    const r = current.getBoundingClientRect();
    if (r.width < MIN_W || r.height < MIN_H || r.bottom < 0 || r.top > innerHeight) { hide(); return; }
    button.style.display = "flex";
    const bw = button.offsetWidth || 190;
    button.style.top = Math.max(4, r.top + 12) + "px";
    button.style.left = Math.max(4, Math.min(innerWidth - bw - 4, r.right - bw - 12)) + "px";
  }

  function show(video) {
    if (!enabled) return;
    ensureUi();
    // in full screen the overlay must live inside the full-screen element
    const parent = document.fullscreenElement || document.documentElement;
    if (host.parentNode !== parent) parent.appendChild(host);
    clearTimeout(hideTimer);
    current = video;
    place();
  }

  function hide() {
    if (button) button.style.display = "none";
    if (menu) menu.style.display = "none";
    if (root) root.querySelector(".tip").style.display = "none";
    current = null;
  }

  function scheduleHide() {
    clearTimeout(hideTimer);
    hideTimer = setTimeout(hide, 1800);
  }

  document.addEventListener("mouseover", (e) => {
    const v = e.target instanceof Element ? e.target.closest("video") || videoUnder(e) : null;
    if (v) show(v);
  }, true);
  document.addEventListener("mouseout", (e) => { if (current && e.target === current) scheduleHide(); }, true);
  document.addEventListener("play", (e) => { if (e.target instanceof HTMLVideoElement) { show(e.target); scheduleHide(); } }, true);
  addEventListener("scroll", () => place(), true);
  addEventListener("resize", () => place());
  document.addEventListener("fullscreenchange", () => current && show(current));

  // players often cover the <video> with their own controls; find a video under the mouse
  function videoUnder(e) {
    for (const v of document.querySelectorAll("video")) {
      const r = v.getBoundingClientRect();
      if (r.width >= MIN_W && r.height >= MIN_H && e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) return v;
    }
    return null;
  }

  function tip(text) {
    const el = root.querySelector(".tip");
    el.textContent = text;
    el.style.display = "block";
    const b = button.getBoundingClientRect();
    el.style.top = b.bottom + 8 + "px";
    el.style.left = Math.max(4, b.right - el.offsetWidth) + "px";
    setTimeout(() => (el.style.display = "none"), 3500);
  }

  const fmt = (n) => { if (!n) return ""; const u = ["B", "KB", "MB", "GB"]; let i = 0; while (n >= 1024 && i < 3) { n /= 1024; i++; } return (n >= 100 ? n.toFixed(0) : n.toFixed(1)) + " " + u[i]; };

  async function onButtonClick(e) {
    e.stopPropagation();
    e.preventDefault();
    const direct = current && /^https?:/i.test(current.currentSrc || "") ? current.currentSrc : null;
    const res = await chrome.runtime.sendMessage({ type: "pageMedia", direct });
    if (!res || !res.running) { tip(t("popupStartApp")); return; }
    const list = res.media || [];
    if (!list.length) { tip(t("btnNoMedia")); return; }
    if (list.length === 1) { send(list[0]); return; }

    menu.replaceChildren();
    for (const m of list) {
      const b = document.createElement("button");
      b.className = "item";
      const name = document.createElement("div"); name.className = "name"; name.textContent = m.name;
      const meta = document.createElement("div"); meta.className = "meta"; meta.textContent = [m.label, fmt(m.size)].filter(Boolean).join("  ·  ");
      b.append(name, meta);
      b.addEventListener("click", (ev) => { ev.stopPropagation(); menu.style.display = "none"; send(m); });
      menu.append(b);
    }
    const r = button.getBoundingClientRect();
    menu.style.display = "block";
    menu.style.top = r.bottom + 6 + "px";
    menu.style.left = Math.max(4, Math.min(innerWidth - menu.offsetWidth - 4, r.right - menu.offsetWidth)) + "px";
  }

  async function send(m) {
    const ok = await chrome.runtime.sendMessage({ type: "download", url: m.url, referrer: location.href, fileName: m.name });
    tip(t(ok ? "popupSent" : "popupFailed"));
  }
})();
