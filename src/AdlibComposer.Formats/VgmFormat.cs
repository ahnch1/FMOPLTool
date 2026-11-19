using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// VGM 곡 (.vgm, gzip으로 줄인 .vgz): 소리 칩 레지스터 쓰기와 기다림(44100Hz 샘플 단위)을 그대로 적은 기록.
/// 이 프로그램이 내는 칩: AY-3-8910 / YM2149 (PSG, 0xA0), K051649 / K052539 (SCC · SCC+, 0xD2),
/// YM3812 (OPL2, 0x5A) · YM3526 (OPL, 0x5B) · Y8950 (MSX-AUDIO FM 부분, 0x5C) · YMF262 (OPL3, 0x5E · 0x5F),
/// SN76489 (0x50 · 0x4F), YM2413 (OPLL, 0x51), YM2612 (OPN2, 0x52 · 0x53 · DAC 0x80~0x8F), YM2151 (OPM, 0x54),
/// YM2203 (OPN, 0x55), YM2608 (OPNA, 0x56 · 0x57, ADPCM 블록 0x81), OKIM6295 (0xB8, ROM 블록 0x8B).
/// 둘째 칩(듀얼 칩)은 소리 내지 않는다.
/// 그 밖의 칩 명령은 길이만 알고 건너뛴다 (<see cref="UnsupportedChips"/>에 이름).
/// 명령 해석은 재생기(AdlibComposer.Audio.VgmPlayer)가 <see cref="Data"/>를 직접 읽으며 한다.
/// </summary>
public sealed class VgmSong
{
    public const int Rate = 44100;

    public string FilePath;
    /// <summary>원래 형식 (VGM이 아닌 것을 바꿔 읽었으면 그 이름, 예: "S98 v3" · "KSS").</summary>
    public string SourceFormat = "VGM";
    /// <summary>루프가 없는 곡을 끝에서 줄여 끝낼 시간 (초, 0 = 그대로). KSS처럼 길이를 정해 자른 곡.</summary>
    public double FadeSeconds;
    /// <summary>파일 전체 (gzip을 푼 것).</summary>
    public byte[] Data = new byte[0];
    /// <summary>판 번호 (0x171 = 1.71).</summary>
    public int Version;
    /// <summary>명령이 시작하는 곳 (파일 안 위치).</summary>
    public int DataOffset;
    /// <summary>명령이 끝나는 곳 (EOF 또는 GD3 앞).</summary>
    public int DataEnd;
    /// <summary>되돌아갈 곳 (파일 안 위치), 없으면 0.</summary>
    public int LoopOffset;
    /// <summary>곡 전체 길이 (샘플, 44100Hz).</summary>
    public long TotalSamples;
    /// <summary>되돌아가는 부분 길이 (샘플), 없으면 0.</summary>
    public long LoopSamples;

    public double AyClock;
    /// <summary>AY 종류 (0x00 AY-3-8910, 0x01 AY-3-8912, 0x03 AY8930, 0x10 YM2149, 0x11 YM3439 …).</summary>
    public int AyType;
    /// <summary>AY 깃발 (bit4 = YM2149 클럭 반으로).</summary>
    public int AyFlags;
    public double SccClock;
    /// <summary>K052539 (SCC+): 다섯 채널 모두 자기 파형.</summary>
    public bool SccPlus;
    public double Ym3812Clock, Ym3526Clock, Y8950Clock, Ymf262Clock;
    /// <summary>둘째 SN76489 클럭 (VGM 클럭 bit30 = 같은 칩 둘, 명령 30h · 3Fh). 메모리 속 곡은 첫째와 다른 클럭일 수 있다 (hoot 세가 System 1: 2MHz + 4MHz).</summary>
    public double Sn76489Clock2;
    /// <summary>둘째 OKIM6295 클럭 (VGM 클럭 bit30, 레지스터 bit7, ROM 블록 크기 bit31). 메모리 속 곡은 첫째와 다른 클럭일 수 있다 (hoot Data East: 1MHz + 2MHz).</summary>
    public double Okim6295Clock2;
    /// <summary>uPD7759 클럭 (VGM 8Ch, 명령 B6h). 슬레이브 모드만 (bit31 마스터 모드 = 자기 ROM은 아직).</summary>
    public double Upd7759Clock;
    /// <summary>QSound 클럭 (VGM B4h, 명령 C4h, ROM 블록 8Fh) — DSP 없는 HLE로 낸다.</summary>
    public double QsoundClock;
    /// <summary>세가 PCM 클럭 (VGM 38h) · 인터페이스 (3Ch: 마스크 &lt;&lt; 16 | 뱅크 자리), 명령 C0h, ROM 블록 80h.</summary>
    public double SegaPcmClock;
    public int SegaPcmInterface;
    public double Sn76489Clock, Ym2413Clock, Ym2612Clock, Ym2151Clock, Ym2203Clock, Ym2608Clock, Okim6295Clock, NesApuClock, Huc6280Clock, Ym2610Clock, Okim6258Clock, Saa1099Clock, GbDmgClock;
    /// <summary>OKIM6258 깃발 (머리말 0x94: bit0~1 분주, bit3 12비트 출력).</summary>
    public int Okim6258Flags;
    /// <summary>YM2610 클럭 bit31: YM2610B (FM 6채널).</summary>
    public bool Ym2610B;
    /// <summary>NES APU 클럭 bit31: FDS 음원이 함께 있다 (0xB4 aa 0x20~0x7F).</summary>
    public bool NesFds;
    /// <summary>NSF에서 바꾼 곡의 확장 음원 비트 (NSF 머리말 0x7B: 1 VRC6, 2 VRC7, 4 FDS, 8 MMC5, 0x10 N163, 0x20 5B). VGM 파일에는 없다 (0).
    /// VRC6 · N163 · MMC5는 예약 명령 0xD7 cc aa dd로 온다.</summary>
    public int NsfChips;
    /// <summary>YM2413 자리가 VRC7 (NSF): 6채널, VRC7 내장 악기.</summary>
    public bool Vrc7;
    /// <summary>SPC 파일 전체 (슈퍼 패미컴): 있으면 SPC 칩이 이것으로 스스로 돈다 (명령은 길이뿐).</summary>
    public byte[] SpcImage;
    /// <summary>스스로 도는 칩을 만드는 함수 (기종 흉내: PC-98 DOS 등). 재생기가 칩을 만들 때마다 부른다 (ISoundChip을 돌려줘야 함). 명령은 길이뿐.</summary>
    public Func<object> SelfRunning;
    /// <summary><see cref="SelfRunning"/> 칩 이름 (칩 요약에 보임).</summary>
    public string SelfRunningName = "";
    /// <summary>SN76489 노이즈 되먹임 · LFSR 너비 (0 = 기본값).</summary>
    public int SnFeedback, SnWidth;
    /// <summary>OKIM6295 핀7 (클럭 칸 bit31).</summary>
    public bool OkiPin7;

    /// <summary>파일에 있지만 이 프로그램이 소리 내지 않는 칩.</summary>
    public readonly List<string> UnsupportedChips = new List<string>();

    // GD3 태그
    public string TitleEn = "", TitleJp = "", GameEn = "", GameJp = "", SystemEn = "", SystemJp = "", AuthorEn = "", AuthorJp = "", ReleaseDate = "", Ripper = "", Notes = "";

    public bool HasPsg { get { return AyClock > 0; } }
    public bool HasScc { get { return SccClock > 0; } }
    public bool HasOpl { get { return Ym3812Clock > 0 || Ym3526Clock > 0 || Y8950Clock > 0 || Ymf262Clock > 0; } }
    public bool IsOpl3 { get { return Ymf262Clock > 0; } }
    /// <summary>PSG · SCC · OPL 말고 다른 칩 (공통 칩 경로로 내는 것).</summary>
    public bool HasOther { get { return Sn76489Clock > 0 || Ym2413Clock > 0 || Ym2612Clock > 0 || Ym2151Clock > 0 || Ym2203Clock > 0 || Ym2608Clock > 0 || Okim6295Clock > 0 || NesApuClock > 0 || Huc6280Clock > 0 || Ym2610Clock > 0 || Okim6258Clock > 0 || Saa1099Clock > 0 || GbDmgClock > 0 || Upd7759Clock > 0 || QsoundClock > 0 || SegaPcmClock > 0 || SpcImage != null || SelfRunning != null; } }
    /// <summary>소리 낼 수 있는 칩이 하나라도 있는가.</summary>
    public bool IsPlayable { get { return HasPsg || HasScc || HasOpl || HasOther; } }
    public bool AyIsYm { get { return AyType >= 0x10; } }

    public string VersionText { get { return (Version >> 8).ToString("X") + "." + (Version & 0xFF).ToString("X2"); } }

    /// <summary>곡 이름: GD3 영어 → 일본어 → 파일 이름.</summary>
    public string Title
    {
        get
        {
            if (TitleEn.Length > 0) return TitleEn;
            if (TitleJp.Length > 0) return TitleJp;
            return FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : "";
        }
    }

    public string Game { get { return GameEn.Length > 0 ? GameEn : GameJp; } }
    public string SystemName { get { return SystemEn.Length > 0 ? SystemEn : SystemJp; } }
    public string Author { get { return AuthorEn.Length > 0 ? AuthorEn : AuthorJp; } }

    public double LengthSeconds { get { return TotalSamples / (double)Rate; } }

    /// <summary>쓰는 칩 이름 (예: "AY-3-8910 + SCC").</summary>
    public string ChipSummary
    {
        get
        {
            var l = new List<string>();
            if (SpcImage != null) l.Add("SPC700 + S-DSP");
            if (SelfRunning != null && SelfRunningName.Length > 0) l.Add(SelfRunningName);
            if (Sn76489Clock > 0) l.Add(Sn76489Clock2 > 0 ? "SN76489 ×2" : "SN76489");
            if (Ym2413Clock > 0) l.Add(Vrc7 ? "VRC7" : "YM2413");
            if (Ym2612Clock > 0) l.Add("YM2612");
            if (Ym2151Clock > 0) l.Add("YM2151");
            if (Ym2203Clock > 0) l.Add("YM2203");
            if (Ym2608Clock > 0) l.Add("YM2608");
            if (Ym2610Clock > 0) l.Add(Ym2610B ? "YM2610B" : "YM2610");
            if (Okim6295Clock > 0) l.Add(Okim6295Clock2 > 0 ? "OKIM6295 ×2" : "OKIM6295");
            if (Upd7759Clock > 0) l.Add("uPD7759");
            if (QsoundClock > 0) l.Add("QSound");
            if (SegaPcmClock > 0) l.Add("SegaPCM");
            if (Okim6258Clock > 0) l.Add("OKIM6258");
            if (Saa1099Clock > 0) l.Add("SAA1099");
            if (GbDmgClock > 0) l.Add("GB DMG");
            if (NesApuClock > 0) l.Add(NesFds ? "2A03 + FDS" : "2A03");
            if ((NsfChips & 0x01) != 0) l.Add("VRC6");
            if ((NsfChips & 0x10) != 0) l.Add("N163");
            if ((NsfChips & 0x08) != 0) l.Add("MMC5");
            if (Huc6280Clock > 0) l.Add("HuC6280");
            if (HasPsg) l.Add((NsfChips & 0x20) != 0 ? "Sunsoft 5B" : AyTypeName);
            if (HasScc) l.Add(SccPlus ? "SCC+" : "SCC");
            if (Ymf262Clock > 0) l.Add("OPL3");
            else if (Ym3812Clock > 0) l.Add("OPL2");
            else if (Y8950Clock > 0) l.Add("Y8950");
            else if (Ym3526Clock > 0) l.Add("OPL");
            return string.Join(" + ", l);
        }
    }

    public string AyTypeName
    {
        get
        {
            switch (AyType)
            {
                case 0x01: return "AY-3-8912";
                case 0x02: return "AY-3-8913";
                case 0x03: return "AY8930";
                case 0x10: return "YM2149";
                case 0x11: return "YM3439";
                case 0x12: return "YMZ284";
                case 0x13: return "YMZ294";
                default: return "AY-3-8910";
            }
        }
    }
}

/// <summary>.vgm / .vgz 읽기.</summary>
public static class VgmFormat
{
    public static readonly string[] Extensions = { ".vgm", ".vgz" };

    public static bool IsVgm(string path)
    {
        string ext = Path.GetExtension(path ?? "");
        return Extensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
    }

    public static VgmSong Load(string path)
    {
        var s = Read(File.ReadAllBytes(path));
        s.FilePath = path;
        return s;
    }

    /// <summary>gzip(1F 8B)이면 풀어서 읽는다.</summary>
    public static VgmSong Read(byte[] raw)
    {
        byte[] d = raw;
        if (d.Length >= 2 && d[0] == 0x1F && d[1] == 0x8B)
        {
            using (var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                gz.CopyTo(ms);
                d = ms.ToArray();
            }
        }
        if (d.Length < 0x40 || d[0] != (byte)'V' || d[1] != (byte)'g' || d[2] != (byte)'m' || d[3] != (byte)' ')
            throw new InvalidDataException("VGM 파일이 아닙니다 (머리말 'Vgm '이 없음).");

        var s = new VgmSong { Data = d };
        s.Version = (int)U32(d, 0x08);
        int eof = Rel(d, 0x04);
        if (eof <= 0 || eof > d.Length) eof = d.Length;
        int dataOfs = s.Version >= 0x150 ? Rel(d, 0x34) : 0;
        if (dataOfs <= 0) dataOfs = 0x40;
        if (dataOfs >= d.Length) throw new InvalidDataException("VGM 명령 위치가 파일 밖입니다.");
        s.DataOffset = dataOfs;
        int gd3 = Rel(d, 0x14);
        s.DataEnd = gd3 > dataOfs && gd3 < eof ? gd3 : eof;
        s.TotalSamples = U32(d, 0x18);
        int loop = Rel(d, 0x1C);
        s.LoopSamples = U32(d, 0x20);
        if (loop >= dataOfs && loop < s.DataEnd && s.LoopSamples > 0) s.LoopOffset = loop; else s.LoopSamples = 0;

        // 머리말 칸: 명령 시작 위치보다 앞에 있는 것만 (그 뒤는 0으로 본다)
        Func<int, uint> clock = o => o + 4 <= dataOfs ? U32(d, o) : 0;
        Func<int, double> hz = o => clock(o) & 0x3FFFFFFF;
        s.AyClock = hz(0x74);
        if (0x79 < dataOfs) { s.AyType = d[0x78]; s.AyFlags = d[0x79]; }
        s.SccClock = hz(0x9C);
        s.SccPlus = (clock(0x9C) & 0x80000000) != 0;
        s.Ym3812Clock = hz(0x50);
        s.Ym3526Clock = hz(0x54);
        s.Y8950Clock = hz(0x58);
        s.Ymf262Clock = hz(0x5C);
        s.Sn76489Clock = hz(0x0C);
        if (s.Sn76489Clock > 0 && (clock(0x0C) & 0x40000000) != 0) s.Sn76489Clock2 = s.Sn76489Clock;
        if (s.Sn76489Clock > 0 && s.Version >= 0x110 && 0x2B < dataOfs) { s.SnFeedback = d[0x28] | (d[0x29] << 8); s.SnWidth = d[0x2A]; }
        s.Ym2413Clock = hz(0x10);
        s.Ym2612Clock = hz(0x2C);
        s.Ym2151Clock = hz(0x30);
        s.Ym2203Clock = hz(0x44);
        s.Ym2608Clock = hz(0x48);
        s.Ym2610Clock = hz(0x4C);
        s.Ym2610B = s.Ym2610Clock > 0 && (clock(0x4C) & 0x80000000) != 0;
        s.Okim6295Clock = hz(0x98);
        s.Okim6258Clock = hz(0x90);
        if (s.Okim6258Clock > 0 && 0x94 < dataOfs) s.Okim6258Flags = d[0x94];
        s.Saa1099Clock = hz(0xC8);
        s.GbDmgClock = hz(0x80);
        s.NesApuClock = hz(0x84);
        s.NesFds = s.NesApuClock > 0 && (clock(0x84) & 0x80000000) != 0;
        s.Huc6280Clock = hz(0xA4);
        s.OkiPin7 = (clock(0x98) & 0x80000000) != 0;
        if (s.Okim6295Clock > 0 && (clock(0x98) & 0x40000000) != 0) s.Okim6295Clock2 = s.Okim6295Clock;
        s.Upd7759Clock = (clock(0x8C) & 0x80000000) != 0 ? 0 : hz(0x8C);   // 마스터 모드 (bit31)는 아직
        if ((clock(0x8C) & 0x80000000) != 0) s.UnsupportedChips.Add("uPD7759 (master)");
        s.QsoundClock = hz(0xB4);
        s.SegaPcmClock = hz(0x38);
        if (s.SegaPcmClock > 0 && 0x3F < dataOfs) s.SegaPcmInterface = (int)clock(0x3C);
        if (s.Version < 0x110 && s.Ym2413Clock > 0)
        {
            // 1.10 전에는 0x10 칸 하나가 YM2413 · YM2612 · YM2151 모두의 클럭: 명령으로 어느 칩인지 본다
            bool o = false, n2 = false, m = false;
            ScanCommands(s, c => { if (c == 0x51) o = true; else if (c == 0x52 || c == 0x53) n2 = true; else if (c == 0x54) m = true; });
            double clk = s.Ym2413Clock;
            s.Ym2413Clock = o ? clk : 0;
            if (n2) s.Ym2612Clock = clk;
            if (m) s.Ym2151Clock = clk;
        }

        var other = new (int ofs, string name)[]
        {
            (0x40, "RF5C68"), (0x60, "YMF278B (OPL4)"), (0x64, "YMF271 (OPX)"),
            (0x68, "YMZ280B"), (0x6C, "RF5C164"), (0x70, "PWM"), (0x88, "MultiPCM"),
            (0xA0, "K054539"), (0xA8, "C140"),
            (0xAC, "K053260"), (0xB0, "Pokey"), (0xB8, "SCSP"), (0xC0, "WonderSwan"), (0xC4, "VSU"),
            (0xCC, "ES5503"), (0xD0, "ES5506"), (0xD8, "X1-010"), (0xDC, "C352"), (0xE0, "GA20"),
        };
        foreach (var (ofs, name) in other)
            if (hz(ofs) > 0) s.UnsupportedChips.Add(name);
        // Y8950은 FM 부분만 (ADPCM은 소리 내지 않는다)
        if (gd3 > 0 && gd3 + 12 <= d.Length) ReadGd3(d, gd3, s);
        if (s.TotalSamples <= 0) s.TotalSamples = CountSamples(s);
        return s;
    }

    /// <summary>명령 바이트를 하나씩 본다 (끝까지 또는 0x66까지).</summary>
    private static void ScanCommands(VgmSong s, Action<int> seen)
    {
        int p = s.DataOffset;
        var d = s.Data;
        while (p < s.DataEnd)
        {
            int c = d[p];
            if (c == 0x66) break;
            seen(c);
            int len = CommandLength(d, p);
            if (len <= 0) break;
            p += len;
        }
    }

    /// <summary>머리말에 길이가 없으면 명령을 훑어 기다림을 더한다.</summary>
    private static long CountSamples(VgmSong s)
    {
        long total = 0;
        int p = s.DataOffset;
        var d = s.Data;
        while (p < s.DataEnd)
        {
            int c = d[p];
            if (c == 0x66) break;
            if (c == 0x61 && p + 2 < d.Length) total += d[p + 1] | (d[p + 2] << 8);
            else if (c == 0x62) total += 735;
            else if (c == 0x63) total += 882;
            else if (c >= 0x70 && c <= 0x7F) total += (c & 15) + 1;
            else if (c >= 0x80 && c <= 0x8F) total += c & 15;
            int len = CommandLength(d, p);
            if (len <= 0) break;
            p += len;
        }
        return total;
    }

    /// <summary>
    /// p에 있는 명령의 길이 (바이트, 명령 바이트 포함). 모르는 명령이면 0 (재생을 멈춘다).
    /// VGM 1.72 명세의 표를 따른다.
    /// </summary>
    public static int CommandLength(byte[] d, int p)
    {
        int c = d[p];
        if (c >= 0x30 && c <= 0x3F) return 2;
        if (c >= 0x40 && c <= 0x4E) return 3;
        if (c == 0x4F || c == 0x50) return 2;
        if (c >= 0x51 && c <= 0x5F) return 3;
        if (c == 0x61) return 3;
        if (c == 0x62 || c == 0x63 || c == 0x66) return 1;
        if (c == 0x67)
        {
            if (p + 7 > d.Length) return 0;
            long size = U32(d, p + 3) & 0x7FFFFFFF;
            return (int)Math.Min(int.MaxValue, 7 + size);
        }
        if (c == 0x68) return 12;
        if (c >= 0x70 && c <= 0x8F) return 1;
        switch (c)
        {
            case 0x90: case 0x91: case 0x95: return 5;
            case 0x92: return 6;
            case 0x93: return 11;
            case 0x94: return 2;
        }
        if (c >= 0xA0 && c <= 0xBF) return 3;
        if (c >= 0xC0 && c <= 0xDF) return 4;
        if (c >= 0xE0) return 5;
        return 0;
    }

    private static void ReadGd3(byte[] d, int at, VgmSong s)
    {
        if (d[at] != (byte)'G' || d[at + 1] != (byte)'d' || d[at + 2] != (byte)'3' || d[at + 3] != (byte)' ') return;
        int len = (int)Math.Min(U32(d, at + 8), (uint)(d.Length - at - 12));
        string all = Encoding.Unicode.GetString(d, at + 12, len & ~1);
        var f = all.Split('\0');
        string F(int i) { return i < f.Length ? f[i].Trim() : ""; }
        s.TitleEn = F(0); s.TitleJp = F(1);
        s.GameEn = F(2); s.GameJp = F(3);
        s.SystemEn = F(4); s.SystemJp = F(5);
        s.AuthorEn = F(6); s.AuthorJp = F(7);
        s.ReleaseDate = F(8); s.Ripper = F(9); s.Notes = F(10);
    }

    public static uint U32(byte[] d, int o) { return o + 4 <= d.Length ? (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)) : 0; }

    /// <summary>머리말의 상대 위치 칸 → 파일 안 위치 (0이면 0).</summary>
    private static int Rel(byte[] d, int o)
    {
        uint v = U32(d, o);
        return v == 0 ? 0 : (int)Math.Min(int.MaxValue, o + (long)v);
    }
}
