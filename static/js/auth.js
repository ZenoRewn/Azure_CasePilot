"use strict";
// ---------------------------------------------------------------- 认证
document.querySelectorAll("[data-auth]").forEach((b) => b.onclick = () => {
  document.querySelectorAll("[data-auth]").forEach((x) => x.classList.toggle("active", x === b));
  $("auth-cli").classList.toggle("hidden", b.dataset.auth !== "cli");
  $("auth-device").classList.toggle("hidden", b.dataset.auth !== "device");
  $("auth-sp").classList.toggle("hidden", b.dataset.auth !== "sp");
});
function setAuthed(name, meta, cloud) {
  state.cloud = cloud;
  $("identity").classList.remove("hidden");
  $("who-name").textContent = name; $("who-meta").textContent = meta;
  $("avatar").textContent = (name || "?").trim()[0].toUpperCase();
  $("topbar").classList.remove("hidden");
  callout($("auth-msg"), "", "");
  showView("create");
}
async function loginCli(silent) {
  if (!silent) callout($("auth-msg"), "info", "正在读取 Azure CLI 会话…");
  try {
    const d = await api("login/cli");
    setAuthed(d.user, `Azure CLI · ${d.cloud}`, d.cloud);
    await afterLogin(d.tenant);
  } catch (e) {
    callout($("auth-msg"), silent ? "warning" : "error",
      (silent ? "未检测到可用的 Azure CLI 会话。请在终端执行 az login 后点击上方按钮, 或改用 App Registration。\n\n" : "") + errText(e));
  }
}
$("btn-cli").onclick = (ev) => busy(ev.currentTarget, () => loginCli(false), "连接中…");
$("btn-sp").onclick = (ev) => busy(ev.currentTarget, async () => {
  try {
    const d = await api("login/sp", { tenantId: $("sp-tenant").value, clientId: $("sp-client").value, clientSecret: $("sp-secret").value, cloud: $("sp-cloud").value });
    $("sp-secret").value = "";
    localStorage.setItem("cp.sp", JSON.stringify({ t: $("sp-tenant").value, c: $("sp-client").value, cloud: $("sp-cloud").value }));
    setAuthed("App Registration", `${$("sp-client").value.slice(0, 8)}… · ${d.cloud}`, d.cloud);
    await afterLogin(d.tenant);
  } catch (e) { callout($("auth-msg"), "error", errText(e)); }
}, "验证中…");
$("btn-logout").onclick = async () => {
  if (!(await modal({ title: "切换身份?", body: "将清除当前进程内的令牌, 并返回登录页。", ok: "切换" }))) return;
  await api("logout"); location.reload();
};

async function afterLogin(defaultTenant) {
  let tenants = [];
  try { tenants = await api("tenants"); } catch (e) { toast("加载租户失败: " + e.message); }
  if (!tenants.length && defaultTenant) tenants = [{ tenantId: defaultTenant, displayName: defaultTenant, domain: "" }];
  fill($("tenant"), tenants, (t) => t.tenantId, (t) => `${t.displayName}${t.domain ? " · " + t.domain : ""}`);
  const saved = localStorage.getItem("cp.tenant");
  const pick = tenants.find((t) => t.tenantId === saved) || tenants.find((t) => t.tenantId === defaultTenant);
  if (pick) $("tenant").value = pick.tenantId;
  await onTenant();
}


// 设备码登录: 获取验证码 → 用户在浏览器完成登录 → 轮询换取令牌
$("btn-device").onclick = (ev) => busy(ev.currentTarget, async () => {
  callout($("auth-msg"), "", "");
  try {
    const d = await api("login/device/start", { cloud: $("dv-cloud").value });
    $("dv-usercode").textContent = d.userCode;
    $("dv-link").href = d.verificationUri; $("dv-link").textContent = d.verificationUri;
    $("dv-code").classList.remove("hidden");
    navigator.clipboard?.writeText(d.userCode).catch(() => {});
    window.open(d.verificationUri, "_blank", "noopener");
    for (;;) {
      await new Promise((r) => setTimeout(r, 4000));
      const p = await api("login/device/poll");
      if (!p.pending) { setAuthed(p.user, `浏览器登录 · ${p.cloud}`, p.cloud); await afterLogin(p.tenant); return; }
    }
  } catch (e) { callout($("auth-msg"), "error", errText(e)); }
}, "等待浏览器中完成登录…");
$("btn-quit").onclick = async () => {
  if (!(await modal({ title: "退出 CasePilot?", body: "将停止本地服务, 关闭此页面即可。", ok: "退出" }))) return;
  await api("quit").catch(() => {});
  document.body.innerHTML = '<div style="display:grid;place-items:center;height:100vh;color:#6b7485">CasePilot 已退出, 可关闭此页面。</div>';
};
