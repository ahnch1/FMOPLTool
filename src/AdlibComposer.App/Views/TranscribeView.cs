using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AdlibComposer.App.Shell;
using AdlibComposer.Audio;
using AdlibComposer.Core;

namespace AdlibComposer.App.Views;

/// <summary>
/// Transcribe 페이지 (SCORE, Ctrl+8): 음악 파일(MP3 · WAV …)의 주 멜로디를 뽑아 악보로. 화음 · 반주는 보지 않는다. 모두 이 PC에서 계산.
/// <code>
/// ┌ [파일 열기] 파일 이름 · 길이                                  진행 / 상태 ┐
/// ├ 음역 [C3]~[C6]  감도 ───●──  최소 길이 [90]ms  템포 [100.0] [자동]  격자 [8분] ┤
/// ├ 피아노 롤: 멜로디 곡선(점) + 음(막대) + 박 격자, 클릭 = 그 위치부터 재생     ┤
/// └ [▶ 재생] [x] 원곡 [x] 멜로디   음 N개          [악보 보기 (Note)] [Composer에서 편집] ┘
/// </code>
/// 분석(<see cref="MelodyAnalysis.Analyze"/>)은 파일마다 한 번, 조절값을 바꾸면 <see cref="MelodyAnalysis.Track"/>만 다시 한다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class TranscribeView : UserControl, IShellPage, IPlaybackPage
{
    private readonly Workspace ws;
    private readonly Func<bool> openAudio;
    private readonly Func<Song, AppPage, bool> sendSong;

    private readonly Button btnOpen = new Button(), btnNote = new Button(), btnComposer = new Button(), btnAutoTempo = new Button();
    private readonly Label lblFile = new Label(), lblStatus = new Label(), lblInfo = new Label();
    private readonly ComboBox cbLow = new ComboBox(), cbHigh = new ComboBox(), cbGrid = new ComboBox();
    private readonly TrackBar tbVoicing = new TrackBar();
    private readonly NumericUpDown nudMinMs = new NumericUpDown(), nudBpm = new NumericUpDown();
    private readonly CheckBox chkOriginal = new CheckBox(), chkMelody = new CheckBox();
    private readonly TranscribeCanvas canvas = new TranscribeCanvas();
    private readonly HScrollBar hscroll = new HScrollBar();
    private readonly ToolTip tips = new ToolTip();
    private readonly System.Windows.Forms.Timer playTimer = new System.Windows.Forms.Timer { Interval = 33 };
    private readonly System.Windows.Forms.Timer retrackTimer = new System.Windows.Forms.Timer { Interval = 250 };

    private string path;
    private float[] samples;
    private MelodyAnalysis analysis;
    private TranscriptionResult result;
    private CancellationTokenSource cancel;
    private bool busy, playing, filling;
    private int trackGeneration;
    private string lastDir;

    public TranscribeView(Workspace ws, Func<bool> openAudio, Func<Song, AppPage, bool> sendSong)
    {
        this.ws = ws;
        this.openAudio = openAudio;
        this.sendSong = sendSong;
        BackColor = Color.White;
        Font = ShellTheme.Ui;
        DoubleBuffered = true;

        var top = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Color.White };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.White };
        BuildTop(top);
        BuildBottom(bottom);
        canvas.Dock = DockStyle.Fill;
        canvas.SeekRequested += s => { if (playing) StartPlay(s); else canvas.Cursor0 = s; canvas.Invalidate(); };
        canvas.ViewChanged += SyncScroll;
        hscroll.Dock = DockStyle.Bottom;
        hscroll.Scroll += delegate { canvas.ViewStart = hscroll.Value / 100.0; canvas.Invalidate(); };
        Controls.Add(canvas);
        Controls.Add(hscroll);
        Controls.Add(bottom);
        Controls.Add(top);
        playTimer.Tick += delegate { TickPlay(); };
        retrackTimer.Tick += delegate { retrackTimer.Stop(); Retrack(); };
        UpdateButtons();
    }

    public void OnShown() { }

    public void OnHidden()
    {
        if (playing) StopPlay();
    }

    // ------------------------------------------------------------ 구성

    private void BuildTop(Panel host)
    {
        btnOpen.Text = "음악 파일 열기...";
        btnOpen.SetBounds(12, 9, 130, 28);
        btnOpen.Font = ShellTheme.UiSemibold;
        btnOpen.Click += delegate { OpenFile(); };
        tips.SetToolTip(btnOpen, "MP3 · WAV · WMA · M4A (Windows 코덱으로 읽음). 분석은 이 PC에서만 한다");
        lblFile.AutoSize = false;
        lblFile.SetBounds(152, 14, 520, 20);
        lblFile.Text = "파일을 열면 주 멜로디를 찾아 악보로 만든다 (화음 · 반주는 보지 않음)";
        lblFile.ForeColor = ShellTheme.Muted;
        lblStatus.AutoSize = false;
        lblStatus.TextAlign = ContentAlignment.MiddleRight;
        lblStatus.ForeColor = ShellTheme.Muted;
        lblStatus.SetBounds(680, 14, 300, 20);
        lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        host.Controls.AddRange(new Control[] { btnOpen, lblFile, lblStatus });

        int x = 12, y = 48;
        Control Label(string t) { var l = new Label { Text = t, AutoSize = true, Location = new Point(x, y + 4) }; host.Controls.Add(l); x += TextRenderer.MeasureText(t, Font).Width + 4; return l; }
        Label("음역");
        foreach (var cb in new[] { cbLow, cbHigh })
        {
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int m = MelodyAnalysis.LowMidi; m <= MelodyAnalysis.LowMidi + 60; m++) cb.Items.Add(NoteName(m));
            cb.Width = 58;
            cb.SelectedIndexChanged += delegate { ParamsChanged(); };
        }
        cbLow.SelectedIndex = 48 - MelodyAnalysis.LowMidi;
        cbHigh.SelectedIndex = 84 - MelodyAnalysis.LowMidi;
        cbLow.Location = new Point(x, y); host.Controls.Add(cbLow); x += 62;
        Label("~");
        cbHigh.Location = new Point(x, y); host.Controls.Add(cbHigh); x += 74;
        tips.SetToolTip(cbLow, "멜로디가 있는 음역. 베이스 · 반주가 멜로디로 잡히면 아래쪽을 올린다");
        tips.SetToolTip(cbHigh, "멜로디가 있는 음역의 위쪽");

        Label("감도");
        tbVoicing.Minimum = 0; tbVoicing.Maximum = 100; tbVoicing.Value = 50; tbVoicing.TickFrequency = 10;
        tbVoicing.AutoSize = false; tbVoicing.SetBounds(x, y - 2, 120, 28);
        tbVoicing.ValueChanged += delegate { ParamsChanged(); };
        tips.SetToolTip(tbVoicing, "높이면 약한 소리 · 잡음도 음으로 잡고, 낮추면 뚜렷한 멜로디만 잡는다");
        host.Controls.Add(tbVoicing); x += 128;

        Label("최소 길이");
        nudMinMs.Minimum = 20; nudMinMs.Maximum = 1000; nudMinMs.Increment = 10; nudMinMs.Value = 90;
        nudMinMs.SetBounds(x, y, 58, 24);
        nudMinMs.ValueChanged += delegate { ParamsChanged(); };
        host.Controls.Add(nudMinMs); x += 62;
        Label("ms");
        x += 10;

        Label("템포");
        nudBpm.Minimum = 30; nudBpm.Maximum = 300; nudBpm.DecimalPlaces = 1; nudBpm.Increment = 1; nudBpm.Value = 120;
        nudBpm.SetBounds(x, y, 64, 24);
        nudBpm.ValueChanged += delegate { if (!filling) { canvas.Bpm = (double)nudBpm.Value; canvas.Invalidate(); } };
        host.Controls.Add(nudBpm); x += 68;
        btnAutoTempo.Text = "자동";
        btnAutoTempo.SetBounds(x, y - 1, 48, 26);
        btnAutoTempo.Click += delegate { ApplyAutoTempo(); };
        tips.SetToolTip(btnAutoTempo, "곡의 어택으로 찾은 템포 · 첫 박으로 되돌린다 (반이나 두 배로 잡힐 수 있다)");
        tips.SetToolTip(nudBpm, "악보의 템포 (4분음표 = 1박). 음 길이를 박에 맞출 때 쓴다");
        host.Controls.Add(btnAutoTempo); x += 58;

        Label("격자");
        cbGrid.DropDownStyle = ComboBoxStyle.DropDownList;
        cbGrid.Items.AddRange(new object[] { "4분음표", "8분음표", "16분음표", "없음" });
        cbGrid.SelectedIndex = 2;
        cbGrid.SetBounds(x, y, 86, 24);
        cbGrid.SelectedIndexChanged += delegate { canvas.Grid = GridDivision; canvas.Invalidate(); };
        tips.SetToolTip(cbGrid, "악보로 보낼 때 음의 시작 · 길이를 이 단위에 맞춘다");
        host.Controls.Add(cbGrid);
    }

    private void BuildBottom(Panel host)
    {
        // 재생 · 현재 위치부터 재생 · 정지 = 툴바의 공통 버튼 (Space = 재생 / 정지, 화면을 클릭하면 그 위치가 현재 위치)
        chkOriginal.Text = "원곡"; chkOriginal.Checked = true; chkOriginal.SetBounds(12, 13, 56, 22);
        chkMelody.Text = "채보한 멜로디"; chkMelody.Checked = true; chkMelody.SetBounds(70, 13, 110, 22);
        foreach (var c in new[] { chkOriginal, chkMelody }) c.CheckedChanged += delegate { if (playing) ws.Audio.SetPcmMix(chkOriginal.Checked, chkMelody.Checked); };
        tips.SetToolTip(chkMelody, "채보한 음을 간단한 소리로 겹쳐 들려준다 (원곡과 견주기)");
        lblInfo.AutoSize = false; lblInfo.SetBounds(190, 15, 520, 20); lblInfo.ForeColor = ShellTheme.Muted;
        btnComposer.Text = "Composer에서 편집";
        btnComposer.SetBounds(0, 9, 140, 28);
        btnComposer.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnComposer.Click += delegate { Send(AppPage.Composer); };
        btnNote.Text = "악보 보기 (Note)";
        btnNote.Font = ShellTheme.UiSemibold;
        btnNote.SetBounds(0, 9, 140, 28);
        btnNote.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnNote.Click += delegate { Send(AppPage.Note); };
        tips.SetToolTip(btnNote, "채보한 멜로디를 새 곡(보이스 1)으로 만들어 오선 악보로 본다. MusicXML · MIDI · PNG로 내보내거나 ROL로 저장할 수 있다");
        host.Controls.AddRange(new Control[] { chkOriginal, chkMelody, lblInfo, btnComposer, btnNote });
        host.Resize += delegate
        {
            btnNote.Left = host.Width - btnNote.Width - 12;
            btnComposer.Left = btnNote.Left - btnComposer.Width - 6;
            lblInfo.Width = Math.Max(50, btnComposer.Left - lblInfo.Left - 10);
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var p = new Pen(ShellTheme.HeaderLine))
        {
            e.Graphics.DrawLine(p, 0, 83, Width, 83);
            e.Graphics.DrawLine(p, 0, Height - 46, Width, Height - 46);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Space && !(ActiveControl is TextBoxBase) && !(ActiveControl is NumericUpDown) && samples != null)
        {
            if (playing) StopPlay(); else StartPlay(canvas.Cursor0);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private int GridDivision { get { return new[] { 4, 8, 16, 0 }[Math.Max(0, cbGrid.SelectedIndex)]; } }

    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    internal static string NoteName(int midi) { return Names[midi % 12] + (midi / 12 - 1); }

    private void UpdateButtons()
    {
        bool has = analysis != null && !busy;
        btnOpen.Enabled = !busy;
        btnNote.Enabled = btnComposer.Enabled = has && result != null && result.Notes.Count > 0;
        btnAutoTempo.Enabled = has;
    }

    // ------------------------------------------------------------ 열기 / 분석

    private void OpenFile()
    {
        using (var dlg = new OpenFileDialog
        {
            Title = "채보할 음악 파일",
            Filter = "음악 파일 (*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac)|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac|모든 파일 (*.*)|*.*",
            InitialDirectory = lastDir != null && Directory.Exists(lastDir) ? lastDir : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        })
        {
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            lastDir = Path.GetDirectoryName(dlg.FileName);
            LoadFile(dlg.FileName);
        }
    }

    /// <summary>공용 파일 목록에서 고른 음악 파일을 분석한다.</summary>
    public void Open(string file)
    {
        lastDir = Path.GetDirectoryName(file);
        LoadFile(file);
    }

    private async void LoadFile(string file)
    {
        if (busy) return;
        if (playing) StopPlay();
        busy = true;
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        UpdateButtons();
        lblFile.Text = Path.GetFileName(file);
        lblFile.ForeColor = ForeColor;
        lblStatus.Text = "읽는 중...";
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int srcRate = 0;
            var pcm = await Task.Run(() => AudioFileReader.ReadMono(file, MelodyAnalysis.Rate, out srcRate), token);
            if (pcm.Length < MelodyAnalysis.Rate / 2) throw new InvalidDataException("소리가 너무 짧습니다.");
            Normalize(pcm);
            lblFile.Text = Path.GetFileName(file) + "  ·  " + Clock(pcm.Length / (double)MelodyAnalysis.Rate) + "  ·  " + srcRate + " Hz";
            var progress = new Progress<double>(f => lblStatus.Text = "분석 중 " + (int)(f * 100) + "%");
            var a = await Task.Run(() => MelodyAnalysis.Analyze(pcm, progress, token), token);
            path = file;
            samples = pcm;
            analysis = a;
            canvas.Duration = a.Duration;
            canvas.Cursor0 = 0;
            canvas.ViewStart = 0;
            ApplyAutoTempo(false);
            lblStatus.Text = "분석 " + sw.Elapsed.TotalSeconds.ToString("0.0") + "초";
            AppLog.Info("채보", "분석: " + Path.GetFileName(file) + " — " + Clock(a.Duration) + ", 템포 " + a.Bpm.ToString("0.#") + " BPM, " + sw.Elapsed.TotalSeconds.ToString("0.0") + "초");
            busy = false;
            Retrack();
        }
        catch (OperationCanceledException) { lblStatus.Text = "취소함"; }
        catch (Exception ex)
        {
            lblStatus.Text = "읽기 실패";
            AppLog.Error("채보", "음악 파일을 읽을 수 없습니다: " + Path.GetFileName(file), ex);
            MessageBox.Show(FindForm(), "음악 파일을 읽을 수 없습니다.\n\n" + ex.Message, "Transcribe", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            busy = false;
            UpdateButtons();
        }
    }

    /// <summary>가장 큰 소리가 0.9가 되게 (조용한 파일도 같은 기준으로 분석 · 재생).</summary>
    private static void Normalize(float[] x)
    {
        float m = 0;
        foreach (var v in x) m = Math.Max(m, Math.Abs(v));
        if (m < 1e-6f) return;
        float g = 0.9f / m;
        for (int i = 0; i < x.Length; i++) x[i] *= g;
    }

    private void ApplyAutoTempo(bool redraw = true)
    {
        if (analysis == null) return;
        filling = true;
        nudBpm.Value = (decimal)Math.Max(30, Math.Min(300, analysis.Bpm));
        filling = false;
        canvas.Bpm = (double)nudBpm.Value;
        canvas.BeatOffset = analysis.BeatOffset;
        canvas.Grid = GridDivision;
        if (redraw) canvas.Invalidate();
    }

    private TranscribeOptions Options()
    {
        int lo = MelodyAnalysis.LowMidi + Math.Max(0, cbLow.SelectedIndex), hi = MelodyAnalysis.LowMidi + Math.Max(0, cbHigh.SelectedIndex);
        if (hi <= lo + 5) hi = lo + 6;
        return new TranscribeOptions { MinMidi = lo, MaxMidi = hi, Voicing = 100 - tbVoicing.Value, MinNoteMs = (double)nudMinMs.Value };
    }

    private void ParamsChanged()
    {
        if (analysis == null) return;
        retrackTimer.Stop();
        retrackTimer.Start();
    }

    private async void Retrack()
    {
        var a = analysis;
        if (a == null) return;
        var opt = Options();
        int gen = ++trackGeneration;
        canvas.Range = (opt.MinMidi, opt.MaxMidi);
        var r = await Task.Run(() => a.Track(opt));
        if (gen != trackGeneration || a != analysis) return;
        result = r;
        canvas.Result = r;
        canvas.Invalidate();
        SyncScroll();
        int n = r.Notes.Count;
        string range = n > 0 ? NoteName(r.Notes.Min(x => x.Midi)) + " ~ " + NoteName(r.Notes.Max(x => x.Midi)) : "";
        double off = n > 0 ? r.Notes.Average(x => Math.Abs(x.Pitch - x.Midi)) * 100 : 0;
        lblInfo.Text = n == 0 ? "찾은 음이 없습니다 (감도를 높이거나 음역을 넓혀 보세요)"
            : "음 " + n + "개 · " + range + " · 음정 평균 ±" + off.ToString("0") + "센트";
        UpdateButtons();
    }

    // ------------------------------------------------------------ 재생

    private void StartPlay(double seconds)
    {
        if (samples == null || !openAudio()) return;
        seconds = Math.Max(0, Math.Min(canvas.Duration, seconds));
        ws.Audio.PlayPcm(samples, MelodyAnalysis.Rate, result != null ? result.Notes : null, seconds, chkOriginal.Checked, chkMelody.Checked);
        playing = true;
        playTimer.Start();
        UpdateButtons();
    }

    // IPlaybackPage: 툴바의 공통 재생 버튼
    public bool CanPlay { get { return samples != null && !busy; } }
    public bool IsPlaying { get { return playing; } }
    public void PlayFromStart() { canvas.Cursor0 = 0; StartPlay(0); }
    public void PlayFromCursor() { StartPlay(canvas.Cursor0); }

    /// <summary>멈추고, 현재 위치를 멈춘 곳으로.</summary>
    public void StopPlay()
    {
        if (!playing) return;
        canvas.Cursor0 = ws.Audio.PositionTick / 1000.0;
        canvas.PlayPosition = -1;
        ws.Audio.Stop();
        playing = false;
        playTimer.Stop();
        canvas.Invalidate();
        UpdateButtons();
    }

    private void TickPlay()
    {
        if (!playing) return;
        if (!ws.Audio.IsPlaying) { playing = false; playTimer.Stop(); canvas.Cursor0 = 0; canvas.PlayPosition = -1; UpdateButtons(); canvas.Invalidate(); return; }
        double t = ws.Audio.PositionTick / 1000.0;
        canvas.PlayPosition = t;
        canvas.FollowPlay();
        SyncScroll();
        canvas.Invalidate();
    }

    private void SyncScroll()
    {
        double span = canvas.VisibleSeconds;
        double max = Math.Max(0, canvas.Duration - span);
        hscroll.Minimum = 0;
        hscroll.LargeChange = Math.Max(1, (int)(span * 100));
        hscroll.SmallChange = Math.Max(1, (int)(span * 10));
        hscroll.Maximum = (int)(max * 100) + hscroll.LargeChange;
        hscroll.Value = Math.Max(0, Math.Min((int)(max * 100), (int)(canvas.ViewStart * 100)));
    }

    // ------------------------------------------------------------ 악보로

    private void Send(AppPage page)
    {
        if (result == null || result.Notes.Count == 0) return;
        if (playing) StopPlay();
        var song = Workspace.CreateEmptySong();
        MelodyToSong.Build(song, result.Notes, (double)nudBpm.Value, canvas.BeatOffset, GridDivision);
        song.Title = Path.GetFileNameWithoutExtension(path);
        if (!sendSong(song, page)) return;
        AppLog.Info("채보", "악보로: " + song.Title + " — 음 " + song.Voices[0].Notes.Count + "개, " + song.BasicTempo + " BPM, 격자 " + (GridDivision > 0 ? "1/" + GridDivision : "없음"));
    }

    private static string Clock(double s) { return TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"m\:ss"); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tips.Dispose(); playTimer.Dispose(); retrackTimer.Dispose(); cancel?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 채보 화면의 피아노 롤: 왼쪽 건반 축, 박 격자(마디 굵게), 멜로디 곡선(프레임별 점), 음 막대(뚜렷할수록 진하게), 재생 위치.
/// 휠 = 좌우 이동, Ctrl+휠 = 확대 · 축소, 클릭 = 그 위치로.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class TranscribeCanvas : Control
{
    private const int KeyWidth = 44;
    public TranscriptionResult Result;
    public double Duration = 0;
    public double ViewStart;
    public double PixelsPerSecond = 120;
    public double Bpm = 120, BeatOffset;
    public int Grid = 16;
    public (int lo, int hi) Range = (48, 84);
    public double Cursor0, PlayPosition = -1;
    public event Action<double> SeekRequested;
    public event Action ViewChanged;

    public TranscribeCanvas()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.White;
    }

    public double VisibleSeconds { get { return Math.Max(0.1, (Width - KeyWidth) / PixelsPerSecond); } }

    public void FollowPlay()
    {
        if (PlayPosition < ViewStart || PlayPosition > ViewStart + VisibleSeconds * 0.9)
            ViewStart = Math.Max(0, Math.Min(Math.Max(0, Duration - VisibleSeconds), PlayPosition - VisibleSeconds * 0.1));
    }

    private float X(double sec) { return (float)(KeyWidth + (sec - ViewStart) * PixelsPerSecond); }

    private float Y(double midi)
    {
        int lo = Range.lo - 2, hi = Range.hi + 2;
        return (float)((hi + 0.5 - midi) * (Height - 1) / (hi - lo + 1));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.White);
        int lo = Range.lo - 2, hi = Range.hi + 2;
        float rowH = (float)(Height - 1) / (hi - lo + 1);
        // 줄 (검은 건반 줄은 옅게)
        using (var black = new SolidBrush(Color.FromArgb(246, 247, 249)))
        using (var cLine = new Pen(Color.FromArgb(220, 224, 230)))
        using (var font = new Font(ShellTheme.Ui.FontFamily, Math.Max(6.5f, Math.Min(8.5f, rowH * 0.7f))))
        {
            for (int m = lo; m <= hi; m++)
            {
                float y = Y(m) - rowH / 2;
                bool isBlack = new[] { 1, 3, 6, 8, 10 }.Contains(m % 12);
                if (isBlack) g.FillRectangle(black, KeyWidth, y, Width - KeyWidth, rowH);
                if (m % 12 == 0) g.DrawLine(cLine, KeyWidth, y + rowH, Width, y + rowH);
                g.FillRectangle(isBlack ? Brushes.DimGray : Brushes.White, 0, y, KeyWidth - 14, rowH);
                if (m % 12 == 0 || rowH >= 11)
                    TextRenderer.DrawText(g, TranscribeView.NoteName(m), font, new Rectangle(KeyWidth - 40, (int)y, 38, (int)Math.Ceiling(rowH)),
                        m < Range.lo || m > Range.hi ? Color.Silver : ShellTheme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            g.DrawLine(Pens.Silver, KeyWidth, 0, KeyWidth, Height);
        }
        if (Duration <= 0) return;
        g.SetClip(new Rectangle(KeyWidth + 1, 0, Width - KeyWidth - 1, Height));
        // 박 격자
        double beat = 60.0 / Math.Max(20, Bpm);
        double sub = Grid > 0 ? beat * 4 / Grid : beat;
        using (var subPen = new Pen(Color.FromArgb(240, 242, 245)))
        using (var beatPen = new Pen(Color.FromArgb(214, 219, 226)))
        using (var barPen = new Pen(Color.FromArgb(160, 170, 185)))
        {
            double first = BeatOffset - Math.Ceiling((BeatOffset - ViewStart) / sub) * sub;
            for (double t = first; t < ViewStart + VisibleSeconds + sub; t += sub)
            {
                double beats = (t - BeatOffset) / beat;
                bool isBeat = Math.Abs(beats - Math.Round(beats)) < 1e-6;
                bool isBar = isBeat && ((long)Math.Round(beats) % 4 + 4) % 4 == 0;
                float x = X(t);
                g.DrawLine(isBar ? barPen : isBeat ? beatPen : subPen, x, 0, x, Height);
            }
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Result != null)
        {
            // 음 막대
            foreach (var n in Result.Notes)
            {
                if (n.End < ViewStart || n.Start > ViewStart + VisibleSeconds) continue;
                int alpha = 120 + (int)(135 * n.Strength);
                using (var b = new SolidBrush(Color.FromArgb(alpha, ShellTheme.Accent)))
                {
                    float y = Y(n.Midi) - rowH / 2 + 1;
                    var r = new RectangleF(X(n.Start), y, Math.Max(2, (float)(n.Duration * PixelsPerSecond)), Math.Max(3, rowH - 2));
                    g.FillRectangle(b, r);
                    g.DrawRectangle(Pens.White, r.X, r.Y, r.Width, r.Height);
                }
            }
            // 멜로디 곡선
            int f0 = Math.Max(0, (int)(ViewStart / Result.HopSeconds)), f1 = Math.Min(Result.Pitch.Length, (int)((ViewStart + VisibleSeconds) / Result.HopSeconds) + 1);
            using (var dot = new SolidBrush(Color.FromArgb(200, 230, 120, 30)))
                for (int f = f0; f < f1; f++)
                {
                    float p = Result.Pitch[f];
                    if (float.IsNaN(p)) continue;
                    g.FillRectangle(dot, X(f * Result.HopSeconds) - 1, Y(p) - 1, 2.2f, 2.2f);
                }
        }
        // 위치
        using (var cur = new Pen(Color.FromArgb(200, 40, 40), 1.5f))
        {
            double pos = PlayPosition >= 0 ? PlayPosition : Cursor0;
            float x = X(pos);
            g.DrawLine(cur, x, 0, x, Height);
        }
        g.ResetClip();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left || e.X < KeyWidth || Duration <= 0) return;
        double t = ViewStart + (e.X - KeyWidth) / PixelsPerSecond;
        Cursor0 = Math.Max(0, Math.Min(Duration, t));
        PlayPosition = -1;
        SeekRequested?.Invoke(Cursor0);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Duration <= 0) return;
        if ((ModifierKeys & Keys.Control) != 0)
        {
            double at = ViewStart + Math.Max(0, e.X - KeyWidth) / PixelsPerSecond;
            PixelsPerSecond = Math.Max(10, Math.Min(1200, PixelsPerSecond * (e.Delta > 0 ? 1.25 : 0.8)));
            ViewStart = Math.Max(0, at - Math.Max(0, e.X - KeyWidth) / PixelsPerSecond);
        }
        else ViewStart = Math.Max(0, Math.Min(Math.Max(0, Duration - VisibleSeconds), ViewStart - e.Delta / 120.0 * VisibleSeconds * 0.15));
        ViewChanged?.Invoke();
        Invalidate();
    }
}
