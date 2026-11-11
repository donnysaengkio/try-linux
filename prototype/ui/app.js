// Try Linux's interface (prototype): the same pages on Windows, macOS and Linux. The Rust side (src-tauri)
// answers `systems`, `catalog`, `icons` and `open_page`; nothing here starts or changes a system yet.

const invoke = window.__TAURI__.core.invoke;
const appWindow = window.__TAURI__.window.getCurrentWindow();
let ICONS = {};

// The built-in distributions, shown as Recommended (the Windows edition's Distro.Catalog).
const RECOMMENDED = [
  { id: "fedora", name: "Fedora Workstation", home: "https://fedoraproject.org/",
    about: "A community distribution sponsored by Red Hat that ships new GNOME, kernel and Mesa releases soon after they come out" },
  { id: "ubuntu", name: "Ubuntu Desktop", home: "https://www.ubuntu.com/",
    about: "The most widely used desktop Linux, made by Canonical, with long-term support releases and a large software catalog" },
  { id: "debian", name: "Debian (GNOME)", home: "https://www.debian.org/",
    about: "One of the oldest community distributions, known for stability and the base of Ubuntu, Mint and many others" },
  { id: "mint", name: "Linux Mint (Cinnamon)", home: "https://linuxmint.com/",
    about: "A beginner-friendly distribution based on Ubuntu, with the Windows-like Cinnamon desktop and media codecs available at install" },
  { id: "zorin", name: "Zorin OS Core", home: "https://zorin.com/os/",
    about: "A polished distribution based on Ubuntu, designed to feel familiar to people moving from Windows" },
];
let more = null;   // the catalog from the repository, once loaded

// ---- small helpers
const $ = (sel, root = document) => root.querySelector(sel);
function el(tag, attrs = {}, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") e.className = v;
    else if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
    else if (v !== null && v !== undefined) e.setAttribute(k, v);
  }
  for (const k of kids.flat()) if (k !== null && k !== undefined) e.append(k);
  return e;
}
function icon(name) {
  const span = document.createElement("span");
  span.innerHTML = ICONS[name] || "";
  const svg = span.firstElementChild;
  if (svg) { svg.setAttribute("class", "icon"); svg.removeAttribute("width"); svg.removeAttribute("height"); }
  return svg || span;
}
function iconButton(name, title, onclick) {
  return el("button", { class: "btn icon", title, onclick }, icon(name));
}
function toast(text) {
  const t = $("#toast");
  t.textContent = text; t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.hidden = true), 2600);
}
function header(left, middle, right) {
  $("#header-left").replaceChildren(...(left ? [left] : []));
  $("#header-middle").replaceChildren(...(middle ? [middle] : []));
  $("#header-right").replaceChildren(...(right ? [right] : []));
}
function rateText(bytesPerSecond) {
  if (bytesPerSecond < 1024) return "0 KB/s";
  if (bytesPerSecond < 2 ** 20) return Math.round(bytesPerSecond / 1024) + " KB/s";
  const mb = bytesPerSecond / 2 ** 20;
  return (mb < 10 ? mb.toFixed(1) : Math.round(mb)) + " MB/s";
}
function sizeText(bytes) {
  return bytes >= 2 ** 30 ? (bytes / 2 ** 30).toFixed(1) + " GB" : Math.round(bytes / 2 ** 20) + " MB";
}

// The pixel logo, drawn like the Windows edition's: 5×7 letters, a shadow, and a gradient down the rows.
const GLYPHS = {
  T: ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
  R: ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
  Y: ["#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."],
  L: ["#....", "#....", "#....", "#....", "#....", "#....", "#####"],
  I: ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####"],
  N: ["#...#", "##..#", "##..#", "#.#.#", "#..##", "#..##", "#...#"],
  U: ["#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
  X: ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"],
};
function logo(px = 7) {
  const word = "TRY LINUX", cols = word.length * 6 - 1, shadow = Math.max(1, Math.round(px / 3));
  const css = getComputedStyle(document.documentElement);
  const c = el("canvas", { class: "logo", width: cols * px + shadow, height: 7 * px + shadow });
  const g = c.getContext("2d");
  const mix = (a, b, t) => {
    const p = h => [1, 3, 5].map(i => parseInt(h.trim().slice(i, i + 2), 16));
    const [x, y] = [p(a), p(b)];
    return `rgb(${x.map((v, i) => Math.round(v + (y[i] - v) * t)).join(",")})`;
  };
  const draw = (dx, dy, color) => [...word].forEach((ch, i) => (GLYPHS[ch] || []).forEach((row, r) =>
    [...row].forEach((cell, col) => { if (cell === "#") { g.fillStyle = color(r); g.fillRect(dx + (i * 6 + col) * px, dy + r * px, px, px); } })));
  draw(shadow, shadow, () => css.getPropertyValue("--accent2"));
  draw(0, 0, r => mix(css.getPropertyValue("--highlight"), css.getPropertyValue("--highlight2"), r / 6));
  return c;
}

// ---- Virtual Machines
let liveTimer = null;

async function showHome() {
  clearInterval(liveTimer);
  const vms = await invoke("systems");
  if (vms.length === 0) return showChoose(false);

  const create = el("button", { class: "btn primary", onclick: () => showChoose(true) }, "Create");
  header(create, null, iconButton("menu", "Main menu", () => toast("The main menu isn't in the prototype yet.")));

  const card = el("div", { class: "card" });
  const page = el("div", { class: "column wide home" },
    logo(),
    el("div", { class: "vm-head" },
      el("h1", {}, "Virtual Machines"),
      el("span", { class: "col-label status" }, "STATUS"),
      el("span", { class: "divider", style: "visibility:hidden" }),
      el("span", { class: "col-label stat" }, "CPU"),
      el("span", { class: "col-label stat" }, "MEM"),
      el("span", { class: "col-label stat" }, "DISK"),
      el("span", { class: "actions" })),
    card);
  $("#page").replaceChildren(page);

  const render = list => { if (!document.querySelector(".menu")) card.replaceChildren(...list.map(vmRow)); };
  render(vms);
  refreshHome = async () => render(await invoke("systems"));
  // Running systems' activity, every two seconds while this page is open; at once when one starts or stops.
  liveTimer = setInterval(refreshHome, 2000);
}
let refreshHome = () => {};

// ---- dialogs and menus

// A question with a main action; resolves true when it's chosen. `danger` colours the action red.
function ask(title, text, action, danger = false) {
  return new Promise(done => {
    const close = ok => { back.remove(); done(ok); };
    const back = el("div", { class: "modal-back", onclick: e => { if (e.target === back) close(false); } },
      el("div", { class: "modal" },
        el("div", { class: "modal-title" }, title),
        el("div", { class: "modal-text" }, text),
        el("div", { class: "modal-buttons" },
          action ? el("button", { class: "btn", onclick: () => close(false) }, "Cancel") : null,
          el("button", { class: "btn " + (danger ? "danger" : "primary"), onclick: () => close(true) }, action || "OK"))));
    document.body.append(back);
    back.querySelector(".modal-buttons .btn:last-child").focus();
    back.addEventListener("keydown", e => { if (e.key === "Escape") close(false); });
  });
}
const problem = p => ask(p.title || "Something went wrong", p.text || String(p), null);

// A menu under a button; items are [label, action, danger] or "-" for a line.
function popup(anchor, items) {
  document.querySelectorAll(".menu").forEach(m => m.remove());
  const r = anchor.getBoundingClientRect();
  const menu = el("div", { class: "menu", style: `top:${r.bottom + 4}px;left:${r.left}px;min-width:${r.width}px` },
    items.map(i => i === "-" ? el("div", { class: "menu-line" })
      : el("button", { class: "menu-item" + (i[2] ? " danger" : ""), onclick: () => { menu.remove(); i[1](); } }, i[0])));
  document.body.append(menu);
  setTimeout(() => document.addEventListener("click", function off(e) {
    if (!menu.contains(e.target)) { menu.remove(); document.removeEventListener("click", off); }
  }), 0);
}

const call = (cmd, args) => invoke(cmd, args).catch(problem);

// The Power menu, as on the Windows edition: Shut down and Restart ask Linux; Force stop and Reset lose work.
function powerMenu(anchor, vm) {
  const act = action => call("power", { id: vm.id, action });
  if (vm.state === "held") return popup(anchor, [["Power on", () => act("power-on")], ["Close console", () => act("close-console")]]);
  popup(anchor, [
    ["Show console", () => call("console", { id: vm.id })],
    "-",
    ["Shut down", async () => { if (await ask(`Shut down ${vm.name}?`, "Linux shuts down cleanly. Its desktop may ask first; GNOME goes ahead on its own after 60 seconds.", "Shut down")) act("shutdown"); }],
    ["Restart", async () => { if (await ask(`Restart ${vm.name}?`, "Linux shuts down cleanly, then starts again with any settings you changed. Its desktop may ask first; GNOME goes ahead on its own after 60 seconds.", "Restart")) act("restart"); }],
    vm.state === "paused" ? ["Resume", () => act("resume")] : ["Pause", () => act("pause")],
    "-",
    ["Force stop", async () => { if (await ask(`Force ${vm.name} to stop?`, "This turns it off immediately, like unplugging a PC. Unsaved work in Linux is lost.", "Force stop", true)) act("force"); }, true],
    ["Reset", async () => { if (await ask(`Reset ${vm.name}?`, "This restarts it immediately, like a PC's reset button. Unsaved work in Linux is lost.", "Reset", true)) act("reset"); }, true],
  ]);
}

function vmRow(vm) {
  const stat = (value, sub) => el("div", { class: "stat" }, el("div", { class: "value" }, value), sub ? el("div", { class: "sub" }, sub) : null);
  const cores = vm.cores + (vm.cores === 1 ? " core" : " cores"), ram = vm.ram_gb + " GB";
  const on = vm.state === "running" || vm.state === "paused" || vm.state === "elsewhere";
  const live = on && vm.cpu !== null;
  const elsewhere = vm.state === "elsewhere";
  const startIt = () => call("start", { id: vm.id });
  let go;
  if (vm.state === "off")
    go = el("button", { class: "btn small primary go", onclick: startIt }, vm.status === "Ready to install" ? "Install" : "Start");
  else {
    go = el("button", { class: "btn small go", title: elsewhere ? "Started by another copy of Try Linux" : null },
      "Power", el("span", { class: "chev" }, icon("chevron-down")));
    if (elsewhere) go.disabled = true;
    else go.addEventListener("click", e => { e.stopPropagation(); powerMenu(go, vm); });
  }
  const consoleBtn = el("button", { class: "btn small icon", title: on ? "Show console" : "Open console",
    onclick: () => call("console", { id: vm.id }) }, icon("terminal"));
  if (elsewhere) consoleBtn.disabled = true;
  return el("div", { class: "vm-row", ondblclick: e => { if (vm.state === "off" && !e.target.closest("button")) startIt(); } },
    el("div", { class: "vm-name" }, el("div", { class: "title", title: vm.name }, vm.name), el("div", { class: "os" }, vm.os)),
    el("div", { class: "status" + (on ? " on" : "") }, vm.status),
    el("div", { class: "divider" }),
    live ? stat(Math.round(vm.cpu) + "%", "of " + cores) : stat(cores),
    // What Linux is using once it reports it; until then, what QEMU holds on this PC.
    live ? stat(sizeText(vm.guest_mem ?? vm.mem), "of " + ram) : stat(ram),
    live && vm.disk_rate !== null ? stat(rateText(vm.disk_rate), vm.disk) : stat(vm.disk),
    el("div", { class: "actions" },
      consoleBtn,
      go,
      el("button", { class: "btn small icon", title: "Settings", onclick: () => toast("Settings isn't in the app yet.") }, icon("settings"))));
}

// ---- Choose a Linux OS (the first step of Create)
function showChoose(hasSystems) {
  clearInterval(liveTimer);
  refreshHome = () => {};
  header(null, null, null);
  let chosen = RECOMMENDED[0];

  const search = el("input", { class: "search", placeholder: "Search distributions, e.g. KDE, Xfce or Ubuntu", spellcheck: "false" });
  const heading = el("div", { class: "heading" }, "Recommended");
  const list = el("div", { class: "box list" });
  const own = el("div", { class: "option" },
    el("div", { class: "text" }, el("div", { class: "title" }, "Use Your Own Installer"),
      el("div", { class: "about" }, "An existing virtual disk to import, an .iso file on this PC, or a URL link to one")));
  own.addEventListener("click", () => { chosen = "own"; mark(); });

  const mark = () => {
    list.querySelectorAll(".option").forEach(o => o.classList.toggle("selected", o.distro === chosen));
    own.classList.toggle("selected", chosen === "own");
  };
  const option = d => {
    const o = el("div", { class: "option" },
      el("div", { class: "text" }, el("div", { class: "title" }, d.name), el("div", { class: "about" }, d.about || "")),
      d.home ? el("span", { class: "link", onclick: e => { e.stopPropagation(); invoke("open_page", { url: d.home }); } }, "Website ↗") : null);
    o.distro = d;
    o.addEventListener("click", () => { chosen = d; mark(); });
    return o;
  };
  // A search covers the built-in five and the repository's list, by name, description or id; names that start
  // with the text come first.
  const refresh = () => {
    const q = search.value.trim().toLowerCase(), words = q.split(/\s+/).filter(Boolean);
    const all = RECOMMENDED.concat((more || []).filter(d => !RECOMMENDED.some(r => r.id === d.id)));
    const shown = words.length === 0 ? RECOMMENDED
      : all.filter(d => words.every(w => (d.name + " " + (d.about || "") + " " + d.id).toLowerCase().includes(w)))
           .sort((a, b) => b.name.toLowerCase().startsWith(q) - a.name.toLowerCase().startsWith(q));
    heading.textContent = words.length === 0 ? "Recommended" : shown.length + (shown.length === 1 ? " result" : " results");
    list.replaceChildren(...(shown.length ? shown.map(option)
      : [el("div", { class: "empty" }, `No distribution matches "${search.value.trim()}".`, el("br"), "Try another name, or use your own installer below.")]));
    if (chosen !== "own") chosen = shown[0] || null;
    mark();
  };
  search.addEventListener("input", refresh);

  const back = hasSystems ? el("button", { class: "btn", onclick: showHome }, "Back") : el("span");
  const next = el("button", { class: "btn primary", onclick: () => toast(
    chosen === "own" ? "Using your own installer isn't in the prototype yet."
      : chosen ? "Creating " + chosen.name + " isn't in the prototype yet." : "Choose a distribution first.") }, "Next");

  $("#page").replaceChildren(el("div", { class: "column" },
    logo(),
    el("h1", { class: "center" }, "Choose a Linux OS"),
    el("div", { class: "subtitle" }, "Try Linux downloads the official installer and checks it before use."),
    search, heading, list,
    el("div", { class: "box", style: "margin-top:12px" }, own),
    el("div", { class: "nav" }, back, next)));
  refresh();
  search.focus();

  if (!more) invoke("catalog").then(text => {
    try { more = (JSON.parse(text).distros || []).filter(d => d.id && d.name); } catch { more = []; }
    if (search.value.trim()) refresh();
  }).catch(() => (more = []));
}

// ---- start
(async () => {
  ICONS = await invoke("icons");
  const close = $("#close");
  close.replaceChildren(icon("x"));
  close.addEventListener("click", () => appWindow.close());
  // From the Rust side: a system started or stopped, or something went wrong (e.g. QEMU couldn't start).
  window.__TAURI__.event.listen("systems-changed", () => refreshHome());
  window.__TAURI__.event.listen("problem", e => problem(e.payload));
  // TRYLINUX_PAGE=choose opens Choose a Linux OS first (for screenshots).
  if ((await invoke("start_page")) === "choose") showChoose(true); else await showHome();
})();
