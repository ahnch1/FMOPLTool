using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using AdlibComposer.App.Shell;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Jukebox;

/// <summary>
/// Jukebox 플레이어 데크 색: Instrument Maker와 같은 요즘 색 (흰 · 회색 단계 + 청록 강조색 하나).
/// 상자 선 모양은 옛 DOS 플레이어 그대로 (Bevel: 돋움 = 왼쪽 위 밝게 · 오른쪽 아래 어둡게, 오목 = 반대).
/// 채널 색(Shell.VoiceColors, 설정에서 바꿈 · Composer · Note와 같이 씀)만 채널을 가르려고 여러 색.
/// </summary>
internal static class DeckTheme
{
    public static readonly Color Back = Color.FromArgb(234, 237, 241);       // 페이지 바탕 (연회색)
    public static readonly Color Panel = InstTheme.Surface;                   // 돋운 상자
    public static readonly Color PanelLight = Color.White;
    public static readonly Color PanelDark = Color.FromArgb(176, 182, 191);
    public static readonly Color PanelLine = InstTheme.Line;
    public static readonly Color Hover = InstTheme.Hover;
    public static readonly Color Inset = Color.White;                         // 오목 칸 (값 · 이름)
    public static readonly Color InsetLight = Color.White;                    // 오목 칸 (가사)
    public static readonly Color Text = InstTheme.Ink;                        // 오목 칸 위 글자
    public static readonly Color TextDark = InstTheme.Ink;                    // 바탕 · 버튼 위 글자
    public static readonly Color Muted = InstTheme.Muted;
    public static readonly Color Lcd = InstTheme.AccentDark;                  // 숫자 (시간 · 음 · 값)
    public static readonly Color Lit = InstTheme.Accent;                      // 제목 · 재생 버튼
    public static readonly Color LitOnLight = InstTheme.Accent;               // 부른 가사
    public static readonly Color Name = InstTheme.Ink;                        // 악기 이름
    public static readonly Color Warn = Color.FromArgb(214, 84, 70);          // 뱅크에 없는 악기 · 끈 채널
    public static readonly Color Ok = InstTheme.Accent;
    public static readonly Color MeterLow = InstTheme.Accent;
    public static readonly Color MeterHigh = InstTheme.AccentDark;
    public static readonly Color MeterOff = Color.FromArgb(230, 233, 237);
    public static readonly Color Progress = InstTheme.Accent;                 // 진행 막대

    public static readonly Font Title = new Font("Malgun Gothic", 13f, FontStyle.Bold);
    public static readonly Font Info = new Font("Malgun Gothic", 9.5f, FontStyle.Bold);
    public static readonly Font Time = new Font("Consolas", 20f, FontStyle.Bold);
    public static readonly Font Small = new Font("Consolas", 8.5f, FontStyle.Bold);
    public static readonly Font Mono = new Font("Consolas", 9.5f, FontStyle.Bold);
    public static readonly Font LyricBig = new Font("Malgun Gothic", 20f, FontStyle.Bold);
    public static readonly Font LyricNext = new Font("Malgun Gothic", 13f, FontStyle.Bold);
    public static readonly Font Button = new Font("Malgun Gothic", 8.5f, FontStyle.Bold);

    /// <summary>
    /// DOS 상자: fill로 칠하고 테두리 depth픽셀. raised = 왼쪽 위 밝게 · 오른쪽 아래 어둡게 (돋움), false = 반대 (오목).
    /// </summary>
    public static void Bevel(Graphics g, Rectangle r, bool raised, Color fill, int depth = 2)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var oldSmooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
        Color lt = raised ? PanelLight : PanelDark, dk = raised ? PanelDark : PanelLight;
        using (var pl = new Pen(lt))
        using (var pd = new Pen(dk))
            for (int i = 0; i < depth; i++)
            {
                g.DrawLine(pl, r.Left + i, r.Top + i, r.Right - 1 - i, r.Top + i);
                g.DrawLine(pl, r.Left + i, r.Top + i, r.Left + i, r.Bottom - 1 - i);
                g.DrawLine(pd, r.Left + i, r.Bottom - 1 - i, r.Right - 1 - i, r.Bottom - 1 - i);
                g.DrawLine(pd, r.Right - 1 - i, r.Top + i, r.Right - 1 - i, r.Bottom - 1 - i);
            }
        g.SmoothingMode = oldSmooth;
    }

    /// <summary>오목한 검은 칸에 글자 (가운데 맞춤 아님: flags로).</summary>
    public static void InsetText(Graphics g, Rectangle r, string text, Font font, Color color, TextFormatFlags flags)
    {
        Bevel(g, r, false, Inset, 1);
        TextRenderer.DrawText(g, text, font, Rectangle.Inflate(r, (flags & TextFormatFlags.HorizontalCenter) != 0 ? -1 : -4, 0), color, Inset, flags | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>반음 값: ±0, +3, −2.</summary>
    public static string KeyText(int semis) { return semis == 0 ? "±0" : semis > 0 ? "+" + semis : "−" + (-semis); }

    public static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        int s = (int)Math.Floor(seconds);
        return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
    }
}

/// <summary>더블 버퍼 기본 컨트롤.</summary>
[System.ComponentModel.DesignerCategory("Code")]
internal class DeckControl : Control
{
    public DeckControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = DeckTheme.Back;
        ForeColor = DeckTheme.Text;
    }

    protected static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }
}

/// <summary>
/// 곡 정보 상자 (올리브 돋움): 가로로 긴 검은 칸 줄 — 맨 위 상태 줄 (시간 / 곡 전체 · 템포 · 속도 · 키 · 볼륨 · 상태 · 모드),
/// 제목, 곡 파일 + 악기 수, 뱅크 + 악기 수, 가사 파일.
/// </summary>
internal sealed class PlayerDisplay : DeckControl
{
    public string Title = "곡을 고르세요";
    public string SongFile = "";
    public int Loaded, Total;
    public string BankFile = "";
    public int BankCount;
    public bool BankSameName;
    /// <summary>뱅크 줄에 [뱅크 선택…] · [강제 사용] 단추를 그린다 (ROL · IMS일 때만).</summary>
    public bool BankButtons;
    /// <summary>[강제 사용] 켜짐: 이 뱅크를 같은 이름 BNK · 기본 뱅크 대신 모든 ROL · IMS에.</summary>
    public bool BankForced;
    public event Action BankPickRequested;
    public event Action BankForceToggled;
    private Rectangle bankPickRect, bankForceRect;
    /// <summary>마우스가 올라간 뱅크 단추: 0 = 선택, 1 = 강제, -1 = 없음.</summary>
    private int hoverBank = -1;
    public string LyricFile;
    public string State = "정지";
    public double Seconds, TotalSeconds;
    public double Bpm;
    public int SpeedPercent = 100;
    public int Key;
    public int VolumePercent = 100;
    public string Mode = "";
    /// <summary>곡 파일 줄 오른쪽 칸 (null = "악기 불러옴/전체").</summary>
    public string SongTag;
    /// <summary>가사 줄 대신 보일 글 (null = 가사 파일).</summary>
    public string InfoLine;

    /// <summary>아래 표시 방식 (맨 아래 단추 줄: Implay · Ocplay · 노래방). 단추를 누르면 ViewModeRequested.</summary>
    public JukeboxViewMode ViewMode = JukeboxViewMode.Implay;
    public event Action<JukeboxViewMode> ViewModeRequested;
    /// <summary>false = 맨 아래 표시 방식 단추 줄을 그리지 않음 (Hoot 페이지: 가사가 없어 고를 것이 없다).</summary>
    public bool ShowModes = true;
    private static readonly string[] ModeNames = { "Implay", "Ocplay", "노래방", "노래방(Cyberfunk)" };
    private static readonly string[] ModeTips = { "Implay: 가사 + 채널 믹서", "Ocplay: 가사 + 작은 악보 · 세로 음량 막대", "노래방: 곡 폴더의 그림 + 가사",
        "노래방(Cyberfunk): 흐르는 별 · 우주 유영 · 튕기는 도형 + 가사 (오른쪽 위 체크박스로 켜고 끄기)" };
    private readonly Rectangle[] modeRects = new Rectangle[4];
    private int hoverMode = -1;
    private readonly ToolTip modeTip = new ToolTip();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Array.FindIndex(modeRects, r => r.Contains(e.Location));
        int hb = !BankButtons ? -1 : bankPickRect.Contains(e.Location) ? 0 : bankForceRect.Contains(e.Location) ? 1 : -1;
        if (h == hoverMode && hb == hoverBank) return;
        hoverMode = h;
        hoverBank = hb;
        Cursor = h >= 0 || hb >= 0 ? Cursors.Hand : Cursors.Default;
        modeTip.SetToolTip(this, h >= 0 ? ModeTips[h]
            : hb == 0 ? "ROL · IMS에 쓸 뱅크 파일 (BNK) 고르기 — 지금 곡에 바로 (재생 중이면 같은 자리부터)"
            : hb == 1 ? "켜면 이 뱅크를 모든 ROL · IMS에 (같은 이름 BNK · 기본 뱅크 대신), 설정에 저장\n끄면 다시 같은 이름 BNK → 기본 뱅크 순서로" : null);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { if (hoverMode >= 0 || hoverBank >= 0) { hoverMode = -1; hoverBank = -1; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (BankButtons && bankPickRect.Contains(e.Location)) { BankPickRequested?.Invoke(); return; }
        if (BankButtons && bankForceRect.Contains(e.Location)) { BankForceToggled?.Invoke(); return; }
        int i = Array.FindIndex(modeRects, r => r.Contains(e.Location));
        if (i >= 0) ViewModeRequested?.Invoke((JukeboxViewMode)i);
    }

    /// <summary>뱅크 줄: [뱅크 파일][n개 · 같은 이름][뱅크 선택…][□ 강제 사용].</summary>
    private void BankRow(Graphics g, Rectangle r)
    {
        string tag = BankCount + "개" + (BankForced ? " · 강제" : BankSameName ? " · 같은 이름" : "");
        if (!BankButtons)
        {
            bankPickRect = bankForceRect = Rectangle.Empty;
            TagRow(g, r, BankFile, tag, DeckTheme.Text);
            return;
        }
        const string pick = "뱅크 선택…", force = "강제 사용";
        int pw = TextRenderer.MeasureText(g, pick, DeckTheme.Button).Width + 14;
        int box = Math.Max(9, r.Height - 10);
        int fw = TextRenderer.MeasureText(g, force, DeckTheme.Button).Width + box + 16;
        bankForceRect = new Rectangle(r.Right - fw, r.Y, fw, r.Height);
        bankPickRect = new Rectangle(bankForceRect.X - 4 - pw, r.Y, pw, r.Height);
        TagRow(g, new Rectangle(r.X, r.Y, Math.Max(40, bankPickRect.X - 4 - r.X), r.Height), BankFile, tag, BankForced ? DeckTheme.Lcd : DeckTheme.Text);

        DeckTheme.Bevel(g, bankPickRect, true, hoverBank == 0 ? DeckTheme.Hover : DeckTheme.Panel, 1);
        TextRenderer.DrawText(g, pick, DeckTheme.Button, bankPickRect, DeckTheme.TextDark, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        DeckTheme.Bevel(g, bankForceRect, !BankForced, BankForced ? DeckTheme.Lit : hoverBank == 1 ? DeckTheme.Hover : DeckTheme.Panel, 1);
        var cb = new Rectangle(bankForceRect.X + 6, r.Y + (r.Height - box) / 2, box, box);
        using (var bg = new SolidBrush(Color.White)) g.FillRectangle(bg, cb);
        using (var pen = new Pen(DeckTheme.TextDark)) g.DrawRectangle(pen, cb);
        if (BankForced)
            using (var pen = new Pen(Color.Black, 2))
                g.DrawLines(pen, new[] { new Point(cb.X + 2, cb.Y + box / 2), new Point(cb.X + box / 2 - 1, cb.Bottom - 3), new Point(cb.Right - 2, cb.Y + 2) });
        TextRenderer.DrawText(g, force, DeckTheme.Button, new Rectangle(cb.Right + 4, r.Y, bankForceRect.Right - cb.Right - 6, r.Height), BankForced ? Color.White : DeckTheme.TextDark,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    /// <summary>표시 방식 단추 줄 (고른 것 = 청록 바탕).</summary>
    private void ModeRow(Graphics g, Rectangle r)
    {
        int n = ModeNames.Length;
        int w = (r.Width - 4 * (n - 1)) / n;
        for (int i = 0; i < n; i++)
        {
            var b = new Rectangle(r.X + i * (w + 4), r.Y, i == n - 1 ? r.Right - (r.X + i * (w + 4)) : w, r.Height);
            modeRects[i] = b;
            bool on = (int)ViewMode == i;
            DeckTheme.Bevel(g, b, !on, on ? DeckTheme.Lit : hoverMode == i ? DeckTheme.Hover : DeckTheme.Panel, 1);
            // 넓이가 모자라면 작은 글자로
            var font = TextRenderer.MeasureText(g, ModeNames[i], DeckTheme.Info).Width + 6 > b.Width ? DeckTheme.Button : DeckTheme.Info;
            TextRenderer.DrawText(g, ModeNames[i], font, b, on ? Color.White : DeckTheme.TextDark, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(DeckTheme.Back);
        DeckTheme.Bevel(g, new Rectangle(0, 0, Width, Height), true, DeckTheme.Panel, 2);

        const int pad = 9, gap = 5;
        int rowH = Math.Max(18, Math.Min(28, (Height - pad * 2 - gap * 5) * 10 / 64));   // 제목 줄은 1.4배, 맨 아래 표시 방식 단추 줄
        int lw = Math.Max(60, Width - pad * 2);
        var left = TextFormatFlags.Left;
        var center = TextFormatFlags.HorizontalCenter;

        // 맨 위: 상태 줄 [시간 / 전체 | 템포 · 속도 · 키 · 볼륨 | 상태 · 모드]
        int y = pad;
        StatusRow(g, new Rectangle(pad, y, lw, rowH));
        y += rowH + gap;

        // 제목 (초록) · 곡 · 뱅크 · 가사
        int titleH = rowH * 14 / 10;
        DeckTheme.InsetText(g, new Rectangle(pad, y, lw, titleH), Title, DeckTheme.Title, DeckTheme.Lit, center);
        y += titleH + gap;
        if (!string.IsNullOrEmpty(SongFile))
        {
            TagRow(g, new Rectangle(pad, y, lw, rowH), SongFile, SongTag ?? "악기 " + Loaded + "/" + Total, Loaded >= Total ? DeckTheme.Lcd : DeckTheme.Warn);
            y += rowH + gap;
            BankRow(g, new Rectangle(pad, y, lw, rowH));
            y += rowH + gap;
            if (InfoLine != null) DeckTheme.InsetText(g, new Rectangle(pad, y, lw, rowH), InfoLine, DeckTheme.Mono, DeckTheme.Text, left | TextFormatFlags.EndEllipsis);
            else DeckTheme.InsetText(g, new Rectangle(pad, y, lw, rowH), "가사  " + (LyricFile ?? "없음"), DeckTheme.Mono, LyricFile != null ? DeckTheme.Text : DeckTheme.Muted, left);
        }
        else
            DeckTheme.InsetText(g, new Rectangle(pad, y, lw, rowH), "왼쪽 목록에서 곡을 두 번 클릭하면 재생합니다", DeckTheme.Mono, DeckTheme.Muted, left);
        if (ShowModes) ModeRow(g, new Rectangle(pad, Height - pad - rowH, lw, rowH));
        else for (int i = 0; i < modeRects.Length; i++) modeRects[i] = Rectangle.Empty;
    }

    /// <summary>상태 줄: 한 칸 안에 왼쪽 시간 (초록 LCD), 가운데 템포 · 속도 · 키 · 볼륨, 오른쪽 상태 · 모드.</summary>
    private void StatusRow(Graphics g, Rectangle r)
    {
        DeckTheme.InsetText(g, r, "", DeckTheme.Mono, DeckTheme.Text, TextFormatFlags.Left);
        var inner = Rectangle.Inflate(r, -8, 0);
        var vc = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        string time = DeckTheme.Clock(Seconds) + " / " + DeckTheme.Clock(TotalSeconds);
        int tw = TextRenderer.MeasureText(g, time, DeckTheme.Mono).Width + 4;
        TextRenderer.DrawText(g, time, DeckTheme.Mono, new Rectangle(inner.X, inner.Y, tw, inner.Height), DeckTheme.Lcd, DeckTheme.Inset, vc | TextFormatFlags.Left);
        string adj = (Bpm > 0 ? "♩=" + Math.Round(Bpm) + "  " : "") + "속도 " + SpeedPercent + "%  키 " + DeckTheme.KeyText(Key) + "  볼륨 " + VolumePercent + "%";
        int aw = TextRenderer.MeasureText(g, adj, DeckTheme.Mono).Width + 4;
        string st = State + (string.IsNullOrEmpty(Mode) ? "" : "  " + Mode);
        int restX = inner.X + tw + 14;
        int restW = Math.Max(0, inner.Right - restX);
        if (aw + 60 <= restW)
        {
            TextRenderer.DrawText(g, adj, DeckTheme.Mono, new Rectangle(restX, inner.Y, aw, inner.Height), DeckTheme.Text, DeckTheme.Inset, vc | TextFormatFlags.Left);
            TextRenderer.DrawText(g, st, DeckTheme.Small, new Rectangle(restX + aw + 14, inner.Y, Math.Max(0, inner.Right - restX - aw - 14), inner.Height), DeckTheme.Muted, DeckTheme.Inset,
                vc | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }
        else
            TextRenderer.DrawText(g, adj + "  " + st, DeckTheme.Mono, new Rectangle(restX, inner.Y, restW, inner.Height), DeckTheme.Text, DeckTheme.Inset, vc | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    /// <summary>[값            ][태그] 두 칸.</summary>
    private static void TagRow(Graphics g, Rectangle r, string value, string tag, Color tagColor)
    {
        int tw = Math.Min(r.Width / 2, TextRenderer.MeasureText(tag, DeckTheme.Mono).Width + 14);
        DeckTheme.InsetText(g, new Rectangle(r.X, r.Y, r.Width - tw - 4, r.Height), value, DeckTheme.Mono, DeckTheme.Text, TextFormatFlags.HorizontalCenter);
        DeckTheme.InsetText(g, new Rectangle(r.Right - tw, r.Y, tw, r.Height), tag, DeckTheme.Mono, tagColor, TextFormatFlags.HorizontalCenter);
    }
}

/// <summary>가사 상자 (밝은 회색 오목 칸): 지금 줄(부른 칸은 초록, 노래방처럼)과 다음 줄.</summary>
internal sealed class LyricsPanel : DeckControl
{
    private IssLyrics lyrics;
    private LyricState state;

    /// <summary>가사. 바꾸면 바로 다시 그린다 (가사 없는 곡으로 바꿨을 때 앞 곡 가사가 남지 않게).</summary>
    public IssLyrics Lyrics
    {
        get { return lyrics; }
        set { if (!ReferenceEquals(lyrics, value)) { lyrics = value; Invalidate(); } }
    }

    public LyricState State
    {
        get { return state; }
        set { if (!ReferenceEquals(state, value)) { state = value; Invalidate(); } }
    }
    public string Empty = "가사 파일(.ISS)이 없습니다";

    // 글자 크기는 칸 높이에 맞춘다 (지금 줄 ≈ 안쪽 높이의 38%, 다음 줄 ≈ 25%, 원래 크기 20pt · 13pt까지)
    private Font bigFont = DeckTheme.LyricBig, nextFont = DeckTheme.LyricNext;
    private int fontsFor = -1;

    private void FitFonts()
    {
        int inner = Math.Max(20, Height - 16);
        if (inner == fontsFor) return;
        fontsFor = inner;
        if (!ReferenceEquals(bigFont, DeckTheme.LyricBig)) bigFont.Dispose();
        if (!ReferenceEquals(nextFont, DeckTheme.LyricNext)) nextFont.Dispose();
        float big = Math.Min(DeckTheme.LyricBig.SizeInPoints, inner * 0.38f * 0.75f);
        float next = Math.Min(DeckTheme.LyricNext.SizeInPoints, inner * 0.25f * 0.75f);
        bigFont = big >= DeckTheme.LyricBig.SizeInPoints ? DeckTheme.LyricBig : new Font("Malgun Gothic", Math.Max(7f, big), FontStyle.Bold);
        nextFont = next >= DeckTheme.LyricNext.SizeInPoints ? DeckTheme.LyricNext : new Font("Malgun Gothic", Math.Max(6f, next), FontStyle.Bold);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(DeckTheme.Back);
        // UUI: 돋운 상자 안에 흰 오목 칸
        DeckTheme.Bevel(g, new Rectangle(0, 0, Width, Height), true, DeckTheme.Panel, 2);
        DeckTheme.Bevel(g, new Rectangle(8, 8, Width - 16, Height - 16), false, DeckTheme.InsetLight, 1);
        if (Lyrics == null || Lyrics.Lines.Count == 0 || State == null || State.Line < 0)
        {
            if (Lyrics == null)
            {
                FitFonts();
                int h1 = nextFont.Height, h2 = DeckTheme.Small.Height;
                int y0 = (Height - h1 - h2 - 4) / 2;
                TextRenderer.DrawText(g, Empty, nextFont, new Rectangle(0, y0, Width, h1), DeckTheme.Muted, TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, "NO LYRICS DATA LOADED", DeckTheme.Small, new Rectangle(0, y0 + h1 + 4, Width, h2), Color.FromArgb(150, 150, 150), TextFormatFlags.HorizontalCenter);
            }
            else TextRenderer.DrawText(g, "♪", DeckTheme.LyricNext, new Rectangle(0, 0, Width, Height), DeckTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        FitFonts();
        DrawLine(g, Lyrics.Lines[State.Line], State.Lit, bigFont, 8 + (Height - 16) * 0.36f);
        if (State.NextLine >= 0 && State.NextLine < Lyrics.Lines.Count)
            DrawLine(g, Lyrics.Lines[State.NextLine], null, nextFont, 8 + (Height - 16) * 0.79f);
    }

    private void DrawLine(Graphics g, IssLine line, bool[] lit, Font font, float centerY)
    {
        LyricsPainter.DrawLine(g, line, lit, font, centerY, 12, Width - 24, lit == null ? DeckTheme.Muted : DeckTheme.TextDark, DeckTheme.LitOnLight, Color.Empty);
    }
}

/// <summary>가사 한 줄 그리기 (가사 상자 · 노래방 겹침 공용): 글자마다 너비를 재서 가운데에 놓고, 부른 칸은 on 색.</summary>
internal static class LyricsPainter
{
    /// <param name="shadow">글자 그림자 색 (Color.Empty = 없음: 그림 위에 겹칠 때 읽히도록).</param>
    public static void DrawLine(Graphics g, IssLine line, bool[] lit, Font font, float centerY, int left, int width, Color dim, Color on, Color shadow)
    {
        // 앞뒤 공백은 빼고 가운데 맞춤 (원본 두 번째 화면 방식)
        int first = 0, last = line.Cells.Count - 1;
        while (first <= last && line.Cells[first].Text.Trim().Length == 0) first++;
        while (last >= first && line.Cells[last].Text.Trim().Length == 0) last--;
        if (first > last || width <= 0) return;
        var widths = new float[line.Cells.Count];
        float total = 0;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        int spaceW = TextRenderer.MeasureText(g, "가", font, Size.Empty, flags).Width / 2;
        for (int i = first; i <= last; i++)
        {
            var c = line.Cells[i];
            widths[i] = c.Text == " " ? spaceW : TextRenderer.MeasureText(g, c.Text, font, Size.Empty, flags).Width;
            total += widths[i];
        }
        float scale = total > width ? width / total : 1f;
        float x = left + (width - total * scale) / 2;
        int h = font.Height;
        for (int i = first; i <= last; i++)
        {
            var c = line.Cells[i];
            bool lighted = lit != null && c.Column < lit.Length && lit[c.Column];
            if (c.Text != " ")
            {
                var pt = new Point((int)x, (int)(centerY - h / 2));
                if (shadow != Color.Empty) TextRenderer.DrawText(g, c.Text, font, new Point(pt.X + 2, pt.Y + 2), shadow, flags);
                TextRenderer.DrawText(g, c.Text, font, pt, lighted ? on : dim, flags);
            }
            x += widths[i] * scale;
        }
    }
}

/// <summary>재생 진행 막대 (검은 오목 홈 + 주황 채움): 클릭/끌기로 위치 이동.</summary>
internal sealed class SeekBar : DeckControl
{
    private double value;
    private bool dragging;
    private double dragValue;

    /// <summary>끌기를 마쳤을 때 (0~1).</summary>
    public event Action<double> SeekRequested;
    /// <summary>끄는 중 (0~1, 시간 미리보기).</summary>
    public event Action<double> Scrubbing;

    public bool IsDragging { get { return dragging; } }
    public double Value
    {
        get { return value; }
        set { this.value = Math.Max(0, Math.Min(1, value)); if (!dragging) Invalidate(); }
    }

    public SeekBar() { Cursor = Cursors.Hand; Height = 24; }

    private Rectangle Track { get { return new Rectangle(4, Height / 2 - 7, Width - 8, 14); } }

    private double FromX(int x) { var t = Track; return t.Width <= 4 ? 0 : Math.Max(0, Math.Min(1, (x - t.X - 2) / (double)(t.Width - 4))); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !Enabled) return;
        dragging = true;
        Capture = true;
        dragValue = FromX(e.X);
        if (Scrubbing != null) Scrubbing(dragValue);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        dragValue = FromX(e.X);
        if (Scrubbing != null) Scrubbing(dragValue);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        Capture = false;
        value = FromX(e.X);
        Invalidate();
        if (SeekRequested != null) SeekRequested(value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(DeckTheme.Back);
        var t = Track;
        double v = dragging ? dragValue : value;
        DeckTheme.Bevel(g, t, false, DeckTheme.Inset, 2);
        int fw = (int)Math.Round((t.Width - 4) * v);
        if (fw > 0)
            using (var b = new SolidBrush(Enabled ? DeckTheme.Progress : DeckTheme.Muted))
                g.FillRectangle(b, t.X + 2, t.Y + 3, fw, t.Height - 6);
        if (dragging)
            using (var p = new Pen(DeckTheme.Text)) g.DrawLine(p, t.X + 2 + fw, t.Y + 1, t.X + 2 + fw, t.Bottom - 2);
    }
}

/// <summary>"제목  [−] 값 [+]" 작은 조절기 (속도 / 키 / 볼륨): 올리브 돋움 버튼 + 검은 칸 값. 마우스 휠도 된다.</summary>
internal sealed class Stepper : DeckControl
{
    public string Caption = "";
    public string MinusText = "−", PlusText = "+";
    public Func<string> ValueText = () => "";
    public event Action<int> Step;
    private int hover = 0;   // -1 빼기, 1 더하기
    private int pressed = 0;

    public Stepper() { Height = 34; Width = 190; Cursor = Cursors.Hand; }

    private Rectangle MinusRect { get { int bw = Math.Max(28, TextRenderer.MeasureText(MinusText, DeckTheme.Button).Width + 10); return new Rectangle(CaptionWidth, 4, bw, Height - 8); } }
    private Rectangle PlusRect { get { int bw = Math.Max(28, TextRenderer.MeasureText(PlusText, DeckTheme.Button).Width + 10); return new Rectangle(Width - bw - 1, 4, bw, Height - 8); } }
    private int CaptionWidth { get { return string.IsNullOrEmpty(Caption) ? 0 : TextWidth(Caption) + 8; } }

    private int TextWidth(string s) { return TextRenderer.MeasureText(s, DeckTheme.Info).Width; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = MinusRect.Contains(e.Location) ? -1 : PlusRect.Contains(e.Location) ? 1 : 0;
        if (h != hover) { hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { hover = 0; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int d = MinusRect.Contains(e.Location) ? -1 : PlusRect.Contains(e.Location) ? 1 : 0;
        pressed = d;
        if (d != 0 && Step != null) Step(d);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) { pressed = 0; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Step != null) Step(e.Delta > 0 ? 1 : -1);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(DeckTheme.Back);
        if (CaptionWidth > 0)
            TextRenderer.DrawText(g, Caption, DeckTheme.Info, new Rectangle(0, 0, CaptionWidth, Height), DeckTheme.TextDark, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        RetroButton(g, MinusRect, MinusText, hover == -1, pressed == -1);
        RetroButton(g, PlusRect, PlusText, hover == 1, pressed == 1);
        var mid = Rectangle.FromLTRB(MinusRect.Right + 3, 4, PlusRect.Left - 3, Height - 4);
        DeckTheme.InsetText(g, mid, ValueText(), DeckTheme.Mono, DeckTheme.Lcd, TextFormatFlags.HorizontalCenter);
    }

    /// <summary>올리브 돋움 버튼 (누르면 오목).</summary>
    internal static void RetroButton(Graphics g, Rectangle r, string text, bool hot, bool down)
    {
        DeckTheme.Bevel(g, r, !down, hot ? DeckTheme.Hover : DeckTheme.Panel, 2);
        var tr = down ? new Rectangle(r.X + 1, r.Y + 1, r.Width, r.Height) : r;
        TextRenderer.DrawText(g, text, DeckTheme.Button, tr, DeckTheme.TextDark, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>채널 한 줄의 표시값.</summary>
internal sealed class ChannelRow
{
    public string Label = "";
    public string Instrument;
    public bool Found;
    public bool Used;
    public int Note = -1;
    public float Level;
    public bool Muted;
    public int Pitch;
    /// <summary>이 채널의 색 (아래 건반에 칠하는 색, 보이스 선택 칸도 이 색).</summary>
    public Color Color = InstTheme.Accent;
}

/// <summary>
/// 채널 믹서 (UUI 배치): 채널마다 가로 한 줄 흰 상자, 가로 전체.
/// [선택 칸 (켜짐 = 채널 색, 울리면 밝게, 끔 = 회색 + OFF)] [번호] [악기 이름 280px (+ 울리는 음, 오른쪽)] [음량 막대 40칸 (남는 넓이 전부)] [− 음높이 +].
/// 줄이 넘치면 세로 스크롤 (휠). 선택 칸 왼쪽 클릭 = 켜기/끄기, 오른쪽 클릭 = 이 채널만 (솔로).
/// </summary>
internal sealed class ChannelMixer : DeckControl
{
    public readonly List<ChannelRow> Rows = new List<ChannelRow>();
    public event Action<int> MuteToggled;
    public event Action<int> SoloRequested;
    public event Action<int, int> PitchStep;
    private const int Pad = 8, RowH = 32, Gap = 3, NameW = 280, Segments = 40;
    private readonly VScrollBar bar = new VScrollBar { Dock = DockStyle.Right, Visible = false, TabStop = false };
    private int scroll;
    private Point hoverPt = new Point(-1, -1);
    private int pressedRow = -1, pressedDir;
    private readonly ToolTip tip = new ToolTip();

    public ChannelMixer()
    {
        tip.SetToolTip(this, "왼쪽 칸: 채널 켜기/끄기 · 오른쪽 클릭: 이 채널만 (솔로) · − +: 채널 음높이(반음)\n칸의 색 = 아래 건반에 칠하는 색 · 줄이 많으면 휠로 넘긴다");
        Controls.Add(bar);
        bar.Scroll += delegate { scroll = bar.Value; Invalidate(); };
    }

    private int ContentHeight { get { return Pad * 2 + Rows.Count * (RowH + Gap); } }
    private int ViewWidth { get { return Width - (bar.Visible ? bar.Width : 0); } }

    private void UpdateScroll()
    {
        bool need = ContentHeight > Height && Height > 0;
        if (bar.Visible != need) bar.Visible = need;
        if (!need) { scroll = 0; return; }
        bar.Minimum = 0;
        bar.Maximum = ContentHeight;
        bar.LargeChange = Math.Max(1, Height);
        bar.SmallChange = RowH + Gap;
        scroll = Math.Max(0, Math.Min(ContentHeight - Height, scroll));
        bar.Value = scroll;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateScroll(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!bar.Visible) return;
        scroll = Math.Max(0, Math.Min(ContentHeight - Height, scroll - Math.Sign(e.Delta) * (RowH + Gap) * 3));
        bar.Value = scroll;
        Invalidate();
    }

    private struct Cols { public Rectangle Box, Select, Label, Inst, Meter, Minus, Pitch, Plus; }

    /// <summary>한 줄 배치. 좁으면 악기 칸을 줄이고, 그래도 모자라면 음량 막대를 뺀다 (Meter 폭 0).</summary>
    private Cols RowLayout(int i)
    {
        var c = new Cols();
        int top = Pad + i * (RowH + Gap) - scroll;
        c.Box = new Rectangle(Pad, top, ViewWidth - Pad * 2, RowH);
        int y = top + 4, h = RowH - 8;
        int right = c.Box.Right - 5;
        c.Plus = new Rectangle(right - 26, y, 26, h); right -= 26 + 2;
        c.Pitch = new Rectangle(right - 52, y, 52, h); right -= 52 + 2;
        c.Minus = new Rectangle(right - 26, y, 26, h); right -= 26 + 8;
        int x = c.Box.X + 5;
        c.Select = new Rectangle(x, y, h, h); x += h + 6;
        c.Label = new Rectangle(x, y, 24, h); x += 26;
        int instW = Math.Max(60, Math.Min(NameW - (x - c.Box.X - 5), right - x - 80));
        c.Inst = new Rectangle(x, y, instW, h); x += instW + 8;
        int meterW = right - x;
        c.Meter = new Rectangle(x, y, meterW >= 40 ? meterW : 0, h);
        return c;
    }

    protected override void OnMouseMove(MouseEventArgs e) { hoverPt = e.Location; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hoverPt = new Point(-1, -1); Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { pressedRow = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        for (int i = 0; i < Rows.Count; i++)
        {
            var c = RowLayout(i);
            if (!c.Box.Contains(e.Location)) continue;
            if (c.Select.Contains(e.Location) || c.Label.Contains(e.Location))
            {
                if (e.Button == MouseButtons.Right) { if (SoloRequested != null) SoloRequested(i); }
                else if (MuteToggled != null) MuteToggled(i);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (c.Minus.Contains(e.Location)) { pressedRow = i; pressedDir = -1; if (PitchStep != null) PitchStep(i, -1); return; }
            if (c.Plus.Contains(e.Location)) { pressedRow = i; pressedDir = 1; if (PitchStep != null) PitchStep(i, 1); return; }
            return;
        }
    }

    private int laidOutRows = -1;

    protected override void OnPaint(PaintEventArgs e)
    {
        if (laidOutRows != Rows.Count) { laidOutRows = Rows.Count; UpdateScroll(); }   // 곡이 바뀌어 채널 수가 달라졌으면
        var g = e.Graphics;
        Prepare(g);
        g.Clear(DeckTheme.Back);
        DeckTheme.Bevel(g, new Rectangle(0, 0, Width, Height), true, DeckTheme.Panel, 2);
        bool anyHot = false;
        var left = TextFormatFlags.Left;
        var center = TextFormatFlags.HorizontalCenter;
        for (int i = 0; i < Rows.Count; i++)
        {
            var c = RowLayout(i);
            if (c.Box.Bottom < 0 || c.Box.Top > Height) continue;
            var row = Rows[i];
            if (c.Select.Contains(hoverPt) || c.Label.Contains(hoverPt) || c.Minus.Contains(hoverPt) || c.Plus.Contains(hoverPt)) anyHot = true;
            bool playing = row.Note >= 0 && !row.Muted;
            DeckTheme.Bevel(g, c.Box, false, DeckTheme.Inset, 1);

            // 선택 칸: 켜짐 = 채널 색 (울리면 밝게, 쉬면 옅게), 끔 = 회색 + OFF
            Color sel = row.Muted ? DeckTheme.MeterOff : playing ? row.Color : Blend(row.Color, DeckTheme.Inset, row.Used ? 0.45f : 0.75f);
            DeckTheme.Bevel(g, c.Select, false, sel, 1);
            if (row.Muted) TextRenderer.DrawText(g, "OFF", DeckTheme.Small, c.Select, DeckTheme.Warn, center | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, row.Label, DeckTheme.Mono, c.Label, row.Used ? DeckTheme.TextDark : DeckTheme.Muted, center | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // 악기 이름 (뱅크에 없으면 빨강 + 취소선, 쓰지 않는 채널은 흐리게) + 오른쪽에 울리는 음
            string inst = string.IsNullOrEmpty(row.Instrument) ? "—" : row.Instrument.ToUpperInvariant();
            Color ic = !row.Used ? DeckTheme.Muted : row.Found ? DeckTheme.Name : DeckTheme.Warn;
            string note = row.Note >= 0 ? Core.NoteHelper.Name(row.Note) : "";
            int noteW = note.Length > 0 ? TextRenderer.MeasureText(note, DeckTheme.Mono).Width + 4 : 0;
            DeckTheme.Bevel(g, c.Inst, false, DeckTheme.Inset, 1);
            var nameR = new Rectangle(c.Inst.X + 4, c.Inst.Y, c.Inst.Width - 8 - noteW, c.Inst.Height);
            TextRenderer.DrawText(g, inst, DeckTheme.Mono, nameR, ic, left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (noteW > 0) TextRenderer.DrawText(g, note, DeckTheme.Mono, new Rectangle(c.Inst.Right - 4 - noteW, c.Inst.Y, noteW, c.Inst.Height), DeckTheme.Lcd, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            if (row.Used && !row.Found && !string.IsNullOrEmpty(row.Instrument))
            {
                int tw = Math.Min(nameR.Width, TextRenderer.MeasureText(inst, DeckTheme.Mono).Width);
                using (var p = new Pen(DeckTheme.Warn)) g.DrawLine(p, nameR.X, nameR.Y + nameR.Height / 2, nameR.X + tw, nameR.Y + nameR.Height / 2);
            }

            if (c.Meter.Width > 0) DrawMeter(g, c.Meter, row.Muted ? 0 : row.Level);

            // 음높이: [−] +00 [+]
            Stepper.RetroButton(g, c.Minus, "−", c.Minus.Contains(hoverPt), pressedRow == i && pressedDir < 0);
            Stepper.RetroButton(g, c.Plus, "+", c.Plus.Contains(hoverPt), pressedRow == i && pressedDir > 0);
            string pt = (row.Pitch > 0 ? "+" : row.Pitch < 0 ? "−" : "+") + Math.Abs(row.Pitch).ToString("00");
            DeckTheme.InsetText(g, c.Pitch, pt, DeckTheme.Mono, row.Pitch != 0 ? DeckTheme.Warn : DeckTheme.Lcd, center);
        }
        Cursor = anyHot ? Cursors.Hand : Cursors.Default;
    }

    private static Color Blend(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>오목 칸 안 40칸 막대: 켜진 칸 = 청록 (끝 20%는 짙게), 꺼진 칸 = 연회색.</summary>
    private static void DrawMeter(Graphics g, Rectangle r, float level)
    {
        DeckTheme.Bevel(g, r, false, DeckTheme.Inset, 1);
        var inner = Rectangle.Inflate(r, -4, -4);
        const int gap = 2;
        double seg = (inner.Width - gap * (Segments - 1)) / (double)Segments;
        if (seg < 1) return;
        int on = (int)Math.Round(Math.Max(0, Math.Min(1, level)) * Segments);
        for (int k = 0; k < Segments; k++)
        {
            int x0 = inner.X + (int)Math.Round(k * (seg + gap)), x1 = inner.X + (int)Math.Round(k * (seg + gap) + seg);
            Color col = k < on ? (k >= Segments * 0.8 ? DeckTheme.MeterHigh : DeckTheme.MeterLow) : DeckTheme.MeterOff;
            using (var b = new SolidBrush(col)) g.FillRectangle(b, x0, inner.Y, Math.Max(1, x1 - x0), inner.Height);
        }
    }
}
