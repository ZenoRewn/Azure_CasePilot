using CasePilot.App.Mvvm;
using CasePilot.Core.Abstractions;
using CasePilot.Core.AI;

namespace CasePilot.App.ViewModels;

public enum MicState { Idle, Recording, Transcribing }

/// <summary>
/// 麦克风按钮: 点击开始录音, 再次点击结束并转写, 结果插入到光标处.
/// 配置流式转写时录音中通过 preview 实时显示中间结果; 流式失败则录完后退回整段转写.
/// </summary>
public sealed class MicVM : ObservableObject
{
    public const string PrivacyUrl = "ms-settings:privacy-microphone";

    private readonly IAudioRecorder _recorder;
    private readonly ITranscriber _transcriber;
    private readonly Action<string> _insert;
    private readonly IShell? _os;
    private readonly IStreamingTranscriber? _streaming;
    private readonly Action<string?>? _preview;
    private IStreamSession? _session;
    private DateTimeOffset _started;

    /// <param name="preview">流式中间结果 (原文本 + 当前识别文本); null 表示清除预览.</param>
    public MicVM(IAudioRecorder recorder, ITranscriber transcriber, Action<string> insert, IShell? os = null,
        IStreamingTranscriber? streaming = null, Action<string?>? preview = null)
    {
        (_recorder, _transcriber, _insert, _os, _streaming, _preview) = (recorder, transcriber, insert, os, streaming, preview);
        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => State != MicState.Transcribing);
        OpenPrivacyCommand = new RelayCommand(() => _os?.OpenUrl(PrivacyUrl));
        DismissCommand = new RelayCommand(() => Error = null);
    }

    MicState _state;
    public MicState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(Tooltip));
            ToggleCommand.NotifyCanExecuteChanged();
        }
    }

    public string Tooltip => State switch
    {
        MicState.Recording => "结束并转写",
        MicState.Transcribing => "正在转写…",
        _ => "语音输入",
    };

    string? _error;
    public string? Error
    {
        get => _error;
        private set { if (SetProperty(ref _error, value) && value is null) PermissionHint = false; }
    }

    bool _permission;
    /// <summary>无法打开麦克风时提示到 Windows 隐私设置中允许.</summary>
    public bool PermissionHint { get => _permission; private set => SetProperty(ref _permission, value); }

    string _elapsed = "00:00";
    public string Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }

    /// <summary>视图计时器调用, 刷新录音时长.</summary>
    public void Tick(DateTimeOffset now) { if (State == MicState.Recording) Elapsed = FormatElapsed(now - _started); }

    public AsyncRelayCommand ToggleCommand { get; }
    public RelayCommand OpenPrivacyCommand { get; }
    public RelayCommand DismissCommand { get; }

    public static string FormatElapsed(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    /// <summary>在 caret 处插入, 与相邻文字以空格分隔; 返回新文本与新光标.</summary>
    public static (string Text, int Caret) InsertAt(string text, int caret, string insert)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var pre = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : "";
        var post = caret < text.Length && !char.IsWhiteSpace(text[caret]) ? " " : "";
        var piece = pre + insert + post;
        return (text[..caret] + piece + text[caret..], caret + piece.Length);
    }

    public async Task ToggleAsync()
    {
        if (State == MicState.Transcribing) return;
        if (State == MicState.Idle)
        {
            try
            {
                var ui = SynchronizationContext.Current;
                void Show(string t) { if (ui is null) _preview?.Invoke(t); else ui.Post(_ => { if (State == MicState.Recording) _preview?.Invoke(t); }, null); }
                _session = _streaming?.Start(Show);
                var live = _session;
                _recorder.Start(live is null ? null : live.Push);
                Error = null;
                _started = DateTimeOffset.Now;
                Elapsed = "00:00";
                State = MicState.Recording;
            }
            catch (Exception e)
            {
                _session = null;
                Error = Errors.Text(e);
                PermissionHint = true;
            }
            return;
        }
        var audio = _recorder.Stop();
        State = MicState.Transcribing;
        var session = _session;
        _session = null;
        try
        {
            string? text = null;
            if (session is not null)
            {
                try { text = await session.FinishAsync(CancellationToken.None); }
                catch (Exception e) when (e is not OperationCanceledException) { text = null; }
                _preview?.Invoke(null);
            }
            text ??= await _transcriber.TranscribeAsync(audio, "voice.wav", "audio/wav", CancellationToken.None);
            text = text.Trim();
            if (text.Length > 0) _insert(text);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Error = Errors.Text(e); }
        finally { State = MicState.Idle; }
    }
}
