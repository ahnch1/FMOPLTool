using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using AdlibComposer.App.Shell;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Jukebox;

/// <summary>Jukebox 플레이어 데크 색 (옛 IMPLAY/OCPLAY의 어두운 패널 + LCD 글자를 현대식으로).</summary>
internal static class DeckTheme
{
    public static readonly Color Back = Color.FromArgb(22, 25, 31);
    public static readonly Color Panel = Color.FromArgb(30, 34, 42);
    public static readonly Color PanelLine = Color.FromArgb(48, 54, 66);
    public static readonly Color Text = Color.FromArgb(232, 234, 238);
    public static readonly Color Muted = Color.FromArgb(128, 136, 150);
    public static readonly Color Lcd = Color.FromArgb(94, 226, 208);
    public static readonly Color Lit = Color.FromArgb(255, 196, 64);
    public static readonly Color Warn = Color.FromArgb(240, 110, 96);
    public static readonly Color Ok = Color.FromArgb(120, 210, 120);
    public static readonly Color MeterLow = Color.FromArgb(46, 190, 120);
    public static readonly Color MeterHigh = Color.FromArgb(250, 190, 60);

    public static readonly Font Title = new Font("Malgun Gothic", 15f, FontStyle.Bold);
    public static readonly Font Info = new Font("Malgun Gothic", 9.5f);
    public static readonly Font Time = new Font("Consolas", 22f, FontStyle.Bold);
    public static readonly Font Small = new Font("Segoe UI", 8.5f);
    public static readonly Font Mono = new Font("Consolas", 9.5f);
    public static readonly Font LyricBig = new Font("Malgun Gothic", 20f, FontStyle.Bold);
    public static readonly Font LyricNext = new Font("Malgun Gothic", 13f);

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
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }
}

/// <summary>곡 정보 화면: 제목, 곡 파일(불러온 악기/전체 악기), 뱅크(전체 악기 수), 가사 파일, 시간, 템포/속도/키.</summary>
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
        var card = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = DeckTheme.Round(card, 10))
        using (var b = new LinearGradientBrush(card, DeckTheme.Panel, Color.FromArgb(24, 28, 35), 90f))
        using (var p = new Pen(DeckTheme.PanelLine))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }

        // 오른쪽: 시간 / 템포
        int rightW = 230;
        var rt = new Rectangle(Width - rightW - 16, 12, rightW, Height - 24);
        using (var sfR = new StringFormat { Alignment = StringAlignment.Far })
        using (var lcd = new SolidBrush(DeckTheme.Lcd))
        using (var muted = new SolidBrush(DeckTheme.Muted))
        {
            g.DrawString(State, DeckTheme.Small, muted, new RectangleF(rt.X, rt.Y, rt.Width, 16), sfR);
            g.DrawString(DeckTheme.Clock(Seconds) + " / " + DeckTheme.Clock(TotalSeconds), DeckTheme.Time, lcd, new RectangleF(rt.X - 40, rt.Y + 14, rt.Width + 40, 40), sfR);
            string tempo = "♩ = " + Math.Round(Bpm) + "   속도 " + SpeedPercent + "%   키 " + DeckTheme.KeyText(Key);
            g.DrawString(tempo, DeckTheme.Info, lcd, new RectangleF(rt.X - 60, rt.Y + 58, rt.Width + 60, 20), sfR);
            if (!string.IsNullOrEmpty(Mode))
                g.DrawString(Mode, DeckTheme.Small, muted, new RectangleF(rt.X - 60, rt.Y + 80, rt.Width + 60, 18), sfR);
        }

        // 왼쪽: 제목과 파일 정보
        float x = 18, y = 10, w = Math.Max(50, rt.X - x - 12);
        using (var white = new SolidBrush(DeckTheme.Text))
        using (var muted = new SolidBrush(DeckTheme.Muted))
        using (var ok = new SolidBrush(DeckTheme.Ok))
        using (var warn = new SolidBrush(DeckTheme.Warn))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(Title, DeckTheme.Title, white, new RectangleF(x, y, w, 30), sf);
            y += 36;
            if (!string.IsNullOrEmpty(SongFile))
            {
                Row(g, ref y, x, w, "곡", SongFile, "악기 " + Loaded + "/" + Total, Loaded >= Total ? ok : warn, sf);
                Row(g, ref y, x, w, "뱅크", BankFile, BankCount + "개" + (BankSameName ? " · 같은 이름" : ""), muted, sf);
                Row(g, ref y, x, w, "가사", LyricFile ?? "없음", null, muted, sf);
            }
            else
            {
                g.DrawString("왼쪽 목록에서 ROL / IMS 파일을 두 번 클릭하면 재생합니다.", DeckTheme.Info, muted, new RectangleF(x, y, w, 20), sf);
            }
        }
    }

    private static void Row(Graphics g, ref float y, float x, float w, string label, string value, string tag, Brush tagBrush, StringFormat sf)
    {
        using (var muted = new SolidBrush(DeckTheme.Muted))
        using (var white = new SolidBrush(DeckTheme.Text))
        {
            g.DrawString(label, DeckTheme.Small, muted, x, y + 2);
            float vx = x + 38;
            var sz = g.MeasureString(value, DeckTheme.Mono);
            g.DrawString(value, DeckTheme.Mono, white, new RectangleF(vx, y, Math.Max(10, w - 38 - 110), 18), sf);
            if (tag != null)
                g.DrawString("(" + tag + ")", DeckTheme.Info, tagBrush, Math.Min(vx + sz.Width + 4, x + w - 110), y);
        }
        y += 20;
    }
}

/// <summary>가사 화면: 지금 줄(칠한 칸은 강조색, 카라오케처럼)과 다음 줄.</summary>
internal sealed class LyricsPanel : DeckControl
{
    public IssLyrics Lyrics;
    public LyricState State;
    public string Empty = "가사 파일(.ISS)이 없습니다";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        var card = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = DeckTheme.Round(card, 10))
        using (var b = new SolidBrush(Color.FromArgb(16, 18, 23)))
        using (var p = new Pen(DeckTheme.PanelLine))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        using (var sfc = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        using (var muted = new SolidBrush(DeckTheme.Muted))
        {
            if (Lyrics == null || Lyrics.Lines.Count == 0 || State == null || State.Line < 0)
            {
                g.DrawString(Lyrics == null ? Empty : "♪", DeckTheme.LyricNext, muted, new RectangleF(0, 0, Width, Height), sfc);
                return;
            }
            float mid = Height * 0.40f;
            DrawLine(g, Lyrics.Lines[State.Line], State.Lit, DeckTheme.LyricBig, mid);
            if (State.NextLine >= 0 && State.NextLine < Lyrics.Lines.Count)
                DrawLine(g, Lyrics.Lines[State.NextLine], null, DeckTheme.LyricNext, Height * 0.78f);
        }
    }

    /// <summary>글자마다 너비를 재서 가운데에 놓고, 칠한 칸이면 강조색으로 그린다.</summary>
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
        Color dim = lit == null ? DeckTheme.Muted : Color.FromArgb(200, 204, 212);
        for (int i = first; i <= last; i++)
        {
            var c = line.Cells[i];
            bool on = lit != null && c.Column < lit.Length && lit[c.Column];
            if (c.Text != " ")
                TextRenderer.DrawText(g, c.Text, font, new Point((int)x, (int)(centerY - h / 2)), on ? DeckTheme.Lit : dim, flags);
            x += widths[i] * scale;
        }
    }
}

/// <summary>재생 진행 막대: 클릭/끌기로 위치 이동.</summary>
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

    private RectangleF Track { get { return new RectangleF(8, Height / 2f - 3, Width - 16, 6); } }

    private double FromX(int x) { var t = Track; return t.Width <= 0 ? 0 : Math.Max(0, Math.Min(1, (x - t.X) / t.Width)); }

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
        var t = Track;
        double v = dragging ? dragValue : value;
        using (var path = DeckTheme.Round(t, 3))
        using (var b = new SolidBrush(Color.FromArgb(52, 58, 70)))
            g.FillPath(b, path);
        var fill = new RectangleF(t.X, t.Y, (float)(t.Width * v), t.Height);
        if (fill.Width > 1)
            using (var path = DeckTheme.Round(fill, 3))
            using (var b = new SolidBrush(Enabled ? ShellTheme.Accent : DeckTheme.Muted))
                g.FillPath(b, path);
        float cx = t.X + (float)(t.Width * v), cy = Height / 2f, r = dragging ? 8 : 6;
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        using (var p = new Pen(ShellTheme.Accent, 2)) g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
    }
}

/// <summary>"제목  [−] 값 [+]" 작은 조절기 (속도 / 키 / 볼륨). 마우스 휠도 된다.</summary>
internal sealed class Stepper : DeckControl
{
    public string Caption = "";
    public string MinusText = "−", PlusText = "+";
    public Func<string> ValueText = () => "";
    public event Action<int> Step;
    private int hover = 0;   // -1 빼기, 1 더하기

    public Stepper() { Height = 34; Width = 190; Cursor = Cursors.Hand; }

    private Rectangle MinusRect { get { int bw = Math.Max(30, TextWidth(MinusText) + 16); return new Rectangle(CaptionWidth, 3, bw, Height - 6); } }
    private Rectangle PlusRect { get { int bw = Math.Max(30, TextWidth(PlusText) + 16); return new Rectangle(Width - bw - 1, 3, bw, Height - 6); } }
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
        if (d != 0 && Step != null) Step(d);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Step != null) Step(e.Delta > 0 ? 1 : -1);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        if (CaptionWidth > 0)
            TextRenderer.DrawText(g, Caption, DeckTheme.Info, new Rectangle(0, 0, CaptionWidth, Height), DeckTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        DrawButton(g, MinusRect, MinusText, hover == -1);
        DrawButton(g, PlusRect, PlusText, hover == 1);
        var mid = Rectangle.FromLTRB(MinusRect.Right, 0, PlusRect.Left, Height);
        TextRenderer.DrawText(g, ValueText(), DeckTheme.Mono, mid, DeckTheme.Lcd, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }

    private static void DrawButton(Graphics g, Rectangle r, string text, bool hot)
    {
        using (var path = DeckTheme.Round(r, 6))
        using (var b = new SolidBrush(hot ? Color.FromArgb(62, 70, 86) : Color.FromArgb(42, 47, 58)))
        using (var p = new Pen(DeckTheme.PanelLine))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        TextRenderer.DrawText(g, text, DeckTheme.Info, r, DeckTheme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
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
}

/// <summary>
/// 채널 믹서: 채널마다 [연주 중 표시등] 번호 악기(뱅크에 없으면 빨강) 지금 음 [음량 막대] [켜기/끄기] [− 음높이 +].
/// 켜기/끄기 버튼을 오른쪽 클릭하면 그 채널만 켜기(솔로).
/// </summary>
internal sealed class ChannelMixer : DeckControl
{
    public readonly List<ChannelRow> Rows = new List<ChannelRow>();
    public event Action<int> MuteToggled;
    public event Action<int> SoloRequested;
    public event Action<int, int> PitchStep;
    private const int HeaderH = 26;
    private const int MinRowH = 22;
    /// <summary>한 열이면 자리가 모자랄 때 두 열로 나눈다 (11채널이 다 보이게).</summary>
    private int ColumnCount { get { return Rows.Count > 1 && (Height - HeaderH - 10) / Rows.Count < MinRowH && Width >= 640 ? 2 : 1; } }
    private int RowsPerColumn { get { return (Rows.Count + ColumnCount - 1) / ColumnCount; } }
    /// <summary>줄 높이: 자리에 맞춰 22~30 (그래도 모자라면 18까지).</summary>
    private int RowH { get { return Rows.Count == 0 ? 30 : Math.Max(18, Math.Min(30, (Height - HeaderH - 10) / RowsPerColumn)); } }
    private int ColumnWidth { get { return (Width - 8) / ColumnCount; } }
    private Point hoverPt = new Point(-1, -1);
    private readonly ToolTip tip = new ToolTip();

    public ChannelMixer()
    {
        tip.SetToolTip(this, "켜기/끄기: 채널 소리 · 오른쪽 클릭: 이 채널만 (솔로) · − +: 채널 음높이(반음)");
    }

    private int RowTop(int i) { return HeaderH + 4 + (i % RowsPerColumn) * RowH; }
    private int ColumnLeft(int i) { return 4 + (i / RowsPerColumn) * ColumnWidth; }

    private struct Cols { public Rectangle Led, Label, Inst, Note, Meter, Toggle, Minus, Pitch, Plus; }

    private Cols RowLayout(int i)
    {
        int y = RowTop(i), h = RowH - (RowH >= 26 ? 6 : 3);
        var c = new Cols();
        bool narrow = ColumnCount > 1;
        int x = ColumnLeft(i) + 10;
        c.Led = new Rectangle(x, y + h / 2 - 5, 10, 10); x += 18;
        c.Label = new Rectangle(x, y, 30, h); x += 32;
        c.Inst = new Rectangle(x, y, narrow ? 82 : 120, h); x += narrow ? 84 : 124;
        c.Note = new Rectangle(x, y, narrow ? 36 : 40, h); x += narrow ? 38 : 44;
        int right = ColumnLeft(i) + ColumnWidth - 10;
        c.Plus = new Rectangle(right - 24, y, 24, h); right -= 24;
        int pw = narrow ? 32 : 40;
        c.Pitch = new Rectangle(right - pw, y, pw, h); right -= pw;
        c.Minus = new Rectangle(right - 24, y, 24, h); right -= 24 + (narrow ? 6 : 12);
        c.Toggle = new Rectangle(right - 48, y, 48, h); right -= 48 + (narrow ? 6 : 12);
        c.Meter = new Rectangle(x, y + h / 2 - 5, Math.Max(20, right - x), 10);
        return c;
    }

    protected override void OnMouseMove(MouseEventArgs e) { hoverPt = e.Location; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hoverPt = new Point(-1, -1); Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            var c = RowLayout(i);
            if (c.Toggle.Contains(e.Location) || c.Led.Contains(e.Location) || c.Label.Contains(e.Location))
            {
                if (e.Button == MouseButtons.Right) { if (SoloRequested != null) SoloRequested(i); }
                else if (MuteToggled != null) MuteToggled(i);
                return;
            }
            if (e.Button != MouseButtons.Left) continue;
            if (c.Minus.Contains(e.Location)) { if (PitchStep != null) PitchStep(i, -1); return; }
            if (c.Plus.Contains(e.Location)) { if (PitchStep != null) PitchStep(i, 1); return; }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        var card = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = DeckTheme.Round(card, 10))
        using (var b = new SolidBrush(DeckTheme.Panel))
        using (var p = new Pen(DeckTheme.PanelLine))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        var hdr = TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
        for (int col = 0; col < ColumnCount && Rows.Count > 0; col++)
        {
            var c0 = RowLayout(col * RowsPerColumn);
            if (col > 0)
                using (var p = new Pen(DeckTheme.PanelLine)) g.DrawLine(p, ColumnLeft(col * RowsPerColumn), 8, ColumnLeft(col * RowsPerColumn), Height - 8);
            TextRenderer.DrawText(g, "채널", DeckTheme.Small, new Rectangle(c0.Label.X, 2, 40, HeaderH), DeckTheme.Muted, hdr);
            TextRenderer.DrawText(g, "악기", DeckTheme.Small, new Rectangle(c0.Inst.X, 2, 60, HeaderH), DeckTheme.Muted, hdr);
            TextRenderer.DrawText(g, "음", DeckTheme.Small, new Rectangle(c0.Note.X, 2, 40, HeaderH), DeckTheme.Muted, hdr);
            TextRenderer.DrawText(g, "음량", DeckTheme.Small, new Rectangle(c0.Meter.X, 2, 60, HeaderH), DeckTheme.Muted, hdr);
            TextRenderer.DrawText(g, "소리", DeckTheme.Small, new Rectangle(c0.Toggle.X, 2, 52, HeaderH), DeckTheme.Muted, hdr | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "음높이", DeckTheme.Small, new Rectangle(c0.Minus.X, 2, c0.Plus.Right - c0.Minus.X, HeaderH), DeckTheme.Muted, hdr | TextFormatFlags.HorizontalCenter);
        }
        bool anyHot = false;
        for (int i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            var c = RowLayout(i);
            if (c.Toggle.Contains(hoverPt) || c.Minus.Contains(hoverPt) || c.Plus.Contains(hoverPt) || c.Led.Contains(hoverPt) || c.Label.Contains(hoverPt)) anyHot = true;
            bool playing = row.Note >= 0 && !row.Muted;
            // 줄 바탕
            if ((i % RowsPerColumn) % 2 == 1)
                using (var b = new SolidBrush(Color.FromArgb(34, 39, 48)))
                    g.FillRectangle(b, ColumnLeft(i) + 2, RowTop(i) - 2, ColumnWidth - 4, RowH);
            // 표시등
            using (var b = new SolidBrush(playing ? DeckTheme.Ok : Color.FromArgb(58, 64, 76))) g.FillEllipse(b, c.Led);
            if (playing) using (var p = new Pen(Color.FromArgb(90, DeckTheme.Ok), 3)) g.DrawEllipse(p, Rectangle.Inflate(c.Led, 2, 2));
            var left = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, row.Label, DeckTheme.Mono, c.Label, row.Used ? DeckTheme.Text : DeckTheme.Muted, left);
            // 악기 (없음 = 빨강 + 취소선, 쓰지 않는 채널 = 흐리게)
            string inst = string.IsNullOrEmpty(row.Instrument) ? "—" : row.Instrument;
            Color ic = !row.Used ? DeckTheme.Muted : row.Found ? DeckTheme.Text : DeckTheme.Warn;
            TextRenderer.DrawText(g, inst, DeckTheme.Mono, c.Inst, ic, left);
            if (row.Used && !row.Found && !string.IsNullOrEmpty(row.Instrument))
            {
                int tw = Math.Min(c.Inst.Width, TextRenderer.MeasureText(inst, DeckTheme.Mono).Width);
                using (var p = new Pen(DeckTheme.Warn)) g.DrawLine(p, c.Inst.X + 3, c.Inst.Y + c.Inst.Height / 2, c.Inst.X + tw - 3, c.Inst.Y + c.Inst.Height / 2);
            }
            TextRenderer.DrawText(g, row.Note >= 0 ? Core.NoteHelper.Name(row.Note) : "", DeckTheme.Mono, c.Note, DeckTheme.Lcd, left);
            // 음량 막대 (칸 나눔)
            DrawMeter(g, c.Meter, row.Muted ? 0 : row.Level);
            // 켜기/끄기
            bool hot = c.Toggle.Contains(hoverPt);
            using (var path = DeckTheme.Round(c.Toggle, c.Toggle.Height / 2f))
            using (var b = new SolidBrush(row.Muted ? Color.FromArgb(hot ? 80 : 64, 44, 48) : hot ? ShellTheme.Accent : ShellTheme.AccentDark))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, row.Muted ? "OFF" : "ON", DeckTheme.Small, c.Toggle, row.Muted ? DeckTheme.Warn : Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            // 음높이
            SmallButton(g, c.Minus, "−", c.Minus.Contains(hoverPt));
            SmallButton(g, c.Plus, "+", c.Plus.Contains(hoverPt));
            string pt = (row.Pitch > 0 ? "+" : row.Pitch < 0 ? "−" : "±") + Math.Abs(row.Pitch).ToString("00");
            TextRenderer.DrawText(g, pt, DeckTheme.Mono, c.Pitch, row.Pitch != 0 ? DeckTheme.Lit : DeckTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }
        Cursor = anyHot ? Cursors.Hand : Cursors.Default;
    }

    private static void SmallButton(Graphics g, Rectangle r, string t, bool hot)
    {
        using (var path = DeckTheme.Round(r, 5))
        using (var b = new SolidBrush(hot ? Color.FromArgb(62, 70, 86) : Color.FromArgb(42, 47, 58)))
            g.FillPath(b, path);
        TextRenderer.DrawText(g, t, DeckTheme.Info, r, DeckTheme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }

    private static void DrawMeter(Graphics g, Rectangle r, float level)
    {
        const int seg = 6, gap = 2;
        int n = Math.Max(1, (r.Width + gap) / (seg + gap));
        int on = (int)Math.Round(Math.Max(0, Math.Min(1, level)) * n);
        for (int k = 0; k < n; k++)
        {
            var sr = new Rectangle(r.X + k * (seg + gap), r.Y, seg, r.Height);
            Color col = k < on ? (k > n * 0.8 ? DeckTheme.MeterHigh : DeckTheme.MeterLow) : Color.FromArgb(44, 50, 60);
            using (var b = new SolidBrush(col)) g.FillRectangle(b, sr);
        }
    }

    /// <summary>줄 수에 맞는 높이.</summary>
    public int PreferredRowsHeight { get { return HeaderH + 12 + Rows.Count * RowH; } }
}
