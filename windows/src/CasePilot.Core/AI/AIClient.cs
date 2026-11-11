using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;

namespace CasePilot.Core.AI;

/// <summary>Azure OpenAI / Foundry 对话 + 语音转写. 未配置 Key 时使用当前身份的 Entra 令牌.</summary>
public sealed class AIClient(HttpClient http, IAuthProvider auth, IDelay delay, Func<AIConfig> config, ISecretStore secrets) : IChat, ITranscriber
{
    public const string ChatKeyTarget = "CasePilot/ai/aoai-key";
    public const string SttKeyTarget = "CasePilot/ai/stt-key";
    public const int MaxRetries = 2;
    public static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, string? jsonSchema, CancellationToken ct)
    {
        var c = config().Trimmed();
        var body = new JsonObject
        {
            ["model"] = c.ChatDeployment,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode?)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["reasoning_effort"] = "none",
        };
        if (jsonSchema is not null)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "casepilot", ["strict"] = true, ["schema"] = JsonNode.Parse(jsonSchema) },
            };
        JsonObject j;
        try { j = await PostAsync(c, SttUrls.Chat(c), Encoding.UTF8.GetBytes(body.ToJsonString()), "application/json", AIKind.Chat, ct); }
        catch (AppError e) when (e.Status == 400 && e.Message.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase))
        {
            // 非推理模型不认识 reasoning_effort: 去掉后重试一次
            body.Remove("reasoning_effort");
            j = await PostAsync(c, SttUrls.Chat(c), Encoding.UTF8.GetBytes(body.ToJsonString()), "application/json", AIKind.Chat, ct);
        }
        return j["choices"]?[0]?["message"]?["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "{}";
    }

    public async Task<string> TranscribeAsync(byte[] audio, string filename, string mime, CancellationToken ct)
    {
        var c = config().Trimmed();
        var (body, contentType) = Multipart.Build(c, audio, filename, mime, "cp" + Guid.NewGuid().ToString("N"));
        var j = await PostAsync(c, SttUrls.For(c), body, contentType, AIKind.Stt, ct);
        if (c.SttProvider == SttProvider.AzureSpeech)
            return string.Concat((j["combinedPhrases"] as JsonArray ?? [])
                .Select(p => p?["text"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : ""));
        return j["text"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : "";
    }

    private async Task<JsonObject> PostAsync(AIConfig c, string url, byte[] body, string contentType, AIKind kind, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new AppError($"Endpoint 无效: {url}");
        var key = secrets.Read(kind == AIKind.Chat ? ChatKeyTarget : SttKeyTarget);
        var usesKey = !string.IsNullOrEmpty(key);
        var bearer = usesKey ? null : await auth.GetTokenAsync(null, AuthProvider.Cognitive, ct);
        var headers = AIHeaders.For(c, kind, key, bearer);

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
            req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            HttpResponseMessage resp;
            string text;
            try
            {
                resp = await http.SendAsync(req, cts.Token);
                text = await resp.Content.ReadAsStringAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw AppError.Timeout(); }
            using var _ = resp;
            var status = (int)resp.StatusCode;
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRetries)
            {
                await delay.DelayAsync(RetryWait(resp), ct);
                continue;
            }
            JsonObject j;
            try { j = JsonNode.Parse(text) as JsonObject ?? []; }
            catch (JsonException) { j = []; }
            if (status >= 400)
            {
                var code = j["error"]?["code"] is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : "";
                var msg = j["error"]?["message"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : "";
                var hint = !usesKey && msg.Contains("does not match resource tenant", StringComparison.OrdinalIgnoreCase)
                    ? "该 AI 资源属于其他租户, 当前登录身份的默认租户无法访问。请在 设置 → AI 与语音 中填写 Key, 或换用本租户内的 Endpoint。"
                    : status is 401 or 403
                    ? usesKey ? "请检查 设置 → AI 与语音 中的 Key 与 Endpoint / 区域。"
                              : "请确认当前身份在该 AI 资源上具备 Cognitive Services OpenAI User 角色, 或在设置中填写 Key。"
                    : status == 429 ? "配额不足, 可在 设置 → AI 与语音 中换用其他部署或区域。" : null;
                var requestId = resp.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault()
                    : resp.Headers.TryGetValues("apim-request-id", out var aids) ? aids.FirstOrDefault() : null;
                throw new AppError($"AI 服务错误 {status}: {code} {msg}" + (hint is null ? "" : "\n" + hint), requestId, status, hint);
            }
            return j;
        }
    }

    private static TimeSpan RetryWait(HttpResponseMessage resp)
    {
        var wait = resp.Headers.TryGetValues("Retry-After", out var vs) && int.TryParse(vs.FirstOrDefault(), out var s) && s >= 0
            ? TimeSpan.FromSeconds(s) : DefaultRetry;
        return wait > MaxRetryWait ? MaxRetryWait : wait;
    }
}
