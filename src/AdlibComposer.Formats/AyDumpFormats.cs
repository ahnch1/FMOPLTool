using System;
using System.IO;
using System.Linq;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// AY-3-8910 / YM2149 레지스터 기록 곡 → VGM (<see cref="VgmSong"/>, AY 명령 A0h): 프레임마다 레지스터 14 (또는 16)개.
/// <list type="bullet">
/// <item><b>YM</b> (아타리 ST · CPC 등, Leonard / STSound): 보통 LHA -lh5- 단계 0으로 감싸 있다 (<see cref="Lha.UnpackSingle"/>).
/// YM2! · YM3! = 2MHz · 50Hz · 레지스터 14개 평면 (모든 프레임의 R0, 다음 R1 …), YM3b = 끝 4바이트가 되돌아갈 프레임,
/// YM5! · YM6! = "LeOnArD!" + 프레임 수 · 속성 (bit0 평면) · 디지드럼 수 · 클럭 · 프레임 Hz · 되돌아갈 프레임 · 덧붙임 크기 (빅 엔디언), 디지드럼, 제목 · 작곡 · 설명 (0 끝), 레지스터 16개.</item>
/// <item><b>VTX</b> (ZX 스펙트럼 · CPC, Vortex): "ay" / "ym", 스테레오 방식, 되돌아갈 프레임, 클럭, 프레임 Hz, 해, 풀린 크기, 글 5개 (제목 · 작곡 · 프로그램 · 트래커 · 설명), 머리말 없는 -lh5- 흐름 → 레지스터 14개 평면.</item>
/// <item><b>PSG</b> (ZX 스펙트럼 에뮬레이터 기록): "PSG" 1Ah + 16바이트 머리말, FFh = 프레임 끝, FEh n = 4n 프레임 쉼, FDh = 끝, 00h~0Fh rr vv = 레지스터 쓰기. 1.7734MHz · 50Hz.</item>
/// </list>
/// R13 (엔벨로프 모양)은 FFh가 아닐 때만 쓴다 (쓰면 엔벨로프를 처음부터). YM5 · YM6의 특수 효과 (SID 음 · 디지드럼 · Sinus-SID · Sync-Buzzer, R14 · R15와 R1 · R3 · R6 · R8~R10의 윗비트)는 아직 내지 않고 윗비트를 지워 보통 AY로 낸다.
/// </summary>
public static class AyDumpFormats
{
    public static readonly string[] YmExtensions = { ".ym" };
    public static readonly string[] VtxExtensions = { ".vtx" };
    public static readonly string[] PsgExtensions = { ".psg" };
    public const double AtariClock = 2000000, ZxClock = 1773400;

    public sealed class Dump
    {
        public string Format = "", Title = "", Author = "", Comment = "", Program = "", Year = "";
        public double Clock = AtariClock, FrameHz = 50;
        public int Frames, LoopFrame = -1;
        /// <summary>프레임마다 레지스터 (frames × 16, R14 · R15는 YM5/6 효과 자리).</summary>
        public byte[] Regs;
        public bool Ym, Effects;
        public int Stereo;
    }

    public static VgmSong LoadYm(string path) { return Finish(ReadYm(File.ReadAllBytes(path)), path); }
    public static VgmSong LoadVtx(string path) { return Finish(ReadVtx(File.ReadAllBytes(path)), path); }
    public static VgmSong LoadPsg(string path) { return Finish(ReadPsg(File.ReadAllBytes(path)), path); }

    private static VgmSong Finish(Dump d, string path)
    {
        var v = ToVgm(d, Path.GetFileNameWithoutExtension(path));
        v.FilePath = path;
        return v;
    }

    // ------------------------------------------------------------ YM

    public static Dump ReadYm(byte[] file)
    {
        var b = Lha.UnpackSingle(file);
        if (b.Length < 4) throw new InvalidDataException("YM 파일이 너무 짧습니다.");
        string id = Encoding.ASCII.GetString(b, 0, 4);
        var d = new Dump { Format = id.TrimEnd('!'), Ym = true };
        switch (id)
        {
            case "YM2!":
            case "YM3!":
            case "YM3b":
            {
                int len = b.Length - 4 - (id == "YM3b" ? 4 : 0);
                d.Frames = len / 14;
                if (id == "YM3b") d.LoopFrame = (int)(b[b.Length - 4] | (b[b.Length - 3] << 8) | (b[b.Length - 2] << 16) | (b[b.Length - 1] << 24));
                d.Regs = Planes(b, 4, d.Frames, 14, true);
                break;
            }
            case "YM5!":
            case "YM6!":
            {
                if (b.Length < 34 || Encoding.ASCII.GetString(b, 4, 8) != "LeOnArD!") throw new InvalidDataException("YM5 / YM6 머리말 (LeOnArD!)이 없습니다.");
                int p = 12;
                d.Frames = (int)Be32(b, ref p);
                long attrib = Be32(b, ref p);
                int drums = Be16(b, ref p);
                d.Clock = Be32(b, ref p);
                d.FrameHz = Be16(b, ref p);
                d.LoopFrame = (int)Be32(b, ref p);
                int skip = Be16(b, ref p);
                p += skip;
                for (int i = 0; i < drums && p + 4 <= b.Length; i++) { long size = Be32(b, ref p); p += (int)Math.Min(size, b.Length - p); }
                d.Title = Str(b, ref p); d.Author = Str(b, ref p); d.Comment = Str(b, ref p);
                if (d.Frames < 0 || p + (long)d.Frames * 16 > b.Length) d.Frames = Math.Max(0, (b.Length - p) / 16);
                d.Regs = Planes(b, p, d.Frames, 16, (attrib & 1) != 0);
                d.Effects = true;
                break;
            }
            case "YM4!":
                throw new InvalidDataException("YM4 형식은 STSound도 읽지 않습니다 (YM5로 바꿔 주세요).");
            default:
                throw new InvalidDataException("YM 파일이 아닙니다 (머리말 " + id + ").");
        }
        if (d.Clock <= 0) d.Clock = AtariClock;
        if (d.FrameHz <= 0) d.FrameHz = 50;
        return d;
    }

    // ------------------------------------------------------------ VTX

    public static Dump ReadVtx(byte[] b)
    {
        if (b.Length < 16) throw new InvalidDataException("VTX 파일이 너무 짧습니다.");
        string id = Encoding.ASCII.GetString(b, 0, 2).ToLowerInvariant();
        if (id != "ay" && id != "ym") throw new InvalidDataException("VTX 파일이 아닙니다 (머리말 ay / ym이 없음).");
        var d = new Dump { Format = "VTX " + id.ToUpperInvariant(), Ym = id == "ym", Stereo = b[2] };
        d.LoopFrame = b[3] | (b[4] << 8);
        d.Clock = (uint)(b[5] | (b[6] << 8) | (b[7] << 16) | (b[8] << 24));
        d.FrameHz = b[9];
        int year = b[10] | (b[11] << 8);
        if (year > 0) d.Year = year.ToString();
        int size = (int)(uint)(b[12] | (b[13] << 8) | (b[14] << 16) | (b[15] << 24));
        int p = 16;
        d.Title = Str(b, ref p); d.Author = Str(b, ref p); d.Program = Str(b, ref p);
        string tracker = Str(b, ref p); d.Comment = Str(b, ref p);
        if (tracker.Length > 0) d.Program = (d.Program + " · " + tracker).Trim(' ', '·');
        if (size <= 0 || size > 64 << 20) throw new InvalidDataException("VTX 풀린 크기가 이상합니다: " + size);
        var raw = Lha.Decompress(b, p, b.Length - p, size, "-lh5-");
        d.Frames = raw.Length / 14;
        d.Regs = Planes(raw, 0, d.Frames, 14, true);
        if (d.Clock <= 0) d.Clock = ZxClock;
        if (d.FrameHz <= 0) d.FrameHz = 50;
        if (d.LoopFrame >= d.Frames) d.LoopFrame = -1;
        return d;
    }

    // ------------------------------------------------------------ PSG

    public static Dump ReadPsg(byte[] b)
    {
        if (b.Length < 16 || b[0] != 'P' || b[1] != 'S' || b[2] != 'G' || b[3] != 0x1A) throw new InvalidDataException("PSG 파일이 아닙니다 (머리말 PSG 1Ah가 없음).");
        var d = new Dump { Format = "PSG", Clock = ZxClock, FrameHz = 50 };
        if (b[4] >= 10 && b[5] > 0) d.FrameHz = b[5];   // 판 10 이상: 인터럽트 Hz
        var frames = new System.Collections.Generic.List<byte[]>();
        var cur = Enumerable.Repeat((byte)0, 16).ToArray();
        cur[13] = 0xFF;
        var now = (byte[])cur.Clone();
        int p = 16;
        while (p < b.Length)
        {
            int c = b[p++];
            if (c == 0xFF) { frames.Add(now); now = (byte[])now.Clone(); now[13] = 0xFF; continue; }
            if (c == 0xFE)
            {
                int n = p < b.Length ? b[p++] * 4 : 0;
                for (int i = 0; i < n; i++) { frames.Add(now); now = (byte[])now.Clone(); now[13] = 0xFF; }
                continue;
            }
            if (c == 0xFD) break;
            if (c < 16) { if (p < b.Length) now[c] = b[p++]; continue; }
            p++;   // 모르는 레지스터 (값 건너뜀)
        }
        d.Frames = frames.Count;
        d.Regs = new byte[d.Frames * 16];
        for (int i = 0; i < d.Frames; i++) Array.Copy(frames[i], 0, d.Regs, i * 16, 16);
        return d;
    }

    // ------------------------------------------------------------ VGM으로

    public static VgmSong ToVgm(Dump d, string fallbackTitle = "")
    {
        var w = new VgmWriter { System = d.Format.StartsWith("YM") ? "Atari ST (YM)" : "ZX Spectrum (" + d.Format + ")", Title = d.Title.Length > 0 ? d.Title : fallbackTitle ?? "", Author = d.Author, Date = d.Year, Notes = (d.Comment + (d.Program.Length > 0 ? " / " + d.Program : "")).Trim(' ', '/') };
        w.SetClock(VgmWriter.ClockAy8910, d.Clock);
        w.SetHeaderByte(0x78, (byte)(d.Ym ? 0x10 : 0x00));
        w.SetHeaderByte(0x79, 0x01);
        double frameSamples = VgmSong.Rate / d.FrameHz, owed = 0;
        var last = Enumerable.Repeat(-1, 14).ToArray();
        for (int f = 0; f < d.Frames; f++)
        {
            if (f == d.LoopFrame && f > 0) { w.MarkLoop(); for (int r = 0; r < 14; r++) last[r] = -1; }
            else if (f == 0 && d.LoopFrame == 0) w.MarkLoop();
            int at = f * 16;
            for (int r = 0; r < 14; r++)
            {
                int v = d.Regs[at + r];
                if (d.Effects)
                {
                    // YM5 · YM6 효과 비트 지우기
                    if (r == 1 || r == 3 || r == 5) v &= 0x0F;
                    else if (r == 6) v &= 0x1F;
                    else if (r >= 8 && r <= 10) v &= 0x1F;
                }
                if (r == 13)
                {
                    if (v == 0xFF) continue;
                    v &= 0x0F;
                }
                else if (v == last[r]) continue;
                last[r] = v;
                w.Write(0xA0, r, v);
            }
            owed += frameSamples;
            long n = (long)owed;
            owed -= n;
            w.Wait(n);
        }
        var song = VgmFormat.Read(w.ToArray());
        song.SourceFormat = d.Format + " · " + d.Frames + " 프레임 · " + d.FrameHz + "Hz" + (d.LoopFrame > 0 ? " · 루프" : "");
        song.TitleEn = w.Title;
        song.AuthorEn = d.Author;
        return song;
    }

    /// <summary>레지스터 자료 → 프레임마다 16바이트 (planar = 레지스터 평면 순서). 모자란 레지스터는 0, R13 = FFh.</summary>
    private static byte[] Planes(byte[] b, int at, int frames, int regs, bool planar)
    {
        var o = new byte[frames * 16];
        for (int f = 0; f < frames; f++)
        {
            for (int r = 0; r < 16; r++)
            {
                byte v = r == 13 ? (byte)0xFF : (byte)0;
                if (r < regs)
                {
                    long i = planar ? at + (long)r * frames + f : at + (long)f * regs + r;
                    if (i < b.Length) v = b[i];
                }
                o[f * 16 + r] = v;
            }
        }
        return o;
    }

    private static long Be32(byte[] b, ref int p) { long v = p + 3 < b.Length ? ((long)b[p] << 24) | ((long)b[p + 1] << 16) | ((long)b[p + 2] << 8) | b[p + 3] : 0; p += 4; return v; }
    private static int Be16(byte[] b, ref int p) { int v = p + 1 < b.Length ? (b[p] << 8) | b[p + 1] : 0; p += 2; return v; }

    private static string Str(byte[] b, ref int p)
    {
        int s = p;
        while (p < b.Length && b[p] != 0) p++;
        string r = KoreanText.ShiftJis.GetString(b, s, p - s).Trim();
        if (p < b.Length) p++;
        return r;
    }
}
