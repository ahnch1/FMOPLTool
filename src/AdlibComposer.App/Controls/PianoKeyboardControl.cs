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
    /// <summary>울리는 음 → 칠할 색 (Jukebox: 채널 색). 누른 건반보다 먼저 보인다.</summary>
    public Dictionary<int, Color> Lit { get; } = new Dictionary<int, Color>();
    /// <summary>false면 마우스로 치지 않는다 (보기 전용).</summary>
    public bool Interactive { get; set; } = true;

    // 색 (기본 = Instrument Maker의 현대식, Jukebox는 옛 DOS 플레이어 색으로 바꾼다)
    public Color WhiteKeyColor { get; set; } = Color.White;
    public Color BlackKeyColor { get; set; } = InstTheme.Ink;
    public Color KeyLineColor { get; set; } = InstTheme.Line;
    public Color KeyboardBack { get; set; } = InstTheme.Surface;
    /// <summary>
    /// 0보다 크면 이 음역(FitLow ~ FitHigh)을 넓이에 딱 맞게 보인다 (흰 건반 폭 = 넓이 / 흰 건반 수).
    /// 높이는 바꾸지 않으므로 비율을 지키려면 쓰는 쪽이 높이 = PreferredHeight로 맞춘다.
    /// </summary>
    public int FitLow { get; set; }
    public int FitHigh { get; set; }
    /// <summary>FitLow ~ FitHigh를 넓이 w에 맞출 때 흰 건반 비율(KeyAspect)을 지키는 높이.</summary>
    public int PreferredHeight(int w)
    {
        int whites = 0;
        for (int n = FitLow; n <= FitHigh; n++) if (!NoteHelper.IsBlack(n)) whites++;
        return whites == 0 ? Height : (int)Math.Round(w / (float)whites * KeyAspect);
    }

    /// <summary>true면 울리는 건반을 그 색으로 꽉 칠한다 (DOS 플레이어처럼), false면 흰 건반은 반투명 + 아래 띠.</summary>
    public bool SolidLit { get; set; }

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

    private float KeyWidth
    {
        get
        {
            if (FitLow > 0 && FitHigh > FitLow)
            {
                int whites = 0;
                for (int n = FitLow; n <= FitHigh; n++) if (!NoteHelper.IsBlack(n)) whites++;
                return Math.Max(3f, Width / (float)Math.Max(1, whites));
            }
            return Math.Max(6f, Height / KeyAspect);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Arrange();
    }

    /// <summary>넓이에 들어가는 흰 건반 수만큼, FocusNote가 가운데 오도록 LowNote / HighNote를 정한다.</summary>
    private void Arrange()
    {
        if (FitLow > 0 && FitHigh > FitLow) { LowNote = FitLow; HighNote = FitHigh; return; }
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
        g.Clear(KeyboardBack);
        var narrowLabels = new List<int>();
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var whiteUp = new SolidBrush(WhiteKeyColor))
        using (var whiteDown = new SolidBrush(InstTheme.AccentSoft))
        using (var blackUp = new SolidBrush(BlackKeyColor))
        using (var accent = new SolidBrush(InstTheme.Accent))
        using (var edge = new Pen(KeyLineColor))
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
                    Color litColor;
                    bool lit = Lit.TryGetValue(n, out litColor);
                    string label;
                    bool hasLabel = KeyLabels.TryGetValue(n, out label);
                    if (!black)
                    {
                        g.FillRectangle(down ? whiteDown : whiteUp, r);
                        if (lit)
                            using (var lb = new SolidBrush(SolidLit ? litColor : Color.FromArgb(150, litColor))) g.FillRectangle(lb, r.X + 1, r.Y, r.Width - 1, r.Height);
                        if (down || (lit && !SolidLit))
                            using (var band = new SolidBrush(lit ? litColor : InstTheme.Accent)) g.FillRectangle(band, r.X + 1, r.Bottom - 6, r.Width - 1, 6);
                        g.DrawLine(edge, r.X, 0, r.X, r.Bottom);
                        if (n % 12 == 0)
                        {
                            if (r.Width >= 20) g.DrawString(NoteHelper.Name(n), Theme.SmallFont, muted, new RectangleF(r.X, r.Bottom - 22, r.Width, 14), sfCenter);
                            else narrowLabels.Add(n);   // 좁은 건반: 다 그린 뒤 옆 건반까지 걸쳐서
                        }
                        if (hasLabel) g.DrawString(label, Theme.SmallFont, ink, new RectangleF(r.X, r.Bottom - (n % 12 == 0 ? 38 : 22), r.Width, 14), sfCenter);
                    }
                    else
                    {
                        if (lit) using (var lb = new SolidBrush(litColor)) g.FillRectangle(lb, r);
                        else g.FillRectangle(down ? accent : blackUp, r);
                        if (hasLabel) g.DrawString(label, Theme.SmallFont, onBlack, new RectangleF(r.X - 2, r.Bottom - 16, r.Width + 4, 14), sfCenter);
                    }
                }
            }
            foreach (int n in narrowLabels)
            {
                bool b; var r = NoteRect(n, out b);
                g.DrawString(NoteHelper.Name(n), Theme.SmallFont, muted, r.X + 1, r.Bottom - 16);
            }
            g.DrawLine(edge, 0, 0, Width, 0);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Interactive || e.Button != MouseButtons.Left) return;
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
