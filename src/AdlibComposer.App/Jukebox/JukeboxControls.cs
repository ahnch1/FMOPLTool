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
/// 곡 정보 상자 (올리브 돋움): 왼쪽 = 검은 칸 줄 (제목 · 곡 파일 + 악기 수 · 뱅크 + 악기 수 · 가사 파일),
/// 오른쪽 = 검은 칸에 시간 / 곡 전체, 템포 · 속도 · 키, 상태 · 모드.
/// </summary>
internal sealed class PlayerDisplay : DeckControl
{
    public string Title = "곡을 고르세요";
    public string SongFile = "";
    public int Loaded, Total;
    public string BankFile = "";
    public int BankCount;
    public bool BankSameName;
    public string LyricFile;
    public string State = "정지";
    public double Seconds, TotalSeconds;
    public double Bpm;
    public int SpeedPercent = 100;
    public int Key;
    public string Mode = "";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(DeckTheme.Back);
        DeckTheme.Bevel(g, new Rectangle(0, 0, Width, Height), true, DeckTheme.Panel, 2);

        const int pad = 8, gap = 4;
        int rowH = Math.Max(18, Math.Min(26, (Height - pad * 2 - gap * 3) / 4));
        int rightW = Math.Min(250, Math.Max(170, Width / 3));
        var rt = new Rectangle(Width - pad - rightW, pad, rightW, Height - pad * 2);
        int lw = Math.Max(60, rt.X - pad - 6);
        var left = TextFormatFlags.Left;
        var center = TextFormatFlags.HorizontalCenter;

        // 왼쪽: 제목 (초록) · 곡 · 뱅크 · 가사
        int y = pad;
        DeckTheme.InsetText(g, new Rectangle(pad, y, lw, rowH), Title, DeckTheme.Info, DeckTheme.Lit, center);
        y += rowH + gap;
        if (!string.IsNullOrEmpty(SongFile))
        {
            TagRow(g, new Rectangle(pad, y, lw, rowH), SongFile, "악기 " + Loaded + "/" + Total, Loaded >= Total ? DeckTheme.Lcd : DeckTheme.Warn);
            y += rowH + gap;
            TagRow(g, new Rectangle(pad, y, lw, rowH), BankFile, BankCount + "개" + (BankSameName ? " · 같은 이름" : ""), DeckTheme.Text);
            y += rowH + gap;
            DeckTheme.InsetText(g, new Rectangle(pad, y, lw, rowH), "가사  " + (LyricFile ?? "없음"), DeckTheme.Mono, LyricFile != null ? DeckTheme.Text : DeckTheme.Muted, left);
        }
        else
            DeckTheme.InsetText(g, new Rectangle(pad, y, lw, rowH), "왼쪽 목록에서 곡을 두 번 클릭하면 재생합니다", DeckTheme.Mono, DeckTheme.Muted, left);

        // 오른쪽: 시간 · 템포 · 상태
        DeckTheme.Bevel(g, rt, false, DeckTheme.Inset, 1);
        var inner = Rectangle.Inflate(rt, -6, -4);
        int third = inner.Height / 3;
        TextRenderer.DrawText(g, DeckTheme.Clock(Seconds) + " / " + DeckTheme.Clock(TotalSeconds), DeckTheme.Time,
            new Rectangle(inner.X, inner.Y, inner.Width, third + 6), DeckTheme.Lcd, DeckTheme.Inset, center | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "♩=" + Math.Round(Bpm) + "  " + SpeedPercent + "%  키 " + DeckTheme.KeyText(Key), DeckTheme.Mono,
            new Rectangle(inner.X, inner.Y + third + 4, inner.Width, third - 4), DeckTheme.Text, DeckTheme.Inset, center | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, State + (string.IsNullOrEmpty(Mode) ? "" : "  " + Mode), DeckTheme.Small,
            new Rectangle(inner.X, inner.Y + third * 2, inner.Width, third), DeckTheme.Muted, DeckTheme.Inset, center | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(DeckTheme.Back);
        DeckTheme.Bevel(g, new Rectangle(0, 0, Width, Height), false, DeckTheme.InsetLight, 2);
        if (Lyrics == null || Lyrics.Lines.Count == 0 || State == null || State.Line < 0)
        {
            TextRenderer.DrawText(g, Lyrics == null ? Empty : "♪", DeckTheme.LyricNext, new Rectangle(0, 0, Width, Height), DeckTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        DrawLine(g, Lyrics.Lines[State.Line], State.Lit, DeckTheme.LyricBig, Height * 0.40f);
        if (State.NextLine >= 0 && State.NextLine < Lyrics.Lines.Count)
            DrawLine(g, Lyrics.Lines[State.NextLine], null, DeckTheme.LyricNext, Height * 0.78f);
    }

    /// <summary>글자마다 너비를 재서 가운데에 놓고, 부른 칸이면 초록으로 그린다.</summary>
    private void DrawLine(Graphics g, IssLine line, bool[] lit, Font font, float centerY)
    {
        // 앞뒤 공백은 빼고 가운데 맞춤 (원본 두 번째 화면 방식)
        int first = 0, last = line.Cells.Count - 1;
        while (first <= last && line.Cells[first].Text.Trim().Length == 0) first++;
        while (last >= first && line.Cells[last].Text.Trim().Length == 0) last--;
        if (first > last) return;
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
        float scale = total > Width - 24 ? (Width - 24) / total : 1f;
        float x = (Width - total * scale) / 2;
        int h = font.Height;
        Color dim = lit == null ? DeckTheme.Muted : DeckTheme.TextDark;
        for (int i = first; i <= last; i++)
        {
            var c = line.Cells[i];
            bool on = lit != null && c.Column < lit.Length && lit[c.Column];
            if (c.Text != " ")
                TextRenderer.DrawText(g, c.Text, font, new Point((int)x, (int)(centerY - h / 2)), on ? DeckTheme.LitOnLight : dim, flags);
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
/// 채널 믹서 (올리브 돋움 상자, 옛 DOS 플레이어의 두 믹서를 한 줄에 합침):
/// [보이스 선택 칸 (켜짐 = 채널 색, 울리면 밝게, 끔 = 어두운 칸에 ×)] [채널] [악기 이름 (검은 칸, 빨강)] [음] [음량 막대 (검은 칸 · 초록 칸, 끝은 하늘색)] [− 음높이 +].
/// 선택 칸 왼쪽 클릭 = 켜기/끄기, 오른쪽 클릭 = 이 채널만 (솔로).
/// </summary>
internal sealed class ChannelMixer : DeckControl
{
    public readonly List<ChannelRow> Rows = new List<ChannelRow>();
    public event Action<int> MuteToggled;
    public event Action<int> SoloRequested;
    public event Action<int, int> PitchStep;
    private const int Pad = 8;
    private const int MinRowH = 20;
    /// <summary>한 열이면 자리가 모자랄 때 두 열로 나눈다.</summary>
    private int ColumnCount
    {
        get
        {
            int fit = Math.Max(1, (Height - Pad * 2) / MinRowH);
            if (Rows.Count <= 1 || Rows.Count <= fit || Width < 560) return 1;
            return Rows.Count > fit * 2 && Width >= 960 ? 3 : 2;   // S3M 32채널은 세 열까지
        }
    }
    private int RowsPerColumn { get { return (Rows.Count + ColumnCount - 1) / ColumnCount; } }
    /// <summary>줄 높이: 자리에 맞춰 14~26.</summary>
    private int RowH { get { return Rows.Count == 0 ? 26 : Math.Max(14, Math.Min(26, (Height - Pad * 2) / RowsPerColumn)); } }
    private int ColumnWidth { get { return (Width - Pad * 2) / ColumnCount; } }
    private Point hoverPt = new Point(-1, -1);
    private int pressedRow = -1, pressedDir;
    private readonly ToolTip tip = new ToolTip();

    public ChannelMixer()
    {
        tip.SetToolTip(this, "왼쪽 칸: 채널 켜기/끄기 · 오른쪽 클릭: 이 채널만 (솔로) · − +: 채널 음높이(반음)\n칸의 색 = 아래 건반에 칠하는 색");
    }

    private int RowTop(int i) { return Pad + (i % RowsPerColumn) * RowH; }
    private int ColumnLeft(int i) { return Pad + (i / RowsPerColumn) * ColumnWidth; }

    private struct Cols { public Rectangle Select, Label, Inst, Note, Meter, Minus, Pitch, Plus; }

    /// <summary>한 줄 배치. 좁으면 악기 칸을 줄이고, 그래도 모자라면 음량 막대를 뺀다 (Meter 폭 0).</summary>
    private Cols RowLayout(int i)
    {
        int y = RowTop(i) + 1, h = RowH - 3;
        var c = new Cols();
        int colLeft = ColumnLeft(i), colW = ColumnWidth;
        bool narrow = ColumnCount > 1 || colW < 420;
        int right = colLeft + colW - (ColumnCount > 1 ? 8 : 0);
        c.Plus = new Rectangle(right - 20, y, 20, h); right -= 20 + 1;
        int pw = 38;
        c.Pitch = new Rectangle(right - pw, y, pw, h); right -= pw + 1;
        c.Minus = new Rectangle(right - 20, y, 20, h); right -= 20 + 4;
        int x = colLeft;
        c.Select = new Rectangle(x, y, h + 2, h); x += h + 6;
        c.Label = new Rectangle(x, y, 28, h); x += 30;
        int noteW = narrow ? 36 : 40;
        int instW = Math.Max(40, Math.Min(narrow ? 92 : 120, right - x - noteW - 6 - 30));
        c.Inst = new Rectangle(x, y, instW, h); x += instW + 3;
        c.Note = new Rectangle(x, y, noteW, h); x += noteW + 3;
        int meterW = right - x;
        c.Meter = new Rectangle(x, y, meterW >= 24 ? meterW : 0, h);
        return c;
    }

    protected override void OnMouseMove(MouseEventArgs e) { hoverPt = e.Location; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hoverPt = new Point(-1, -1); Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { pressedRow = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            var c = RowLayout(i);
            if (c.Select.Contains(e.Location) || c.Label.Contains(e.Location))
            {
                if (e.Button == MouseButtons.Right) { if (SoloRequested != null) SoloRequested(i); }
                else if (MuteToggled != null) MuteToggled(i);
                return;
            }
            if (e.Button != MouseButtons.Left) continue;
            if (c.Minus.Contains(e.Location)) { pressedRow = i; pressedDir = -1; if (PitchStep != null) PitchStep(i, -1); return; }
            if (c.Plus.Contains(e.Location)) { pressedRow = i; pressedDir = 1; if (PitchStep != null) PitchStep(i, 1); return; }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(DeckTheme.Back);
        DeckTheme.Bevel(g, new Rectangle(0, 0, Width, Height), true, DeckTheme.Panel, 2);
        for (int col = 1; col < ColumnCount && Rows.Count > 0; col++)
        {
            int x = ColumnLeft(col * RowsPerColumn) - 5;
            using (var pd = new Pen(DeckTheme.PanelDark)) g.DrawLine(pd, x, Pad, x, Height - Pad);
            using (var pl = new Pen(DeckTheme.PanelLight)) g.DrawLine(pl, x + 1, Pad, x + 1, Height - Pad);
        }
        bool anyHot = false;
        var left = TextFormatFlags.Left;
        var center = TextFormatFlags.HorizontalCenter;
        for (int i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            var c = RowLayout(i);
            if (c.Select.Contains(hoverPt) || c.Label.Contains(hoverPt) || c.Minus.Contains(hoverPt) || c.Plus.Contains(hoverPt)) anyHot = true;
            bool playing = row.Note >= 0 && !row.Muted;

            // 보이스 선택 칸: 켜짐 = 채널 색 (울리면 밝게, 쉬면 어둡게), 끔 = 어두운 회색 + ×
            Color sel = row.Muted ? DeckTheme.MeterOff : playing ? row.Color : Blend(row.Color, DeckTheme.Inset, row.Used ? 0.55f : 0.8f);
            DeckTheme.Bevel(g, c.Select, !row.Muted, sel, 2);
            if (row.Muted)
                using (var p = new Pen(DeckTheme.Warn, 2))
                {
                    var r = Rectangle.Inflate(c.Select, -5, -5);
                    g.DrawLine(p, r.Left, r.Top, r.Right, r.Bottom);
                    g.DrawLine(p, r.Right, r.Top, r.Left, r.Bottom);
                }
            TextRenderer.DrawText(g, row.Label, DeckTheme.Mono, c.Label, row.Used ? DeckTheme.TextDark : DeckTheme.Muted, left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // 악기 이름: 검은 칸에 빨강 (뱅크에 없으면 노랑 + 취소선, 쓰지 않는 채널은 흐리게)
            string inst = string.IsNullOrEmpty(row.Instrument) ? "—" : row.Instrument.ToUpperInvariant();
            Color ic = !row.Used ? DeckTheme.Muted : row.Found ? DeckTheme.Name : DeckTheme.Warn;
            DeckTheme.InsetText(g, c.Inst, inst, DeckTheme.Mono, ic, left);
            if (row.Used && !row.Found && !string.IsNullOrEmpty(row.Instrument))
            {
                int tw = Math.Min(c.Inst.Width - 6, TextRenderer.MeasureText(inst, DeckTheme.Mono).Width);
                using (var p = new Pen(DeckTheme.Warn)) g.DrawLine(p, c.Inst.X + 4, c.Inst.Y + c.Inst.Height / 2, c.Inst.X + 4 + tw, c.Inst.Y + c.Inst.Height / 2);
            }
            DeckTheme.InsetText(g, c.Note, row.Note >= 0 ? Core.NoteHelper.Name(row.Note) : "", DeckTheme.Mono, DeckTheme.Lcd, center);

            // 음량: 검은 칸 안 칸 나눈 막대
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

    /// <summary>검은 오목 칸 안에 세로 칸 막대: 아래쪽 80%는 초록, 끝은 하늘색, 꺼진 칸은 어두운 초록.</summary>
    private static void DrawMeter(Graphics g, Rectangle r, float level)
    {
        DeckTheme.Bevel(g, r, false, DeckTheme.Inset, 1);
        var inner = Rectangle.Inflate(r, -3, -3);
        const int seg = 4, gap = 2;
        int n = Math.Max(1, (inner.Width + gap) / (seg + gap));
        int on = (int)Math.Round(Math.Max(0, Math.Min(1, level)) * n);
        for (int k = 0; k < n; k++)
        {
            var sr = new Rectangle(inner.X + k * (seg + gap), inner.Y, seg, inner.Height);
            Color col = k < on ? (k >= n * 0.8 ? DeckTheme.MeterHigh : DeckTheme.MeterLow) : DeckTheme.MeterOff;
            using (var b = new SolidBrush(col)) g.FillRectangle(b, sr);
        }
    }

    /// <summary>줄 수에 맞는 높이.</summary>
    public int PreferredRowsHeight { get { return Pad * 2 + Rows.Count * RowH; } }
}
