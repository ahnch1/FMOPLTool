using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AdlibComposer.App.Controls;
using AdlibComposer.Core;

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
    private int snapDivision = 4;

    public event Action SongModified;
    public event Action<int> HoverNoteChanged;

    public ComposerView(Workspace ws)
    {
        this.ws = ws;
        BackColor = Theme.Back;
        DoubleBuffered = true;

        BuildTopPanel();
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
        roll.TimbreRequested += delegate (int tick) { ChooseInstrumentAt(tick); };
        roll.HoverNote += delegate (int n) { noteLabel.Text = n >= 0 ? NoteHelper.Name(n) : ""; if (HoverNoteChanged != null) HoverNoteChanged(n); };
        roll.PreviewOn += PreviewNote;
        roll.PreviewOff += delegate { ws.Audio.PreviewNoteOff(roll.CurrentVoice); };

        hScroll.ValueChanged += delegate { if (syncing) return; roll.ScrollTick = hScroll.Value; roll.Invalidate(); };
        vScroll.ValueChanged += delegate
        {
            if (syncing) return;
            roll.TopNote = Song.MaxNote - vScroll.Value;
            roll.ClampView();
            roll.Invalidate();
        };

        BindSong();
    }

    public PianoRollControl Roll { get { return roll; } }

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
        instrLabel.DoubleClick += delegate { ChooseInstrumentAt(roll.CursorTick); };
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

        string[] labels = Song.VoiceLabels(ws.Song.IsMelodic);
        int x = 2;
        for (int i = 0; i <= labels.Length; i++)
        {
            bool all = i == labels.Length;
            var rb = new RadioButton
            {
                Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, Text = all ? "All" : labels[i],
                Tag = all ? -1 : i, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.UiFont
            };
            int w = all ? 38 : (labels[i].Length > 1 ? 34 : 28);
            rb.SetBounds(x, 4, w, 24);
            x += w + 2;
            rb.FlatAppearance.BorderColor = Color.Black;
            rb.FlatAppearance.CheckedBackColor = Color.Black;
            rb.CheckedChanged += OnVoiceChecked;
            voiceButtons.Add(rb);
            voicePanel.Controls.Add(rb);
        }
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
        roll.Invalidate();
        UpdateInstrLabel();
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

    public int SnapDivision
    {
        get { return snapDivision; }
        set
        {
            snapDivision = Math.Max(1, value);
            roll.SnapTicks = Math.Max(1, ws.Song.TickBeat / snapDivision);
            roll.Invalidate();
        }
    }

    /// <summary>ws.Song이 바뀌었을 때(새 곡/열기/실행취소) 호출.</summary>
    public void BindSong()
    {
        roll.Song = ws.Song;
        roll.SnapTicks = Math.Max(1, ws.Song.TickBeat / snapDivision);
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

    private void PreviewNote(int note)
    {
        int voice = roll.CurrentVoice;
        ws.Audio.PreviewSetMode(!ws.Song.IsMelodic);
        ws.Audio.PreviewSetInstrument(voice, InstrumentAt(voice, roll.CursorTick));
        ws.Audio.PreviewNoteOn(voice, note);
    }

    private void UpdateInstrLabel()
    {
        var t = ws.Song.Voices[Math.Min(roll.CurrentVoice, Song.MaxVoices - 1)].TimbreAt(roll.CursorTick);
        instrLabel.Text = t != null ? t.InstrumentName : "(default)";
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

    public void SetRhythmMode(bool rhythm)
    {
        if (ws.Song.IsMelodic == !rhythm) return;
        ws.PushUndo();
        ws.Song.IsMelodic = !rhythm;
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
