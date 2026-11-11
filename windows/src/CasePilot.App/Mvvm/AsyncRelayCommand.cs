using System.Windows.Input;

namespace CasePilot.App.Mvvm;

/// <summary>异步命令: 运行期间禁止重入; 异常经 <see cref="Failed"/> 报告, 不会逃逸到 async void.</summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ObservableObject, ICommand
{
    bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (SetProperty(ref _isRunning, value)) NotifyCanExecuteChanged(); }
    }

    public event EventHandler? CanExecuteChanged;
    public event Action<Exception>? Failed;

    /// <summary>兜底: 无 <see cref="Failed"/> 订阅者时的非取消异常 (由 App 显示并记录).</summary>
    public static Action<Exception>? Unobserved { get; set; }

    public bool CanExecute(object? parameter) => !IsRunning && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        IsRunning = true;
        try { await execute(); }
        catch (Exception e)
        {
            if (Failed is { } f) f(e);
            else if (e is not OperationCanceledException) Unobserved?.Invoke(e);
        }
        finally { IsRunning = false; }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
