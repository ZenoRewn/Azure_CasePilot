using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;

namespace CasePilot.Core.AI;

/// <summary>WebSocket 连接抽象 (测试可替换).</summary>
public interface IWebSocketConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken ct);
    Task SendTextAsync(string text, CancellationToken ct);
    Task SendBinaryAsync(byte[] data, CancellationToken ct);
    /// <summary>读取一条完整文本消息; 连接关闭时返回 null. 二进制消息忽略.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken ct);
}

/// <summary>一次流式识别: Push 16 kHz PCM 块, FinishAsync 返回最终文本. 连接失败时 FinishAsync 抛出.</summary>
public interface IStreamSession
{
    void Push(byte[] pcm16k);
    Task<string> FinishAsync(CancellationToken ct);
}

public interface IStreamingTranscriber
{
    /// <summary>立即返回会话并在后台连接; onText 在后台线程回调当前全文 (已确认 + 中间结果).</summary>
    IStreamSession Start(Action<string> onText);
}

/// <summary>流式转写: AOAI Realtime (gpt-live-transcribe) 或 Azure Speech 实时识别 + PSR.</summary>
public sealed class StreamingTranscriber(HttpClient http, IAuthProvider auth, Func<AIConfig> config, ISecretStore secrets,
    Func<IWebSocketConnection> socket, IClock clock) : IStreamingTranscriber
{
    public static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(15);

    public IStreamSession Start(Action<string> onText) => new Session(this, config().Trimmed(), onText);

    private sealed class Session : IStreamSession
    {
        private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
        private readonly StreamTranscript _t = new();
        private readonly Task _run;
        private readonly CancellationTokenSource _cts = new();

        public Session(StreamingTranscriber owner, AIConfig c, Action<string> onText) =>
            _run = Task.Run(() => owner.RunAsync(c, _audio.Reader, _t, onText, _cts.Token));

        public void Push(byte[] pcm16k) => _audio.Writer.TryWrite(pcm16k);

        public async Task<string> FinishAsync(CancellationToken ct)
        {
            _audio.Writer.TryComplete();
            _cts.CancelAfter(FinishTimeout + TimeSpan.FromSeconds(30));
            using var reg = ct.Register(_cts.Cancel);
            try { await _run; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && _t.Text.Length == 0) { throw AppError.Timeout(); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if (_t.Error is { } e) throw new AppError("实时转写错误: " + e);
            return _t.Text.Trim();
        }
    }

    private async Task RunAsync(AIConfig c, ChannelReader<byte[]> audio, StreamTranscript t, Action<string> onText, CancellationToken ct)
    {
        await using var ws = socket();
        var key = AIKeys.Effective(c, AIKind.Stt, secrets);
        var usesKey = key is not null;
        if (c.SttProvider == SttProvider.AzureSpeech) await RunSpeechAsync(ws, c, key, usesKey, audio, t, onText, ct);
        else await RunRealtimeAsync(ws, c, key, usesKey, audio, t, onText, ct);
    }

    /// <summary>测试连接: AOAI 收到 session.updated / Speech 取得 STS 并连上即成功.</summary>
    public async Task ProbeAsync(CancellationToken ct)
    {
        var c = config().Trimmed();
        await using var ws = socket();
        var key = AIKeys.Effective(c, AIKind.Stt, secrets);
        if (c.SttProvider == SttProvider.AzureSpeech)
        {
            await ConnectSpeechAsync(ws, c, key, key is not null, ct);
            return;
        }
        await ConnectRealtimeAsync(ws, c, key, key is not null, ct);
        await ws.SendTextAsync(RealtimeStt.SessionUpdate(c), ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(FinishTimeout);
        while (await ws.ReceiveTextAsync(cts.Token) is { } msg)
        {
            var e = System.Text.Json.Nodes.JsonNode.Parse(msg);
            var type = (string?)e?["type"];
            if (type == "session.updated") return;
            if (type == "error") throw new AppError("实时转写错误: " + ((string?)e?["error"]?["message"] ?? "未知"));
        }
        throw new AppError("实时转写连接已关闭");
    }

    private async Task ConnectRealtimeAsync(IWebSocketConnection ws, AIConfig c, string? key, bool usesKey, CancellationToken ct)
    {
        IReadOnlyList<KeyValuePair<string, string>> headers = usesKey
            ? [KeyValuePair.Create("api-key", key!)]
            : [KeyValuePair.Create("Authorization", "Bearer " + await auth.GetTokenAsync(null, AuthProvider.Cognitive, ct))];
        await ws.ConnectAsync(RealtimeStt.Url(c), headers, ct);
    }

    private async Task RunRealtimeAsync(IWebSocketConnection ws, AIConfig c, string? key, bool usesKey,
        ChannelReader<byte[]> audio, StreamTranscript t, Action<string> onText, CancellationToken ct)
    {
        await ConnectRealtimeAsync(ws, c, key, usesKey, ct);
        await ws.SendTextAsync(RealtimeStt.SessionUpdate(c), ct);
        var recv = ReceiveAsync(ws, t, RealtimeStt.Apply, onText, ct);
        await foreach (var chunk in audio.ReadAllAsync(ct))
        {
            if (t.Done) break;
            await ws.SendTextAsync(RealtimeStt.Append(chunk), ct);
        }
        if (!t.Done) await ws.SendTextAsync(RealtimeStt.Commit, ct);
        await WaitAsync(recv, ct);
    }

    private async Task RunSpeechAsync(IWebSocketConnection ws, AIConfig c, string? key, bool usesKey,
        ChannelReader<byte[]> audio, StreamTranscript t, Action<string> onText, CancellationToken ct)
    {
        await ConnectSpeechAsync(ws, c, key, usesKey, ct);
        var rid = Guid.NewGuid().ToString("N");
        await ws.SendTextAsync(SpeechStt.Config(rid, clock.UtcNow), ct);
        await ws.SendTextAsync(SpeechStt.Context(rid, clock.UtcNow, c.SttLanguage), ct);
        var recv = ReceiveAsync(ws, t, SpeechStt.Apply, onText, ct);
        await ws.SendBinaryAsync(SpeechStt.Audio(rid, clock.UtcNow, Pcm.StreamingWavHeader()), ct);
        await foreach (var chunk in audio.ReadAllAsync(ct))
            await ws.SendBinaryAsync(SpeechStt.Audio(rid, clock.UtcNow, chunk), ct);
        await ws.SendBinaryAsync(SpeechStt.Audio(rid, clock.UtcNow, []), ct);
        await WaitAsync(recv, ct);
    }

    private async Task ConnectSpeechAsync(IWebSocketConnection ws, AIConfig c, string? key, bool usesKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, SpeechStt.StsUrl(c, usesKey)) { Content = new ByteArrayContent([]) };
        if (usesKey) req.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
        else req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await auth.GetTokenAsync(null, AuthProvider.Cognitive, ct));
        using var resp = await http.SendAsync(req, ct);
        var sts = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        if (!resp.IsSuccessStatusCode)
            throw new AppError($"Speech 令牌获取失败 {(int)resp.StatusCode}" + ((int)resp.StatusCode is 401 or 403
                ? usesKey ? ", 请检查 Speech Key 与区域" : ", 请确认当前身份在该资源上具备 Cognitive Services User 角色" : ""), status: (int)resp.StatusCode);
        var region = c.SpeechRegion.Trim().Length > 0 ? c.SpeechRegion.Trim() : SpeechStt.RegionFromJwt(sts)
            ?? throw new AppError("无法确定 Speech 区域, 请在 设置 → AI 与语音 中填写区域。");
        await ws.ConnectAsync(SpeechStt.Url(region),
            [KeyValuePair.Create("Authorization", "Bearer " + sts), KeyValuePair.Create("X-ConnectionId", Guid.NewGuid().ToString("N"))], ct);
    }

    private static async Task ReceiveAsync(IWebSocketConnection ws, StreamTranscript t, Action<StreamTranscript, string> apply,
        Action<string> onText, CancellationToken ct)
    {
        while (!t.Done && await ws.ReceiveTextAsync(ct) is { } msg)
        {
            var before = t.Text;
            apply(t, msg);
            if (t.Text != before) onText(t.Text);
        }
    }

    private static async Task WaitAsync(Task recv, CancellationToken ct)
    {
        if (await Task.WhenAny(recv, Task.Delay(FinishTimeout, ct)) == recv) await recv;
    }
}

/// <summary>System.Net.WebSockets 实现.</summary>
public sealed class ClientWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _send = new(1, 1);

    public async Task ConnectAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken ct)
    {
        foreach (var (k, v) in headers) _ws.Options.SetRequestHeader(k, v);
        await _ws.ConnectAsync(uri, ct);
    }

    public Task SendTextAsync(string text, CancellationToken ct) => SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, ct);

    public Task SendBinaryAsync(byte[] data, CancellationToken ct) => SendAsync(data, WebSocketMessageType.Binary, ct);

    private async Task SendAsync(byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        await _send.WaitAsync(ct);
        try { await _ws.SendAsync(data, type, true, ct); }
        finally { _send.Release(); }
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            if (r.MessageType == WebSocketMessageType.Text) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        _ws.Dispose();
        _send.Dispose();
    }
}
