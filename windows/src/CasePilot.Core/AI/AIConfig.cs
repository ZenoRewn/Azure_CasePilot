using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CasePilot.Core.AI;

public enum SttProvider { AzureOpenAI, AzureSpeech }

public enum AIKind { Chat, Stt }

/// <summary>AI 调用的认证方式: 当前登录身份 (Entra ID) 或 API Key.</summary>
public enum AIAuth { Entra, Key }

public static class SttProviderExtensions
{
    public static string ApiValue(this SttProvider p) => p == SttProvider.AzureSpeech ? "azure-speech" : "azure-openai";

    public static string Label(this SttProvider p) =>
        p == SttProvider.AzureOpenAI ? "Azure OpenAI (gpt-live-transcribe 流式)" : "Azure Speech (实时流式 + PSR)";

    public static SttProvider? Parse(string? v) => v switch
    {
        "azure-openai" => SttProvider.AzureOpenAI,
        "azure-speech" => SttProvider.AzureSpeech,
        _ => null,
    };
}

/// <summary>AI 与语音设置 (非机密). Key 只保存在凭据管理器, 从不序列化.</summary>
public sealed record AIConfig
{
    public string ChatEndpoint { get; init; } = "https://westus3-azureai-service-507154.openai.azure.com";
    public string ChatDeployment { get; init; } = "gpt-6-luna";
    public SttProvider SttProvider { get; init; } = SttProvider.AzureOpenAI;
    public string SttEndpoint { get; init; } = "https://westus3-azureai-service-507154.openai.azure.com";
    /// <summary>整段转写 (流式失败时兜底).</summary>
    public string SttDeployment { get; init; } = "gpt-4o-mini-transcribe";
    /// <summary>AOAI Realtime 流式转写部署.</summary>
    public string SttStreamDeployment { get; init; } = "gpt-live-transcribe";
    public string SttLanguage { get; init; } = "zh-CN";
    /// <summary>设置后 Speech 使用区域端点.</summary>
    public string SpeechRegion { get; init; } = "";
    /// <summary>null = 旧版设置未选择, 由 <see cref="Migrated"/> 按已存 Key 推断.</summary>
    public AIAuth? ChatAuth { get; init; }
    public AIAuth? SttAuth { get; init; }

    /// <summary>旧设置: 已存 Key → Key, 否则 Entra (升级后行为不变); 已显式选择的保持不变.</summary>
    public AIConfig Migrated(Abstractions.ISecretStore secrets) => this with
    {
        ChatAuth = ChatAuth ?? (string.IsNullOrEmpty(secrets.Read(AIClient.ChatKeyTarget)) ? AIAuth.Entra : AIAuth.Key),
        SttAuth = SttAuth ?? (string.IsNullOrEmpty(secrets.Read(AIClient.SttKeyTarget)) ? AIAuth.Entra : AIAuth.Key),
    };

    public AIConfig Trimmed() => this with { ChatEndpoint = TrimEndpoint(ChatEndpoint), SttEndpoint = TrimEndpoint(SttEndpoint) };

    private static string TrimEndpoint(string s) => s.Trim().Trim('/');

    /// <summary>兼容旧版本与损坏的设置: 缺字段或类型不符时取默认.</summary>
    public static AIConfig FromJson(string? json)
    {
        var d = new AIConfig();
        JsonObject? o;
        try { o = json is null ? null : JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { o = null; }
        if (o is null) return d;
        string Str(string k, string def) =>
            o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;
        return new AIConfig
        {
            ChatEndpoint = Str("chatEndpoint", d.ChatEndpoint),
            ChatDeployment = Str("chatDeployment", d.ChatDeployment),
            SttProvider = SttProviderExtensions.Parse(Str("sttProvider", "")) ?? d.SttProvider,
            SttEndpoint = Str("sttEndpoint", d.SttEndpoint),
            SttDeployment = Str("sttDeployment", d.SttDeployment),
            SttStreamDeployment = Str("sttStreamDeployment", d.SttStreamDeployment),
            SttLanguage = Str("sttLanguage", d.SttLanguage),
            SpeechRegion = Str("speechRegion", d.SpeechRegion),
            ChatAuth = ParseAuth(Str("chatAuth", "")),
            SttAuth = ParseAuth(Str("sttAuth", "")),
        };
    }

    public string ToJson() => new JsonObject
    {
        ["chatEndpoint"] = ChatEndpoint,
        ["chatDeployment"] = ChatDeployment,
        ["sttProvider"] = SttProvider.ApiValue(),
        ["sttEndpoint"] = SttEndpoint,
        ["sttDeployment"] = SttDeployment,
        ["sttStreamDeployment"] = SttStreamDeployment,
        ["sttLanguage"] = SttLanguage,
        ["speechRegion"] = SpeechRegion,
        ["chatAuth"] = AuthValue(ChatAuth),
        ["sttAuth"] = AuthValue(SttAuth),
    }.ToJsonString();

    static AIAuth? ParseAuth(string v) => v switch { "entra" => AIAuth.Entra, "key" => AIAuth.Key, _ => null };
    static string? AuthValue(AIAuth? a) => a switch { AIAuth.Entra => "entra", AIAuth.Key => "key", _ => null };
}

public static class AIKeys
{
    /// <summary>实际使用的 Key: 仅 Key 模式且已保存时返回; Entra 模式忽略已存 Key (不删除).</summary>
    public static string? Effective(AIConfig c, AIKind kind, Abstractions.ISecretStore secrets)
    {
        var auth = kind == AIKind.Chat ? c.ChatAuth : c.SttAuth;
        var key = secrets.Read(kind == AIKind.Chat ? AIClient.ChatKeyTarget : AIClient.SttKeyTarget);
        // 未迁移 (null) 时沿用旧行为: 有 Key 用 Key
        return auth == AIAuth.Entra || string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>登录身份调用所需的 RBAC 角色.</summary>
    public static string Role(AIConfig c, AIKind kind) =>
        kind == AIKind.Stt && c.SttProvider == SttProvider.AzureSpeech ? "Cognitive Services User" : "Cognitive Services OpenAI User";
}

public static class AIHeaders
{
    /// <summary>key 为空时使用 Entra 令牌 (Bearer).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> For(AIConfig config, AIKind kind, string? key, string? bearer)
    {
        if (string.IsNullOrEmpty(key))
            return bearer is null ? [] : [KeyValuePair.Create("Authorization", "Bearer " + bearer)];
        var name = kind == AIKind.Stt && config.SttProvider == SttProvider.AzureSpeech ? "Ocp-Apim-Subscription-Key" : "api-key";
        return [KeyValuePair.Create(name, key)];
    }
}

public static class SttUrls
{
    public static string Chat(AIConfig c) => $"{c.ChatEndpoint}/openai/v1/chat/completions";

    public static string For(AIConfig c)
    {
        if (c.SttProvider == SttProvider.AzureSpeech)
        {
            var b = c.SpeechRegion.Length == 0 ? c.SttEndpoint : $"https://{c.SpeechRegion}.api.cognitive.microsoft.com";
            return $"{b}/speechtotext/transcriptions:transcribe?api-version=2024-11-15";
        }
        return $"{c.SttEndpoint}/openai/deployments/{c.SttDeployment}/audio/transcriptions?api-version=2025-04-01-preview";
    }
}

public static class Multipart
{
    /// <summary>OpenAI: language + file; Speech: definition + audio.</summary>
    public static (byte[] Body, string ContentType) Build(AIConfig c, byte[] audio, string filename, string mime, string boundary)
    {
        var ms = new MemoryStream();
        void Put(string s) { var b = Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Field(string k, string v) => Put($"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n");
        string part;
        if (c.SttProvider == SttProvider.AzureSpeech)
        {
            part = "audio";
            Field("definition", new JsonObject { ["locales"] = new JsonArray(c.SttLanguage) }.ToJsonString());
        }
        else
        {
            part = "file";
            Field("language", c.SttLanguage.Length > 2 ? c.SttLanguage[..2] : c.SttLanguage);
        }
        Put($"--{boundary}\r\nContent-Disposition: form-data; name=\"{part}\"; filename=\"{filename}\"\r\nContent-Type: {mime}\r\n\r\n");
        ms.Write(audio, 0, audio.Length);
        Put($"\r\n--{boundary}--\r\n");
        return (ms.ToArray(), $"multipart/form-data; boundary={boundary}");
    }
}
