using System.IO;

namespace CasePilot.App;

/// <summary>未处理异常日志: %LOCALAPPDATA%\CasePilot\crash.log. 记录失败时静默, 不得再次抛出.</summary>
public static class CrashLog
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CasePilot", "crash.log");

    public static void Append(Exception e, string source, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{source}]{Environment.NewLine}{e}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception) { }
    }
}
