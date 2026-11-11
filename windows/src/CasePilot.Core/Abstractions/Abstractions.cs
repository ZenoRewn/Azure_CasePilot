namespace CasePilot.Core.Abstractions;

/// <summary>机密存储 (Windows: 凭据管理器). 写入空值表示删除.</summary>
public interface ISecretStore
{
    string? Read(string target);
    void Write(string target, string? value);
}

/// <summary>非机密设置 (JSON 文件).</summary>
public interface ISettingsStore
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);
}

public interface IClock { DateTimeOffset UtcNow { get; } }

public interface IDelay { Task DelayAsync(TimeSpan delay, CancellationToken ct); }

public interface IIdGenerator { Guid NewId(); }

public sealed record ChatMessage(string Role, string Content);

public interface IChat
{
    /// <summary>发送对话; 给定 JSON schema 时返回结构化输出文本.</summary>
    Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, string? jsonSchema, CancellationToken ct);
}

public interface IAudioRecorder
{
    bool IsRecording { get; }
    /// <summary>onChunk 在录音线程回调 16 kHz PCM 块 (约 200 ms), 用于流式识别.</summary>
    void Start(Action<byte[]>? onChunk = null);
    /// <summary>停止录音, 返回 16 kHz 单声道 WAV.</summary>
    byte[] Stop();
}

public interface IShell
{
    void Copy(string text);
    void OpenUrl(string url);
}

public interface ITranscriber
{
    Task<string> TranscribeAsync(byte[] audio, string filename, string mime, CancellationToken ct);
}
