using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AdlibComposer.Audio.Windows;

/// <summary>Windows MIDI 매퍼(midiOut)로 내보내기. SysEx는 midiOutLongMsg, 다 보낸 버퍼는 다음 보낼 때 푼다.</summary>
public sealed class WinMidiOut : IMidiOutDevice
{
    [DllImport("winmm.dll")] private static extern int midiOutOpen(out IntPtr handle, int deviceId, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int midiOutShortMsg(IntPtr handle, int message);
    [DllImport("winmm.dll")] private static extern int midiOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int midiOutClose(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int midiOutPrepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int midiOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int midiOutLongMsg(IntPtr handle, IntPtr header, int size);

    /// <summary>MIDIHDR (mmsystem.h). 순서 배치가 C 구조체와 같다 (32 · 64비트 모두).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MidiHdr
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public int Flags;
        public IntPtr Next;
        public IntPtr Reserved;
        public int Offset;
        public IntPtr R0, R1, R2, R3, R4, R5, R6, R7;
    }

    private static readonly int HdrSize = Marshal.SizeOf(typeof(MidiHdr));
    private const int MidiErrStillPlaying = 65;
    private IntPtr midiOut;
    /// <summary>장치가 아직 다 보내지 않은 SysEx 버퍼 (다 보내면 풀어 준다).</summary>
    private readonly List<(IntPtr hdr, IntPtr data)> pending = new List<(IntPtr, IntPtr)>();

    public bool IsOpen { get { return midiOut != IntPtr.Zero; } }
    public int SysExPending { get { return pending.Count; } }

    public bool Open()
    {
        if (midiOut != IntPtr.Zero) return true;
        try
        {
            if (midiOutOpen(out midiOut, -1, IntPtr.Zero, IntPtr.Zero, 0) != 0) { midiOut = IntPtr.Zero; return false; }   // -1 = MIDI 매퍼
            return true;
        }
        catch (Exception) { midiOut = IntPtr.Zero; return false; }   // winmm 없음 (Windows 아님)
    }

    public void ShortMessage(int message)
    {
        if (midiOut != IntPtr.Zero) midiOutShortMsg(midiOut, message);
    }

    public bool SysEx(byte[] sx)
    {
        if (midiOut == IntPtr.Zero || sx == null || sx.Length < 2) return false;
        Release(false);
        IntPtr data = Marshal.AllocHGlobal(sx.Length);
        IntPtr hdr = Marshal.AllocHGlobal(HdrSize);
        Marshal.Copy(sx, 0, data, sx.Length);
        Marshal.StructureToPtr(new MidiHdr { Data = data, BufferLength = sx.Length, BytesRecorded = sx.Length }, hdr, false);
        try
        {
            if (midiOutPrepareHeader(midiOut, hdr, HdrSize) != 0) { Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data); return false; }
            if (midiOutLongMsg(midiOut, hdr, HdrSize) != 0) { midiOutUnprepareHeader(midiOut, hdr, HdrSize); Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data); return false; }
        }
        catch (Exception) { Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data); return false; }
        pending.Add((hdr, data));
        Release(false);
        return true;
    }

    /// <summary>다 보낸 SysEx 버퍼를 푼다. all = 장치를 닫기 전 (리셋 뒤라 모두 끝나 있다).</summary>
    private void Release(bool all)
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var (hdr, data) = pending[i];
            int r = midiOut != IntPtr.Zero ? midiOutUnprepareHeader(midiOut, hdr, HdrSize) : 0;
            if (r == MidiErrStillPlaying && !all) continue;
            Marshal.FreeHGlobal(hdr);
            Marshal.FreeHGlobal(data);
            pending.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        if (midiOut == IntPtr.Zero) return;
        try { midiOutReset(midiOut); Release(true); midiOutClose(midiOut); } catch (Exception) { }
        midiOut = IntPtr.Zero;
    }
}
