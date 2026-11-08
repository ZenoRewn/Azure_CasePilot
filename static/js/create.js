"use strict";
// ---------------------------------------------------------------- 范围
async function onTenant() {
  state.tenant = $("tenant").value;
  localStorage.setItem("cp.tenant", state.tenant);
  fill($("subscription"), [], null, null, ["", "加载中…"]);
  try {
    const subs = await api("subscriptions", { tenantId: state.tenant });
    fill($("subscription"), subs, (s) => s.subscriptionId,
      (s) => `${s.displayName} · ${s.subscriptionId.slice(0, 8)}${s.state !== "Enabled" ? " [" + s.state + "]" : ""}`, ["", "无 · 租户级 Case"]);
    const saved = localStorage.getItem("cp.sub." + state.tenant);
    if (saved !== null && (saved === "" || subs.some((s) => s.subscriptionId === saved))) $("subscription").value = saved;
    else if (subs.length) $("subscription").value = subs[0].subscriptionId;
  } catch (e) { fill($("subscription"), [], null, null, ["", "无 · 租户级 Case"]); toast("加载订阅失败: " + e.message); }
  onSub();
  try { state.services = await api("services", { tenantId: state.tenant }); } catch (e) { toast("加载服务失败: " + e.message); }
  renderChips();
  applyKind();
}
function onSub() {
  state.sub = $("subscription").value;
  state.allRes = null; state.plan = null;
  localStorage.setItem("cp.sub." + state.tenant, state.sub);
  $("pick-res-field").classList.toggle("hidden", !state.sub);
  loadResources(); detectPlan();
  if (!$("view-history").classList.contains("hidden")) loadHistory();
  refreshChecklist();
}
$("tenant").onchange = onTenant;
$("subscription").onchange = onSub;

// 支持计划: 无直接 API, 取该订阅最近一张 Case 的 supportPlanType 作为参考
async function detectPlan() {
  const sub = state.sub;
  try {
    const ts = await api("tickets", { tenantId: state.tenant, subscriptionId: sub || null, days: 365 });
    if (sub !== state.sub) return;
    state.tickets = ts;
    state.plan = ts.find((t) => t.supportPlanType)?.supportPlanType || null;
    updateOpenCount();
  } catch (_) { state.plan = null; }
  renderPlanHint();
}
function renderPlanHint() {
  const el = $("plan-hint"); el.innerHTML = "";
  if (state.plan) el.append(h("span", { class: "badge accent", title: "根据该订阅最近的 Case 推断" }, "支持计划: " + state.plan));
  else el.append(h("span", { class: "t-caption tertiary" }, "支持计划未知 (该订阅暂无历史 Case)"));
  const basic = /basic|developer/i.test(state.plan || "");
  document.querySelectorAll(".sev-opt").forEach((o) => {
    const v = o.querySelector("input").value;
    o.title = basic && (v === "critical" || v === "highestcriticalimpact") ? "当前支持计划可能不支持该级别" : "";
  });
}
function updateOpenCount() {
  const n = state.tickets.filter((t) => t.status === "Open").length;
  $("nav-open").textContent = n; $("nav-open").classList.toggle("hidden", !n);
}

// ---------------------------------------------------------------- 问题类别
document.querySelectorAll("[data-kind]").forEach((b) => b.onclick = () => {
  document.querySelectorAll("[data-kind]").forEach((x) => x.classList.toggle("active", x === b));
  state.kind = b.dataset.kind; applyKind();
});
function applyKind() {
  const k = KIND[state.kind];
  $("kind-hint").textContent = k.hint;
  $("quota-card").classList.add("hidden");
  $("svc-chips").classList.toggle("hidden", !!k.match);
  $("pick-res-field").classList.toggle("hidden", !!k.match || !state.sub);
  $("res-field").classList.toggle("hidden", !!k.match);
  if (k.match) {
    const s = state.services.find((x) => k.match.test(x.displayName));
    if (s) pickService(s); else toast("未找到对应服务, 请手动搜索");
  } else if (state.service && Object.values(KIND).some((x) => x.match && x.match.test(state.service.displayName))) {
    state.service = null; $("svc-filter").value = ""; fill($("problem"), [], null, null, ["", "先选择服务"]); $("problem").disabled = true;
  }
  refreshChecklist();
}

function renderChips() {
  const box = $("svc-chips"); box.innerHTML = "";
  const recent = JSON.parse(localStorage.getItem("cp.recentSvc") || "[]");
  const names = [...new Set([...recent, ...POPULAR])].slice(0, 6);
  names.map((n) => state.services.find((s) => s.displayName === n)).filter(Boolean).forEach((s) =>
    box.append(h("button", { type: "button", class: "chip", onclick: () => pickService(s) }, s.displayName)));
}

const svcInput = $("svc-filter"), svcList = $("svc-list");
function renderServices() {
  const terms = svcInput.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const list = state.services.filter((s) => terms.every((t) => s.displayName.toLowerCase().includes(t))).slice(0, 80);
  svcList.innerHTML = "";
  list.forEach((s, i) => svcList.append(h("li", { class: i === 0 ? "hl" : "", onmousedown: (e) => { e.preventDefault(); pickService(s); } },
    s.displayName, s.resourceTypes.length ? h("small", {}, s.resourceTypes.length + " 种资源") : null)));
  if (!list.length) svcList.append(h("li", { class: "empty" }, state.services.length ? "无匹配服务" : "服务列表加载中…"));
  svcList.classList.remove("hidden");
}
svcInput.onfocus = renderServices;
svcInput.oninput = () => { state.service = null; refreshChecklist(); renderServices(); };
svcInput.onblur = () => setTimeout(() => svcList.classList.add("hidden"), 120);
svcInput.onkeydown = (e) => {
  const items = [...svcList.querySelectorAll("li:not(.empty)")];
  let i = items.findIndex((li) => li.classList.contains("hl"));
  if (e.key === "ArrowDown" || e.key === "ArrowUp") {
    e.preventDefault(); items[i]?.classList.remove("hl");
    i = Math.max(0, Math.min(items.length - 1, i + (e.key === "ArrowDown" ? 1 : -1)));
    items[i]?.classList.add("hl"); items[i]?.scrollIntoView({ block: "nearest" });
  } else if (e.key === "Enter" && items[i]) { e.preventDefault(); items[i].dispatchEvent(new MouseEvent("mousedown")); }
  else if (e.key === "Escape") svcInput.blur();
};

async function pickService(s, preferResource) {
  if (!s) return;
  state.service = s;
  svcInput.value = s.displayName; svcList.classList.add("hidden");
  if (!KIND[state.kind].match) {
    const recent = JSON.parse(localStorage.getItem("cp.recentSvc") || "[]").filter((n) => n !== s.displayName);
    localStorage.setItem("cp.recentSvc", JSON.stringify([s.displayName, ...recent].slice(0, 4)));
  }
  const sel = $("problem"); sel.disabled = true; fill(sel, [], null, null, ["", "加载中…"]);
  refreshChecklist();
  try {
    const ps = await api("problems", { tenantId: state.tenant, service: s.name });
    if (state.service !== s) return;
    fill(sel, ps, (p) => p.id, (p) => p.displayName, ["", `— 请选择问题类型 (${ps.length}) —`]);
    sel.disabled = false;
    if (state.kind === "quota") {
      const vm = ps.find((p) => /compute-vm \(cores-vcpus\)/i.test(p.displayName));
      if (vm) sel.value = vm.id;
      onProblem();
    }
  } catch (e) { fill(sel, [], null, null, ["", "加载失败: " + e.message]); }
  await loadResources(preferResource);
  refreshChecklist();
}
function onProblem() {
  const isVmQuota = state.kind === "quota" && /compute-vm \(cores-vcpus\)/i.test($("problem").selectedOptions[0]?.text || "");
  $("quota-card").classList.toggle("hidden", !isVmQuota);
  refreshChecklist();
}
$("problem").onchange = onProblem;

async function loadResources(prefer) {
  const sel = $("resource");
  fill(sel, [], null, null, ["", "— 不指定 —"]);
  if (!state.sub || !state.service || !state.service.resourceTypes.length || KIND[state.kind].match) return;
  sel.options[0].text = "加载资源中…";
  const svc = state.service;
  try {
    const rs = await api("resources", { tenantId: state.tenant, subscriptionId: state.sub, resourceTypes: svc.resourceTypes });
    if (state.service !== svc) return;
    rs.forEach((r) => sel.add(new Option(`${r.name} · ${r.location || "-"}`, r.id)));
    sel.options[0].text = rs.length ? `— 不指定 (${rs.length} 个可选) —` : "— 该订阅下无此类资源 —";
    if (prefer) sel.value = prefer;
  } catch (_) { sel.options[0].text = "— 不指定 —"; }
}

// 按资源反查服务: 同一资源类型对应多个服务时, 优先与资源类型名相近且覆盖类型少 (更专一) 的服务
function rankServices(svcs, r) {
  const leaf = r.type.split("/").pop().replace(/s$/, "").toLowerCase();
  const words = leaf.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
  const score = (s) => {
    const n = s.displayName.toLowerCase();
    return (n.includes(words.slice(0, 7)) ? -100 : 0) + s.resourceTypes.length;
  };
  return [...svcs].sort((a, b) => score(a) - score(b));
}
$("btn-by-res").onclick = async (ev) => {
  const btn = ev.currentTarget;
  if (!state.allRes) {
    await busy(btn, async () => {
      try { state.allRes = await api("allResources", { tenantId: state.tenant, subscriptionId: state.sub }); }
      catch (e) { toast("加载资源失败: " + e.message); }
    }, "加载中…");
    if (!state.allRes) return;
  }
  const typeToSvc = new Map();
  state.services.forEach((s) => s.resourceTypes.forEach((t) => { const k = t.toLowerCase(); if (!typeToSvc.has(k)) typeToSvc.set(k, []); typeToSvc.get(k).push(s); }));
  const input = h("input", { class: "control", placeholder: `在 ${state.allRes.length} 个资源中搜索名称 / 类型 / 资源组`, autocomplete: "off" });
  const list = h("ul", { class: "popover", style: "position:static;margin-top:10px;box-shadow:none;max-height:50vh;animation:none" });
  const root = $("modal-root");
  const close = () => { root.innerHTML = ""; document.removeEventListener("keydown", onKey); };
  const onKey = (e) => { if (e.key === "Escape") close(); };
  const render = () => {
    const terms = input.value.toLowerCase().split(/\s+/).filter(Boolean);
    list.innerHTML = "";
    const rows = state.allRes.filter((r) => terms.every((t) => (r.name + " " + r.type + " " + r.group).toLowerCase().includes(t))).slice(0, 100);
    rows.forEach((r) => {
      const svcs = rankServices(typeToSvc.get(r.type) || [], r);
      list.append(h("li", { class: svcs.length ? "" : "empty", style: "flex-wrap:wrap" },
        h("span", { style: "flex:1;min-width:200px" }, h("div", { style: "font-weight:500" }, r.name),
          h("div", { class: "t-caption tertiary" }, `${r.type} · ${r.group} · ${r.location || "-"}`)),
        svcs.length ? h("span", { class: "chips" }, svcs.slice(0, 4).map((s) =>
          h("button", { type: "button", class: "chip", onclick: () => { close(); pickService(s, r.id); } }, s.displayName)))
          : h("small", {}, "无对应服务")));
    });
    if (!rows.length) list.append(h("li", { class: "empty" }, "无匹配资源"));
  };
  input.oninput = render;
  const wrap = h("div", { class: "modal-wrap", onclick: (e) => { if (e.target === wrap) close(); } },
    h("div", { class: "card modal wide" }, h("h3", { class: "t-title-3" }, "从订阅资源中选择"),
      h("div", { class: "body" }, "选择出问题的资源, 将自动匹配对应的支持服务并关联该资源。"),
      h("div", { class: "combo mt" }, icon("search"), input), list));
  root.innerHTML = ""; root.append(wrap); document.addEventListener("keydown", onKey);
  render(); input.focus();
};

// ---------------------------------------------------------------- 描述
$("btn-template").onclick = async () => {
  const ta = $("description");
  if (ta.value.trim() && !(await modal({ title: "覆盖当前描述?", body: "插入模板将替换已填写的内容。", ok: "覆盖" }))) return;
  ta.value = ["【现象】", "", "【业务影响】(影响用户/请求量, 是否生产环境)", "", "【首次发生时间 (含时区) / 是否持续】", "",
    "【复现步骤】", "1. ", "", "【错误信息 / Request ID / Correlation ID】", "", "【近期变更】", "", "【已尝试的排查】", ""].join("\n");
  ta.focus(); refreshChecklist();
};
$("title").oninput = () => { $("title-count").textContent = `${$("title").value.length} / 200`; refreshChecklist(); };
$("description").oninput = refreshChecklist;
["q-region", "q-sku", "q-limit"].forEach((id) => $(id).oninput = refreshChecklist);

document.querySelectorAll('input[name="sev"]').forEach((r) => r.onchange = () => {
  const high = sev() === "critical" || sev() === "highestcriticalimpact";
  $("h24").disabled = !high; $("h24").checked = high;
  refreshChecklist();
});

// ---------------------------------------------------------------- 联系人 (默认中文 / 中国, 本地记忆)
const CONTACT = ["c-first", "c-last", "c-email", "c-cc", "c-method", "c-phone", "c-tz", "c-country", "c-lang"];
fill($("c-tz"), TIMEZONES, (z) => z, (z) => z === "China Standard Time" ? "China Standard Time (UTC+8 北京)" : z);
fill($("c-country"), COUNTRIES, (c) => c[0], (c) => `${c[1]} (${c[0]})`);
Object.entries(DEFAULTS).forEach(([id, v]) => { $(id).value = v; });
try { const c = JSON.parse(localStorage.getItem("cp.contact") || "{}"); CONTACT.forEach((id) => { if (c[id]) $(id).value = c[id]; }); } catch (_) {}
CONTACT.forEach((id) => $(id).addEventListener("input", () => {
  localStorage.setItem("cp.contact", JSON.stringify(Object.fromEntries(CONTACT.map((i) => [i, $(i).value]))));
  $("phone-opt").textContent = $("c-method").value === "phone" ? "必填" : "可选";
  refreshChecklist();
}));

// ---------------------------------------------------------------- 检查清单
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
function checks() {
  const v = (id) => $(id).value.trim();
  const quota = !$("quota-card").classList.contains("hidden");
  return [
    ["选择服务与问题类型", !!state.service && !!$("problem").value, 1],
    quota ? ["配额区域 / 系列 / 新上限", !!(v("q-region") && v("q-sku") && +v("q-limit") > 0), "q"] : null,
    ["标题 (≥ 5 字)", v("title").length >= 5, 2],
    ["详细描述 (≥ 20 字)", v("description").length >= 20, 2],
    ["联系人姓名与有效邮箱", !!(v("c-first") && v("c-last") && EMAIL_RE.test(v("c-email"))), 4],
    ["电话 (首选电话联系时)", v("c-method") !== "phone" || !!v("c-phone"), 4],
  ].filter(Boolean);
}
function refreshChecklist() {
  const cs = checks(), done = cs.filter((c) => c[1]).length;
  $("checklist").innerHTML = "";
  cs.forEach(([t, ok]) => $("checklist").append(h("li", { class: ok ? "ok" : "" }, h("i"), t)));
  $("progress").style.width = `${(done / cs.length) * 100}%`;
  $("progress-text").textContent = `${done} / ${cs.length}`;
  const stepOk = {};
  cs.forEach(([, ok, st]) => { stepOk[st] = (stepOk[st] ?? true) && ok; });
  stepOk[3] = true;
  document.querySelectorAll(".step[data-step]").forEach((s) => s.classList.toggle("done", !!stepOk[s.dataset.step]));
  const sm = $("summary"); sm.innerHTML = "";
  const subText = state.sub ? ($("subscription").selectedOptions[0]?.text || "") : "租户级";
  [["范围", subText], ["服务", state.service?.displayName || "—"], ["严重性", SEV[sev()].join(" · ")], ["语言", $("c-lang").selectedOptions[0]?.text]]
    .forEach(([k, v]) => sm.append(h("dt", {}, k), h("dd", {}, v)));
  $("btn-create").disabled = done !== cs.length;
}

function buildBody(dryRun) {
  const v = (id) => $(id).value.trim();
  const quota = !$("quota-card").classList.contains("hidden");
  return {
    tenantId: state.tenant, subscriptionId: state.sub || null,
    serviceId: state.service?.id, problemClassificationId: $("problem").value,
    resourceId: KIND[state.kind].match ? null : $("resource").value || null,
    title: $("title").value, description: $("description").value,
    severity: sev(), require24X7Response: $("h24").checked, advancedDiagnosticConsent: $("consent").checked,
    problemStartTime: $("start").value ? new Date($("start").value).toISOString() : null, dryRun,
    quota: quota ? { region: v("q-region"), sku: v("q-sku"), newLimit: +v("q-limit") } : null,
    contact: { firstName: v("c-first"), lastName: v("c-last"), email: v("c-email"), cc: $("c-cc").value,
      method: $("c-method").value, phone: v("c-phone"), timeZone: $("c-tz").value, country: $("c-country").value, language: $("c-lang").value },
  };
}

$("btn-preview").onclick = (ev) => busy(ev.currentTarget, async () => {
  try {
    const d = await api("create", buildBody(true));
    await modal({ title: "请求体预览", body: `PUT ${d.scope}/supportTickets/{name}`, code: JSON.stringify(d.request, null, 2), cancel: null, ok: "关闭", wide: true });
  } catch (e) { callout($("create-msg"), "error", errText(e)); }
});

$("btn-create").onclick = async (ev) => {
  const btn = ev.currentTarget, body = buildBody(false);
  const highWarn = /basic|developer/i.test(state.plan || "") && (body.severity === "critical" || body.severity === "highestcriticalimpact");
  const ok = await modal({
    title: "确认创建 Support Case?", tone: highWarn ? "error" : "accent", ok: "创建",
    body: highWarn ? `当前订阅支持计划 (${state.plan}) 可能不支持 ${SEV[body.severity][0]} 级, 提交可能被拒绝。` : "提交后 Microsoft 支持会按所选严重性与联系方式与你联系。",
    kv: [["标题", body.title], ["服务", state.service.displayName], ["严重性", SEV[body.severity].join(" · ")],
      ["范围", body.subscriptionId ? $("subscription").selectedOptions[0].text : "租户级"], ["联系人", `${body.contact.lastName}${body.contact.firstName} · ${body.contact.email}`]],
  });
  if (!ok) return;
  await busy(btn, async () => {
    callout($("create-msg"), "info", "正在提交, 通常需要 10–60 秒…");
    try {
      const d = await api("create", body);
      const link = h("a", { href: "#", onclick: (e) => { e.preventDefault(); openTicket(d.name); } }, "查看详情 →");
      callout($("create-msg"), "success", `已创建 Case ${d.supportTicketId || d.name}\n`, link);
      toast("Case 已创建 · " + (d.supportTicketId || d.name));
      ["title", "description", "start", "q-region", "q-sku", "q-limit"].forEach((id) => { $(id).value = ""; });
      $("title-count").textContent = "0 / 200";
      detectPlan();
    } catch (e) { callout($("create-msg"), "error", errText(e)); }
  }, "提交中…");
};

