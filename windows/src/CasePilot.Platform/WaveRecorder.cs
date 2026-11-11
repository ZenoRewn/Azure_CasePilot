using System.ComponentModel;
using System.Runtime.InteropServices;
using CasePilot.Core.Abstractions;

namespace CasePilot.Platform;

/// <summary>16 kHz / 16 bit / 单声道 PCM 的 WAV 封装.</summary>
public static class Wav
{
    public const int SampleRate = 16000;
    public const int BytesPerSecond = SampleRate * 2;

    public static byte[] Encode(ReadOnlySpan<byte> pcm)
    {
        var w = new byte[44 + pcm.Length];
        var s = w.AsSpan();
        "RIFF"u8.CopyTo(s);
        BitConverter.TryWriteBytes(s[4..], 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(s[8..]);
        BitConverter.TryWriteBytes(s[16..], 16);
        BitConverter.TryWriteBytes(s[20..], (short)1);
        BitConverter.TryWriteBytes(s[22..], (short)1);
        BitConverter.TryWriteBytes(s[24..], SampleRate);
        BitConverter.TryWriteBytes(s[28..], BytesPerSecond);
        BitConverter.TryWriteBytes(s[32..], (short)2);
        BitConverter.TryWriteBytes(s[34..], (short)16);
        "data"u8.CopyTo(s[36..]);
        BitConverter.TryWriteBytes(s[40..], pcm.Length);
        pcm.CopyTo(s[44..]);
        return w;
    }
}

/// <summary>
/// winmm waveIn 录音 (16 kHz 单声道). 超过 <see cref="MaxPcmBytes"/> (14 MiB 上传限制内, 约 7 分钟) 后丢弃后续音频.
/// 只能手动验证; 所有 waveIn 缓冲操作在录音线程内完成.
/// </summary>
public sealed partial class WaveRecorder : IAudioRecorder, IDisposable
{
    public const int MaxPcmBytes = 14 * 1024 * 1024 - 44 - 1024;
    private const int BufferBytes = Wav.BytesPerSecond / 5; // 200 ms
    private const int BufferCount = 4;
    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const uint CALLBACK_EVENT = 0x00050000;
    private const uint WHDR_DONE = 1;
    private static readonly int HdrSize = Marshal.SizeOf<WAVEHDR>();

    private readonly Lock _gate = new();
    private readonly MemoryStream _pcm = new();
    private Thread? _thread;
    private volatile bool _stopping;
    private Exception? _error;

    public bool IsRecording => _thread is not null;

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null) return;
            _pcm.SetLength(0);
            _stopping = false;
            _error = null;
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "CasePilot.WaveRecorder" };
            _thread.Start();
            ready.Wait();
            if (_error is { } e)
            {
                _thread.Join();
                _thread = null;
                throw e;
            }
        }
    }

    public byte[] Stop()
    {
        lock (_gate)
        {
            if (_thread is { } t)
            {
                _stopping = true;
                t.Join();
                _thread = null;
            }
            return Wav.Encode(_pcm.GetBuffer().AsSpan(0, (int)_pcm.Length));
        }
    }

    public void Dispose() => Stop();

    private void Run(ManualResetEventSlim ready)
    {
        using var evt = new AutoResetEvent(false);
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = 1, nSamplesPerSec = Wav.SampleRate,
            nAvgBytesPerSec = Wav.BytesPerSecond, nBlockAlign = 2, wBitsPerSample = 16,
        };
        var rc = waveInOpen(out var h, WAVE_MAPPER, ref fmt, evt.SafeWaitHandle.DangerousGetHandle(), 0, CALLBACK_EVENT);
        if (rc != 0)
        {
            _error = new Win32Exception($"无法打开麦克风 (waveIn 错误 {rc})。请检查麦克风权限。");
            ready.Set();
            return;
        }
        var hdrs = new nint[BufferCount];
        try
        {
            for (var i = 0; i < BufferCount; i++)
            {
                hdrs[i] = Marshal.AllocHGlobal(HdrSize);
                Marshal.StructureToPtr(new WAVEHDR { lpData = Marshal.AllocHGlobal(BufferBytes), dwBufferLength = BufferBytes }, hdrs[i], false);
                waveInPrepareHeader(h, hdrs[i], (uint)HdrSize);
                waveInAddBuffer(h, hdrs[i], (uint)HdrSize);
            }
            rc = waveInStart(h);
            if (rc != 0) _error = new Win32Exception($"无法开始录音 (waveIn 错误 {rc})。");
            ready.Set();
            while (_error is null && !_stopping)
            {
                evt.WaitOne(100);
                Harvest(h, hdrs, requeue: true);
            }
            waveInReset(h);
            Harvest(h, hdrs, requeue: false);
        }
        finally
        {
            ready.Set();
            foreach (var p in hdrs)
            {
                if (p == 0) continue;
                waveInUnprepareHeader(h, p, (uint)HdrSize);
                Marshal.FreeHGlobal(Marshal.PtrToStructure<WAVEHDR>(p).lpData);
                Marshal.FreeHGlobal(p);
            }
            waveInClose(h);
        }
    }

    private void Harvest(nint h, nint[] hdrs, bool requeue)
    {
        foreach (var p in hdrs)
        {
            var hdr = Marshal.PtrToStructure<WAVEHDR>(p);
            if ((hdr.dwFlags & WHDR_DONE) == 0) continue;
            var n = (int)Math.Min(hdr.dwBytesRecorded, (uint)Math.Max(0, MaxPcmBytes - _pcm.Length));
            if (n > 0)
            {
                var buf = new byte[n];
                Marshal.Copy(hdr.lpData, buf, 0, n);
                _pcm.Write(buf);
            }
            if (!requeue) continue;
            hdr.dwFlags &= ~WHDR_DONE;
            hdr.dwBytesRecorded = 0;
            Marshal.StructureToPtr(hdr, p, false);
            waveInAddBuffer(h, p, (uint)HdrSize);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public nint lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public nuint dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public nint lpNext;
        public nuint reserved;
    }

    [LibraryImport("winmm.dll")]
    private static partial uint waveInOpen(out nint handle, uint deviceId, ref WAVEFORMATEX format, nint callback, nint instance, uint flags);
    [LibraryImport("winmm.dll")]
    private static partial uint waveInPrepareHeader(nint h, nint hdr, uint size);
    [LibraryImport("winmm.dll")]
    private static partial uint waveInUnprepareHeader(nint h, nint hdr, uint size);
    [LibraryImport("winmm.dll")]
    private static partial uint waveInAddBuffer(nint h, nint hdr, uint size);
    [LibraryImport("winmm.dll")]
    private static partial uint waveInStart(nint h);
    [LibraryImport("winmm.dll")]
    private static partial uint waveInReset(nint h);
    [LibraryImport("winmm.dll")]
    private static partial uint waveInClose(nint h);
}
