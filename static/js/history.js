"use strict";
// ---------------------------------------------------------------- 我的 Case
let hStatus = "";
async function loadHistory() {
  const tb = $("h-body");
  $("h-scope").textContent = state.sub ? `订阅 · ${$("subscription").selectedOptions[0]?.text || ""}` : "租户级 Case";
  tb.innerHTML = "";
  for (let i = 0; i < 4; i++) tb.append(h("tr", {}, [1, 2, 3, 4, 5].map(() => h("td", {}, h("div", { class: "skeleton" })))));
  try {
    state.tickets = await api("tickets", { tenantId: state.tenant, subscriptionId: state.sub || null, days: +$("h-days").value });
    updateOpenCount(); renderHistory();
  } catch (e) { tb.innerHTML = ""; tb.append(h("tr", { class: "empty" }, h("td", { colspan: 5 }, "加载失败: " + errText(e)))); }
}
function renderHistory() {
  const ts = state.tickets;
  $("st-all").textContent = ts.length;
  $("st-open").textContent = ts.filter((t) => t.status === "Open").length;
  $("st-closed").textContent = ts.filter((t) => t.status === "Closed").length;
  $("st-crit").textContent = ts.filter((t) => /critical/i.test(t.severity || "")).length;
  const q = $("h-search").value.trim().toLowerCase();
  const rows = ts.filter((t) => (!hStatus || t.status === hStatus) &&
    (!q || [t.supportTicketId, t.title, t.serviceDisplayName].some((v) => (v || "").toLowerCase().includes(q))));
  const tb = $("h-body"); tb.innerHTML = "";
  if (!rows.length) { tb.append(h("tr", { class: "empty" }, h("td", { colspan: 5 }, "该范围内没有 Case"))); return; }
  rows.forEach((t, i) => {
    const tr = h("tr", { class: "row", style: `animation-delay:${Math.min(i, 12) * 25}ms`, onclick: () => openTicket(t.name) },
      h("td", { class: "mono nowrap" }, t.supportTicketId || t.name),
      h("td", {}, h("span", { class: "ttl" }, t.title || ""), h("span", { class: "sub2" }, [t.serviceDisplayName, t.problemClassificationDisplayName].filter(Boolean).join(" · "))),
      h("td", {}, sevBadge(t.severity)), h("td", {}, statusBadge(t.status)),
      h("td", { class: "nowrap secondary", title: fmtTime(t.createdDate) }, relTime(t.createdDate)));
    tb.append(tr);
  });
}
$("h-search").oninput = renderHistory;
document.querySelectorAll("#h-status button").forEach((b) => b.onclick = () => {
  document.querySelectorAll("#h-status button").forEach((x) => x.classList.toggle("active", x === b));
  hStatus = b.dataset.st; renderHistory();
});
$("h-days").onchange = loadHistory;
$("btn-refresh").onclick = (ev) => busy(ev.currentTarget, loadHistory);

