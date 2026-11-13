using System;
using System.Drawing;
using System.Windows.Forms;
using AdlibComposer.Core;
using AdlibComposer.Notation;

namespace AdlibComposer.App.Orgel;

/// <summary>
/// 오르골 띠 화면: 음계 표시는 왼쪽에 고정, 띠는 가로로 스크롤(아래 막대 · 휠). Ctrl+휠 = 확대.
/// 클릭 = 시험 연주 시작 위치, 구멍 위에 마우스 = 음 정보.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class OrgelCanvas : Control
{
    private readonly HScrollBar hbar = new HScrollBar();
    private readonly ToolTip tip = new ToolTip { InitialDelay = 200, ReshowDelay = 50 };
    private OrgelStrip strip;
    private double scrollTick = -24;
    private OrgelPunch hover;
    private string lastTip;

    public OrgelRenderer Renderer { get; } = new OrgelRenderer();
    /// <summary>가로 확대 (격자 한 칸 = 줄 간격 × 4 × Zoom, 실제 띠의 8mm : 2mm 비율이 1.0).</summary>
    public float Zoom { get; set; } = 1f;
    public float GridRatio { get; set; } = 4f;
    public double CursorTick { get; private set; }
    public double PlayTick { get; set; } = -1;
    /// <summary>음 번호 → 이름 (툴팁용, 조옮김 · 빗살 기준 반영).</summary>
    public Func<OrgelPunch, string> DescribePunch;
    public Func<OrgelMissing, string> DescribeMissing;

    public event Action<double> CursorMoved;
    public event Action<int> ZoomRequested;

    public OrgelCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(236, 238, 241);
        hbar.Dock = DockStyle.Bottom;
        hbar.Scroll += delegate { scrollTick = hbar.Value - Lead; Invalidate(); };
        Controls.Add(hbar);
    }

    public OrgelStrip Strip
    {
        get { return strip; }
        set { strip = value; hover = null; UpdateScroll(); Invalidate(); }
    }

    private float StripLeft { get { return 12 + Renderer.LabelWidth; } }
    private float PaperTop { get { return Math.Max(Renderer.RowPitch * 2.2f, (Height - hbar.Height - Renderer.PaperHeight) / 2f); } }
    private double VisibleTicks { get { return Math.Max(1, (Width - StripLeft - 8) / Renderer.UnitsPerTick); } }
    private int TotalTicks { get { return strip == null ? 0 : strip.LengthTicks + Renderer.GridTicks * 4; } }
    /// <summary>띠 앞의 빈 칸 (첫 구멍이 가장자리에 걸리지 않게).</summary>
    private int Lead { get { return Math.Max(1, Renderer.GridTicks); } }

    public void UpdateScroll()
    {
        int total = TotalTicks + Lead;
        int vis = (int)VisibleTicks;
        hbar.Minimum = 0;
        hbar.Maximum = Math.Max(0, total);
        hbar.LargeChange = Math.Max(1, vis);
        hbar.SmallChange = Math.Max(1, Renderer.GridTicks);
        scrollTick = Math.Max(-Lead, Math.Min(scrollTick, Math.Max(-Lead, total - Lead - vis + 1)));
        hbar.Value = (int)Math.Min(hbar.Maximum, scrollTick + Lead);
        hbar.Enabled = total > vis;
        Invalidate();
    }

    /// <summary>재생 위치가 화면 밖으로 나가면 따라간다.</summary>
    public void Follow(double tick)
    {
        double vis = VisibleTicks;
        if (tick < scrollTick || tick > scrollTick + vis * 0.85)
        {
            scrollTick = tick - vis * 0.15;
            UpdateScroll();
        }
    }

    public void SetCursor(double tick)
    {
        CursorTick = Math.Max(0, tick);
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); ApplySize(); }

    /// <summary>띠 전체(30줄 + 마디 번호 + 아래 × 표시)가 높이에 들어가게 줄 간격을 정하고, 가로는 Zoom으로.</summary>
    public void ApplySize()
    {
        float avail = Math.Max(100, Height - hbar.Height - 8);
        float pitch = avail / (OrgelComb.Rows - 1 + 2 * OrgelRenderer.EdgeRows + 3.2f);
        pitch = Math.Max(8f, Math.Min(20f, pitch));
        Renderer.RowPitch = pitch;
        Renderer.LabelWidth = pitch * 4.6f;
        Renderer.GridWidth = pitch * GridRatio * Zoom;
        UpdateScroll();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (strip == null) return;
        float top = PaperTop;
        double to = scrollTick + VisibleTicks;
        g.SetClip(new RectangleF(StripLeft, 0, Width - StripLeft, Height - hbar.Height));
        Renderer.DrawStrip(g, strip, StripLeft, top, scrollTick, to, PlayTick >= 0 ? PlayTick : -1, hover);
        // 시작 위치 표시 (초록 삼각형)
        if (CursorTick >= scrollTick && CursorTick < to)
        {
            float x = StripLeft + (float)((CursorTick - scrollTick) * Renderer.UnitsPerTick);
            using (var b = new SolidBrush(Color.FromArgb(0, 140, 60)))
                g.FillPolygon(b, new[] { new PointF(x - 6, top - 12), new PointF(x + 6, top - 12), new PointF(x, top - 2) });
            using (var p = new Pen(Color.FromArgb(120, 0, 140, 60), 1f)) g.DrawLine(p, x, top, x, top + Renderer.PaperHeight);
        }
        g.ResetClip();
        Renderer.DrawLabels(g, 12, top);
    }

    private double TickAtX(float x) { return scrollTick + (x - StripLeft) / Renderer.UnitsPerTick; }

    private OrgelPunch HitPunch(Point p)
    {
        if (strip == null) return null;
        float top = PaperTop, r = Renderer.RowPitch * 0.6f;
        foreach (var pu in strip.Punches)
        {
            float x = StripLeft + (float)((pu.Tick - scrollTick) * Renderer.UnitsPerTick);
            if (Math.Abs(x - p.X) > r) continue;
            float y = Renderer.RowY(top, pu.Row);
            if (Math.Abs(y - p.Y) <= r) return pu;
        }
        return null;
    }

    private OrgelMissing HitMissing(Point p)
    {
        if (strip == null) return null;
        float my = PaperTop + Renderer.PaperHeight + Renderer.RowPitch * 0.75f, r = Renderer.RowPitch * 0.6f;
        if (Math.Abs(p.Y - my) > r) return null;
        foreach (var m in strip.Missing)
        {
            float x = StripLeft + (float)((m.Tick - scrollTick) * Renderer.UnitsPerTick);
            if (Math.Abs(x - p.X) <= r) return m;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitPunch(e.Location);
        string t = null;
        if (h != null && DescribePunch != null) t = DescribePunch(h);
        else
        {
            var m = HitMissing(e.Location);
            if (m != null && DescribeMissing != null) t = DescribeMissing(m);
        }
        if (!ReferenceEquals(h, hover)) { hover = h; Invalidate(); }
        if (t != lastTip) { lastTip = t; tip.SetToolTip(this, t ?? ""); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (strip == null || e.Button != MouseButtons.Left || e.X < StripLeft) return;
        double t = TickAtX(e.X);
        int g = Math.Max(1, Renderer.GridTicks);
        t = Math.Max(0, Math.Round(t / g) * g);
        SetCursor(t);
        if (CursorMoved != null) CursorMoved(t);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0) { if (ZoomRequested != null) ZoomRequested(e.Delta > 0 ? 1 : -1); return; }
        scrollTick -= Math.Sign(e.Delta) * Renderer.GridTicks * 4;
        UpdateScroll();
    }
}
