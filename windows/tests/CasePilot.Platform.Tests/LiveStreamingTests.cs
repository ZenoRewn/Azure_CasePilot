using CasePilot.Core.Abstractions;
using CasePilot.Core.AI;
using CasePilot.Core.Auth;
using CasePilot.Platform;

namespace CasePilot.Platform.Tests;

/// 真实流式转写 (Azure Speech 实时识别, Entra). 需 CASEPILOT_LIVE=1, CASEPILOT_SPEECH_ENDPOINT, CASEPILOT_STT_WAV (16kHz mono).
[TestClass]
public class LiveStreamingTests
{
    sealed class NoSecrets : ISecretStore { public string? Read(string t) => null; public void Write(string t, string? v) { } }

    [TestMethod, TestCategory("Live")]
    public async Task AzureSpeech_Streaming_TranscribesWav()
    {
        var ep = Environment.GetEnvironmentVariable("CASEPILOT_SPEECH_ENDPOINT");
        var wav = Environment.GetEnvironmentVariable("CASEPILOT_STT_WAV");
        if (Environment.GetEnvironmentVariable("CASEPILOT_LIVE") != "1" || ep is null || wav is null)
            Assert.Inconclusive("需要 CASEPILOT_LIVE=1, Endpoint, WAV");
        using var http = new HttpClient();
        var auth = new AuthProvider(http, new AzCli(), new SystemClock(), new SystemDelay());
        await auth.LoginCliAsync(default);
        var cfg = new AIConfig { SttProvider = SttProvider.AzureSpeech, SttEndpoint = ep, SpeechRegion = "",
            SttLanguage = Environment.GetEnvironmentVariable("CASEPILOT_STT_LANG") ?? "zh-CN" };
        var st = new StreamingTranscriber(http, auth, () => cfg, new NoSecrets(), () => new ClientWebSocketConnection(), new SystemClock());
        var partial = "";
        var s = st.Start(t => partial = t);
        var pcm = File.ReadAllBytes(wav)[44..];
        for (var i = 0; i < pcm.Length; i += 6400) { s.Push(pcm[i..Math.Min(i + 6400, pcm.Length)]); await Task.Delay(100); }
        var text = await s.FinishAsync(default);
        Console.WriteLine($"STREAM=[{text}] partial=[{partial}]");
        StringAssert.Contains(text.ToLowerInvariant(), "virtual machine");
    }
}
