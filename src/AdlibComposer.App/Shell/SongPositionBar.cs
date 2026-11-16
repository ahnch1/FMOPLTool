using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AdlibComposer.App.Shell;

/// <summary>
/// 곡 전체 길이 막대 (Note · Orgel Note 맨 위): 왼쪽 = 지금 위치 시간, 가운데 = 막대(지금 위치까지 채움 + 손잡이), 오른쪽 = 곡 전체 시간.
/// 막대를 누르거나 끌어 놓으면 그 지점의 틱으로 <see cref="SeekRequested"/> (페이지가 그 지점부터 재생). 끄는 동안은 왼쪽 시간이 끄는 곳을 보인다.
/// 단위는 틱, 시간은 <see cref="SecondsAt"/> (Note = 곡의 템포, Orgel = 돌리는 빠르기).
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class SongPositionBar : Control
{
    private const int LabelWidth = 64;
    private double length, position, dragTick;
    private bool dragging, hover;

    /// <summary>막대를 놓은 곳 (틱): 그 지점부터 재생.</summary>
    public event Action<double> SeekRequested;

    /// <summary>틱 → 초.</summary>
    public Func<double, double> SecondsAt = t => 0;

    public SongPositionBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 34;
        BackColor = Color.White;
        Font = ShellTheme.Ui;
        Cursor = Cursors.Hand;
    }

    /// <summary>곡 전체 길이 (틱). 0이면 막대를 쓸 수 없다.</summary>
    public double LengthTicks
    {
        get { return length; }
        set { length = Math.Max(0, value); Invalidate(); }
    }

    /// <summary>지금 위치 (틱): 재생 중이면 재생 위치, 아니면 현재 위치(재생 시작점).</summary>
    public double Position
    {
        get { return position; }
        set
        {
            double v = Math.Max(0, Math.Min(length, value));
            if (Math.Abs(v - position) < 1e-6) return;
            position = v;
            if (!dragging) Invalidate();
        }
    }

    private Rectangle Track { get { return new Rectangle(LabelWidth + 8, Height / 2 - 4, Math.Max(10, Width - 2 * LabelWidth - 16), 8); } }

    private double TickAtX(int x)
    {
        var t = Track;
        double f = Math.Max(0, Math.Min(1, (x - t.X) / (double)Math.Max(1, t.Width)));
        return f * length;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || length <= 0) return;
        dragging = true;
        Capture = true;
        dragTick = TickAtX(e.X);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging) return;
        dragTick = TickAtX(e.X);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!dragging) return;
        dragging = false;
        Capture = false;
        position = TickAtX(e.X);
        Invalidate();
        if (SeekRequested != null) SeekRequested(position);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

    private static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        int s = (int)Math.Floor(seconds);
        return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using (var line = new Pen(ShellTheme.HeaderLine)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        double at = dragging ? dragTick : position;
        bool usable = length > 0;
        var left = new Rectangle(0, 0, LabelWidth, Height);
        var right = new Rectangle(Width - LabelWidth, 0, LabelWidth, Height);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, usable ? Clock(SecondsAt(at)) : "--:--", Font, left, dragging ? ShellTheme.AccentDark : Color.FromArgb(40, 44, 52), flags);
        TextRenderer.DrawText(g, usable ? Clock(SecondsAt(length)) : "--:--", Font, right, ShellTheme.Muted, flags);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var t = Track;
        using (var back = new SolidBrush(Color.FromArgb(226, 229, 234))) FillRound(g, back, t);
        if (!usable) return;
        float fx = t.X + (float)(t.Width * Math.Max(0, Math.Min(1, at / length)));
        if (fx > t.X + 1)
            using (var fill = new SolidBrush(ShellTheme.Accent)) FillRound(g, fill, new RectangleF(t.X, t.Y, fx - t.X, t.Height));
        float r = dragging || hover ? 8 : 6;
        using (var knob = new SolidBrush(dragging ? ShellTheme.AccentDark : Color.White))
        using (var edge = new Pen(ShellTheme.AccentDark, 2))
        {
            var k = new RectangleF(fx - r, t.Y + t.Height / 2f - r, 2 * r, 2 * r);
            g.FillEllipse(knob, k);
            g.DrawEllipse(edge, k);
        }
    }

    private static void FillRound(Graphics g, Brush b, RectangleF r)
    {
        float d = Math.Min(r.Height, r.Width);
        if (d <= 0) return;
        using (var p = new GraphicsPath())
        {
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure();
            g.FillPath(b, p);
        }
    }
}
