using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Tracker;

/// <summary>
/// Scream Tracker 3 패턴 편집 화면 (F2). 한 칸 = "C-5 04 v12 H81" (음 · 옥타브, 악기, 볼륨, 명령, 인포). 색 · 배치는 OpenMPT 패턴 화면처럼.
/// 키 (ST3 그대로): 아랫줄 Z S X D C V G B H N J M = 기준 옥타브, 윗줄 Q 2 W 3 E R 5 T 6 Y 7 U I 9 O 0 P = 한 옥타브 위부터,
/// . = 지우기, 1 = ^^ (노트 오프), / * = 기준 옥타브, &lt; &gt; = 악기, Enter = 칸의 음 · 악기 가져오기, Ins / Del = 줄 넣기 / 빼기,
/// 4 = 칸의 음 듣기, 8 = 줄 듣기, Alt+Q / Alt+A = 고른 영역 반음 올림/내림, Shift+화살표 = 영역, Ctrl+C / X / V = 복사 / 잘라내기 / 붙이기, Ctrl+Z = 되돌리기.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class PatternGrid : Control
{
    // OpenMPT 패턴 화면 색 (흰 바탕, 박 · 마디 줄은 옅은 회녹색, 재생 줄은 노랑)
    public static readonly Color Back = Color.White;
    public static readonly Color BackBeat = Color.FromArgb(242, 246, 242);
    public static readonly Color BackBar = Color.FromArgb(226, 236, 228);
    public static readonly Color HeadBack = Color.FromArgb(240, 240, 240);
    public static readonly Color HeadLine = Color.FromArgb(200, 200, 200);
    public static readonly Color RowNoBack = Color.FromArgb(236, 236, 236);
    public static readonly Color ColumnLine = Color.FromArgb(210, 210, 210);
    public static readonly Color Dim = Color.FromArgb(170, 170, 170);          // 빈 칸 점 · 꺼진 채널
    public static readonly Color NoteFg = Color.FromArgb(20, 20, 20);         // 음: 검정
    public static readonly Color InsFg = Color.FromArgb(0, 128, 128);         // 악기: 청록
    public static readonly Color VolFg = Color.FromArgb(0, 128, 0);           // 볼륨: 초록
    public static readonly Color CmdFg = Color.FromArgb(0, 64, 192);          // 효과: 파랑
    public static readonly Color RowNoFg = Color.FromArgb(60, 60, 60);
    public static readonly Color CursorBack = Color.FromArgb(40, 40, 40);     // 커서 칸: 검은 칸에 흰 글자
    public static readonly Color CursorRowBack = Color.FromArgb(232, 240, 255);
    public static readonly Color PlayBack = Color.FromArgb(255, 255, 140);   // 재생 줄
    public static readonly Color SelBack = Color.FromArgb(180, 210, 240);
    public static readonly Color MeterOn = Color.FromArgb(0, 168, 0), MeterOff = Color.FromArgb(214, 214, 214);

    /// <summary>채널 음량 0~1 (머리줄 막대). 재생기가 채운다 (없으면 막대 없음).</summary>
    public float[] Levels = new float[TrackerModule.MaxChannels];

    private static readonly string[] NoteNames = { "C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-" };
    /// <summary>칸 안의 글자 위치: 음(3) 공백 악기(2) 공백 v볼륨(3) 공백 명령(1) 인포(2) = 14글자 (OpenMPT처럼 볼륨 앞에 v).</summary>
    private static readonly int[] FieldCol = { 0, 4, 5, 8, 9, 11, 12, 13 };
    private static readonly int[] FieldWidth = { 3, 1, 1, 1, 1, 1, 1, 1 };
    public const int Fields = 8;   // 0 음, 1 악기 10, 2 악기 1, 3 볼륨 10, 4 볼륨 1, 5 명령, 6 인포 16, 7 인포 1

    private static readonly Dictionary<Keys, int> PianoKeys = BuildPiano();

    private static Dictionary<Keys, int> BuildPiano()
    {
        var m = new Dictionary<Keys, int>();
        Keys[] low = { Keys.Z, Keys.S, Keys.X, Keys.D, Keys.C, Keys.V, Keys.G, Keys.B, Keys.H, Keys.N, Keys.J, Keys.M };
        for (int i = 0; i < low.Length; i++) m[low[i]] = i;
        Keys[] high = { Keys.Q, Keys.D2, Keys.W, Keys.D3, Keys.E, Keys.R, Keys.D5, Keys.T, Keys.D6, Keys.Y, Keys.D7, Keys.U, Keys.I, Keys.D9, Keys.O, Keys.D0, Keys.P };
        for (int i = 0; i < high.Length; i++) m[high[i]] = 12 + i;
        return m;
    }

    /// <summary>ST3 건반 키 → 기준 옥타브 C로부터의 반음 (F3 악기 시음에도 쓴다).</summary>
    public static bool TryPiano(Keys key, out int semitone) { return PianoKeys.TryGetValue(key, out semitone); }

    private readonly VScrollBar vbar = new VScrollBar();
    private readonly HScrollBar hbar = new HScrollBar();
    private readonly Font font = new Font("Consolas", 10f);
    private TrackerModule module;
    private TrackerPattern pattern;
    private int charW = 8, lineH = 16;
    private int firstRow, firstChannel;

    public int CursorRow, CursorChannel, CursorField;
    public int PlayRow = -1;
    public int BaseOctave = 4;
    public int CurrentInstrument = 1;
    public int Skip = 1;
    public int BarRows = 16, BeatRows = 4;
    /// <summary>선택 영역 (채널, 줄). 없으면 SelStart.X = -1.</summary>
    public Point SelStart = new Point(-1, -1), SelEnd;
    private TrackerCell[,] clipboard;

    public event Action PatternEdited;
    public event Action BeforeEdit;
    public event Action CursorMoved;
    public event Action<int, int, int> PlayNote;   // (악기, 음, 채널)
    public event Action<int> PlayRowRequested;
    public event Action<int> InstrumentChanged;
    public event Action<int> PatternStep;
    public event Action<int> OctaveChanged;

    public PatternGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Back;
        vbar.Dock = DockStyle.Right;
        hbar.Dock = DockStyle.Bottom;
        vbar.Scroll += delegate { firstRow = vbar.Value; Invalidate(); };
        hbar.Scroll += delegate { firstChannel = hbar.Value; Invalidate(); };
        Controls.Add(vbar);
        Controls.Add(hbar);
        using (var g = CreateGraphics())
        {
            var sz = TextRenderer.MeasureText(g, "0000000000", font, Size.Empty, TextFormatFlags.NoPadding);
            charW = Math.Max(6, sz.Width / 10);
            lineH = Math.Max(14, sz.Height + 2);
        }
    }

    public void Bind(TrackerModule m, TrackerPattern p)
    {
        module = m;
        pattern = p;
        CursorChannel = Math.Min(CursorChannel, Math.Max(0, ChannelCount - 1));
        UpdateScroll();
        Invalidate();
    }

    public TrackerPattern Pattern { get { return pattern; } }
    private int ChannelCount { get { return module == null ? 0 : module.UsedChannelCount; } }
    private int RowNoW { get { return charW * 3 + 6; } }
    private int ChannelW { get { return charW * 14 + 10; } }
    /// <summary>머리줄: 색 띠 4 + 이름 + 음량 막대 6.</summary>
    private int HeaderH { get { return lineH + 18; } }
    private int VisibleRows { get { return Math.Max(1, (Height - hbar.Height - HeaderH) / lineH); } }
    private int VisibleChannels { get { return Math.Max(1, (Width - vbar.Width - RowNoW) / ChannelW); } }

    private void UpdateScroll()
    {
        int vr = VisibleRows, vc = VisibleChannels;
        vbar.Minimum = 0; vbar.Maximum = TrackerPattern.Rows - 1; vbar.LargeChange = vr;
        firstRow = Math.Max(0, Math.Min(firstRow, TrackerPattern.Rows - vr));
        vbar.Value = Math.Max(0, Math.Min(vbar.Maximum, firstRow));
        hbar.Minimum = 0; hbar.Maximum = Math.Max(0, ChannelCount - 1); hbar.LargeChange = vc;
        firstChannel = Math.Max(0, Math.Min(firstChannel, Math.Max(0, ChannelCount - vc)));
        hbar.Value = Math.Max(0, Math.Min(hbar.Maximum, firstChannel));
        hbar.Visible = ChannelCount > vc;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateScroll(); }

    public void EnsureVisible(int row, bool center)
    {
        int vr = VisibleRows;
        if (center) firstRow = row - vr / 2;
        else if (row < firstRow) firstRow = row;
        else if (row >= firstRow + vr) firstRow = row - vr + 1;
        int vc = VisibleChannels;
        if (CursorChannel < firstChannel) firstChannel = CursorChannel;
        else if (CursorChannel >= firstChannel + vc) firstChannel = CursorChannel - vc + 1;
        UpdateScroll();
        Invalidate();
    }

    // ---------------------------------------------------------------- 그리기

    public static string NoteText(TrackerCell c)
    {
        if (c.Note == TrackerCell.NoNote) return "...";
        if (c.Note == TrackerCell.NoteOff) return "^^ ";
        return NoteNames[Math.Min(11, c.Note & 15)] + (c.Note >> 4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Back);
        if (module == null || pattern == null) return;
        int vr = VisibleRows, vc = VisibleChannels;
        int gridW = Width - vbar.Width;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        int minR = Math.Min(SelStart.Y, SelEnd.Y), maxR = Math.Max(SelStart.Y, SelEnd.Y);
        int minC = Math.Min(SelStart.X, SelEnd.X), maxC = Math.Max(SelStart.X, SelEnd.X);

        // 줄
        for (int i = 0; i < vr && firstRow + i < TrackerPattern.Rows; i++)
        {
            int r = firstRow + i;
            int y = HeaderH + i * lineH;
            Color rowBack = r == PlayRow ? PlayBack : r == CursorRow ? CursorRowBack : r % BarRows == 0 ? BackBar : r % BeatRows == 0 ? BackBeat : Back;
            int shown = Math.Min(vc, ChannelCount - firstChannel);
            using (var b = new SolidBrush(rowBack)) g.FillRectangle(b, RowNoW, y, Math.Min(gridW - RowNoW, shown * ChannelW), lineH);   // 채널 칸까지만
            // 줄 번호 칸 (회색, 박 · 마디는 굵게)
            using (var b = new SolidBrush(r == PlayRow ? PlayBack : RowNoBack)) g.FillRectangle(b, 0, y, RowNoW - 1, lineH);
            TextRenderer.DrawText(g, r.ToString("00"), font, new Point(4, y + 1), r % BeatRows == 0 ? Color.Black : RowNoFg, flags);
            for (int k = 0; k < vc && firstChannel + k < ChannelCount; k++)
            {
                int c = firstChannel + k;
                int x = RowNoW + k * ChannelW;
                if (SelStart.X >= 0 && c >= minC && c <= maxC && r >= minR && r <= maxR)
                    using (var sb = new SolidBrush(SelBack)) g.FillRectangle(sb, x, y, ChannelW - 1, lineH);
                var cell = pattern[r, c];
                bool cur = r == CursorRow && c == CursorChannel;
                if (cur)
                {
                    int fx = x + 4 + FieldCol[CursorField] * charW;
                    using (var cb = new SolidBrush(CursorBack)) g.FillRectangle(cb, fx - 1, y, FieldWidth[CursorField] * charW + 2, lineH);
                }
                Draw(g, x + 4, y + 1, cell, module.IsChannelUsed(c), cur ? CursorField : -1);
            }
        }
        // 세로 선: 줄 번호 | 채널 사이
        using (var p = new Pen(ColumnLine))
        {
            int bottom = HeaderH + Math.Min(vr, TrackerPattern.Rows - firstRow) * lineH;
            g.DrawLine(p, RowNoW - 1, HeaderH, RowNoW - 1, bottom);
            for (int k = 0; k < vc && firstChannel + k < ChannelCount; k++)
            {
                int x = RowNoW + (k + 1) * ChannelW - 1;
                g.DrawLine(p, x, HeaderH, x, bottom);
            }
        }
        DrawHeader(g, vc, gridW);
    }

    /// <summary>머리줄 (OpenMPT): 회색 바탕, 채널 색 파스텔 띠, "1: L1" 이름, 그 아래 초록 음량 막대.</summary>
    private void DrawHeader(Graphics g, int vc, int gridW)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        using (var hb = new SolidBrush(HeadBack)) g.FillRectangle(hb, 0, 0, gridW, HeaderH);
        TextRenderer.DrawText(g, "#" + (Math.Max(0, PatternNumber)), font, new Rectangle(0, 4, RowNoW - 1, HeaderH - 4), RowNoFg, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        for (int k = 0; k < vc && firstChannel + k < ChannelCount; k++)
        {
            int c = firstChannel + k;
            int x = RowNoW + k * ChannelW;
            bool used = module.IsChannelUsed(c);
            Color strip = used ? Shell.VoiceColors.Get(c) : Dim;
            using (var b = new SolidBrush(Blend(strip, Color.White, 0.45f))) g.FillRectangle(b, x, 0, ChannelW - 1, 4);
            string h = (c + 1) + ": " + module.ChannelLabel(c);
            TextRenderer.DrawText(g, h, font, new Rectangle(x, 5, ChannelW - 1, lineH), !used ? Dim : c == CursorChannel ? Color.Black : RowNoFg,
                flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            // 음량 막대: 가운데 칸 나눈 초록 막대
            int mw = Math.Min(ChannelW - 20, 64), mx = x + (ChannelW - mw) / 2, my = 6 + lineH;
            const int seg = 3, gap = 1;
            int n = mw / (seg + gap);
            int on = used ? (int)Math.Round(Math.Max(0, Math.Min(1, c < Levels.Length ? Levels[c] : 0)) * n) : 0;
            for (int s = 0; s < n; s++)
                using (var b = new SolidBrush(s < on ? MeterOn : MeterOff)) g.FillRectangle(b, mx + s * (seg + gap), my, seg, 6);
            using (var p = new Pen(HeadLine)) g.DrawLine(p, x + ChannelW - 1, 0, x + ChannelW - 1, HeaderH);
        }
        using (var p = new Pen(HeadLine)) g.DrawLine(p, 0, HeaderH - 1, gridW, HeaderH - 1);
    }

    /// <summary>머리줄 왼쪽 "#n" (지금 패턴 번호).</summary>
    public int PatternNumber = -1;

    private static Color Blend(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    private void Draw(Graphics g, int x, int y, TrackerCell c, bool used, int cursorField)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        Color dim = Dim;
        string note = NoteText(c);
        string ins = c.Instrument == 0 ? ".." : c.Instrument.ToString("00");
        string vol = c.Volume == TrackerCell.NoVolume ? ".." : c.Volume.ToString("00");
        string cmd = c.Command == 0 ? "." : ((char)('A' + c.Command - 1)).ToString();
        string inf = c.Command == 0 && c.Info == 0 ? ".." : c.Info.ToString("X2");
        Color fn = !used ? dim : c.Note == TrackerCell.NoNote ? dim : NoteFg;
        Color fi = !used || c.Instrument == 0 ? dim : InsFg;
        Color fv = !used || c.Volume == TrackerCell.NoVolume ? dim : VolFg;
        Color fc = !used || c.Command == 0 ? dim : CmdFg;
        DrawField(g, note, x, y, fn, cursorField == 0, flags);
        DrawField(g, ins.Substring(0, 1), x + 4 * charW, y, fi, cursorField == 1, flags);
        DrawField(g, ins.Substring(1, 1), x + 5 * charW, y, fi, cursorField == 2, flags);
        DrawField(g, c.Volume == TrackerCell.NoVolume ? "." : "v", x + 7 * charW, y, fv, false, flags);
        DrawField(g, vol.Substring(0, 1), x + 8 * charW, y, fv, cursorField == 3, flags);
        DrawField(g, vol.Substring(1, 1), x + 9 * charW, y, fv, cursorField == 4, flags);
        DrawField(g, cmd, x + 11 * charW, y, fc, cursorField == 5, flags);
        DrawField(g, inf.Substring(0, 1), x + 12 * charW, y, fc, cursorField == 6, flags);
        DrawField(g, inf.Substring(1, 1), x + 13 * charW, y, fc, cursorField == 7, flags);
    }

    private void DrawField(Graphics g, string s, int x, int y, Color fg, bool cursor, TextFormatFlags flags)
    {
        for (int i = 0; i < s.Length; i++)
            TextRenderer.DrawText(g, s.Substring(i, 1), font, new Point(x + i * charW, y), cursor ? Color.White : fg, flags);
    }

    // ---------------------------------------------------------------- 마우스

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (module == null || e.Y < HeaderH || e.X < RowNoW) return;
        int r = firstRow + (e.Y - HeaderH) / lineH;
        int k = (e.X - RowNoW) / ChannelW;
        int c = firstChannel + k;
        if (r >= TrackerPattern.Rows || c >= ChannelCount) return;
        int col = (e.X - RowNoW - k * ChannelW - 4) / charW;
        int f = 0;
        for (int i = 0; i < Fields; i++) if (col >= FieldCol[i]) f = i;
        if ((ModifierKeys & Keys.Shift) != 0) { if (SelStart.X < 0) SelStart = new Point(CursorChannel, CursorRow); SelEnd = new Point(c, r); }
        else SelStart = new Point(-1, -1);
        CursorRow = r; CursorChannel = c; CursorField = f;
        Moved();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button != MouseButtons.Left || module == null || e.Y < HeaderH || e.X < RowNoW) return;
        int r = Math.Min(TrackerPattern.Rows - 1, firstRow + (e.Y - HeaderH) / lineH);
        int c = Math.Min(ChannelCount - 1, firstChannel + (e.X - RowNoW) / ChannelW);
        if (r == CursorRow && c == CursorChannel && SelStart.X < 0) return;
        if (SelStart.X < 0) SelStart = new Point(CursorChannel, CursorRow);
        SelEnd = new Point(c, r);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        firstRow -= Math.Sign(e.Delta) * 4;
        UpdateScroll();
        Invalidate();
    }

    private void Moved()
    {
        EnsureVisible(CursorRow, false);
        if (CursorMoved != null) CursorMoved();
    }

    // ---------------------------------------------------------------- 키

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right: case Keys.Tab: case Keys.Enter: return true;
        }
        return base.IsInputKey(keyData);
    }

    /// <summary>편집 키를 처리했으면 true.</summary>
    public bool HandleKey(Keys keyData)
    {
        if (module == null || pattern == null) return false;
        Keys key = keyData & Keys.KeyCode;
        bool shift = (keyData & Keys.Shift) != 0, ctrl = (keyData & Keys.Control) != 0, alt = (keyData & Keys.Alt) != 0;

        // 이동
        switch (key)
        {
            case Keys.Up: MoveRow(-1, shift); return true;
            case Keys.Down: MoveRow(1, shift); return true;
            case Keys.PageUp: MoveRow(-16, shift); return true;
            case Keys.PageDown: MoveRow(16, shift); return true;
            case Keys.Left:
                if (ctrl) { MoveChannel(-1, shift); return true; }
                if (CursorField > 0) CursorField--; else if (CursorChannel > 0) { CursorChannel--; CursorField = Fields - 1; }
                Moved(); return true;
            case Keys.Right:
                if (ctrl) { MoveChannel(1, shift); return true; }
                if (CursorField < Fields - 1) CursorField++; else if (CursorChannel < ChannelCount - 1) { CursorChannel++; CursorField = 0; }
                Moved(); return true;
            case Keys.Tab: MoveChannel(shift ? -1 : 1, false); CursorField = 0; return true;
            case Keys.Home:
                if (CursorField != 0) CursorField = 0; else if (CursorRow != 0) CursorRow = 0; else CursorChannel = 0;
                Moved(); return true;
            case Keys.End:
                if (CursorField != Fields - 1) CursorField = Fields - 1; else if (CursorRow != 63) CursorRow = 63; else CursorChannel = ChannelCount - 1;
                Moved(); return true;
        }
        if (ctrl && key == Keys.C) { Copy(false); return true; }
        if (ctrl && key == Keys.X) { Copy(true); return true; }
        if (ctrl && key == Keys.V) { Paste(); return true; }
        if (ctrl && key == Keys.A) { SelStart = new Point(0, 0); SelEnd = new Point(ChannelCount - 1, 63); Invalidate(); return true; }
        if (alt && key == Keys.Q) { TransposeArea(1); return true; }
        if (alt && key == Keys.A) { TransposeArea(-1); return true; }
        if (alt && key == Keys.L) { SelStart = new Point(CursorChannel, 0); SelEnd = new Point(CursorChannel, 63); Invalidate(); return true; }
        if (alt && key == Keys.U) { SelStart = new Point(-1, -1); Invalidate(); return true; }
        if (ctrl || alt) return false;
        if (shift && key == Keys.OemPeriod) { SetInstrument(CurrentInstrument + 1); return true; }   // >
        if (shift && key == Keys.Oemcomma) { SetInstrument(CurrentInstrument - 1); return true; }    // <

        switch (key)
        {
            case Keys.Divide: case Keys.OemQuestion: SetOctave(BaseOctave - 1); return true;
            case Keys.Multiply: SetOctave(BaseOctave + 1); return true;
            case Keys.Add: if (PatternStep != null) PatternStep(shift ? 4 : 1); return true;
            case Keys.Subtract: if (PatternStep != null) PatternStep(shift ? -4 : -1); return true;
            case Keys.OemPeriod: case Keys.Decimal: ClearField(); return true;
            case Keys.Delete: if (SelStart.X >= 0) { ClearArea(); return true; } DeleteRow(shift); return true;
            case Keys.Insert: InsertRow(shift); return true;
            case Keys.Enter: PickUp(); return true;
            case Keys.Escape: if (SelStart.X >= 0) { SelStart = new Point(-1, -1); Invalidate(); return true; } return false;
        }

        if (CursorField == 0)
        {
            if (key == Keys.D1) { Edit(c => { c.Note = TrackerCell.NoteOff; c.Instrument = 0; return c; }); Advance(); return true; }
            if (key == Keys.D4) { var c0 = pattern[CursorRow, CursorChannel]; if (c0.HasNote && PlayNote != null) PlayNote(c0.Instrument != 0 ? c0.Instrument : CurrentInstrument, c0.Note, CursorChannel); return true; }
            if (key == Keys.D8) { if (PlayRowRequested != null) PlayRowRequested(CursorRow); return true; }
            int semi;
            if (!shift && PianoKeys.TryGetValue(key, out semi))
            {
                int n = BaseOctave * 12 + semi;
                int maxOct = module.Kind == TrackerKind.Stm ? 4 : 7;
                n = Math.Max(0, Math.Min((maxOct + 1) * 12 - 1, n));
                byte note = TrackerCell.MakeNote(n / 12, n % 12);
                int ins = CurrentInstrument;
                Edit(c => { c.Note = note; c.Instrument = (byte)ins; return c; });
                if (PlayNote != null) PlayNote(ins, note, CursorChannel);
                Advance();
                return true;
            }
            return false;
        }
        int digit = DigitOf(key);
        if (CursorField == 1 || CursorField == 2)   // 악기 (10진 두 자리)
        {
            if (digit < 0 || digit > 9) return false;
            int max = module.Kind == TrackerKind.Stm ? 31 : 99;
            Edit(c =>
            {
                int v = c.Instrument;
                v = CursorField == 1 ? digit * 10 + v % 10 : (v / 10) * 10 + digit;
                c.Instrument = (byte)Math.Min(max, v);
                return c;
            });
            if (CursorField == 1) { CursorField = 2; Invalidate(); } else { CursorField = 1; Advance(); }
            return true;
        }
        if (CursorField == 3 || CursorField == 4)   // 볼륨 (10진 0~64)
        {
            if (digit < 0 || digit > 9) return false;
            Edit(c =>
            {
                int v = c.Volume == TrackerCell.NoVolume ? 0 : c.Volume;
                v = CursorField == 3 ? digit * 10 + v % 10 : (v / 10) * 10 + digit;
                c.Volume = (byte)Math.Min(64, v);
                return c;
            });
            if (CursorField == 3) { CursorField = 4; Invalidate(); } else { CursorField = 3; Advance(); }
            return true;
        }
        if (CursorField == 5)   // 명령 A~Z
        {
            if (key < Keys.A || key > Keys.Z) return false;
            int cmd = key - Keys.A + 1;
            if (module.Kind == TrackerKind.Stm && cmd > 10) return true;   // ST2: A~J만
            Edit(c => { c.Command = (byte)cmd; return c; });
            CursorField = 6; Invalidate();
            return true;
        }
        if (CursorField == 6 || CursorField == 7)   // 인포 (16진)
        {
            int h = HexOf(key);
            if (h < 0) return false;
            Edit(c => { c.Info = (byte)(CursorField == 6 ? (h << 4) | (c.Info & 15) : (c.Info & 0xF0) | h); return c; });
            if (CursorField == 6) { CursorField = 7; Invalidate(); } else { CursorField = 6; Advance(); }
            return true;
        }
        return false;
    }

    private static int DigitOf(Keys k)
    {
        if (k >= Keys.D0 && k <= Keys.D9) return k - Keys.D0;
        if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return k - Keys.NumPad0;
        return -1;
    }

    private static int HexOf(Keys k)
    {
        int d = DigitOf(k);
        if (d >= 0) return d;
        if (k >= Keys.A && k <= Keys.F) return 10 + (k - Keys.A);
        return -1;
    }

    private void SetOctave(int o)
    {
        int max = module != null && module.Kind == TrackerKind.Stm ? 4 : 7;
        BaseOctave = Math.Max(0, Math.Min(max, o));
        if (OctaveChanged != null) OctaveChanged(BaseOctave);
        Invalidate();
    }

    public void SetInstrument(int i)
    {
        int max = module == null ? 1 : Math.Max(1, module.Kind == TrackerKind.Stm ? 31 : Math.Max(1, module.Samples.Count));
        CurrentInstrument = Math.Max(1, Math.Min(max, i));
        if (InstrumentChanged != null) InstrumentChanged(CurrentInstrument);
    }

    private void MoveRow(int d, bool select)
    {
        if (select && SelStart.X < 0) SelStart = new Point(CursorChannel, CursorRow);
        CursorRow = Math.Max(0, Math.Min(63, CursorRow + d));
        if (select) SelEnd = new Point(CursorChannel, CursorRow); else SelStart = new Point(-1, -1);
       
        Moved();
    }

    private void MoveChannel(int d, bool select)
    {
        if (select && SelStart.X < 0) SelStart = new Point(CursorChannel, CursorRow);
        CursorChannel = Math.Max(0, Math.Min(ChannelCount - 1, CursorChannel + d));
        if (select) SelEnd = new Point(CursorChannel, CursorRow); else SelStart = new Point(-1, -1);
        Moved();
    }

    private void Advance()
    {
        CursorRow = Math.Min(63, CursorRow + Skip);
        Moved();
    }

    private void Edit(Func<TrackerCell, TrackerCell> f)
    {
        if (BeforeEdit != null) BeforeEdit();
        pattern[CursorRow, CursorChannel] = f(pattern[CursorRow, CursorChannel]);
        Changed();
    }

    private void Changed()
    {
        Invalidate();
        if (PatternEdited != null) PatternEdited();
    }

    private void ClearField()
    {
        if (SelStart.X >= 0) { ClearArea(); return; }
        Edit(c =>
        {
            if (CursorField == 0) { c.Note = TrackerCell.NoNote; c.Instrument = 0; }
            else if (CursorField <= 2) c.Instrument = 0;
            else if (CursorField <= 4) c.Volume = TrackerCell.NoVolume;
            else { c.Command = 0; c.Info = 0; }
            return c;
        });
        Advance();
    }

    private void PickUp()
    {
        var c = pattern[CursorRow, CursorChannel];
        if (c.Instrument != 0) SetInstrument(c.Instrument);
        if (c.HasNote) SetOctave(c.Octave);
    }

    private void InsertRow(bool all)
    {
        if (BeforeEdit != null) BeforeEdit();
        for (int ch = all ? 0 : CursorChannel; ch <= (all ? ChannelCount - 1 : CursorChannel); ch++)
        {
            for (int r = 63; r > CursorRow; r--) pattern[r, ch] = pattern[r - 1, ch];
            pattern[CursorRow, ch] = TrackerCell.Empty;
        }
        Changed();
    }

    private void DeleteRow(bool all)
    {
        if (BeforeEdit != null) BeforeEdit();
        for (int ch = all ? 0 : CursorChannel; ch <= (all ? ChannelCount - 1 : CursorChannel); ch++)
        {
            for (int r = CursorRow; r < 63; r++) pattern[r, ch] = pattern[r + 1, ch];
            pattern[63, ch] = TrackerCell.Empty;
        }
        Changed();
    }

    private bool Area(out int c0, out int c1, out int r0, out int r1)
    {
        if (SelStart.X < 0) { c0 = c1 = CursorChannel; r0 = r1 = CursorRow; return false; }
        c0 = Math.Min(SelStart.X, SelEnd.X); c1 = Math.Max(SelStart.X, SelEnd.X);
        r0 = Math.Min(SelStart.Y, SelEnd.Y); r1 = Math.Max(SelStart.Y, SelEnd.Y);
        return true;
    }

    private void Copy(bool cut)
    {
        int c0, c1, r0, r1;
        Area(out c0, out c1, out r0, out r1);
        clipboard = new TrackerCell[c1 - c0 + 1, r1 - r0 + 1];
        for (int c = c0; c <= c1; c++) for (int r = r0; r <= r1; r++) clipboard[c - c0, r - r0] = pattern[r, c];
        if (cut) ClearArea();
    }

    private void ClearArea()
    {
        int c0, c1, r0, r1;
        Area(out c0, out c1, out r0, out r1);
        if (BeforeEdit != null) BeforeEdit();
        for (int c = c0; c <= c1; c++) for (int r = r0; r <= r1; r++) pattern[r, c] = TrackerCell.Empty;
        Changed();
    }

    private void Paste()
    {
        if (clipboard == null) return;
        if (BeforeEdit != null) BeforeEdit();
        for (int c = 0; c < clipboard.GetLength(0); c++)
            for (int r = 0; r < clipboard.GetLength(1); r++)
            {
                int cc = CursorChannel + c, rr = CursorRow + r;
                if (cc < ChannelCount && rr < 64) pattern[rr, cc] = clipboard[c, r];
            }
        Changed();
    }

    private void TransposeArea(int semis)
    {
        int c0, c1, r0, r1;
        Area(out c0, out c1, out r0, out r1);
        if (BeforeEdit != null) BeforeEdit();
        int maxOct = module.Kind == TrackerKind.Stm ? 4 : 7;
        for (int c = c0; c <= c1; c++)
            for (int r = r0; r <= r1; r++)
            {
                var cell = pattern[r, c];
                if (!cell.HasNote) continue;
                int n = Math.Max(0, Math.Min((maxOct + 1) * 12 - 1, cell.Octave * 12 + cell.Semitone + semis));
                cell.Note = TrackerCell.MakeNote(n / 12, n % 12);
                pattern[r, c] = cell;
            }
        Changed();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) font.Dispose();
        base.Dispose(disposing);
    }
}
