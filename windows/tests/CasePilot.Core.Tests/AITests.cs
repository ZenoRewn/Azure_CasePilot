using System.Text;
using System.Text.Json.Nodes;
using CasePilot.Core.Abstractions;
using CasePilot.Core.AI;
using CasePilot.Core.Models;
using CasePilot.Core.Tests.Fakes;

namespace CasePilot.Core.Tests;

[TestClass]
public sealed class AIConfigTests
{
    [TestMethod]
    public void Defaults_MatchMac()
    {
        var c = new AIConfig();
        Assert.AreEqual("gpt-6-luna", c.ChatDeployment);
        StringAssert.Contains(c.ChatEndpoint, "westus3");
        Assert.AreEqual(SttProvider.AzureOpenAI, c.SttProvider);
        Assert.AreEqual("gpt-4o-mini-transcribe", c.SttDeployment);
        Assert.AreEqual("zh-CN", c.SttLanguage);
        Assert.AreEqual("", c.SpeechRegion);
    }

    [TestMethod]
    public void FromJson_OldSettingsWithoutSpeechRegion()
    {
        var c = AIConfig.FromJson("""{"chatEndpoint":"https://x","chatDeployment":"d","sttProvider":"azure-openai","sttEndpoint":"https://y","sttDeployment":"t","sttLanguage":"zh-CN"}""");
        Assert.AreEqual("d", c.ChatDeployment);
        Assert.AreEqual("https://y", c.SttEndpoint);
        Assert.AreEqual("", c.SpeechRegion);
    }

    [TestMethod]
    public void FromJson_PartialBrokenOrNull_FallsBackToDefaults()
    {
        var d = new AIConfig();
        Assert.AreEqual(d, AIConfig.FromJson(null));
        Assert.AreEqual(d, AIConfig.FromJson("not json"));
        Assert.AreEqual(d, AIConfig.FromJson("[1]"));
        var c = AIConfig.FromJson("""{"sttProvider":"azure-speech","chatDeployment":5,"speechRegion":"eastasia"}""");
        Assert.AreEqual(SttProvider.AzureSpeech, c.SttProvider);
        Assert.AreEqual(d.ChatDeployment, c.ChatDeployment);
        Assert.AreEqual("eastasia", c.SpeechRegion);
        Assert.AreEqual(SttProvider.AzureOpenAI, AIConfig.FromJson("""{"sttProvider":"bogus"}""").SttProvider);
    }

    [TestMethod]
    public void ToJson_RoundTrips_AndNeverContainsKeys()
    {
        var c = new AIConfig { SttProvider = SttProvider.AzureSpeech, SpeechRegion = "southeastasia" };
        var json = c.ToJson();
        Assert.AreEqual(c, AIConfig.FromJson(json));
        StringAssert.Contains(json, "\"azure-speech\"");
        Assert.IsFalse(json.Contains("key", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Trimmed_StripsSpacesAndSlashes()
    {
        var c = new AIConfig { ChatEndpoint = " https://a/ ", SttEndpoint = "https://b//" }.Trimmed();
        Assert.AreEqual("https://a", c.ChatEndpoint);
        Assert.AreEqual("https://b", c.SttEndpoint);
    }
}

[TestClass]
public sealed class AIHeadersAndUrlTests
{
    [TestMethod]
    public void Headers_Routing()
    {
        var c = new AIConfig();
        CollectionAssert.AreEqual(new[] { KeyValuePair.Create("Authorization", "Bearer b") }, AIHeaders.For(c, AIKind.Chat, null, "b").ToArray());
        CollectionAssert.AreEqual(new[] { KeyValuePair.Create("Authorization", "Bearer b") }, AIHeaders.For(c, AIKind.Chat, "", "b").ToArray());
        CollectionAssert.AreEqual(new[] { KeyValuePair.Create("api-key", "k") }, AIHeaders.For(c, AIKind.Chat, "k", null).ToArray());
        CollectionAssert.AreEqual(new[] { KeyValuePair.Create("api-key", "t") }, AIHeaders.For(c, AIKind.Stt, "t", null).ToArray());
        var s = c with { SttProvider = SttProvider.AzureSpeech };
        CollectionAssert.AreEqual(new[] { KeyValuePair.Create("Ocp-Apim-Subscription-Key", "s") }, AIHeaders.For(s, AIKind.Stt, "s", null).ToArray());
        CollectionAssert.AreEqual(new[] { KeyValuePair.Create("api-key", "k") }, AIHeaders.For(s, AIKind.Chat, "k", null).ToArray());
    }

    [TestMethod]
    public void SttUrls_PerProvider()
    {
        var c = new AIConfig { SttEndpoint = "https://y", SttDeployment = "t" };
        Assert.AreEqual("https://y/openai/deployments/t/audio/transcriptions?api-version=2025-04-01-preview", SttUrls.For(c));
        var s = c with { SttProvider = SttProvider.AzureSpeech, SpeechRegion = "southeastasia" };
        Assert.AreEqual("https://southeastasia.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2024-11-15", SttUrls.For(s));
        Assert.AreEqual("https://y/speechtotext/transcriptions:transcribe?api-version=2024-11-15", SttUrls.For(s with { SpeechRegion = "" }));
        Assert.AreEqual("https://x/openai/v1/chat/completions", SttUrls.Chat(new AIConfig { ChatEndpoint = "https://x" }));
    }

    [TestMethod]
    public void Multipart_OpenAIFields()
    {
        var (body, ct) = Multipart.Build(new AIConfig(), [1, 2, 3], "voice.wav", "audio/wav", "B");
        Assert.AreEqual("multipart/form-data; boundary=B", ct);
        var s = Encoding.UTF8.GetString(body);
        StringAssert.Contains(s, "--B\r\nContent-Disposition: form-data; name=\"language\"\r\n\r\nzh\r\n");
        StringAssert.Contains(s, "name=\"file\"; filename=\"voice.wav\"\r\nContent-Type: audio/wav\r\n\r\n\u0001\u0002\u0003\r\n--B--\r\n");
    }

    [TestMethod]
    public void Multipart_SpeechFields()
    {
        var (body, _) = Multipart.Build(new AIConfig { SttProvider = SttProvider.AzureSpeech }, [9], "v.wav", "audio/wav", "B");
        var s = Encoding.UTF8.GetString(body);
        StringAssert.Contains(s, "name=\"definition\"\r\n\r\n{\"locales\":[\"zh-CN\"]}\r\n");
        StringAssert.Contains(s, "name=\"audio\"; filename=\"v.wav\"");
        Assert.IsFalse(s.Contains("name=\"file\""));
    }
}

[TestClass]
public sealed class ShortlistTests
{
    static readonly SupportService Vm = new("/s/vm", "svc-vm", "Virtual Machine running Linux", []);
    static readonly SupportService Aoai = new("/s/aoai", "svc-aoai", "Azure OpenAI", []);

    [TestMethod]
    public void Services_KeepsHitsAndPopular_AtMost60()
    {
        var many = Enumerable.Range(0, 300).Select(i => new SupportService($"{i}", $"s{i}", $"Service {i}", [])).Append(Vm).Append(Aoai).ToList();
        var s = Shortlist.Services("gpt 部署 429", many);
        Assert.IsTrue(s.Any(x => x.Name == "svc-aoai"));
        Assert.IsTrue(s.Any(x => x.Name == "svc-vm"), "popular kept");
        Assert.IsTrue(s.Count <= 60);
        Assert.AreEqual(60, Shortlist.Services("service", many).Count);
        Assert.AreEqual("svc-aoai", Shortlist.Services("gpt", many)[0].Name, "hits first");
    }

    [TestMethod]
    public void Services_Synonyms()
    {
        var k = new SupportService("k", "svc-k", "Kubernetes Service (AKS)", []);
        var x = new SupportService("x", "svc-x", "Other Thing", []);
        var s = Shortlist.Services("我的 k8s 集群挂了", [x, k]);
        Assert.AreEqual(1, s.Count);
        Assert.AreEqual("svc-k", s[0].Name);
    }

    [TestMethod]
    public void Problems_CapsAndRanksSshFirst()
    {
        var many = Enumerable.Range(0, 200).Select(i => new ProblemClassification($"/p/{i}", $"Category {i} / Item"))
            .Append(new ProblemClassification("/p/ssh", "Cannot connect to my VM / RDP or SSH port")).ToList();
        var s = Shortlist.Problems("linux vm ssh 连接失败", many);
        Assert.AreEqual(40, s.Count);
        Assert.AreEqual("/p/ssh", s[0].Id);
        Assert.AreEqual("/p/0", s[1].Id, "stable order for ties");
    }
}

internal sealed class ScriptedChat(params string[] replies) : IChat
{
    private readonly Queue<string> _q = new(replies);
    public List<(IReadOnlyList<ChatMessage> Messages, string? Schema)> Calls { get; } = [];
    public Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, string? jsonSchema, CancellationToken ct)
    {
        Calls.Add((messages, jsonSchema));
        return Task.FromResult(_q.Dequeue());
    }
}

[TestClass]
public sealed class AIDrafterTests
{
    static readonly List<SupportService> Services =
        [new("/s/vm", "svc-vm", "Virtual Machine running Linux", []), new("/s/aoai", "svc-aoai", "Azure OpenAI", [])];
    static readonly List<ProblemClassification> Problems =
        [new("/p/ssh", "Cannot connect / SSH"), new("/p/429", "API Errors / HTTP 429")];

    static string J(string service, string severity = "moderate", string title = "t", string q = "[]") =>
        $$"""{"service":"{{service}}","severity":"{{severity}}","title":"{{title}}","description":"d","questions":{{q}}}""";

    static Task<AIDraft> Run(ScriptedChat chat, IReadOnlyList<QA>? answers = null) =>
        AIDrafter.DraftAsync("x", answers ?? [], Services,
            (_, _) => Task.FromResult<IReadOnlyList<ProblemClassification>>(Problems), chat, CancellationToken.None);

    [TestMethod]
    public async Task MapsByNameOrDisplayName()
    {
        var chat = new ScriptedChat(J("Azure OpenAI"), """{"problemId":"/p/429"}""");
        var d = await Run(chat);
        Assert.AreEqual("svc-aoai", d.Service?.Name);
        Assert.AreEqual("/p/429", d.Problem?.Id);
        Assert.IsTrue(d.Ready);
        Assert.AreEqual(AIDrafter.System, chat.Calls[0].Messages[0].Content);
        StringAssert.Contains(chat.Calls[0].Messages[1].Content, "svc-aoai\tAzure OpenAI");
        StringAssert.Contains(chat.Calls[1].Messages[1].Content, "/p/429\tAPI Errors / HTTP 429");
        var schema = JsonNode.Parse(chat.Calls[0].Schema!)!;
        Assert.IsFalse((bool)schema["additionalProperties"]!);
        Assert.AreEqual(4, schema["properties"]!["severity"]!["enum"]!.AsArray().Count);
        Assert.AreEqual("problemId", (string)JsonNode.Parse(chat.Calls[1].Schema!)!["required"]![0]!);
    }

    [TestMethod]
    public async Task RejectsHallucinatedServiceAndProblem()
    {
        var d = await Run(new ScriptedChat(J("nope")));
        Assert.IsNull(d.Service);
        Assert.IsFalse(d.Ready);
        var d2 = await Run(new ScriptedChat(J("svc-vm"), """{"problemId":"/p/made-up"}"""));
        Assert.AreEqual("svc-vm", d2.Service?.Name);
        Assert.IsNull(d2.Problem);
    }

    [TestMethod]
    public async Task QuestionsCappedAndOnlyOneRound_SeverityFallback()
    {
        var d1 = await Run(new ScriptedChat(J("svc-aoai", "x", q: """["a"," ","b","c","d"]"""), """{"problemId":"/p/429"}"""));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, d1.Questions.ToArray());
        Assert.AreEqual(Severity.Moderate, d1.Severity);
        Assert.IsFalse(d1.Ready);
        var chat = new ScriptedChat(J("svc-aoai", "minimal", q: """["a","b"]"""), """{"problemId":"/p/429"}""");
        var d2 = await Run(chat, [new QA("a", "1")]);
        Assert.AreEqual(0, d2.Questions.Count);
        Assert.AreEqual(Severity.Minimal, d2.Severity);
        Assert.IsTrue(d2.Ready);
        StringAssert.Contains(chat.Calls[0].Messages[1].Content, "补充问答:\n问: a\n答: 1");
    }

    [TestMethod]
    public async Task TitleCappedAt200_AndBadJsonTolerated()
    {
        var d = await Run(new ScriptedChat(J("svc-aoai", title: new string('长', 250)), "garbage"));
        Assert.AreEqual(200, d.Title.Length);
        Assert.IsNull(d.Problem);
        var e = await Run(new ScriptedChat("{}"));
        Assert.IsNull(e.Service);
        Assert.AreEqual("", e.Title);
    }
}

[TestClass]
public sealed class AIClientTests
{
    static readonly AIConfig Cfg = new() { ChatEndpoint = "https://x/", ChatDeployment = "dep" };

    sealed class MemSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _d = [];
        public string? Read(string target) => _d.GetValueOrDefault(target);
        public void Write(string target, string? value) { if (string.IsNullOrEmpty(value)) _d.Remove(target); else _d[target] = value; }
    }

    static (AIClient, FakeHttp, FakeDelay, FakeAuth, MemSecrets) Make()
    {
        var http = new FakeHttp(); var delay = new FakeDelay(); var auth = new FakeAuth(); var sec = new MemSecrets();
        return (new AIClient(http.Client(), auth, delay, () => Cfg, sec), http, delay, auth, sec);
    }

    const string Ok = """{"choices":[{"message":{"content":"{\"a\":1}"}}]}""";
    static readonly ChatMessage[] Hi = [new("user", "hi")];

    [TestMethod]
    public async Task Chat_SendsBearerAndStructuredBody()
    {
        var (c, http, _, auth, _) = Make();
        http.Reply(200, Ok);
        Assert.AreEqual("""{"a":1}""", await c.ChatAsync(Hi, """{"type":"object"}""", CancellationToken.None));
        var s = http.Requests[0];
        Assert.AreEqual("https://x/openai/v1/chat/completions", s.Url);
        Assert.AreEqual("Bearer tok", s.Headers["Authorization"]);
        Assert.AreEqual(1, auth.Tenants.Count);
        var b = JsonNode.Parse(s.Body)!;
        Assert.AreEqual("dep", (string)b["model"]!);
        Assert.AreEqual("none", (string)b["reasoning_effort"]!);
        Assert.AreEqual("hi", (string)b["messages"]![0]!["content"]!);
        Assert.AreEqual("json_schema", (string)b["response_format"]!["type"]!);
        Assert.IsTrue((bool)b["response_format"]!["json_schema"]!["strict"]!);
        Assert.AreEqual("object", (string)b["response_format"]!["json_schema"]!["schema"]!["type"]!);
    }

    [TestMethod]
    public async Task Chat_RetriesWithoutReasoningEffort_WhenModelRejectsIt()
    {
        // 非推理模型 (gpt-4.1-mini 等) 返回 400 "Unrecognized request argument supplied: reasoning_effort" (2026-11-11 实测)
        var (c, http, _, _, _) = Make();
        http.Reply(400, """{"error":{"code":null,"message":"Unrecognized request argument supplied: reasoning_effort"}}""");
        http.Reply(200, Ok);
        Assert.AreEqual("""{"a":1}""", await c.ChatAsync(Hi, null, CancellationToken.None));
        Assert.AreEqual(2, http.Requests.Count);
        Assert.IsNull(JsonNode.Parse(http.Requests[1].Body)!["reasoning_effort"]);
    }

    [TestMethod]
    public async Task Chat_UsesStoredKeyInsteadOfToken()
    {
        var (c, http, _, auth, sec) = Make();
        sec.Write(AIClient.ChatKeyTarget, "k");
        http.Reply(200, Ok);
        await c.ChatAsync(Hi, null, CancellationToken.None);
        Assert.AreEqual("k", http.Requests[0].Headers["api-key"]);
        Assert.IsFalse(http.Requests[0].Headers.ContainsKey("Authorization"));
        Assert.AreEqual(0, auth.Tenants.Count);
        Assert.IsNull(JsonNode.Parse(http.Requests[0].Body)!["response_format"]);
    }

    [TestMethod]
    public async Task Chat_429_RetriesWithRetryAfterCappedAt15()
    {
        var (c, http, delay, _, _) = Make();
        http.Reply(429, "{}", ("Retry-After", "40")).Reply(429, "{}").Reply(200, Ok);
        Assert.AreEqual("""{"a":1}""", await c.ChatAsync(Hi, null, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(3) }, delay.Calls);
        Assert.AreEqual(3, http.Requests.Count);
    }

    [TestMethod]
    public async Task Chat_429_GivesUpAfterTwoRetries()
    {
        var (c, http, delay, _, _) = Make();
        http.Reply(429, "{}", ("Retry-After", "1")).Reply(429, "{}")
            .Reply(429, """{"error":{"code":"RateLimit","message":"slow"}}""");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => c.ChatAsync(Hi, null, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) }, delay.Calls);
        Assert.AreEqual(429, e.Status);
        StringAssert.Contains(e.Message, "AI 服务错误 429: RateLimit slow");
        StringAssert.Contains(e.Hint, "配额不足");
    }

    [TestMethod]
    public async Task Chat_401_HintDependsOnKey()
    {
        var (c, http, _, _, sec) = Make();
        http.Reply(401, "{}");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => c.ChatAsync(Hi, null, CancellationToken.None));
        StringAssert.Contains(e.Hint, "Cognitive Services OpenAI User");
        StringAssert.Contains(e.Message, "Cognitive Services OpenAI User");
        sec.Write(AIClient.ChatKeyTarget, "k");
        http.Reply(403, "{}");
        var e2 = await Assert.ThrowsExceptionAsync<AppError>(() => c.ChatAsync(Hi, null, CancellationToken.None));
        StringAssert.Contains(e2.Hint, "Key 与 Endpoint");
    }

    [TestMethod]
    public async Task Chat_400_TenantMismatch_ExplainsTenantAndKey()
    {
        // 实测: 默认 Endpoint 所在租户与 az 默认租户不同时, 服务返回 400 (非 401/403), 此前没有任何提示.
        var (c, http, _, _, _) = Make();
        http.Reply(400, """{"error":{"code":"","message":"Tenant provided in token does not match resource token Token tenant abc does not match resource tenant."}}""");
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => c.ChatAsync(Hi, null, CancellationToken.None));
        Assert.IsNotNull(e.Hint);
        StringAssert.Contains(e.Hint, "租户");
        StringAssert.Contains(e.Hint, "Key");
        StringAssert.Contains(e.Message, e.Hint);
    }

    [TestMethod]
    public async Task Chat_RejectsNonHttpsEndpoint()
    {
        var http = new FakeHttp();
        var c = new AIClient(http.Client(), new FakeAuth(), new FakeDelay(), () => new AIConfig { ChatEndpoint = "http://x" }, new MemSecrets());
        var e = await Assert.ThrowsExceptionAsync<AppError>(() => c.ChatAsync(Hi, null, CancellationToken.None));
        StringAssert.StartsWith(e.Message, "Endpoint 无效");
        Assert.AreEqual(0, http.Requests.Count);
    }

    [TestMethod]
    public async Task Transcribe_OpenAIAndSpeech()
    {
        var http = new FakeHttp();
        var cfg = new AIConfig { SttEndpoint = "https://y" };
        var sec = new MemSecrets();
        var c = new AIClient(http.Client(), new FakeAuth(), new FakeDelay(), () => cfg, sec);
        http.Reply(200, """{"text":"你好"}""");
        Assert.AreEqual("你好", await c.TranscribeAsync([1], "v.wav", "audio/wav", CancellationToken.None));
        StringAssert.StartsWith(http.Requests[0].Headers["Content-Type"], "multipart/form-data; boundary=");
        StringAssert.Contains(http.Requests[0].Body, "name=\"file\"");

        cfg = cfg with { SttProvider = SttProvider.AzureSpeech, SpeechRegion = "eastasia" };
        sec.Write(AIClient.SttKeyTarget, "s");
        http.Reply(200, """{"combinedPhrases":[{"text":"甲"},{"text":"乙"}]}""");
        Assert.AreEqual("甲乙", await c.TranscribeAsync([1], "v.wav", "audio/wav", CancellationToken.None));
        Assert.AreEqual("s", http.Requests[1].Headers["Ocp-Apim-Subscription-Key"]);
        StringAssert.StartsWith(http.Requests[1].Url, "https://eastasia.api.cognitive.microsoft.com/");
    }
}
