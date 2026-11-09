"use strict";
// ---------------------------------------------------------------- AI 帮写 & 语音输入
// AI 设置 (Endpoint / 部署 / 转写服务) 保存在本机 localStorage, 随请求传给服务端覆盖默认值
const aiSettings = () => { try { return JSON.parse(localStorage.getItem("cp.ai") || "{}"); } catch (_) { return {}; } };
let aiAnswers = [];

async function showAiModel() {
  try {
    const c = await api("ai/config", { ai: aiSettings() });
    $("ai-model").textContent = `${c.chat_deployment} · ${new URL(c.chat_endpoint).hostname.split(".")[0]}`;
    $("ai-model").title = "点击修改 AI 设置";
  } catch (_) {}
}
$("ai-model").style.cursor = "pointer";
$("ai-model").onclick = async () => {
  const c = { ...(await api("ai/config", { ai: aiSettings() })), ...aiSettings() };
  const f = (k, label, ph) => h("label", { class: "field" }, h("span", { class: "lbl" }, label), h("input", { class: "control", "data-k": k, value: c[k] || "", placeholder: ph || "" }));
  const prov = h("select", { class: "control", "data-k": "stt_provider" },
    h("option", { value: "azure-openai" }, "Azure OpenAI (gpt-4o-mini-transcribe)"), h("option", { value: "azure-speech" }, "Azure Speech (快速转写)"));
  prov.value = c.stt_provider;
  const form = h("div", { class: "grid" }, f("chat_endpoint", "对话 Endpoint", "https://<resource>.openai.azure.com"), f("chat_deployment", "对话部署名"),
    h("label", { class: "field" }, h("span", { class: "lbl" }, "语音转写服务"), prov),
    f("stt_endpoint", "转写 Endpoint"), f("stt_deployment", "转写部署名 (Azure OpenAI)"), f("stt_language", "语言", "zh-CN"));
  const root = $("modal-root");
  const close = () => { root.innerHTML = ""; };
  const save = () => {
    const v = {}; form.querySelectorAll("[data-k]").forEach((i) => { if (i.value.trim()) v[i.dataset.k] = i.value.trim(); });
    localStorage.setItem("cp.ai", JSON.stringify(v)); close(); showAiModel(); toast("AI 设置已保存到本机");
  };
  root.append(h("div", { class: "modal-wrap", onclick: (e) => { if (e.target.classList.contains("modal-wrap")) close(); } },
    h("div", { class: "card modal wide" }, h("h3", { class: "t-title-3" }, "AI 设置"),
      h("div", { class: "body" }, "使用当前登录身份 (Entra ID) 调用, 需要 Cognitive Services OpenAI User 角色。仅保存在本机。"),
      h("div", { class: "mt" }, form),
      h("div", { class: "actions" }, h("button", { class: "btn", onclick: () => { localStorage.removeItem("cp.ai"); close(); showAiModel(); } }, "恢复默认"),
        h("button", { class: "btn primary", onclick: save }, "保存")))));
};

function flash(id) { const el = $(id); el.classList.remove("filled"); void el.offsetWidth; el.classList.add("filled"); }

async function applyDraft(d) {
  if (d.service) {
    const s = state.services.find((x) => x.name === d.service.name);
    if (s) { await pickService(s); flash("svc-filter"); }
    if (d.problem) { $("problem").value = d.problem.id; onProblem(); flash("problem"); }
  }
  $("title").value = d.title; $("title").dispatchEvent(new Event("input")); flash("title");
  $("description").value = d.description; flash("description");
  const r = document.querySelector(`input[name="sev"][value="${d.severity}"]`); if (r) { r.checked = true; r.dispatchEvent(new Event("change")); }
  refreshChecklist();
}

function renderQuestions(qs, text) {
  const box = $("ai-qa");
  box.className = "qa"; box.innerHTML = "";
  box.append(h("div", { class: "t-caption secondary" }, "还需要一点信息 (可跳过):"));
  const inputs = qs.map((q, i) => {
    const id = "ai-a" + i;
    box.append(h("div", { class: "field" }, h("span", { class: "q" }, q),
      h("div", { class: "row" }, h("input", { class: "control", id, placeholder: "回答或留空" }),
        h("button", { type: "button", class: "btn ghost sm mic", "data-target": id, title: "语音输入" }, icon("mic")))));
    return [q, id];
  });
  bindMics(box);
  box.append(h("div", { class: "row", style: "justify-content:flex-end;gap:8px" },
    h("button", { class: "btn sm", onclick: () => { box.className = "hidden"; } }, "跳过, 直接用草稿"),
    h("button", { class: "btn primary sm", onclick: (ev) => {
      aiAnswers = inputs.map(([q, id]) => ({ q, a: $(id).value.trim() || "未知" }));
      runDraft(ev.currentTarget, text);
    } }, icon("sparkle"), "补充并更新草稿")));
}

async function runDraft(btn, text) {
  text = (text ?? $("ai-text").value).trim();
  if (!text) { $("ai-text").focus(); return; }
  await busy(btn, async () => {
    callout($("ai-msg"), "", "");
    try {
      const d = await api("ai/draft", { tenantId: state.tenant, text, answers: aiAnswers, ai: aiSettings() });
      await applyDraft(d);
      if (d.questions.length) renderQuestions(d.questions, text);
      else { $("ai-qa").className = "hidden"; aiAnswers = []; }
      callout($("ai-msg"), d.ready ? "success" : "info",
        d.ready ? "草稿已填好, 请检查后提交。" : !d.service ? "未能确定服务, 请手动选择。" : d.questions.length ? "已生成初稿, 回答下方问题可让描述更完整。" : "已生成草稿, 请确认问题类型。");
    } catch (e) { callout($("ai-msg"), "error", errText(e)); }
  }, "AI 生成中…");
}
$("ai-go").onclick = (ev) => { aiAnswers = []; runDraft(ev.currentTarget); };
$("ai-text").onkeydown = (e) => { if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) { e.preventDefault(); $("ai-go").click(); } };

// ---------------------------------------------------------------- 语音: 点击开始, 再次点击结束并转写
let recorder = null;
async function toggleMic(btn) {
  const target = $(btn.dataset.target);
  if (recorder) { recorder.stop(); return; }
  let stream;
  try { stream = await navigator.mediaDevices.getUserMedia({ audio: true }); }
  catch (_) { toast("无法访问麦克风, 请在浏览器中允许"); return; }
  const chunks = [];
  const mime = MediaRecorder.isTypeSupported("audio/webm") ? "audio/webm" : "audio/mp4";
  recorder = new MediaRecorder(stream, { mimeType: mime });
  recorder.ondataavailable = (e) => chunks.push(e.data);
  recorder.onstop = async () => {
    stream.getTracks().forEach((t) => t.stop());
    btn.classList.remove("rec"); recorder = null;
    const blob = new Blob(chunks, { type: mime });
    const b64 = await new Promise((r) => { const fr = new FileReader(); fr.onload = () => r(fr.result.split(",")[1]); fr.readAsDataURL(blob); });
    await busy(btn, async () => {
      try {
        const d = await api("ai/transcribe", { audio: b64, mime, filename: "voice." + mime.split("/")[1], ai: aiSettings() });
        target.value = (target.value ? target.value + " " : "") + d.text;
        target.dispatchEvent(new Event("input")); target.focus();
      } catch (e) { toast("转写失败: " + e.message); }
    });
  };
  recorder.start(); btn.classList.add("rec");
  toast("正在录音, 再次点击麦克风结束");
}
function bindMics(root = document) { root.querySelectorAll(".mic").forEach((b) => { b.onclick = () => toggleMic(b); }); }
bindMics();
