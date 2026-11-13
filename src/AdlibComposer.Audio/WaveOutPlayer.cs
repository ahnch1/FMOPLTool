using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AdlibComposer.Audio;

/// <summary>
/// winmm waveOut 기반 출력 (외부 NuGet 패키지 없이 동작). 16bit 모노 또는 스테레오.
/// 짧은 버퍼 여러 개를 이벤트 콜백으로 채워 지연을 줄이고, <see cref="PlayedSamples"/>로 실제로 소리 난 위치를 알려준다.
/// </summary>
public sealed class WaveOutPlayer : IDisposable
{
    public delegate void FillBuffer(short[] buffer, int sampleCount);

    private const int BufferCount = 6;
    private const uint CALLBACK_EVENT = 0x00050000;
    private const uint TIME_BYTES = 0x0004;
    private const uint TIME_SAMPLES = 0x0002;
    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const uint WHDR_DONE = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MMTIME
    {
        public uint wType;
        public uint value;
        public uint pad;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, uint deviceId, ref WAVEFORMATEX format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr hwo, ref MMTIME time, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);

    private readonly int sampleRate;
    private readonly int samplesPerBuffer;
    private readonly int channels;
    private readonly FillBuffer fill;
    private readonly IntPtr[] headers = new IntPtr[BufferCount];
    private readonly IntPtr[] data = new IntPtr[BufferCount];
    private readonly short[] scratch;
    private IntPtr device = IntPtr.Zero;
    private Thread thread;
    private volatile bool running;
    private readonly AutoResetEvent bufferDone = new AutoResetEvent(false);
    private int nextBuffer;
    private uint lastPos;
    private long posHigh;

    public WaveOutPlayer(int sampleRate, int samplesPerBuffer, FillBuffer fill) : this(sampleRate, samplesPerBuffer, 1, fill) { }

    /// <param name="samplesPerBuffer">버퍼 하나의 프레임 수.</param>
    /// <param name="channels">1 = 모노, 2 = 스테레오 (fill에는 왼쪽/오른쪽 번갈아 2 × 프레임 수 샘플).</param>
    public WaveOutPlayer(int sampleRate, int samplesPerBuffer, int channels, FillBuffer fill)
    {
        this.sampleRate = sampleRate;
        this.samplesPerBuffer = samplesPerBuffer;
        this.channels = channels == 2 ? 2 : 1;
        this.fill = fill;
        scratch = new short[samplesPerBuffer * this.channels];
    }

    public bool IsOpen { get { return device != IntPtr.Zero; } }

    public void Start()
    {
        if (running) return;
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = (ushort)channels, nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 16, nBlockAlign = (ushort)(2 * channels), nAvgBytesPerSec = (uint)(sampleRate * 2 * channels), cbSize = 0
        };
        int rc = waveOutOpen(out device, WAVE_MAPPER, ref fmt, bufferDone.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
        if (rc != 0) { device = IntPtr.Zero; throw new InvalidOperationException("오디오 장치를 열 수 없습니다 (waveOutOpen=" + rc + ")"); }

        int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        for (int i = 0; i < BufferCount; i++)
        {
            data[i] = Marshal.AllocHGlobal(scratch.Length * 2);
            headers[i] = Marshal.AllocHGlobal(hdrSize);
            var h = new WAVEHDR { lpData = data[i], dwBufferLength = (uint)(scratch.Length * 2) };
            Marshal.StructureToPtr(h, headers[i], false);
            waveOutPrepareHeader(device, headers[i], (uint)hdrSize);
            FillAndWrite(i);
        }
        nextBuffer = 0;
        lastPos = 0; posHigh = 0;
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "WaveOut", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private void FillAndWrite(int i)
    {
        fill(scratch, samplesPerBuffer);
        Marshal.Copy(scratch, 0, data[i], scratch.Length);
        waveOutWrite(device, headers[i], (uint)Marshal.SizeOf(typeof(WAVEHDR)));
    }

    private void Loop()
    {
        while (running)
        {
            bufferDone.WaitOne(20);
            // 재생 순서를 지키기 위해 링 순서대로, 끝난 버퍼만 다시 채운다
            while (running)
            {
                var h = (WAVEHDR)Marshal.PtrToStructure(headers[nextBuffer], typeof(WAVEHDR));
                if ((h.dwFlags & WHDR_DONE) == 0) break;
                FillAndWrite(nextBuffer);
                nextBuffer = (nextBuffer + 1) % BufferCount;
            }
        }
    }

    /// <summary>장치가 실제로 재생을 마친 샘플 수 (Start 이후 누적). 장치를 열지 않았으면 -1.</summary>
    public long PlayedSamples
    {
        get
        {
            if (device == IntPtr.Zero) return -1;
            var t = new MMTIME { wType = TIME_SAMPLES };
            if (waveOutGetPosition(device, ref t, (uint)Marshal.SizeOf(typeof(MMTIME))) != 0) return -1;
            uint pos = t.wType == TIME_BYTES ? t.value / (uint)(2 * channels) : t.value;
            lock (bufferDone)
            {
                if (pos < lastPos && lastPos - pos > 0x80000000u) posHigh += 0x100000000L;   // 32bit 랩어라운드
                lastPos = pos;
                return posHigh + pos;
            }
        }
    }

    public void Stop()
    {
        running = false;
        if (thread != null) { thread.Join(500); thread = null; }
        if (device != IntPtr.Zero)
        {
            waveOutReset(device);
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            for (int i = 0; i < BufferCount; i++)
            {
                if (headers[i] != IntPtr.Zero) { waveOutUnprepareHeader(device, headers[i], (uint)hdrSize); Marshal.FreeHGlobal(headers[i]); headers[i] = IntPtr.Zero; }
                if (data[i] != IntPtr.Zero) { Marshal.FreeHGlobal(data[i]); data[i] = IntPtr.Zero; }
            }
            waveOutClose(device);
            device = IntPtr.Zero;
        }
    }

    public void Dispose() { Stop(); }
}
