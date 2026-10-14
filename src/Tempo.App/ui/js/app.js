/* ===================== Tempo frontend ===================== */
"use strict";

/* ---------- bridge ---------- */
const isHost = !!(window.chrome && window.chrome.webview);
const pending = new Map();
let uid = 0;

function call(method, params = {}) {
  if (!isHost) return mockCall(method, params);
  return new Promise((resolve, reject) => {
    const id = "m" + (++uid);
    pending.set(id, { resolve, reject });
    window.chrome.webview.postMessage({ id, method, params });
  });
}

if (isHost) {
  window.chrome.webview.addEventListener("message", (e) => {
    const m = e.data;
    if (!m) return;
    if (m.evt) { onEvent(m.evt, m.data); return; }
    const p = pending.get(m.id);
    if (!p) return;
    pending.delete(m.id);
    if (m.ok) p.resolve(m.result); else p.reject(new Error(m.error || "Unknown error"));
  });
}

function onEvent(evt, data) {
  if (evt === "metrics") updateMetrics(data);
  else if (evt === "progress") progressLog(data.msg);
}

/* ---------- state ---------- */
let SYS = null;
let OPTS = [];
let BLOAT = [];
let BLOAT_SUMMARY = null;
let STARTUP = [];
const optSel = new Set();
const bloatSel = new Set();
const openCats = new Set();
let bloatFilter = "all";
let bloatGroup = "all";
let bloatSort = "smart";
let bloatSearch = "";

const CATEGORIES = [
  ["gaming", "🎮 Gaming features"],
  ["fps", "⚡ FPS & frame-time"],
  ["power", "🔋 Power"],
  ["cpu", "🧠 CPU"],
  ["gpu", "🖥️ GPU"],
  ["memory", "▤ RAM & memory"],
  ["storage", "💾 Storage"],
  ["network", "🌐 Network & online play"],
  ["input", "🖱️ Input responsiveness"],
  ["visuals", "✨ Windows responsiveness"],
  ["background", "🔕 Background & privacy"],
  ["maintenance", "🧰 Windows maintenance"],
  ["advanced", "⚠️ Advanced"],
];

/* ---------- utils ---------- */
const $ = (s) => document.querySelector(s);
const $$ = (s) => [...document.querySelectorAll(s)];
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
function gb(bytes) { return bytes > 0 ? (bytes / 1073741824).toFixed(1).replace(/\.0$/, "") + " GB" : "—"; }
function mb(bytes) {
  if (!bytes) return "—";
  if (bytes >= 1073741824) return (bytes / 1073741824).toFixed(2) + " GB";
  if (bytes >= 1048576) return Math.round(bytes / 1048576) + " MB";
  return Math.max(1, Math.round(bytes / 1024)) + " KB";
}

function toast(title, msg, type = "ok") {
  const el = document.createElement("div");
  el.className = "toast " + (type === "ok" ? "" : type);
  el.innerHTML = `<b>${esc(title)}</b>${esc(msg)}`;
  $("#toast-wrap").appendChild(el);
  setTimeout(() => { el.style.opacity = "0"; el.style.transition = "opacity .4s"; setTimeout(() => el.remove(), 450); }, 6000);
}

function confirmModal(title, bodyHtml, okLabel = "Confirm", danger = true) {
  return new Promise((resolve) => {
    $("#modal-title").textContent = title;
    $("#modal-body").innerHTML = bodyHtml;
    const ok = $("#modal-ok");
    ok.textContent = okLabel;
    ok.className = "btn " + (danger ? "danger" : "primary");
    $("#modal-back").style.display = "flex";
    const done = (v) => { $("#modal-back").style.display = "none"; ok.onclick = null; $("#modal-cancel").onclick = null; resolve(v); };
    ok.onclick = () => done(true);
    $("#modal-cancel").onclick = () => done(false);
  });
}

let progressHidden = false;
function progressOpen(title) {
  progressHidden = false;
  $("#progress-title").textContent = title;
  $("#progress-fill").style.width = "6%";
  $("#progress-log").innerHTML = "";
  $("#progress-back").style.display = "flex";
}
$("#progress-hide").addEventListener("click", () => {
  progressHidden = true;
  $("#progress-back").style.display = "none";
  toast("Still working in the background", "You'll get a notification here when it finishes.", "warn");
});
let progCount = 0, progTotal = 1;
function progressSetTotal(n) { progTotal = Math.max(1, n); progCount = 0; }
function progressLog(msg, cls = "") {
  if ($("#progress-back").style.display === "none") return;
  const log = $("#progress-log");
  const line = document.createElement("div");
  if (cls) line.className = cls;
  line.textContent = msg;
  log.appendChild(line);
  log.scrollTop = log.scrollHeight;
  if (!cls) { progCount++; $("#progress-fill").style.width = Math.min(96, 6 + (progCount / progTotal) * 90) + "%"; }
}
function progressClose(delay = 700) {
  $("#progress-fill").style.width = "100%";
  setTimeout(() => { $("#progress-back").style.display = "none"; }, delay);
}

/* ---------- navigation ---------- */
$$(".nav-btn").forEach((b) => b.addEventListener("click", () => gotoPage(b.dataset.page)));
$$("[data-goto]").forEach((b) => b.addEventListener("click", () => gotoPage(b.dataset.goto)));
function gotoPage(name) {
  $$(".nav-btn").forEach((b) => b.classList.toggle("active", b.dataset.page === name));
  $$(".page").forEach((p) => p.classList.toggle("active", p.id === "page-" + name));
  if (name === "report") renderReport();
  if (name === "history") refreshHistory();
  if (name === "startup" && STARTUP.length === 0) refreshStartup();
}

/* ---------- live metrics ---------- */
const cpuHistory = [];
const RING_LEN = 2 * Math.PI * 52;

function setRing(id, txtId, pct) {
  const v = Math.max(0, Math.min(100, pct));
  $("#" + id).style.strokeDashoffset = RING_LEN * (1 - v / 100);
  $("#" + txtId).textContent = Math.round(v) + "%";
}

function updateMetrics(m) {
  setRing("ring-cpu", "ring-cpu-txt", m.cpuPercent);
  setRing("ring-gpu", "ring-gpu-txt", m.gpuPercent);
  setRing("ring-ram", "ring-ram-txt", m.ramPercent);
  if (m.ramTotalBytes > 0) $("#ram-foot").textContent = gb(m.ramUsedBytes) + " / " + gb(m.ramTotalBytes);
  $("#vram-foot").textContent = m.vramTotalBytes > 0 ? "VRAM " + gb(m.vramUsedBytes) + " / " + gb(m.vramTotalBytes) : "VRAM " + gb(m.vramUsedBytes);
  $("#proc-foot").textContent = "Processes: " + (m.processCount || "—");
  cpuHistory.push(m.cpuPercent);
  if (cpuHistory.length > 60) cpuHistory.shift();
  drawCpuChart();
}

function drawCpuChart() {
  const c = $("#cpu-chart");
  const ctx = c.getContext("2d");
  const w = c.width, h = c.height;
  ctx.clearRect(0, 0, w, h);
  ctx.strokeStyle = "rgba(125,141,160,.12)";
  ctx.lineWidth = 1;
  for (let i = 1; i <= 3; i++) { ctx.beginPath(); ctx.moveTo(0, (h / 4) * i); ctx.lineTo(w, (h / 4) * i); ctx.stroke(); }
  if (cpuHistory.length < 2) return;
  const step = w / 59;
  const pts = cpuHistory.map((v, i) => [i * step + (60 - cpuHistory.length) * step, h - (v / 100) * (h - 8) - 4]);
  const grad = ctx.createLinearGradient(0, 0, 0, h);
  grad.addColorStop(0, "rgba(46,230,166,.28)");
  grad.addColorStop(1, "rgba(46,230,166,0)");
  ctx.beginPath();
  ctx.moveTo(pts[0][0], h);
  pts.forEach(([x, y]) => ctx.lineTo(x, y));
  ctx.lineTo(pts[pts.length - 1][0], h);
  ctx.closePath();
  ctx.fillStyle = grad;
  ctx.fill();
  ctx.beginPath();
  pts.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
  ctx.strokeStyle = "#2ee6a6";
  ctx.lineWidth = 2;
  ctx.shadowColor = "rgba(46,230,166,.6)";
  ctx.shadowBlur = 6;
  ctx.stroke();
  ctx.shadowBlur = 0;
  const [lx, ly] = pts[pts.length - 1];
  ctx.beginPath(); ctx.arc(lx, ly, 3.5, 0, 7); ctx.fillStyle = "#2ee6a6"; ctx.fill();
}

/* ---------- dashboard ---------- */
function renderDashboard() {
  if (!SYS) return;
  $("#dash-sub").textContent = `${SYS.machineName} · ${SYS.os.name} ${SYS.os.displayVersion} · ${SYS.isLaptop ? "Laptop" : "Desktop"}`;
  $("#cpu-name-mini").textContent = SYS.cpu.name.replace(/\(R\)|\(TM\)|CPU|Processor/g, "").trim();

  const rows = [];
  rows.push(hwRow("⚙️", SYS.cpu.name, `${SYS.cpu.cores} cores / ${SYS.cpu.threads} threads · ${(SYS.cpu.maxClockMhz / 1000).toFixed(1)} GHz base clock`, SYS.cpu.vendor));
  for (const g of SYS.gpus) {
    let d = [];
    if (g.vramBytes > 0) d.push(gb(g.vramBytes) + " VRAM");
    if (g.driverVersion) d.push("driver " + g.driverVersion + (g.driverAgeDays >= 0 ? ` (${g.driverAgeDays}d old)` : ""));
    if (g.resolution) d.push(g.resolution + "@" + g.refreshRate + "Hz");
    rows.push(hwRow("🎮", g.name, d.join(" · "), g.vendor));
  }
  rows.push(hwRow("▤", gb(SYS.ram.totalBytes) + " RAM", `${SYS.ram.sticks} stick(s) · ${SYS.ram.speedMhz} MT/s · ${SYS.ram.channels}`, ""));
  for (const d of SYS.drives) {
    const freePct = d.totalBytes > 0 ? Math.round((d.freeBytes / d.totalBytes) * 100) : 0;
    rows.push(hwRow("💾", `${d.letter} ${d.label || ""} — ${d.mediaType}`, `${gb(d.freeBytes)} free of ${gb(d.totalBytes)} (${freePct}%) · health: ${d.health}${d.isSystemDrive ? " · system drive" : ""}`, ""));
  }
  if (SYS.motherboard) rows.push(hwRow("🧩", SYS.motherboard, `${SYS.os.name} ${SYS.os.displayVersion} build ${SYS.os.build} · power plan: ${SYS.activePowerPlan}`, ""));
  $("#hw-list").innerHTML = rows.join("");

  const adv = [];
  const oldGpu = SYS.gpus.find((g) => g.driverAgeDays > 120);
  if (oldGpu) {
    const url = oldGpu.vendor === "NVIDIA" ? "https://www.nvidia.com/Download/index.aspx"
      : oldGpu.vendor === "AMD" ? "https://www.amd.com/en/support" : "https://www.intel.com/content/www/us/en/download-center/home.html";
    adv.push(`<div class="advice warn">🔄 <b>GPU driver is ${oldGpu.driverAgeDays} days old.</b> New drivers often carry real per-game performance fixes — the single most reliable "optimization" there is. <span class="link" data-url="${url}">Get the latest ${esc(oldGpu.vendor)} driver</span>.</div>`);
  }
  if (!/high performance|ultimate/i.test(SYS.activePowerPlan)) {
    adv.push(`<div class="advice info">⚡ You're on the <b>${esc(SYS.activePowerPlan)}</b> power plan. The High Performance plan is in the Optimize tab.</div>`);
  }
  const lowDrive = SYS.drives.find((d) => d.totalBytes > 0 && d.freeBytes / d.totalBytes < 0.12);
  if (lowDrive) {
    adv.push(`<div class="advice warn">💾 <b>Drive ${esc(lowDrive.letter)} is nearly full</b> (${gb(lowDrive.freeBytes)} free). SSDs slow down and games stutter when the drive is this full — aim for 10–15% free. See Optimize → Storage.</div>`);
  }
  if (SYS.ram.sticks === 1) {
    adv.push(`<div class="advice info">▤ <b>Single RAM stick detected.</b> A matched second stick enables dual-channel mode — one of the few hardware upgrades that genuinely lifts FPS in CPU-bound games.</div>`);
  }
  if (SYS.os.uptimeHours > 168) {
    adv.push(`<div class="advice info">🕐 This PC has been running for <b>${Math.round(SYS.os.uptimeHours / 24)} days</b> without a restart. A reboot clears leaked memory and is the cheapest fix for creeping slowdowns.</div>`);
  }
  const gpu0 = SYS.gpus[0];
  if (gpu0 && gpu0.refreshRate > 0 && gpu0.refreshRate <= 60 && gpu0.vendor !== "Intel") {
    adv.push(`<div class="advice warn">🖥️ Your display is running at <b>${gpu0.refreshRate} Hz</b>. Run the check in Optimize → FPS &amp; frame-time — if your monitor supports more, that's the biggest free smoothness win there is.</div>`);
  }
  if (adv.length === 0) adv.push(`<div class="advice good">✅ No urgent issues spotted. Check the Optimize tab for tune-ups and diagnostics.</div>`);
  $("#advice-list").innerHTML = adv.join("");
  $$("#advice-list .link").forEach((a) => a.addEventListener("click", () => call("openUrl", { url: a.dataset.url })));
}

function hwRow(ico, name, detail, tag) {
  return `<div class="hw-item"><div class="hw-ico">${ico}</div><div><div class="hw-name">${esc(name)}</div><div class="hw-detail">${esc(detail)}</div></div>${tag ? `<span class="badge kind hw-tag">${esc(tag)}</span>` : ""}</div>`;
}

function updateStats() {
  const settings = OPTS.filter((o) => o.available && o.kind === "setting");
  $("#stat-opt").textContent = settings.length ? `${settings.filter((o) => o.applied).length} / ${settings.length}` : "–";
  $("#stat-startup").textContent = STARTUP.length ? STARTUP.filter((s) => s.enabled).length : "–";
  $("#stat-bloat").textContent = BLOAT.length || "–";
}

/* ========================================================================
   OPTIMIZE PAGE
   ======================================================================== */
async function scanOptimizations(auto = false) {
  const btn = $("#btn-opt-scan");
  btn.disabled = true; btn.textContent = "Scanning…";
  try {
    OPTS = await call("scanOptimizations");
    optSel.clear();
    if (openCats.size === 0) {
      // open categories that contain recommended, not-yet-applied items
      for (const o of OPTS) if (o.available && o.recommended && !o.applied) openCats.add(o.category);
      if (openCats.size === 0) openCats.add("gaming");
    }
    renderOptList();
    updateStats();
    if (!auto) toast("Scan complete", "Nothing was changed. Review and select below.");
  } catch (e) {
    toast("Scan failed", e.message, "err");
  } finally {
    btn.disabled = false; btn.textContent = "Scan";
  }
}

const RISK_LABEL = { low: "low risk", medium: "medium risk", high: "HIGH RISK" };

function renderOptList() {
  const wrap = $("#opt-list");
  const avail = OPTS.filter((o) => o.available);
  if (!avail.length) { wrap.innerHTML = `<div class="empty">Press <b>Scan</b> to inspect this PC. Nothing is changed during a scan.</div>`; return; }
  let html = "";
  for (const [cat, label] of CATEGORIES) {
    const items = avail.filter((o) => o.category === cat);
    if (!items.length) continue;
    const applied = items.filter((o) => o.kind === "setting" && o.applied).length;
    const settings = items.filter((o) => o.kind === "setting").length;
    const open = openCats.has(cat);
    html += `<div class="cat ${open ? "open" : ""}" data-cat="${cat}">
      <button class="cat-head">
        <span class="cat-caret">▸</span> ${label}
        <span class="cat-count">${items.length}</span>
        ${settings ? `<span class="cat-applied">${applied}/${settings} applied</span>` : ""}
      </button>
      <div class="cat-body">${items.map(optCard).join("")}</div>
    </div>`;
  }
  wrap.innerHTML = html;

  $$("#opt-list .cat-head").forEach((h) => h.addEventListener("click", () => {
    const cat = h.parentElement.dataset.cat;
    if (openCats.has(cat)) openCats.delete(cat); else openCats.add(cat);
    h.parentElement.classList.toggle("open");
  }));

  $$("#opt-list .opt-card").forEach((card) => {
    const id = card.dataset.id;
    const item = OPTS.find((o) => o.id === id);
    card.querySelector(".opt-main").addEventListener("click", (e) => {
      if (e.target.closest(".check") || e.target.closest(".btn")) return;
      card.classList.toggle("open");
    });
    const chk = card.querySelector(".check");
    if (chk) chk.addEventListener("click", () => {
      if (optSel.has(id)) optSel.delete(id); else optSel.add(id);
      chk.classList.toggle("on", optSel.has(id));
      syncOptApply();
    });
    const runBtn = card.querySelector("[data-run-diag]");
    if (runBtn) runBtn.addEventListener("click", () => runDiagnostic(item, card, runBtn));
  });
  syncOptApply();
}

function optCard(o) {
  const sel = optSel.has(o.id);
  const isDiag = o.kind === "diagnostic";
  const isAction = o.kind === "action";
  const left = isDiag
    ? `<button class="btn mini diag-run" data-run-diag="1">▶ Run</button>`
    : o.applied
      ? `<span class="badge applied">✓ applied</span>`
      : `<button class="check ${sel ? "on" : ""}" aria-label="select"></button>`;
  const badges = [
    `<span class="badge impact-${o.impact}">${o.impact}</span>`,
    o.risk !== "low" ? `<span class="badge risk-${o.risk}">${RISK_LABEL[o.risk] || o.risk}</span>` : "",
    isAction ? `<span class="badge kind">runs once</span>` : "",
    isDiag ? `<span class="badge kind-diag">check only</span>` : "",
    o.needsReboot ? `<span class="badge reboot">restart needed</span>` : "",
    !o.reversible && !isDiag ? `<span class="badge risk-medium" title="Cannot be automatically undone">no undo</span>` : "",
  ].filter(Boolean).join(" ");
  return `<div class="opt-card ${o.applied ? "applied" : ""}" data-id="${esc(o.id)}">
    <div class="opt-main">
      ${left}
      <div class="opt-grow">
        <div class="opt-title">${esc(o.title)} ${badges}</div>
        <div class="opt-desc">${esc(o.what)}</div>
        ${o.state ? `<div class="opt-state">${esc(o.state)}</div>` : ""}
      </div>
      <span class="caret">▼</span>
    </div>
    <div class="opt-expand">
      <div class="row"><span class="lbl">Expected benefit</span>${esc(o.why)} <span class="benefit-tag">${esc(o.benefit)}</span></div>
      <div class="row"><span class="lbl">Drawbacks</span>${esc(o.drawbacks)}</div>
      ${o.note ? `<div class="row"><span class="lbl">On this PC</span><span class="note">${esc(o.note)}</span></div>` : ""}
      ${isDiag ? `<div class="diag-result" style="display:none"></div>` : `<div class="row muted" style="font-size:12px">Risk: ${o.risk} · ${o.reversible ? "Original value is backed up — restorable from History." : "Cannot be automatically restored."}</div>`}
    </div>
  </div>`;
}

async function runDiagnostic(item, card, btn) {
  btn.disabled = true; btn.textContent = "Running…";
  card.classList.add("open");
  const box = card.querySelector(".diag-result");
  box.style.display = "block";
  box.innerHTML = `<div class="muted">Checking…</div>`;
  try {
    const r = await call("runDiagnostic", { id: item.id });
    box.innerHTML = `<div class="diag-verdict ${r.ok ? "ok" : "warn"}">${r.ok ? "✔" : "⚠"} ${esc(r.summary)}</div>` +
      (r.lines || []).map((l) => `<div class="diag-line">${esc(l)}</div>`).join("");
  } catch (e) {
    box.innerHTML = `<div class="diag-verdict warn">⚠ ${esc(e.message)}</div>`;
  } finally {
    btn.disabled = false; btn.textContent = "▶ Run";
  }
}

function syncOptApply() {
  const b = $("#btn-opt-apply");
  b.disabled = optSel.size === 0;
  b.textContent = optSel.size ? `Apply selected (${optSel.size})` : "Apply selected";
  const settings = OPTS.filter((o) => o.available && o.kind === "setting");
  $("#opt-summary").textContent = OPTS.length ? `${settings.filter((o) => o.applied).length} of ${settings.length} settings applied · ${optSel.size} selected` : "";
}

$("#btn-opt-scan").addEventListener("click", () => scanOptimizations());
$("#btn-opt-recommended").addEventListener("click", () => {
  optSel.clear();
  OPTS.filter((o) => o.available && o.recommended && !o.applied).forEach((o) => optSel.add(o.id));
  for (const o of OPTS) if (optSel.has(o.id)) openCats.add(o.category);
  renderOptList();
  toast("Recommended selected", optSel.size + " low-risk optimizations selected. Review, then Apply.");
});
$("#btn-opt-history").addEventListener("click", () => gotoPage("history"));

$("#btn-opt-apply").addEventListener("click", async () => {
  const chosen = OPTS.filter((o) => optSel.has(o.id));
  if (!chosen.length) return;
  const settings = chosen.filter((o) => o.kind === "setting");
  const actions = chosen.filter((o) => o.kind === "action");
  const reboot = chosen.some((o) => o.needsReboot);
  const risky = chosen.filter((o) => o.risk === "high");
  const noUndo = chosen.filter((o) => !o.reversible && o.kind !== "diagnostic");
  const body = `
    ${settings.length ? `<p><b>${settings.length} setting change${settings.length > 1 ? "s" : ""}</b> — original values are saved first; undo anytime in <b>History &amp; Restore</b>.</p>
    <ul>${settings.map((o) => `<li><b>${esc(o.title)}</b>${o.needsReboot ? ' <span class="badge reboot">restart needed</span>' : ""}${o.risk !== "low" ? ` <span class="badge risk-${o.risk}">${o.risk} risk</span>` : ""}</li>`).join("")}</ul>` : ""}
    ${actions.length ? `<p><b>${actions.length} one-time task${actions.length > 1 ? "s" : ""}</b> — these run once and are not undoable:</p>
    <ul>${actions.map((o) => `<li><b>${esc(o.title)}</b>${o.state ? ` <span class="muted">(${esc(o.state)})</span>` : ""}</li>`).join("")}</ul>` : ""}
    ${risky.length ? `<div class="warn-box">⚠ <b>${risky.length} high-risk item(s) selected.</b> Re-read their descriptions — these are last-resort repairs.</div>` : ""}
    ${noUndo.length ? `<div class="warn-box">⚠ <b>${noUndo.length} item(s) cannot be automatically undone</b> (cleanups/resets).</div>` : ""}
    ${reboot ? `<div class="warn-box">🔁 At least one change needs a <b>restart</b> to take effect. Tempo will never restart your PC for you.</div>` : ""}`;
  const ok = await confirmModal(`Apply ${chosen.length} item${chosen.length > 1 ? "s" : ""}?`, body, "Apply now", risky.length > 0);
  if (!ok) return;

  const applyBtn = $("#btn-opt-apply");
  applyBtn.disabled = true; // no double-launching a batch
  progressOpen("Applying optimizations…");
  progressSetTotal(chosen.length);
  try {
    const r = await call("applyOptimizations", { ids: [...optSel] });
    const okResults = r.results.filter((x) => x.success);
    const fails = r.results.filter((x) => !x.success);
    okResults.forEach((x) => { if (x.error) progressLog("ℹ " + x.id + ": " + x.error, "ok"); });
    fails.forEach((f) => progressLog("Failed: " + f.id + " — " + f.error, "err"));
    progressLog(`Done. ${okResults.length} succeeded, ${fails.length} failed.`, okResults.length ? "ok" : "err");
    progressClose(fails.length || okResults.some((x) => x.error) ? 3500 : 800);
    toast("Finished", `${okResults.length} change(s) made.${reboot ? " Restart when convenient." : ""}`, fails.length ? "warn" : "ok");
    SYS = await call("rescan");
    renderDashboard();
    await scanOptimizations(true);
  } catch (e) {
    progressClose(0);
    toast("Apply failed", e.message, "err");
  } finally {
    syncOptApply();
  }
});

/* ========================================================================
   STARTUP PAGE
   ======================================================================== */
async function refreshStartup() {
  try {
    STARTUP = await call("listStartup");
    renderStartup();
    updateStats();
  } catch (e) { toast("Startup scan failed", e.message, "err"); }
}

function renderStartup() {
  const wrap = $("#startup-list");
  if (!STARTUP.length) { wrap.innerHTML = `<div class="empty">No startup entries found in the Run registry keys.</div>`; return; }
  wrap.innerHTML = STARTUP.map((s, i) => `
    <div class="opt-card">
      <div class="opt-main" style="cursor:default">
        <div class="opt-grow">
          <div class="opt-title">${esc(s.name)} <span class="badge kind">${esc(s.location)}</span></div>
          <div class="opt-desc">${s.hint ? esc(s.hint) + " · " : ""}<span style="opacity:.7">${esc(s.command.length > 90 ? s.command.slice(0, 90) + "…" : s.command)}</span></div>
        </div>
        <button class="toggle ${s.enabled ? "on" : ""}" data-i="${i}" title="${s.enabled ? "Enabled — click to disable at boot" : "Disabled — click to re-enable"}"></button>
      </div>
    </div>`).join("");
  $$("#startup-list .toggle").forEach((t) => t.addEventListener("click", async () => {
    const s = STARTUP[+t.dataset.i];
    t.disabled = true;
    try {
      STARTUP = await call("setStartup", { name: s.name, scope: s.scope, location: s.location, enable: !s.enabled });
      renderStartup();
      updateStats();
      toast(s.enabled ? "Startup app disabled" : "Startup app enabled", s.name + (s.enabled ? " will no longer launch at boot. The app itself is untouched." : " will launch at boot again."));
    } catch (e) {
      t.disabled = false;
      toast("Change failed", e.message, "err");
    }
  }));
}
$("#btn-startup-scan").addEventListener("click", refreshStartup);

/* ========================================================================
   BLOATWARE PAGE
   ======================================================================== */
const GROUP_META = {
  "likely-unnecessary": ["likely unnecessary", "group-safe"],
  "optional": ["optional — your call", "group-opt"],
  "resource-heavy": ["resource-heavy", "group-heavy"],
  "keep-unless-sure": ["keep unless you're sure", "group-risky"],
  "normal": ["normal app", "group-normal"],
};

async function scanBloat(auto = false) {
  const btn = $("#btn-bloat-scan");
  btn.disabled = true; btn.textContent = "Scanning…";
  if (!auto) $("#bloat-list").innerHTML = `<div class="empty">Scanning installed apps and sampling resource usage… ~10 seconds.</div>`;
  try {
    const r = await call("scanBloatware");
    BLOAT = r.apps || r;           // tolerate old shape
    BLOAT_SUMMARY = r.summary || null;
    bloatSel.clear();
    BLOAT.filter((a) => a.preselect).forEach((a) => bloatSel.add(a.id));
    $("#bloat-filters").style.display = "flex";
    renderBloatSummary();
    renderBloat();
    updateStats();
    if (!auto) toast("Scan complete", BLOAT.length + " removable apps listed. Nothing was changed.");
  } catch (e) {
    if (!auto) toast("Scan failed", e.message, "err");
  } finally {
    btn.disabled = false; btn.textContent = "Scan";
  }
}

function renderBloatSummary() {
  const s = BLOAT_SUMMARY;
  const box = $("#bloat-summary-row");
  if (!s) { box.innerHTML = ""; return; }
  box.innerHTML = `
    <div class="sum-card"><div class="sum-num">${s.totalApps}</div><div class="sum-label">removable apps<br>${mb(s.totalSizeBytes)} total</div></div>
    <div class="sum-card"><div class="sum-num">${s.startupEnabled}</div><div class="sum-label">launch at startup</div></div>
    <div class="sum-card"><div class="sum-num">${s.runningNow}</div><div class="sum-label">running now<br>${mb(s.runningRamBytes)} RAM</div></div>
    <div class="sum-card"><div class="sum-num">${mb(s.cleanupBytes)}</div><div class="sum-label">cleanup opportunity<br><span class="link" id="goto-storage">clean in Optimize ›</span></div></div>
    ${s.topRamApps.length ? `<div class="sum-wide"><b>Top background RAM:</b> ${s.topRamApps.map(esc).join(" · ")}</div>` : ""}
    ${s.topDiskApps.length ? `<div class="sum-wide"><b>Biggest on disk:</b> ${s.topDiskApps.map(esc).join(" · ")}</div>` : ""}`;
  const g = $("#goto-storage");
  if (g) g.addEventListener("click", () => { gotoPage("optimize"); openCats.add("storage"); renderOptList(); });
}

function bloatVisible() {
  let list = BLOAT.filter((a) =>
    (bloatFilter === "all" || a.kind === bloatFilter) &&
    (bloatGroup === "all" || a.group === bloatGroup) &&
    (!bloatSearch || (a.name + " " + a.publisher).toLowerCase().includes(bloatSearch)));
  const by = {
    name: (a, b) => a.name.localeCompare(b.name),
    size: (a, b) => b.sizeBytes - a.sizeBytes,
    ram: (a, b) => b.ramBytes - a.ramBytes,
    publisher: (a, b) => (a.publisher || "~").localeCompare(b.publisher || "~"),
    startup: (a, b) => (b.hasStartup - a.hasStartup) || (b.runningProcesses - a.runningProcesses),
  };
  if (bloatSort !== "smart" && by[bloatSort]) list = [...list].sort(by[bloatSort]);
  return list;
}

function renderBloat() {
  const wrap = $("#bloat-list");
  const list = bloatVisible();
  if (!BLOAT.length) { wrap.innerHTML = `<div class="empty">Press <b>Scan</b> to list removable apps. Scanning changes nothing.</div>`; return; }
  if (!list.length) { wrap.innerHTML = `<div class="empty">No apps match the current filter.</div>`; syncBloatBtn(); return; }
  wrap.innerHTML = list.map((a) => {
    const sel = bloatSel.has(a.id);
    const [gLabel, gClass] = GROUP_META[a.group] || GROUP_META.normal;
    const usage = [];
    if (a.hasStartup) usage.push("🚀 starts with Windows");
    if (a.runningProcesses > 0) usage.push(`● running: ${a.runningProcesses} process${a.runningProcesses > 1 ? "es" : ""} · ${mb(a.ramBytes)} RAM${a.cpuPercent >= 1 ? " · " + a.cpuPercent + "% CPU" : ""}`);
    if (a.serviceCount > 0) usage.push(`⚙ ${a.serviceCount} auto-start service${a.serviceCount > 1 ? "s" : ""}`);
    return `<div class="opt-card" data-id="${esc(a.id)}">
      <div class="opt-main">
        <button class="check ${sel ? "on" : ""}"></button>
        <div class="opt-grow">
          <div class="opt-title">${esc(a.name)}
            <span class="badge ${gClass}">${gLabel}</span>
            <span class="badge kind">${a.kind === "store" ? "Store app" : "Desktop program"}</span>
          </div>
          <div class="opt-desc">${esc(a.description)} ${a.publisher ? "· " + esc(a.publisher) : ""}</div>
          ${usage.length ? `<div class="opt-state">${usage.map(esc).join("   ")}</div>` : ""}
        </div>
        <span class="size-tag">${mb(a.sizeBytes)}</span>
        <span class="caret">▼</span>
      </div>
      <div class="opt-expand">
        <div class="row"><span class="lbl">Why it may be unnecessary</span>${esc(a.whyRemovable)}</div>
        <div class="row"><span class="lbl">Impact of removing</span>${esc(a.safetyNote)}</div>
        <div class="row"><span class="lbl">How it's removed</span>${esc(a.uninstallMethod)}</div>
        <div class="row"><span class="lbl">Getting it back</span>${a.restorable ? "✅ " : "⚠ "}${esc(a.restore)}</div>
        ${a.hasStartup ? `<div class="row"><span class="lbl">Lighter option</span>Instead of uninstalling, you can just stop it launching at boot (keeps the app): <span class="link" data-startup-jump="1">manage startup entry "${esc(a.startupName)}" ›</span></div>` : ""}
        <div class="row muted" style="font-size:12px">Uninstalling removes the app · disabling startup only stops auto-launch · neither is the same as ending a running process.</div>
      </div>
    </div>`;
  }).join("");

  $$("#bloat-list .opt-card").forEach((card) => {
    const id = card.dataset.id;
    card.querySelector(".opt-main").addEventListener("click", (e) => {
      if (e.target.closest(".check")) return;
      card.classList.toggle("open");
    });
    card.querySelector(".check").addEventListener("click", (e) => {
      if (bloatSel.has(id)) bloatSel.delete(id); else bloatSel.add(id);
      e.target.classList.toggle("on", bloatSel.has(id));
      syncBloatBtn();
    });
    const jump = card.querySelector("[data-startup-jump]");
    if (jump) jump.addEventListener("click", () => gotoPage("startup"));
  });
  syncBloatBtn();
}

function syncBloatBtn() {
  const b = $("#btn-bloat-remove");
  b.disabled = bloatSel.size === 0;
  b.textContent = bloatSel.size ? `Review & remove (${bloatSel.size})…` : "Review & remove…";
  $("#bloat-count").textContent = BLOAT.length ? `${bloatVisible().length} shown of ${BLOAT.length} · ${bloatSel.size} selected` : "";
}

$("#btn-bloat-scan").addEventListener("click", () => scanBloat());
$$("#bloat-filters .chip[data-filter]").forEach((c) => c.addEventListener("click", () => {
  $$("#bloat-filters .chip[data-filter]").forEach((x) => x.classList.toggle("active", x === c));
  bloatFilter = c.dataset.filter;
  renderBloat();
}));
$$("#bloat-filters .chip[data-group]").forEach((c) => c.addEventListener("click", () => {
  const already = c.classList.contains("active");
  $$("#bloat-filters .chip[data-group]").forEach((x) => x.classList.remove("active"));
  if (!already) c.classList.add("active");
  bloatGroup = already ? "all" : c.dataset.group;
  renderBloat();
}));
$("#bloat-sort").addEventListener("change", (e) => { bloatSort = e.target.value; renderBloat(); });
$("#bloat-search").addEventListener("input", (e) => { bloatSearch = e.target.value.toLowerCase(); renderBloat(); });

$("#btn-bloat-remove").addEventListener("click", async () => {
  const chosen = BLOAT.filter((a) => bloatSel.has(a.id));
  if (!chosen.length) return;
  const nonRestorable = chosen.filter((a) => !a.restorable);
  const risky = chosen.filter((a) => a.safety === "risky" || a.safety === "unknown");
  const interactive = chosen.filter((a) => a.kind === "win32" && !a.silent);
  const hasWin32 = chosen.some((a) => a.kind === "win32");
  const totalSize = chosen.reduce((s, a) => s + (a.sizeBytes || 0), 0);
  const body = `
    <p>Final review — <b>${chosen.length} app${chosen.length > 1 ? "s" : ""}</b>${totalSize ? ` (~${mb(totalSize)} on disk)` : ""}:</p>
    <ul>${chosen.map((a) => `<li><b>${esc(a.name)}</b> — ${esc(a.uninstallMethod)}<br><span class="muted">${a.restorable ? "Restorable: " + esc(a.restore) : "⚠ NOT automatically restorable. " + esc(a.restore)}</span></li>`).join("")}</ul>
    ${nonRestorable.length ? `<div class="warn-box">⚠ <b>${nonRestorable.length} item(s) cannot be automatically restored</b> — you'd reinstall them yourself.</div>` : ""}
    ${risky.length ? `<div class="warn-box">⚠ <b>${risky.length} item(s) are marked risky/unknown.</b> Re-read their notes before confirming.</div>` : ""}
    ${interactive.length ? `<p class="muted">ℹ ${interactive.length} program(s) use an interactive uninstaller — a window will open; follow its steps.</p>` : ""}
    <label class="rp-row"><input type="checkbox" id="rp-check" ${hasWin32 ? "checked" : ""}> Create a System Restore point first <span class="muted">(protects Windows settings/registry — it does NOT reliably bring removed apps back)</span></label>`;
  const ok = await confirmModal("Remove " + chosen.length + " app" + (chosen.length > 1 ? "s" : "") + "?", body, "Uninstall now");
  const wantRp = !!document.getElementById("rp-check")?.checked; // read before modal content is replaced
  if (!ok) return;

  const removeBtn = $("#btn-bloat-remove");
  removeBtn.disabled = true; // no double-launching a batch
  progressOpen("Removing apps…");
  progressSetTotal(chosen.length + (wantRp ? 1 : 0));
  try {
    const r = await call("uninstallApps", { ids: [...bloatSel], restorePoint: wantRp });
    const results = r.results || r;
    const okCount = results.filter((x) => x.success).length;
    const fails = results.filter((x) => !x.success);
    if (r.restorePointWarning) progressLog("Restore point: " + r.restorePointWarning, "err");
    results.filter((x) => x.success && x.error).forEach((x) => progressLog("ℹ " + x.error, "ok"));
    fails.forEach((f) => progressLog("✗ " + f.error, "err"));
    progressLog(`Done. ${okCount} removed, ${fails.length} not removed.`, okCount ? "ok" : "err");
    if (progressHidden) {
      $("#progress-back").style.display = "none";
      toast("Removal finished", `${okCount} removed, ${fails.length} not removed.${fails.length ? " Open the Bloatware tab for details." : ""}`, fails.length ? "warn" : "ok");
    } else {
      progressClose(fails.length || r.restorePointWarning ? 5000 : 900);
      toast("Removal finished", `${okCount} app(s) removed.` + (fails.length ? ` ${fails.length} not removed — see the log for why.` : ""), fails.length ? "warn" : "ok");
    }
    await scanBloat(true);
  } catch (e) {
    progressClose(0);
    toast("Removal failed", e.message, "err");
  } finally {
    removeBtn.disabled = bloatSel.size === 0;
    syncBloatBtn();
  }
});

/* ========================================================================
   HISTORY PAGE
   ======================================================================== */
async function refreshHistory() {
  try {
    const hist = await call("getHistory");
    renderHistory(hist);
  } catch (e) { toast("History failed", e.message, "err"); }
}

function renderHistory(hist) {
  const wrap = $("#history-list");
  if (!hist || !hist.length) { wrap.innerHTML = `<div class="empty">No changes recorded yet. Tempo logs every change it makes here, with the original value.</div>`; return; }
  wrap.innerHTML = hist.map((s) => {
    const restorable = s.changes.filter((c) => c.restorable && !c.restored).length;
    return `<div class="session-card">
      <div class="session-head">
        <div style="flex:1">
          <div class="session-title">${esc(s.label)}</div>
          <div class="session-date">${new Date(s.at).toLocaleString()} · ${s.changes.length} change(s)</div>
        </div>
        ${restorable ? `<button class="btn tiny" data-restore-session="${s.id}">↩ Restore all (${restorable})</button>` : ""}
      </div>
      ${s.changes.map((c) => `
        <div class="change-row">
          <div class="ch-title">
            ${esc(c.title)}
            <div class="ch-meta">${esc(c.type)} · ${esc(c.target)}${c.valueName ? " → " + esc(c.valueName) : ""}${c.oldValue !== null && c.oldValue !== undefined ? " · was: " + esc(String(c.oldValue).slice(0, 48)) : " · was: (not set)"}</div>
          </div>
          ${c.restored ? `<span class="badge kind">restored</span>`
            : c.restorable ? `<button class="btn tiny" data-restore="${s.id}|${c.id}">↩ Restore</button>`
            : `<span class="badge risk-medium" title="Cannot be automatically undone">manual</span>`}
        </div>`).join("")}
    </div>`;
  }).join("");

  $$("[data-restore]").forEach((b) => b.addEventListener("click", async () => {
    const [sessionId, changeId] = b.dataset.restore.split("|");
    b.disabled = true;
    try {
      const hist2 = await call("restoreChange", { sessionId, changeId });
      renderHistory(hist2);
      toast("Restored", "Original value written back.");
      SYS = await call("rescan"); renderDashboard(); scanOptimizations(true);
    } catch (e) { b.disabled = false; toast("Restore failed", e.message, "err"); }
  }));
  $$("[data-restore-session]").forEach((b) => b.addEventListener("click", async () => {
    b.disabled = true;
    progressOpen("Restoring session…");
    progressSetTotal(6);
    try {
      const r = await call("restoreSession", { sessionId: b.dataset.restoreSession });
      progressClose(400);
      renderHistory(r.history);
      if (r.errors && r.errors.length) toast("Restored with issues", r.errors.join("; "), "warn");
      else toast("Session restored", "All restorable changes were rolled back.");
      SYS = await call("rescan"); renderDashboard(); scanOptimizations(true);
    } catch (e) { progressClose(0); b.disabled = false; toast("Restore failed", e.message, "err"); }
  }));
}
$("#btn-history-refresh").addEventListener("click", refreshHistory);

/* ========================================================================
   REPORT PAGE
   ======================================================================== */
function renderReport() {
  if (!SYS) return;
  const o = OPTS.filter((x) => x.available && x.kind === "setting");
  $("#report-view").innerHTML = `
    <h3>Hardware</h3>
    <table>
      <tr><th>CPU</th><td>${esc(SYS.cpu.name)} — ${SYS.cpu.cores}C/${SYS.cpu.threads}T @ ${(SYS.cpu.maxClockMhz / 1000).toFixed(1)} GHz base</td></tr>
      ${SYS.gpus.map((g) => `<tr><th>GPU</th><td>${esc(g.name)}${g.vramBytes ? " — " + gb(g.vramBytes) + " VRAM" : ""} — driver ${esc(g.driverVersion)}${g.driverAgeDays >= 0 ? ` (${g.driverAgeDays} days old)` : ""}</td></tr>`).join("")}
      <tr><th>RAM</th><td>${gb(SYS.ram.totalBytes)} — ${SYS.ram.sticks} stick(s), ${SYS.ram.speedMhz} MT/s, ${esc(SYS.ram.channels)}</td></tr>
      ${SYS.drives.map((d) => `<tr><th>Drive ${esc(d.letter)}</th><td>${esc(d.mediaType)} — ${gb(d.freeBytes)} free of ${gb(d.totalBytes)} — health ${esc(d.health)}${d.isSystemDrive ? " (system)" : ""}</td></tr>`).join("")}
      <tr><th>Motherboard</th><td>${esc(SYS.motherboard || "—")}</td></tr>
      <tr><th>OS</th><td>${esc(SYS.os.name)} ${esc(SYS.os.displayVersion)} (build ${esc(SYS.os.build)})</td></tr>
      <tr><th>Power plan</th><td>${esc(SYS.activePowerPlan)}</td></tr>
    </table>
    <h3>Optimization status (${o.filter((x) => x.applied).length}/${o.length} applied)</h3>
    <table>${o.map((x) => `<tr><th>${esc(x.title)}</th><td>${x.applied ? "✅ applied" : "— not applied"}</td></tr>`).join("") || "<tr><td>Run a scan on the Optimize page first.</td></tr>"}</table>`;
}
$("#btn-report-export").addEventListener("click", async () => {
  try {
    const r = await call("exportReport");
    toast("Report exported", "Saved to " + r.path + " and opened in your browser.");
  } catch (e) { toast("Export failed", e.message, "err"); }
});
$("#btn-rescan").addEventListener("click", async () => {
  $("#dash-sub").textContent = "Re-scanning…";
  SYS = await call("rescan");
  renderDashboard();
  toast("Hardware re-scanned", "Dashboard updated.");
});

/* ========================================================================
   UPDATES
   ======================================================================== */
let UPDATE = null;

async function checkForUpdate(manual = false) {
  const stateEl = $("#about-update-state");
  const btn = $("#btn-check-update");
  if (manual) { btn.disabled = true; btn.textContent = "Checking…"; stateEl.textContent = "Checking for updates…"; }
  try {
    const r = await call("checkUpdate");
    UPDATE = r;
    $("#about-version").textContent = r.currentVersion;
    if (r.error) {
      stateEl.textContent = "Couldn't check for updates: " + r.error;
      if (manual) toast("Update check failed", r.error, "warn");
      return;
    }
    if (r.available) {
      showUpdateBanner(r);
      stateEl.textContent = `Version ${r.latestVersion} is available${r.released ? " (" + r.released + ")" : ""}.`;
      if (manual) toast("Update available", "Version " + r.latestVersion + " is ready to install.");
    } else {
      stateEl.textContent = "You're on the latest version.";
      if (manual) toast("Up to date", "You're running the newest version of Tempo.");
    }
  } catch (e) {
    stateEl.textContent = "Couldn't check for updates: " + e.message;
    if (manual) toast("Update check failed", e.message, "warn");
  } finally {
    if (manual) { btn.disabled = false; btn.textContent = "Check for updates"; }
  }
}

function showUpdateBanner(r) {
  $("#ub-title").textContent = `Tempo ${r.latestVersion} is available — you're on ${r.currentVersion}`;
  $("#ub-notes").textContent = r.canSelfUpdate ? (r.notes || "") : (r.blocked || "");
  $("#ub-now").style.display = r.canSelfUpdate ? "" : "none";
  $("#update-banner").style.display = "flex";
  $("#app").classList.add("has-banner");
}

function hideUpdateBanner() {
  $("#update-banner").style.display = "none";
  $("#app").classList.remove("has-banner");
}

$("#ub-later").addEventListener("click", hideUpdateBanner);
$("#btn-check-update").addEventListener("click", () => checkForUpdate(true));

$("#ub-now").addEventListener("click", async () => {
  if (!UPDATE) return;
  const ok = await confirmModal(
    `Update to Tempo ${UPDATE.latestVersion}?`,
    `<p>Tempo will download the new version, verify its checksum, replace itself and restart. Takes about a minute.</p>
     ${UPDATE.notes ? `<p><b>What's new</b></p><ul>${UPDATE.notes.split("\n").filter(Boolean).map((l) => `<li>${esc(l.replace(/^[-*]\s*/, ""))}</li>`).join("")}</ul>` : ""}
     <p class="muted">Your settings and change history are kept. Optimizations you already applied stay applied.</p>`,
    "Update & restart", false);
  if (!ok) return;
  hideUpdateBanner();
  progressOpen("Updating Tempo…");
  progressSetTotal(12);
  try {
    await call("applyUpdate");
    progressLog("Update installed — restarting…", "ok");
    $("#progress-fill").style.width = "100%";
  } catch (e) {
    progressClose(0);
    toast("Update failed", e.message, "err");
    showUpdateBanner(UPDATE);
  }
});

/* ---------- boot ---------- */
async function boot() {
  try {
    const r = await call("init");
    SYS = r.system;
    renderDashboard();
    renderHistory(r.history);
    scanOptimizations(true);   // read-only
    refreshStartup();          // read-only
    scanBloat(true);           // read-only
  } catch (e) {
    $("#dash-sub").textContent = "Initialization failed: " + e.message;
  }
  // non-blocking: never let a slow/unreachable update server delay the UI
  try {
    const v = await call("appVersion");
    $("#about-version").textContent = v.version;
  } catch { }
  setTimeout(() => checkForUpdate(false), 2500);
}

/* ===================== mock mode (browser preview only) ===================== */
function mockCall(method, params) {
  const delay = (v, ms = 300) => new Promise((r) => setTimeout(() => r(JSON.parse(JSON.stringify(v))), ms));
  switch (method) {
    case "init": startMockMetrics(); return delay({ system: MOCK_SYS, history: [] }, 400);
    case "rescan": return delay(MOCK_SYS);
    case "scanOptimizations": return delay(MOCK_OPTS, 600);
    case "listStartup": return delay(MOCK_STARTUP, 300);
    case "scanBloatware": return delay({ apps: MOCK_BLOAT, summary: MOCK_BLOAT_SUMMARY }, 900);
    case "getHistory": return delay([]);
    case "runDiagnostic":
      return delay({ id: params.id, ok: params.id !== "diag-display", summary: params.id === "diag-display" ? "Your display supports 240 Hz but runs at 60 Hz." : "All good — nothing to change.", lines: ["Detail line one", "Detail line two"] }, 900);
    case "applyOptimizations":
      params.ids.forEach((id) => { const o = MOCK_OPTS.find((x) => x.id === id); if (o && o.kind === "setting") o.applied = true; });
      return delay({ results: params.ids.map((id) => ({ id, success: true, error: id.startsWith("act-") ? "Freed 1.2 GB." : null })) }, 1200);
    case "uninstallApps": return delay({ results: params.ids.map((id) => ({ id, success: true })), restorePointWarning: null }, 1200);
    case "setStartup": { const s = MOCK_STARTUP.find((x) => x.name === params.name); if (s) s.enabled = params.enable; return delay(MOCK_STARTUP, 200); }
    case "restoreChange": case "restoreSession": return delay({ history: [], errors: [] }, 400);
    case "exportReport": return delay({ path: "C:\\Users\\you\\Desktop\\Tempo-Report.html" });
    case "openUrl": return delay(true, 10);
    case "appVersion": return delay({ version: "1.0.0" });
    case "checkUpdate": return delay({ available: true, currentVersion: "1.0.0", latestVersion: "1.1.0", notes: "- Faster bloatware scan\n- Fixed uninstall progress", released: "2026-10-13", canSelfUpdate: true, error: null, blocked: null }, 800);
    case "applyUpdate": return delay({ restarted: true }, 2000);
  }
  return delay(null);
}
let mockT = 0, mockTimer = null;
function startMockMetrics() {
  if (mockTimer) return;
  mockTimer = setInterval(() => {
    mockT += 0.35;
    updateMetrics({
      cpuPercent: 38 + Math.sin(mockT) * 18 + Math.random() * 9,
      gpuPercent: 55 + Math.cos(mockT * .7) * 22 + Math.random() * 7,
      ramPercent: 32 + Math.sin(mockT * .2) * 3,
      ramUsedBytes: 10.2 * 1073741824, ramTotalBytes: 32 * 1073741824,
      vramUsedBytes: 3.1 * 1073741824, vramTotalBytes: 12 * 1073741824,
      processCount: 214 + Math.round(Math.random() * 6),
    });
  }, 1000);
}
const MOCK_SYS = {
  machineName: "GAMING-PC", isLaptop: false, activePowerPlan: "Balanced", activePowerPlanGuid: "x",
  cpu: { name: "12th Gen Intel(R) Core(TM) i5-12400F", vendor: "Intel", cores: 6, threads: 12, maxClockMhz: 2500 },
  gpus: [{ name: "NVIDIA GeForce RTX 5070", vendor: "NVIDIA", driverVersion: "617.42", driverAgeDays: 14, vramBytes: 12 * 1073741824, resolution: "1920x1080", refreshRate: 60, isPrimary: true }],
  ram: { totalBytes: 32 * 1073741824, sticks: 2, speedMhz: 3200, channels: "Likely dual-channel" },
  drives: [{ letter: "C:", label: "System", mediaType: "NVMe SSD", totalBytes: 1000 * 1073741824, freeBytes: 328 * 1073741824, health: "Healthy", isSystemDrive: true }],
  os: { name: "Windows 11 Home", displayVersion: "26H2", build: "26300", edition: "Core", uptimeHours: 20 },
  motherboard: "H610M-K Wi-Fi",
};
const MOCK_OPTS = [
  { id: "power-plan", category: "power", kind: "setting", title: "High Performance power plan", what: "Switches the active plan to High Performance.", why: "Stops CPU downclocking between frames.", drawbacks: "Higher idle power.", benefit: "smoothness", impact: "medium", risk: "low", reversible: true, needsReboot: false, recommended: true, applied: false, available: true, state: "Active plan: Balanced" },
  { id: "game-dvr-off", category: "gaming", kind: "setting", title: "Disable Xbox background recording", what: "Turns off background gameplay recording.", why: "Background encoding costs real CPU/GPU.", drawbacks: "Win+G instant replay stops working.", benefit: "FPS", impact: "medium", risk: "low", reversible: true, needsReboot: false, recommended: true, applied: false, available: true },
  { id: "display-max-hz", category: "fps", kind: "setting", title: "Set display to its maximum refresh rate", what: "Switches to the highest refresh rate supported.", why: "Free smoothness.", drawbacks: "Restore from History if the screen misbehaves.", benefit: "smoothness", impact: "high", risk: "medium", reversible: true, needsReboot: false, recommended: false, applied: false, available: true, state: "Now: 1920x1080 @ 60 Hz · max supported: 240 Hz", note: "Your display can run 240 Hz but is set to 60 Hz — strongly recommended." },
  { id: "diag-display", category: "fps", kind: "diagnostic", title: "Check: is your monitor running at full speed?", what: "Read-only check of refresh rate vs maximum.", why: "Finds the 144 Hz-stuck-at-60 problem.", drawbacks: "None.", benefit: "diagnostic", impact: "high", risk: "low", reversible: true, recommended: false, applied: false, available: true },
  { id: "act-clean-temp", category: "storage", kind: "action", title: "Clean temporary files now", what: "Deletes temp files older than 24 h.", why: "Reclaims disk space safely.", drawbacks: "Not undoable (temp files are disposable).", benefit: "storage", impact: "low", risk: "low", reversible: false, recommended: false, applied: false, available: true, state: "~1.8 GB reclaimable now" },
  { id: "act-winsock-reset", category: "advanced", kind: "action", title: "Reset the network stack (Winsock)", what: "netsh winsock reset.", why: "Last-resort network repair.", drawbacks: "Needs restart; not reversible.", benefit: "stability", impact: "situational", risk: "high", reversible: false, needsReboot: true, recommended: false, applied: false, available: true },
];
const MOCK_STARTUP = [
  { name: "Discord", command: "C:\\Users\\you\\AppData\\Local\\Discord\\Update.exe --processStart Discord.exe", location: "HKCU Run", scope: "user", enabled: true, source: "run-key", hint: "Chat app." },
  { name: "SecurityHealth", command: "%windir%\\system32\\SecurityHealthSystray.exe", location: "HKLM Run", scope: "machine", enabled: true, source: "run-key", hint: "Windows Security icon. KEEP enabled." },
];
const MOCK_BLOAT = [
  { id: "store:king", kind: "store", name: "Candy Crush Saga", publisher: "king.com", version: "1.0", description: "Advertised game.", whyRemovable: "Sponsored content.", safety: "safe", safetyNote: "No system impact.", sizeBytes: 350 * 1048576, restore: "Reinstall free from the Microsoft Store.", restorable: true, preselect: true, uninstallMethod: "Official Store package removal", silent: true, group: "likely-unnecessary", hasStartup: false, runningProcesses: 0, ramBytes: 0, cpuPercent: 0, serviceCount: 0, startupName: "" },
  { id: "win32:mcafee", kind: "win32", name: "McAfee LiveSafe", publisher: "McAfee LLC", version: "16.0", description: "Antivirus suite (trial).", whyRemovable: "Windows Security takes over for free.", safety: "optional", safetyNote: "Defender re-enables automatically.", sizeBytes: 1.2 * 1073741824, restore: "Not automatic — reinstall from mcafee.com.", restorable: false, preselect: false, uninstallMethod: "Official uninstaller (window will open)", silent: false, group: "resource-heavy", hasStartup: true, startupName: "McAfee Tray", runningProcesses: 4, ramBytes: 480 * 1048576, cpuPercent: 2.1, serviceCount: 3 },
];
const MOCK_BLOAT_SUMMARY = { totalApps: 2, totalSizeBytes: 1.55 * 1073741824, startupEnabled: 1, runningNow: 1, runningRamBytes: 480 * 1048576, cleanupBytes: 2.3 * 1073741824, topRamApps: ["McAfee LiveSafe — 480 MB · 2.1% CPU"], topDiskApps: ["McAfee LiveSafe — 1.2 GB", "Candy Crush Saga — 350 MB"], cleanupLines: [] };

/* start after all declarations (incl. mock data) are in place */
boot();
