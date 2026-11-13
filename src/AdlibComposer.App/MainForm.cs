using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AdlibComposer.App.Controls;
using AdlibComposer.App.Views;
using AdlibComposer.Core;
using AdlibComposer.Formats;
using AdlibComposer.Notation;

namespace AdlibComposer.App;

/// <summary>Visual Composer(작곡)와 Instrument Maker(악기 편집)를 한 창에 담은 메인 폼.</summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class MainForm : Form
{
    private readonly Workspace ws = new Workspace();
    private readonly ComposerView composer;
    private readonly InstrumentView instrument;
    private readonly MenuStrip menu = new MenuStrip();
    private readonly StatusStrip status = new StatusStrip();
    private readonly ToolStripStatusLabel statusMain = new ToolStripStatusLabel();
    private readonly ToolStripStatusLabel statusSong = new ToolStripStatusLabel();
    private readonly ToolStripStatusLabel statusBank = new ToolStripStatusLabel();
    private readonly Timer timer = new Timer();

    private ToolStripMenuItem fileMenu, editMenu, playMenu, displayMenu, optionsMenu, instOptionMenu, helpMenu;
    private ToolStripMenuItem additiveItem, instMidiItem;
    private ToolStripMenuItem modeComposer, modeInstrument, loopItem, showAllItem;
    private ToolStripMenuItem revertItem, editModeItem, basicTempoItem, ticksBeatItem, beatsMeasureItem;
    private ToolStripMenuItem percOnItem, percOffItem, feedbackItem, midiItem;
    private readonly List<ToolStripMenuItem> snapItems = new List<ToolStripMenuItem>();
    private readonly List<ToolStripMenuItem> bandItems = new List<ToolStripMenuItem>();
    private readonly List<ToolStripMenuItem> gridItems = new List<ToolStripMenuItem>();
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    private readonly List<ToolStripMenuItem> previewItems = new List<ToolStripMenuItem>();
    private bool composerMode = true;
    private bool wasPlaying;

    public MainForm()
    {
        Text = "AdLib Composer";
        ClientSize = new Size(960, 640);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Back;
        KeyPreview = true;

        composer = new ComposerView(ws) { Dock = DockStyle.Fill };
        instrument = new InstrumentView(ws) { Dock = DockStyle.Fill, Visible = false };

        BuildMenu();
        BuildStatus();

        Controls.Add(composer);
        Controls.Add(instrument);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        ws.SongReplaced += delegate { composer.BindSong(); SyncMenus(); UpdateStatus(); };
        ws.BankReplaced += delegate { UpdateStatus(); };
        ws.CurrentInstrumentChanged += delegate { UpdateStatus(); };
        composer.SongModified += delegate { UpdateStatus(); };
        composer.HoverNoteChanged += delegate (int n) { statusMain.Text = n >= 0 ? "Note " + NoteHelper.Name(n) + " (" + n + ")" : ""; };
        composer.Roll.EditCursorChanged += UpdateStatus;
        instrument.InstrumentEdited += UpdateStatus;
        instrument.ModeChanged += delegate { if (menu.Items.Count > 0) SyncMenus(); };

        timer.Interval = 30;
        timer.Tick += OnTimer;
        timer.Start();

        Shown += delegate { TryOpenAudio(); };
        FormClosing += OnClosing;
        FormClosed += delegate { timer.Stop(); ws.Dispose(); };

        SyncMenus();
        UpdateStatus();
    }

    // ================================================================ 메뉴

    private static ToolStripMenuItem Item(string text, EventHandler click, Keys keys = Keys.None, string display = null)
    {
        var it = new ToolStripMenuItem(text);
        if (click != null) it.Click += click;
        if (keys != Keys.None) it.ShortcutKeys = keys;
        if (display != null) it.ShortcutKeyDisplayString = display;
        return it;
    }

    /// <summary>
    /// 원본 Visual Composer 메뉴 구성을 그대로 따른다 (F5 File, F6 Edit, F7 Playback, F8 Display, F9 Options).
    /// 원본에 없는 이 프로그램의 추가 기능은 각 메뉴의 구분선 아래에 둔다.
    /// </summary>
    private void BuildMenu()
    {
        // ---------------------------------------------------------------- F5 File
        revertItem = Item("Revert", delegate { RevertSong(); });
        fileMenu = new ToolStripMenuItem("File (F5)");
        fileMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("New", delegate { NewSong(); }, Keys.Control | Keys.N),
            Item("Open...", delegate { OpenSong(); }, Keys.Control | Keys.O),
            Item("Save", delegate { SaveSong(false); }, Keys.Control | Keys.S),
            Item("Save As...", delegate { SaveSong(true); }),
            revertItem,
            new ToolStripSeparator(),
            Item("Instrument Bank File...", delegate { OpenBankBrowser(); }, Keys.Control | Keys.B),
            new ToolStripSeparator(),
            Item("New Bank", delegate { NewBank(); }),
            Item("Save Bank", delegate { SaveBank(false); }, Keys.Control | Keys.Shift | Keys.S),
            Item("Save Bank As...", delegate { SaveBank(true); }),
            Item("Import Instrument (.INS)...", delegate { ImportInstrument(); }),
            Item("Export Instrument (.INS)...", delegate { ExportInstrument(); }),
            Item("Export Score (text)...", delegate { ExportScoreText(); }),
            new ToolStripSeparator(),
            Item("Exit", delegate { Close(); }, Keys.None, "Alt+F4"),
        });

        // ---------------------------------------------------------------- F6 Edit
        editModeItem = Item("Edit", delegate
        {
            ComposerOnly(delegate { composer.Tool = composer.Tool == RollTool.Select ? RollTool.Pencil : RollTool.Select; });
            SyncMenus();
        }, Keys.None, "I tool");
        editMenu = new ToolStripMenuItem("Edit (F6)");
        editMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            editModeItem,
            Item("Copy", delegate { ComposerOnly(delegate { composer.Roll.CopySelection(); }); }, Keys.Control | Keys.C),
            Item("Cut", delegate { ComposerOnly(delegate { composer.Roll.CutSelection(); }); }, Keys.Control | Keys.X),
            Item("Paste", delegate { ComposerOnly(delegate { composer.Roll.Paste(); }); }, Keys.Control | Keys.V),
            Item("Clear", delegate { ComposerOnly(delegate { composer.Roll.DeleteSelection(); }); }, Keys.Delete),
            new ToolStripSeparator(),
            Item("Semitone Up", delegate { ComposerOnly(delegate { composer.Roll.Transpose(1); }); }, Keys.Control | Keys.Up),
            Item("Semitone Down", delegate { ComposerOnly(delegate { composer.Roll.Transpose(-1); }); }, Keys.Control | Keys.Down),
            Item("Octave Up", delegate { ComposerOnly(delegate { composer.Roll.Transpose(12); }); }, Keys.Control | Keys.Shift | Keys.Up),
            Item("Octave Down", delegate { ComposerOnly(delegate { composer.Roll.Transpose(-12); }); }, Keys.Control | Keys.Shift | Keys.Down),
            new ToolStripSeparator(),
            Item("Undo", delegate { ComposerOnly(delegate { ws.Undo(); }); }, Keys.Control | Keys.Z),
            Item("Redo", delegate { ComposerOnly(delegate { ws.Redo(); }); }, Keys.Control | Keys.Y),
            Item("Select All", delegate { ComposerOnly(delegate { composer.Roll.SelectAll(); }); }, Keys.Control | Keys.A),
            Item("Insert Event at Cursor", delegate { ComposerOnly(delegate { composer.EditBandAt(composer.Roll.CursorTick); }); }, Keys.Insert),
            Item("Tempo Events...", delegate { ComposerOnly(delegate { composer.EditTempoEvents(); }); }),
            Item("Volume Events (current voice)...", delegate { ComposerOnly(delegate { composer.EditVolumeEvents(); }); }),
            Item("Pitch Events (current voice)...", delegate { ComposerOnly(delegate { composer.EditPitchEvents(); }); }),
        });

        // ---------------------------------------------------------------- F7 Playback
        loopItem = Item("Loop Playback", null);
        loopItem.CheckOnClick = true;
        basicTempoItem = Item("Basic Tempo...", delegate { ComposerOnly(PromptBasicTempo); });
        playMenu = new ToolStripMenuItem("Playback (F7)");
        playMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("Play", delegate { PlayFrom(composer.Roll.CursorTick); }, Keys.None, "Space"),
            Item("Play Voice", delegate { PlayFrom(composer.Roll.CursorTick, composer.ShowAllVoices ? -1 : composer.Roll.CurrentVoice); }, Keys.None, "Shift+Space"),
            Item("Interrupt", delegate { StopPlayback(); }, Keys.None, "Esc"),
            new ToolStripSeparator(),
            basicTempoItem,
            new ToolStripSeparator(),
            Item("Play from Start", delegate { PlayFrom(0); }, Keys.None, "Home"),
            loopItem,
        });

        // ---------------------------------------------------------------- F8 Display
        displayMenu = new ToolStripMenuItem("Display (F8)");
        var bands = new[] { BandTrack.Tempo, BandTrack.Instrument, BandTrack.Volume, BandTrack.Pitch };
        string[] bandNames = { "Tempo...", "Instrument", "Volume...", "Pitch..." };
        for (int i = 0; i < bands.Length; i++)
        {
            var band = bands[i];
            var it = Item(bandNames[i], null);
            it.Tag = band;
            it.Click += delegate { ComposerOnly(delegate { ChooseBand(band); }); };
            bandItems.Add(it);
            displayMenu.DropDownItems.Add(it);
        }
        displayMenu.DropDownItems.Add(new ToolStripSeparator());
        string[] gridNames = { "Large Grid", "Medium Grid", "Small Grid" };
        for (int i = 0; i < gridNames.Length; i++)
        {
            int size = i;
            var it = Item(gridNames[i], null);
            it.Tag = size;
            it.Click += delegate { composer.GridSize = size; SyncMenus(); };
            gridItems.Add(it);
            displayMenu.DropDownItems.Add(it);
        }
        displayMenu.DropDownItems.Add(new ToolStripSeparator());
        ticksBeatItem = Item("Ticks per Beat...", delegate { ComposerOnly(PromptTicksPerBeat); });
        beatsMeasureItem = Item("Beats per Measure...", delegate { ComposerOnly(PromptBeatsPerMeasure); });
        displayMenu.DropDownItems.Add(ticksBeatItem);
        displayMenu.DropDownItems.Add(beatsMeasureItem);
        displayMenu.DropDownItems.Add(new ToolStripSeparator());
        showAllItem = Item("Show All Voices", delegate { composer.ShowAllVoices = showAllItem.Checked; });
        showAllItem.CheckOnClick = true;
        displayMenu.DropDownItems.Add(showAllItem);
        var snapMenu = new ToolStripMenuItem("Snap");
        int[] divisions = { 1, 2, 4, 8 };
        string[] names = { "1 beat", "1/2 beat", "1/4 beat", "1/8 beat" };
        for (int i = 0; i < divisions.Length; i++)
        {
            int div = divisions[i];
            var it = Item(names[i], null);
            it.Tag = div;
            it.Click += delegate { composer.SnapDivision = div; SyncMenus(); };
            snapItems.Add(it);
            snapMenu.DropDownItems.Add(it);
        }
        displayMenu.DropDownItems.Add(snapMenu);
        displayMenu.DropDownItems.Add(Item("Zoom In", delegate { composer.ZoomIn(); SyncMenus(); }, Keys.Control | Keys.Oemplus));
        displayMenu.DropDownItems.Add(Item("Zoom Out", delegate { composer.ZoomOut(); SyncMenus(); }, Keys.Control | Keys.OemMinus));

        // ---------------------------------------------------------------- F9 Options
        percOnItem = Item("With Percussion", delegate { ComposerOnly(delegate { composer.SetRhythmMode(true); }); SyncMenus(); });
        percOffItem = Item("Without Percussion", delegate { ComposerOnly(delegate { composer.SetRhythmMode(false); }); SyncMenus(); });
        feedbackItem = Item("Audio Feedback", delegate { composer.Roll.AudioFeedback = feedbackItem.Checked; });
        feedbackItem.CheckOnClick = true;
        feedbackItem.Checked = composer.Roll.AudioFeedback;
        midiItem = Item("MIDI Input", null, Keys.None, "not yet");
        midiItem.Enabled = false;

        optionsMenu = new ToolStripMenuItem("Options (F9)");
        optionsMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            percOnItem,
            percOffItem,
            new ToolStripSeparator(),
            feedbackItem,
            new ToolStripSeparator(),
            midiItem,
            new ToolStripSeparator(),
            Item("Pitch Bend Range...", delegate { PromptPitchBend(); }),
            Item("Master Volume...", delegate { PromptVolume(); }),
        });

        // ---------------------------------------------------------------- Instrument Maker: F6 Option (원본 구성)
        additiveItem = Item("Additive Synthesis", delegate { instrument.Additive = !instrument.Additive; SyncMenus(); });
        instMidiItem = Item("MIDI Input", null, Keys.None, "not yet");
        instMidiItem.Enabled = false;
        instOptionMenu = new ToolStripMenuItem("Option (F6)");
        instOptionMenu.DropDownItems.Add(Item("Transpose Keyboard Up", delegate { instrument.TransposeKeyboard(1); }, Keys.None, "PgUp"));
        instOptionMenu.DropDownItems.Add(Item("Transpose Keyboard Down", delegate { instrument.TransposeKeyboard(-1); }, Keys.None, "PgDn"));
        instOptionMenu.DropDownItems.Add(new ToolStripSeparator());
        instOptionMenu.DropDownItems.Add(additiveItem);
        instOptionMenu.DropDownItems.Add(new ToolStripSeparator());
        string[] modes = { "Melodic Mode", "Bass Drum Mode", "Snare Drum Mode", "Tom Mode", "Cymbal Mode", "Hi-hat Mode" };
        int[] modeVoice = { 0, 6, 7, 8, 9, 10 };
        for (int i = 0; i < modes.Length; i++)
        {
            int voice = modeVoice[i];
            var it = Item(modes[i], null);
            it.Tag = voice;
            it.Click += delegate { SetPreviewVoice(voice); };
            previewItems.Add(it);
            instOptionMenu.DropDownItems.Add(it);
        }
        instOptionMenu.DropDownItems.Add(new ToolStripSeparator());
        instOptionMenu.DropDownItems.Add(instMidiItem);
        instOptionMenu.DropDownItems.Add(new ToolStripSeparator());
        instOptionMenu.DropDownItems.Add(Item("Master Volume...", delegate { PromptVolume(); }));

        helpMenu = new ToolStripMenuItem("Help (F10)");
        helpMenu.DropDownItems.Add(Item("About", delegate { About(); }));

        modeComposer = Item("Composer", delegate { SetMode(true); }, Keys.Control | Keys.D1);
        modeInstrument = Item("Instrument Maker", delegate { SetMode(false); }, Keys.Control | Keys.D2);
        modeComposer.Alignment = ToolStripItemAlignment.Right;
        modeInstrument.Alignment = ToolStripItemAlignment.Right;

        menu.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, instOptionMenu, playMenu, displayMenu, optionsMenu, helpMenu, modeInstrument, modeComposer });
        menu.MenuActivate += delegate { SyncMenus(); };
    }

    // ---------------------------------------------------------------- 원본 메뉴 동작

    private double? AskNumber(string title, string label, double current, double min, double max)
    {
        string v = Prompt.Show(this, title, label + " (" + min.ToString(Inv) + " ~ " + max.ToString(Inv) + "):", current.ToString("0.###", Inv));
        double d;
        if (v == null || !double.TryParse(v, System.Globalization.NumberStyles.Float, Inv, out d)) return null;
        if (d < min || d > max) { MessageBox.Show(this, "범위를 벗어난 값입니다.", title); return null; }
        return d;
    }

    /// <summary>Display → Tempo / Instrument / Volume / Pitch: 위쪽 행을 그 트랙으로 바꾸고, 넣을 값을 정한다.</summary>
    private void ChooseBand(BandTrack band)
    {
        composer.Band = band;
        double? d;
        switch (band)
        {
            case BandTrack.Tempo:
                d = AskNumber("Tempo", "Tempo multiplier to insert", composer.TempoValue, 0.01, 10.0);
                if (d.HasValue) composer.TempoValue = d.Value;
                break;
            case BandTrack.Volume:
                d = AskNumber("Volume", "Volume to insert", composer.VolumeValue, 0.0, 1.0);
                if (d.HasValue) composer.VolumeValue = d.Value;
                break;
            case BandTrack.Pitch:
                d = AskNumber("Pitch", "Pitch to insert (1.0 = accurate)", composer.PitchValue, 0.0, 2.0);
                if (d.HasValue) composer.PitchValue = d.Value;
                break;
        }
        SyncMenus();
    }

    private void PromptBasicTempo()
    {
        var d = AskNumber("Basic tempo", "Beats per minute", ws.Song.BasicTempo, 1, 1000);
        if (!d.HasValue) return;
        ws.PushUndo();
        ws.Song.BasicTempo = (float)d.Value;
        ws.SongDirty = true;
        SyncMenus();
        UpdateStatus();
    }

    private void PromptTicksPerBeat()
    {
        var d = AskNumber("Ticks per beat", "Ticks per beat", ws.Song.TickBeat, 1, 240);
        if (!d.HasValue) return;
        ws.PushUndo();
        ws.Song.TickBeat = (int)Math.Round(d.Value);
        ws.SongDirty = true;
        composer.BindSong();
        SyncMenus();
        UpdateStatus();
    }

    private void PromptBeatsPerMeasure()
    {
        var d = AskNumber("Beats per measure", "Beats per measure", ws.Song.BeatsPerMeasure, 1, 32);
        if (!d.HasValue) return;
        ws.PushUndo();
        ws.Song.BeatsPerMeasure = (int)Math.Round(d.Value);
        ws.SongDirty = true;
        composer.BindSong();
        SyncMenus();
        UpdateStatus();
    }

    /// <summary>File → Revert: 저장된 파일 내용으로 되돌린다.</summary>
    private void RevertSong()
    {
        string path = ws.Song.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        if (ws.SongDirty && MessageBox.Show(this, "저장하지 않은 변경 내용을 버리고 파일에서 다시 읽을까요?", "Revert",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try { StopPlayback(); ws.OpenSong(path); }
        catch (Exception ex) { Fail("곡을 다시 읽을 수 없습니다.", ex); }
    }

    private void BuildStatus()
    {
        statusMain.Spring = true;
        statusMain.TextAlign = ContentAlignment.MiddleLeft;
        status.Items.AddRange(new ToolStripItem[] { statusMain, statusSong, statusBank });
    }

    private void ComposerOnly(Action a) { if (composerMode) a(); }

    private void SyncMenus()
    {
        var song = ws.Song;
        percOnItem.Checked = !song.IsMelodic;
        percOffItem.Checked = song.IsMelodic;
        foreach (var it in snapItems) it.Checked = (int)it.Tag == composer.SnapDivision;
        foreach (var it in gridItems) it.Checked = (int)it.Tag == composer.GridSize;
        foreach (var it in bandItems) it.Checked = (BandTrack)it.Tag == composer.Band;
        bandItems[0].ShortcutKeyDisplayString = composer.TempoValue.ToString("0.00", Inv);
        bandItems[2].ShortcutKeyDisplayString = composer.VolumeValue.ToString("0.00", Inv);
        bandItems[3].ShortcutKeyDisplayString = composer.PitchValue.ToString("0.00", Inv);
        int cv = Math.Min(composer.Roll.CurrentVoice, Song.MaxVoices - 1);
        bandItems[1].ShortcutKeyDisplayString = "default " + Workspace.DefaultInstrumentFor(cv, song.IsMelodic);
        basicTempoItem.ShortcutKeyDisplayString = song.BasicTempo.ToString("0.0", Inv);
        ticksBeatItem.ShortcutKeyDisplayString = song.TickBeat.ToString();
        beatsMeasureItem.ShortcutKeyDisplayString = song.BeatsPerMeasure.ToString();
        revertItem.Enabled = !string.IsNullOrEmpty(song.FilePath);
        editModeItem.Checked = composer.Tool == RollTool.Select;
        showAllItem.Checked = composer.ShowAllVoices;
        modeComposer.Checked = composerMode;
        modeInstrument.Checked = !composerMode;
        editMenu.Visible = playMenu.Visible = displayMenu.Visible = composerMode;
        optionsMenu.Visible = composerMode;
        instOptionMenu.Visible = !composerMode;
        foreach (var it in previewItems) it.Checked = (int)it.Tag == instrument.PreviewVoice;
        additiveItem.Checked = instrument.Additive;
        additiveItem.Enabled = instrument.AdditiveAvailable;
    }

    // ================================================================ 모드 / 상태

    private void SetMode(bool composerOn)
    {
        if (composerMode == composerOn) return;
        composerMode = composerOn;
        StopPlayback();
        ws.Audio.PreviewAllOff();
        composer.Visible = composerOn;
        instrument.Visible = !composerOn;
        if (!composerOn) instrument.ApplyPreview();
        SyncMenus();
        UpdateStatus();
        (composerOn ? (Control)composer : instrument).Focus();
    }

    private void UpdateStatus()
    {
        var s = ws.Song;
        string songName = (s.FilePath != null ? Path.GetFileName(s.FilePath) : s.Title) + (ws.SongDirty ? " *" : "");
        string bankName = (string.IsNullOrEmpty(ws.Bank.FilePath) ? "(new bank)" : Path.GetFileName(ws.Bank.FilePath)) + (ws.BankDirty ? " *" : "");
        statusSong.Text = songName + "  " + s.BasicTempo + " BPM  " + (s.IsMelodic ? "9 voices" : "rhythm 11");
        statusBank.Text = "Bank: " + bankName + " (" + ws.Bank.UsedCount + ")";
        if (composerMode)
        {
            int c = composer.Roll.CursorTick;
            int m = c / Math.Max(1, s.MeasureTicks) + 1;
            int b = (c % Math.Max(1, s.MeasureTicks)) / Math.Max(1, s.TickBeat) + 1;
            int t = c % Math.Max(1, s.TickBeat);
            statusSong.Text += "   cursor " + m + ":" + b + ":" + t;
            Text = "AdLib Composer - " + songName;
        }
        else
        {
            Text = "AdLib Composer - Instrument Maker - " + bankName + (instrument.Current != null ? " [" + instrument.Current.Name + "]" : "");
        }
        composer.RefreshLabels();
    }

    /// <summary>Instrument Maker Option → ... Mode: 악기 모드(시음 보이스)를 바꾸고 악기에 기록한다.</summary>
    private void SetPreviewVoice(int voice)
    {
        instrument.SetMode(voice);
        SyncMenus();
    }

    private void PromptPitchBend()
    {
        string v = Prompt.Show(this, "Pitch bend range", "Semitones for pitch 0.0 / 2.0 (default 1):", ws.Audio.Mapping.PitchBendSemitones.ToString(System.Globalization.CultureInfo.InvariantCulture));
        double d;
        if (v != null && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) && d >= 0 && d <= 24)
            ws.Audio.Mapping.PitchBendSemitones = d;
    }

    private void PromptVolume()
    {
        string v = Prompt.Show(this, "Master volume", "0 ~ 200 (%):", ((int)Math.Round(ws.Audio.MasterVolume * 100)).ToString());
        double d;
        if (v != null && double.TryParse(v, out d) && d >= 0 && d <= 200) ws.Audio.MasterVolume = d / 100.0;
    }

    private void About()
    {
        MessageBox.Show(this,
            "AdLib Composer 0.1\n\n" +
            "AdLib Visual Composer + Instrument Maker 재현 프로젝트.\n" +
            "ROL / IMS / BNK / INS 읽기·쓰기, 내장 OPL2 에뮬레이터(근사)로 재생합니다.\n\n" +
            "Space: 재생/정지   Home: 처음부터   Esc: 정지\n" +
            "Instrument Maker: F1/F2 열 전환, Z S X D C V... 키로 시음, PageUp/PageDown 옥타브.",
            "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ================================================================ 오디오 / 재생

    private bool TryOpenAudio()
    {
        if (ws.Audio.IsDeviceOpen) return true;
        try { ws.Audio.OpenDevice(); return true; }
        catch (Exception ex) { statusMain.Text = "오디오 장치를 열 수 없습니다: " + ex.Message; return false; }
    }

    private void TogglePlay()
    {
        if (!composerMode) return;
        if (ws.Audio.IsPlaying) StopPlayback(); else PlayFrom(composer.Roll.CursorTick);
    }

    private void PlayFrom(int tick, int onlyVoice = -1)
    {
        if (!composerMode || !TryOpenAudio()) return;
        ws.Audio.Stop();
        int ls = -1, le = -1;
        if (loopItem.Checked) { ls = 0; le = Math.Max(1, ws.Song.LengthTicks); }
        ws.Audio.Play(ws.Song, ws.Bank, tick, ls, le, onlyVoice);
    }

    private void StopPlayback()
    {
        ws.Audio.Stop();
        composer.UpdatePlayback(-1, false);
        wasPlaying = false;
    }

    private void OnTimer(object sender, EventArgs e)
    {
        if (!composerMode) return;
        bool playing = ws.Audio.IsPlaying;
        if (playing || wasPlaying) composer.UpdatePlayback(ws.Audio.PositionTick, playing);
        wasPlaying = playing;
    }

    // ================================================================ 키 처리

    private Control FocusedControl()
    {
        Control c = this;
        while (true)
        {
            var cc = c as ContainerControl;
            if (cc == null || cc.ActiveControl == null) return c;
            c = cc.ActiveControl;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool text = FocusedControl() is TextBoxBase;
        switch (keyData)
        {
            case Keys.F5: fileMenu.ShowDropDown(); return true;
            case Keys.F6:
                if (composerMode) editMenu.ShowDropDown(); else instOptionMenu.ShowDropDown();
                return true;
            case Keys.F7: if (composerMode) { playMenu.ShowDropDown(); return true; } break;
            case Keys.F8: if (composerMode) { displayMenu.ShowDropDown(); return true; } break;
            case Keys.F9: if (composerMode) { optionsMenu.ShowDropDown(); return true; } break;
            case Keys.F10: helpMenu.ShowDropDown(); return true;
            case Keys.F1: if (!composerMode) { instrument.FocusColumn(true); return true; } break;
            case Keys.F2: if (!composerMode) { instrument.FocusColumn(false); return true; } break;
            case Keys.Space: if (composerMode && !text) { TogglePlay(); return true; } break;
            case Keys.Shift | Keys.Space:
                if (composerMode && !text)
                {
                    if (ws.Audio.IsPlaying) StopPlayback();
                    else PlayFrom(composer.Roll.CursorTick, composer.ShowAllVoices ? -1 : composer.Roll.CurrentVoice);
                    return true;
                }
                break;
            case Keys.Home: if (composerMode && !text) { PlayFrom(0); return true; } break;
            case Keys.Escape: if (composerMode && ws.Audio.IsPlaying) { StopPlayback(); return true; } break;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!composerMode && !instrument.IsEditingText && !e.Control && !e.Alt && instrument.HandleKeyDown(e.KeyCode))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!composerMode && instrument.HandleKeyUp(e.KeyCode)) e.Handled = true;
    }

    // ================================================================ 파일

    private bool ConfirmDiscardSong()
    {
        if (!ws.SongDirty) return true;
        var r = MessageBox.Show(this, "곡의 변경 내용을 저장할까요?", "AdLib Composer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) return SaveSong(false);
        return true;
    }

    private bool ConfirmDiscardBank()
    {
        if (!ws.BankDirty) return true;
        var r = MessageBox.Show(this, "악기 뱅크의 변경 내용을 저장할까요?", "AdLib Composer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) return SaveBank(false);
        return true;
    }

    private void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (!ConfirmDiscardSong() || !ConfirmDiscardBank()) e.Cancel = true;
    }

    private void Fail(string what, Exception ex)
    {
        MessageBox.Show(this, what + "\n\n" + ex.Message, "AdLib Composer", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void NewSong()
    {
        if (!ConfirmDiscardSong()) return;
        StopPlayback();
        ws.NewSong();
    }

    private void OpenSong()
    {
        if (!ConfirmDiscardSong()) return;
        var readable = new List<ISongFormat>();
        foreach (var f in ws.Formats.Songs) if (f.CanRead) readable.Add(f);
        using (var dlg = new OpenFileDialog { Filter = FormatRegistry.BuildFilter(readable), Title = "Open song" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                StopPlayback();
                ws.OpenSong(dlg.FileName);
                if (ws.Bank.UsedCount <= 1) OfferBankFor(dlg.FileName);
                var missing = ws.MissingInstruments();
                if (missing.Count > 0)
                    MessageBox.Show(this, "뱅크에 없는 악기가 " + missing.Count + "개 있습니다 (기본 악기로 재생됩니다):\n" +
                        string.Join(", ", missing.GetRange(0, Math.Min(8, missing.Count)).ToArray()), "Missing instruments");
            }
            catch (Exception ex) { Fail("곡을 열 수 없습니다.", ex); }
        }
    }

    /// <summary>곡과 같은 폴더의 BNK 중 곡이 쓰는 악기를 가장 많이 가진 뱅크를 불러올지 묻는다.</summary>
    private void OfferBankFor(string songPath)
    {
        var needed = ws.MissingInstruments();
        if (needed.Count == 0) return;
        string dir = Path.GetDirectoryName(songPath);
        string bestPath = null; int best = 0;
        foreach (string file in Directory.GetFiles(dir, "*.bnk"))
        {
            try
            {
                InstrumentBank b;
                using (var fs = File.OpenRead(file)) b = ws.Formats.FindBank(file).Read(fs);
                int hit = 0;
                foreach (string n in needed) if (b.Find(n) != null) hit++;
                if (hit > best) { best = hit; bestPath = file; }
            }
            catch { }
        }
        if (bestPath == null) return;
        var r = MessageBox.Show(this, "같은 폴더의 '" + Path.GetFileName(bestPath) + "'에 이 곡의 악기 " + best + "개가 있습니다. 불러올까요?",
            "Instrument bank", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r == DialogResult.Yes && ConfirmDiscardBank()) ws.OpenBank(bestPath);
    }

    private bool SaveSong(bool saveAs)
    {
        string path = ws.Song.FilePath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            var writable = new List<ISongFormat>();
            foreach (var f in ws.Formats.Songs) if (f.CanWrite) writable.Add(f);
            using (var dlg = new SaveFileDialog { Filter = FormatRegistry.BuildFilter(writable), DefaultExt = "rol", FileName = ws.Song.Title, Title = "Save song" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                path = dlg.FileName;
            }
        }
        try { ws.SaveSong(path); return true; }
        catch (Exception ex) { Fail("곡을 저장할 수 없습니다.", ex); return false; }
    }

    private void ExportScoreText()
    {
        using (var dlg = new SaveFileDialog { Filter = "Text score (*.txt)|*.txt", DefaultExt = "txt", FileName = ws.Song.Title + "_score", Title = "Export score" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var score = ScoreBuilder.Build(ws.Song, new ScoreOptions { QuantizeTicks = composer.Roll.SnapTicks });
                using (var fs = File.Create(dlg.FileName)) new TextScoreExporter().Export(score, fs);
            }
            catch (Exception ex) { Fail("악보를 내보낼 수 없습니다.", ex); }
        }
    }

    private void NewBank()
    {
        if (!ConfirmDiscardBank()) return;
        ws.NewBank();
    }

    /// <summary>File → Instrument Bank File: 뱅크 브라우저 (BNK 목록 → 악기 목록 → 뱅크 사용 / 악기 추가).</summary>
    private void OpenBankBrowser()
    {
        StopPlayback();
        using (var f = new BankBrowserForm(ws, ConfirmDiscardBank))
            f.ShowDialog(this);
        if (!composerMode) instrument.ApplyPreview();
        UpdateStatus();
    }

    private void OpenBank()
    {
        if (!ConfirmDiscardBank()) return;
        using (var dlg = new OpenFileDialog { Filter = FormatRegistry.BuildFilter(ws.Formats.Banks), Title = "Open instrument bank" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { ws.OpenBank(dlg.FileName); }
            catch (Exception ex) { Fail("뱅크를 열 수 없습니다.", ex); }
        }
    }

    private bool SaveBank(bool saveAs)
    {
        string path = ws.Bank.FilePath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            using (var dlg = new SaveFileDialog { Filter = FormatRegistry.BuildFilter(ws.Formats.Banks), DefaultExt = "bnk", FileName = "NEW.BNK", Title = "Save instrument bank" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                path = dlg.FileName;
            }
        }
        try { ws.SaveBank(path); UpdateStatus(); return true; }
        catch (Exception ex) { Fail("뱅크를 저장할 수 없습니다.", ex); return false; }
    }

    private void ImportInstrument()
    {
        using (var dlg = new OpenFileDialog { Filter = FormatRegistry.BuildFilter(ws.Formats.Instruments), Title = "Import instrument" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { ws.ImportInstrument(dlg.FileName); }
            catch (Exception ex) { Fail("악기를 가져올 수 없습니다.", ex); }
        }
    }

    private void ExportInstrument()
    {
        var ins = composerMode ? ws.CurrentInstrument : instrument.Current;
        if (ins == null) return;
        using (var dlg = new SaveFileDialog { Filter = FormatRegistry.BuildFilter(ws.Formats.Instruments), DefaultExt = "ins", FileName = ins.Name + ".ins", Title = "Export instrument" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { ws.ExportInstrument(dlg.FileName, ins); }
            catch (Exception ex) { Fail("악기를 내보낼 수 없습니다.", ex); }
        }
    }
}
