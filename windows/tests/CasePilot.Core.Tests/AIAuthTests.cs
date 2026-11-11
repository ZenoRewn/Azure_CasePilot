using System.Text;
using System.Text.Json.Nodes;
using CasePilot.Core.Abstractions;
using CasePilot.Core.AI;
using CasePilot.Core.Models;
using CasePilot.Core.Tests.Fakes;

namespace CasePilot.Core.Tests;

[TestClass]
public sealed class AIAuthTests
{
    sealed class Sec(string? chat, string? stt) : ISecretStore
    {
        public string? Read(string t) => t == AIClient.ChatKeyTarget ? chat : t == AIClient.SttKeyTarget ? stt : null;
        public void Write(string t, string? v) { }
    }

    [TestMethod]
    public void EntraIgnoresStoredKey_KeyUsesIt()
    {
        var s = new Sec("k", "s");
        Assert.IsNull(AIKeys.Effective(new AIConfig { ChatAuth = AIAuth.Entra }, AIKind.Chat, s));
        Assert.AreEqual("k", AIKeys.Effective(new AIConfig { ChatAuth = AIAuth.Key }, AIKind.Chat, s));
        Assert.IsNull(AIKeys.Effective(new AIConfig { SttAuth = AIAuth.Entra }, AIKind.Stt, s));
        Assert.AreEqual("s", AIKeys.Effective(new AIConfig { SttAuth = AIAuth.Key }, AIKind.Stt, s));
        Assert.IsNull(AIKeys.Effective(new AIConfig { ChatAuth = AIAuth.Key }, AIKind.Chat, new Sec(null, null)));
    }

    [TestMethod]
    public void LegacyInfersFromStoredKey_AndRoundTrips()
    {
        var legacy = AIConfig.FromJson("""{"chatDeployment":"x"}""");
        Assert.IsNull(legacy.ChatAuth);
        var m = legacy.Migrated(new Sec("k", null));
        Assert.AreEqual(AIAuth.Key, m.ChatAuth);
        Assert.AreEqual(AIAuth.Entra, m.SttAuth);
        var r = AIConfig.FromJson(m.ToJson());
        Assert.AreEqual(AIAuth.Key, r.ChatAuth);
        Assert.AreEqual(AIAuth.Entra, r.SttAuth);
        // 已显式选择的不被覆盖
        Assert.AreEqual(AIAuth.Entra, (r with { ChatAuth = AIAuth.Entra }).Migrated(new Sec("k", "s")).ChatAuth);
    }

    [TestMethod]
    public async Task Chat_EntraModeUsesBearerEvenWithStoredKey()
    {
        var http = new FakeHttp(); http.Reply(200, """{"choices":[{"message":{"content":"{}"}}]}""");
        var c = new AIClient(http.Client(), new FakeAuth(), new FakeDelay(), () => new AIConfig { ChatAuth = AIAuth.Entra }, new Sec("k", null));
        await c.ChatAsync([new("user", "hi")], null, default);
        Assert.AreEqual("Bearer tok", http.Requests[0].Headers["Authorization"]);
        Assert.IsFalse(http.Requests[0].Headers.ContainsKey("api-key"));
    }

    [TestMethod]
    public async Task Ping_SendsMinimalRequest()
    {
        var http = new FakeHttp(); http.Reply(200, """{"choices":[{"message":{"content":"pong"}}]}""");
        var c = new AIClient(http.Client(), new FakeAuth(), new FakeDelay(), () => new AIConfig(), new Sec(null, null));
        await c.PingAsync(default);
        var b = JsonNode.Parse(http.Requests[0].Body)!;
        Assert.AreEqual("ping", (string)b["messages"]![0]!["content"]!);
        Assert.AreEqual(16, (int)b["max_completion_tokens"]!);
        Assert.IsNull(b["response_format"]);
    }

    [TestMethod]
    public void RoleHints()
    {
        Assert.AreEqual("Cognitive Services OpenAI User", AIKeys.Role(new AIConfig(), AIKind.Chat));
        Assert.AreEqual("Cognitive Services OpenAI User", AIKeys.Role(new AIConfig(), AIKind.Stt));
        Assert.AreEqual("Cognitive Services User", AIKeys.Role(new AIConfig { SttProvider = SttProvider.AzureSpeech }, AIKind.Stt));
    }
}

[TestClass]
public sealed class StreamingAuthTests
{
    sealed class Secrets(string? key) : ISecretStore
    {
        public string? Read(string target) => target == AIClient.SttKeyTarget ? key : null;
        public void Write(string target, string? value) { }
    }

    static StreamingTranscriber Make(AIConfig c, FakeSocket sock, FakeHttp? http = null, string? key = null) =>
        new((http ?? new FakeHttp()).Client(), new FakeAuth(), () => c, new Secrets(key), () => sock, new FakeClock());

    [TestMethod]
    public async Task Realtime_EntraModeIgnoresStoredKey()
    {
        var sock = new FakeSocket();
        sock.Script.Add(("input_audio_buffer.commit", ["""{"type":"conversation.item.input_audio_transcription.completed","transcript":"x"}"""]));
        var st = Make(new AIConfig { SttAuth = AIAuth.Entra }, sock, key: "k1");
        Assert.AreEqual("x", await st.Start(_ => { }).FinishAsync(default));
        Assert.AreEqual("Bearer tok", sock.Headers["Authorization"]);
        Assert.IsFalse(sock.Headers.ContainsKey("api-key"));
    }

    [TestMethod]
    public async Task Probe_RealtimeSucceedsOnSessionUpdated()
    {
        var sock = new FakeSocket();
        sock.Script.Add(("session.update", ["""{"type":"session.updated"}"""]));
        await Make(new AIConfig(), sock).ProbeAsync(default);
        Assert.IsTrue(sock.Disposed);
    }

    [TestMethod]
    public async Task Probe_RealtimeErrorThrows()
    {
        var sock = new FakeSocket();
        sock.Script.Add(("session.update", ["""{"type":"error","error":{"message":"DeploymentNotFound"}}"""]));
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => Make(new AIConfig(), sock).ProbeAsync(default));
        StringAssert.Contains(e.Message, "DeploymentNotFound");
    }

    [TestMethod]
    public async Task Probe_SpeechStsThenConnect()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"region":"westus3"}""")).TrimEnd('=');
        var http = new FakeHttp(); http.Reply(200, $"h.{payload}.s");
        var sock = new FakeSocket();
        await Make(new AIConfig { SttProvider = SttProvider.AzureSpeech, SttEndpoint = "https://res" }, sock, http).ProbeAsync(default);
        Assert.AreEqual("wss://westus3.stt.speech.microsoft.com/stt/speech/universal/v2", sock.Uri!.ToString());
        Assert.IsTrue(sock.Disposed);
    }

    [TestMethod]
    public async Task Probe_SpeechSts401_ExplainsRole()
    {
        var http = new FakeHttp(); http.Reply(401, "");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() =>
            Make(new AIConfig { SttProvider = SttProvider.AzureSpeech, SttEndpoint = "https://res" }, new FakeSocket(), http).ProbeAsync(default));
        StringAssert.Contains(e.Message, "Cognitive Services User");
    }
}
