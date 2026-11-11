using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using CasePilot.Core.AI;
using CasePilot.Core.Models;

namespace CasePilot.Core.Tests;

[TestClass]
public sealed class StreamingProtocolTests
{
    static readonly DateTimeOffset Ts = new(2026, 11, 11, 6, 0, 0, TimeSpan.Zero);

    static byte[] Pcm16(params short[] s)
    {
        var b = new byte[s.Length * 2];
        for (var i = 0; i < s.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), s[i]);
        return b;
    }

    static short[] Samples(byte[] b) => Enumerable.Range(0, b.Length / 2).Select(i => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i * 2))).ToArray();

    [TestMethod]
    public void Config_StreamDeployment_DefaultsAndRoundTrips()
    {
        Assert.AreEqual("gpt-live-transcribe", new AIConfig().SttStreamDeployment);
        Assert.AreEqual("gpt-live-transcribe", AIConfig.FromJson("""{"sttDeployment":"x"}""").SttStreamDeployment);
        var c = new AIConfig { SttStreamDeployment = "s1" };
        Assert.AreEqual("s1", AIConfig.FromJson(c.ToJson()).SttStreamDeployment);
        StringAssert.Contains(SttProvider.AzureOpenAI.Label(), "gpt-live-transcribe");
        StringAssert.Contains(SttProvider.AzureSpeech.Label(), "PSR");
    }

    [TestMethod]
    public void Resample_16k_to_24k_LinearInterpolation()
    {
        var o = Samples(Pcm.Resample16To24(Pcm16(0, 300, 600, 900)));
        CollectionAssert.AreEqual(new short[] { 0, 200, 400, 600, 800, 900 }, o);
        Assert.AreEqual(0, Pcm.Resample16To24([]).Length);
        Assert.AreEqual(3200 * 3 / 2, Pcm.Resample16To24(new byte[3200]).Length);
    }

    [TestMethod]
    public void StreamingWavHeader_Is16kMonoWithZeroLength()
    {
        var h = Pcm.StreamingWavHeader();
        Assert.AreEqual(44, h.Length);
        Assert.AreEqual("RIFF", Encoding.ASCII.GetString(h, 0, 4));
        Assert.AreEqual(16000, BitConverter.ToInt32(h, 24));
        Assert.AreEqual(1, BitConverter.ToInt16(h, 22));
        Assert.AreEqual(0, BitConverter.ToInt32(h, 40));
    }

    [TestMethod]
    public void Realtime_UrlAndSessionUpdate()
    {
        var c = new AIConfig { SttEndpoint = "https://r.openai.azure.com/" };
        Assert.AreEqual("wss://r.openai.azure.com/openai/v1/realtime?intent=transcription", RealtimeStt.Url(c).ToString());
        var j = JsonNode.Parse(RealtimeStt.SessionUpdate(c))!;
        Assert.AreEqual("session.update", (string)j["type"]!);
        var input = j["session"]!["audio"]!["input"]!;
        Assert.AreEqual(24000, (int)input["format"]!["rate"]!);
        Assert.AreEqual("gpt-live-transcribe", (string)input["transcription"]!["model"]!);
        Assert.AreEqual("zh", (string)input["transcription"]!["languages"]![0]!);
        Assert.IsTrue(input.AsObject().ContainsKey("turn_detection"));
        Assert.IsNull(input["turn_detection"]);

        var j2 = JsonNode.Parse(RealtimeStt.SessionUpdate(c with { SttStreamDeployment = "gpt-4o-mini-transcribe" }))!;
        var tr = j2["session"]!["audio"]!["input"]!["transcription"]!;
        Assert.AreEqual("zh", (string)tr["language"]!);
        Assert.IsNull(tr["languages"]);
    }

    [TestMethod]
    public void Realtime_AppendResamplesAndBase64()
    {
        var j = JsonNode.Parse(RealtimeStt.Append(new byte[320]))!;
        Assert.AreEqual("input_audio_buffer.append", (string)j["type"]!);
        Assert.AreEqual(480, Convert.FromBase64String((string)j["audio"]!).Length);
    }

    [TestMethod]
    public void Realtime_EventsAccumulate()
    {
        var t = new StreamTranscript();
        RealtimeStt.Apply(t, """{"type":"session.created"}""");
        RealtimeStt.Apply(t, """{"type":"conversation.item.input_audio_transcription.delta","delta":"虚拟"}""");
        RealtimeStt.Apply(t, """{"type":"conversation.item.input_audio_transcription.delta","delta":"机"}""");
        Assert.AreEqual("虚拟机", t.Text);
        Assert.IsFalse(t.Done);
        RealtimeStt.Apply(t, """{"type":"conversation.item.input_audio_transcription.completed","transcript":"虚拟机。"}""");
        Assert.AreEqual("虚拟机。", t.Text);
        Assert.IsTrue(t.Done);
        var e = new StreamTranscript();
        RealtimeStt.Apply(e, """{"type":"error","error":{"message":"bad rate"}}""");
        Assert.AreEqual("bad rate", e.Error);
        RealtimeStt.Apply(e, "not json");
    }

    [TestMethod]
    public void Speech_StsUrl_KeyRequiresRegion()
    {
        var c = new AIConfig { SttEndpoint = "https://res.cognitiveservices.azure.com/" };
        Assert.AreEqual("https://res.cognitiveservices.azure.com/sts/v1.0/issueToken", SpeechStt.StsUrl(c, false));
        Assert.ThrowsException<AppError>(() => SpeechStt.StsUrl(c, true));
        Assert.AreEqual("https://westus3.api.cognitive.microsoft.com/sts/v1.0/issueToken", SpeechStt.StsUrl(c with { SpeechRegion = "westus3" }, true));
        Assert.AreEqual("wss://westus3.stt.speech.microsoft.com/stt/speech/universal/v2", SpeechStt.Url("westus3").ToString());
    }

    [TestMethod]
    public void Speech_RegionFromJwt()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"region":"westus3","x":"??>"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual("westus3", SpeechStt.RegionFromJwt($"h.{payload}.s"));
        Assert.IsNull(SpeechStt.RegionFromJwt("nope"));
        Assert.IsNull(SpeechStt.RegionFromJwt("a.!!!.c"));
    }

    [TestMethod]
    public void Speech_TextMessages()
    {
        var m = SpeechStt.Context("rid", Ts, "zh-CN");
        StringAssert.StartsWith(m, "Path: speech.context\r\nX-RequestId: rid\r\nX-Timestamp: 2026-11-11T06:00:00.000Z\r\nContent-Type: application/json\r\n\r\n");
        var j = JsonNode.Parse(m[(m.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..])!;
        Assert.AreEqual("zh-CN", (string)j["phraseDetection"]!["language"]!);
        Assert.AreEqual("PostRefinement", (string)j["phraseDetection"]!["enrichment"]!["conversation"]!["postprocessingoption"]!);
        StringAssert.StartsWith(SpeechStt.Config("rid", Ts), "Path: speech.config\r\n");
    }

    [TestMethod]
    public void Speech_AudioFrame_BigEndianHeaderLength()
    {
        var f = SpeechStt.Audio("rid", Ts, [9, 8]);
        var n = BinaryPrimitives.ReadUInt16BigEndian(f);
        var head = Encoding.ASCII.GetString(f, 2, n);
        StringAssert.StartsWith(head, "Path: audio\r\n");
        StringAssert.Contains(head, "Content-Type: audio/x-wav");
        CollectionAssert.AreEqual(new byte[] { 9, 8 }, f[(2 + n)..]);
        Assert.AreEqual(2 + n, SpeechStt.Audio("rid", Ts, []).Length);
    }

    [TestMethod]
    public void Speech_EventsAccumulate_PathCaseInsensitiveNoSpace()
    {
        var t = new StreamTranscript();
        SpeechStt.Apply(t, "Path:turn.start\r\n\r\n{}");
        SpeechStt.Apply(t, "X-RequestId: a\r\nPath:speech.hypothesis\r\n\r\n{\"Text\":\"虚拟机无法\"}");
        Assert.AreEqual("虚拟机无法", t.Text);
        SpeechStt.Apply(t, "path: Speech.Phrase\r\n\r\n{\"RecognitionStatus\":\"Success\",\"DisplayText\":\"虚拟机无法连接。\"}");
        SpeechStt.Apply(t, "Path:speech.hypothesis\r\n\r\n{\"Text\":\"从今天\"}");
        Assert.AreEqual("虚拟机无法连接。从今天", t.Text);
        SpeechStt.Apply(t, "Path:speech.phrase\r\n\r\n{\"RecognitionStatus\":\"EndOfDictation\"}");
        Assert.AreEqual("虚拟机无法连接。从今天", t.Text);
        Assert.IsFalse(t.Done);
        SpeechStt.Apply(t, "Path:turn.end\r\n\r\n{}");
        Assert.IsTrue(t.Done);
    }
}
