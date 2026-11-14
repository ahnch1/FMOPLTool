using System;
using System.Drawing;
using System.Globalization;

namespace AdlibComposer.App.Shell;

/// <summary>
/// 보이스(채널)별 색: Composer 노트 막대 · Note 음표 · Jukebox 믹서 선택 칸과 건반이 같이 쓴다.
/// 11색 = 보이스 1~9 (리듬 모드는 7~11 = BD SD TO CY HH). 채널이 더 많은 곡(SOP 20 · MIDI 16 · S3M 32)은 돌려 쓴다.
/// 설정(FMOPLTool.cfg의 VoiceColors)에 저장하고 Preferences에서 바꾼다. 바뀌면 Changed.
/// </summary>
internal static class VoiceColors
{
    public const int Count = 11;

    public static readonly Color[] Defaults =
    {
        Color.FromArgb(38, 166, 154), Color.FromArgb(66, 133, 244), Color.FromArgb(234, 88, 76), Color.FromArgb(244, 180, 0),
        Color.FromArgb(142, 106, 216), Color.FromArgb(246, 128, 38), Color.FromArgb(52, 168, 83), Color.FromArgb(222, 84, 148),
        Color.FromArgb(0, 151, 199), Color.FromArgb(141, 110, 99), Color.FromArgb(120, 132, 148),
    };

    /// <summary>설정 화면 이름 (리듬 모드 이름 같이).</summary>
    public static readonly string[] Labels = { "1", "2", "3", "4", "5", "6", "7 · BD", "8 · SD", "9 · TO", "10 · CY", "11 · HH" };

    private static Color[] colors = (Color[])Defaults.Clone();

    /// <summary>색이 바뀌었다 (화면을 다시 그릴 때).</summary>
    public static event Action Changed;

    /// <summary>보이스(채널) 번호의 색. 11개를 넘으면 돌려 쓴다.</summary>
    public static Color Get(int voice)
    {
        return colors[((voice % Count) + Count) % Count];
    }

    public static Color[] All { get { return (Color[])colors.Clone(); } }

    public static void Set(Color[] c)
    {
        var n = (Color[])Defaults.Clone();
        if (c != null) for (int i = 0; i < Math.Min(c.Length, Count); i++) n[i] = c[i];
        colors = n;
        if (Changed != null) Changed();
    }

    /// <summary>"#26A69A,#4285F4,..." (설정 파일).</summary>
    public static string Format(Color[] c)
    {
        var parts = new string[Count];
        for (int i = 0; i < Count; i++)
        {
            var x = c != null && i < c.Length ? c[i] : Defaults[i];
            parts[i] = "#" + x.R.ToString("X2") + x.G.ToString("X2") + x.B.ToString("X2");
        }
        return string.Join(",", parts);
    }

    /// <summary>Format의 반대. 읽을 수 없는 칸은 기본 색.</summary>
    public static Color[] Parse(string s)
    {
        var r = (Color[])Defaults.Clone();
        if (string.IsNullOrWhiteSpace(s)) return r;
        var parts = s.Split(',');
        for (int i = 0; i < Math.Min(parts.Length, Count); i++)
        {
            string p = parts[i].Trim().TrimStart('#');
            int v;
            if (p.Length == 6 && int.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                r[i] = Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        }
        return r;
    }

    /// <summary>a를 b 쪽으로 t만큼 (0 = a, 1 = b).</summary>
    public static Color Blend(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}
