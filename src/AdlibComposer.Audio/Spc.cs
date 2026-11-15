using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// SPC (슈퍼 패미컴 사운드 순간 상태: "SNES-SPC700 Sound File Data"). 재생은 <see cref="Chips.SnesSpc"/>가 스스로 돌린다:
/// VGM 재생기에는 길이만 든 VGM(<see cref="VgmSong.SpcImage"/>)을 넘겨 채널 믹서 · 페이드 · 이동을 같이 쓴다.
/// <para>ID666 태그 (0x2E~): 제목 32 · 게임 32 · 뜬 사람 16 · 메모 32, 그다음 글자 형식(날짜 11 · 길이 초 3 · 페이드 ms 5 · 작곡 32)
/// 또는 바이너리 형식(날짜 4 · 길이 초 3 · 페이드 ms 4 · 작곡 32) — 길이 칸이 숫자 글자인지로 가른다. 길이가 없으면 3분.</para>
/// </summary>
public static class Spc
{
    public static readonly string[] Extensions = { ".spc" };
    public const double DefaultSeconds = 180, DefaultFade = 8;
    private const string Magic = "SNES-SPC700 Sound File Data";

    public sealed class Tags
    {
        public string Title = "", Game = "", Dumper = "", Comment = "", Artist = "";
        public double Seconds, Fade = -1;
    }

    public static bool IsSpc(byte[] d)
    {
        return d.Length >= 0x10180 && Encoding.ASCII.GetString(d, 0, Magic.Length) == Magic;
    }

    private static string Text(byte[] d, int at, int len)
    {
        int n = 0;
        while (n < len && at + n < d.Length && d[at + n] != 0) n++;
        return KoreanText.ShiftJis.GetString(d, at, n).Trim();
    }

    private static bool Digits(byte[] d, int at, int len)
    {
        for (int i = 0; i < len; i++)
        {
            byte b = d[at + i];
            if (b == 0) return true;
            if (b < (byte)'0' || b > (byte)'9') return false;
        }
        return true;
    }

    public static Tags ReadTags(byte[] d)
    {
        var t = new Tags();
        if (d.Length < 0x100 || d[0x23] != 0x1A) return t;   // 0x23 = 26: 태그 있음
        t.Title = Text(d, 0x2E, 32); t.Game = Text(d, 0x4E, 32); t.Dumper = Text(d, 0x6E, 16); t.Comment = Text(d, 0x7E, 32);
        bool text = Digits(d, 0xA9, 3) && Digits(d, 0xAC, 5) && (d[0xA9] != 0 || d[0xAC] != 0 || d[0xB0] >= 0x20 || d[0xB0] == 0);
        if (text)
        {
            if (int.TryParse(Text(d, 0xA9, 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec)) t.Seconds = sec;
            if (int.TryParse(Text(d, 0xAC, 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms)) t.Fade = ms / 1000.0;
            t.Artist = Text(d, 0xB1, 32);
        }
        else
        {
            t.Seconds = d[0xA9] | (d[0xAA] << 8) | (d[0xAB] << 16);
            t.Fade = (d[0xAC] | (d[0xAD] << 8) | (d[0xAE] << 16) | (d[0xAF] << 24)) / 1000.0;
            t.Artist = Text(d, 0xB0, 32);
        }
        if (t.Seconds > 3600) t.Seconds = 0;
        if (t.Fade > 60) t.Fade = -1;
        return t;
    }

    public static VgmSong Load(string path)
    {
        var d = File.ReadAllBytes(path);
        var v = Read(d, Path.GetFileNameWithoutExtension(path));
        v.FilePath = path;
        return v;
    }

    /// <summary>SPC → 길이만 든 VGM (재생은 SpcImage로 SPC 칩이 직접).</summary>
    public static VgmSong Read(byte[] d, string fallbackTitle = "")
    {
        if (!IsSpc(d)) throw new InvalidDataException("SPC 파일이 아닙니다 (머리말 SNES-SPC700이 없음).");
        var t = ReadTags(d);
        double sec = t.Seconds > 0 ? t.Seconds : DefaultSeconds;
        double fade = t.Fade >= 0 ? t.Fade : DefaultFade;
        var w = new VgmWriter { Title = t.Title, Game = t.Game, System = "Super Famicom / SNES", Author = t.Artist, Ripper = t.Dumper, Notes = t.Comment };
        w.Wait((long)Math.Round((sec + fade) * VgmSong.Rate));
        var v = VgmFormat.Read(w.ToArray());
        v.SpcImage = d;
        v.FadeSeconds = Math.Min(fade, sec + fade);
        if (v.TitleEn.Length == 0 && v.TitleJp.Length == 0) v.TitleEn = fallbackTitle;
        v.SourceFormat = "SPC" + (t.Seconds > 0 ? "" : " (길이 정보 없음: " + DefaultSeconds + "초)");
        return v;
    }
}
