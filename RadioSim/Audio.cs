using System.Runtime.InteropServices;

namespace RadioSim;

/// <summary>Plays clips straight from the game's audio.rpf: each clip is a stream reference
/// ("&lt;bank offset&gt;:&lt;stream ID&gt;"), decoded from the AWC bank to 48 kHz stereo float PCM.</summary>
public sealed class GameAudio : IDisposable
{
    public const int Rate = 48000;
    readonly Game.AwcArchive awc;

    public GameAudio(string game) { awc = new Game.AwcArchive(Path.Combine(game, "audio.rpf")); }

    public float[] Decode(string src)
    {
        var parts = src.Split(':');
        if (parts.Length != 2) throw new InvalidDataException($"bad stream reference '{src}'");
        return awc.Decode(Convert.ToInt64(parts[0], 16), Convert.ToUInt32(parts[1], 16));
    }

    /// <summary>A segment with its DJ lines mixed in. <paramref name="voiceSeconds"/>, when given,
    /// receives each DJ line's length.</summary>
    public float[] Render(Segment s, List<double>? voiceSeconds = null)
    {
        var pcm = Decode(s.Src);
        foreach (var v in s.Voices)
        {
            var vp = Decode(v.Src);
            voiceSeconds?.Add(vp.Length / 2.0 / Rate);
            pcm = Mixer.Overlay(pcm, vp, v.At);
        }
        return pcm;
    }

    public void Dispose() => awc.Dispose();
}

public static class Mixer
{
    /// <summary>Song with the DJ's voice on top from <paramref name="at"/> seconds, the music
    /// ducked by <paramref name="duckDb"/> dB with short ramps while the DJ talks.</summary>
    public static float[] Overlay(float[] music, float[] voice, double at, double duckDb = -7, double ramp = 0.25)
    {
        var outp = (float[])music.Clone();
        long start = (long)(at * GameAudio.Rate) * 2;
        long len = voice.Length;
        float duck = (float)Math.Pow(10, duckDb / 20);
        long rampN = (long)(ramp * GameAudio.Rate) * 2;
        long end = Math.Min(outp.Length, start + len);
        for (long i = Math.Max(0, start - rampN); i < Math.Min(outp.Length, end + rampN); i++)
        {
            float g;
            if (i < start) g = 1 - (1 - duck) * (i - (start - rampN)) / (float)rampN;
            else if (i >= end) g = duck + (1 - duck) * (i - end) / (float)rampN;
            else g = duck;
            outp[i] *= g;
        }
        for (long i = 0; i < len && start + i < outp.Length; i++)
            if (start + i >= 0) outp[start + i] = Math.Clamp(outp[start + i] + voice[i], -1f, 1f);
        return outp;
    }
}

/// <summary>Writes a 32-bit float stereo WAV.</summary>
public sealed class WavWriter : IDisposable
{
    readonly FileStream fs;
    readonly BinaryWriter w;
    long frames;

    public WavWriter(string path)
    {
        fs = File.Create(path); w = new BinaryWriter(fs);
        w.Write(new byte[44]);
    }

    public void Write(float[] pcm)
    {
        var b = new byte[pcm.Length * 4];
        Buffer.BlockCopy(pcm, 0, b, 0, b.Length);
        w.Write(b); frames += pcm.Length / 2;
    }

    public void Dispose()
    {
        long data = frames * 8;
        fs.Position = 0;
        w.Write("RIFF"u8); w.Write((uint)(36 + data)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)3); w.Write((short)2); w.Write(GameAudio.Rate);
        w.Write(GameAudio.Rate * 8); w.Write((short)8); w.Write((short)32);
        w.Write("data"u8); w.Write((uint)data);
        w.Dispose();
    }
}

/// <summary>Live playback through the Windows waveOut API (winmm.dll).</summary>
public sealed unsafe class WaveOutPlayer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, WAVEHDR* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, WAVEHDR* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, WAVEHDR* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveOutSetVolume(IntPtr h, uint vol);
    [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr h, ref MMTIME t, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct MMTIME { public uint wType, value, pad; }
    const uint TIME_BYTES = 4, TIME_SAMPLES = 2;

    public void Pause() => waveOutPause(handle);
    public void Resume() => waveOutRestart(handle);
    /// <summary>Drop everything queued (used to skip ahead immediately). Both frame counters
    /// start again from 0.</summary>
    public void Flush()
    {
        lock (posGate) { waveOutReset(handle); Interlocked.Exchange(ref written, 0); lastRaw = 0; wraps = 0; }
    }

    long written;
    readonly object posGate = new();
    uint lastRaw; long wraps;

    /// <summary>Frames handed to the device since it opened (or the last <see cref="Flush"/>).</summary>
    public long Written => Interlocked.Read(ref written);

    /// <summary>Frames the device has actually played since it opened (or the last
    /// <see cref="Flush"/>): what is audible now. Stops while paused.</summary>
    public long Played
    {
        get
        {
            lock (posGate)
            {
                var t = new MMTIME { wType = TIME_SAMPLES };
                if (waveOutGetPosition(handle, ref t, Marshal.SizeOf<MMTIME>()) != 0) return 0;
                uint raw = t.value;
                if (raw < lastRaw && lastRaw - raw > 0x80000000u) wraps++;      // the 32-bit counter wrapped
                lastRaw = raw;
                long count = wraps * 0x100000000L + raw;
                return Math.Min(t.wType == TIME_BYTES ? count / 8 : count, Written);
            }
        }
    }
    /// <summary>Device volume, 0..1 for both channels.</summary>
    public void SetVolume(double v)
    {
        uint x = (uint)(Math.Clamp(v, 0, 1) * 0xFFFF);
        waveOutSetVolume(handle, x | (x << 16));
    }

    const uint WHDR_DONE = 1;
    const int Chunk = GameAudio.Rate / 4 * 2;          // 250 ms of stereo samples per buffer
    const int Buffers = 8;
    readonly IntPtr handle;
    readonly WAVEHDR*[] hdrs = new WAVEHDR*[Buffers];
    readonly IntPtr[] mem = new IntPtr[Buffers];
    int next;

    public WaveOutPlayer()
    {
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 3, nChannels = 2, nSamplesPerSec = GameAudio.Rate, wBitsPerSample = 32,
            nBlockAlign = 8, nAvgBytesPerSec = GameAudio.Rate * 8,
        };
        int r = waveOutOpen(out handle, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (r != 0) throw new InvalidOperationException(r == 2
            ? "No sound output device was found. Connect or enable speakers or headphones, then press Play again."
            : $"The sound device could not be opened (waveOutOpen error {r}).");
        for (int i = 0; i < Buffers; i++)
        {
            mem[i] = Marshal.AllocHGlobal(Chunk * 4);
            hdrs[i] = (WAVEHDR*)Marshal.AllocHGlobal(sizeof(WAVEHDR));
            *hdrs[i] = new WAVEHDR { dwFlags = WHDR_DONE };
        }
    }

    public void Play(float[] pcm, CancellationToken ct)
    {
        for (int pos = 0; pos < pcm.Length && !ct.IsCancellationRequested; pos += Chunk)
        {
            var h = hdrs[next];
            while ((h->dwFlags & WHDR_DONE) == 0) { if (ct.IsCancellationRequested) return; Thread.Sleep(5); }
            if (h->lpData != IntPtr.Zero) waveOutUnprepareHeader(handle, h, sizeof(WAVEHDR));
            int n = Math.Min(Chunk, pcm.Length - pos);
            Marshal.Copy(pcm, pos, mem[next], n);
            *h = new WAVEHDR { lpData = mem[next], dwBufferLength = (uint)(n * 4) };
            waveOutPrepareHeader(handle, h, sizeof(WAVEHDR));
            waveOutWrite(handle, h, sizeof(WAVEHDR));
            Interlocked.Add(ref written, n / 2);
            next = (next + 1) % Buffers;
        }
    }

    public void Drain()
    {
        foreach (var h in hdrs)
            while ((h->dwFlags & WHDR_DONE) == 0) Thread.Sleep(10);
    }

    public void Dispose()
    {
        waveOutReset(handle);
        for (int i = 0; i < Buffers; i++)
        {
            if (hdrs[i]->lpData != IntPtr.Zero) waveOutUnprepareHeader(handle, hdrs[i], sizeof(WAVEHDR));
            Marshal.FreeHGlobal((IntPtr)hdrs[i]); Marshal.FreeHGlobal(mem[i]);
        }
        waveOutClose(handle);
    }
}
