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

/// <summary>
/// Instrument Maker 현대식 색: 흰 바탕 + 회색 단계(면 · 선 · 글자) + 강조색 하나(셸의 청록).
/// 켜짐 · 고른 것 · 누른 건반 · 슬라이더 값 = 강조색, 나머지는 무채색.
/// </summary>
internal static class InstTheme
{
    public static readonly Color Back = Color.White;
    public static readonly Color Surface = Color.FromArgb(246, 247, 249);     // 정보 줄 · 꺼진 머리글 · 슬라이더 홈
    public static readonly Color Hover = Color.FromArgb(236, 239, 242);
    public static readonly Color Line = Color.FromArgb(218, 222, 228);        // 테두리 · 구분선
    public static readonly Color Ink = Color.FromArgb(44, 48, 56);            // 글자 · 검은 건반
    public static readonly Color Muted = Color.FromArgb(118, 124, 134);       // 보조 글자
    public static readonly Color Accent = Shell.ShellTheme.Accent;            // 청록
    public static readonly Color AccentDark = Shell.ShellTheme.AccentDark;
    public static readonly Color AccentSoft = Color.FromArgb(214, 238, 235);  // 슬라이더 채움 · 누른 흰 건반
}

/// <summary>프로그램 아이콘 (실행 파일과 같은 AdlibComposer.ico, 리소스에서 한 번만 읽는다).</summary>
internal static class AppIcon
{
    private static Icon icon;
    private static bool loaded;

    public static Icon Get()
    {
        if (loaded) return icon;
        loaded = true;
        try
        {
            using (var s = typeof(AppIcon).Assembly.GetManifestResourceStream("AdlibComposer.App.AppIcon.ico"))
                if (s != null) icon = new Icon(s);
        }
        catch (System.Exception) { icon = null; }   // 아이콘이 없어도 프로그램은 동작
        return icon;
    }

    public static void Apply(System.Windows.Forms.Form f)
    {
        var i = Get();
        if (i != null) f.Icon = i;
    }
}
