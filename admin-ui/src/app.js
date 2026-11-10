// WorldBox Multiplayer server admin. Plain JS, no build step beyond Tailwind.
// Everything user-supplied goes through esc() before it touches innerHTML.

const $ = (s, el = document) => el.querySelector(s);
const esc = (v) => String(v ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

const state = { view: "overview", overview: null, accounts: null, bans: null, q: "", timer: null };

// ── api ────────────────────────────────────────────────────────────────
async function api(path, opts = {}) {
  const res = await fetch("/api" + path, {
    method: opts.method || "GET",
    headers: { "Content-Type": "application/json", "X-Wbmp": "1" },
    body: opts.body ? JSON.stringify(opts.body) : undefined,
    credentials: "same-origin",
  });
  let data = {};
  try { data = await res.json(); } catch { /* empty body */ }
  if (res.status === 401 && path !== "/login") { showLogin(); throw new Error("Signed out"); }
  if (!res.ok) throw new Error(data.error || `Request failed (${res.status})`);
  return data;
}
const post = (path, body = {}) => api(path, { method: "POST", body });

// ── theme ──────────────────────────────────────────────────────────────
function applyTheme(t) {
  document.documentElement.classList.toggle("dark", t === "dark");
  $("#theme-label").textContent = t === "dark" ? "Light mode" : "Dark mode";
}
let theme = localStorage.getItem("wbmp-theme") || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");

// ── helpers ────────────────────────────────────────────────────────────
const palette = ["bg-violet-500", "bg-rose-500", "bg-emerald-500", "bg-amber-500", "bg-sky-500", "bg-fuchsia-500", "bg-teal-500", "bg-orange-500"];
function avatar(name, size = "") {
  let h = 0;
  for (const c of String(name)) h = (h * 31 + c.charCodeAt(0)) >>> 0;
  const initials = String(name).replace(/[^A-Za-z0-9]/g, "").slice(0, 2).toUpperCase() || "?";
  return `<span class="avatar ${palette[h % palette.length]} ${size}">${esc(initials)}</span>`;
}
function ago(iso) {
  if (!iso) return "never";
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86400)} d ago`;
}
function since(iso) {
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 3600) return `${Math.max(1, Math.floor(s / 60))} min`;
  return `${Math.floor(s / 3600)} h ${Math.floor((s % 3600) / 60)} min`;
}
function uptime(sec) {
  const d = Math.floor(sec / 86400), h = Math.floor((sec % 86400) / 3600), m = Math.floor((sec % 3600) / 60);
  return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : `${m}m`;
}
const rolePill = (role) =>
  role === "King" ? `<span class="pill-violet">Owner</span>` :
  role === "Admin" ? `<span class="pill-blue">Room admin</span>` :
  role ? `<span class="pill-gray">Player</span>` : "";

function toast(msg, bad = false) {
  const el = document.createElement("div");
  el.className = `card flex items-center gap-2 px-4 py-3 text-sm font-medium shadow-pop ${bad ? "text-rose-600 dark:text-rose-400" : ""}`;
  el.innerHTML = `<span class="size-2 rounded-full ${bad ? "bg-rose-500" : "bg-emerald-500"}"></span>${esc(msg)}`;
  $("#toasts").append(el);
  setTimeout(() => el.remove(), 3800);
}

// ── modal ──────────────────────────────────────────────────────────────
let modalResolve = null;
function modal({ title, text = "", body = "", ok = "Confirm", danger = false }) {
  $("#modal-title").textContent = title;
  $("#modal-text").textContent = text;
  $("#modal-body").innerHTML = body;
  const okBtn = $("#modal-ok");
  okBtn.textContent = ok;
  okBtn.className = danger ? "btn bg-rose-500 text-white hover:bg-rose-600" : "btn-primary";
  $("#modal").classList.replace("hidden", "grid");
  setTimeout(() => ($("#modal-body input, #modal-body select") || okBtn).focus(), 30);
  return new Promise((r) => (modalResolve = r));
}
function closeModal(result) {
  $("#modal").classList.replace("grid", "hidden");
  const r = modalResolve; modalResolve = null;
  r && r(result);
}
function formValues() {
  const out = {};
  document.querySelectorAll("#modal-body [name]").forEach((el) => (out[el.name] = el.value));
  return out;
}
const reasonField = (placeholder = "Optional, the player sees this") =>
  `<div><label class="label">Reason</label><input name="reason" class="field" maxlength="200" placeholder="${esc(placeholder)}"></div>`;
const durationField = () => `
  <div><label class="label">Duration</label>
    <select name="hours" class="field">
      <option value="1">1 hour</option><option value="24">1 day</option>
      <option value="168">1 week</option><option value="720">30 days</option>
      <option value="" selected>Permanent</option>
    </select></div>`;

// ── views ──────────────────────────────────────────────────────────────
const views = {
  overview: { kicker: "Server", title: "Overview", render: renderOverview, load: loadOverview },
  players: { kicker: "Players", title: "Online now", render: renderPlayers, load: loadOverview },
  accounts: { kicker: "Players", title: "Accounts", render: renderAccounts, load: loadAccounts },
  rooms: { kicker: "World", title: "Rooms & lobbies", render: renderRooms, load: loadOverview },
  bans: { kicker: "Moderation", title: "Bans", render: renderBans, load: loadBans },
  settings: { kicker: "You", title: "Settings", render: renderSettings, load: async () => {} },
};

async function loadOverview() { state.overview = await api("/overview"); }
async function loadAccounts() { state.accounts = await api("/accounts?q=" + encodeURIComponent(state.q)); }
async function loadBans() { state.bans = await api("/bans"); }

async function go(view) {
  state.view = view;
  history.replaceState(null, "", "#" + view);
  document.querySelectorAll("[data-view]").forEach((b) => b.classList.toggle("active", b.dataset.view === view));
  $("#view-kicker").textContent = views[view].kicker;
  $("#view-title").textContent = views[view].title;
  await refresh();
}

async function refresh() {
  const v = views[state.view];
  try {
    await v.load();
    $("#live-text").textContent = "Live";
    v.render();
  } catch (e) {
    if (e.message !== "Signed out") $("#live-text").textContent = "Offline, retrying";
  }
}

function stat(label, value, hint, accent) {
  return `<div class="card p-5">
    <div class="flex items-center justify-between"><span class="text-sm font-medium text-muted">${label}</span><span class="size-2.5 rounded-full ${accent}"></span></div>
    <div class="mt-3 text-3xl font-bold tracking-tight">${value}</div>
    <div class="mt-1 text-xs text-muted">${hint}</div></div>`;
}

function renderOverview() {
  const o = state.overview;
  const busiest = o.rooms.filter((r) => r.count > 0).slice(0, 5);
  $("#view").innerHTML = `
    <div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      ${stat("Players online", o.online.length, `${o.connections} open connections`, "bg-emerald-500")}
      ${stat("Rooms", o.rooms.length, `${o.rooms.filter((r) => r.lobby).length} server lobbies`, "bg-violet-500")}
      ${stat("Active bans", o.bans, "accounts and addresses", "bg-rose-500")}
      ${stat("Uptime", uptime(o.uptimeSeconds), `port ${o.port} · ${esc(o.protocol)}`, "bg-sky-500")}
    </div>
    <div class="mt-4 grid items-start gap-4 xl:grid-cols-5">
      <form id="announce" class="card p-6 xl:col-span-2">
        <h2 class="text-lg font-bold tracking-tight">Announcement</h2>
        <p class="mt-1 text-sm text-muted">Shows up on screen for everyone who is signed in.</p>
        <textarea name="text" rows="3" maxlength="200" class="field mt-4 resize-none" placeholder="Server restarts in 5 minutes, finish your wars."></textarea>
        <div class="mt-3 flex justify-end"><button class="btn-primary">Send to ${o.online.length} player${o.online.length === 1 ? "" : "s"}</button></div>
      </form>
      <div class="card xl:col-span-3">
        <div class="flex items-center justify-between px-6 pt-6 pb-2"><h2 class="text-lg font-bold tracking-tight">Busiest rooms</h2>
          <button class="btn-ghost text-xs" data-goto="rooms">All rooms</button></div>
        ${busiest.length ? busiest.map(roomRow).join("") : `<p class="px-6 pb-6 text-sm text-muted">Nobody is playing right now.</p>`}
      </div>
    </div>`;
}

function roomRow(r) {
  return `<div class="row">
    <div class="min-w-0 flex-1"><div class="truncate font-semibold">${esc(r.name)}</div>
      <div class="text-xs text-muted">${r.king ? "Owner " + esc(r.king) : "No owner yet"}</div></div>
    ${stack(r.members)}
    <span class="chip">${r.count}${r.max ? "/" + r.max : ""}</span></div>`;
}

function stack(members) {
  const shown = members.slice(0, 4).map((m) => avatar(m.username, "size-8 text-xs -ml-2 first:ml-0")).join("");
  const more = members.length > 4 ? `<span class="avatar -ml-2 size-8 bg-gray-200 text-xs text-gray-600 dark:bg-white/10 dark:text-gray-300">+${members.length - 4}</span>` : "";
  return `<div class="flex items-center">${shown}${more}</div>`;
}

function renderPlayers() {
  const list = state.overview.online.filter((p) => p.username.toLowerCase().includes(state.q.toLowerCase()));
  $("#view").innerHTML = `
    <div class="card">
      <div class="flex flex-wrap items-center gap-4 px-6 pt-5">
        <div class="flex gap-6 border-b border-line dark:border-night-line">
          <span class="tab active">Signed in<span class="count">${state.overview.online.length}</span></span>
        </div>
        ${searchBox("Search players")}
      </div>
      <div class="mt-2">${list.length ? list.map(playerRow).join("") : empty("No players online.")}</div>
    </div>`;
}

function playerRow(p) {
  return `<div class="row flex-wrap">
    ${avatar(p.username)}
    <div class="min-w-0 flex-1">
      <div class="flex items-center gap-2"><span class="truncate font-semibold">${esc(p.username)}</span>${rolePill(p.role)}</div>
      <div class="text-xs text-muted">${p.room ? `in ${esc(p.roomName)} <span class="chip ml-1">${esc(p.room)}</span>` : "In the menu"}</div>
    </div>
    <div class="hidden text-right text-xs text-muted md:block"><div class="font-mono">${esc(p.ip)}</div><div>${p.pingMs} ms · ${since(p.connectedAt)}</div></div>
    <div class="flex gap-1">
      ${p.room ? `<button class="btn-ghost" data-act="kick" data-id="${esc(p.id4)}" data-name="${esc(p.username)}">Kick</button>` : ""}
      <button class="btn-ghost" data-act="disconnect" data-id="${esc(p.id4)}" data-name="${esc(p.username)}">Disconnect</button>
      <button class="btn-danger" data-act="ban" data-id="${esc(p.id4)}" data-name="${esc(p.username)}" data-ip="${esc(p.ip)}">Ban</button>
    </div></div>`;
}

function renderAccounts() {
  const a = state.accounts;
  const online = new Set((state.overview?.online || []).map((p) => p.id4));
  $("#view").innerHTML = `
    <div class="card">
      <div class="flex flex-wrap items-center gap-4 px-6 pt-5">
        <div class="flex gap-6 border-b border-line dark:border-night-line"><span class="tab active">All accounts<span class="count">${a.total}</span></span></div>
        ${searchBox("Search by username")}
      </div>
      <div class="mt-2">${a.accounts.length ? a.accounts.map((p) => `
        <div class="row flex-wrap">
          ${avatar(p.username)}
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-2"><span class="truncate font-semibold">${esc(p.username)}</span>${online.has(p.id4) ? `<span class="pill-green">Online</span>` : ""}</div>
            <div class="text-xs text-muted">Joined ${new Date(p.createdAt).toLocaleDateString()} · last seen ${ago(p.lastLogin)}</div>
          </div>
          <div class="flex gap-1">
            <button class="btn-ghost" data-act="signout" data-id="${esc(p.id4)}" data-name="${esc(p.username)}">Sign out</button>
            <button class="btn-ghost" data-act="password" data-id="${esc(p.id4)}" data-name="${esc(p.username)}">Reset password</button>
            <button class="btn-danger" data-act="ban" data-id="${esc(p.id4)}" data-name="${esc(p.username)}">Ban</button>
          </div></div>`).join("") : empty(state.q ? "No accounts match." : "No accounts yet.")}
      </div>
      ${a.accounts.length >= 100 ? `<p class="px-6 pb-5 text-xs text-muted">Showing the newest 100. Search to narrow it down.</p>` : ""}
    </div>`;
}

function renderRooms() {
  const rooms = state.overview.rooms;
  $("#view").innerHTML = `<div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">${rooms.map((r) => `
    <div class="card flex flex-col p-6">
      <div class="flex items-start justify-between gap-3">
        <div class="min-w-0"><h3 class="truncate text-lg font-bold tracking-tight">${esc(r.name)}</h3>
          <div class="mt-1 flex flex-wrap items-center gap-1.5">
            <span class="chip">${esc(r.id)}</span>
            ${r.lobby ? `<span class="pill-violet">Server lobby</span>` : r.visibility === "Public" ? `<span class="pill-green">Public</span>` : `<span class="pill-gray">Unlisted</span>`}
            ${r.hasPassword ? `<span class="pill-rose">Password</span>` : ""}
          </div></div>
        <span class="text-sm font-semibold text-muted">${r.count}${r.max ? "/" + r.max : ""}</span>
      </div>
      <div class="mt-5 space-y-2">${r.members.length ? r.members.map((m) => `
        <div class="flex items-center gap-2.5">${avatar(m.username, "size-7 text-[11px]")}<span class="truncate text-sm font-medium">${esc(m.username)}</span>${m.role === "King" ? `<span class="pill-violet ml-auto">Owner</span>` : m.role === "Admin" ? `<span class="pill-blue ml-auto">Admin</span>` : ""}</div>`).join("")
        : `<p class="text-sm text-muted">Empty${r.lobby ? ", the next player in holds the world." : "."}</p>`}</div>
      ${r.count ? `<button class="btn-danger mt-5 self-start" data-act="close" data-id="${esc(r.id)}" data-name="${esc(r.name)}">${r.lobby ? "Clear lobby" : "Close room"}</button>` : ""}
    </div>`).join("")}</div>`;
}

function renderBans() {
  const bans = state.bans;
  $("#view").innerHTML = `
    <div class="grid gap-4 xl:grid-cols-3">
      <div class="card xl:col-span-2">
        <div class="flex items-center gap-4 px-6 pt-5"><div class="flex gap-6 border-b border-line dark:border-night-line"><span class="tab active">All bans<span class="count">${bans.length}</span></span></div></div>
        <div class="mt-2">${bans.length ? bans.map((b) => {
          const expired = b.expiresAt && new Date(b.expiresAt) < new Date();
          return `<div class="row">
            <span class="${b.kind === "ip" ? "pill-rose" : "pill-violet"}">${b.kind === "ip" ? "Address" : "Account"}</span>
            <div class="min-w-0 flex-1"><div class="truncate font-semibold ${b.kind === "ip" ? "font-mono text-sm" : ""}">${esc(b.label || b.value)}</div>
              <div class="truncate text-xs text-muted">${b.reason ? esc(b.reason) + " · " : ""}${expired ? "expired" : b.expiresAt ? "until " + new Date(b.expiresAt).toLocaleString() : "permanent"}</div></div>
            <button class="btn-ghost" data-act="unban" data-id="${esc(b.id)}">Lift</button></div>`;
        }).join("") : empty("Nobody is banned.")}</div>
      </div>
      <form id="ban-ip" class="card h-fit p-6">
        <h2 class="text-lg font-bold tracking-tight">Ban an address</h2>
        <p class="mt-1 text-sm text-muted">Blocks new connections from this IP.</p>
        <div class="mt-4 space-y-4">
          <div><label class="label">IP address</label><input name="value" class="field font-mono" required placeholder="203.0.113.7"></div>
          <div><label class="label">Reason</label><input name="reason" class="field" maxlength="200" placeholder="Optional"></div>
          ${durationField()}
        </div>
        <button class="btn-primary mt-5 w-full">Ban address</button>
      </form>
    </div>`;
}

function renderSettings() {
  $("#view").innerHTML = `
    <form id="admin-pass" class="card max-w-md p-6">
      <h2 class="text-lg font-bold tracking-tight">Change your password</h2>
      <p class="mt-1 text-sm text-muted">Signed in as ${esc(state.user)}.</p>
      <div class="mt-5 space-y-4">
        <div><label class="label">Current password</label><input name="current" type="password" class="field" required autocomplete="current-password"></div>
        <div><label class="label">New password</label><input name="next" type="password" class="field" required minlength="8" autocomplete="new-password"></div>
      </div>
      <button class="btn-primary mt-5">Save password</button>
    </form>`;
}

const searchBox = (ph) => `
  <label class="relative ml-auto w-full max-w-xs">
    <svg class="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>
    <input id="search" class="field pl-9" placeholder="${esc(ph)}" value="${esc(state.q)}">
  </label>`;
const empty = (t) => `<p class="px-6 py-10 text-center text-sm text-muted">${esc(t)}</p>`;

// ── actions ────────────────────────────────────────────────────────────
async function act(btn) {
  const { act: kind, id, name, ip } = btn.dataset;
  try {
    if (kind === "kick") {
      if (!(await modal({ title: `Kick ${name} from the room?`, text: "They stay connected and land back in the lobby list.", body: reasonField(), ok: "Kick" }))) return;
      await post(`/players/${id}/kick`, { reason: formValues().reason }); toast(`${name} was kicked.`);
    } else if (kind === "disconnect") {
      if (!(await modal({ title: `Disconnect ${name}?`, text: "Their game reconnects on its own unless they are banned.", body: reasonField(), ok: "Disconnect" }))) return;
      await post(`/players/${id}/disconnect`, { reason: formValues().reason }); toast(`${name} was disconnected.`);
    } else if (kind === "ban") {
      const body = `${ip ? `<div><label class="label">Ban</label><select name="kind" class="field">
          <option value="account">The account ${esc(name)}</option><option value="ip">The address ${esc(ip)}</option></select></div>` : ""}
        ${reasonField("Optional, shown when they try to connect")}${durationField()}`;
      if (!(await modal({ title: `Ban ${name}?`, text: "They are disconnected right away.", body, ok: "Ban", danger: true }))) return;
      const v = formValues();
      const byIp = v.kind === "ip";
      await post("/bans", { kind: byIp ? "ip" : "account", value: byIp ? ip : id, label: byIp ? ip : name, reason: v.reason, hours: v.hours ? Number(v.hours) : null });
      toast(`${byIp ? ip : name} is banned.`);
    } else if (kind === "signout") {
      if (!(await modal({ title: `Sign ${name} out everywhere?`, text: "Forgets every saved login. They need their password next time." , ok: "Sign out" }))) return;
      await post(`/accounts/${id}/signout`); toast(`${name} was signed out.`);
    } else if (kind === "password") {
      if (!(await modal({ title: `New password for ${name}`, text: "Saved logins are cleared too.", body: `<div><label class="label">New password</label><input name="password" class="field" minlength="4" maxlength="64" required></div>`, ok: "Set password" }))) return;
      await post(`/accounts/${id}/password`, { password: formValues().password }); toast("Password updated.");
    } else if (kind === "close") {
      if (!(await modal({ title: `Empty ${name}?`, text: "Everyone inside goes back to the room list.", body: reasonField(), ok: "Close", danger: true }))) return;
      await post(`/rooms/${id}/close`, { reason: formValues().reason }); toast(`${name} was closed.`);
    } else if (kind === "unban") {
      await api(`/bans/${id}`, { method: "DELETE" }); toast("Ban lifted.");
    }
    await refresh();
  } catch (e) { toast(e.message, true); }
}

// ── auth / boot ────────────────────────────────────────────────────────
function showLogin() {
  clearInterval(state.timer);
  $("#app").classList.add("hidden");
  $("#login").classList.replace("hidden", "grid");
  $("#login-pass").focus();
}
async function showApp(user) {
  state.user = user;
  $("#login").classList.replace("grid", "hidden");
  $("#app").classList.remove("hidden");
  const start = location.hash.slice(1);
  await go(views[start] ? start : "overview");
  clearInterval(state.timer);
  state.timer = setInterval(() => {
    if (["overview", "players", "rooms"].includes(state.view) && !modalResolve && !document.activeElement?.matches("input, textarea, select")) refresh();
  }, 5000);
}

document.addEventListener("DOMContentLoaded", async () => {
  applyTheme(theme);
  $("#theme-toggle").addEventListener("click", () => { theme = theme === "dark" ? "light" : "dark"; localStorage.setItem("wbmp-theme", theme); applyTheme(theme); });
  $("#sign-out").addEventListener("click", async () => { await post("/logout").catch(() => {}); showLogin(); });
  document.querySelectorAll("[data-view]").forEach((b) => b.addEventListener("click", () => { state.q = ""; go(b.dataset.view); }));
  $("#modal-cancel").addEventListener("click", () => closeModal(false));
  $("#modal").addEventListener("click", (e) => { if (e.target.id === "modal") closeModal(false); });
  $("#modal-form").addEventListener("submit", (e) => { e.preventDefault(); closeModal(true); });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape" && modalResolve) closeModal(false); });

  $("#view").addEventListener("click", (e) => {
    const b = e.target.closest("[data-act]"); if (b) return act(b);
    const g = e.target.closest("[data-goto]"); if (g) go(g.dataset.goto);
  });
  let searchTimer;
  $("#view").addEventListener("input", (e) => {
    if (e.target.id !== "search") return;
    state.q = e.target.value;
    clearTimeout(searchTimer);
    searchTimer = setTimeout(async () => {
      const pos = e.target.selectionStart;
      await refresh();
      const s = $("#search"); if (s) { s.focus(); s.setSelectionRange(pos, pos); }
    }, state.view === "accounts" ? 250 : 0);
  });
  $("#view").addEventListener("submit", async (e) => {
    e.preventDefault();
    const f = Object.fromEntries(new FormData(e.target));
    try {
      if (e.target.id === "announce") {
        if (!f.text.trim()) return;
        const r = await post("/announce", { text: f.text }); toast(`Sent to ${r.sent} player${r.sent === 1 ? "" : "s"}.`); e.target.reset();
      } else if (e.target.id === "ban-ip") {
        await post("/bans", { kind: "ip", value: f.value, label: f.value, reason: f.reason, hours: f.hours ? Number(f.hours) : null });
        toast(`${f.value} is banned.`); await refresh();
      } else if (e.target.id === "admin-pass") {
        await post("/admin/password", f); toast("Password changed."); e.target.reset();
      }
    } catch (err) { toast(err.message, true); }
  });

  $("#login-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const err = $("#login-error"); err.classList.add("hidden");
    try {
      const r = await post("/login", { username: $("#login-user").value, password: $("#login-pass").value });
      $("#login-pass").value = "";
      await showApp(r.user);
    } catch (ex) { err.textContent = ex.message; err.classList.remove("hidden"); }
  });

  try { const me = await api("/me"); await showApp(me.user); } catch { showLogin(); }
});
