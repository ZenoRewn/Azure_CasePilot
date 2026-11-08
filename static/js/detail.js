"use strict";
// ---------------------------------------------------------------- 详情抽屉
let current = null;
function openDrawer() { $("drawer").classList.remove("hidden"); $("mask").classList.remove("hidden"); }
function closeDrawer() { $("drawer").classList.add("hidden"); $("mask").classList.add("hidden"); current = null; }
$("d-x").onclick = closeDrawer; $("mask").onclick = closeDrawer;
document.addEventListener("keydown", (e) => { if (e.key === "Escape" && !$("modal-root").childElementCount) closeDrawer(); });
document.querySelectorAll("#d-tabs button").forEach((b) => b.onclick = () => switchTab(b.dataset.tab));
function switchTab(t) {
  document.querySelectorAll("#d-tabs button").forEach((x) => x.classList.toggle("active", x.dataset.tab === t));
  $("d-overview").classList.toggle("hidden", t !== "overview");
  $("d-comms-pane").classList.toggle("hidden", t !== "comms");
}

// 沟通记录为 HTML 邮件: DOMParser 解析为纯文本, 从不注入 HTML
function htmlToText(html) {
  const doc = new DOMParser().parseFromString(html || "", "text/html");
  doc.querySelectorAll("style,script,head").forEach((n) => n.remove());
  doc.querySelectorAll("br").forEach((b) => b.replaceWith("\n"));
  doc.querySelectorAll("p,div,li,tr,h1,h2,h3,h4").forEach((b) => b.append("\n"));
  return (doc.body.textContent || "").replace(/[ \t ]+\n/g, "\n").replace(/\n\s*\n+/g, "\n\n").trim();
}

async function openTicket(name) {
  current = name;
  $("d-id").textContent = "加载中…"; $("d-title").textContent = ""; $("d-status").innerHTML = ""; $("d-sev").innerHTML = "";
  $("d-props").innerHTML = ""; $("d-desc").textContent = ""; $("d-comms").innerHTML = ""; $("d-ccount").textContent = "";
  $("d-props").append(...[1, 2, 3, 4].map(() => h("div", {}, h("div", { class: "skeleton", style: "width:40%;margin-bottom:8px" }), h("div", { class: "skeleton" }))));
  callout($("d-msg"), "", ""); switchTab("overview"); openDrawer();
  try {
    const d = await api("ticket", { tenantId: state.tenant, subscriptionId: state.sub || null, name });
    if (current !== name) return;
    renderTicket(name, d);
  } catch (e) { $("d-id").textContent = "加载失败"; $("d-props").innerHTML = ""; $("d-desc").textContent = errText(e); }
}
function renderTicket(name, d) {
  const p = d.properties, cd = p.contactDetails || {}, closed = (p.status || "").toLowerCase() === "closed";
  $("d-id").textContent = p.supportTicketId || name;
  $("d-title").textContent = p.title || "";
  $("d-status").replaceChildren(statusBadge(p.status)); $("d-sev").replaceChildren(sevBadge(p.severity));
  const rid = `${state.sub ? "/subscriptions/" + state.sub : ""}/providers/Microsoft.Support/supportTickets/${name}`;
  $("d-portal").href = `${PORTAL[state.cloud] || PORTAL.AzureCloud}/#view/Microsoft_Azure_Support/SupportRequestDetails.ReactView/id/${encodeURIComponent(rid)}`;
  $("d-close-case").classList.toggle("hidden", closed);
  $("d-foot").classList.toggle("hidden", closed);
  const sla = p.serviceLevelAgreement;
  const props = [
    ["服务", p.serviceDisplayName], ["问题类型", p.problemClassificationDisplayName],
    ["创建时间", fmtTime(p.createdDate)], ["最后更新", `${fmtTime(p.modifiedDate)} (${relTime(p.modifiedDate)})`],
    ["SLA 首次响应截止", sla ? `${fmtTime(sla.expirationTime)}${sla.slaMinutes ? ` · ${sla.slaMinutes} 分钟` : ""}` : ""],
    ["支持计划", p.supportPlanDisplayName || p.supportPlanType],
    ["支持工程师", p.supportEngineer?.emailAddress], ["24×7 响应", p.require24X7Response ? "是" : "否"],
    ["联系人", [`${cd.lastName || ""}${cd.firstName || ""}`, cd.primaryEmailAddress].filter(Boolean).join(" · ")],
    ["语言 / 时区", [cd.preferredSupportLanguage, cd.preferredTimeZone].filter(Boolean).join(" · ")],
    ["关联资源", p.technicalTicketDetails?.resourceId, true],
    ["配额请求", p.quotaTicketDetails?.quotaChangeRequests?.map((q) => `${q.region}: ${q.payload}`).join("\n"), true],
  ];
  $("d-props").innerHTML = "";
  props.filter(([, v]) => v).forEach(([k, v, wide]) => $("d-props").append(h("div", { class: wide ? "wide" : "" }, h("dt", {}, k), h("dd", { class: wide ? "mono" : "" }, v))));
  $("d-desc").textContent = p.description || "";
  const comms = [...d.communications].reverse();
  $("d-ccount").textContent = comms.length;
  const ol = $("d-comms"); ol.innerHTML = "";
  if (!comms.length) ol.append(h("li", { class: "secondary" }, "暂无沟通记录"));
  comms.forEach((c, i) => {
    const ms = c.direction === "Outbound";
    const text = htmlToText(c.body);
    const body = h("div", { class: "msg-body" + (text.length > 400 ? " clamp" : "") }, text);
    const more = text.length > 400 ? h("button", { class: "msg-more", onclick: () => { body.classList.toggle("clamp"); more.textContent = body.classList.contains("clamp") ? "展开全文" : "收起"; } }, "展开全文") : null;
    ol.append(h("li", { class: ms ? "ms" : "me", style: `animation-delay:${Math.min(i, 8) * 30}ms` }, h("div", { class: "msg-card" },
      h("div", { class: "msg-head" }, h("span", { class: "badge " + (ms ? "accent" : "success") }, ms ? "Microsoft" : "我方"),
        h("span", { class: "subj", title: c.subject || "" }, c.subject || c.sender || ""), h("span", { class: "when", title: fmtTime(c.createdDate) }, relTime(c.createdDate))),
      body, more)));
  });
}
$("d-copy").onclick = () => { navigator.clipboard.writeText($("d-id").textContent); toast("已复制 Case 号"); };

$("d-close-case").onclick = async (ev) => {
  const btn = ev.currentTarget, name = current, id = $("d-id").textContent;
  const ok = await modal({ title: `关闭 Case ${id}?`, tone: "error", ok: "关闭 Case",
    body: "关闭后支持工程师将停止处理。如问题复发, 需要新建 Case 或在 Portal 中重新打开。" });
  if (!ok) return;
  await busy(btn, async () => {
    try {
      await api("close", { tenantId: state.tenant, subscriptionId: state.sub || null, name });
      toast(`Case ${id} 已关闭`);
      const t = state.tickets.find((x) => x.name === name); if (t) t.status = "Closed";
      updateOpenCount(); if (!$("view-history").classList.contains("hidden")) renderHistory();
      if (current === name) openTicket(name);
    } catch (e) { callout($("d-msg"), "error", errText(e)); $("d-foot").classList.remove("hidden"); }
  }, "关闭中…");
};

async function sendReply(btn) {
  const text = $("d-reply").value.trim();
  if (!text) return;
  await busy(btn, async () => {
    try {
      const esc = text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/\n/g, "<br>");
      await api("reply", { tenantId: state.tenant, subscriptionId: state.sub || null, name: current, subject: "客户补充信息 / Update from customer", body: esc });
      $("d-reply").value = ""; toast("补充信息已发送");
      await openTicket(current); switchTab("comms");
    } catch (e) { callout($("d-msg"), "error", errText(e)); }
  }, "发送中…");
}
$("d-send").onclick = (ev) => sendReply(ev.currentTarget);
$("d-reply").onkeydown = (e) => { if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) { e.preventDefault(); sendReply($("d-send")); } };

