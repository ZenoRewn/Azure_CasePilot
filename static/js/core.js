"use strict";
const $ = (id) => document.getElementById(id);
const TOKEN = document.querySelector('meta[name="casepilot-token"]').content;
const state = { tenant: null, sub: "", cloud: "AzureCloud", services: [], service: null, kind: "technical", tickets: [], plan: null, allRes: null };

// Support API 要求 Windows 时区名; 默认中国
const TIMEZONES = ["China Standard Time", "Taipei Standard Time", "Tokyo Standard Time", "Korea Standard Time", "Singapore Standard Time",
  "India Standard Time", "AUS Eastern Standard Time", "GMT Standard Time", "W. Europe Standard Time", "Central Europe Standard Time",
  "Eastern Standard Time", "Central Standard Time", "Mountain Standard Time", "Pacific Standard Time", "UTC"];
const COUNTRIES = [["CHN", "中国"], ["HKG", "中国香港"], ["MAC", "中国澳门"], ["TWN", "中国台湾"], ["SGP", "新加坡"], ["JPN", "日本"],
  ["KOR", "韩国"], ["USA", "美国"], ["GBR", "英国"], ["DEU", "德国"], ["AUS", "澳大利亚"], ["IND", "印度"]];
const DEFAULTS = { "c-lang": "zh-hans", "c-country": "CHN", "c-tz": "China Standard Time", "c-method": "email" };
const SEV = { minimal: ["C", "最小影响"], moderate: ["B", "中等影响"], critical: ["A", "严重影响"], highestcriticalimpact: ["A+", "业务关键"] };
const POPULAR = ["Azure OpenAI Service", "Virtual Machine running Linux", "Virtual Machine running Windows", "Kubernetes Service (AKS)", "Storage Account Management", "App Service"];
const KIND = {
  technical: { hint: "具体服务的技术故障或咨询", match: null },
  quota: { hint: "服务和订阅限制 (配额)", match: /service and subscription limits|quota/i },
  billing: { hint: "计费、发票、订阅管理", match: /^billing$|subscription management/i },
};
const PORTAL = { AzureCloud: "https://portal.azure.com", AzureChinaCloud: "https://portal.azure.cn", AzureUSGovernment: "https://portal.azure.us" };

// ---------------------------------------------------------------- 基础工具
async function api(action, body = {}) {
  const r = await fetch("/api/" + action, {
    method: "POST", headers: { "Content-Type": "application/json", "X-CasePilot-Token": TOKEN }, body: JSON.stringify(body),
  });
  const j = await r.json().catch(() => ({ ok: false, error: `HTTP ${r.status}` }));
  if (!j.ok) { const e = new Error(j.error || "请求失败"); e.detail = j.detail; throw e; }
  return j.data;
}
const h = (tag, attrs = {}, ...kids) => {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") el.className = v; else if (k.startsWith("on")) el[k] = v; else if (v != null) el.setAttribute(k, v);
  }
  kids.flat().forEach((c) => c != null && el.append(c));
  return el;
};
const icon = (name) => { const s = document.createElementNS("http://www.w3.org/2000/svg", "svg"); s.setAttribute("class", "i");
  const u = document.createElementNS("http://www.w3.org/2000/svg", "use"); u.setAttribute("href", "#i-" + name); s.append(u); return s; };
function errText(e) { return e.message + (e.detail?.requestId ? `\nRequest ID: ${e.detail.requestId}` : typeof e.detail === "string" ? "\n" + e.detail : ""); }
function callout(el, kind, text, ...extra) {
  if (!text) { el.className = "hidden mt"; el.innerHTML = ""; return; }
  el.className = `callout ${kind} mt`; el.innerHTML = "";
  el.append(icon(kind === "error" || kind === "warning" ? "alert" : kind === "success" ? "check" : "info"), h("span", {}, text, ...extra));
}
function fill(sel, items, valFn, textFn, first) {
  sel.innerHTML = "";
  if (first) sel.add(new Option(first[1], first[0]));
  items.forEach((i) => sel.add(new Option(textFn(i), valFn(i))));
}
async function busy(btn, fn, label) {
  const html = btn.innerHTML; btn.disabled = true;
  if (label) { btn.innerHTML = ""; btn.append(h("span", { class: "spin" }), label); }
  try { return await fn(); } finally { btn.innerHTML = html; btn.disabled = false; refreshChecklist(); }
}
function toast(t) {
  const el = $("toast"); el.querySelector("span").textContent = t; el.classList.remove("hidden");
  el.style.animation = "none"; void el.offsetWidth; el.style.animation = "";
  clearTimeout(toast.t); toast.t = setTimeout(() => el.classList.add("hidden"), 2800);
}
const fmtTime = (s) => s ? new Date(s).toLocaleString("zh-CN", { hour12: false, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" }) : "";
function relTime(s) {
  if (!s) return ""; const m = (Date.now() - new Date(s)) / 60000;
  if (m < 1) return "刚刚"; if (m < 60) return `${Math.floor(m)} 分钟前`; if (m < 1440) return `${Math.floor(m / 60)} 小时前`;
  if (m < 43200) return `${Math.floor(m / 1440)} 天前`; return fmtTime(s).slice(0, 10);
}
const sev = () => document.querySelector('input[name="sev"]:checked').value;
const sevKey = (s) => ({ minimal: "minimal", moderate: "moderate", critical: "critical", highestcriticalimpact: "highestcriticalimpact" })[(s || "").toLowerCase()] || null;
function sevBadge(s) { const k = sevKey(s); const [lv, nm] = SEV[k] || [s || "-", ""];
  return h("span", { class: "badge " + (k === "critical" || k === "highestcriticalimpact" ? "error" : k === "moderate" ? "warning" : "") }, `${lv} ${nm}`.trim()); }
function statusBadge(s) { const open = (s || "").toLowerCase() === "open"; return h("span", { class: "badge dot " + (open ? "success" : "") }, open ? "处理中" : s === "Closed" ? "已关闭" : s || "-"); }

// 模态框 (替代 confirm), 返回 Promise<boolean>
function modal({ title, body, kv, code, tone = "accent", ok = "确认", cancel = "取消", wide = false }) {
  return new Promise((resolve) => {
    const root = $("modal-root");
    const done = (v) => { root.innerHTML = ""; document.removeEventListener("keydown", onKey); resolve(v); };
    const onKey = (e) => { if (e.key === "Escape") done(false); };
    const card = h("div", { class: "card modal" + (wide ? " wide" : "") },
      h("div", { class: "icon " + tone }, icon(tone === "error" ? "alert" : code ? "code" : "info")),
      h("h3", { class: "t-title-3" }, title),
      body ? h("div", { class: "body" }, body) : null,
      kv ? h("dl", { class: "kv" }, kv.flatMap(([k, v]) => [h("dt", {}, k), h("dd", {}, v)])) : null,
      code ? h("pre", { class: "code" }, code) : null,
      h("div", { class: "actions" },
        cancel ? h("button", { class: "btn", onclick: () => done(false) }, cancel) : null,
        h("button", { class: "btn " + (tone === "error" ? "danger-solid" : "primary"), onclick: () => done(true) }, ok)));
    const wrap = h("div", { class: "modal-wrap", onclick: (e) => { if (e.target === wrap) done(false); } }, card);
    root.append(wrap); document.addEventListener("keydown", onKey);
    card.querySelector(".actions .btn:last-child").focus();
  });
}

// ---------------------------------------------------------------- 视图
function showView(v) {
  ["auth", "create", "history"].forEach((x) => $("view-" + x).classList.toggle("hidden", x !== v));
  document.querySelectorAll(".nav-item").forEach((x) => x.classList.toggle("active", x.dataset.view === v));
  if (v === "history") loadHistory();
}
document.querySelectorAll(".nav-item").forEach((b) => b.onclick = () => { if (state.tenant) showView(b.dataset.view); });

