"use strict";
// ---------------------------------------------------------------- 启动
try { const sp = JSON.parse(localStorage.getItem("cp.sp") || "null"); if (sp) { $("sp-tenant").value = sp.t; $("sp-client").value = sp.c; $("sp-cloud").value = sp.cloud; } } catch (_) {}
$("phone-opt").textContent = $("c-method").value === "phone" ? "必填" : "可选";
refreshChecklist();
loginCli(true);
