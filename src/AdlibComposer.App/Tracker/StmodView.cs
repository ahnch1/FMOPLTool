using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Tracker;

/// <summary>
/// F4 STMOD: Scream Tracker 2 모듈 플레이어 화면 흉내 (_incoming/scream_tracker_2_player.html 레트로 모양).
/// 검은 바탕에 가로로 흐르는 점 별, 빨간 위 · 아래 띠, 줄무늬 "SCREAM TRACKER" 로고, 초록 기능 목록,
/// 아래 3단 [노란 글 | 빨간 테두리 instrument 상자 | 노란 글].
/// instrument 상자 = 채널마다 한 줄 (초록 ==== 음량 막대 + 흰 악기 이름), 4줄씩 보이고 옆 DOS 풍 스크롤바 · 휠로 넘긴다. 다른 기능은 없다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class StmodView : Control
{
    // DOS 색
    private static readonly Color TopBar = Color.FromArgb(170, 0, 0), Yellow = Color.FromArgb(255, 255, 85), Cyan = Color.FromArgb(85, 255, 255),
        Green = Color.FromArgb(85, 255, 85), Red = Color.FromArgb(255, 85, 85), White = Color.White, Blue = Color.FromArgb(0, 0, 170), DimRed = Color.FromArgb(110, 20, 20);
    private static readonly Color[] StarColors = { Cyan, Red, Color.FromArgb(85, 85, 255), White };
    private const int VisibleRows = 4, BarChars = 16;

    private sealed class Star { public float X, Y, Vx; public int Size; public Color Color; }
    private readonly List<Star> stars = new List<Star>();
    private readonly Random rnd = new Random(2);
    private readonly Timer anim = new Timer { Interval = 33 };
    private readonly Font text = new Font("Consolas", 13f, FontStyle.Bold);
    private readonly Font logoFont = new Font("Consolas", 72f, FontStyle.Bold);

    private TrackerModule module;
    private readonly float[] levels = new float[TrackerModule.MaxChannels];
    private readonly string[] names = new string[TrackerModule.MaxChannels];
    private readonly int[] lastCount = new int[TrackerModule.MaxChannels];
    private int top;   // instrument 상자 맨 위 채널
    private Rectangle upRect, downRect, trackRect, thumbRect;
    private int dragOffset = -1;

    public StmodView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Black;
        anim.Tick += delegate { MoveStars(); Invalidate(); };
        VisibleChanged += delegate { if (Visible) anim.Start(); else anim.Stop(); };
    }

    private int Channels { get { return module == null ? 0 : module.Kind == TrackerKind.Stm ? 4 : Math.Max(1, module.UsedChannelCount); } }
    private int MaxTop { get { return Math.Max(0, Channels - VisibleRows); } }

    /// <summary>곡이 바뀌면: 채널마다 처음 쓰는 악기 이름으로 채운다.</summary>
    public void Bind(TrackerModule m)
    {
        module = m;
        top = 0;
        Array.Clear(levels, 0, levels.Length);
        for (int c = 0; c < names.Length; c++) names[c] = FirstSample(c);
        Invalidate();
    }

    /// <summary>재생 중 (Tracker 타이머): 채널 음량과 지금 울리는 악기.</summary>
    public void Update(AdlibComposer.Audio.VoiceState[] states, bool playing)
    {
        for (int c = 0; c < levels.Length && c < states.Length; c++)
        {
            var s = states[c];
            float target = playing && s.Note >= 0 ? Math.Max(0.1f, s.Volume) : 0f;
            if (playing && s.NoteOnCount != lastCount[c]) { lastCount[c] = s.NoteOnCount; levels[c] = Math.Max(levels[c], target); }
            else levels[c] = Math.Max(target * 0.8f, levels[c] * 0.85f);
            if (playing && !string.IsNullOrEmpty(s.Instrument)) names[c] = s.Instrument;
        }
    }

    /// <summary>정지: 막대를 내린다.</summary>
    public void Silence() { Array.Clear(levels, 0, levels.Length); Invalidate(); }

    private string FirstSample(int c)
    {
        if (module == null || c >= module.Channels) return "";
        foreach (var p in module.Patterns)
            for (int r = 0; r < TrackerPattern.Rows; r++)
            {
                int i = p[r, c].Instrument;
                if (i > 0 && i <= module.Samples.Count)
                {
                    var s = module.Samples[i - 1];
                    string n = (s.FileName ?? "").Trim();
                    return n.Length > 0 ? n : (s.Name ?? "").Trim();
                }
            }
        return "";
    }

    // ---------------------------------------------------------------- 별

    private void MoveStars()
    {
        if (Width <= 0 || Height <= 0) return;
        if (stars.Count == 0)
            for (int i = 0; i < 150; i++)
            {
                float vx = (float)((rnd.NextDouble() - 0.5) * 4);
                if (Math.Abs(vx) < 0.5f) vx = vx < 0 ? -1 : 1;
                stars.Add(new Star { X = (float)rnd.NextDouble() * Width, Y = (float)rnd.NextDouble() * Height, Vx = vx, Size = 1 + rnd.Next(2), Color = StarColors[rnd.Next(StarColors.Length)] });
            }
        foreach (var s in stars)
        {
            s.X += s.Vx;
            if (s.X < 0) s.X = Width;
            if (s.X > Width) s.X = 0;
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); stars.Clear(); }

    // ---------------------------------------------------------------- 그리기

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        foreach (var s in stars)
            using (var b = new SolidBrush(s.Color)) g.FillRectangle(b, (int)s.X, (int)(s.Y * Height / Math.Max(1, Height)), s.Size + 1, s.Size + 1);

        int lineH = text.Height;
        var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        // 위 · 아래 띠
        using (var b = new SolidBrush(TopBar))
        {
            g.FillRectangle(b, 0, 0, Width, lineH + 6);
            g.FillRectangle(b, 0, Height - lineH - 6, Width, lineH + 6);
        }
        TextRenderer.DrawText(g, "Scream Tracker V2.2 - Module Player Routine V1.1 - (C)1990 Sami Tammilehto", text, new Rectangle(0, 3, Width, lineH), Yellow, center);
        TextRenderer.DrawText(g, "Esc- Quit      D-Dos Shell", text, new Point(16, Height - lineH - 3), White, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        // 아래 3단 (높이: instrument 상자 4줄 + 테두리)
        int boxH = lineH * (VisibleRows + 1) + 24;
        int bottomY = Height - lineH - 6 - 16 - boxH;
        int colW = Math.Min(480, (Width - 64) / 3);
        int x0 = (Width - colW * 3 - 48) / 2;
        int midX = x0 + colW + 24, rightX = x0 + 2 * (colW + 24);

        // 가운데 위: Coming soon · 로고 · 기능 · 빨간 글
        int areaTop = lineH + 6, areaBottom = bottomY - 8;
        DrawCenter(g, areaTop, areaBottom, lineH, center);

        // 왼쪽 · 오른쪽 노란 글
        string[] left = { "To get scream Tracker", "when availible,call to:", "- SILICON DRAGON", "+358-(9) 21-500318" };
        string[] right = { "If you want to", "get spectrum", "version,call to:", "SMASH SOFT", "(0312) 68-87-52" };
        for (int i = 0; i < left.Length; i++)
            TextRenderer.DrawText(g, left[i], text, new Point(x0, bottomY + boxH - (left.Length - i) * lineH), Yellow, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        for (int i = 0; i < right.Length; i++)
            TextRenderer.DrawText(g, right[i], text, new Rectangle(rightX, bottomY + boxH - (right.Length - i) * lineH, colW, lineH), Yellow, TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        DrawInstrumentBox(g, new Rectangle(midX, bottomY, colW, boxH), lineH);
    }

    private void DrawCenter(Graphics g, int areaTop, int areaBottom, int lineH, TextFormatFlags center)
    {
        int h = areaBottom - areaTop;
        if (h < lineH * 4) return;
        // 로고 크기: 남는 높이에 맞춘다
        float logoH = Math.Max(lineH * 2, Math.Min(h * 0.38f, 150));
        string[] features = { "Amiga Tracker Compatible", "Supports Sound Blaster and Covox", "Background Playing, even in DOS Shell", "4 Digital channels, even with PC-speaker.", "Tremor, sliding, and all other Amiga Effects" };
        bool showFeatures = h > logoH + lineH * 9;
        int total = lineH + (int)logoH + 16 + (showFeatures ? lineH * (features.Length + 1) + 8 : 0) + lineH + 12;
        int y = areaTop + Math.Max(0, (h - total) / 2);
        TextRenderer.DrawText(g, "Coming soon:", text, new Rectangle(0, y, Width, lineH), Cyan, center);
        y += lineH + 14;
        DrawLogo(g, new RectangleF(0, y, Width, logoH));
        y += (int)logoH + 12;
        if (showFeatures)
        {
            TextRenderer.DrawText(g, "Features :", text, new Rectangle(0, y, Width, lineH), Green, center);
            y += lineH + 4;
            foreach (var f in features) { TextRenderer.DrawText(g, f, text, new Rectangle(0, y, Width, lineH), Green, center); y += lineH; }
            y += 4;
        }
        using (var it = new Font(text, FontStyle.Italic | FontStyle.Bold))
            TextRenderer.DrawText(g, "but this not the case! In fast this is an", it, new Rectangle(0, y + 4, Width, lineH), Red, center);
    }

    /// <summary>"SCREAM TRACKER": 위아래 파랑 · 가운데 하늘-흰-하늘 줄무늬 (ASCII 블록 글자 색), 세로로 늘인다.</summary>
    private void DrawLogo(Graphics g, RectangleF r)
    {
        using (var path = new GraphicsPath())
        {
            path.AddString("SCREAM TRACKER", logoFont.FontFamily, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return;
            float sx = Math.Min((Width - 40) / b.Width, r.Height / b.Height * 1.5f);
            float sy = r.Height / b.Height;
            using (var m = new Matrix())
            {
                m.Translate(-b.X, -b.Y, MatrixOrder.Append);
                m.Scale(sx, sy, MatrixOrder.Append);
                m.Translate((Width - b.Width * sx) / 2, r.Y, MatrixOrder.Append);
                path.Transform(m);
            }
            var bb = path.GetBounds();
            using (var brush = new LinearGradientBrush(new RectangleF(bb.X, bb.Y - 1, bb.Width, bb.Height + 2), Blue, Blue, LinearGradientMode.Vertical))
            {
                brush.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Blue, Blue, Cyan, White, Cyan, Blue, Blue },
                    Positions = new[] { 0f, 0.33f, 0.331f, 0.5f, 0.66f, 0.661f, 1f },
                };
                g.SmoothingMode = SmoothingMode.None;
                g.FillPath(brush, path);
            }
        }
    }

    /// <summary>빨간 테두리 상자 "instrument": 채널 4줄 (초록 ==== 막대 + 흰 이름) + 오른쪽 DOS 스크롤바.</summary>
    private void DrawInstrumentBox(Graphics g, Rectangle r, int lineH)
    {
        int legendY = r.Y + lineH / 2;
        var box = new Rectangle(r.X, legendY, r.Width, r.Height - lineH / 2);
        using (var p = new Pen(Red, 2)) g.DrawRectangle(p, box);
        string legend = "instrument";
        var lsz = TextRenderer.MeasureText(g, legend, text, Size.Empty, TextFormatFlags.NoPadding);
        int lx = r.X + (r.Width - lsz.Width) / 2;
        g.FillRectangle(Brushes.Black, lx - 6, r.Y, lsz.Width + 12, lineH);
        TextRenderer.DrawText(g, legend, text, new Point(lx, r.Y), Red, TextFormatFlags.NoPadding);

        // 스크롤바 (상자 안 오른쪽): ▲ / 트랙 ░ / 손잡이 █ / ▼
        int sbW = Math.Max(14, lineH * 2 / 3);
        int sbX = box.Right - sbW - 6, sbTop = box.Y + lineH / 2 + 4, sbBottom = box.Bottom - 8;
        upRect = new Rectangle(sbX, sbTop, sbW, sbW);
        downRect = new Rectangle(sbX, sbBottom - sbW, sbW, sbW);
        trackRect = new Rectangle(sbX, upRect.Bottom + 2, sbW, downRect.Top - upRect.Bottom - 4);
        DrawArrow(g, upRect, true);
        DrawArrow(g, downRect, false);
        using (var tb = new HatchBrush(HatchStyle.Percent25, DimRed, Color.Black)) g.FillRectangle(tb, trackRect);
        int n = Channels;
        if (n > VisibleRows && trackRect.Height > 4)
        {
            int th = Math.Max(sbW, trackRect.Height * VisibleRows / n);
            int ty = trackRect.Y + (trackRect.Height - th) * top / Math.Max(1, MaxTop);
            thumbRect = new Rectangle(sbX, ty, sbW, th);
        }
        else thumbRect = trackRect;
        using (var b = new SolidBrush(Red)) g.FillRectangle(b, thumbRect);

        // 채널 줄
        int rowX = box.X + 12, rowW = sbX - 10 - rowX;
        int barW = TextRenderer.MeasureText(g, new string('=', BarChars + 1), text, Size.Empty, TextFormatFlags.NoPadding).Width;
        for (int i = 0; i < VisibleRows; i++)
        {
            int c = top + i;
            int y = box.Y + lineH / 2 + 6 + i * lineH;
            if (c >= n) break;
            int on = (int)Math.Round(Math.Max(0, Math.Min(1, levels[c])) * BarChars);
            string bar = new string('=', on) + "-";
            TextRenderer.DrawText(g, bar, text, new Point(rowX, y), Green, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            string name = names[c] ?? "";
            TextRenderer.DrawText(g, name.Length > 0 ? name : "-", text, new Rectangle(rowX + Math.Min(barW, rowW / 2), y, rowW - Math.Min(barW, rowW / 2), lineH), White,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    private static void DrawArrow(Graphics g, Rectangle r, bool up)
    {
        using (var b = new SolidBrush(Red))
        {
            var pts = up
                ? new[] { new Point(r.X + r.Width / 2, r.Y + 2), new Point(r.Right - 2, r.Bottom - 3), new Point(r.X + 2, r.Bottom - 3) }
                : new[] { new Point(r.X + 2, r.Y + 3), new Point(r.Right - 2, r.Y + 3), new Point(r.X + r.Width / 2, r.Bottom - 2) };
            g.FillPolygon(b, pts);
        }
    }

    // ---------------------------------------------------------------- 스크롤 (휠 · 화살표 · 트랙 · 손잡이)

    private void ScrollTo(int t)
    {
        t = Math.Max(0, Math.Min(MaxTop, t));
        if (t != top) { top = t; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Focus(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollTo(top - Math.Sign(e.Delta));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        if (upRect.Contains(e.Location)) ScrollTo(top - 1);
        else if (downRect.Contains(e.Location)) ScrollTo(top + 1);
        else if (thumbRect.Contains(e.Location) && MaxTop > 0) dragOffset = e.Y - thumbRect.Y;
        else if (trackRect.Contains(e.Location)) ScrollTo(e.Y < thumbRect.Y ? top - VisibleRows : top + VisibleRows);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragOffset < 0 || e.Button != MouseButtons.Left) return;
        int span = trackRect.Height - thumbRect.Height;
        if (span <= 0) return;
        ScrollTo((int)Math.Round((double)(e.Y - dragOffset - trackRect.Y) / span * MaxTop));
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragOffset = -1; }

    protected override bool IsInputKey(Keys keyData)
    {
        var k = keyData & Keys.KeyCode;
        return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Up) ScrollTo(top - 1);
        else if (e.KeyCode == Keys.Down) ScrollTo(top + 1);
        else if (e.KeyCode == Keys.PageUp) ScrollTo(top - VisibleRows);
        else if (e.KeyCode == Keys.PageDown) ScrollTo(top + VisibleRows);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { anim.Dispose(); text.Dispose(); logoFont.Dispose(); }
        base.Dispose(disposing);
    }
}
