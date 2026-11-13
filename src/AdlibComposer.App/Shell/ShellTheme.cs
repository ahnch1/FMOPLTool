using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace AdlibComposer.App.Shell;

/// <summary>프로그램 틀(사이드바·툴바·로그)의 현대식 색/글꼴. 각 페이지 안의 원본 재현 화면(Theme)과는 따로 둔다.</summary>
internal static class ShellTheme
{
    // 사이드바 (어두운 남색)
    public static readonly Color SideBack = Color.FromArgb(30, 33, 41);
    public static readonly Color SideHover = Color.FromArgb(40, 44, 54);
    public static readonly Color SideSelected = Color.FromArgb(48, 54, 66);
    public static readonly Color SideText = Color.FromArgb(232, 234, 238);
    public static readonly Color SideMuted = Color.FromArgb(130, 138, 150);
    public static readonly Color SideLine = Color.FromArgb(52, 57, 68);

    // 강조색 (청록)
    public static readonly Color Accent = Color.FromArgb(38, 166, 154);
    public static readonly Color AccentDark = Color.FromArgb(26, 128, 118);
    public static readonly Color Blue = Color.FromArgb(0, 103, 192);

    // 본문 / 머리글
    public static readonly Color ContentBack = Color.White;
    public static readonly Color HeaderLine = Color.FromArgb(225, 228, 232);
    public static readonly Color Muted = Color.FromArgb(100, 106, 115);

    // 로그 (콘솔)
    public static readonly Color LogBack = Color.FromArgb(27, 30, 36);
    public static readonly Color LogBar = Color.FromArgb(38, 42, 50);
    public static readonly Color LogText = Color.FromArgb(214, 216, 220);
    public static readonly Color LogTime = Color.FromArgb(120, 126, 136);
    public static readonly Color LogInfo = Color.FromArgb(78, 201, 176);
    public static readonly Color LogWarn = Color.FromArgb(230, 180, 80);
    public static readonly Color LogError = Color.FromArgb(240, 100, 90);

    public static readonly Font Ui = new Font("Segoe UI", 9f);
    public static readonly Font UiSemibold = new Font("Segoe UI Semibold", 9.5f);
    public static readonly Font Nav = new Font("Segoe UI Semibold", 10f);
    public static readonly Font Section = new Font("Segoe UI", 7.5f, FontStyle.Bold);
    public static readonly Font AppTitle = new Font("Segoe UI", 15f, FontStyle.Bold);
    public static readonly Font PageTitle = new Font("Segoe UI Semibold", 13f);
    public static readonly Font Console = new Font("Consolas", 9f);

    /// <summary>아이콘 글꼴: Windows 11 Segoe Fluent Icons, 없으면 Windows 10 Segoe MDL2 Assets.</summary>
    public static readonly string IconFontName = PickIconFont();

    private static string PickIconFont()
    {
        try
        {
            using (var fonts = new InstalledFontCollection())
                foreach (var f in fonts.Families)
                    if (f.Name == "Segoe Fluent Icons") return f.Name;
        }
        catch (Exception) { }
        return "Segoe MDL2 Assets";
    }
}

/// <summary>아이콘 글리프 (Segoe MDL2 Assets / Segoe Fluent Icons 코드포인트).</summary>
internal static class Glyphs
{
    public const char Composer = '';     // Audio (음표)
    public const char Instrument = '';   // Equalizer
    public const char Jukebox = '';      // MusicAlbum
    public const char Note = '';
    public const char Orgel = '';        // Audio (오르골)
    public const char Tracker = '';      // TVMonitor (트래커 화면)         // QuickNote
    public const char NewFile = '';      // Add
    public const char Open = '';         // OpenFile
    public const char Save = '';         // Save
    public const char Library = '';      // Library (뱅크)
    public const char Play = '';
    public const char Stop = '';
    public const char Settings = '';
    public const char Folder = '';
}

/// <summary>글리프를 비트맵으로 그려 캐시한다 (툴바/버튼 이미지).</summary>
internal static class GlyphImages
{
    private static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();

    public static Bitmap Get(char glyph, int size, Color color)
    {
        string key = (int)glyph + ":" + size + ":" + color.ToArgb();
        Bitmap bmp;
        if (cache.TryGetValue(key, out bmp)) return bmp;
        bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        using (var f = new Font(ShellTheme.IconFontName, size * 0.72f, GraphicsUnit.Pixel))
        using (var b = new SolidBrush(color))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString(glyph.ToString(), f, b, new RectangleF(0, 0, size, size), sf);
        }
        cache[key] = bmp;
        return bmp;
    }

    /// <summary>원 안에 글리프 (사이드바 메뉴 아이콘).</summary>
    public static void DrawCircleIcon(Graphics g, Rectangle r, char glyph, Color circle, Color fore)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(circle)) g.FillEllipse(b, r);
        g.SmoothingMode = old;
        using (var f = new Font(ShellTheme.IconFontName, r.Height * 0.46f, GraphicsUnit.Pixel))
        using (var fb = new SolidBrush(fore))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString(glyph.ToString(), f, fb, new RectangleF(r.X, r.Y + 1, r.Width, r.Height), sf);
        }
    }
}
