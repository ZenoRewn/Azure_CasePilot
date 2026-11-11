using CasePilot.App.Mvvm;
using CasePilot.Core.Abstractions;

namespace CasePilot.App.ViewModels;

public enum MicState { Idle, Recording, Transcribing }

/// <summary>麦克风按钮: 点击开始录音, 再次点击结束并转写, 结果插入到光标处.</summary>
public sealed class MicVM : ObservableObject
{
    public const string PrivacyUrl = "ms-settings:privacy-microphone";

    private readonly IAudioRecorder _recorder;
    private readonly ITranscriber _transcriber;
    private readonly Action<string> _insert;
    private readonly IShell? _os;
    private DateTimeOffset _started;

    public MicVM(IAudioRecorder recorder, ITranscriber transcriber, Action<string> insert, IShell? os = null)
    {
        (_recorder, _transcriber, _insert, _os) = (recorder, transcriber, insert, os);
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
                _recorder.Start();
                Error = null;
                _started = DateTimeOffset.Now;
                Elapsed = "00:00";
                State = MicState.Recording;
            }
            catch (Exception e)
            {
                Error = Errors.Text(e);
                PermissionHint = true;
            }
            return;
        }
        var audio = _recorder.Stop();
        State = MicState.Transcribing;
        try
        {
            var text = (await _transcriber.TranscribeAsync(audio, "voice.wav", "audio/wav", CancellationToken.None)).Trim();
            if (text.Length > 0) _insert(text);
        }
        catch (Exception e) when (e is not OperationCanceledException) { Error = Errors.Text(e); }
        finally { State = MicState.Idle; }
    }
}
