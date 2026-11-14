using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AdlibComposer.Core;

namespace AdlibComposer.App.Controls;

/// <summary>
/// Instrument Maker 하단의 가로 피아노 건반. 마우스 클릭과 컴퓨터 키 표시를 지원한다.
/// 건반 크기는 높이에 맞춘다 (흰 건반 폭 = 높이 / 5, 검은 건반 = 폭 60% · 높이 62%). 넓이에 들어가는 만큼 보이고,
/// FocusNote(컴퓨터 키가 닿는 음역의 가운데)가 가운데 오도록 보이는 음역(LowNote ~ HighNote)을 정한다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class PianoKeyboardControl : Control
{
    /// <summary>흰 건반 높이 : 폭.</summary>
    public const float KeyAspect = 5f;
    private const int MinNote = 12, MaxNote = 119;

    private readonly HashSet<int> pressed = new HashSet<int>();
    private int mouseNote = -1;
    private int focusNote = 66;

    public int LowNote { get; private set; } = 36;
    public int HighNote { get; private set; } = 96;
    /// <summary>노트 번호 → 컴퓨터 키 글자 (건반에 작게 표시).</summary>
    public Dictionary<int, string> KeyLabels { get; } = new Dictionary<int, string>();

    public event Action<int> NoteDown;
    public event Action<int> NoteUp;

    public PianoKeyboardControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = InstTheme.Surface;
    }

    /// <summary>가운데 둘 음 (바꾸면 보이는 음역을 다시 정한다).</summary>
    public int FocusNote
    {
        get { return focusNote; }
        set { focusNote = value; Arrange(); Invalidate(); }
    }

    private float KeyWidth { get { return Math.Max(6f, Height / KeyAspect); } }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Arrange();
    }

    /// <summary>넓이에 들어가는 흰 건반 수만큼, FocusNote가 가운데 오도록 LowNote / HighNote를 정한다.</summary>
    private void Arrange()
    {
        int whites = Math.Max(1, (int)Math.Ceiling(Width / KeyWidth));
        // FocusNote에서 왼쪽으로 흰 건반 whites/2개
        int low = focusNote;
        for (int w = 0; w < whites / 2 && low > MinNote; ) { low--; if (!NoteHelper.IsBlack(low)) w++; }
        while (NoteHelper.IsBlack(low)) low++;
        int high = low, count = 1;
        while (count < whites && high < MaxNote) { high++; if (!NoteHelper.IsBlack(high)) count++; }
        // 오른쪽 끝에 닿으면 왼쪽으로 채운다
        while (count < whites && low > MinNote) { low--; if (!NoteHelper.IsBlack(low)) count++; }
        while (NoteHelper.IsBlack(low) && low < high) low++;
        LowNote = low;
        HighNote = high;
    }

    public void SetPressed(int note, bool on)
    {
        if (on) pressed.Add(note); else pressed.Remove(note);
        Invalidate();
    }

    private RectangleF NoteRect(int note, out bool black)
    {
        float kw = KeyWidth;
        int wi = 0;
        for (int n = LowNote; n < note; n++) if (!NoteHelper.IsBlack(n)) wi++;
        black = NoteHelper.IsBlack(note);
        if (!black) return new RectangleF(wi * kw, 0, kw, Height);
        float bw = kw * 0.6f;
        return new RectangleF(wi * kw - bw / 2f, 0, bw, Height * 0.62f);
    }

    private int HitTest(Point p)
    {
        for (int n = LowNote; n <= HighNote; n++)
        {
            bool b; var r = NoteRect(n, out b);
            if (b && r.Contains(p.X, p.Y)) return n;
        }
        for (int n = LowNote; n <= HighNote; n++)
        {
            bool b; var r = NoteRect(n, out b);
            if (!b && r.Contains(p.X, p.Y)) return n;
        }
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(InstTheme.Surface);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var whiteUp = new SolidBrush(Color.White))
        using (var whiteDown = new SolidBrush(InstTheme.AccentSoft))
        using (var blackUp = new SolidBrush(InstTheme.Ink))
        using (var accent = new SolidBrush(InstTheme.Accent))
        using (var edge = new Pen(InstTheme.Line))
        using (var muted = new SolidBrush(InstTheme.Muted))
        using (var ink = new SolidBrush(InstTheme.Ink))
        using (var onBlack = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
        using (var sfCenter = new StringFormat { Alignment = StringAlignment.Center })
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int n = LowNote; n <= HighNote; n++)
                {
                    bool black; var r = NoteRect(n, out black);
                    if (black != (pass == 1)) continue;
                    if (r.X > Width) continue;
                    bool down = pressed.Contains(n);
                    string label;
                    bool hasLabel = KeyLabels.TryGetValue(n, out label);
                    if (!black)
                    {
                        g.FillRectangle(down ? whiteDown : whiteUp, r);
                        if (down) g.FillRectangle(accent, r.X + 1, r.Bottom - 5, r.Width - 1, 5);
                        g.DrawLine(edge, r.X, 0, r.X, r.Bottom);
                        if (n % 12 == 0) g.DrawString(NoteHelper.Name(n), Theme.SmallFont, muted, new RectangleF(r.X, r.Bottom - 22, r.Width, 14), sfCenter);
                        if (hasLabel) g.DrawString(label, Theme.SmallFont, ink, new RectangleF(r.X, r.Bottom - (n % 12 == 0 ? 38 : 22), r.Width, 14), sfCenter);
                    }
                    else
                    {
                        g.FillRectangle(down ? accent : blackUp, r);
                        if (hasLabel) g.DrawString(label, Theme.SmallFont, onBlack, new RectangleF(r.X - 2, r.Bottom - 16, r.Width + 4, 14), sfCenter);
                    }
                }
            }
            g.DrawLine(edge, 0, 0, Width, 0);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int n = HitTest(e.Location);
        if (n < 0) return;
        Capture = true;
        mouseNote = n;
        SetPressed(n, true);
        if (NoteDown != null) NoteDown(n);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (mouseNote < 0) return;
        int n = HitTest(e.Location);
        if (n >= 0 && n != mouseNote)
        {
            SetPressed(mouseNote, false);
            if (NoteUp != null) NoteUp(mouseNote);
            mouseNote = n;
            SetPressed(n, true);
            if (NoteDown != null) NoteDown(n);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (mouseNote < 0) return;
        SetPressed(mouseNote, false);
        if (NoteUp != null) NoteUp(mouseNote);
        mouseNote = -1;
        Capture = false;
    }
}
