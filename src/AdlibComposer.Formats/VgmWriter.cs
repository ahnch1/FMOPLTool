using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// VGM 1.71 파일 쓰기: 칩 클럭(머리말 칸)과 레지스터 쓰기 · 기다림(44100Hz 샘플)을 차례로 받아 .vgm / .vgz로 만든다.
/// 어느 칩이든 같은 방법으로 쓴다: <see cref="SetClock"/>로 머리말 칸을 채우고 <see cref="Write"/>로 명령 바이트를 붙인다.
/// </summary>
public sealed class VgmWriter
{
    /// <summary>머리말 크기 (명령은 여기부터).</summary>
    private const int HeaderSize = 0x100;

    // 자주 쓰는 머리말 칸 (VGM 명세의 위치)
    public const int ClockSn76489 = 0x0C, ClockYm2413 = 0x10, ClockYm2612 = 0x2C, ClockYm2151 = 0x30,
        ClockYm2203 = 0x44, ClockYm2608 = 0x48, ClockYm2610 = 0x4C, ClockYm3812 = 0x50, ClockYm3526 = 0x54, ClockY8950 = 0x58, ClockYmf262 = 0x5C,
        ClockAy8910 = 0x74, ClockNesApu = 0x84, ClockOkim6295 = 0x98, ClockK051649 = 0x9C, ClockHuc6280 = 0xA4;

    private readonly MemoryStream body = new MemoryStream();
    private readonly SortedDictionary<int, uint> clocks = new SortedDictionary<int, uint>();
    private readonly Dictionary<int, byte> headerBytes = new Dictionary<int, byte>();
    private long totalSamples;
    private long loopAtSamples = -1;
    private int loopAtByte = -1;
    private bool ended;

    public string Title = "", Game = "", System = "", Author = "", Date = "", Ripper = "", Notes = "";

    /// <summary>지금까지의 길이 (44100Hz 샘플).</summary>
    public long TotalSamples { get { return totalSamples; } }

    /// <summary>머리말 칸 offset에 칩 클럭(Hz)을 쓴다 (bit31 등 깃발은 flags로).</summary>
    public void SetClock(int offset, double hz, uint flags = 0)
    {
        if (offset < 0x0C || offset + 4 > HeaderSize) throw new ArgumentOutOfRangeException(nameof(offset));
        clocks[offset] = (uint)Math.Round(hz) | flags;
    }

    /// <summary>머리말 바이트 하나 (예: 0x78 AY 종류).</summary>
    public void SetHeaderByte(int offset, byte value) { headerBytes[offset] = value; }

    /// <summary>명령 바이트를 그대로 붙인다 (예: 0x5A reg val).</summary>
    public void Write(params byte[] command)
    {
        if (ended) throw new InvalidOperationException("이미 끝난 기록입니다.");
        body.Write(command, 0, command.Length);
    }

    /// <summary>1바이트 명령 (칩 명령 + 값, 예: 0x50 SN76489).</summary>
    public void Write(byte cmd, int value) { Write(cmd, (byte)value); }

    /// <summary>2바이트 명령 (칩 명령 + 레지스터 + 값).</summary>
    public void Write(byte cmd, int reg, int value) { Write(cmd, (byte)reg, (byte)value); }

    /// <summary>samples만큼 기다림 (44100Hz). 0이면 아무것도 쓰지 않는다.</summary>
    public void Wait(long samples)
    {
        if (samples <= 0) return;
        totalSamples += samples;
        while (samples > 0)
        {
            if (samples == 735) { body.WriteByte(0x62); return; }
            if (samples == 882) { body.WriteByte(0x63); return; }
            if (samples <= 16) { body.WriteByte((byte)(0x70 + samples - 1)); return; }
            int n = (int)Math.Min(samples, 0xFFFF);
            body.WriteByte(0x61); body.WriteByte((byte)n); body.WriteByte((byte)(n >> 8));
            samples -= n;
        }
    }

    /// <summary>되돌아갈 곳 = 지금 위치.</summary>
    public void MarkLoop() { loopAtByte = (int)body.Length; loopAtSamples = totalSamples; }

    /// <summary>끝 표시(0x66)를 붙이고 파일 바이트를 만든다. gzip = .vgz.</summary>
    public byte[] ToArray(bool gzip = false)
    {
        if (!ended) { body.WriteByte(0x66); ended = true; }
        var gd3 = BuildGd3();
        var hdr = new byte[HeaderSize];
        int dataLen = (int)body.Length;
        int gd3At = HeaderSize + dataLen;
        int eof = gd3At + gd3.Length;
        Put(hdr, 0x00, 0x206D6756);   // "Vgm "
        Put(hdr, 0x04, (uint)(eof - 0x04));
        Put(hdr, 0x08, 0x171);
        foreach (var kv in clocks) Put(hdr, kv.Key, kv.Value);
        foreach (var kv in headerBytes) hdr[kv.Key] = kv.Value;
        Put(hdr, 0x14, (uint)(gd3At - 0x14));
        Put(hdr, 0x18, (uint)totalSamples);
        if (loopAtByte >= 0 && loopAtSamples < totalSamples)
        {
            Put(hdr, 0x1C, (uint)(HeaderSize + loopAtByte - 0x1C));
            Put(hdr, 0x20, (uint)(totalSamples - loopAtSamples));
        }
        Put(hdr, 0x34, (uint)(HeaderSize - 0x34));

        var all = new byte[eof];
        Buffer.BlockCopy(hdr, 0, all, 0, HeaderSize);
        Buffer.BlockCopy(body.GetBuffer(), 0, all, HeaderSize, dataLen);
        Buffer.BlockCopy(gd3, 0, all, gd3At, gd3.Length);
        if (!gzip) return all;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(all, 0, all.Length);
            return ms.ToArray();
        }
    }

    public void Save(string path)
    {
        bool gz = string.Equals(Path.GetExtension(path), ".vgz", StringComparison.OrdinalIgnoreCase);
        File.WriteAllBytes(path, ToArray(gz));
    }

    private byte[] BuildGd3()
    {
        // 영어 / 일본어 칸 짝: 일본어 칸은 비운다
        var fields = new[] { Title, "", Game, "", System, "", Author, "", Date, Ripper, Notes };
        var sb = new StringBuilder();
        foreach (var f in fields) { sb.Append((f ?? "").Replace('\0', ' ')); sb.Append('\0'); }
        var text = Encoding.Unicode.GetBytes(sb.ToString());
        var b = new byte[12 + text.Length];
        b[0] = (byte)'G'; b[1] = (byte)'d'; b[2] = (byte)'3'; b[3] = (byte)' ';
        Put(b, 4, 0x100);
        Put(b, 8, (uint)text.Length);
        Buffer.BlockCopy(text, 0, b, 12, text.Length);
        return b;
    }

    private static void Put(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }
}
