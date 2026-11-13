using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AdlibComposer.App.Controls;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Views;

/// <summary>Visual Composer 화면: 도구/보이스 선택줄 + Instr. 줄 + 건반 악보 + 스크롤바 + 줌 표시(1:1).</summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class ComposerView : UserControl
{
    private const int TopHeight = 56;

    private readonly Workspace ws;
    private readonly PianoRollControl roll = new PianoRollControl();
    private readonly VScrollBar vScroll = new VScrollBar();
    private readonly HScrollBar hScroll = new HScrollBar();
    private readonly Panel topPanel = new Panel();
    // 라디오 버튼 그룹은 부모 컨테이너 단위이므로 도구/보이스 버튼을 서로 다른 패널에 둔다.
    private readonly Panel toolPanel = new Panel();
    private readonly Panel voicePanel = new Panel();
    private readonly Label zoomLabel = new Label();
    private readonly Label titleLabel = new Label();
    private readonly Label noteLabel = new Label();
    private readonly Label instrCaption = new Label();
    private readonly Label instrLabel = new Label();
    private readonly RadioButton penButton = new RadioButton();
    private readonly RadioButton selButton = new RadioButton();
    private readonly List<RadioButton> voiceButtons = new List<RadioButton>();
    private bool syncing;
    private bool building;
    private int lengthIndex = 4;   // 1/16
    private readonly List<RadioButton> lengthButtons = new List<RadioButton>();
    private readonly Panel lengthPanel = new Panel();
    private readonly Label lengthCaption = new Label();

    public event Action SongModified;
    public event Action<int> HoverNoteChanged;

    public ComposerView(Workspace ws)
    {
        this.ws = ws;
        BackColor = Theme.Back;
        DoubleBuffered = true;

        BuildTopPanel();
        BuildLengthButtons();
        Controls.Add(roll);
        Controls.Add(vScroll);
        Controls.Add(hScroll);
        Controls.Add(zoomLabel);
        Controls.Add(topPanel);

        zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
        zoomLabel.BorderStyle = BorderStyle.FixedSingle;
        zoomLabel.Font = Theme.UiFont;
        zoomLabel.BackColor = Color.White;

        roll.BeforeEdit += delegate { ws.PushUndo(); };
        roll.SongEdited += OnRollEdited;
        roll.ViewChanged += UpdateScrollBars;
        roll.EditCursorChanged += UpdateInstrLabel;
        roll.TimbreRequested += delegate (int tick) { EditBandAt(tick); };
        roll.BandDeleteRequested += DeleteBandEventNear;
        roll.HoverNote += delegate (int n) { noteLabel.Text = n >= 0 ? NoteHelper.Name(n) : ""; if (HoverNoteChanged != null) HoverNoteChanged(n); };
        roll.PreviewOn += PreviewNote;
        roll.PreviewOff += delegate { ws.Audio.PreviewNoteOff(PreviewVoice); };

        hScroll.ValueChanged += delegate { if (syncing) return; roll.ScrollTick = hScroll.Value; roll.Invalidate(); };
        vScroll.ValueChanged += delegate
        {
            if (syncing) return;
            roll.TopNote = Song.MaxNote - vScroll.Value;
            roll.ClampView();
            roll.Invalidate();
        };

        BindSong();
        roll.KeyboardBase = keyboardBase;
    }

    public PianoRollControl Roll { get { return roll; } }

    /// <summary>Note 페이지에서 음표를 클릭했을 때: 그 보이스로 바꾸고 커서를 옮겨 그 노트를 선택한다.</summary>
    public void GoTo(int voice, int tick, int pitch)
    {
        if (voice < 0 || voice >= ws.Song.VoiceCount) return;
        if (ShowAllVoices || roll.CurrentVoice != voice) SelectVoice(voice);
        var n = ws.Song.Voices[voice].FindNote(tick, pitch);
        roll.Selection.Clear();
        if (n != null) roll.Selection.Add(n);
        roll.MoveCursor(n != null ? n.StartTick : tick);
        roll.MoveCursorNote(pitch);
        roll.Invalidate();
        Say((n != null ? "Note " + NoteHelper.Name(pitch) : "position") + "  (from Note page)");
    }

    /// <summary>연필(입력) / I(선택, 원본 Edit → Edit) 도구.</summary>
    public RollTool Tool
    {
        get { return roll.Tool; }
        set { if (value == RollTool.Select) selButton.Checked = true; else penButton.Checked = true; }
    }

    // ------------------------------------------------------------ 구성

    private void BuildTopPanel()
    {
        topPanel.BackColor = Theme.Back;
        topPanel.Height = TopHeight;

        var symbol = new Font("Segoe UI Symbol", 11f, FontStyle.Bold);
        StyleToolButton(penButton, "\u270E", 4, symbol);
        StyleToolButton(selButton, "I", 38, Theme.UiFont);
        penButton.Checked = true;
        penButton.CheckedChanged += delegate { if (penButton.Checked) roll.Tool = RollTool.Pencil; };
        selButton.CheckedChanged += delegate { if (selButton.Checked) roll.Tool = RollTool.Select; };
        toolPanel.SetBounds(0, 0, 76, 32);
        toolPanel.Controls.Add(penButton);
        toolPanel.Controls.Add(selButton);
        topPanel.Controls.Add(toolPanel);
        voicePanel.SetBounds(78, 0, 480, 32);
        topPanel.Controls.Add(voicePanel);

        noteLabel.SetBounds(0, 4, 40, 24);
        noteLabel.BorderStyle = BorderStyle.FixedSingle;
        noteLabel.TextAlign = ContentAlignment.MiddleCenter;
        noteLabel.Font = Theme.UiFont;
        noteLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        topPanel.Controls.Add(noteLabel);

        titleLabel.Font = Theme.UiFont;
        titleLabel.TextAlign = ContentAlignment.MiddleRight;
        titleLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        titleLabel.Width = 180; titleLabel.Height = 24; titleLabel.Top = 4;
        topPanel.Controls.Add(titleLabel);

        instrCaption.Text = "Instr.";
        instrCaption.Font = Theme.UiFont;
        instrCaption.SetBounds(4, 33, 48, 20);
        instrCaption.TextAlign = ContentAlignment.MiddleLeft;
        topPanel.Controls.Add(instrCaption);

        instrLabel.Font = Theme.UiFont;
        instrLabel.BorderStyle = BorderStyle.FixedSingle;
        instrLabel.BackColor = Color.White;
        instrLabel.SetBounds(56, 30, 260, 22);
        instrLabel.TextAlign = ContentAlignment.MiddleLeft;
        instrLabel.DoubleClick += delegate { EditBandAt(roll.CursorTick); };
        topPanel.Controls.Add(instrLabel);
    }

    private static void StyleToolButton(RadioButton b, string text, int x, Font font)
    {
        b.Appearance = Appearance.Button;
        b.FlatStyle = FlatStyle.Flat;
        b.Text = text;
        b.Font = font;
        b.SetBounds(x, 3, 32, 26);
        b.TextAlign = ContentAlignment.MiddleCenter;
        b.FlatAppearance.BorderColor = Color.Black;
        b.FlatAppearance.CheckedBackColor = Color.Black;
        b.ForeColor = Color.Black;
        b.CheckedChanged += delegate { b.ForeColor = b.Checked ? Color.White : Color.Black; };
    }

    private void RebuildVoiceButtons()
    {
        building = true;
        foreach (var b in voiceButtons) { voicePanel.Controls.Remove(b); b.Dispose(); }
        voiceButtons.Clear();

        string[] labels = ws.Song.Labels;
        bool compact = labels.Length > Song.MaxVoices;   // SOP 20보이스: 좁은 버튼
        int x = 2;
        for (int i = 0; i <= labels.Length; i++)
        {
            bool all = i == labels.Length;
            var rb = new RadioButton
            {
                Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, Text = all ? "All" : labels[i],
                Tag = all ? -1 : i, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.UiFont
            };
            int w = all ? 38 : compact ? (labels[i].Length > 1 ? 28 : 24) : (labels[i].Length > 1 ? 34 : 28);
            rb.SetBounds(x, 4, w, 24);
            x += w + 2;
            rb.FlatAppearance.BorderColor = Color.Black;
            rb.FlatAppearance.CheckedBackColor = Color.Black;
            rb.CheckedChanged += OnVoiceChecked;
            voiceButtons.Add(rb);
            voicePanel.Controls.Add(rb);
        }
        voicePanel.Width = Math.Max(480, x + 2);
        if (roll.CurrentVoice >= ws.Song.VoiceCount) roll.CurrentVoice = 0;
        foreach (var b in voiceButtons)
            if (ShowAllVoices ? (int)b.Tag == -1 : (int)b.Tag == roll.CurrentVoice) { b.Checked = true; b.ForeColor = Color.White; }
        building = false;
        PositionTitle(x);
    }

    private void PositionTitle(int rightOfVoices)
    {
        noteLabel.Left = topPanel.Width - noteLabel.Width - 6;
        titleLabel.Left = noteLabel.Left - titleLabel.Width - 6;
    }

    private void OnVoiceChecked(object sender, EventArgs e)
    {
        var rb = (RadioButton)sender;
        rb.ForeColor = rb.Checked ? Color.White : Color.Black;
        if (building || !rb.Checked) return;
        int tag = (int)rb.Tag;
        if (tag < 0) roll.ShowAll = true;
        else { roll.ShowAll = false; roll.CurrentVoice = tag; }
        roll.Selection.Clear();
        ApplyVoiceRange();
        roll.Invalidate();
        UpdateInstrLabel();
    }

    /// <summary>드럼 음역 기본값: 칩 노트 24 = ROL 36 (C2), adlib.c TOM_PITCH. 드럼 소리는 낮은 음에서 난다.</summary>
    public const int DrumBaseNote = 36;
    private int melodicKeyboardBase = Song.MiddleC;
    private bool drumRange;

    private bool IsDrumVoice(int voice) { return ws.Song.IsDrumVoice(voice); }

    /// <summary>
    /// 리듬 모드에서 BD/SD/TO/CY/HH 보이스를 고르면 건반 키·음높이 커서를 드럼 음역(C2)으로,
    /// 멜로딕 보이스로 돌아오면 이전 옥타브로 되돌린다.
    /// </summary>
    private void ApplyVoiceRange()
    {
        bool drum = !roll.ShowAll && IsDrumVoice(roll.CurrentVoice);
        if (drum == drumRange) return;
        drumRange = drum;
        if (drum)
        {
            melodicKeyboardBase = keyboardBase;
            SetKeyboardBase(DrumBaseNote);
            roll.MoveCursorNote(DrumBaseNote);
        }
        else
        {
            SetKeyboardBase(melodicKeyboardBase);
            roll.MoveCursorNote(Math.Max(Song.MinNote, Math.Min(Song.MaxNote, melodicKeyboardBase)));
        }
    }

    // ------------------------------------------------------------ 공개 기능

    public bool ShowAllVoices
    {
        get { return roll.ShowAll; }
        set
        {
            roll.ShowAll = value;
            building = true;
            foreach (var b in voiceButtons)
            {
                bool on = value ? (int)b.Tag == -1 : (int)b.Tag == roll.CurrentVoice;
                b.Checked = on; b.ForeColor = on ? Color.White : Color.Black;
            }
            building = false;
            roll.Invalidate();
        }
    }

    /// <summary>
    /// 노트 길이 = 스냅 = 키보드 스텝. 박(4분음표) 기준 길이: 온음표 4박 ... 64분음표 1/16박, 마지막은 1틱.
    /// 박당 틱이 작은 곡(예: 4)에서는 짧은 길이가 1틱으로 모인다.
    /// </summary>
    public static readonly string[] LengthNames = { "1", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64", "tick" };
    public static readonly double[] LengthBeats = { 4, 2, 1, 0.5, 0.25, 0.125, 0.0625, 0 };

    public int LengthIndex
    {
        get { return lengthIndex; }
        set
        {
            lengthIndex = Math.Max(0, Math.Min(LengthNames.Length - 1, value));
            ApplyLength();
            Say("Length " + LengthNames[lengthIndex] + " = " + roll.SnapTicks + " tick" + (roll.SnapTicks == 1 ? "" : "s"));
        }
    }

    public static int LengthTicks(int index, int tickBeat)
    {
        double beats = LengthBeats[Math.Max(0, Math.Min(LengthBeats.Length - 1, index))];
        return Math.Max(1, (int)Math.Round(Math.Max(1, tickBeat) * beats));
    }

    private void ApplyLength()
    {
        roll.SnapTicks = LengthTicks(lengthIndex, ws.Song.TickBeat);
        foreach (var b in lengthButtons)
        {
            bool on = (int)b.Tag == lengthIndex;
            if (b.Checked != on) b.Checked = on;
            b.ForeColor = on ? Color.White : Color.Black;
            // 이 곡의 박당 틱으로 표현할 수 없는 길이(이전 길이와 같은 틱)는 흐리게
            int idx = (int)b.Tag;
            bool distinct = idx == 0 || LengthTicks(idx, ws.Song.TickBeat) != LengthTicks(idx - 1, ws.Song.TickBeat);
            if (!on) b.ForeColor = distinct ? Color.Black : Color.Silver;
        }
        roll.Invalidate();
    }

    private void BuildLengthButtons()
    {
        lengthCaption.Text = "Length";
        lengthCaption.Font = Theme.UiFont;
        lengthCaption.TextAlign = ContentAlignment.MiddleRight;
        lengthCaption.SetBounds(324, 33, 56, 20);
        topPanel.Controls.Add(lengthCaption);
        lengthPanel.SetBounds(384, 30, 340, 24);
        topPanel.Controls.Add(lengthPanel);
        int x = 0;
        for (int i = 0; i < LengthNames.Length; i++)
        {
            var rb = new RadioButton
            {
                Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, Text = LengthNames[i], Tag = i,
                TextAlign = ContentAlignment.MiddleCenter, Font = Theme.SmallFont
            };
            int w = LengthNames[i].Length > 3 ? 40 : 34;
            rb.SetBounds(x, 0, w, 22);
            x += w + 2;
            rb.FlatAppearance.BorderColor = Color.Black;
            rb.FlatAppearance.CheckedBackColor = Color.Black;
            rb.TabStop = false;
            rb.CheckedChanged += delegate { if (rb.Checked && (int)rb.Tag != lengthIndex) LengthIndex = (int)rb.Tag; else ApplyLength(); };
            lengthButtons.Add(rb);
            lengthPanel.Controls.Add(rb);
        }
    }

    /// <summary>ws.Song이 바뀌었을 때(새 곡/열기/실행취소) 호출.</summary>
    public void BindSong()
    {
        roll.Song = ws.Song;
        ApplyLength();
        roll.ClampView();
        RebuildVoiceButtons();
        RefreshLabels();
        UpdateScrollBars();
        roll.Invalidate();
    }

    public void RefreshLabels()
    {
        titleLabel.Text = ws.Song.Title;
        UpdateInstrLabel();
    }

    // ------------------------------------------------------------ 원본 Display 메뉴: 위쪽 행 트랙 / 입력 값 / 격자

    /// <summary>위쪽 행에 넣을 값 (원본 Display 메뉴 항목 옆 숫자). 기본: Tempo 1.0, Volume 0.75, Pitch 1.0.</summary>
    public double TempoValue { get; set; } = 1.0;
    public double VolumeValue { get; set; } = 0.75;
    public double PitchValue { get; set; } = 1.0;
    /// <summary>위쪽 행 Pan에 넣을 값 (SOP: 0 = R, 1 = C, 2 = L).</summary>
    public int PanValue { get; set; } = PanEvent.Center;

    public BandTrack Band
    {
        get { return roll.Band; }
        set { roll.Band = value; UpdateInstrLabel(); roll.Invalidate(); }
    }

    /// <summary>격자 크기 = 박당 화면 폭 + 건반 한 칸 높이. 0 Large, 1 Medium(1:1), 2 Small.</summary>
    public static readonly float[] GridPixelsPerBeat = { 80f, 40f, 20f };
    public static readonly int[] GridRowHeight = { 12, 8, 5 };

    public int GridSize
    {
        get
        {
            for (int i = 0; i < GridPixelsPerBeat.Length; i++)
                if (Math.Abs(roll.PixelsPerBeat - GridPixelsPerBeat[i]) < 0.5f && roll.RowHeight == GridRowHeight[i]) return i;
            return -1;
        }
        set
        {
            int i = Math.Max(0, Math.Min(2, value));
            int center = roll.TopNote - roll.VisibleRows / 2;    // 화면 가운데 음을 유지
            roll.PixelsPerBeat = GridPixelsPerBeat[i];
            roll.RowHeight = GridRowHeight[i];
            roll.TopNote = center + roll.VisibleRows / 2;
            roll.ClampView();
            UpdateScrollBars();
            roll.Invalidate();
        }
    }

    // ------------------------------------------------------------ 키보드 조작

    private readonly Dictionary<Keys, int> heldKeys = new Dictionary<Keys, int>();
    private int keyboardBase = Song.MiddleC;

    /// <summary>상태 표시줄에 보여 줄 안내 (키보드 옥타브 등).</summary>
    public event Action<string> Message;

    private void Say(string text) { if (Message != null) Message(text); }

    public string KeyboardRangeText
    {
        get { return "Keys Z/Q = " + NoteHelper.Name(keyboardBase) + " / " + NoteHelper.Name(keyboardBase + 12); }
    }

    private void SetKeyboardBase(int note)
    {
        keyboardBase = Math.Max(Song.MinNote, Math.Min(Song.MaxNote - ComputerKeyboard.Span, note));
        roll.KeyboardBase = keyboardBase;
        roll.EnsureNoteVisible(keyboardBase + 12);
        roll.Invalidate();
        Say(KeyboardRangeText);
    }

    private void SelectVoice(int voice)
    {
        int n = ws.Song.VoiceCount;
        voice = (voice % n + n) % n;
        foreach (var b in voiceButtons)
            if ((int)b.Tag == voice) { b.Checked = true; break; }
        string warn = SopConvert.IsSilencedPartner(ws.Song, ws.Resolve, voice)
            ? "  (silent: voice " + ws.Song.Labels[voice - 3] + " uses a 4-op instrument)" : "";
        Say("Voice " + ws.Song.Labels[voice] + "  " + instrLabel.Text + warn);
    }

    /// <summary>
    /// Composer 키보드 조작. 처리했으면 true. 커서 = (시간, 음높이) 상자, 스텝 = 노트 길이(Length).
    /// ← → / ↑ ↓: 커서 이동 (스텝 / 반음), Ctrl+← →: 마디 단위.
    /// Shift+→ / Shift+←: 커서 자리에 노트 그리기·늘리기 / 줄이기 (선택된 노트가 있으면 그 노트).
    /// Shift+↑ / Shift+↓: 그 노트의 음높이를 반음씩.
    /// 건반 키(Z S X... / Q 2 W...): 소리 + 연필 도구면 그 음으로 노트를 넣고 커서를 앞으로 (이어서 Shift+→로 늘리기).
    /// [ ]: 노트 길이 짧게/길게, PageUp/PageDown: 건반 키 옥타브, Enter: 쉼표, Backspace: 뒤로 지우기,
    /// Esc: 선택 해제, End: 곡 끝, Tab/Shift+Tab: 보이스 바꾸기.
    /// </summary>
    public bool HandleKeyDown(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        Keys mods = keyData & Keys.Modifiers;
        int snap = Math.Max(1, roll.SnapTicks);
        var song = ws.Song;

        if (mods == Keys.None)
        {
            int offset;
            if (ComputerKeyboard.TryGetOffset(key, out offset))
            {
                if (heldKeys.ContainsKey(key)) return true;      // 키 반복 무시
                int note = Math.Max(Song.MinNote, Math.Min(Song.MaxNote, keyboardBase + offset));
                heldKeys[key] = note;
                roll.CursorNote = note;
                if (roll.Tool == RollTool.Pencil && !ws.Audio.IsPlaying)
                {
                    roll.InsertNoteAtCursor(note, snap);   // 넣은 노트가 선택됨 → Shift+→로 바로 늘릴 수 있다
                    roll.MoveCursor(roll.CursorTick + snap);
                }
                roll.EnsureNoteVisible(note);
                PreviewNote(note);
                Say(NoteHelper.Name(note) + (roll.Tool == RollTool.Pencil ? "  entered" : ""));
                return true;
            }
        }

        var active = roll.ActiveNote;
        switch (keyData)
        {
            // ---- 커서 이동 (선택 해제: 다음 Shift+→는 커서 자리에 새 노트)
            case Keys.Left: roll.ClearSelection(); roll.MoveCursor(Math.Max(0, roll.CursorTick - snap)); return true;
            case Keys.Right: roll.ClearSelection(); roll.MoveCursor(roll.CursorTick + snap); return true;
            case Keys.Up: roll.ClearSelection(); roll.MoveCursorNote(roll.CursorNote + 1); Say(NoteHelper.Name(roll.CursorNote)); return true;
            case Keys.Down: roll.ClearSelection(); roll.MoveCursorNote(roll.CursorNote - 1); Say(NoteHelper.Name(roll.CursorNote)); return true;
            case Keys.Control | Keys.Left:
            {
                int m = Math.Max(1, song.MeasureTicks);
                roll.ClearSelection();
                roll.MoveCursor(roll.CursorTick % m == 0 ? Math.Max(0, roll.CursorTick - m) : roll.CursorTick / m * m);
                return true;
            }
            case Keys.Control | Keys.Right:
            {
                int m = Math.Max(1, song.MeasureTicks);
                roll.ClearSelection();
                roll.MoveCursor((roll.CursorTick / m + 1) * m);
                return true;
            }

            // ---- Shift: 노트 그리기 / 길이 / 음높이
            case Keys.Shift | Keys.Right:
                if (active == null)
                {
                    active = roll.InsertNoteAtCursor(roll.CursorNote, snap);
                    if (roll.AudioFeedback) PreviewBlip(active.Pitch);
                }
                else roll.ResizeNote(active, active.Duration + snap);
                roll.MoveCursor(active.EndTick);
                Say(NoteHelper.Name(active.Pitch) + "  length " + active.Duration + " ticks");
                return true;
            case Keys.Shift | Keys.Left:
                if (active == null) { roll.MoveCursor(Math.Max(0, roll.CursorTick - snap)); return true; }
                roll.ResizeNote(active, Math.Max(snap, active.Duration - snap));
                roll.MoveCursor(active.EndTick);
                Say(NoteHelper.Name(active.Pitch) + "  length " + active.Duration + " ticks");
                return true;
            case Keys.Shift | Keys.Up:
            case Keys.Shift | Keys.Down:
            {
                int d = keyData == (Keys.Shift | Keys.Up) ? 1 : -1;
                if (active == null) { roll.MoveCursorNote(roll.CursorNote + d); Say(NoteHelper.Name(roll.CursorNote)); return true; }
                roll.SetNotePitch(active, active.Pitch + d);
                roll.MoveCursorNote(active.Pitch);
                if (roll.AudioFeedback) PreviewBlip(active.Pitch);
                Say(NoteHelper.Name(active.Pitch));
                return true;
            }

            // ---- 길이 / 옥타브 / 기타
            case Keys.OemOpenBrackets: LengthIndex = lengthIndex + 1; return true;    // [ : 짧게
            case Keys.OemCloseBrackets: LengthIndex = lengthIndex - 1; return true;   // ] : 길게
            case Keys.PageUp: SetKeyboardBase(keyboardBase + 12); return true;
            case Keys.PageDown: SetKeyboardBase(keyboardBase - 12); return true;
            case Keys.End: roll.ClearSelection(); roll.MoveCursor(song.LengthTicks); return true;
            case Keys.Enter: roll.ClearSelection(); roll.MoveCursor(roll.CursorTick + snap); Say("rest"); return true;
            case Keys.Escape: if (roll.Selection.Count > 0) { roll.ClearSelection(); return true; } return false;
            case Keys.Back:
            {
                roll.ClearSelection();
                int t = Math.Max(0, roll.CursorTick - snap);
                roll.MoveCursor(t);
                if (roll.DeleteNotesStartingAt(t)) Say("deleted");
                return true;
            }
            case Keys.Tab: SelectVoice(ShowAllVoices ? 0 : roll.CurrentVoice + 1); return true;
            case Keys.Shift | Keys.Tab: SelectVoice(ShowAllVoices ? 0 : roll.CurrentVoice - 1); return true;
        }
        return false;
    }

    private Timer blipTimer;

    /// <summary>키보드로 그리거나 음높이를 바꿀 때 잠깐 소리를 낸다.</summary>
    private void PreviewBlip(int note)
    {
        if (ws.Audio.IsPlaying) return;
        PreviewNote(note);
        if (blipTimer == null)
        {
            blipTimer = new Timer { Interval = 250 };
            blipTimer.Tick += delegate { blipTimer.Stop(); if (heldKeys.Count == 0) ws.Audio.PreviewNoteOff(PreviewVoice); };
        }
        blipTimer.Stop();
        blipTimer.Start();
    }

    public bool HandleKeyUp(Keys key)
    {
        int note;
        if (!heldKeys.TryGetValue(key, out note)) return false;
        heldKeys.Remove(key);
        if (heldKeys.Count == 0) ws.Audio.PreviewNoteOff(PreviewVoice);
        return true;
    }

    /// <summary>모든 눌린 건반 키를 놓는다 (포커스를 잃을 때).</summary>
    public void ReleaseKeys()
    {
        if (heldKeys.Count == 0) return;
        heldKeys.Clear();
        ws.Audio.PreviewNoteOff(PreviewVoice);
    }

    private void ScrollRows(int rows)
    {
        roll.TopNote += rows;
        roll.ClampView();
        roll.Invalidate();
        UpdateScrollBars();
    }

    /// <summary>위쪽 행 더블클릭/Insert: Instrument는 악기 선택, 나머지는 현재 입력 값으로 이벤트를 넣는다.</summary>
    public void EditBandAt(int tick)
    {
        tick = Math.Max(0, tick);
        if (roll.Band == BandTrack.Instrument) { ChooseInstrumentAt(tick); return; }
        ws.PushUndo();
        var voice = ws.Song.Voices[roll.CurrentVoice];
        switch (roll.Band)
        {
            case BandTrack.Tempo:
                ws.Song.TempoEvents.RemoveAll(e => e.Tick == tick);
                ws.Song.TempoEvents.Add(new TempoEvent { Tick = tick, Multiplier = (float)TempoValue });
                ws.Song.TempoEvents.Sort((a, b) => a.Tick.CompareTo(b.Tick));
                break;
            case BandTrack.Volume:
                voice.Volumes.RemoveAll(e => e.Tick == tick);
                voice.Volumes.Add(new VolumeEvent { Tick = tick, Volume = (float)VolumeValue });
                voice.Volumes.Sort((a, b) => a.Tick.CompareTo(b.Tick));
                break;
            case BandTrack.Pitch:
                voice.Pitches.RemoveAll(e => e.Tick == tick);
                voice.Pitches.Add(new PitchEvent { Tick = tick, Pitch = (float)PitchValue });
                voice.Pitches.Sort((a, b) => a.Tick.CompareTo(b.Tick));
                break;
            case BandTrack.Pan:
                voice.Pans.RemoveAll(e => e.Tick == tick);
                voice.Pans.Add(new PanEvent { Tick = tick, Pan = PanValue });
                voice.Pans.Sort((a, b) => a.Tick.CompareTo(b.Tick));
                break;
        }
        AfterEventEdit();
    }

    /// <summary>위쪽 행 오른쪽 클릭: 클릭 위치 근처의 이벤트를 지운다.</summary>
    public void DeleteBandEventNear(int tick)
    {
        int tol = roll.TickTolerance;
        var voice = ws.Song.Voices[roll.CurrentVoice];
        Predicate<int> near = t => Math.Abs(t - tick) <= tol;
        bool any;
        switch (roll.Band)
        {
            case BandTrack.Tempo: any = ws.Song.TempoEvents.Exists(e => near(e.Tick)); break;
            case BandTrack.Volume: any = voice.Volumes.Exists(e => near(e.Tick)); break;
            case BandTrack.Pitch: any = voice.Pitches.Exists(e => near(e.Tick)); break;
            case BandTrack.Pan: any = voice.Pans.Exists(e => near(e.Tick)); break;
            default: any = voice.Timbres.Exists(e => near(e.Tick)); break;
        }
        if (!any) return;
        ws.PushUndo();
        switch (roll.Band)
        {
            case BandTrack.Tempo: ws.Song.TempoEvents.RemoveAll(e => near(e.Tick)); break;
            case BandTrack.Volume: voice.Volumes.RemoveAll(e => near(e.Tick)); break;
            case BandTrack.Pitch: voice.Pitches.RemoveAll(e => near(e.Tick)); break;
            case BandTrack.Pan: voice.Pans.RemoveAll(e => near(e.Tick)); break;
            default: voice.Timbres.RemoveAll(e => near(e.Tick)); break;
        }
        AfterEventEdit();
    }

    public void ZoomIn() { roll.PixelsPerBeat = roll.PixelsPerBeat * 1.25f; }
    public void ZoomOut() { roll.PixelsPerBeat = roll.PixelsPerBeat * 0.8f; }

    public void UpdatePlayback(double tick, bool playing)
    {
        roll.PlayTick = playing ? tick : -1;
        if (playing)
        {
            int t = (int)tick;
            if (t < roll.ScrollTick || t > roll.ScrollTick + roll.VisibleTicks * 9 / 10)
            {
                roll.ScrollTick = Math.Max(0, t - roll.VisibleTicks / 10);
                UpdateScrollBars();
            }
        }
        roll.Invalidate();
    }

    // ------------------------------------------------------------ 내부

    private void OnRollEdited()
    {
        ws.SongDirty = true;
        UpdateInstrLabel();
        UpdateScrollBars();
        if (SongModified != null) SongModified();
    }

    private AdlibInstrument InstrumentAt(int voice, int tick)
    {
        var t = ws.Song.Voices[voice].TimbreAt(tick);
        return t != null ? ws.Resolve(t.InstrumentName) : null;
    }

    /// <summary>
    /// 시음 보이스 (OPL2 시음 신디사이저는 11보이스): SOP 보이스 12~20은 보이스 1로, SOP 멜로디 모드의 10·11도 보이스 1로 시음한다.
    /// 4-op 악기는 오퍼레이터 1·2만 들린다.
    /// </summary>
    private int PreviewVoice
    {
        get
        {
            int v = roll.CurrentVoice;
            if (v >= Song.MaxVoices) return 0;
            if (ws.Song.IsMelodic && v >= Song.MelodicVoices) return 0;
            return v;
        }
    }

    private void PreviewNote(int note)
    {
        int voice = PreviewVoice;
        ws.Audio.PreviewSetMode(!ws.Song.IsMelodic);
        ws.Audio.PreviewSetInstrument(voice, InstrumentAt(roll.CurrentVoice, roll.CursorTick));
        ws.Audio.PreviewNoteOn(voice, note);
    }

    private void UpdateInstrLabel()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int tick = roll.CursorTick;
        var voice = ws.Song.Voices[Math.Min(roll.CurrentVoice, Song.TotalVoices - 1)];
        instrCaption.Text = PianoRollControl.BandCaption(roll.Band);
        switch (roll.Band)
        {
            case BandTrack.Tempo:
            {
                double m = 1.0;
                foreach (var e in ws.Song.TempoEvents) if (e.Tick <= tick) m = e.Multiplier;
                instrLabel.Text = m.ToString("0.00", inv) + "  (" + (ws.Song.BasicTempo * m).ToString("0.#", inv) + " BPM)";
                break;
            }
            case BandTrack.Volume:
            {
                double v = 1.0;
                foreach (var e in voice.Volumes) if (e.Tick <= tick) v = e.Volume;
                instrLabel.Text = v.ToString("0.00", inv);
                break;
            }
            case BandTrack.Pitch:
            {
                double v = 1.0;
                foreach (var e in voice.Pitches) if (e.Tick <= tick) v = e.Pitch;
                instrLabel.Text = v.ToString("0.00", inv);
                break;
            }
            case BandTrack.Pan:
            {
                int p = PanEvent.Center;
                foreach (var e in voice.Pans) if (e.Tick <= tick) p = e.Pan;
                instrLabel.Text = PanEvent.Label(p) + (ws.Song.IsSop ? "" : "  (SOP only)");
                break;
            }
            default:
            {
                var t = voice.TimbreAt(tick);
                instrLabel.Text = t != null ? t.InstrumentName : "(default)";
                break;
            }
        }
    }

    private void UpdateScrollBars()
    {
        if (hScroll.Width <= 0) return;
        syncing = true;
        try
        {
            var song = ws.Song;
            int vis = roll.VisibleTicks;
            int total = Math.Max(song.LengthTicks + song.MeasureTicks * 4, roll.ScrollTick + vis);
            hScroll.Minimum = 0;
            hScroll.LargeChange = Math.Max(1, vis);
            hScroll.SmallChange = Math.Max(1, song.TickBeat);
            hScroll.Maximum = total + hScroll.LargeChange - 1;
            hScroll.Value = Math.Max(0, Math.Min(roll.ScrollTick, total));

            int span = Song.MaxNote - Song.MinNote + 1;
            int rows = roll.VisibleRows;
            vScroll.Minimum = 0;
            vScroll.LargeChange = Math.Max(1, Math.Min(rows, span));
            vScroll.SmallChange = 1;
            vScroll.Maximum = span - 1;
            int maxVal = Math.Max(0, span - rows);
            vScroll.Value = Math.Max(0, Math.Min(maxVal, Song.MaxNote - roll.TopNote));

            double ratio = roll.PixelsPerBeat / 40.0;
            zoomLabel.Text = ratio >= 1 ? Math.Round(ratio).ToString() + ":1" : "1:" + Math.Round(1.0 / ratio).ToString();
        }
        finally { syncing = false; }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int sb = SystemInformation.VerticalScrollBarWidth;
        topPanel.SetBounds(0, 0, Width, TopHeight);
        roll.SetBounds(0, TopHeight, Math.Max(10, Width - sb), Math.Max(10, Height - TopHeight - sb));
        vScroll.SetBounds(Width - sb, TopHeight, sb, Math.Max(10, Height - TopHeight - sb));
        zoomLabel.SetBounds(0, Height - sb, PianoRollControl.KeyboardWidth, sb);
        hScroll.SetBounds(PianoRollControl.KeyboardWidth, Height - sb, Math.Max(10, Width - PianoRollControl.KeyboardWidth - sb), sb);
        PositionTitle(0);
        UpdateScrollBars();
    }

    // ------------------------------------------------------------ 악기 교체 / 이벤트 편집

    public void ChooseInstrumentAt(int tick)
    {
        var voice = ws.Song.Voices[roll.CurrentVoice];
        var cur = InstrumentAt(roll.CurrentVoice, tick);
        using (var f = new InstrumentListForm(ws.Bank, cur))
        {
            var result = f.ShowDialog(FindForm());
            if (f.Modified) ws.BankDirty = true;
            if (result == DialogResult.OK && f.Selected != null)
            {
                ws.PushUndo();
                ws.Song.Voices[roll.CurrentVoice].SetTimbre(tick, f.Selected.Name);
                ws.SongDirty = true;
                OnRollEdited();
                roll.Invalidate();
            }
            else if (f.Modified && SongModified != null) SongModified();
        }
    }

    public void RemoveInstrumentChangeAtCursor()
    {
        var voice = ws.Song.Voices[roll.CurrentVoice];
        for (int i = 0; i < voice.Timbres.Count; i++)
            if (voice.Timbres[i].Tick == roll.CursorTick)
            {
                ws.PushUndo();
                voice.Timbres.RemoveAt(i);
                ws.SongDirty = true;
                OnRollEdited();
                roll.Invalidate();
                return;
            }
    }

    public void EditTempoEvents()
    {
        var rows = new List<KeyValuePair<int, double>>();
        foreach (var t in ws.Song.TempoEvents) rows.Add(new KeyValuePair<int, double>(t.Tick, t.Multiplier));
        using (var f = new EventEditorForm("Tempo events (multiplier of " + ws.Song.BasicTempo + " BPM)", "Multiplier", rows, 0.01, 10.0))
        {
            if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
            ws.PushUndo();
            ws.Song.TempoEvents.Clear();
            foreach (var r in f.Result) ws.Song.TempoEvents.Add(new TempoEvent { Tick = r.Key, Multiplier = (float)r.Value });
            AfterEventEdit();
        }
    }

    public void EditVolumeEvents()
    {
        var voice = ws.Song.Voices[roll.CurrentVoice];
        var rows = new List<KeyValuePair<int, double>>();
        foreach (var t in voice.Volumes) rows.Add(new KeyValuePair<int, double>(t.Tick, t.Volume));
        using (var f = new EventEditorForm("Volume events - " + voice.Name, "Volume", rows, 0.0, 1.0))
        {
            if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
            ws.PushUndo();
            voice.Volumes.Clear();
            foreach (var r in f.Result) voice.Volumes.Add(new VolumeEvent { Tick = r.Key, Volume = (float)r.Value });
            AfterEventEdit();
        }
    }

    public void EditPitchEvents()
    {
        var voice = ws.Song.Voices[roll.CurrentVoice];
        var rows = new List<KeyValuePair<int, double>>();
        foreach (var t in voice.Pitches) rows.Add(new KeyValuePair<int, double>(t.Tick, t.Pitch));
        using (var f = new EventEditorForm("Pitch events - " + voice.Name + " (1.0 = normal)", "Pitch", rows, 0.0, 2.0))
        {
            if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
            ws.PushUndo();
            voice.Pitches.Clear();
            foreach (var r in f.Result) voice.Pitches.Add(new PitchEvent { Tick = r.Key, Pitch = (float)r.Value });
            AfterEventEdit();
        }
    }

    public void EditSongSettings()
    {
        using (var f = new SongSettingsForm(ws.Song))
        {
            if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
            ws.PushUndo();
            var s = ws.Song;
            s.TickBeat = f.TickBeat;
            s.BeatsPerMeasure = f.BeatsPerMeasure;
            s.BasicTempo = f.Tempo;
            s.IsMelodic = f.Melodic;
            ws.SongDirty = true;
            BindSong();
            if (SongModified != null) SongModified();
        }
    }

    public void SetRhythmMode(bool rhythm) { SetMode(rhythm, false); }

    /// <summary>
    /// Options → With / Without Percussion (ROL/IMS, OPL2) · With Percussion (SOP, OPL3 20보이스).
    /// SOP에서 나올 때 보이스 12~20에 노트가 있으면 묻는다 (지우지 않고 숨긴다. 다시 SOP 모드로 오면 보인다).
    /// </summary>
    public void SetMode(bool rhythm, bool sop)
    {
        try { SetModeCore(rhythm, sop); } finally { ApplyVoiceRange(); }
    }

    private void SetModeCore(bool rhythm, bool sop)
    {
        var song = ws.Song;
        if (song.IsMelodic == !rhythm && song.IsSop == sop) return;
        if (song.IsSop && !sop)
        {
            int hidden = 0;
            for (int v = Song.MaxVoices; v < Song.TotalVoices; v++) hidden += song.Voices[v].Notes.Count;
            if (hidden > 0 && MessageBox.Show(FindForm(),
                    "보이스 12~20에 노트가 " + hidden + "개 있습니다.\nROL / IMS 모드에서는 보이지 않고 저장되지 않습니다 (팬 · 4-op도 빠집니다). 바꿀까요?",
                    "Leave SOP mode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }
        ws.PushUndo();
        if (sop && !song.IsSop)
        {
            // 새로 보이는 보이스 12~20: 악기가 없으면 기본 악기
            for (int v = Song.MaxVoices; v < Song.TotalVoices; v++)
                if (song.Voices[v].Timbres.Count == 0) song.Voices[v].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = Workspace.DefaultInstrumentFor(v, !rhythm) });
        }
        song.IsSop = sop;
        if (!sop && roll.Band == BandTrack.Pan) roll.Band = BandTrack.Instrument;
        if (song.IsMelodic == !rhythm) { FinishModeChange(); return; }
        song.IsMelodic = !rhythm;
        // 보이스 7~11이 노트 없이 이전 모드의 기본 악기만 갖고 있으면 새 모드의 기본 악기로 바꾼다 (예: piano1 -> bdrum)
        for (int v = 6; v < Song.MaxVoices; v++)
        {
            var vt = ws.Song.Voices[v];
            if (vt.Notes.Count == 0 && vt.Timbres.Count == 1 && vt.Timbres[0].Tick == 0
                && string.Equals(vt.Timbres[0].InstrumentName, Workspace.DefaultInstrumentFor(v, rhythm), StringComparison.OrdinalIgnoreCase))
                vt.Timbres[0].InstrumentName = Workspace.DefaultInstrumentFor(v, !rhythm);
        }
        FinishModeChange();
    }

    private void FinishModeChange()
    {
        ws.SongDirty = true;
        BindSong();
        if (SongModified != null) SongModified();
    }

    private void AfterEventEdit()
    {
        ws.SongDirty = true;
        OnRollEdited();
        roll.Invalidate();
    }
}
