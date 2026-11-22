using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// GYM (메가드라이브 / 제네시스 YM2612 + SN76489 기록, Gens 등): 1/60초 프레임마다 칩 쓰기를 적은 것.
/// 읽으면 같은 내용의 VGM(<see cref="VgmSong"/>)으로 바꿔 VGM 재생기로 낸다 (참고: modizer libGME <c>Gym_Emu.cpp</c>, libvgm).
/// <para>머리말 (GYMX, 선택): 0x04 곡 · 0x24 게임 · 0x44 저작권 · 0x64 에뮬레이터 · 0x84 덤프한 사람 (32바이트씩) · 0xA4 설명 256바이트,
/// 0x1A4 되돌아갈 프레임 (1부터, 0 = 루프 없음) · 0x1A8 압축 (0 = 아님, 그 밖 = zlib으로 압축된 몸통의 원래 크기). 몸통은 0x1AC부터. 머리말이 없으면 0부터 몸통.</para>
/// <para>명령: 00 = 프레임 끝 (1/60초) / 01 rr dd = YM2612 포트 0 / 02 rr dd = 포트 1 / 03 dd = SN76489.
/// 한 프레임 안의 DAC 쓰기(포트 0 · 레지스터 2A)는 프레임 안에 고르게 나눈다 (GME와 같이).</para>
/// </summary>
public static class GymFormat
{
    public static readonly string[] Extensions = { ".gym" };
    public const int HeaderSize = 0x1AC;
    public const double Ym2612Clock = 7670453, Sn76489Clock = 3579545;

    public static VgmSong Load(string path)
    {
        var v = Read(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
        v.FilePath = path;
        return v;
    }

    public static VgmSong Read(byte[] d, string fallbackTitle = "")
    {
        if (d.Length < 1) throw new InvalidDataException("GYM 파일이 비었습니다.");
        bool hasHeader = d.Length >= 4 && d[0] == 'G' && d[1] == 'Y' && d[2] == 'M' && d[3] == 'X';
        string title = fallbackTitle ?? "", game = "", copyright = "", dumper = "", comment = "";
        long loopFrame = 0;
        byte[] body = d;
        bool packed = false;
        if (hasHeader)
        {
            if (d.Length < HeaderSize) throw new InvalidDataException("GYM 머리말이 잘렸습니다.");
            string t = Text(d, 0x04, 32);
            if (t.Length > 0) title = t;
            game = Text(d, 0x24, 32);
            copyright = Text(d, 0x44, 32);
            dumper = Text(d, 0x84, 32);
            comment = Text(d, 0xA4, 256);
            loopFrame = U32(d, 0x1A4);
            uint unpacked = U32(d, 0x1A8);
            if (unpacked != 0)
            {
                packed = true;
                body = Inflate(d, HeaderSize, (int)Math.Min(unpacked, 64u << 20));
            }
            else
            {
                body = new byte[d.Length - HeaderSize];
                Array.Copy(d, HeaderSize, body, 0, body.Length);
            }
        }
        else if (d[0] > 3) throw new InvalidDataException("GYM 파일이 아닙니다 (첫 명령이 0~3이 아님).");

        var w = new VgmWriter { System = "Sega Mega Drive / Genesis (GYM)", Title = title, Game = game, Ripper = dumper, Notes = comment };
        w.SetClock(VgmWriter.ClockYm2612, Ym2612Clock);
        w.SetClock(VgmWriter.ClockSn76489, Sn76489Clock);
        // SN76489 (세가): 귀환 패턴 0x0009, 시프트 폭 16
        w.SetHeaderByte(0x28, 0x09); w.SetHeaderByte(0x29, 0x00); w.SetHeaderByte(0x2A, 16);

        const int frameSamples = VgmSong.Rate / 60;   // 735
        var dac = new System.Collections.Generic.List<byte>();
        int p = 0;
        long frame = 0;
        while (p < body.Length)
        {
            if (loopFrame > 0 && frame == loopFrame - 1) w.MarkLoop();
            // 프레임 하나: 00까지 (DAC는 모아 두었다가 프레임 안에 나눈다)
            dac.Clear();
            while (p < body.Length)
            {
                int c = body[p++];
                if (c == 0) break;
                if (c == 1)
                {
                    if (p + 2 > body.Length) { p = body.Length; break; }
                    int r = body[p], v = body[p + 1];
                    p += 2;
                    if (r == 0x2A) dac.Add((byte)v);
                    else w.Write(0x52, r, v);
                }
                else if (c == 2)
                {
                    if (p + 2 > body.Length) { p = body.Length; break; }
                    w.Write(0x53, body[p], body[p + 1]);
                    p += 2;
                }
                else if (c == 3)
                {
                    if (p + 1 > body.Length) { p = body.Length; break; }
                    w.Write(0x50, body[p++]);
                }
                // 그 밖의 바이트는 건너뜀 (깨진 GYM이 많다)
            }
            if (dac.Count == 0) w.Wait(frameSamples);
            else
            {
                long done = 0;
                for (int i = 0; i < dac.Count; i++)
                {
                    w.Write((byte)0x52, 0x2A, (int)dac[i]);
                    long next = (long)(i + 1) * frameSamples / dac.Count;
                    w.Wait(next - done);
                    done = next;
                }
            }
            frame++;
        }

        var song = VgmFormat.Read(w.ToArray());
        song.SourceFormat = "GYM" + (packed ? " (압축)" : "") + (loopFrame > 0 ? " · 루프" : "");
        if (copyright.Length > 0 && song.ReleaseDate.Length == 0) song.ReleaseDate = copyright;
        return song;
    }

    /// <summary>프레임 수 (= 길이 × 60).</summary>
    public static long FrameCount(byte[] body)
    {
        long n = 0;
        for (int p = 0; p < body.Length;)
        {
            switch (body[p++])
            {
                case 0: n++; break;
                case 1: case 2: p += 2; break;
                case 3: p += 1; break;
            }
        }
        return n;
    }

    private static byte[] Inflate(byte[] d, int at, int size)
    {
        using var ms = new MemoryStream(d, at, d.Length - at);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    private static string Text(byte[] d, int at, int max)
    {
        int n = 0;
        while (n < max && at + n < d.Length && d[at + n] != 0) n++;
        return KoreanText.ShiftJis.GetString(d, at, n).Trim();
    }

    private static uint U32(byte[] d, int o) { return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)); }
}
