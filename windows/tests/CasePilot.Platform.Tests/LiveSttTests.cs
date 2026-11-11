using CasePilot.Core.Abstractions;
using CasePilot.Core.AI;
using CasePilot.Core.Auth;
using CasePilot.Platform;

namespace CasePilot.Platform.Tests;

/// 真实语音转写: Azure Speech. 需要 CASEPILOT_LIVE=1, CASEPILOT_STT_WAV, 以及 CASEPILOT_SPEECH_ENDPOINT (Entra 身份)
/// 或 CASEPILOT_SPEECH_REGION + CASEPILOT_SPEECH_KEY.
[TestClass]
public class LiveSttTests
{
    sealed class KeyStore(string key) : ISecretStore
    {
        public string? Read(string target) => target == AIClient.SttKeyTarget && key != "" ? key : null;
        public void Write(string target, string? value) { }
    }

    [TestMethod, TestCategory("Live")]
    public async Task AzureSpeech_TranscribesWav()
    {
        var region = Environment.GetEnvironmentVariable("CASEPILOT_SPEECH_REGION") ?? "";
        var endpoint = Environment.GetEnvironmentVariable("CASEPILOT_SPEECH_ENDPOINT") ?? "";
        var key = Environment.GetEnvironmentVariable("CASEPILOT_SPEECH_KEY") ?? "";
        var wav = Environment.GetEnvironmentVariable("CASEPILOT_STT_WAV");
        if (Environment.GetEnvironmentVariable("CASEPILOT_LIVE") != "1" || wav is null || (endpoint == "" && (region == "" || key == "")))
            Assert.Inconclusive("需要 CASEPILOT_LIVE=1, WAV, 以及 Endpoint 或 区域+Key");
        using var http = new HttpClient();
        var auth = new AuthProvider(http, new AzCli(), new SystemClock(), new SystemDelay());
        if (key == "") await auth.LoginCliAsync(default);
        var cfg = new AIConfig { SttProvider = SttProvider.AzureSpeech, SpeechRegion = region, SttEndpoint = endpoint, SttLanguage = Environment.GetEnvironmentVariable("CASEPILOT_STT_LANG") ?? "zh-CN" };
        var ai = new AIClient(http, auth, new SystemDelay(), () => cfg, new KeyStore(key));
        var text = await ai.TranscribeAsync(File.ReadAllBytes(wav), "audio.wav", "audio/wav", default);
        Console.WriteLine($"STT=[{text}]");
        StringAssert.Contains(text.ToLowerInvariant(), Environment.GetEnvironmentVariable("CASEPILOT_STT_EXPECT") ?? "virtual machine");
    }
}
