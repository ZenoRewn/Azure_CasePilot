using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CasePilot.Core.Models;

namespace CasePilot.Core.AI;

/// <summary>PCM16LE 单声道工具.</summary>
public static class Pcm
{
    /// <summary>16 kHz → 24 kHz 线性插值 (AOAI Realtime 只接受 24 kHz).</summary>
    public static byte[] Resample16To24(ReadOnlySpan<byte> pcm16)
    {
        var n = pcm16.Length / 2;
        if (n == 0) return [];
        var m = n * 3 / 2;
        var o = new byte[m * 2];
        for (var i = 0; i < m; i++)
        {
            var pos = i * 2.0 / 3.0;
            var a = (int)pos;
            var b = Math.Min(a + 1, n - 1);
            var fa = BinaryPrimitives.ReadInt16LittleEndian(pcm16[(a * 2)..]);
            var fb = BinaryPrimitives.ReadInt16LittleEndian(pcm16[(b * 2)..]);
            var v = (short)Math.Round(fa + (fb - fa) * (pos - a));
            BinaryPrimitives.WriteInt16LittleEndian(o.AsSpan(i * 2), v);
        }
        return o;
    }

    /// <summary>流式 WAV 头 (16 kHz / 16 bit / 单声道, 长度字段为 0).</summary>
    public static byte[] StreamingWavHeader()
    {
        var w = new byte[44];
        var s = w.AsSpan();
        "RIFF"u8.CopyTo(s);
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], 16000);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], 32000);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        return w;
    }
}

/// <summary>流式识别累积结果: 已确认句子 + 当前中间结果.</summary>
public sealed class StreamTranscript
{
    public string Finals { get; set; } = "";
    public string Partial { get; set; } = "";
    public bool Done { get; set; }
    public string? Error { get; set; }
    public string Text => Finals + Partial;
}

/// <summary>Azure OpenAI Realtime 转写 (gpt-live-transcribe).</summary>
public static class RealtimeStt
{
    public const int Rate = 24000;

    public static Uri Url(AIConfig c)
    {
        var e = c.SttEndpoint.Trim().Trim('/');
        if (e.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) e = "wss://" + e[8..];
        return new Uri(e + "/openai/v1/realtime?intent=transcription");
    }

    public static string SessionUpdate(AIConfig c)
    {
        var dep = c.SttStreamDeployment;
        var lang = c.SttLanguage.Length > 2 ? c.SttLanguage[..2] : c.SttLanguage;
        var t = new JsonObject { ["model"] = dep };
        if (dep.Contains("live", StringComparison.OrdinalIgnoreCase)) t["languages"] = new JsonArray(lang);
        else t["language"] = lang;
        return new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["type"] = "transcription",
                ["audio"] = new JsonObject
                {
                    ["input"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = Rate },
                        ["transcription"] = t,
                        ["turn_detection"] = null,
                    },
                },
            },
        }.ToJsonString();
    }

    /// <summary>输入 16 kHz PCM, 内部重采样到 24 kHz.</summary>
    public static string Append(ReadOnlySpan<byte> pcm16k) =>
        new JsonObject { ["type"] = "input_audio_buffer.append", ["audio"] = Convert.ToBase64String(Pcm.Resample16To24(pcm16k)) }.ToJsonString();

    public const string Commit = """{"type":"input_audio_buffer.commit"}""";

    public static void Apply(StreamTranscript s, string message)
    {
        JsonObject? j;
        try { j = JsonNode.Parse(message) as JsonObject; } catch (JsonException) { return; }
        var type = Str(j?["type"]);
        if (type.EndsWith("input_audio_transcription.delta", StringComparison.Ordinal)) s.Partial += Str(j!["delta"]);
        else if (type.EndsWith("input_audio_transcription.completed", StringComparison.Ordinal))
        {
            s.Finals += Str(j!["transcript"]);
            s.Partial = "";
            s.Done = true;
        }
        else if (type == "error")
        {
            var m = Str(j!["error"]?["message"]);
            s.Error = m.Length > 0 ? m : "实时转写错误";
            s.Done = true;
        }
    }

    internal static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>Azure Speech 实时识别 (WebSocket 原生协议) + Post-Stream Refinement.</summary>
public static class SpeechStt
{
    public static Uri Url(string region) => new($"wss://{region}.stt.speech.microsoft.com/stt/speech/universal/v2");

    public static string StsUrl(AIConfig c, bool usesKey)
    {
        if (!usesKey) return $"{c.SttEndpoint.Trim().Trim('/')}/sts/v1.0/issueToken";
        if (c.SpeechRegion.Trim().Length == 0) throw new AppError("使用 Speech Key 时请在 设置 → AI 与语音 中填写区域。");
        return $"https://{c.SpeechRegion.Trim()}.api.cognitive.microsoft.com/sts/v1.0/issueToken";
    }

    /// <summary>从 STS JWT 的 payload 读取 region 声明.</summary>
    public static string? RegionFromJwt(string jwt)
    {
        var parts = jwt.Trim().Split('.');
        if (parts.Length < 2) return null;
        var p = parts[1].Replace('-', '+').Replace('_', '/');
        p += new string('=', (4 - p.Length % 4) % 4);
        try { return JsonNode.Parse(Convert.FromBase64String(p)) is JsonObject o ? RealtimeStt.Str(o["region"]) is { Length: > 0 } r ? r : null : null; }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }

    static string Header(string path, string rid, DateTimeOffset ts, string ctype) =>
        $"Path: {path}\r\nX-RequestId: {rid}\r\nX-Timestamp: {ts.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fff}Z\r\nContent-Type: {ctype}\r\n";

    public static string Text(string path, string rid, DateTimeOffset ts, string json) =>
        Header(path, rid, ts, "application/json") + "\r\n" + json;

    public static string Config(string rid, DateTimeOffset ts) =>
        Text("speech.config", rid, ts, """{"context":{"system":{"name":"CasePilot","version":"1.0"}}}""");

    public static string Context(string rid, DateTimeOffset ts, string language) =>
        Text("speech.context", rid, ts, new JsonObject
        {
            ["phraseDetection"] = new JsonObject
            {
                ["mode"] = "CONVERSATION",
                ["language"] = language,
                ["enrichment"] = new JsonObject { ["conversation"] = new JsonObject { ["postprocessingoption"] = "PostRefinement" } },
            },
            ["phraseOutput"] = new JsonObject { ["format"] = "Detailed" },
        }.ToJsonString());

    /// <summary>二进制音频帧: 2 字节大端头长度 + 头 + 负载 (空负载表示结束).</summary>
    public static byte[] Audio(string rid, DateTimeOffset ts, ReadOnlySpan<byte> payload)
    {
        var h = Encoding.ASCII.GetBytes(Header("audio", rid, ts, "audio/x-wav"));
        var o = new byte[2 + h.Length + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(o, (ushort)h.Length);
        h.CopyTo(o, 2);
        payload.CopyTo(o.AsSpan(2 + h.Length));
        return o;
    }

    /// <summary>拆分头/体; Path 行大小写不敏感, 冒号后可无空格.</summary>
    public static (string Path, string Body) Parse(string message)
    {
        var i = message.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = i < 0 ? message : message[..i];
        var body = i < 0 ? "" : message[(i + 4)..];
        foreach (var line in head.Split("\r\n"))
            if (line.StartsWith("path:", StringComparison.OrdinalIgnoreCase)) return (line[5..].Trim().ToLowerInvariant(), body);
        return ("", body);
    }

    public static void Apply(StreamTranscript s, string message)
    {
        var (path, body) = Parse(message);
        JsonObject? j = null;
        try { if (body.Length > 0) j = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { }
        switch (path)
        {
            case "speech.hypothesis":
                s.Partial = RealtimeStt.Str(j?["Text"]);
                break;
            case "speech.phrase" when RealtimeStt.Str(j?["RecognitionStatus"]) == "Success":
                s.Finals += RealtimeStt.Str(j?["DisplayText"]);
                s.Partial = "";
                break;
            case "turn.end":
                s.Done = true;
                break;
        }
    }
}
