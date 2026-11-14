using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AdlibComposer.App.Jukebox;
using AdlibComposer.App.Shell;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Views;

/// <summary>
/// Jukebox 페이지: 플레이어 데크. 곡은 창 왼쪽의 공용 폴더 / 파일 목록(<see cref="FileBrowser"/>)에서 고른다.
/// SOP(OPL3, 최대 20트랙 · 4-op · 스테레오)는 곡 안의 악기로 AudioEngine.PlaySop이 재생한다 (뱅크 필요 없음).
/// 데크: 곡 정보(곡 파일 · 불러온 악기 수/전체, 뱅크 · 악기 수, 가사 파일), 가사(같은 이름 ISS, 조합형 → 유니코드),
/// 진행 막대(끌어서 이동), 재생 · 정지 · ±5초 · 이전/다음 곡, 속도 · 키(남성/여성) · 볼륨, 채널 믹서(악기 유무 · 연주 표시 · 음량 · 켜기/끄기 · 음높이).
/// 같은 이름의 BNK가 곡 옆에 있으면 그 뱅크를 먼저 쓰고, 없으면 설정의 기본 뱅크(STANDARD.BNK)를 쓴다.
/// 재생은 공유 오디오 엔진(Workspace.Audio)을 쓰고 Composer의 곡/뱅크는 건드리지 않는다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class JukeboxView : UserControl, IShellPage
{
    private static readonly string[] SongExtensions = { ".rol", ".ims", ".sop", ".s3m", ".stm", ".mod", ".nst", ".mid", ".midi", ".wrk" };

    private readonly Workspace ws;
    private readonly Func<AppSettings> settings;
    private readonly Func<bool> openAudio;

    // 왼쪽 공용 폴더 / 파일 목록 (MainForm)
    private readonly FileBrowser browser;

    // 오른쪽 데크
    private readonly PlayerDisplay display = new PlayerDisplay();
    private readonly LyricsPanel lyrics = new LyricsPanel();
    private readonly SeekBar seek = new SeekBar();
    private readonly Label posLabel = new Label(), lenLabel = new Label();
    private readonly ChannelMixer mixer = new ChannelMixer();
    private readonly Stepper speedStep = new Stepper(), keyStep = new Stepper(), volStep = new Stepper();
    private readonly CheckBox autoNext = new CheckBox();
    private readonly CheckBox rawTone = new CheckBox(), vintageSpeaker = new CheckBox();
    private Button playButton;
    private readonly Timer timer = new Timer { Interval = 33 };
    private readonly ToolTip tips = new ToolTip();

    // 재생 상태
    private enum PlayState { Empty, Stopped, Playing, Paused }
    private PlayState state = PlayState.Empty;
    private readonly PlaybackOptions options = new PlaybackOptions();
    private int volumePercent = 100;
    private Song song;
    /// <summary>SOP 곡이면 이것 (song은 null).</summary>
    private SopSong sopSong;
    /// <summary>Scream Tracker 곡(STM/S3M)이면 이것. 위치 = ms.</summary>
    private TrackerModule trackerSong;
    /// <summary>MIDI 곡(.mid / .wrk)이면 이것. 위치 = MIDI 틱.</summary>
    private MidiSong midiSong;
    private GmBank gmBank;
    private string gmBankKey;
    private InstrumentBank bank;
    private IssLyrics iss;
    private TempoMap tempoMap;
    private double totalSeconds;
    private double pausedTick;
    private string currentPath;
    private List<string> playlist = new List<string>();
    private readonly VoiceState[] voiceStates = new VoiceState[PlaybackOptions.Voices];
    private readonly float[] levels = new float[PlaybackOptions.Voices];
    private readonly int[] lastCounts = new int[PlaybackOptions.Voices];
    private readonly bool[] voiceUsed = new bool[PlaybackOptions.Voices];
    private InstrumentBank defaultBank;
    private string defaultBankPath;

    public JukeboxView(Workspace ws, Func<AppSettings> settings, Func<bool> openAudio, FileBrowser browser)
    {
        this.ws = ws;
        this.browser = browser;
        this.settings = settings;
        this.openAudio = openAudio;
        BackColor = DeckTheme.Back;
        DoubleBuffered = true;

        var deck = new Panel { Dock = DockStyle.Fill, BackColor = DeckTheme.Back };
        Controls.Add(deck);
        BuildDeck(deck);

        timer.Tick += delegate { Tick(); };
        RebuildMixerRows();
        UpdateStepperTexts();
    }

    /// <summary>Jukebox가 재생하는 형식인가 (MP3 · WAV는 Transcribe).</summary>
    public static bool CanPlay(string path)
    {
        return Array.IndexOf(SongExtensions, (Path.GetExtension(path) ?? "").ToLowerInvariant()) >= 0;
    }

    /// <summary>공용 파일 목록에서 재생할 수 있는 곡만 (보이는 순서).</summary>
    private List<string> CurrentListPaths()
    {
        return browser.Paths.Where(CanPlay).ToList();
    }

    private void MarkPlaying()
    {
        browser.MarkCurrent(currentPath);
    }

    /// <summary>공용 파일 목록에서 고른 곡을 재생한다 (다음 곡 목록 = 그 폴더).</summary>
    public void Play(string path)
    {
        PlayFile(path, true);
    }

    // ================================================================ 오른쪽: 데크

    private void BuildDeck(Control host)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14, 12, 14, 12), BackColor = DeckTheme.Back };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));   // 정보
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));   // 가사
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));    // 진행
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));    // 재생 버튼
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));    // 속도 / 키 / 볼륨
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // 채널
        host.Controls.Add(table);

        display.Dock = DockStyle.Fill;
        display.Margin = new Padding(0, 0, 0, 8);
        table.Controls.Add(display, 0, 0);

        lyrics.Dock = DockStyle.Fill;
        lyrics.Margin = new Padding(0, 0, 0, 6);
        table.Controls.Add(lyrics, 0, 1);

        // 진행 막대 + 시간
        var seekRow = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = DeckTheme.Back };
        foreach (var l in new[] { posLabel, lenLabel })
        {
            l.Dock = l == posLabel ? DockStyle.Left : DockStyle.Right;
            l.Width = 54;
            l.Font = DeckTheme.Mono;
            l.ForeColor = DeckTheme.Lcd;
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Text = "00:00";
        }
        seek.Dock = DockStyle.Fill;
        seek.Enabled = false;
        seek.SeekRequested += f => SeekToFraction(f);
        seek.Scrubbing += f => posLabel.Text = DeckTheme.Clock(f * totalSeconds);
        seekRow.Controls.Add(seek);
        seekRow.Controls.Add(posLabel);
        seekRow.Controls.Add(lenLabel);
        table.Controls.Add(seekRow, 0, 2);

        // 재생 버튼
        var transport = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = DeckTheme.Back, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        transport.Controls.Add(DeckButton('', "", "이전 곡 (PageUp)", delegate { PlayNeighbor(-1); }));
        transport.Controls.Add(DeckButton('', "5초", "뒤로 5초 (←)", delegate { SeekSeconds(-5); }));
        playButton = DeckButton('', "재생", "재생 / 일시정지 (Space)", delegate { TogglePlay(); }, true);
        transport.Controls.Add(playButton);
        transport.Controls.Add(DeckButton('', "정지", "정지 (Esc)", delegate { StopPlay(); }));
        transport.Controls.Add(DeckButton('', "5초", "앞으로 5초 (→)", delegate { SeekSeconds(5); }));
        transport.Controls.Add(DeckButton('', "", "다음 곡 (PageDown)", delegate { PlayNeighbor(1); }));
        autoNext.Text = "다음 곡 자동 재생";
        autoNext.Checked = true;
        autoNext.ForeColor = DeckTheme.Text;
        autoNext.Font = ShellTheme.Ui;
        autoNext.AutoSize = true;
        autoNext.Margin = new Padding(18, 11, 0, 0);
        transport.Controls.Add(autoNext);
        rawTone.Text = "거친 음색";
        vintageSpeaker.Text = "그 시절 스피커";
        foreach (var cb in new[] { rawTone, vintageSpeaker })
        {
            cb.ForeColor = DeckTheme.Text;
            cb.Font = ShellTheme.Ui;
            cb.AutoSize = true;
            cb.Margin = new Padding(cb == rawTone ? 18 : 6, 11, 0, 0);
            transport.Controls.Add(cb);
        }
        rawTone.Checked = settings().JukeboxRawTone;
        vintageSpeaker.Checked = settings().JukeboxVintageSpeaker;
        tips.SetToolTip(rawTone, "끔 = 표준: 오늘 FM 카드를 새로 설계했다면 냈을 소리 (되먹임을 조금 줄이고 12kHz 위를 살짝 깎음)\n켬 = 거칠게: 칩이 낸 소리 그대로 · ROL / IMS / SOP에만");
        tips.SetToolTip(vintageSpeaker, "모니터 옆에 놓던 작은 PC 스피커로 듣는 소리 흉내 (저음이 빠지고 중고음이 튐)");
        rawTone.CheckedChanged += delegate { settings().JukeboxRawTone = rawTone.Checked; ApplySound(); SaveSettings(); };
        vintageSpeaker.CheckedChanged += delegate { settings().JukeboxVintageSpeaker = vintageSpeaker.Checked; ApplySound(); SaveSettings(); };
        table.Controls.Add(transport, 0, 3);

        // 속도 / 키 / 볼륨
        var adjust = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = DeckTheme.Back, WrapContents = false };
        speedStep.Caption = "속도";
        speedStep.MinusText = "느리게";
        speedStep.PlusText = "빠르게";
        speedStep.Width = 230;
        speedStep.ValueText = () => Math.Round(options.Speed * 100) + "%";
        speedStep.Step += d => SetSpeed(Math.Round(options.Speed * 100) + d * 5);
        keyStep.Caption = "키";
        keyStep.MinusText = "♂ 남성";
        keyStep.PlusText = "여성 ♀";
        keyStep.Width = 210;
        keyStep.ValueText = () => DeckTheme.KeyText(options.Transpose);
        keyStep.Step += d => SetKey(options.Transpose + d);
        volStep.Caption = "볼륨";
        volStep.Width = 150;
        volStep.ValueText = () => volumePercent + "%";
        volStep.Step += d => SetVolume(volumePercent + d * 10);
        tips.SetToolTip(speedStep, "재생 속도 50% ~ 200% (휠로도 조절) · 더블클릭은 없음, 초기화는 채널 믹서의 '모두 초기화'");
        tips.SetToolTip(keyStep, "키: 남성(낮게) / 여성(높게), 반음 단위 · 드럼은 그대로");
        tips.SetToolTip(volStep, "볼륨 0% ~ 150%");
        foreach (var st in new[] { speedStep, keyStep, volStep }) { st.Margin = new Padding(0, 4, 22, 0); adjust.Controls.Add(st); }
        var reset = DeckButton('\uE72C', "초기화", "속도 · 키 · 채널 켜기/음높이를 처음 상태로 (볼륨은 그대로)", delegate { ResetAdjustments(); });
        reset.Height = 32;
        reset.Width = 84;
        reset.Margin = new Padding(0, 5, 0, 0);
        adjust.Controls.Add(reset);
        table.Controls.Add(adjust, 0, 4);

        mixer.Dock = DockStyle.Fill;
        mixer.Margin = new Padding(0, 6, 0, 0);
        mixer.MuteToggled += v => SetMute(v, !options.VoiceMute[v]);
        mixer.SoloRequested += Solo;
        mixer.PitchStep += (v, d) => SetVoicePitch(v, options.VoiceTranspose[v] + d);
        table.Controls.Add(mixer, 0, 5);
    }

    private Button DeckButton(char glyph, string text, string tip, EventHandler click, bool primary = false)
    {
        var b = new Button
        {
            FlatStyle = FlatStyle.Flat, Height = 38, Width = string.IsNullOrEmpty(text) ? 42 : primary ? 92 : 68,
            BackColor = primary ? ShellTheme.Accent : Color.FromArgb(42, 47, 58), ForeColor = Color.White,
            Font = ShellTheme.Ui, Text = string.IsNullOrEmpty(text) ? "" : " " + text, Image = GlyphImages.Get(glyph, 16, Color.White),
            ImageAlign = string.IsNullOrEmpty(text) ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText, Margin = new Padding(0, 0, 6, 0), TabStop = false, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? ShellTheme.AccentDark : DeckTheme.PanelLine;
        b.FlatAppearance.MouseOverBackColor = primary ? ShellTheme.AccentDark : Color.FromArgb(58, 65, 80);
        b.Click += click;
        tips.SetToolTip(b, tip);
        return b;
    }

    // ================================================================ 곡 불러오기

    private void PlayFile(string path, bool fromList)
    {
        if (path == null) return;
        try { path = Path.GetFullPath(path); } catch (Exception) { }
        if (SopFormat.IsSop(path)) { PlaySopFile(path, fromList); return; }
        if (TrackerFiles.IsTracker(path)) { PlayTrackerFile(path, fromList); return; }
        if (MidiFiles.IsMidi(path)) { PlayMidiFile(path, fromList); return; }
        Song s;
        try
        {
            var fmt = ws.Formats.FindSong(path);
            if (fmt == null || !fmt.CanRead) throw new InvalidDataException("읽을 수 없는 곡 형식입니다.");
            using (var fs = File.OpenRead(path)) s = fmt.Read(fs);
            s.FilePath = path;
            s.Title = System.Text.RegularExpressions.Regex.Replace(s.Title ?? "", @"\s{2,}", " ").Trim();   // IMS 제목은 칸을 맞추느라 공백이 많다
            if (s.Title.Length == 0 || s.Title == "Untitled") s.Title = Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception ex)
        {
            AppLog.Error("Jukebox", "곡을 열 수 없습니다: " + Path.GetFileName(path), ex);
            return;
        }

        StopEngine();
        song = s;
        sopSong = null;
        trackerSong = null;
        midiSong = null;
        currentPath = path;
        if (fromList) playlist = CurrentListPaths();
        bool sameName;
        bank = FindBank(path, out sameName);
        iss = FindLyrics(path);
        tempoMap = new TempoMap(song);
        totalSeconds = tempoMap.SecondsAt(song.LengthTicks);
        pausedTick = 0;

        // 곡마다 채널 조절은 처음 상태로 (속도 / 키 / 볼륨은 유지)
        for (int v = 0; v < PlaybackOptions.Voices; v++) { options.VoiceMute[v] = false; options.VoiceTranspose[v] = 0; levels[v] = 0; }

        // 악기 수: 곡이 쓰는 서로 다른 악기 이름 / 그중 뱅크에 있는 것
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int v = 0; v < song.VoiceCount; v++)
        {
            voiceUsed[v] = song.Voices[v].Notes.Count > 0;
            foreach (var t in song.Voices[v].Timbres)
                if (!string.IsNullOrWhiteSpace(t.InstrumentName)) used.Add(t.InstrumentName.Trim());
        }
        int loaded = used.Count(n => bank != null && bank.Find(n) != null);

        display.Title = song.Title;
        display.SongFile = Path.GetFileName(path);
        display.Loaded = loaded;
        display.Total = used.Count;
        display.BankFile = bank != null && bank.FilePath != null ? Path.GetFileName(bank.FilePath) : "(뱅크 없음)";
        display.BankCount = bank != null ? bank.UsedCount : 0;   // BNK의 빈 예약 칸은 빼고
        display.BankSameName = sameName;
        display.LyricFile = iss != null ? Path.GetFileNameWithoutExtension(path) + ".ISS" + (iss.Encoding == KoreanText.Kind.Wansung ? " · 완성형" : " · 조합형") : null;
        display.TotalSeconds = totalSeconds;
        display.Mode = (song.IsMelodic ? "멜로디 9채널" : "리듬 11채널") + " · " + song.BeatsPerMeasure + "/4 · 박당 " + song.TickBeat + "틱";
        lyrics.Lyrics = iss;
        lyrics.State = null;
        lenLabel.Text = DeckTheme.Clock(totalSeconds);
        seek.Enabled = true;
        RebuildMixerRows();
        MarkPlaying();

        string missing = string.Join(", ", used.Where(n => bank == null || bank.Find(n) == null));
        AppLog.Info("Jukebox", "재생: " + Path.GetFileName(path) + " — 악기 " + loaded + "/" + used.Count + ", 뱅크 " + display.BankFile + (sameName ? " (같은 이름)" : "")
            + (iss != null ? ", 가사 " + iss.Lines.Count + "줄" : ""), missing.Length > 0 ? "뱅크에 없는 악기: " + missing : null);

        StartAt(0);
    }

    /// <summary>SOP 곡: 악기가 곡 안에 들어 있어 뱅크를 찾지 않는다. 가사는 ROL/IMS처럼 같은 이름의 ISS.</summary>
    private void PlaySopFile(string path, bool fromList)
    {
        SopSong s;
        try { s = SopFormat.Load(path); }
        catch (Exception ex)
        {
            AppLog.Error("Jukebox", "곡을 열 수 없습니다: " + Path.GetFileName(path), ex);
            return;
        }
        string title = System.Text.RegularExpressions.Regex.Replace(s.Title ?? "", @"\s{2,}", " ").Trim();
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(path);

        StopEngine();
        song = null;
        sopSong = s;
        trackerSong = null;
        midiSong = null;
        currentPath = path;
        if (fromList) playlist = CurrentListPaths();
        bank = null;
        iss = FindLyrics(path);
        tempoMap = null;
        totalSeconds = s.SecondsAt(s.LengthTicks);
        pausedTick = 0;
        for (int v = 0; v < PlaybackOptions.Voices; v++) { options.VoiceMute[v] = false; options.VoiceTranspose[v] = 0; levels[v] = 0; voiceUsed[v] = false; }

        var used = new HashSet<int>();
        int fourOp = 0;
        for (int t = 0; t < s.TrackCount; t++)
        {
            voiceUsed[t] = s.Tracks[t].NoteCount > 0;
            foreach (var e in s.Tracks[t].Events)
                if (e.Type == SopEventType.Instrument && e.Value < s.Instruments.Count && !s.Instruments[e.Value].IsEmpty && used.Add(e.Value) && s.Instruments[e.Value].Is4Op) fourOp++;
        }
        int active = voiceUsed.Count(u => u);

        display.Title = title;
        display.SongFile = Path.GetFileName(path);
        display.Loaded = used.Count;
        display.Total = used.Count;
        display.BankFile = "곡 안 악기 " + s.Instruments.Count(i => !i.IsEmpty) + "개" + (fourOp > 0 ? " (4-op " + fourOp + ")" : "");
        display.BankCount = used.Count;
        display.BankSameName = false;
        display.LyricFile = iss != null ? Path.GetFileNameWithoutExtension(path) + ".ISS" + (iss.Encoding == KoreanText.Kind.Wansung ? " · 완성형" : " · 조합형") : null;
        display.TotalSeconds = totalSeconds;
        display.Mode = "SOP · OPL3 " + active + "/" + s.TrackCount + "트랙" + (s.Percussive ? " · 리듬" : "") + " · " + s.BeatsPerMeasure + "/4 · 박당 " + s.TickBeat + "틱";
        lyrics.Lyrics = iss;
        lyrics.State = null;
        lenLabel.Text = DeckTheme.Clock(totalSeconds);
        seek.Enabled = true;
        RebuildMixerRows();
        MarkPlaying();

        AppLog.Info("Jukebox", "재생: " + Path.GetFileName(path) + " — SOP " + active + "트랙, 악기 " + used.Count + "개" + (fourOp > 0 ? " (4-op " + fourOp + ")" : "")
            + (iss != null ? ", 가사 " + iss.Lines.Count + "줄" : ""));
        StartAt(0);
    }

    /// <summary>
    /// Scream Tracker 곡: 악기는 곡 안에 있고(샘플 · AdLib), 가사 없음. 길이는 끝(또는 되돌아가는 곳)까지 빨리 돌려 잰다.
    /// 위치 단위는 ms (SecondsAt / TickAt이 1000으로 나누고 곱한다).
    /// </summary>
    private void PlayTrackerFile(string path, bool fromList)
    {
        TrackerModule m;
        try { m = TrackerFiles.Load(path); }
        catch (Exception ex)
        {
            AppLog.Error("Jukebox", "곡을 열 수 없습니다: " + Path.GetFileName(path), ex);
            return;
        }
        StopEngine();
        song = null; sopSong = null; midiSong = null;
        trackerSong = m;
        currentPath = path;
        if (fromList) playlist = CurrentListPaths();
        bank = null;
        iss = null;
        tempoMap = null;
        totalSeconds = TrackerPlayer.MeasureSeconds(m);
        pausedTick = 0;
        int channels = m.UsedChannelCount;
        for (int v = 0; v < PlaybackOptions.Voices; v++) { options.VoiceMute[v] = false; options.VoiceTranspose[v] = 0; levels[v] = 0; voiceUsed[v] = false; }
        foreach (var p in m.Patterns)
            for (int r = 0; r < TrackerPattern.Rows; r++)
                for (int c = 0; c < Math.Min(channels, PlaybackOptions.Voices); c++)
                    if (p[r, c].HasNote && m.IsChannelUsed(c)) voiceUsed[c] = true;
        int samples = 0, adlib = 0;
        foreach (var s in m.Samples) { if (s.IsSample) samples++; else if (s.IsAdlib) adlib++; }
        string title = (m.Title ?? "").Trim();
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(path);
        bool stm = m.Kind == TrackerKind.Stm;

        display.Title = title;
        display.SongFile = Path.GetFileName(path);
        display.Loaded = samples + adlib;
        display.Total = samples + adlib;
        display.BankFile = "곡 안 샘플 " + samples + "개" + (adlib > 0 ? " · AdLib " + adlib + "개" : "");
        display.BankCount = samples + adlib;
        display.BankSameName = false;
        display.LyricFile = null;
        display.TotalSeconds = totalSeconds;
        bool modFile = TrackerFiles.IsMod(path);
        display.Mode = (stm ? "STM · Scream Tracker 2 · 4채널" : modFile ? "MOD · ProTracker · " + channels + "채널" : "S3M · Scream Tracker 3 · " + voiceUsed.Count(u => u) + "/" + channels + "채널")
            + " · 패턴 " + m.Patterns.Count + " · 주문 " + m.PlayOrders().Count;
        lyrics.Lyrics = null;
        lyrics.State = null;
        lenLabel.Text = DeckTheme.Clock(totalSeconds);
        seek.Enabled = true;
        RebuildMixerRows();
        MarkPlaying();
        AppLog.Info("Jukebox", "재생: " + Path.GetFileName(path) + " — " + (stm ? "STM" : modFile ? "MOD" : "S3M") + " " + channels + "채널, 샘플 " + samples + (adlib > 0 ? ", AdLib " + adlib : "") + ", " + DeckTheme.Clock(totalSeconds));
        StartAt(0);
    }

    /// <summary>
    /// MIDI 곡 (.mid / .wrk): 악기는 GM 번호. FM이면 GM 뱅크(설정의 MIDI 뱅크 → 뱅크 폴더의 GENMIDI.OP2 → 기본 뱅크 이름 맞춤),
    /// Windows 신디사이저면 장치로 보낸다. 채널 믹서 = MIDI 채널 16개 (10 = 드럼).
    /// </summary>
    private void PlayMidiFile(string path, bool fromList)
    {
        MidiSong m;
        try { m = MidiFiles.Load(path); }
        catch (Exception ex)
        {
            AppLog.Error("Jukebox", "곡을 열 수 없습니다: " + Path.GetFileName(path), ex);
            return;
        }
        StopEngine();
        song = null; sopSong = null; trackerSong = null;
        midiSong = m;
        currentPath = path;
        if (fromList) playlist = CurrentListPaths();
        bank = null;
        iss = null;
        tempoMap = null;
        totalSeconds = m.LengthSeconds;
        pausedTick = 0;
        for (int v = 0; v < PlaybackOptions.Voices; v++) { options.VoiceMute[v] = false; options.VoiceTranspose[v] = 0; levels[v] = 0; voiceUsed[v] = v < MidiSong.Channels && m.ChannelUsed[v]; }
        bool windows = settings().MidiWindowsSynth;
        bool mt32 = settings().MidiMt32;
        string mt32Rom = null;
        if (mt32)
        {
            mt32Rom = ws.Audio.OpenMt32(settings().Mt32RomFolderPath, out string err);
            if (mt32Rom == null) AppLog.Warn("Jukebox", "MT-32 에뮬레이터를 쓸 수 없어 GM으로 냅니다 (MT-32 음색 → GM 번호)", err);
        }
        if (!windows && mt32Rom == null) gmBank = LoadGmBank();
        int channels = m.ChannelUsed.Count(u => u);
        var programs = new HashSet<int>(m.Events.Where(e => e.Kind == 0xC0 && e.Channel != GeneralMidi.DrumChannel).Select(e => (int)e.Data1));

        display.Title = m.Title;
        display.SongFile = Path.GetFileName(path);
        display.Loaded = programs.Count;
        display.Total = programs.Count;
        display.BankFile = mt32Rom != null ? "MT-32 에뮬레이터 · " + mt32Rom
            : (windows ? "Windows MIDI 신디사이저" : "GM FM · " + (gmBank != null ? gmBank.Source : "(뱅크 없음)")) + (mt32 ? " (MT-32 → GM)" : "");
        display.BankCount = programs.Count;
        display.BankSameName = false;
        display.LyricFile = null;
        display.TotalSeconds = totalSeconds;
        display.Mode = m.FormatName + " · " + channels + "채널 · 박당 " + m.Division + "틱" + (mt32Rom != null ? " · MT-32" : windows ? "" : " · OPL3 18음");
        lyrics.Lyrics = null;
        lyrics.State = null;
        lenLabel.Text = DeckTheme.Clock(totalSeconds);
        seek.Enabled = true;
        RebuildMixerRows();
        MarkPlaying();
        AppLog.Info("Jukebox", "재생: " + Path.GetFileName(path) + " — " + m.FormatName + " " + channels + "채널, 프로그램 " + programs.Count + "개, " + DeckTheme.Clock(totalSeconds)
            + ", " + (mt32Rom != null ? "MT-32 에뮬레이터 (" + mt32Rom + ")" : windows ? "Windows 신디사이저" : "FM 뱅크 " + (gmBank != null ? gmBank.Source : "없음")) + (mt32 && mt32Rom == null ? ", MT-32 → GM" : ""),
            m.Comment.Length > 0 ? m.Comment : null);
        StartAt(0);
    }

    /// <summary>GM FM 뱅크: 설정의 MIDI 뱅크(OP2 / BNK) → 뱅크 폴더 · resource 폴더의 GENMIDI.OP2 → 기본 뱅크(이름 맞춤). 같은 것이면 다시 읽지 않는다.</summary>
    private GmBank LoadGmBank()
    {
        var s = settings();
        string path = s.MidiBankPath;
        if (path == null || !File.Exists(path))
        {
            path = null;
            foreach (var dir in new[] { s.BankFolderPath, AppSettings.ResourcePath })
            {
                try
                {
                    if (path == null && Directory.Exists(dir))
                        path = Directory.GetFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), "GENMIDI.OP2", StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception) { }
            }
        }
        string key = path ?? ("BNK:" + s.DefaultBankPath);
        if (gmBank != null && key == gmBankKey) return gmBank;
        GmBank b = null;
        try
        {
            if (path != null && path.EndsWith(".op2", StringComparison.OrdinalIgnoreCase)) b = GmBank.LoadOp2(path);
            else if (path != null) b = GmBank.FromBank(ReadBank(path));
        }
        catch (Exception ex) { AppLog.Warn("Jukebox", "MIDI 뱅크를 읽을 수 없습니다: " + Path.GetFileName(path), ex.Message); }
        if (b == null)
        {
            string def = s.DefaultBankPath;
            b = GmBank.FromBank(def != null && File.Exists(def) ? ReadBank(def) : ws.Bank);
        }
        gmBank = b;
        gmBankKey = key;
        return b;
    }

    private bool HasSong { get { return song != null || sopSong != null || trackerSong != null || midiSong != null; } }
    private int LengthTicks { get { return midiSong != null ? midiSong.LengthTicks : trackerSong != null ? (int)(totalSeconds * 1000) : sopSong != null ? sopSong.LengthTicks : song != null ? song.LengthTicks : 0; } }
    private int TrackCount { get { return midiSong != null ? MidiSong.Channels : trackerSong != null ? Math.Min(PlaybackOptions.Voices, trackerSong.UsedChannelCount) : sopSong != null ? sopSong.TrackCount : song != null ? song.VoiceCount : PlaybackOptions.Voices; } }

    private double SecondsAt(double tick)
    {
        if (trackerSong != null) return tick / 1000.0;
        if (sopSong != null) return sopSong.SecondsAt(tick);
        if (midiSong != null) return midiSong.TickToSeconds(tick);
        return tempoMap != null ? tempoMap.SecondsAt((int)tick) : 0;
    }

    private double TickAt(double seconds)
    {
        if (trackerSong != null) return seconds * 1000.0;
        if (sopSong != null) return sopSong.TickAt(seconds);
        if (midiSong != null) return midiSong.SecondsToTick(seconds);
        return tempoMap != null ? tempoMap.TickAt(seconds) : 0;
    }

    /// <summary>같은 이름의 BNK가 곡 옆에 있으면 그것, 아니면 기본 뱅크, 그것도 없으면 지금 작업 뱅크.</summary>
    private InstrumentBank FindBank(string songPath, out bool sameName)
    {
        sameName = false;
        string side = SideFile(songPath, ".bnk");
        if (side != null)
        {
            var b = ReadBank(side);
            if (b != null) { sameName = true; return b; }
        }
        string def = settings().DefaultBankPath;
        if (def != null && File.Exists(def))
        {
            if (defaultBank == null || !string.Equals(defaultBankPath, def, StringComparison.OrdinalIgnoreCase))
            {
                defaultBank = ReadBank(def);
                defaultBankPath = def;
            }
            if (defaultBank != null) return defaultBank;
        }
        return ws.Bank;
    }

    private InstrumentBank ReadBank(string path)
    {
        try
        {
            var fmt = ws.Formats.FindBank(path);
            if (fmt == null) return null;
            InstrumentBank b;
            using (var fs = File.OpenRead(path)) b = fmt.Read(fs);
            b.FilePath = path;
            return b;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Jukebox", "뱅크를 읽을 수 없습니다: " + Path.GetFileName(path), ex.Message);
            return null;
        }
    }

    private IssLyrics FindLyrics(string songPath)
    {
        string side = SideFile(songPath, ".iss");
        if (side == null) return null;
        try { return IssLyrics.Load(side); }
        catch (Exception ex)
        {
            AppLog.Warn("Jukebox", "가사 파일을 읽을 수 없습니다: " + Path.GetFileName(side), ex.Message);
            return null;
        }
    }

    /// <summary>곡과 같은 폴더의 같은 이름 다른 확장자 파일 (대소문자 무시).</summary>
    private static string SideFile(string songPath, string ext)
    {
        try
        {
            string dir = Path.GetDirectoryName(songPath), stem = Path.GetFileNameWithoutExtension(songPath);
            foreach (var f in Directory.GetFiles(dir))
                if (string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase))
                    return f;
        }
        catch (Exception) { }
        return null;
    }

    // ================================================================ 재생 제어

    private void StartAt(double tick)
    {
        if (!HasSong || !openAudio()) return;
        if (trackerSong != null) ws.Audio.PlayTracker(trackerSong, 0, 0, -1, false, options, Math.Max(0, tick));
        else if (sopSong != null) ws.Audio.PlaySop(sopSong, (int)Math.Max(0, tick), options);
        else if (midiSong != null)
        {
            ws.Audio.MidiBank = gmBank;
            ws.Audio.MidiSynth = settings().MidiWindowsSynth ? MidiSynth.Windows : MidiSynth.Fm;
            ws.Audio.MidiModule = settings().MidiMt32 ? MidiModule.Mt32 : MidiModule.Gm;
            ws.Audio.PlayMidi(midiSong, Math.Max(0, tick), options);
        }
        else ws.Audio.Play(song, bank, (int)Math.Max(0, tick), -1, -1, -1, options);
        state = PlayState.Playing;
        timer.Start();
        SyncPlayButton();
    }

    private void StopEngine()
    {
        if (state == PlayState.Playing) ws.Audio.Stop();
    }

    private void TogglePlay()
    {
        switch (state)
        {
            case PlayState.Playing:
                pausedTick = ws.Audio.PositionTick;
                ws.Audio.Stop();
                state = PlayState.Paused;
                break;
            case PlayState.Paused:
            case PlayState.Stopped:
                StartAt(pausedTick);
                break;
            case PlayState.Empty:
                {
                    string sel = browser.SelectedPath;
                    if (sel != null && CanPlay(sel)) PlayFile(sel, true);
                    else { var all = CurrentListPaths(); if (all.Count > 0) PlayFile(all[0], true); }
                }
                return;
        }
        SyncPlayButton();
        Tick();
    }

    private void StopPlay()
    {
        if (!HasSong) return;
        StopEngine();
        state = PlayState.Stopped;
        pausedTick = 0;
        SyncPlayButton();
        Tick();
    }

    private double CurrentTick { get { return state == PlayState.Playing ? ws.Audio.PositionTick : pausedTick; } }

    private void SeekToTick(double tick)
    {
        if (!HasSong) return;
        tick = Math.Max(0, Math.Min(LengthTicks, tick));
        if (state == PlayState.Playing) StartAt(tick);
        else { pausedTick = tick; if (state == PlayState.Empty) state = PlayState.Stopped; }
        Tick();
    }

    private void SeekToFraction(double f)
    {
        if (!HasSong) return;
        SeekToTick(TickAt(f * totalSeconds));
    }

    /// <summary>곡 시간(원래 속도)으로 ±초. 화면의 시간도 원래 속도 기준.</summary>
    private void SeekSeconds(double delta)
    {
        if (!HasSong) return;
        double now = SecondsAt(CurrentTick);
        SeekToTick(TickAt(Math.Max(0, now + delta)));
    }

    private void PlayNeighbor(int dir)
    {
        var pl = playlist.Count > 0 ? playlist : CurrentListPaths();
        if (pl.Count == 0) return;
        int i = currentPath == null ? -1 : pl.FindIndex(p => string.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase));
        int next = i < 0 ? 0 : i + dir;
        if (next < 0 || next >= pl.Count) return;
        PlayFile(pl[next], false);
    }

    private void SetSpeed(double percent)
    {
        percent = Math.Max(50, Math.Min(200, percent));
        options.Speed = percent / 100.0;
        if (state == PlayState.Playing) ws.Audio.SetSpeed(options.Speed);   // 재생 중이 아니면 다음 재생에 options로 들어간다
        UpdateStepperTexts();
    }

    private void SetKey(int semis)
    {
        options.Transpose = Math.Max(-12, Math.Min(12, semis));
        UpdateStepperTexts();
    }

    private void SetVolume(int percent)
    {
        volumePercent = Math.Max(0, Math.Min(150, percent));
        options.Gain = volumePercent / 100.0;
        if (state == PlayState.Playing) ws.Audio.SetGain(options.Gain);
        UpdateStepperTexts();
    }

    private void SetMute(int v, bool mute)
    {
        options.VoiceMute[v] = mute;
        if (state == PlayState.Playing) ws.Audio.SetVoiceMute(v, mute);
        RefreshMixer();
    }

    /// <summary>이 채널만 켠다. 이미 혼자 켜져 있으면 모두 켠다.</summary>
    private void Solo(int voice)
    {
        int count = TrackCount;
        bool alreadySolo = !options.VoiceMute[voice];
        for (int v = 0; v < count; v++) if (v != voice && !options.VoiceMute[v]) alreadySolo = false;
        for (int v = 0; v < count; v++) SetMute(v, alreadySolo ? false : v != voice);
    }

    private void SetVoicePitch(int v, int semis)
    {
        options.VoiceTranspose[v] = Math.Max(-24, Math.Min(24, semis));
        RefreshMixer();
    }

    private void ResetAdjustments()
    {
        for (int v = 0; v < PlaybackOptions.Voices; v++) { SetMute(v, false); options.VoiceTranspose[v] = 0; }
        SetSpeed(100);
        SetKey(0);
        RefreshMixer();
    }

    private void UpdateStepperTexts()
    {
        speedStep.Invalidate();
        keyStep.Invalidate();
        volStep.Invalidate();
        display.SpeedPercent = (int)Math.Round(options.Speed * 100);
        display.Key = options.Transpose;
        display.Invalidate();
    }

    private void SyncPlayButton()
    {
        bool playing = state == PlayState.Playing;
        playButton.Text = playing ? " 일시정지" : " 재생";
        playButton.Image = GlyphImages.Get(playing ? '' : '', 16, Color.White);
        display.State = state == PlayState.Playing ? "▶ 재생 중" : state == PlayState.Paused ? "❚❚ 일시정지" : state == PlayState.Stopped ? "■ 정지" : "";
        display.Invalidate();
    }

    // ================================================================ 화면 갱신

    private void Tick()
    {
        if (!HasSong) return;
        if (state == PlayState.Playing && !ws.Audio.IsPlaying)
        {
            // 곡이 끝났다
            state = PlayState.Stopped;
            pausedTick = 0;
            SyncPlayButton();
            if (autoNext.Checked && Visible)
            {
                var pl = playlist.Count > 0 ? playlist : CurrentListPaths();
                int i = pl.FindIndex(p => string.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i + 1 < pl.Count) { PlayFile(pl[i + 1], false); return; }
            }
        }
        double tick = CurrentTick;
        double sec = SecondsAt(Math.Min(tick, LengthTicks));
        display.Seconds = sec;
        double basic = midiSong != null ? midiSong.InitialBpm : trackerSong != null ? (trackerSong.Kind == TrackerKind.S3m ? trackerSong.Tempo : 125.0 * 6 / Math.Max(1, trackerSong.Tempo >> 4))
            : sopSong != null ? sopSong.BasicTempo : song.BasicTempo;
        double mult = state == PlayState.Playing ? ws.Audio.TempoMultiplier : midiSong != null ? 1.0 : sopSong != null ? sopSong.TempoAt(tick) / (double)sopSong.BasicTempo : 1.0;
        display.Bpm = basic * mult * options.Speed;
        display.Invalidate();
        if (!seek.IsDragging)
        {
            seek.Value = totalSeconds > 0 ? sec / totalSeconds : 0;
            posLabel.Text = DeckTheme.Clock(sec);
        }
        if (iss != null)
        {
            // SOP의 ISS도 240/박 기준 (SV6-GOSV.ISS 마지막 기록 = 곡 끝과 같음)
            var st = sopSong != null ? iss.StateAt(tick, sopSong.TickBeat, 0) : iss.StateAt(tick, song.TickBeat, song.FileTickBeat);
            if (lyrics.State == null || st.Line != lyrics.State.Line || st.NextLine != lyrics.State.NextLine || !SameLit(st.Lit, lyrics.State.Lit))
            {
                lyrics.State = st;
                lyrics.Invalidate();
            }
        }
        RefreshMixer();
        if (state != PlayState.Playing) timer.Stop();
    }

    private static bool SameLit(bool[] a, bool[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private void RebuildMixerRows()
    {
        mixer.Rows.Clear();
        if (trackerSong != null)
        {
            for (int c = 0; c < TrackCount; c++) mixer.Rows.Add(new ChannelRow { Label = trackerSong.ChannelLabel(c) });
            RefreshMixer();
            return;
        }
        if (sopSong != null)
        {
            for (int t = 0; t < sopSong.TrackCount; t++) mixer.Rows.Add(new ChannelRow { Label = sopSong.TrackLabel(t) });
            RefreshMixer();
            return;
        }
        if (midiSong != null)
        {
            for (int c = 0; c < MidiSong.Channels; c++) mixer.Rows.Add(new ChannelRow { Label = c == GeneralMidi.DrumChannel ? "D10" : (c + 1).ToString() });
            RefreshMixer();
            return;
        }
        bool melodic = song == null || song.IsMelodic;
        var labels = Song.VoiceLabels(melodic);
        for (int v = 0; v < labels.Length; v++) mixer.Rows.Add(new ChannelRow { Label = labels[v] });
        RefreshMixer();
    }

    /// <summary>엔진의 보이스 상태로 채널 줄을 채운다. 음량 막대는 노트온에 볼륨만큼 올라가고 천천히 내려온다.</summary>
    private void RefreshMixer()
    {
        bool playing = state == PlayState.Playing;
        if (playing) ws.Audio.GetVoiceStates(voiceStates);
        for (int v = 0; v < mixer.Rows.Count; v++)
        {
            var row = mixer.Rows[v];
            var vs = voiceStates[v];
            row.Muted = options.VoiceMute[v];
            row.Pitch = options.VoiceTranspose[v];
            row.Used = HasSong && voiceUsed[v];
            if (trackerSong != null)
            {
                row.Instrument = playing && vs.Instrument != null ? vs.Instrument : FirstTrackerSample(v);
                row.Found = true;
            }
            else if (sopSong != null)
            {
                row.Instrument = playing && vs.Instrument != null ? vs.Instrument : FirstSopInstrument(v);
                row.Found = true;   // 악기는 곡 안에 있다
            }
            else if (midiSong != null)
            {
                row.Instrument = playing && vs.Instrument != null ? vs.Instrument : MidiInstrument(v);
                row.Found = true;
            }
            else if (song != null)
            {
                string name = playing && vs.Instrument != null ? vs.Instrument : FirstTimbre(v);
                row.Instrument = name;
                row.Found = bank != null && name != null && bank.Find(name) != null;
            }
            row.Note = playing ? vs.Note : -1;
            float target = row.Note >= 0 ? Math.Max(0.15f, vs.Volume) : 0f;
            if (playing && vs.NoteOnCount != lastCounts[v]) { levels[v] = Math.Max(levels[v], Math.Max(0.15f, vs.Volume)); lastCounts[v] = vs.NoteOnCount; }
            else levels[v] = row.Note >= 0 ? Math.Max(target * 0.75f, levels[v] * 0.97f) : levels[v] * 0.82f;
            if (!playing) levels[v] *= 0.5f;
            row.Level = levels[v];
        }
        mixer.Invalidate();
    }

    private string FirstTrackerSample(int c)
    {
        if (trackerSong == null) return null;
        foreach (var p in trackerSong.Patterns)
            for (int r = 0; r < TrackerPattern.Rows; r++)
            {
                if (c >= p.Channels) return null;
                int i = p[r, c].Instrument;
                if (i > 0 && i <= trackerSong.Samples.Count)
                {
                    var s = trackerSong.Samples[i - 1];
                    return s.Name.Trim().Length > 0 ? s.Name.Trim() : s.FileName;
                }
            }
        return null;
    }

    private string MidiInstrument(int c)
    {
        if (midiSong == null || c >= MidiSong.Channels) return null;
        if (c == GeneralMidi.DrumChannel) return "Drums";
        int p = midiSong.FirstProgram[c];
        return GeneralMidi.ProgramNames[p < 0 ? 0 : p];
    }

    private string FirstSopInstrument(int t)
    {
        if (sopSong == null || t >= sopSong.TrackCount) return null;
        foreach (var e in sopSong.Tracks[t].Events)
            if (e.Type == SopEventType.Instrument && e.Value < sopSong.Instruments.Count && !sopSong.Instruments[e.Value].IsEmpty)
                return sopSong.Instruments[e.Value].Name;
        return null;
    }

    private string FirstTimbre(int v)
    {
        if (song == null) return null;
        var t = song.Voices[v].Timbres;
        return t.Count > 0 ? t[0].InstrumentName : null;
    }

    // ================================================================ 셸 / 키

    public void OnShown()
    {
        ApplySound();
        browser.FocusList();
    }

    /// <summary>다른 페이지로 가면 멈춘다 (오디오 엔진을 Composer와 같이 쓰므로). 위치는 남겨 둔다.</summary>
    public void OnHidden()
    {
        if (state == PlayState.Playing)
        {
            pausedTick = ws.Audio.PositionTick;
            ws.Audio.Stop();
            state = PlayState.Paused;
            SyncPlayButton();
        }
        timer.Stop();
        // 음색 · 스피커는 Jukebox에서만: 다른 페이지(Composer 재생 · 시음)는 칩 소리 그대로
        ws.Audio.ToneStandard = false;
        ws.Audio.VintageSpeaker = false;
    }

    /// <summary>음색 · 스피커 선택을 오디오 엔진에 (재생 중에도 바로 바뀐다).</summary>
    private void ApplySound()
    {
        ws.Audio.ToneStandard = !rawTone.Checked;
        ws.Audio.VintageSpeaker = vintageSpeaker.Checked;
    }

    private void SaveSettings()
    {
        try { settings().Save(); }
        catch (Exception ex) { AppLog.Warn("Jukebox", "설정을 저장하지 못했습니다: " + ex.Message); }
    }

    /// <summary>설정(기본 뱅크)이 바뀌면 다시 읽도록.</summary>
    public void SettingsChanged()
    {
        defaultBank = null;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        return HandleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Jukebox 키 (이 페이지 안 또는 왼쪽 공용 파일 목록에 포커스가 있을 때). 처리했으면 true.</summary>
    public bool HandleKey(Keys keyData)
    {
        bool inTree = browser.TreeFocused;
        switch (keyData)
        {
            case Keys.Space: TogglePlay(); return true;
            case Keys.Escape: StopPlay(); return true;
            case Keys.Left: if (!inTree) { SeekSeconds(-5); return true; } break;
            case Keys.Right: if (!inTree) { SeekSeconds(5); return true; } break;
            case Keys.PageUp: if (!browser.ListFocused) { PlayNeighbor(-1); return true; } break;
            case Keys.PageDown: if (!browser.ListFocused) { PlayNeighbor(1); return true; } break;
            case Keys.Control | Keys.Left: SeekSeconds(-5); return true;
            case Keys.Control | Keys.Right: SeekSeconds(5); return true;
            case Keys.Control | Keys.PageUp: PlayNeighbor(-1); return true;
            case Keys.Control | Keys.PageDown: PlayNeighbor(1); return true;
            case Keys.Add: case Keys.Oemplus: SetVolume(volumePercent + 10); return true;
            case Keys.Subtract: case Keys.OemMinus: SetVolume(volumePercent - 10); return true;
        }
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tips.Dispose(); }
        base.Dispose(disposing);
    }
}
