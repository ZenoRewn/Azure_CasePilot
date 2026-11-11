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
    h("option", { value: "azure-openai" }, "Azure OpenAI (gpt-live-transcribe 流式)"), h("option", { value: "azure-speech" }, "Azure Speech (实时流式 + PSR)"));
  prov.value = c.stt_provider;
  const form = h("div", { class: "grid" }, f("chat_endpoint", "对话 Endpoint", "https://<resource>.openai.azure.com"), f("chat_deployment", "对话部署名"),
    h("label", { class: "field" }, h("span", { class: "lbl" }, "语音转写服务"), prov),
    f("stt_endpoint", "转写 Endpoint"), f("stt_stream_deployment", "流式部署名 (Azure OpenAI)", "gpt-live-transcribe"), f("stt_deployment", "整段转写部署名 (兜底)"), f("speech_region", "Speech 区域 (可选)"), f("stt_language", "语言", "zh-CN"));
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

// ---------------------------------------------------------------- 语音: 点击开始边说边出字 (流式), 再次点击结束
// 浏览器采集 16kHz PCM16, 经服务端中转到 Azure OpenAI Realtime / Azure Speech (PSR); 流式失败时退回整段转写
function toPcm16(chunks) {
  const n = chunks.reduce((a, c) => a + c.length, 0), out = new Int16Array(n); let o = 0;
  for (const c of chunks) for (let i = 0; i < c.length; i++) out[o++] = Math.max(-1, Math.min(1, c[i])) * 0x7fff;
  return new Uint8Array(out.buffer);
}
const b64 = (u8) => { let s = ""; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode(...u8.subarray(i, i + 0x8000)); return btoa(s); };
function wav(pcm) {
  const h = new DataView(new ArrayBuffer(44)), w = (o, t) => [...t].forEach((c, i) => h.setUint8(o + i, c.charCodeAt(0)));
  w(0, "RIFF"); h.setUint32(4, 36 + pcm.length, true); w(8, "WAVEfmt "); h.setUint32(16, 16, true); h.setUint16(20, 1, true);
  h.setUint16(22, 1, true); h.setUint32(24, 16000, true); h.setUint32(28, 32000, true); h.setUint16(32, 2, true); h.setUint16(34, 16, true);
  w(36, "data"); h.setUint32(40, pcm.length, true);
  const out = new Uint8Array(44 + pcm.length); out.set(new Uint8Array(h.buffer)); out.set(pcm, 44); return out;
}

let mic = null;
async function toggleMic(btn) {
  if (mic) { mic.stop(); return; }
  const target = $(btn.dataset.target), base = target.value ? target.value + " " : "";
  let stream;
  try { stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } }); }
  catch (_) { toast("无法访问麦克风, 请在浏览器中允许"); return; }
  const ctx = new AudioContext({ sampleRate: 16000 });
  await ctx.audioWorklet.addModule("/js/pcm-worklet.js");
  const node = new AudioWorkletNode(ctx, "pcm"), all = [];
  let pending = [], sid = null, sending = Promise.resolve(), stopped = false;
  const show = (t) => { if (t) { target.value = base + t; target.dispatchEvent(new Event("input")); } };
  const flush = () => {
    if (!sid || !pending.length) return;
    const pcm = toPcm16(pending); pending = [];
    sending = sending.then(() => api("ai/stream/push", { id: sid, audio: b64(pcm) }).then((d) => { if (!stopped) show(d.text); }))
      .catch(() => { sid = null; });   // 中途失败: 结束时整段转写
  };
  node.port.onmessage = (e) => { all.push(e.data); pending.push(e.data); };
  ctx.createMediaStreamSource(stream).connect(node);
  const timer = setInterval(flush, 200);
  mic = { stop: async () => {
    mic = null; stopped = true; clearInterval(timer);
    stream.getTracks().forEach((t) => t.stop()); ctx.close(); btn.classList.remove("rec");
    await busy(btn, async () => {
      try {
        if (sid) { flush(); await sending; }
        if (sid) {   // 流式结果为空 (如部署名错误时服务端静默无输出) 或出错: 退回整段转写
          const t = await api("ai/stream/stop", { id: sid }).then((d) => d.text, () => "");
          if (t) { show(t); return; }
        }
        const d = await api("ai/transcribe", { audio: b64(wav(toPcm16(all))), mime: "audio/wav", filename: "voice.wav", ai: aiSettings() });
        show(d.text);
      } catch (e) { toast("转写失败: " + e.message); }
      target.focus();
    });
  } };
  btn.classList.add("rec");
  toast("正在聆听, 再次点击麦克风结束");
  try { sid = (await api("ai/stream/start", { ai: aiSettings() })).id; }
  catch (e) { toast("实时转写不可用, 将在结束后整段转写: " + e.message); }
}
function bindMics(root = document) { root.querySelectorAll(".mic").forEach((b) => { b.onclick = () => toggleMic(b); }); }
bindMics();
