using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AdlibComposer.App.Notes;

/// <summary>악보 그림판: 보이는 줄만 그리고, 음표 위에 마우스를 올리면 강조 + 설명, 클릭하면 NoteClicked.</summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class ScoreCanvas : Control
{
    private ScoreRenderer renderer;
    private List<ScoreSystem> systems = new List<ScoreSystem>();
    private readonly ToolTip tip = new ToolTip { InitialDelay = 200, ReshowDelay = 50 };
    private float layoutWidth = -1;

    public event Action<ScoreHit> NoteClicked;
    public event Action<ScoreHit> NoteHovered;

    public ScoreCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
    }

    public ScoreRenderer Renderer
    {
        get { return renderer; }
        set { renderer = value; layoutWidth = -1; Relayout(); }
    }

    /// <summary>폭이 바뀌거나 확대 비율이 바뀌면 줄을 다시 나누고 높이를 맞춘다.</summary>
    public void Relayout()
    {
        if (renderer == null || renderer.IsEmpty) { systems.Clear(); Height = Math.Max(Parent != null ? Parent.ClientSize.Height - 4 : 200, 200); Invalidate(); return; }
        float total;
        systems = renderer.Layout(Width, out total);
        layoutWidth = Width;
        int h = (int)Math.Ceiling(total);
        if (Height != h) Height = h;
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (renderer != null && Math.Abs(Width - layoutWidth) > 0.5f) Relayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (renderer == null || renderer.IsEmpty)
        {
            using (var f = new Font("Segoe UI", 10f))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString("이 곡에는 보여 줄 노트가 없습니다. Composer에서 노트를 넣거나 곡을 열어 보세요.", f, Brushes.Gray, new RectangleF(0, 60, Width, 40), sf);
            return;
        }
        renderer.Hits.Clear();
        renderer.DrawTitle(g, Width, 0);
        foreach (var s in systems)
        {
            if (s.Y + s.Height < e.ClipRectangle.Top || s.Y > e.ClipRectangle.Bottom) continue;
            renderer.DrawSystem(g, s, 0, Width, true);
        }
    }

    private int HitIndex(Point p)
    {
        if (renderer == null) return -1;
        for (int i = 0; i < renderer.Hits.Count; i++)
            if (renderer.Hits[i].Bounds.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = HitIndex(e.Location);
        if (renderer == null || i == renderer.HoverIndex) return;
        renderer.HoverIndex = i;
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        var hit = i >= 0 ? renderer.Hits[i] : null;
        tip.SetToolTip(this, hit != null ? hit.Label + "\n클릭: Composer에서 이 음으로 이동" : null);
        if (NoteHovered != null) NoteHovered(hit);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (renderer == null || renderer.HoverIndex < 0) return;
        renderer.HoverIndex = -1;
        if (NoteHovered != null) NoteHovered(null);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = HitIndex(e.Location);
        if (i >= 0 && e.Button == MouseButtons.Left && NoteClicked != null) NoteClicked(renderer.Hits[i]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tip.Dispose();
        base.Dispose(disposing);
    }
}
