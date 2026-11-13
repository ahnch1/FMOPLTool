using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AdlibComposer.App.Controls;
using AdlibComposer.App.Shell;
using AdlibComposer.App.Views;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;
using AdlibComposer.Notation;

namespace AdlibComposer.App;

/// <summary>
/// 메인 창: 메뉴 + 툴바 + 왼쪽 사이드바(Composer / Instrument Maker / Jukebox / Note) + 페이지 머리글 + 페이지 + 진행 로그 + 상태 표시줄.
/// Composer와 Instrument Maker 페이지 안은 원본 화면 재현, 바깥 틀은 현대식.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class MainForm : Form
{
    private readonly Workspace ws = new Workspace();
    private AppSettings settings = AppSettings.Load();
    private readonly ComposerView composer;
    private readonly InstrumentView instrument;
    private readonly MenuStrip menu = new MenuStrip();
    private readonly StatusStrip status = new StatusStrip();
    private readonly ToolStripStatusLabel statusMain = new ToolStripStatusLabel();
    private readonly ToolStripStatusLabel statusSong = new ToolStripStatusLabel();
    private readonly ToolStripStatusLabel statusBank = new ToolStripStatusLabel();
    private readonly Timer timer = new Timer();
    private readonly ToolStripStatusLabel linkCfg = new ToolStripStatusLabel();
    private readonly ToolStripStatusLabel linkFolder = new ToolStripStatusLabel();

    // 셸 (현대식 틀)
    private readonly NavSidebar sidebar = new NavSidebar();
    private readonly ToolStrip toolbar = new ToolStrip();
    private readonly Panel header = new Panel();
    private readonly Label headerTitle = new Label();
    private readonly Panel pageHost = new Panel();
    private readonly LogPanel logPanel = new LogPanel();
    private readonly Splitter logSplitter = new Splitter();
    private readonly JukeboxView jukebox;
    private readonly OrgelView orgel;
    private readonly TrackerView trackerView;
    private readonly OrganizerView organizer;
    private readonly NoteView note;
    private ToolStripButton tbPlay, tbStop;

    private ToolStripMenuItem fileMenu, editMenu, playMenu, displayMenu, optionsMenu, instOptionMenu, helpMenu;
    private ToolStripMenuItem additiveItem, instMidiItem;
    private ToolStripMenuItem loopItem, showAllItem;
    private ToolStripMenuItem revertItem, editModeItem, basicTempoItem, ticksBeatItem, beatsMeasureItem;
    private ToolStripMenuItem percOnItem, percOffItem, percSopItem, feedbackItem, midiItem;
    private readonly List<ToolStripMenuItem> snapItems = new List<ToolStripMenuItem>();
    private readonly List<ToolStripMenuItem> bandItems = new List<ToolStripMenuItem>();
    private readonly List<ToolStripMenuItem> gridItems = new List<ToolStripMenuItem>();
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
    private readonly List<ToolStripMenuItem> previewItems = new List<ToolStripMenuItem>();
    private AppPage page = AppPage.Composer;
    private bool composerMode { get { return page == AppPage.Composer; } }
    private bool instrumentMode { get { return page == AppPage.Instrument; } }

    public static string VersionText
    {
        get
        {
            var v = typeof(MainForm).Assembly.GetName().Version;
            return v == null ? "" : v.Major + "." + v.Minor + "." + v.Build;
        }
    }
    private bool wasPlaying;

    public MainForm()
    {
        ApplyOplCore();
        Text = "FMOPL Tool";
        AppIcon.Apply(this);
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ShellTheme.ContentBack;
        Font = ShellTheme.Ui;
        KeyPreview = true;

        composer = new ComposerView(ws) { Dock = DockStyle.Fill };
        instrument = new InstrumentView(ws) { Dock = DockStyle.Fill, Visible = false };
        jukebox = new JukeboxView(ws, delegate { return settings; }, TryOpenAudio) { Dock = DockStyle.Fill, Visible = false };
        note = new NoteView(ws, delegate { return settings.FolderForSong(ws.Song.FilePath); }) { Dock = DockStyle.Fill, Visible = false };
        trackerView = new TrackerView(ws, delegate { return settings; }, TryOpenAudio) { Dock = DockStyle.Fill, Visible = false };
        organizer = new OrganizerView(delegate { return settings; }) { Dock = DockStyle.Fill, Visible = false };
        orgel = new OrgelView(ws, TryOpenAudio, delegate { return settings.FolderForSong(ws.Song.FilePath); }) { Dock = DockStyle.Fill, Visible = false };
        note.NoteActivated += delegate (int voice, int tick, int pitch) { ShowPage(AppPage.Composer); composer.GoTo(voice, tick, pitch); };

        BuildMenu();
        BuildToolbar();
        BuildStatus();
        BuildShell();
        MainMenuStrip = menu;

        ws.SongReplaced += delegate { composer.BindSong(); SyncMenus(); UpdateStatus(); };
        ws.BankReplaced += delegate { UpdateStatus(); };
        ws.CurrentInstrumentChanged += delegate { UpdateStatus(); };
        composer.SongModified += delegate { UpdateStatus(); };
        composer.Message += delegate (string m) { statusMain.Text = m; };
        Deactivate += delegate { composer.ReleaseKeys(); };
        composer.HoverNoteChanged += delegate (int n) { statusMain.Text = n >= 0 ? "Note " + NoteHelper.Name(n) + " (" + n + ")" : ""; };
        composer.Roll.EditCursorChanged += UpdateStatus;
        instrument.InstrumentEdited += UpdateStatus;
        instrument.ModeChanged += delegate { if (menu.Items.Count > 0) SyncMenus(); };

        timer.Interval = 30;
        timer.Tick += OnTimer;
        timer.Start();

        Shown += delegate { TryOpenAudio(); composer.Focus(); };
        FormClosing += OnClosing;
        FormClosed += delegate { timer.Stop(); ws.Dispose(); };

        AppLog.Info("FMOPL Tool", "v" + VersionText + " 시작 — 설정 파일 " + AppSettings.FilePath);
        if (settings.LoadWarning != null) AppLog.Warn("설정", settings.LoadWarning);
        SyncMenus();
        LoadStartupBank();
        UpdateHeader();
        UpdateStatus();
        if (settings.LoadWarning != null) statusMain.Text = settings.LoadWarning;
    }

    // ================================================================ 셸 (현대식 틀)

    /// <summary>
    /// 배치: 메뉴(위) · 툴바(위) · 상태 표시줄(아래) · 사이드바(왼쪽) · [머리글 / 페이지 / 로그].
    /// WinForms 도킹은 나중에 추가한 컨트롤이 바깥쪽에 붙으므로 안쪽부터 추가한다.
    /// </summary>
    private void BuildShell()
    {
        sidebar.Dock = DockStyle.Left;
        sidebar.Version = VersionText;
        sidebar.AppName = "FMOPL Tool";
        sidebar.Subtitle = "AdLib Composer · OPL2";
        sidebar.PageSelected += ShowPage;

        pageHost.Dock = DockStyle.Fill;
        pageHost.Controls.AddRange(new Control[] { composer, instrument, jukebox, note, orgel, trackerView, organizer });

        logPanel.Dock = DockStyle.Bottom;
        logPanel.Height = 140;
        logSplitter.Dock = DockStyle.Bottom;
        logSplitter.Height = 4;
        logSplitter.BackColor = ShellTheme.SideLine;
        logSplitter.MinExtra = 300;
        logSplitter.MinSize = 60;

        header.Dock = DockStyle.Top;
        header.Height = 46;
        header.BackColor = ShellTheme.ContentBack;
        header.Paint += delegate (object o, PaintEventArgs e) { using (var pen = new Pen(ShellTheme.HeaderLine)) e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };
        headerTitle.Dock = DockStyle.Fill;
        headerTitle.Font = ShellTheme.PageTitle;
        headerTitle.ForeColor = Color.FromArgb(32, 34, 38);
        headerTitle.TextAlign = ContentAlignment.MiddleLeft;
        headerTitle.Padding = new Padding(18, 0, 0, 0);
        header.Controls.Add(headerTitle);

        var content = new Panel { Dock = DockStyle.Fill, BackColor = ShellTheme.ContentBack };
        content.Controls.Add(pageHost);
        content.Controls.Add(logSplitter);
        content.Controls.Add(logPanel);
        content.Controls.Add(header);

        Controls.Add(content);
        Controls.Add(sidebar);
        Controls.Add(status);
        Controls.Add(toolbar);
        Controls.Add(menu);
    }

    private ToolStripButton ToolButton(string text, char glyph, string tip, EventHandler click)
    {
        var b = new ToolStripButton(text, GlyphImages.Get(glyph, 16, Color.FromArgb(50, 54, 62)), click)
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ToolTipText = tip,
            Padding = new Padding(4, 1, 4, 1),
        };
        return b;
    }

    private void BuildToolbar()
    {
        toolbar.GripStyle = ToolStripGripStyle.Hidden;
        toolbar.RenderMode = ToolStripRenderMode.System;
        toolbar.BackColor = Color.FromArgb(248, 249, 250);
        toolbar.Padding = new Padding(8, 3, 8, 3);
        toolbar.Font = ShellTheme.Ui;
        toolbar.ImageScalingSize = new Size(16, 16);
        tbPlay = ToolButton("재생", Glyphs.Play, "커서부터 재생 / 정지 (Space)", delegate { ShowPage(AppPage.Composer); TogglePlay(); });
        tbStop = ToolButton("정지", Glyphs.Stop, "정지 (Esc)", delegate { StopPlayback(); });
        var settingsButton = ToolButton("설정", Glyphs.Settings, "Preferences (Ctrl+,)", delegate { ShowPreferences(); });
        settingsButton.Alignment = ToolStripItemAlignment.Right;
        toolbar.Items.AddRange(new ToolStripItem[]
        {
            ToolButton("새 곡", Glyphs.NewFile, "새 곡 (Ctrl+N)", delegate { NewSong(); ShowPage(AppPage.Composer); }),
            ToolButton("열기", Glyphs.Open, "곡 열기 — ROL / IMS / SOP (Ctrl+O)", delegate { OpenSong(); }),
            ToolButton("저장", Glyphs.Save, "곡 저장 (Ctrl+S)", delegate { SaveSong(false); }),
            new ToolStripSeparator(),
            ToolButton("뱅크 브라우저", Glyphs.Library, "BNK 목록 · 악기 목록 · 불러오기 (Ctrl+B)", delegate { OpenBankBrowser(); }),
            new ToolStripSeparator(),
            tbPlay,
            tbStop,
            settingsButton,
        });
    }

    /// <summary>사이드바 / Ctrl+1~4: 페이지 바꾸기.</summary>
    private void ShowPage(AppPage target)
    {
        if (page == target) return;
        var old = PageControl(page) as IShellPage;
        if (old != null) old.OnHidden();   // 먼저: Jukebox가 멈춘 위치를 기억하도록
        StopPlayback();
        ws.Audio.PreviewAllOff();
        composer.ReleaseKeys();

        page = target;
        sidebar.Selected = target;
        composer.Visible = target == AppPage.Composer;
        instrument.Visible = target == AppPage.Instrument;
        jukebox.Visible = target == AppPage.Jukebox;
        note.Visible = target == AppPage.Note;
        orgel.Visible = target == AppPage.Orgel;
        trackerView.Visible = target == AppPage.Tracker;
        organizer.Visible = target == AppPage.Organizer;
        if (target == AppPage.Instrument) instrument.ApplyPreview();
        var shown = PageControl(target) as IShellPage;
        if (shown != null) shown.OnShown();

        UpdateHeader();
        SyncMenus();
        UpdateStatus();
        PageControl(target).Focus();
    }

    private Control PageControl(AppPage p)
    {
        switch (p)
        {
            case AppPage.Instrument: return instrument;
            case AppPage.Jukebox: return jukebox;
            case AppPage.Note: return note;
            case AppPage.Orgel: return orgel;
            case AppPage.Tracker: return trackerView;
            case AppPage.Organizer: return organizer;
        }
        return composer;
    }

    private void UpdateHeader()
    {
        string name = page == AppPage.Instrument ? "Instrument Maker" : page == AppPage.Orgel ? "Orgel Note" : page.ToString();
        headerTitle.Text = name + "  —  " + sidebar.HintFor(page);
    }

    private static void OpenInExplorer(string path, bool select)
    {
        try
        {
            if (select && File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            else System.Diagnostics.Process.Start("explorer.exe", "\"" + (Directory.Exists(path) ? path : Path.GetDirectoryName(path)) + "\"");
        }
        catch (Exception ex) { AppLog.Error("셸", "탐색기를 열 수 없습니다", ex); }
    }

    /// <summary>설정의 기본 뱅크(기본: 실행 폴더\resource\STANDARD.BNK)를 불러온다. 없으면 내장 새 뱅크 그대로.</summary>
    private void LoadStartupBank()
    {
        string path = settings.DefaultBankPath;
        if (!settings.LoadDefaultBank || path == null) return;
        if (!File.Exists(path))
        {
            statusMain.Text = "기본 뱅크를 찾을 수 없습니다: " + path + "  (File → Preferences)";
            AppLog.Warn("뱅크", "기본 뱅크를 찾을 수 없습니다: " + path);
            return;
        }
        try { ws.OpenBank(path); LogBank("기본 뱅크"); }
        catch (Exception ex) { statusMain.Text = "기본 뱅크를 열 수 없습니다: " + ex.Message; AppLog.Error("뱅크", "기본 뱅크를 열 수 없습니다", ex); }
    }

    private void LogBank(string what)
    {
        AppLog.Info("뱅크", what + ": " + (ws.Bank.FilePath ?? "(new bank)") + " (" + ws.Bank.UsedCount + "개)");
    }

    private void LogSong(string what)
    {
        var s = ws.Song;
        int notes = 0;
        foreach (var v in s.Voices) notes += v.Notes.Count;
        string mode = s.IsSop ? (s.IsMelodic ? "SOP 멜로딕 20" : "SOP 리듬 20") : s.IsMelodic ? "멜로딕 9" : "리듬 11";
        AppLog.Info("곡", what + ": " + (s.FilePath ?? s.Title) + " — " + mode + " 보이스, 노트 " + notes + "개, " + s.BasicTempo + " BPM, 박당 " + s.TickBeat + "틱");
    }

    /// <summary>설정의 OPL 에뮬레이터를 프로그램 전체(모든 OplChip)에 적용한다.</summary>
    private void ApplyOplCore()
    {
        var want = settings.UseNukedOpl ? OplCore.Nuked : OplCore.Builtin;
        if (OplChip.GlobalCore == want) return;
        OplChip.GlobalCore = want;
        AppLog.Info("설정", "OPL 에뮬레이터: " + (want == OplCore.Nuked ? "Nuked OPL3" : "내장"));
    }

    /// <summary>File → Preferences: 폴더 / 기본 뱅크 설정 (FMOPLTool.cfg).</summary>
    private void ShowPreferences()
    {
        using (var f = new PreferencesForm(settings))
        {
            if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
            settings = f.Result;
            ApplyOplCore();
            jukebox.SettingsChanged();
            if (f.LoadBankNow && settings.DefaultBankPath != null && File.Exists(settings.DefaultBankPath) && ConfirmDiscardBank())
            {
                try { StopPlayback(); ws.OpenBank(settings.DefaultBankPath); LogBank("기본 뱅크"); }
                catch (Exception ex) { Fail("뱅크를 열 수 없습니다.", ex); }
            }
            statusMain.Text = "Preferences saved: " + AppSettings.FilePath;
            AppLog.Info("설정", "저장: " + AppSettings.FilePath);
            UpdateStatus();
        }
    }

    /// <summary>곡 대화상자 왼쪽 목록에 ROL / IMS 폴더 바로가기를 넣는다.</summary>
    private void AddSongPlaces(FileDialog dlg)
    {
        foreach (var dir in new[] { settings.SongFolderPath, settings.ImsFolderPath, settings.SopFolderPath })
            if (Directory.Exists(dir)) dlg.CustomPlaces.Add(dir);
    }

    /// <summary>파일 대화상자 시작 폴더: 지금 파일이 있는 폴더, 없으면 설정 폴더.</summary>
    private static string StartDir(string currentFile, string settingFolder)
    {
        if (!string.IsNullOrEmpty(currentFile))
        {
            string d = Path.GetDirectoryName(currentFile);
            if (Directory.Exists(d)) return d;
        }
        return AppSettings.ExistingOrNull(settingFolder);
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
            Item("Preferences...", delegate { ShowPreferences(); }, Keys.Control | Keys.Oemcomma),
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
        var bands = new[] { BandTrack.Tempo, BandTrack.Instrument, BandTrack.Volume, BandTrack.Pitch, BandTrack.Pan };
        string[] bandNames = { "Tempo...", "Instrument", "Volume...", "Pitch...", "Pan (SOP)..." };
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
        var snapMenu = new ToolStripMenuItem("Note Length / Snap");
        for (int i = 0; i < ComposerView.LengthNames.Length; i++)
        {
            int idx = i;
            var it = Item(ComposerView.LengthNames[i] == "tick" ? "1 tick" : ComposerView.LengthNames[i] + " note", null);
            it.Tag = idx;
            it.Click += delegate { composer.LengthIndex = idx; SyncMenus(); };
            snapItems.Add(it);
            snapMenu.DropDownItems.Add(it);
        }
        snapMenu.DropDownItems.Add(new ToolStripSeparator());
        snapMenu.DropDownItems.Add(Item("Shorter", delegate { composer.LengthIndex = composer.LengthIndex + 1; SyncMenus(); }, Keys.None, "["));
        snapMenu.DropDownItems.Add(Item("Longer", delegate { composer.LengthIndex = composer.LengthIndex - 1; SyncMenus(); }, Keys.None, "]"));
        displayMenu.DropDownItems.Add(snapMenu);
        displayMenu.DropDownItems.Add(Item("Zoom In", delegate { composer.ZoomIn(); SyncMenus(); }, Keys.Control | Keys.Oemplus));
        displayMenu.DropDownItems.Add(Item("Zoom Out", delegate { composer.ZoomOut(); SyncMenus(); }, Keys.Control | Keys.OemMinus));

        // ---------------------------------------------------------------- F9 Options
        percOnItem = Item("With Percussion", delegate { ComposerOnly(delegate { composer.SetRhythmMode(true); }); SyncMenus(); });
        percOffItem = Item("Without Percussion", delegate { ComposerOnly(delegate { composer.SetRhythmMode(false); }); SyncMenus(); });
        // 이 프로그램의 추가 기능: SOP(OPL3) 작곡 — 보이스 20개 (1~6, BD SD TO CY HH, 12~20), 팬, 4-op 악기, .sop로 저장
        percSopItem = Item("With Percussion (SOP)", delegate { ComposerOnly(delegate { composer.SetMode(true, true); }); SyncMenus(); });
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
            percSopItem,
            new ToolStripSeparator(),
            feedbackItem,
            new ToolStripSeparator(),
            midiItem,
            new ToolStripSeparator(),
            Item("Pitch Bend Range...", delegate { PromptPitchBend(); }),
            Item("Master Volume...", delegate { PromptVolume(); }),
            new ToolStripSeparator(),
            Item("Preferences...", delegate { ShowPreferences(); }, Keys.None, "Ctrl+,"),
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

        helpMenu.DropDownItems.Insert(0, new ToolStripSeparator());
        helpMenu.DropDownItems.Insert(0, Item("Organizer", delegate { ShowPage(AppPage.Organizer); }, Keys.None, "Ctrl+7"));
        helpMenu.DropDownItems.Insert(0, Item("Tracker", delegate { ShowPage(AppPage.Tracker); }, Keys.None, "Ctrl+6"));
        helpMenu.DropDownItems.Insert(0, Item("Orgel Note", delegate { ShowPage(AppPage.Orgel); }, Keys.None, "Ctrl+5"));
        helpMenu.DropDownItems.Insert(0, Item("Note", delegate { ShowPage(AppPage.Note); }, Keys.None, "Ctrl+4"));
        helpMenu.DropDownItems.Insert(0, Item("Jukebox", delegate { ShowPage(AppPage.Jukebox); }, Keys.None, "Ctrl+3"));
        helpMenu.DropDownItems.Insert(0, Item("Instrument Maker", delegate { ShowPage(AppPage.Instrument); }, Keys.None, "Ctrl+2"));
        helpMenu.DropDownItems.Insert(0, Item("Composer", delegate { ShowPage(AppPage.Composer); }, Keys.None, "Ctrl+1"));

        menu.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, instOptionMenu, playMenu, displayMenu, optionsMenu, helpMenu });
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
            case BandTrack.Pan:
            {
                string v = Prompt.Show(this, "Pan", "Pan to insert: L (left), C (center), R (right) — SOP only", PanEvent.Label(composer.PanValue));
                if (v == null) break;
                v = v.Trim().ToUpperInvariant();
                if (v.StartsWith("L")) composer.PanValue = PanEvent.Left;
                else if (v.StartsWith("R")) composer.PanValue = PanEvent.Right;
                else if (v.StartsWith("C")) composer.PanValue = PanEvent.Center;
                break;
            }
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
        if (SopFormat.IsSop(path) && !ConfirmDiscardBank()) return;   // SOP는 곡 안 악기로 작업 뱅크도 다시 읽는다
        try { StopPlayback(); ws.OpenSong(path); LogSong("되돌리기"); }
        catch (Exception ex) { Fail("곡을 다시 읽을 수 없습니다.", ex); }
    }

    private void BuildStatus()
    {
        statusMain.Spring = true;
        statusMain.TextAlign = ContentAlignment.MiddleLeft;
        linkCfg.Text = "FMOPLTool.cfg";
        linkCfg.IsLink = true;
        linkCfg.ToolTipText = AppSettings.FilePath;
        linkCfg.Click += delegate { OpenInExplorer(AppSettings.FilePath, true); };
        linkFolder.Text = "실행 폴더";
        linkFolder.IsLink = true;
        linkFolder.ToolTipText = AppSettings.BaseDirectory;
        linkFolder.Click += delegate { OpenInExplorer(AppSettings.BaseDirectory, false); };
        status.Items.AddRange(new ToolStripItem[] { statusMain, statusSong, statusBank, linkCfg, linkFolder });
    }

    private void ComposerOnly(Action a) { if (composerMode) a(); }

    private void SyncMenus()
    {
        var song = ws.Song;
        percOnItem.Checked = !song.IsSop && !song.IsMelodic;
        percOffItem.Checked = !song.IsSop && song.IsMelodic;
        percSopItem.Checked = song.IsSop;
        percSopItem.Text = song.IsSop && song.IsMelodic ? "SOP (without percussion)" : "With Percussion (SOP)";
        foreach (var it in snapItems) it.Checked = (int)it.Tag == composer.LengthIndex;
        foreach (var it in gridItems) it.Checked = (int)it.Tag == composer.GridSize;
        foreach (var it in bandItems) it.Checked = (BandTrack)it.Tag == composer.Band;
        bandItems[0].ShortcutKeyDisplayString = composer.TempoValue.ToString("0.00", Inv);
        bandItems[2].ShortcutKeyDisplayString = composer.VolumeValue.ToString("0.00", Inv);
        bandItems[3].ShortcutKeyDisplayString = composer.PitchValue.ToString("0.00", Inv);
        bandItems[4].ShortcutKeyDisplayString = PanEvent.Label(composer.PanValue);
        bandItems[4].Enabled = song.IsSop;
        int cv = Math.Min(composer.Roll.CurrentVoice, Song.TotalVoices - 1);
        bandItems[1].ShortcutKeyDisplayString = "default " + Workspace.DefaultInstrumentFor(cv, song.IsMelodic);
        basicTempoItem.ShortcutKeyDisplayString = song.BasicTempo.ToString("0.0", Inv);
        ticksBeatItem.ShortcutKeyDisplayString = song.TickBeat.ToString();
        beatsMeasureItem.ShortcutKeyDisplayString = song.BeatsPerMeasure.ToString();
        revertItem.Enabled = !string.IsNullOrEmpty(song.FilePath);
        editModeItem.Checked = composer.Tool == RollTool.Select;
        showAllItem.Checked = composer.ShowAllVoices;
        editMenu.Visible = playMenu.Visible = displayMenu.Visible = composerMode;
        optionsMenu.Visible = composerMode;
        instOptionMenu.Visible = instrumentMode;
        if (tbPlay != null) tbPlay.Enabled = tbStop.Enabled = composerMode;
        foreach (var it in previewItems) it.Checked = (int)it.Tag == instrument.PreviewVoice;
        additiveItem.Checked = instrument.Additive;
        additiveItem.Enabled = instrument.AdditiveAvailable;
    }

    // ================================================================ 모드 / 상태

    private void UpdateStatus()
    {
        var s = ws.Song;
        string songName = (s.FilePath != null ? Path.GetFileName(s.FilePath) : s.Title) + (ws.SongDirty ? " *" : "");
        string bankName = (string.IsNullOrEmpty(ws.Bank.FilePath) ? "(new bank)" : Path.GetFileName(ws.Bank.FilePath)) + (ws.BankDirty ? " *" : "");
        statusSong.Text = songName + "  " + s.BasicTempo + " BPM  " + (s.IsSop ? (s.IsMelodic ? "SOP 20" : "SOP rhythm 20") : s.IsMelodic ? "9 voices" : "rhythm 11");
        statusBank.Text = "Bank: " + bankName + " (" + ws.Bank.UsedCount + ")";
        if (composerMode)
        {
            int c = composer.Roll.CursorTick;
            int m = c / Math.Max(1, s.MeasureTicks) + 1;
            int b = (c % Math.Max(1, s.MeasureTicks)) / Math.Max(1, s.TickBeat) + 1;
            int t = c % Math.Max(1, s.TickBeat);
            statusSong.Text += "   cursor " + m + ":" + b + ":" + t;
            Text = "FMOPL Tool - Composer - " + songName;
        }
        else if (instrumentMode)
        {
            Text = "FMOPL Tool - Instrument Maker - " + bankName + (instrument.Current != null ? " [" + instrument.Current.Name + "]" : "");
        }
        else Text = "FMOPL Tool - " + (page == AppPage.Orgel ? "Orgel Note" : page.ToString());
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
        catch (Exception ex) { statusMain.Text = "오디오 장치를 열 수 없습니다: " + ex.Message; AppLog.Error("오디오", "오디오 장치를 열 수 없습니다", ex); return false; }
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
        if (ws.Song.IsSop)
        {
            // SOP 모드: 곡 + 뱅크 → SOP로 바꿔 OPL3로 재생 (Jukebox와 같은 재생기). 보이스 하나만 = 나머지 끄기
            SopSong sop;
            try { sop = ws.BuildSop(); }
            catch (Exception ex) { Fail("SOP로 재생할 수 없습니다.", ex); return; }
            var opts = new PlaybackOptions();
            if (onlyVoice >= 0) for (int v = 0; v < PlaybackOptions.Voices; v++) opts.VoiceMute[v] = v != onlyVoice;
            sopLoop = loopItem.Checked;
            sopOnlyVoice = onlyVoice;
            ws.Audio.PlaySop(sop, tick, opts);
            return;
        }
        ws.Audio.Play(ws.Song, ws.Bank, tick, ls, le, onlyVoice);
    }

    /// <summary>SOP 재생기에는 되풀이가 없어 끝나면 처음부터 다시 튼다 (Loop Playback).</summary>
    private bool sopLoop;
    private int sopOnlyVoice = -1;

    private void StopPlayback()
    {
        sopLoop = false;
        ws.Audio.Stop();
        composer.UpdatePlayback(-1, false);
        wasPlaying = false;
    }

    private void OnTimer(object sender, EventArgs e)
    {
        if (!composerMode) return;
        bool playing = ws.Audio.IsPlaying;
        if (!playing && wasPlaying && sopLoop && ws.Song.IsSop) { PlayFrom(0, sopOnlyVoice); playing = ws.Audio.IsPlaying; }
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
                if (composerMode) editMenu.ShowDropDown(); else if (instrumentMode) instOptionMenu.ShowDropDown();
                return true;
            case Keys.F7: if (composerMode) { playMenu.ShowDropDown(); return true; } break;
            case Keys.F8: if (composerMode) { displayMenu.ShowDropDown(); return true; } break;
            case Keys.F9: if (composerMode) { optionsMenu.ShowDropDown(); return true; } break;
            case Keys.F10: helpMenu.ShowDropDown(); return true;
            case Keys.F1: if (instrumentMode) { instrument.FocusColumn(true); return true; } break;
            case Keys.F2: if (instrumentMode) { instrument.FocusColumn(false); return true; } break;
            case Keys.F3: if (instrumentMode) { instrument.ShowPair(false); return true; } break;   // 4-op: 오퍼레이터 1·2
            case Keys.F4: if (instrumentMode) { instrument.ShowPair(true); return true; } break;    // 4-op: 오퍼레이터 3·4
            case Keys.Control | Keys.D1: ShowPage(AppPage.Composer); return true;
            case Keys.Control | Keys.D2: ShowPage(AppPage.Instrument); return true;
            case Keys.Control | Keys.D3: ShowPage(AppPage.Jukebox); return true;
            case Keys.Control | Keys.D4: ShowPage(AppPage.Note); return true;
            case Keys.Control | Keys.D5: ShowPage(AppPage.Orgel); return true;
            case Keys.Control | Keys.D6: ShowPage(AppPage.Tracker); return true;
            case Keys.Control | Keys.D7: ShowPage(AppPage.Organizer); return true;
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
            case Keys.Escape: if (composerMode && ws.Audio.IsPlaying) { StopPlayback(); return true; } break;   // 재생 중이 아니면 아래에서 선택 해제
        }
        // Composer 키보드 조작: 화살표/Tab/Enter 등은 포커스된 버튼·스크롤바가 먼저 가져가지 않도록 여기서 처리
        if (composerMode && !text && !menu.IsDropDownOpen() && composer.HandleKeyDown(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (instrumentMode && !instrument.IsEditingText && !e.Control && !e.Alt && instrument.HandleKeyDown(e.KeyCode))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (instrumentMode && instrument.HandleKeyUp(e.KeyCode)) e.Handled = true;
        if (composerMode && composer.HandleKeyUp(e.KeyCode)) e.Handled = true;
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
        if (!ConfirmDiscardSong() || !ConfirmDiscardBank() || !trackerView.ConfirmDiscard()) e.Cancel = true;
    }

    private void Fail(string what, Exception ex)
    {
        AppLog.Error("오류", what.TrimEnd('.'), ex);
        MessageBox.Show(this, what + "\n\n" + ex.Message, "AdLib Composer", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void NewSong()
    {
        if (!ConfirmDiscardSong()) return;
        StopPlayback();
        ws.NewSong();
        LogSong("새 곡");
    }

    private void OpenSong()
    {
        if (!ConfirmDiscardSong()) return;
        var readable = new List<ISongFormat>();
        foreach (var f in ws.Formats.Songs) if (f.CanRead) readable.Add(f);
        var openFormats = new List<IFileFormat>(readable) { SopFormat.FileInfo };
        using (var dlg = new OpenFileDialog { Filter = FormatRegistry.BuildFilter(openFormats), Title = "Open song", InitialDirectory = StartDir(ws.Song.FilePath, settings.FolderForSong(ws.Song.FilePath)) })
        {
            AddSongPlaces(dlg);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            bool sop = SopFormat.IsSop(dlg.FileName);
            if (sop && !ConfirmDiscardBank()) return;   // SOP는 곡 안 악기로 작업 뱅크를 바꾼다
            try
            {
                StopPlayback();
                ws.OpenSong(dlg.FileName);
                LogSong("열기");
                if (sop) LogBank("SOP 곡 안 악기");
                if (ws.Bank.UsedCount <= 1) OfferBankFor(dlg.FileName);
                var missing = ws.MissingInstruments();
                if (missing.Count > 0) AppLog.Warn("곡", "뱅크에 없는 악기 " + missing.Count + "개 (기본 악기로 재생): " + string.Join(", ", missing.ToArray()));
                if (page != AppPage.Composer) ShowPage(AppPage.Composer);
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
        if (r == DialogResult.Yes && ConfirmDiscardBank()) { ws.OpenBank(bestPath); LogBank("곡 폴더의 뱅크"); }
    }

    private bool SaveSong(bool saveAs)
    {
        string path = ws.Song.FilePath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            var writable = new List<IFileFormat>();
            foreach (var f in ws.Formats.Songs) if (f.CanWrite) writable.Add(f);
            writable.Add(SopFormat.FileInfo);
            // 지금 곡의 형식(ROL/IMS/SOP)을 기본 선택하고, 그 형식의 폴더에서 시작한다. SOP 모드 곡은 SOP만
            string ext = string.IsNullOrEmpty(path) ? ".rol" : Path.GetExtension(path).ToLowerInvariant();
            if (ws.Song.IsSop) { ext = ".sop"; writable.RemoveAll(f => f != SopFormat.FileInfo); }
            int filterIndex = Math.Max(0, writable.FindIndex(f => Array.IndexOf(f.Extensions, ext) >= 0)) + 1;
            using (var dlg = new SaveFileDialog
            {
                Filter = FormatRegistry.BuildFilter(writable), FilterIndex = filterIndex, DefaultExt = ext.TrimStart('.'), AddExtension = true,
                FileName = ws.Song.Title, Title = "Save song", InitialDirectory = StartDir(path, settings.FolderForSong(ext))
            })
            {
                AddSongPlaces(dlg);
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                path = dlg.FileName;
            }
        }
        try { ws.SaveSong(path); AppLog.Info("곡", "저장: " + path); return true; }
        catch (Exception ex) { Fail("곡을 저장할 수 없습니다.", ex); return false; }
    }

    private void ExportScoreText()
    {
        using (var dlg = new SaveFileDialog { Filter = "Text score (*.txt)|*.txt", DefaultExt = "txt", FileName = ws.Song.Title + "_score", Title = "Export score", InitialDirectory = StartDir(ws.Song.FilePath, settings.FolderForSong(ws.Song.FilePath)) })
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
        LogBank("새 뱅크");
    }

    /// <summary>File → Instrument Bank File: 뱅크 브라우저 (BNK 목록 → 악기 목록 → 뱅크 사용 / 악기 추가).</summary>
    private void OpenBankBrowser()
    {
        StopPlayback();
        using (var f = new BankBrowserForm(ws, ConfirmDiscardBank, settings.BankFolderPath))
        {
            f.ShowDialog(this);
            if (f.Changed) LogBank("뱅크 브라우저");
        }
        if (instrumentMode) instrument.ApplyPreview();
        UpdateStatus();
    }

    private void OpenBank()
    {
        if (!ConfirmDiscardBank()) return;
        using (var dlg = new OpenFileDialog { Filter = FormatRegistry.BuildFilter(ws.Formats.Banks), Title = "Open instrument bank", InitialDirectory = StartDir(ws.Bank.FilePath, settings.BankFolderPath) })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { ws.OpenBank(dlg.FileName); LogBank("열기"); }
            catch (Exception ex) { Fail("뱅크를 열 수 없습니다.", ex); }
        }
    }

    private bool SaveBank(bool saveAs)
    {
        string path = ws.Bank.FilePath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            using (var dlg = new SaveFileDialog { Filter = FormatRegistry.BuildFilter(ws.Formats.Banks), DefaultExt = "bnk", FileName = "NEW.BNK", Title = "Save instrument bank", InitialDirectory = StartDir(ws.Bank.FilePath, settings.BankFolderPath) })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                path = dlg.FileName;
            }
        }
        int fourOps = 0;
        foreach (var i in ws.Bank.Instruments) if (i.IsUsed && i.Is4Op) fourOps++;
        if (fourOps > 0) AppLog.Warn("뱅크", "4-op 악기 " + fourOps + "개는 BNK에 오퍼레이터 1·2만 저장됩니다 (3·4는 SOP 곡으로 저장할 때만 들어감)");
        try { ws.SaveBank(path); LogBank("저장"); UpdateStatus(); return true; }
        catch (Exception ex) { Fail("뱅크를 저장할 수 없습니다.", ex); return false; }
    }

    private void ImportInstrument()
    {
        using (var dlg = new OpenFileDialog { Filter = FormatRegistry.BuildFilter(ws.Formats.Instruments), Title = "Import instrument", InitialDirectory = StartDir(ws.Bank.FilePath, settings.BankFolderPath) })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { var ins = ws.ImportInstrument(dlg.FileName); AppLog.Info("악기", "가져오기: " + dlg.FileName + " → " + ins.Name); }
            catch (Exception ex) { Fail("악기를 가져올 수 없습니다.", ex); }
        }
    }

    private void ExportInstrument()
    {
        var ins = composerMode ? ws.CurrentInstrument : instrument.Current;
        if (ins == null) return;
        using (var dlg = new SaveFileDialog { Filter = FormatRegistry.BuildFilter(ws.Formats.Instruments), DefaultExt = "ins", FileName = ins.Name + ".ins", Title = "Export instrument", InitialDirectory = StartDir(ws.Bank.FilePath, settings.BankFolderPath) })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (ins.Is4Op) AppLog.Warn("악기", ins.Name + ": 4-op 악기는 INS에 오퍼레이터 1·2만 저장됩니다");
            try { ws.ExportInstrument(dlg.FileName, ins); AppLog.Info("악기", "내보내기: " + ins.Name + " → " + dlg.FileName); }
            catch (Exception ex) { Fail("악기를 내보낼 수 없습니다.", ex); }
        }
    }
}
