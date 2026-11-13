using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AdlibComposer.Core;

namespace AdlibComposer.App.Controls;

internal enum RollTool { Pencil, Select }

/// <summary>
/// Visual Composer의 건반 악보 화면. 왼쪽 세로 건반, 위쪽 Instr. 행, 격자(마디=실선, 박=점선),
/// 노트는 가로 막대. 선택한 보이스는 굵게, 나머지 보이스는 얇게(All이면 모두 굵게) 표시한다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class PianoRollControl : Control
{
    public const int KeyboardWidth = 56;
    public const int BandHeight = 22;
    public const int RowHeight = 8;
    private const int ResizeGrip = 4;

    private enum DragMode { None, Draw, Move, Resize }

    private struct Origin { public int Start, Duration, Pitch; }

    private static readonly List<Note> Clipboard = new List<Note>();
    private static readonly Brush SelectedBrush = new SolidBrush(Theme.Selected);

    private Song song = new Song();
    private DragMode drag = DragMode.None;
    private Note drawing;
    private int dragStartTick, dragStartNote;
    private bool dragUndoPushed;
    private readonly Dictionary<Note, Origin> origins = new Dictionary<Note, Origin>();
    private int pressedKey = -1;
    private float pixelsPerBeat = 40f;

    public int CurrentVoice { get; set; }
    public bool ShowAll { get; set; }
    public RollTool Tool { get; set; } = RollTool.Pencil;
    public int SnapTicks { get; set; } = 12;
    public int ScrollTick { get; set; }
    public int TopNote { get; set; } = 84;
    public int CursorTick { get; set; }
    public double PlayTick { get; set; } = -1;
    public HashSet<Note> Selection { get; } = new HashSet<Note>();

    public event Action BeforeEdit;
    public event Action SongEdited;
    public event Action ViewChanged;
    public event Action EditCursorChanged;
    public event Action<int> TimbreRequested;
    public event Action<int> HoverNote;
    public event Action<int> PreviewOn;
    public event Action PreviewOff;

    public PianoRollControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Theme.Back;
    }

    public Song Song
    {
        get { return song; }
        set
        {
            song = value ?? new Song();
            Selection.Clear();
            drawing = null; drag = DragMode.None;
            if (CurrentVoice >= song.VoiceCount) CurrentVoice = 0;
            Invalidate();
        }
    }

    public float PixelsPerBeat
    {
        get { return pixelsPerBeat; }
        set
        {
            pixelsPerBeat = Math.Max(8f, Math.Min(400f, value));
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }
    }

    // ------------------------------------------------------------ 좌표 변환

    private float PxPerTick { get { return pixelsPerBeat / Math.Max(1, song.TickBeat); } }
    public int VisibleTicks { get { return (int)Math.Max(1, (Width - KeyboardWidth) / PxPerTick); } }
    public int VisibleRows { get { return Math.Max(1, (Height - BandHeight) / RowHeight); } }

    private int TickToX(double tick) { return KeyboardWidth + (int)Math.Round((tick - ScrollTick) * PxPerTick); }
    private int XToTick(int x) { return ScrollTick + (int)Math.Floor((x - KeyboardWidth) / PxPerTick); }
    private int NoteToY(int note) { return BandHeight + (TopNote - note) * RowHeight; }
    private int YToNote(int y) { return TopNote - (int)Math.Floor((double)(y - BandHeight) / RowHeight); }

    private int SnapDown(int tick) { int s = Math.Max(1, SnapTicks); return Math.Max(0, tick / s * s); }
    private int SnapUp(int tick) { int s = Math.Max(1, SnapTicks); return Math.Max(s, (tick + s - 1) / s * s); }
    private int SnapRound(int delta) { int s = Math.Max(1, SnapTicks); return (int)Math.Round((double)delta / s) * s; }

    public void ClampView()
    {
        int rows = VisibleRows;
        int minTop = Song.MinNote + rows - 1;
        if (TopNote > Song.MaxNote) TopNote = Song.MaxNote;
        if (TopNote < minTop) TopNote = Math.Min(Song.MaxNote, minTop);
        if (ScrollTick < 0) ScrollTick = 0;
    }

    private VoiceTrack Voice { get { return song.Voices[Math.Max(0, Math.Min(Song.MaxVoices - 1, CurrentVoice))]; } }

    // ------------------------------------------------------------ 그리기

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampView();
        if (ViewChanged != null) ViewChanged();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Back);
        int gridRight = Width;

        // 가로줄 (반음마다) + 검은건반 행 음영
        using (var gridPen = new Pen(Theme.GridLine))
        using (var octPen = new Pen(Theme.OctaveLine))
        using (var shade = new SolidBrush(Theme.BlackKeyRow))
        {
            for (int n = TopNote; n >= Song.MinNote; n--)
            {
                int y = NoteToY(n);
                if (y >= Height) break;
                if (NoteHelper.IsBlack(n)) g.FillRectangle(shade, KeyboardWidth, y, gridRight - KeyboardWidth, RowHeight);
                g.DrawLine(n % 12 == 0 ? octPen : gridPen, KeyboardWidth, y + RowHeight, gridRight, y + RowHeight);
            }
        }

        // 세로줄: 마디 = 실선, 박 = 점선, 스냅 = 아주 연한 점선
        int beat = Math.Max(1, song.TickBeat);
        int measure = Math.Max(1, song.MeasureTicks);
        using (var snapPen = new Pen(Color.FromArgb(225, 225, 225)))
        using (var beatPen = new Pen(Theme.GridLine) { DashStyle = DashStyle.Dot })
        using (var measurePen = new Pen(Color.Black))
        {
            int snap = Math.Max(1, SnapTicks);
            if (snap * PxPerTick >= 5)
                for (int t = ScrollTick / snap * snap; TickToX(t) < gridRight; t += snap)
                    if (t % beat != 0) g.DrawLine(snapPen, TickToX(t), BandHeight, TickToX(t), Height);
            for (int t = ScrollTick / beat * beat; TickToX(t) < gridRight; t += beat)
            {
                int x = TickToX(t);
                if (x < KeyboardWidth) continue;
                g.DrawLine(t % measure == 0 ? measurePen : beatPen, x, BandHeight, x, Height);
                if (t % measure == 0) g.DrawString((t / measure + 1).ToString(), Theme.SmallFont, Brushes.Gray, x + 2, BandHeight + 1);
            }
        }

        DrawNotes(g);
        DrawBand(g);
        DrawKeyboard(g);

        // 편집 커서 / 재생 커서
        int cx = TickToX(CursorTick);
        if (cx >= KeyboardWidth && cx < Width)
            using (var p = new Pen(Theme.EditCursor) { DashStyle = DashStyle.Dash }) g.DrawLine(p, cx, 0, cx, Height);
        if (PlayTick >= 0)
        {
            int px = TickToX(PlayTick);
            if (px >= KeyboardWidth && px < Width)
                using (var p = new Pen(Theme.PlayCursor, 2f)) g.DrawLine(p, px, 0, px, Height);
        }
        g.DrawRectangle(Pens.Black, 0, 0, Width - 1, Height - 1);
    }

    private Rectangle NoteRect(Note n, bool thin)
    {
        int x1 = TickToX(n.StartTick), x2 = TickToX(n.EndTick);
        int y = NoteToY(n.Pitch);
        if (thin) return new Rectangle(x1, y + RowHeight / 2 - 1, Math.Max(2, x2 - x1), 2);
        return new Rectangle(x1, y + 1, Math.Max(2, x2 - x1 - 1), RowHeight - 2);
    }

    private void DrawNotes(Graphics g)
    {
        int count = song.VoiceCount;
        using (var thick = new SolidBrush(Color.FromArgb(90, 90, 90)))
        using (var thin = new SolidBrush(Color.FromArgb(130, 130, 130)))
        {
            for (int v = 0; v < count; v++)
            {
                if (v == CurrentVoice && !ShowAll) continue;
                foreach (var n in song.Voices[v].Notes)
                {
                    var r = NoteRect(n, !ShowAll);
                    if (r.Right < KeyboardWidth || r.Left > Width || r.Bottom < BandHeight || r.Top > Height) continue;
                    g.FillRectangle(ShowAll ? thick : thin, r);
                }
            }
        }
        foreach (var n in Voice.Notes)
        {
            var r = NoteRect(n, false);
            if (r.Right < KeyboardWidth || r.Left > Width || r.Bottom < BandHeight || r.Top > Height) continue;
            bool sel = Selection.Contains(n);
            g.FillRectangle(sel ? SelectedBrush : Brushes.Black, r);
        }
        if (drawing != null)
        {
            var r = NoteRect(drawing, false);
            g.FillRectangle(Brushes.Gray, r);
            g.DrawRectangle(Pens.Black, r);
        }
    }

    private void DrawBand(Graphics g)
    {
        g.FillRectangle(Brushes.White, 0, 0, Width, BandHeight);
        g.DrawLine(Pens.Black, 0, BandHeight, Width, BandHeight);
        g.DrawString("Instr.", Theme.UiFont, Brushes.Black, 3, 3);
        var clip = new Rectangle(KeyboardWidth, 0, Math.Max(1, Width - KeyboardWidth), BandHeight);
        var saved = g.Clip;
        g.SetClip(clip);
        foreach (var t in Voice.Timbres)
        {
            int x = TickToX(t.Tick);
            if (x > Width) continue;
            g.DrawLine(Pens.Black, x, 2, x, BandHeight - 2);
            g.DrawString(t.InstrumentName, Theme.UiFont, Brushes.Black, x + 3, 3);
        }
        g.Clip = saved;
    }

    private void DrawKeyboard(Graphics g)
    {
        g.FillRectangle(Brushes.White, 0, BandHeight, KeyboardWidth, Height - BandHeight);
        for (int n = TopNote; n >= Song.MinNote; n--)
        {
            int y = NoteToY(n);
            if (y >= Height) break;
            bool black = NoteHelper.IsBlack(n);
            if (black) g.FillRectangle(Brushes.Black, 0, y, KeyboardWidth * 3 / 5, RowHeight);
            if (n == pressedKey) g.FillRectangle(black ? Brushes.DimGray : Brushes.LightGray, 0, y, KeyboardWidth - 1, RowHeight);
            g.DrawLine(Pens.Black, black ? KeyboardWidth * 3 / 5 : 0, y + RowHeight, KeyboardWidth, y + RowHeight);
            if (n % 12 == 0) g.DrawString(NoteHelper.Name(n), Theme.SmallFont, Brushes.Black, KeyboardWidth - 24, y - 1);
        }
        g.DrawLine(Pens.Black, KeyboardWidth, BandHeight, KeyboardWidth, Height);
    }

    // ------------------------------------------------------------ 편집 동작

    private void RaiseBeforeEdit() { if (BeforeEdit != null) BeforeEdit(); }
    private void RaiseEdited() { if (SongEdited != null) SongEdited(); Invalidate(); }

    public void DeleteSelection()
    {
        if (Selection.Count == 0) return;
        RaiseBeforeEdit();
        foreach (var n in new List<Note>(Selection)) Voice.Notes.Remove(n);
        Selection.Clear();
        RaiseEdited();
    }

    public void SelectAll()
    {
        Selection.Clear();
        foreach (var n in Voice.Notes) Selection.Add(n);
        Invalidate();
    }

    public void CopySelection()
    {
        if (Selection.Count == 0) return;
        Clipboard.Clear();
        int min = int.MaxValue;
        foreach (var n in Selection) min = Math.Min(min, n.StartTick);
        foreach (var n in Selection)
        {
            var c = n.Clone();
            c.StartTick -= min;
            Clipboard.Add(c);
        }
    }

    public void Paste()
    {
        if (Clipboard.Count == 0) return;
        RaiseBeforeEdit();
        Selection.Clear();
        foreach (var c in Clipboard)
        {
            var n = c.Clone();
            n.StartTick += CursorTick;
            Voice.InsertNote(n);
            Selection.Add(n);
        }
        RaiseEdited();
    }

    public void EnsureTickVisible(int tick)
    {
        if (tick < ScrollTick || tick > ScrollTick + VisibleTicks) { ScrollTick = Math.Max(0, tick - VisibleTicks / 10); Invalidate(); if (ViewChanged != null) ViewChanged(); }
    }

    // ------------------------------------------------------------ 마우스

    private Note HitNote(int tick, int note) { return Voice.FindNote(tick, note); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (e.X < KeyboardWidth && e.Y >= BandHeight)
        {
            int kn = Math.Max(Song.MinNote, Math.Min(Song.MaxNote, YToNote(e.Y)));
            pressedKey = kn;
            Capture = true;
            if (PreviewOn != null) PreviewOn(kn);
            Invalidate();
            return;
        }
        if (e.Y < BandHeight)
        {
            if (e.X >= KeyboardWidth) SetCursor(SnapDown(XToTick(e.X)));
            return;
        }

        int tick = XToTick(e.X);
        int note = YToNote(e.Y);
        if (note < Song.MinNote || note > Song.MaxNote || tick < 0) return;

        if (e.Button == MouseButtons.Right)
        {
            var del = HitNote(tick, note);
            if (del != null) { RaiseBeforeEdit(); Voice.Notes.Remove(del); Selection.Remove(del); RaiseEdited(); }
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        var hit = HitNote(tick, note);
        if (Tool == RollTool.Pencil)
        {
            if (hit != null)
            {
                RaiseBeforeEdit();
                Voice.Notes.Remove(hit); Selection.Remove(hit);
                RaiseEdited();
                return;
            }
            int start = SnapDown(tick);
            drawing = new Note { StartTick = start, Duration = Math.Max(1, SnapTicks), Pitch = note };
            drag = DragMode.Draw;
            Capture = true;
            SetCursor(start);
            if (PreviewOn != null) PreviewOn(note);
            Invalidate();
            return;
        }

        // 선택 도구
        if (hit != null)
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                if (!Selection.Remove(hit)) Selection.Add(hit);
            }
            else if (!Selection.Contains(hit)) { Selection.Clear(); Selection.Add(hit); }

            origins.Clear();
            foreach (var n in Selection) origins[n] = new Origin { Start = n.StartTick, Duration = n.Duration, Pitch = n.Pitch };
            drag = e.X >= TickToX(hit.EndTick) - ResizeGrip ? DragMode.Resize : DragMode.Move;
            dragStartTick = tick; dragStartNote = note;
            dragUndoPushed = false;
            Capture = true;
            if (PreviewOn != null) PreviewOn(hit.Pitch);
        }
        else
        {
            if ((ModifierKeys & Keys.Control) == 0) Selection.Clear();
            SetCursor(SnapDown(tick));
        }
        Invalidate();
    }

    private void SetCursor(int tick)
    {
        CursorTick = Math.Max(0, tick);
        if (EditCursorChanged != null) EditCursorChanged();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (HoverNote != null && e.Y >= BandHeight)
        {
            int hn = YToNote(e.Y);
            HoverNote(hn >= Song.MinNote && hn <= Song.MaxNote ? hn : -1);
        }

        int tick = XToTick(e.X);
        if (drag == DragMode.Draw && drawing != null)
        {
            int end = SnapUp(Math.Max(tick, drawing.StartTick + 1));
            drawing.Duration = Math.Max(Math.Max(1, SnapTicks), end - drawing.StartTick);
            Invalidate();
        }
        else if ((drag == DragMode.Move || drag == DragMode.Resize) && origins.Count > 0)
        {
            int dt = SnapRound(tick - dragStartTick);
            int dn = drag == DragMode.Move ? YToNote(e.Y) - dragStartNote : 0;
            if (!dragUndoPushed && (dt != 0 || dn != 0)) { RaiseBeforeEdit(); dragUndoPushed = true; }
            if (!dragUndoPushed) return;
            foreach (var kv in origins)
            {
                var n = kv.Key; var o = kv.Value;
                if (drag == DragMode.Move)
                {
                    n.StartTick = Math.Max(0, o.Start + dt);
                    n.Pitch = Math.Max(Song.MinNote, Math.Min(Song.MaxNote, o.Pitch + dn));
                }
                else n.Duration = Math.Max(Math.Max(1, SnapTicks), o.Duration + dt);
            }
            Invalidate();
        }
        else if (pressedKey >= 0 && e.X < KeyboardWidth)
        {
            int kn = Math.Max(Song.MinNote, Math.Min(Song.MaxNote, YToNote(e.Y)));
            if (kn != pressedKey) { pressedKey = kn; if (PreviewOn != null) PreviewOn(kn); Invalidate(); }
        }
        UpdateCursorShape(e);
    }

    private void UpdateCursorShape(MouseEventArgs e)
    {
        if (e.X < KeyboardWidth || e.Y < BandHeight) { Cursor = Cursors.Default; return; }
        if (Tool == RollTool.Pencil) { Cursor = Cursors.Cross; return; }
        var hit = HitNote(XToTick(e.X), YToNote(e.Y));
        Cursor = hit != null && e.X >= TickToX(hit.EndTick) - ResizeGrip ? Cursors.SizeWE : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Capture = false;
        if (pressedKey >= 0) { pressedKey = -1; if (PreviewOff != null) PreviewOff(); Invalidate(); }

        if (drag == DragMode.Draw && drawing != null)
        {
            RaiseBeforeEdit();
            Voice.InsertNote(drawing);
            drawing = null;
            drag = DragMode.None;
            if (PreviewOff != null) PreviewOff();
            RaiseEdited();
        }
        else if ((drag == DragMode.Move || drag == DragMode.Resize))
        {
            if (dragUndoPushed)
            {
                foreach (var n in new List<Note>(origins.Keys)) Voice.InsertNote(n);
                RaiseEdited();
            }
            origins.Clear();
            drag = DragMode.None;
            if (PreviewOff != null) PreviewOff();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Y < BandHeight && e.X >= KeyboardWidth && TimbreRequested != null)
            TimbreRequested(SnapDown(XToTick(e.X)));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0)
            PixelsPerBeat = pixelsPerBeat * (e.Delta > 0 ? 1.25f : 0.8f);
        else if ((ModifierKeys & Keys.Shift) != 0)
            ScrollTick = Math.Max(0, ScrollTick + (e.Delta > 0 ? -1 : 1) * song.TickBeat);
        else
            TopNote += e.Delta > 0 ? 3 : -3;
        ClampView();
        Invalidate();
        if (ViewChanged != null) ViewChanged();
    }
}
