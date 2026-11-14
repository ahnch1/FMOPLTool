using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AdlibComposer.App.Controls;

/// <summary>Instrument Maker의 슬라이더(◁ ▒▒■▒▒ ▷ + 숫자). ShowWaveGlyph면 숫자 대신 파형 그림을 그린다.</summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class RetroSlider : Control
{
    private const int ButtonW = 16;
    private const int ValueW = 40;
    private const int ThumbW = 26;

    private int min, max = 15, value;
    private bool dragging;

    public event EventHandler ValueChanged;
    public bool ShowWaveGlyph { get; set; }

    public RetroSlider()
    {
        SetStyle(ControlStyles.Selectable, true);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = true;
        Height = 20;
        BackColor = InstTheme.Back;
    }

    public int Minimum { get { return min; } set { min = value; Invalidate(); } }
    public int Maximum { get { return max; } set { max = value; Invalidate(); } }

    public int Value
    {
        get { return value; }
        set
        {
            int v = value < min ? min : (value > max ? max : value);
            if (v == this.value) return;
            this.value = v;
            Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
    }

    /// <summary>이벤트 없이 값만 바꾼다 (화면 갱신용).</summary>
    public void SetValueSilently(int v)
    {
        this.value = v < min ? min : (v > max ? max : v);
        Invalidate();
    }

    private Rectangle LeftButton { get { return new Rectangle(0, 0, ButtonW, Height - 1); } }
    private Rectangle TrackRect { get { return new Rectangle(ButtonW, 0, Math.Max(10, Width - ButtonW * 2 - ValueW), Height - 1); } }
    private Rectangle RightButton { get { return new Rectangle(TrackRect.Right, 0, ButtonW, Height - 1); } }

    private Rectangle ThumbRect
    {
        get
        {
            var t = TrackRect;
            int span = Math.Max(1, t.Width - ThumbW - 1);
            int range = Math.Max(1, max - min);
            int x = t.X + (int)((long)span * (value - min) / range);
            return new Rectangle(x, t.Y, ThumbW, t.Height);
        }
    }

    /// <summary>현대식: 회색 홈 + 값까지 옅은 강조색 채움 + 강조색 손잡이, 화살표는 테두리만 있는 작은 버튼.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(InstTheme.Back);
        var left = LeftButton; var right = RightButton; var track = TrackRect;
        var thumb = ThumbRect;

        using (var surface = new SolidBrush(InstTheme.Surface)) g.FillRectangle(surface, track);
        using (var fill = new SolidBrush(InstTheme.AccentSoft)) g.FillRectangle(fill, track.X, track.Y, thumb.X + thumb.Width / 2 - track.X, track.Height);
        using (var line = new Pen(InstTheme.Line)) g.DrawRectangle(line, track);

        DrawButton(g, left, true);
        DrawButton(g, right, false);

        using (var tb = new SolidBrush(Focused ? InstTheme.AccentDark : InstTheme.Accent)) g.FillRectangle(tb, thumb.X, thumb.Y + 1, thumb.Width, thumb.Height - 1);
        if (Focused) using (var w = new Pen(Color.White)) g.DrawRectangle(w, new Rectangle(thumb.X + 3, thumb.Y + 4, thumb.Width - 7, thumb.Height - 8));

        int vx = right.Right + 6;
        if (ShowWaveGlyph) DrawWave(g, new Rectangle(vx, 3, ValueW - 8, Height - 7), value);
        else using (var ink = new SolidBrush(InstTheme.Ink)) g.DrawString(value.ToString(), Theme.UiFont, ink, vx, 1);
    }

    private static void DrawButton(Graphics g, Rectangle r, bool leftArrow)
    {
        g.FillRectangle(Brushes.White, r);
        using (var line = new Pen(InstTheme.Line)) g.DrawRectangle(line, r);
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        Point[] tri = leftArrow
            ? new[] { new Point(cx + 3, cy - 4), new Point(cx + 3, cy + 4), new Point(cx - 3, cy) }
            : new[] { new Point(cx - 3, cy - 4), new Point(cx - 3, cy + 4), new Point(cx + 3, cy) };
        using (var muted = new SolidBrush(InstTheme.Muted)) g.FillPolygon(muted, tri);
    }

    /// <summary>OPL 웨이브폼 0~3 (사인, 반파, 절대값, 펄스형), OPL3 4~7 (반주기 2배 사인, 반주기 2배 절대값, 사각, 로그 톱니)의 작은 그림.</summary>
    internal static void DrawWave(Graphics g, Rectangle r, int wave)
    {
        int n = Math.Max(8, r.Width);
        var pts = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            double ph = (double)i / (n - 1) * 2.0 * Math.PI;
            double s = Math.Sin(ph);
            double v;
            switch (wave)
            {
                case 1: v = s > 0 ? s : 0; break;
                case 2: v = Math.Abs(s); break;
                case 3: v = (s > 0 && (ph % Math.PI) < Math.PI / 2) ? s : ((s < 0 && ((ph % Math.PI) < Math.PI / 2)) ? -s : 0); break;
                case 4: v = ph < Math.PI ? Math.Sin(2 * ph) : 0; break;
                case 5: v = ph < Math.PI ? Math.Abs(Math.Sin(2 * ph)) : 0; break;
                case 6: v = s >= 0 ? 1 : -1; break;
                case 7: { double f = ph < Math.PI ? ph / Math.PI : (ph - Math.PI) / Math.PI; v = (ph < Math.PI ? 1 : -1) * Math.Pow(1 - f, 3); break; }
                default: v = s; break;
            }
            bool bipolar = wave == 0 || wave == 4 || wave == 6 || wave == 7;
            float y = bipolar ? (float)(r.Y + r.Height / 2.0 - v * r.Height / 2.0)
                                : (float)(r.Bottom - v * r.Height);
            pts[i] = new PointF(r.X + i, y);
        }
        using (var pen = new Pen(InstTheme.Ink, 1.3f)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.DrawLines(pen, pts); g.SmoothingMode = SmoothingMode.Default; }
    }

    private void SetFromX(int x)
    {
        var t = TrackRect;
        int span = Math.Max(1, t.Width - ThumbW);
        double f = (double)(x - t.X - ThumbW / 2) / span;
        Value = min + (int)Math.Round(f * (max - min));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        if (LeftButton.Contains(e.Location)) Value = value - 1;
        else if (RightButton.Contains(e.Location)) Value = value + 1;
        else if (TrackRect.Contains(e.Location)) { dragging = true; Capture = true; SetFromX(e.X); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = false;
        Capture = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value = value + (e.Delta > 0 ? 1 : -1);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        if (keyData == Keys.Left || keyData == Keys.Right) return true;
        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) { Value = value - 1; e.Handled = true; }
        else if (e.KeyCode == Keys.Right) { Value = value + 1; e.Handled = true; }
        else if (e.KeyCode == Keys.Home) { Value = min; e.Handled = true; }
        else if (e.KeyCode == Keys.End) { Value = max; e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
