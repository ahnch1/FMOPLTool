using System.Drawing;

namespace AdlibComposer.App;

/// <summary>원본(640x400 흑백 DOS 화면)의 느낌을 내는 색/글꼴.</summary>
internal static class Theme
{
    public static readonly Color Back = Color.White;
    public static readonly Color Fore = Color.Black;
    public static readonly Color GridLine = Color.FromArgb(190, 190, 190);
    public static readonly Color OctaveLine = Color.FromArgb(110, 110, 110);
    public static readonly Color BlackKeyRow = Color.FromArgb(228, 228, 228);
    public static readonly Color Selected = Color.FromArgb(0, 90, 200);
    public static readonly Color PlayCursor = Color.FromArgb(200, 0, 0);
    public static readonly Color EditCursor = Color.FromArgb(0, 120, 0);
    public static readonly Font UiFont = new Font("Arial", 9.5f, FontStyle.Bold);
    public static readonly Font SmallFont = new Font("Arial", 7.5f, FontStyle.Bold);
}
