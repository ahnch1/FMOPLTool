using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AdlibComposer.Core;

namespace AdlibComposer.App.Controls;

/// <summary>Instrument Maker 하단의 가로 피아노 건반. 마우스 클릭과 컴퓨터 키 표시를 지원한다.</summary>
internal sealed class PianoKeyboardControl : Control
{
    private readonly HashSet<int> pressed = new HashSet<int>();
    private int mouseNote = -1;

    public int LowNote { get; set; } = 36;
    public int HighNote { get; set; } = 96;
    /// <summary>노트 번호 → 컴퓨터 키 글자 (건반에 작게 표시).</summary>
    public Dictionary<int, string> KeyLabels { get; } = new Dictionary<int, string>();

    public event Action<int> NoteDown;
    public event Action<int> NoteUp;

    public PianoKeyboardControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Back;
    }

    public void SetPressed(int note, bool on)
    {
        if (on) pressed.Add(note); else pressed.Remove(note);
        Invalidate();
    }

    private int WhiteCount()
    {
        int w = 0;
        for (int n = LowNote; n <= HighNote; n++) if (!NoteHelper.IsBlack(n)) w++;
        return Math.Max(1, w);
    }

    private RectangleF NoteRect(int note, out bool black)
    {
        float kw = (float)Width / WhiteCount();
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
        g.Clear(Theme.Back);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int n = LowNote; n <= HighNote; n++)
            {
                bool black; var r = NoteRect(n, out black);
                if (black != (pass == 1)) continue;
                bool down = pressed.Contains(n);
                if (!black)
                {
                    g.FillRectangle(down ? Brushes.LightGray : Brushes.White, r);
                    g.DrawRectangle(Pens.Black, r.X, r.Y, r.Width, r.Height - 1);
                    if (n % 12 == 0) g.DrawString(NoteHelper.Name(n), Theme.SmallFont, Brushes.Gray, r.X + 1, r.Bottom - 26);
                }
                else
                {
                    g.FillRectangle(down ? Brushes.DimGray : Brushes.Black, r);
                }
                string label;
                if (KeyLabels.TryGetValue(n, out label))
                    g.DrawString(label, Theme.SmallFont, black ? Brushes.White : Brushes.Black, r.X + 2, black ? r.Bottom - 14 : r.Bottom - 14);
            }
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
