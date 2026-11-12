using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AdlibComposer.Audio;

/// <summary>winmm waveOut 기반 출력 (외부 NuGet 패키지 없이 동작). 16bit 모노.</summary>
public sealed class WaveOutPlayer : IDisposable
{
    public delegate void FillBuffer(short[] buffer, int sampleCount);

    private const int BufferCount = 4;
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

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, uint deviceId, ref WAVEFORMATEX format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);

    private readonly int sampleRate;
    private readonly int samplesPerBuffer;
    private readonly FillBuffer fill;
    private readonly IntPtr[] headers = new IntPtr[BufferCount];
    private readonly IntPtr[] data = new IntPtr[BufferCount];
    private readonly short[] scratch;
    private IntPtr device = IntPtr.Zero;
    private Thread thread;
    private volatile bool running;

    public WaveOutPlayer(int sampleRate, int samplesPerBuffer, FillBuffer fill)
    {
        this.sampleRate = sampleRate;
        this.samplesPerBuffer = samplesPerBuffer;
        this.fill = fill;
        scratch = new short[samplesPerBuffer];
    }

    public bool IsOpen { get { return device != IntPtr.Zero; } }

    public void Start()
    {
        if (running) return;
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = (uint)(sampleRate * 2), cbSize = 0
        };
        int rc = waveOutOpen(out device, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0) { device = IntPtr.Zero; throw new InvalidOperationException("오디오 장치를 열 수 없습니다 (waveOutOpen=" + rc + ")"); }

        int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        for (int i = 0; i < BufferCount; i++)
        {
            data[i] = Marshal.AllocHGlobal(samplesPerBuffer * 2);
            headers[i] = Marshal.AllocHGlobal(hdrSize);
            var h = new WAVEHDR { lpData = data[i], dwBufferLength = (uint)(samplesPerBuffer * 2) };
            Marshal.StructureToPtr(h, headers[i], false);
            waveOutPrepareHeader(device, headers[i], (uint)hdrSize);
            FillAndWrite(i);
        }
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "WaveOut" };
        thread.Start();
    }

    private void FillAndWrite(int i)
    {
        fill(scratch, samplesPerBuffer);
        Marshal.Copy(scratch, 0, data[i], samplesPerBuffer);
        waveOutWrite(device, headers[i], (uint)Marshal.SizeOf(typeof(WAVEHDR)));
    }

    private void Loop()
    {
        while (running)
        {
            for (int i = 0; i < BufferCount && running; i++)
            {
                var h = (WAVEHDR)Marshal.PtrToStructure(headers[i], typeof(WAVEHDR));
                if ((h.dwFlags & WHDR_DONE) != 0) FillAndWrite(i);
            }
            Thread.Sleep(3);
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
