using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// MSX 음악 드라이버 곡 파일 → KSS 이미지 (<see cref="Kss"/>로 재생): MGS (MGSDRV) · BGM (勤労5号 Kinrou5) · MPK (MPK 1.03 / 1.06) · OPX (OPX4KSS).
/// 곡 파일 + 드라이버 바이너리 + 작은 init / play 틀을 libkss (<c>src/kss/*2kss.c</c>, ISC 라이선스, © Mitsutaka Okazaki)와 같은 자리에 놓는다.
/// <para>드라이버 바이너리는 원작자의 저작물이라 (MGSDRV © Ain · GIGAMIX, Kinrou5 © Keiichi Kuroda, MPK © K-KAZ, OPX4KSS © Mikasen) 함께 배포하지 않는다:
/// <c>resource\kssdrv</c> (또는 <c>resource</c>, <see cref="Folders"/>)에 직접 둔다 — 원래 파일 (mgsdrv.com · kinrou5.bin · mpk.bin · mpk103.bin · opx4kss.bin)
/// 또는 libkss kss-drivers의 C 헤더 (mgsdrv.h · kinrou5.h · mpk106.h · mpk103.h · opx4kss.h, 바이트 배열 글) 그대로.</para>
/// <para>틀은 libkss 확장 I/O (40h · 41h · 42h)로 곡 끝 · 되풀이 횟수를 알린다 → 길이를 저절로 정함 (되풀이 2번 + 페이드, 끝나는 곡은 끝 + 1초, 알리지 않으면 2분).</para>
/// </summary>
public static class KssDrivers
{
    public static readonly string[] Extensions = { ".mgs", ".bgm", ".mpk", ".opx" };
    public const double DetectSeconds = 600;

    /// <summary>드라이버를 찾을 폴더 (앞이 먼저). 기본 = 실행 폴더의 resource\kssdrv, resource.</summary>
    public static readonly List<string> Folders = new List<string>
    {
        Path.Combine(AppContext.BaseDirectory, "resource", "kssdrv"),
        Path.Combine(AppContext.BaseDirectory, "resource"),
    };

    public enum Driver { Mgsdrv, Kinrou5, Mpk106, Mpk103, Opx4kss }

    /// <summary>드라이버마다 찾을 파일 이름 (앞이 먼저).</summary>
    public static string[] FileNames(Driver d)
    {
        switch (d)
        {
            case Driver.Mgsdrv: return new[] { "mgsdrv.com", "mgsdrv.bin", "mgsdrv.h" };
            case Driver.Kinrou5: return new[] { "kinrou5.bin", "kinrou5.h" };
            case Driver.Mpk106: return new[] { "mpk.bin", "mpk106.bin", "mpk106.h" };
            case Driver.Mpk103: return new[] { "mpk103.bin", "mpk103.h" };
            default: return new[] { "opx4kss.bin", "opx4kss.h" };
        }
    }

    private static readonly Dictionary<string, byte[]> cache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>드라이버 바이트 (못 찾으면 null).</summary>
    public static byte[] Find(Driver d)
    {
        foreach (var dir in Folders)
            foreach (var name in FileNames(d))
            {
                string p = Path.Combine(dir, name);
                if (!File.Exists(p)) continue;
                lock (cache)
                {
                    if (cache.TryGetValue(p, out var c)) return c;
                    var b = name.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ? ParseCHeader(File.ReadAllText(p)) : File.ReadAllBytes(p);
                    cache[p] = b;
                    return b;
                }
            }
        return null;
    }

    /// <summary>C 바이트 배열 글 (주석을 빼고 0x.. 를 차례로).</summary>
    public static byte[] ParseCHeader(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\n]*", " ");
        var l = new List<byte>();
        foreach (Match m in Regex.Matches(text, @"0[xX]([0-9a-fA-F]{1,2})\b"))
            l.Add(byte.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return l.ToArray();
    }

    public static bool IsDriverSong(string path)
    {
        string ext = Path.GetExtension(path ?? "");
        return Extensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------ 곡 → KSS

    public sealed class Converted
    {
        public byte[] Kss;
        public string Format = "", Title = "", Extra = "";
        public Driver Driver;
    }

    /// <summary>KSSX 머리말 (확장 0x10, 장치 = FM-PAC + RAM, 곡 0 하나).</summary>
    private static byte[] Image(int loadAdr, int loadSize, int initAdr, int playAdr, byte devices = 0x05)
    {
        var b = new byte[0x20 + loadSize];
        Encoding.ASCII.GetBytes("KSSX").CopyTo(b, 0);
        b[4] = (byte)loadAdr; b[5] = (byte)(loadAdr >> 8);
        b[6] = (byte)loadSize; b[7] = (byte)(loadSize >> 8);
        b[8] = (byte)initAdr; b[9] = (byte)(initAdr >> 8);
        b[0x0A] = (byte)playAdr; b[0x0B] = (byte)(playAdr >> 8);
        b[0x0E] = 0x10; b[0x0F] = devices;
        return b;
    }

    private static void Put(byte[] img, int loadAdr, int addr, byte[] src, int srcAt, int len)
    {
        int at = 0x20 + addr - loadAdr;
        len = Math.Min(len, Math.Min(src.Length - srcAt, img.Length - at));
        if (len > 0) Array.Copy(src, srcAt, img, at, len);
    }

    private static byte[] Need(Driver d)
    {
        var b = Find(d);
        if (b == null || b.Length == 0)
            throw new FileNotFoundException("MSX 드라이버가 없습니다: " + string.Join(" / ", FileNames(d)) + " 를 resource\\kssdrv 폴더에 두세요 (원래 파일 또는 libkss kss-drivers의 .h).");
        return b;
    }

    public static Converted Convert(string ext, byte[] data)
    {
        ext = (ext ?? "").ToLowerInvariant();
        if (ext == ".mgs") return Mgs(data);
        if (ext == ".bgm") return Bgm(data);
        if (ext == ".mpk") return Mpk(data);
        if (ext == ".opx") return Opx(data);
        throw new InvalidDataException("MSX 드라이버 곡이 아닙니다: " + ext);
    }

    // MGS (MGSDRV): 곡 0x200, 드라이버 0x6000 (MGSDRV.COM + 0x0D), init 0x8000, play 0x8100
    private static readonly byte[] MgsInit =
    {
        0x3E, 0x0E, 0xD3, 0x7C, 0x3E, 0x20, 0xD3, 0x7D,          // FM-PAC 리듬 끔
        0xCD, 0x10, 0x60, 0x3E, 0x01, 0xDD, 0x77, 0x00, 0xDD, 0x77, 0x01,   // INIT
        0xDD, 0x22, 0xF0, 0x7F,                                  // LD (7FF0H),IX
        0xAF, 0x32, 0xFE, 0xBF, 0x3E, 0x3F, 0x32, 0x00, 0x90,    // SCC 켬 (메가롬 방식)
        0x11, 0x00, 0x02, 0x06, 0xFF, 0x60, 0x68, 0xCD, 0x16, 0x60,   // PLAY (DE = 0200H)
        0x3E, 0x7F, 0xD3, 0x40, 0xAF, 0xD3, 0x41, 0xD3, 0x42, 0xD3, 0x43, 0xD3, 0x44,
        0xC9,
    };
    private static readonly byte[] MgsPlay =
    {
        0xCD, 0x1F, 0x60, 0xDD, 0x2A, 0xF0, 0x7F,
        0xDD, 0x7E, 0x08, 0xFE, 0x00, 0x20, 0x04, 0x3E, 0x01, 0xD3, 0x41,   // (IX+8) = 0 → 끝
        0xDD, 0x7E, 0x05, 0xD3, 0x42,                                        // 되풀이 횟수
        0xDD, 0x7E, 10, 0xD3, 0x43, 0xDD, 0x7E, 11, 0xD3, 0x44,
        0xAF, 0xDD, 0x77, 10, 0xDD, 0x77, 11,
        0xC9,
    };

    public static Converted Mgs(byte[] data)
    {
        if (data.Length < 32 || data[0] != 'M' || data[1] != 'G' || data[2] != 'S') throw new InvalidDataException("MGS 파일이 아닙니다 (머리말 MGS가 없음).");
        var drv = Need(Driver.Mgsdrv);
        const int load = 0x200, size = 0x8000;
        var img = Image(load, size, 0x8000, 0x8100);
        Put(img, load, 0x200, data, 0, Math.Min(data.Length, 0x6000 - 0x200));
        Put(img, load, 0x6000, drv, 0x0D, 0x2000);
        Put(img, load, 0x8000, MgsInit, 0, MgsInit.Length);
        Put(img, load, 0x8100, MgsPlay, 0, MgsPlay.Length);
        return new Converted { Kss = img, Driver = Driver.Mgsdrv, Format = "MGS " + Encoding.ASCII.GetString(data, 3, 3), Title = Line(data, 8, 128).Replace('\t', ' ').Trim() };
    }

    // BGM (Kinrou5): 드라이버 0x6000 (kinrou5.bin + 7), init 0x7E00, play 0x7F00, 곡 0x8000
    private static readonly byte[] BgmInit =
    {
        0xCD, 0x20, 0x60, 0x3E, 0x3F, 0x32, 0x10, 0x60, 0x32, 0x11, 0x60,
        0x21, 0x07, 0x80, 0xED, 0x5B, 0x01, 0x80,
        0x3E, 0x00, 0xCD, 0x26, 0x60, 0xAF, 0xCD, 0x38, 0x60,
        0x3E, 0x7F, 0xD3, 0x40, 0xAF, 0xD3, 0x41, 0xD3, 0x42,
        0xC9,
    };
    private static readonly byte[] BgmPlay = { 0xCD, 0x29, 0x60, 0xCD, 0x32, 0x60, 0xE6, 0x01, 0xAF, 0xD3, 0x41, 0x2B, 0x7D, 0xD3, 0x42, 0xC9 };

    public static Converted Bgm(byte[] data)
    {
        if (data.Length < 16 || data[0] != 0xFE) throw new InvalidDataException("BGM (Kinrou5) 파일이 아닙니다 (첫 바이트 FEh가 아님).");
        var drv = Need(Driver.Kinrou5);
        const int load = 0x6000;
        int size = Math.Min(0x8000 + data.Length - load, 0x10000 - load);
        var img = Image(load, size, 0x7E00, 0x7F00, 0x01);
        Put(img, load, 0x6000, drv, 7, Math.Min(drv.Length - 7, 0x1E00));
        Put(img, load, 0x7E00, BgmInit, 0, BgmInit.Length);
        Put(img, load, 0x7F00, BgmPlay, 0, BgmPlay.Length);
        Put(img, load, 0x8000, data, 0, data.Length);
        string title = "", extra = "";
        if (data.Length > 0x60 && data[0x50] == 'B' && data[0x51] == 'T' && data[0x52] == 'O')
        {
            int baseAdr = data[0x59] | (data[0x5A] << 8);
            title = Line(data, (data[0x5B] | (data[0x5C] << 8)) - baseAdr + 7, 255, true);
            extra = Line(data, (data[0x5D] | (data[0x5E] << 8)) - baseAdr + 7, 255, true);
        }
        return new Converted { Kss = img, Driver = Driver.Kinrou5, Format = "BGM (Kinrou5)", Title = title, Extra = extra };
    }

    // MPK 1.06 / 1.03: 드라이버 0x4000, init 0x6000, play 0x6100, 곡 0x200 (작으면) 또는 0x6200
    private static readonly byte[] Mpk106Init =
    {
        0x0E, 0x00, 0xCD, 0x10, 0x40, 0x3E, 0x01, 0x32, 0x49, 0x5D, 0x32, 0x4A, 0x5D,
        0x21, 0x00, 0xA0, 0x0E, 0x01, 0xAF, 0xCD, 0x10, 0x40, 0x0E, 0x0C, 0xCD, 0x10, 0x40,
        0x3E, 0x7F, 0xD3, 0x40, 0xAF, 0xD3, 0x41, 0xD3, 0x42, 0xC9,
    };
    private static readonly byte[] Mpk103Init =
    {
        0x0E, 0x00, 0xCD, 0x10, 0x40, 0x21, 0x00, 0xA0, 0x0E, 0x01, 0xAF, 0xCD, 0x10, 0x40, 0x0E, 0x0C, 0xCD, 0x10, 0x40,
        0x3E, 0x7F, 0xD3, 0x40, 0xAF, 0xD3, 0x41, 0xD3, 0x42, 0xC9,
    };
    private static readonly byte[] MpkPlay = { 0xCD, 0x13, 0x40, 0x0E, 0x0E, 0xAF, 0xCD, 0x10, 0x40, 0xD3, 0x42, 0xC9 };

    public static Converted Mpk(byte[] data)
    {
        if (data.Length < 16 || data[0] != 'M' || data[1] != 'P' || data[2] != 'K') throw new InvalidDataException("MPK 파일이 아닙니다 (머리말 MPK가 없음).");
        int.TryParse(Encoding.ASCII.GetString(data, 3, 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ver);
        bool v103 = ver <= 103;
        var drv = Need(v103 ? Driver.Mpk103 : Driver.Mpk106);
        int dataAdr, load, size;
        const int initAdr = 0x6000, playAdr = 0x6100;
        if (data.Length < 0x4000 - 0x200) { dataAdr = 0x200; load = 0x200; size = playAdr + 0x100 - load; }
        else { dataAdr = 0x6200; load = 0x4000; size = Math.Min(dataAdr + data.Length - load, 0x10000 - load); }
        var img = Image(load, size, initAdr, playAdr);
        Put(img, load, 0x4000, drv, 0, 0x2000);
        var init = (byte[])(v103 ? Mpk103Init : Mpk106Init).Clone();
        int hl = v103 ? 6 : 14;   // LD HL,곡 주소
        init[hl] = (byte)dataAdr; init[hl + 1] = (byte)(dataAdr >> 8);
        if (v103) Put(img, load, 0x4364, new byte[] { 0x00, 0x3E, 0x01 }, 0, 3);   // libkss: MPK103.BIN 고침
        Put(img, load, initAdr, init, 0, init.Length);
        Put(img, load, playAdr, MpkPlay, 0, MpkPlay.Length);
        Put(img, load, dataAdr, data, 0, data.Length);
        // 제목 (8부터 CR까지), 다음 줄들 = 작곡 · 편곡 · 메모
        var lines = new List<string>();
        int p = 8;
        for (int i = 0; i < 4 && p < data.Length; i++)
        {
            int e = p;
            while (e < data.Length && data[e] != 0x0D && e - p < 256) e++;
            lines.Add(KoreanText.ShiftJis.GetString(data, p, e - p).Trim());
            if (e + 1 >= data.Length || data[e + 1] != 0x0A) break;
            p = e + 2;
        }
        string extra = lines.Count > 1 ? "Composer: " + lines[1] + (lines.Count > 2 ? " / Arranger: " + lines[2] : "") + (lines.Count > 3 && lines[3].Length > 0 ? " / " + lines[3] : "") : "";
        return new Converted { Kss = img, Driver = v103 ? Driver.Mpk103 : Driver.Mpk106, Format = "MPK " + ver.ToString("000", CultureInfo.InvariantCulture), Title = lines.Count > 0 ? lines[0] : "", Extra = extra };
    }

    // OPX (OPX4KSS): 드라이버 0x100, init 0x106, play 0x103, FM-BIOS 흉내 0x4000 (libkss의 FMTONE 조각 + 악기 자리), 곡 0x8000
    public static Converted Opx(byte[] data)
    {
        if (data.Length < 128 + 32) throw new InvalidDataException("OPX 파일이 너무 짧습니다.");
        var drv = Need(Driver.Opx4kss);
        const int load = 0x100;
        int size = Math.Min(0x8000 + data.Length - load, 0x10000 - load);
        var img = Image(load, size, 0x106, 0x103);
        Put(img, load, 0x100, drv, 0, Math.Min(drv.Length, 0x4000 - 0x100));
        Put(img, load, 0x401C, Encoding.ASCII.GetBytes("OPLL"), 0, 4);
        Put(img, load, 0x4C00, OpxFmTone.Bytes, 0, OpxFmTone.Bytes.Length);
        byte[] voice = new byte[32];
        Encoding.ASCII.GetBytes("Pia34567").CopyTo(voice, 0);
        new byte[] { 0x00, 0x00, 0x0e, 0x00, 0x00, 0x00, 0x00, 0x00, 0x22, 0x1B, 0xf0, 0x0f, 0x00, 0x00, 0x00, 0x00, 0x21, 0x00, 0xf0, 0x0f, 0x00, 0x00, 0x00, 0x00 }.CopyTo(voice, 8);
        for (int i = 0; i < 62; i++) Put(img, load, 0x62EC + i * 32, voice, 0, 32);
        Put(img, load, 0x8000, data, 0, data.Length);
        string title = KoreanText.ShiftJis.GetString(data, 0, 53).TrimEnd(' ', '\0');
        string extra = (KoreanText.ShiftJis.GetString(data, 55, 55) + " " + KoreanText.ShiftJis.GetString(data, 110, 15)).Trim(' ', '\0');
        return new Converted { Kss = img, Driver = Driver.Opx4kss, Format = "OPX", Title = title.Trim(), Extra = extra };
    }

    private static string Line(byte[] d, int at, int max, bool stopBelowSpace = false)
    {
        if (at < 0 || at >= d.Length) return "";
        int e = at;
        while (e < d.Length && e - at < max && d[e] != 0x0D && !(stopBelowSpace && d[e] < 0x20) && d[e] != 0) e++;
        return KoreanText.ShiftJis.GetString(d, at, e - at).Trim();
    }

    // ------------------------------------------------------------ 열기 (ChipSongs)

    /// <summary>목록에 보일 제목 (드라이버 없이 곡 파일 머리말만).</summary>
    public static string Title(string path)
    {
        try
        {
            var d = File.ReadAllBytes(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".mgs" && d.Length > 8) return Line(d, 8, 128).Replace('\t', ' ').Trim();
            if (ext == ".mpk" && d.Length > 8) return Line(d, 8, 256);
            if (ext == ".opx" && d.Length > 53) return KoreanText.ShiftJis.GetString(d, 0, 53).Trim(' ', '\0');
            if (ext == ".bgm" && d.Length > 0x60 && d[0x50] == 'B' && d[0x51] == 'T' && d[0x52] == 'O')
                return Line(d, (d[0x5B] | (d[0x5C] << 8)) - (d[0x59] | (d[0x5A] << 8)) + 7, 255, true);
        }
        catch (Exception) { }
        return "";
    }

    public static VgmSong Load(string path)
    {
        var c = Convert(Path.GetExtension(path), File.ReadAllBytes(path));
        var v = Kss.Render(c.Kss, 0, Kss.DefaultSeconds, Kss.DefaultFade, DetectSeconds, 2);
        v.FilePath = path;
        v.TitleEn = c.Title.Length > 0 ? c.Title : Path.GetFileNameWithoutExtension(path);
        v.SystemEn = "MSX";
        if (c.Extra.Length > 0) v.Notes = c.Extra;
        v.SourceFormat = c.Format + " → KSS";
        return v;
    }
}
