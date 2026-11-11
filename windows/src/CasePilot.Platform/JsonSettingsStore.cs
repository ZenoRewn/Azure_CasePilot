using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Models;

namespace CasePilot.Platform;

/// <summary>
/// 非机密设置, 存于 %LOCALAPPDATA%\CasePilot\settings.json.
/// 写入: 先写临时文件再原子替换. 读取失败 (损坏) 时备份为 settings.json.corrupt-时间戳 并从空设置开始.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CasePilot", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly JsonObject _data;

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _data = Load(_path);
    }

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject o) return o;
        }
        catch (JsonException) { }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        try { File.Move(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return [];
    }

    public T? Get<T>(string key)
    {
        lock (_gate)
        {
            if (_data[key] is not { } node) return default;
            try { return node.Deserialize<T>(Options); }
            catch (JsonException) { return default; }
            catch (InvalidOperationException) { return default; }
        }
    }

    /// <summary>先写入副本并原子替换, 成功后才更新内存; 失败抛出 <see cref="AppError"/>, 内存与文件保持一致.</summary>
    public void Set<T>(string key, T value)
    {
        lock (_gate)
        {
            var next = (JsonObject)_data.DeepClone();
            next[key] = JsonSerializer.SerializeToNode(value, Options);
            var tmp = $"{_path}.{Environment.ProcessId}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                    w.Write(next.ToJsonString(Options));
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Move(tmp, _path, overwrite: true); break; }
                    catch (IOException) when (attempt < 4) { Thread.Sleep(50); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
                throw new AppError($"设置保存失败: {e.Message}", hint: _path);
            }
            _data[key] = next[key]?.DeepClone();
        }
    }
}
