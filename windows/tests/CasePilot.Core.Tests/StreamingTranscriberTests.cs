using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using CasePilot.Core.Abstractions;
using CasePilot.Core.AI;
using CasePilot.Core.Tests.Fakes;

namespace CasePilot.Core.Tests;

/// <summary>脚本化 WebSocket: 收到某条消息后推送预设回复.</summary>
internal sealed class FakeSocket : IWebSocketConnection
{
    private readonly Channel<string?> _in = Channel.CreateUnbounded<string?>();
    public Uri? Uri { get; private set; }
    public Dictionary<string, string> Headers { get; } = [];
    public ConcurrentQueue<string> Texts { get; } = new();
    public ConcurrentQueue<byte[]> Binaries { get; } = new();
    public Exception? ConnectError { get; set; }
    /// <summary>(触发条件, 回复) — 发送的文本或二进制头含触发串时推送回复.</summary>
    public List<(string When, string[] Replies)> Script { get; } = [];
    public bool Disposed { get; private set; }

    public Task ConnectAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken ct)
    {
        if (ConnectError is not null) throw ConnectError;
        Uri = uri;
        foreach (var (k, v) in headers) Headers[k] = v;
        return Task.CompletedTask;
    }

    void Fire(string sent)
    {
        foreach (var (when, replies) in Script.ToList())
            if (sent.Contains(when, StringComparison.Ordinal))
            {
                Script.Remove((when, replies));
                foreach (var r in replies) _in.Writer.TryWrite(r);
            }
    }

    public Task SendTextAsync(string text, CancellationToken ct) { Texts.Enqueue(text); Fire(text); return Task.CompletedTask; }

    public Task SendBinaryAsync(byte[] data, CancellationToken ct)
    {
        Binaries.Enqueue(data);
        var n = BinaryPrimitives.ReadUInt16BigEndian(data);
        Fire(data.Length == 2 + n ? "<end>" : "<audio>");
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken ct) => await _in.Reader.ReadAsync(ct);

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

[TestClass]
public sealed class StreamingTranscriberTests
{
    sealed class Secrets(string? key = null) : ISecretStore
    {
        public string? Read(string target) => target == AIClient.SttKeyTarget ? key : null;
        public void Write(string target, string? value) { }
    }

    static (StreamingTranscriber, FakeSocket, FakeHttp) Make(AIConfig c, string? key = null)
    {
        var sock = new FakeSocket();
        var http = new FakeHttp();
        return (new StreamingTranscriber(http.Client(), new FakeAuth(), () => c, new Secrets(key), () => sock, new FakeClock()), sock, http);
    }

    [TestMethod]
    public async Task Realtime_StreamsDeltasAndReturnsCompleted()
    {
        var (st, sock, _) = Make(new AIConfig());
        sock.Script.Add(("input_audio_buffer.append", [
            """{"type":"conversation.item.input_audio_transcription.delta","delta":"虚拟"}""",
            """{"type":"conversation.item.input_audio_transcription.delta","delta":"机"}"""]));
        sock.Script.Add(("input_audio_buffer.commit", ["""{"type":"conversation.item.input_audio_transcription.completed","transcript":"虚拟机。"}"""]));
        var seen = new ConcurrentQueue<string>();
        var s = st.Start(seen.Enqueue);
        s.Push(new byte[3200]);
        s.Push(new byte[3200]);
        Assert.AreEqual("虚拟机。", await s.FinishAsync(default));
        Assert.AreEqual("wss://westus3-azureai-service-507154.openai.azure.com/openai/v1/realtime?intent=transcription", sock.Uri!.ToString());
        Assert.AreEqual("Bearer tok", sock.Headers["Authorization"]);
        StringAssert.Contains(sock.Texts.First(), "session.update");
        Assert.AreEqual(2, sock.Texts.Count(t => t.Contains("append")));
        CollectionAssert.Contains(seen.ToList(), "虚拟");
        Assert.AreEqual("虚拟机。", seen.Last());
        Assert.IsTrue(sock.Disposed);
    }

    [TestMethod]
    public async Task Realtime_KeyUsesApiKeyHeader_AndErrorThrows()
    {
        var (st, sock, _) = Make(new AIConfig(), key: "k1");
        sock.Script.Add(("session.update", ["""{"type":"error","error":{"message":"bad rate"}}"""]));
        var s = st.Start(_ => { });
        var e = await Assert.ThrowsExceptionAsync<CasePilot.Core.Models.AppError>(() => s.FinishAsync(default));
        StringAssert.Contains(e.Message, "bad rate");
        Assert.AreEqual("k1", sock.Headers["api-key"]);
        Assert.IsFalse(sock.Headers.ContainsKey("Authorization"));
    }

    [TestMethod]
    public async Task ConnectFailure_FinishThrows()
    {
        var (st, sock, _) = Make(new AIConfig());
        sock.ConnectError = new InvalidOperationException("401");
        var s = st.Start(_ => { });
        s.Push(new byte[10]);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => s.FinishAsync(default));
    }

    [TestMethod]
    public async Task Speech_StsThenPsrStream()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"region":"westus3"}""")).TrimEnd('=');
        var c = new AIConfig { SttProvider = SttProvider.AzureSpeech, SttEndpoint = "https://res.cognitiveservices.azure.com" };
        var (st, sock, http) = Make(c);
        http.Reply(200, $"h.{payload}.s");
        sock.Script.Add(("<audio>", [
            "Path:turn.start\r\n\r\n{}",
            "Path:speech.hypothesis\r\n\r\n{\"Text\":\"虚拟机\"}"]));
        sock.Script.Add(("<end>", [
            "Path:speech.phrase\r\n\r\n{\"RecognitionStatus\":\"Success\",\"DisplayText\":\"虚拟机无法连接。\"}",
            "Path:turn.end\r\n\r\n{}"]));
        var s = st.Start(_ => { });
        s.Push(new byte[3200]);
        Assert.AreEqual("虚拟机无法连接。", await s.FinishAsync(default));
        Assert.AreEqual("https://res.cognitiveservices.azure.com/sts/v1.0/issueToken", http.Requests[0].Url);
        Assert.AreEqual("Bearer tok", http.Requests[0].Headers["Authorization"]);
        Assert.AreEqual("wss://westus3.stt.speech.microsoft.com/stt/speech/universal/v2", sock.Uri!.ToString());
        Assert.AreEqual($"Bearer h.{payload}.s", sock.Headers["Authorization"]);
        Assert.AreEqual(32, sock.Headers["X-ConnectionId"].Length);
        var texts = sock.Texts.ToList();
        StringAssert.StartsWith(texts[0], "Path: speech.config");
        StringAssert.Contains(texts[1], "PostRefinement");
        var first = sock.Binaries.First();
        Assert.AreEqual(44, first.Length - 2 - BinaryPrimitives.ReadUInt16BigEndian(first));
        Assert.AreEqual(3, sock.Binaries.Count);
    }

    [TestMethod]
    public async Task Speech_KeyWithoutRegion_Throws()
    {
        var (st, _, http) = Make(new AIConfig { SttProvider = SttProvider.AzureSpeech }, key: "sk");
        var s = st.Start(_ => { });
        await Assert.ThrowsExceptionAsync<CasePilot.Core.Models.AppError>(() => s.FinishAsync(default));
        Assert.AreEqual(0, http.Requests.Count);
    }
}
