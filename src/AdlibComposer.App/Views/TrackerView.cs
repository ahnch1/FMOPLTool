using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AdlibComposer.App.Shell;
using AdlibComposer.App.Tracker;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Views;

/// <summary>
/// Tracker 페이지 (CREATE): Scream Tracker 3를 본뜬 STM / S3M 편집기 (MOD · NST도 S3M 모델로 열고 저장).
/// F1 = 주문 목록 · 곡 변수 · 채널, F2 = 패턴 편집, F3 = 악기 목록 (샘플 WAV 불러오기/저장, AdLib 파라미터).
/// F5 = 곡 재생, F6 = 패턴 재생, F7 = 커서부터, F8 = 정지 (ST3 키). 새 곡 S3M/STM, 열기, 저장(확장자에 맞게 바꿔 저장).
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class TrackerView : UserControl, IShellPage
{
    private readonly Workspace ws;
    private readonly Func<AppSettings> settings;
    private readonly Func<bool> openAudio;
    private readonly ToolStrip bar = new ToolStrip();
    private readonly ToolStripButton tabOrders, tabPattern, tabInstruments, followButton;
    private readonly Label header = new Label();
    private readonly Panel host = new Panel();
    private readonly PatternGrid grid = new PatternGrid();
    private readonly Panel ordersPanel = new Panel(), instrumentsPanel = new Panel();
    private readonly Timer timer = new Timer { Interval = 30 };
    private readonly ToolTip tips = new ToolTip();

    private TrackerModule module = TrackerModule.CreateNew(TrackerKind.S3m);
    private int order;          // 편집 중인 주문 위치
    private int patternIndex;   // 편집 중인 패턴
    private bool dirty, playing, binding;
    private readonly List<KeyValuePair<int, TrackerPattern>> undo = new List<KeyValuePair<int, TrackerPattern>>();

    // F1
    private readonly ListBox orderList = new ListBox();
    private readonly NumericUpDown orderPattern = new NumericUpDown();
    private readonly TextBox titleBox = new TextBox();
    private readonly NumericUpDown speedBox = new NumericUpDown(), tempoBox = new NumericUpDown(), gvolBox = new NumericUpDown(), mvolBox = new NumericUpDown();
    private readonly CheckBox stereoBox = new CheckBox(), fastSlideBox = new CheckBox(), amigaBox = new CheckBox();
    private readonly DataGridView channelGrid = new DataGridView();
    private readonly Label kindLabel = new Label();
    // F3
    private readonly ListView insList = new ListView();
    private readonly TextBox insName = new TextBox(), insFile = new TextBox();
    private readonly ComboBox insType = new ComboBox();
    private readonly NumericUpDown insVol = new NumericUpDown(), insC2 = new NumericUpDown(), insLoopStart = new NumericUpDown(), insLoopEnd = new NumericUpDown();
    private readonly CheckBox insLoop = new CheckBox();
    private readonly Label insInfo = new Label();
    private readonly WaveView wave = new WaveView();
    private readonly NumericUpDown[] adlibBoxes = new NumericUpDown[12];
    private readonly Panel adlibPanel = new Panel();
    private int jamChannel = -1;
    private int insBaseOctave = 4;

    public TrackerView(Workspace ws, Func<AppSettings> settings, Func<bool> openAudio)
    {
        this.ws = ws;
        this.settings = settings;
        this.openAudio = openAudio;
        BackColor = Color.FromArgb(236, 238, 241);

        bar.GripStyle = ToolStripGripStyle.Hidden;
        bar.RenderMode = ToolStripRenderMode.System;
        bar.BackColor = Color.White;
        bar.Padding = new Padding(10, 4, 10, 4);
        bar.Font = ShellTheme.Ui;
        bar.Dock = DockStyle.Top;
        var newMenu = new ToolStripDropDownButton("새 곡", GlyphImages.Get(Glyphs.NewFile, 16, Color.FromArgb(50, 54, 62)));
        newMenu.DropDownItems.Add("S3M (Scream Tracker 3, 32채널 · AdLib)", null, delegate { NewModule(TrackerKind.S3m); });
        newMenu.DropDownItems.Add("STM (Scream Tracker 2, 4채널)", null, delegate { NewModule(TrackerKind.Stm); });
        tabOrders = Tab("F1 주문·변수", delegate { ShowTab(0); });
        tabPattern = Tab("F2 패턴", delegate { ShowTab(1); });
        tabInstruments = Tab("F3 악기", delegate { ShowTab(2); });
        followButton = new ToolStripButton("따라가기") { CheckOnClick = true, Checked = true, ToolTipText = "재생 중 패턴 화면이 재생 위치를 따라간다 (ST3 Scroll Lock)" };
        bar.Items.AddRange(new ToolStripItem[]
        {
            newMenu,
            Button("열기", Glyphs.Open, "S3M / STM 열기 (Ctrl+O)", delegate { OpenFile(); }),
            Button("저장", Glyphs.Save, "저장 (Ctrl+S)", delegate { Save(false); }),
            Button("다른 이름", Glyphs.Save, "다른 이름 / 다른 형식(S3M ↔ STM)으로 저장", delegate { Save(true); }),
            new ToolStripSeparator(),
            tabOrders, tabPattern, tabInstruments,
            new ToolStripSeparator(),
            Button("곡", Glyphs.Play, "곡 재생 (F5)", delegate { PlaySong(0, 0); }),
            Button("패턴", Glyphs.Play, "지금 패턴만 되풀이 (F6)", delegate { PlayPattern(); }),
            Button("커서부터", Glyphs.Play, "커서 줄부터 곡 재생 (F7)", delegate { PlaySong(order, grid.CursorRow); }),
            Button("정지", Glyphs.Stop, "정지 (F8)", delegate { StopPlay(); }),
            followButton,
        });

        header.Dock = DockStyle.Top;
        header.Height = 28;
        header.Font = new Font("Consolas", 10f);
        header.BackColor = PatternGrid.HeadBack;
        header.ForeColor = Color.FromArgb(220, 220, 240);
        header.Padding = new Padding(10, 0, 0, 0);
        header.TextAlign = ContentAlignment.MiddleLeft;

        host.Dock = DockStyle.Fill;
        grid.Dock = DockStyle.Fill;
        ordersPanel.Dock = DockStyle.Fill;
        instrumentsPanel.Dock = DockStyle.Fill;
        ordersPanel.BackColor = instrumentsPanel.BackColor = Color.White;
        host.Controls.Add(grid);
        host.Controls.Add(ordersPanel);
        host.Controls.Add(instrumentsPanel);
        Controls.Add(host);
        Controls.Add(header);
        Controls.Add(bar);

        BuildOrders();
        BuildInstruments();

        grid.BeforeEdit += PushUndo;
        grid.PatternEdited += delegate { MarkDirty(); };
        grid.CursorMoved += UpdateHeader;
        grid.InstrumentChanged += i => { UpdateHeader(); SelectInstrumentRow(i - 1); };
        grid.OctaveChanged += o => UpdateHeader();
        grid.PlayNote += (ins, note, ch) => Jam(ins, note, ch);
        grid.PlayRowRequested += r => PlayRow(r);
        grid.PatternStep += d => SetPattern(patternIndex + d);
        timer.Tick += delegate { OnTimer(); };

        Bind();
        ShowTab(1);
    }

    private static ToolStripButton Button(string text, char glyph, string tip, EventHandler click)
    {
        return new ToolStripButton(text, GlyphImages.Get(glyph, 16, Color.FromArgb(50, 54, 62)), click)
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, ToolTipText = tip, Padding = new Padding(3, 1, 3, 1)
        };
    }

    private static ToolStripButton Tab(string text, EventHandler click)
    {
        return new ToolStripButton(text, null, click) { CheckOnClick = false, Padding = new Padding(4, 1, 4, 1) };
    }

    // ================================================================ 셸

    public void OnShown() { UpdateHeader(); if (tabPattern.Checked) grid.Focus(); }
    public void OnHidden() { StopPlay(); }

    public bool IsDirty { get { return dirty; } }

    /// <summary>저장 안 한 변경이 있으면 묻는다. 계속해도 되면 true.</summary>
    public bool ConfirmDiscard()
    {
        if (!dirty) return true;
        var r = MessageBox.Show(FindForm(), "트래커 곡 '" + DisplayName + "'이(가) 바뀌었습니다. 저장할까요?", "Tracker", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) return Save(false);
        return true;
    }

    private string DisplayName { get { return module.FilePath != null ? Path.GetFileName(module.FilePath) : (module.Title.Length > 0 ? module.Title : "untitled") + (module.Kind == TrackerKind.Stm ? ".stm" : ".s3m"); } }

    private void ShowTab(int t)
    {
        tabOrders.Checked = t == 0; tabPattern.Checked = t == 1; tabInstruments.Checked = t == 2;
        ordersPanel.Visible = t == 0; grid.Visible = t == 1; instrumentsPanel.Visible = t == 2;
        if (t == 0) BindOrders();
        if (t == 2) BindInstruments();
        if (t == 1) { BindPattern(); grid.Focus(); }
        else if (t == 0) orderList.Focus(); else insList.Focus();
    }

    // ================================================================ 곡 / 파일

    private void NewModule(TrackerKind kind)
    {
        if (!ConfirmDiscard()) return;
        StopPlay();
        module = TrackerModule.CreateNew(kind);
        module.Samples.Add(new TrackerSample { Type = TrackerSample.TypeEmpty, C2Spd = kind == TrackerKind.Stm ? 8448 : 8363 });
        dirty = false;
        undo.Clear();
        Bind();
        AppLog.Info("트래커", "새 " + (kind == TrackerKind.Stm ? "STM (4채널)" : "S3M (8채널)") + " 곡");
    }

    private void OpenFile()
    {
        if (!ConfirmDiscard()) return;
        using (var dlg = new OpenFileDialog { Filter = TrackerFiles.OpenFilter, Title = "트래커 곡 열기 (S3M · STM · MOD · NST)" })
        {
            string dir = settings().BrowserFolderPath;
            if (dir != null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            Open(dlg.FileName);
        }
    }

    public void Open(string path)
    {
        try
        {
            StopPlay();
            module = TrackerFiles.Load(path);
            dirty = false;
            undo.Clear();
            Bind();
            AppLog.Info("트래커", "열기: " + Path.GetFileName(path) + " — " + module.Kind + ", 채널 " + module.UsedChannelCount + ", 패턴 " + module.Patterns.Count + ", 악기 " + module.Samples.Count);
        }
        catch (Exception ex)
        {
            AppLog.Error("트래커", "곡을 열 수 없습니다: " + Path.GetFileName(path), ex);
            MessageBox.Show(FindForm(), "곡을 열 수 없습니다.\n\n" + ex.Message, "Tracker", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>저장 대화상자 기본 형식: 지금 파일의 확장자 (MOD 3, NST 4), 없으면 곡 종류.</summary>
    private int SaveFilterIndex()
    {
        string ext = module.FilePath != null ? Path.GetExtension(module.FilePath).ToLowerInvariant() : "";
        if (ext == ".mod") return 3;
        if (ext == ".nst") return 4;
        return module.Kind == TrackerKind.Stm ? 2 : 1;
    }

    private bool Save(bool saveAs)
    {
        CommitOrderEdits();
        string path = module.FilePath;
        if (saveAs || path == null)
        {
            using (var dlg = new SaveFileDialog
            {
                Filter = TrackerFiles.SaveFilter,
                FilterIndex = SaveFilterIndex(), AddExtension = true, Title = "Scream Tracker 곡 저장",
                FileName = Path.GetFileNameWithoutExtension(DisplayName),
            })
            {
                string dir = path != null ? Path.GetDirectoryName(path) : settings().BrowserFolderPath;
                if (dir != null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return false;
                path = dlg.FileName;
            }
        }
        try
        {
            var warnings = new List<string>();
            var saved = TrackerFiles.Save(module, path, warnings);
            bool converted = saved.Kind != module.Kind || TrackerFiles.IsMod(path);
            module = saved;
            dirty = false;
            if (converted) Bind(); else UpdateHeader();
            AppLog.Info("트래커", "저장: " + path, warnings.Count > 0 ? string.Join("\n", warnings) : null);
            if (warnings.Count > 0) MessageBox.Show(FindForm(), "저장했습니다. 형식을 바꾸면서 빠진 것:\n\n• " + string.Join("\n• ", warnings), "Tracker", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("트래커", "저장할 수 없습니다: " + path, ex);
            MessageBox.Show(FindForm(), "저장할 수 없습니다.\n\n" + ex.Message, "Tracker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void MarkDirty() { if (!dirty) { dirty = true; } UpdateHeader(); }

    private void Bind()
    {
        order = 0;
        var po = module.PlayOrders();
        patternIndex = po.Count > 0 && po[0] < module.Patterns.Count ? po[0] : 0;
        if (module.Patterns.Count == 0) module.Patterns.Add(module.NewPattern());
        grid.CursorRow = 0; grid.CursorChannel = 0; grid.CursorField = 0;
        grid.BaseOctave = module.Kind == TrackerKind.Stm ? 2 : 4;
        grid.CurrentInstrument = 1;
        BindPattern();
        BindOrders();
        BindInstruments();
        UpdateHeader();
    }

    private void BindPattern()
    {
        patternIndex = Math.Max(0, Math.Min(module.Patterns.Count - 1, patternIndex));
        grid.Bind(module, module.Patterns[patternIndex]);
        UpdateHeader();
    }

    private void SetPattern(int p)
    {
        if (p >= module.Patterns.Count)
        {
            int max = module.Kind == TrackerKind.Stm ? 63 : 200;
            if (p >= max) p = max - 1;
            while (module.Patterns.Count <= p) module.Patterns.Add(module.NewPattern());
            MarkDirty();
        }
        patternIndex = Math.Max(0, p);
        BindPattern();
    }

    private void UpdateHeader()
    {
        var po = module.PlayOrders();
        int curRow = grid.CursorRow, curIns = grid.CurrentInstrument;
        var ins = grid.CurrentInstrument >= 1 && grid.CurrentInstrument <= module.Samples.Count ? module.Samples[grid.CurrentInstrument - 1] : null;
        string insName = ins == null ? "" : ins.Name.Length > 0 ? ins.Name : ins.FileName;
        header.Text = (module.Kind == TrackerKind.Stm ? "STM" : "S3M") + "  " + DisplayName + (dirty ? " *" : "")
            + "   Order " + order.ToString("000") + "/" + po.Count.ToString("000")
            + "   Pattern " + patternIndex.ToString("00") + "/" + module.Patterns.Count.ToString("00")
            + "   Row " + curRow.ToString("00")
            + "   Octave " + grid.BaseOctave
            + "   Ins " + curIns.ToString("00") + " " + insName
            + "   " + (module.Kind == TrackerKind.Stm ? "Tempo " + module.Tempo.ToString("X2") : "Speed " + module.Speed + " Tempo " + module.Tempo);
    }

    private void PushUndo()
    {
        undo.Add(new KeyValuePair<int, TrackerPattern>(patternIndex, module.Patterns[patternIndex].Clone()));
        if (undo.Count > 100) undo.RemoveAt(0);
    }

    private void Undo()
    {
        if (undo.Count == 0) return;
        var u = undo[undo.Count - 1];
        undo.RemoveAt(undo.Count - 1);
        if (u.Key < module.Patterns.Count) module.Patterns[u.Key] = u.Value;
        patternIndex = u.Key;
        BindPattern();
        MarkDirty();
    }

    // ================================================================ 재생

    private void PlaySong(int fromOrder, int fromRow)
    {
        CommitOrderEdits();
        if (!openAudio()) return;
        ws.Audio.PlayTracker(module, fromOrder, fromRow, -1, true);
        StartTimer();
    }

    private void PlayPattern()
    {
        if (!openAudio()) return;
        ws.Audio.PlayTracker(module, 0, 0, patternIndex, true);
        StartTimer();
    }

    private void PlayRow(int row)
    {
        if (!openAudio()) return;
        var pat = module.Patterns[patternIndex];
        for (int c = 0; c < module.UsedChannelCount; c++)
        {
            var cell = pat[row, c];
            if (cell.HasNote) ws.Audio.TrackerJam(module, cell.Instrument != 0 ? cell.Instrument : grid.CurrentInstrument, cell.Note, c);
        }
    }

    private void StartTimer() { playing = true; timer.Start(); }

    private void StopPlay()
    {
        if (playing || jamChannel >= 0) ws.Audio.Stop();
        playing = false;
        jamChannel = -1;
        timer.Stop();
        grid.PlayRow = -1;
        grid.Invalidate();
    }

    private void Jam(int ins, int note, int channel)
    {
        if (playing || !openAudio()) return;
        ws.Audio.TrackerJam(module, ins, note, channel);
        jamChannel = channel;
    }

    private void OnTimer()
    {
        int o, p, r;
        bool on = ws.Audio.GetTrackerPosition(out o, out p, out r);
        if (!on) { StopPlay(); return; }
        if (followButton.Checked && grid.Visible)
        {
            if (p != patternIndex && p < module.Patterns.Count) { patternIndex = p; order = o; BindPattern(); }
            grid.PlayRow = r;
            grid.EnsureVisible(r, true);
        }
        else grid.PlayRow = p == patternIndex ? r : -1;
        grid.Invalidate();
    }

    // ================================================================ 키 (ST3)

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F1: ShowTab(0); return true;
            case Keys.F2: ShowTab(1); return true;
            case Keys.F3: ShowTab(2); return true;
            case Keys.F5: case Keys.Shift | Keys.F5: PlaySong(0, 0); return true;
            case Keys.F6: PlayPattern(); return true;
            case Keys.Shift | Keys.F6: PlaySong(order, 0); return true;
            case Keys.F7: PlaySong(order, grid.CursorRow); return true;
            case Keys.F8: StopPlay(); return true;
            case Keys.Control | Keys.S: Save(false); return true;
            case Keys.Control | Keys.O: OpenFile(); return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.OemOpenBrackets | Keys.Shift: SetGlobalSpeed(-1); return true;   // {
            case Keys.OemCloseBrackets | Keys.Shift: SetGlobalSpeed(1); return true;   // }
        }
        if (grid.Visible && grid.Focused && grid.HandleKey(keyData)) return true;
        if (instrumentsPanel.Visible && !(ActiveControlIsText())) { if (InstrumentKey(keyData)) return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool ActiveControlIsText()
    {
        Control c = FindForm() != null ? FindForm().ActiveControl : null;
        while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
        return c is TextBox || c is NumericUpDown || c is ComboBox || c is UpDownBase;
    }

    private void SetGlobalSpeed(int d)
    {
        if (module.Kind == TrackerKind.Stm) module.Tempo = Math.Max(0x10, Math.Min(0xFF, module.Tempo + d * 0x10));
        else module.Speed = Math.Max(1, Math.Min(255, module.Speed + d));
        MarkDirty();
        BindOrders();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (jamChannel >= 0 && !playing) { ws.Audio.TrackerJamOff(jamChannel); }
    }

    // ================================================================ F1: 주문 · 변수 · 채널

    private void BuildOrders()
    {
        var left = new Panel { Dock = DockStyle.Left, Width = 220, Padding = new Padding(12) };
        var title = new Label { Text = "ORDER LIST", Dock = DockStyle.Top, Height = 22, Font = ShellTheme.Section, ForeColor = ShellTheme.Muted };
        orderList.Dock = DockStyle.Fill;
        orderList.Font = new Font("Consolas", 10f);
        orderList.IntegralHeight = false;
        orderList.SelectedIndexChanged += delegate
        {
            if (binding || orderList.SelectedIndex < 0) return;
            binding = true;
            byte v = module.Orders[orderList.SelectedIndex];
            orderPattern.Value = v >= TrackerModule.OrderSkip ? 0 : Math.Min(orderPattern.Maximum, v);
            binding = false;
        };
        orderList.DoubleClick += delegate { GoToOrder(orderList.SelectedIndex); };
        orderList.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.G || e.KeyCode == Keys.Enter) { GoToOrder(orderList.SelectedIndex); e.Handled = true; }
            if (e.KeyCode == Keys.Insert) { InsertOrder(); e.Handled = true; }
            if (e.KeyCode == Keys.Delete) { DeleteOrder(); e.Handled = true; }
        };
        tips.SetToolTip(orderList, "주문 목록: 더블클릭 / G / Enter = 그 패턴 편집, Ins / Del = 넣기 / 빼기, --- = 끝, +++ = 건너뜀");
        var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 66, FlowDirection = FlowDirection.LeftToRight };
        orderPattern.Width = 60; orderPattern.Minimum = 0; orderPattern.Maximum = 199;
        orderPattern.ValueChanged += delegate { if (!binding) SetOrderValue((byte)orderPattern.Value); };
        row.Controls.Add(new Label { Text = "패턴", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        row.Controls.Add(orderPattern);
        row.Controls.Add(SmallButton("넣기", delegate { InsertOrder(); }));
        row.Controls.Add(SmallButton("빼기", delegate { DeleteOrder(); }));
        row.Controls.Add(SmallButton("+++", delegate { SetOrderValue(TrackerModule.OrderSkip); }));
        row.Controls.Add(SmallButton("---", delegate { SetOrderValue(TrackerModule.OrderEnd); }));
        left.Controls.Add(orderList);
        left.Controls.Add(row);
        left.Controls.Add(title);

        var vars = new TableLayoutPanel { Dock = DockStyle.Left, Width = 330, Padding = new Padding(12), ColumnCount = 2, AutoScroll = true };
        vars.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        vars.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        vars.Controls.Add(new Label { Text = "GLOBAL VARIABLES", Font = ShellTheme.Section, ForeColor = ShellTheme.Muted, AutoSize = true }, 0, 0);
        vars.SetColumnSpan(vars.GetControlFromPosition(0, 0), 2);
        vars.RowCount = 1;   // 제목 줄 다음부터 AddVar
        kindLabel.AutoSize = true;
        kindLabel.ForeColor = ShellTheme.Muted;
        AddVar(vars, "형식", kindLabel);
        titleBox.Width = 180;
        titleBox.TextChanged += delegate { if (!binding) { module.Title = titleBox.Text; MarkDirty(); } };
        AddVar(vars, "곡 이름", titleBox);
        Nud(speedBox, 1, 255); speedBox.ValueChanged += delegate { if (!binding) { if (module.Kind == TrackerKind.Stm) module.Tempo = ((int)speedBox.Value << 4) | (module.Tempo & 15); else module.Speed = (int)speedBox.Value; MarkDirty(); } };
        AddVar(vars, "속도 (A, 줄당 틱)", speedBox);
        Nud(tempoBox, 0, 255); tempoBox.ValueChanged += delegate { if (!binding) { if (module.Kind == TrackerKind.Stm) module.Tempo = (module.Tempo & 0xF0) | ((int)tempoBox.Value & 15); else module.Tempo = Math.Max(32, (int)tempoBox.Value); MarkDirty(); } };
        AddVar(vars, "템포 (T, BPM)", tempoBox);
        Nud(gvolBox, 0, 64); gvolBox.ValueChanged += delegate { if (!binding) { module.GlobalVolume = (int)gvolBox.Value; MarkDirty(); } };
        AddVar(vars, "전역 볼륨 (V)", gvolBox);
        Nud(mvolBox, 16, 127); mvolBox.ValueChanged += delegate { if (!binding) { module.MasterVolume = (int)mvolBox.Value; MarkDirty(); } };
        AddVar(vars, "마스터 볼륨", mvolBox);
        stereoBox.Text = "스테레오"; stereoBox.AutoSize = true;
        stereoBox.CheckedChanged += delegate { if (!binding) { module.Stereo = stereoBox.Checked; MarkDirty(); } };
        AddVar(vars, "", stereoBox);
        fastSlideBox.Text = "빠른 볼륨 슬라이드 (ST3.00)"; fastSlideBox.AutoSize = true;
        fastSlideBox.CheckedChanged += delegate { if (!binding) { module.Flags = fastSlideBox.Checked ? module.Flags | 64 : module.Flags & ~64; MarkDirty(); } };
        AddVar(vars, "", fastSlideBox);
        amigaBox.Text = "아미가 한계"; amigaBox.AutoSize = true;
        amigaBox.CheckedChanged += delegate { if (!binding) { module.Flags = amigaBox.Checked ? module.Flags | 16 : module.Flags & ~16; MarkDirty(); } };
        AddVar(vars, "", amigaBox);

        var chPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        channelGrid.Dock = DockStyle.Fill;
        channelGrid.AllowUserToAddRows = false;
        channelGrid.AllowUserToDeleteRows = false;
        channelGrid.RowHeadersVisible = false;
        channelGrid.BackgroundColor = Color.White;
        channelGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        channelGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "채널", ReadOnly = true, FillWeight = 40 });
        var typeCol = new DataGridViewComboBoxColumn { HeaderText = "종류 (L = 왼쪽, R = 오른쪽, A = AdLib)", FillWeight = 120 };
        typeCol.Items.Add("--");
        for (int i = 1; i <= 8; i++) typeCol.Items.Add("L" + i);
        for (int i = 1; i <= 8; i++) typeCol.Items.Add("R" + i);
        for (int i = 1; i <= 9; i++) typeCol.Items.Add("A" + i);
        channelGrid.Columns.Add(typeCol);
        channelGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "팬 (0 왼쪽 ~ 15 오른쪽)", FillWeight = 80 });
        channelGrid.CellValueChanged += (s, e) => { if (!binding && e.RowIndex >= 0) CommitChannel(e.RowIndex); };
        channelGrid.CurrentCellDirtyStateChanged += delegate { if (channelGrid.IsCurrentCellDirty) channelGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        channelGrid.DataError += (s, e) => { e.ThrowException = false; };
        chPanel.Controls.Add(channelGrid);
        chPanel.Controls.Add(new Label { Text = "CHANNELS", Dock = DockStyle.Top, Height = 22, Font = ShellTheme.Section, ForeColor = ShellTheme.Muted });

        ordersPanel.Controls.Add(chPanel);
        ordersPanel.Controls.Add(vars);
        ordersPanel.Controls.Add(left);
    }

    private static void Nud(NumericUpDown n, int min, int max) { n.Minimum = min; n.Maximum = max; n.Width = 80; }

    private static void AddVar(TableLayoutPanel t, string label, Control c)
    {
        int r = t.RowCount;
        t.RowCount = r + 1;
        t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 6, 0) }, 0, r);
        t.Controls.Add(c, 1, r);
    }

    private Button SmallButton(string text, EventHandler click)
    {
        var b = new Button { Text = text, Width = 46, Height = 26, FlatStyle = FlatStyle.Flat, Margin = new Padding(2) };
        b.Click += click;
        return b;
    }

    private void BindOrders()
    {
        binding = true;
        try
        {
            int sel = orderList.SelectedIndex;
            orderList.BeginUpdate();
            orderList.Items.Clear();
            for (int i = 0; i < module.Orders.Count; i++)
            {
                byte o = module.Orders[i];
                orderList.Items.Add(i.ToString("000") + ":  " + (o == TrackerModule.OrderEnd ? "---" : o == TrackerModule.OrderSkip ? "+++" : o.ToString("000")));
            }
            orderList.EndUpdate();
            if (orderList.Items.Count > 0) orderList.SelectedIndex = Math.Max(0, Math.Min(orderList.Items.Count - 1, sel < 0 ? order : sel));
            orderPattern.Maximum = module.Kind == TrackerKind.Stm ? 62 : 199;
            bool stm = module.Kind == TrackerKind.Stm;
            kindLabel.Text = stm ? "STM — Scream Tracker 2 (4채널, 명령 A~J)" : "S3M — Scream Tracker 3 (32채널, AdLib)";
            titleBox.MaxLength = stm ? 19 : 27;
            titleBox.Text = module.Title;
            speedBox.Value = Math.Max(speedBox.Minimum, Math.Min(speedBox.Maximum, stm ? Math.Max(1, module.Tempo >> 4) : module.Speed));
            tempoBox.Value = Math.Max(tempoBox.Minimum, Math.Min(tempoBox.Maximum, stm ? module.Tempo & 15 : module.Tempo));
            tempoBox.Maximum = stm ? 15 : 255;
            gvolBox.Value = Math.Min(64, module.GlobalVolume);
            mvolBox.Value = Math.Max(16, Math.Min(127, module.MasterVolume));
            mvolBox.Enabled = stereoBox.Enabled = fastSlideBox.Enabled = amigaBox.Enabled = !stm;
            stereoBox.Checked = module.Stereo;
            fastSlideBox.Checked = (module.Flags & 64) != 0;
            amigaBox.Checked = (module.Flags & 16) != 0;
            channelGrid.Rows.Clear();
            int n = stm ? 4 : TrackerModule.MaxChannels;
            for (int c = 0; c < n; c++)
            {
                int i = channelGrid.Rows.Add();
                channelGrid.Rows[i].Cells[0].Value = (c + 1).ToString("00");
                string lab = stm ? "L" + (c + 1) : module.ChannelLabel(c);
                channelGrid.Rows[i].Cells[1].Value = lab.Length == 2 && char.IsDigit(lab[1]) && "LRA".IndexOf(lab[0]) >= 0 ? lab : "--";
                channelGrid.Rows[i].Cells[2].Value = module.Pan[c].ToString();
            }
            channelGrid.Enabled = !stm;
        }
        finally { binding = false; }
    }

    private void CommitChannel(int c)
    {
        if (module.Kind == TrackerKind.Stm) return;
        string t = Convert.ToString(channelGrid.Rows[c].Cells[1].Value) ?? "--";
        byte v = 255;
        if (t.Length == 2 && char.IsDigit(t[1]))
        {
            int k = t[1] - '1';
            if (t[0] == 'L') v = (byte)k; else if (t[0] == 'R') v = (byte)(8 + k); else if (t[0] == 'A') v = (byte)(16 + k);
        }
        module.ChannelSettings[c] = v;
        int pan;
        if (int.TryParse(Convert.ToString(channelGrid.Rows[c].Cells[2].Value), out pan)) module.Pan[c] = (byte)Math.Max(0, Math.Min(15, pan));
        MarkDirty();
        grid.Bind(module, module.Patterns[patternIndex]);
    }

    private void CommitOrderEdits() { }

    private void SetOrderValue(byte v)
    {
        int i = orderList.SelectedIndex;
        if (i < 0) return;
        if (v < TrackerModule.OrderSkip && v >= module.Patterns.Count) SetPattern(v);
        module.Orders[i] = v;
        MarkDirty();
        BindOrders();
    }

    private void InsertOrder()
    {
        int i = Math.Max(0, orderList.SelectedIndex);
        module.Orders.Insert(i, (byte)Math.Min(patternIndex, 199));
        MarkDirty();
        BindOrders();
        orderList.SelectedIndex = i;
    }

    private void DeleteOrder()
    {
        int i = orderList.SelectedIndex;
        if (i < 0 || module.Orders.Count <= 1) return;
        module.Orders.RemoveAt(i);
        MarkDirty();
        BindOrders();
    }

    private void GoToOrder(int i)
    {
        if (i < 0 || i >= module.Orders.Count) return;
        byte v = module.Orders[i];
        if (v >= TrackerModule.OrderSkip) return;
        int playIndex = 0;
        for (int k = 0; k < i; k++) if (module.Orders[k] < TrackerModule.OrderSkip) playIndex++;
        order = playIndex;
        SetPattern(v);
        ShowTab(1);
    }

    // ================================================================ F3: 악기

    private void BuildInstruments()
    {
        insList.Dock = DockStyle.Left;
        insList.Width = 520;
        insList.View = View.Details;
        insList.FullRowSelect = true;
        insList.HideSelection = false;
        insList.MultiSelect = false;
        insList.Font = new Font("Consolas", 9.5f);
        insList.Columns.Add("##", 34);
        insList.Columns.Add("이름", 210);
        insList.Columns.Add("종류", 52);
        insList.Columns.Add("볼륨", 46, HorizontalAlignment.Right);
        insList.Columns.Add("C4Spd", 60, HorizontalAlignment.Right);
        insList.Columns.Add("길이", 70, HorizontalAlignment.Right);
        insList.Columns.Add("루프", 40, HorizontalAlignment.Center);
        insList.SelectedIndexChanged += delegate { if (!binding && insList.SelectedIndices.Count > 0) { grid.SetInstrument(insList.SelectedIndices[0] + 1); BindInstrumentDetails(); } };
        tips.SetToolTip(insList, "악기 목록: 고르고 건반 키(Z S X … / Q 2 W …)로 들어 보기, / * = 옥타브");

        var detail = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), AutoScroll = true };
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        insName.Width = 230; insName.MaxLength = 27;
        insName.TextChanged += delegate { EditIns(s => s.Name = insName.Text); };
        insFile.Width = 120; insFile.MaxLength = 12;
        insFile.TextChanged += delegate { EditIns(s => s.FileName = insFile.Text); };
        insType.DropDownStyle = ComboBoxStyle.DropDownList;
        insType.Items.AddRange(new object[] { "(비어 있음)", "샘플 (SMP)", "AdLib 멜로디 (AMel)" });
        insType.SelectedIndexChanged += delegate { EditIns(s => { s.Type = insType.SelectedIndex == 2 ? TrackerSample.TypeAdlibMelody : insType.SelectedIndex == 1 ? TrackerSample.TypeSample : TrackerSample.TypeEmpty; if (s.IsAdlib && AllZero(s.Adlib)) DefaultAdlib(s); }); BindInstrumentDetails(); };
        Nud(insVol, 0, 64); insVol.ValueChanged += delegate { EditIns(s => s.Volume = (int)insVol.Value); };
        Nud(insC2, 1000, 65535); insC2.Width = 90; insC2.ValueChanged += delegate { EditIns(s => s.C2Spd = (int)insC2.Value); };
        insLoop.Text = "루프"; insLoop.AutoSize = true;
        insLoop.CheckedChanged += delegate { EditIns(s => s.Loop = insLoop.Checked); };
        Nud(insLoopStart, 0, 1 << 20); insLoopStart.Width = 90; insLoopStart.ValueChanged += delegate { EditIns(s => s.LoopStart = (int)insLoopStart.Value); wave.Invalidate(); };
        Nud(insLoopEnd, 0, 1 << 20); insLoopEnd.Width = 90; insLoopEnd.ValueChanged += delegate { EditIns(s => s.LoopEnd = (int)insLoopEnd.Value); wave.Invalidate(); };
        insInfo.AutoSize = true; insInfo.ForeColor = ShellTheme.Muted;
        AddVar(t, "종류", insType);
        AddVar(t, "이름", insName);
        AddVar(t, "파일 이름", insFile);
        AddVar(t, "볼륨 (0~64)", insVol);
        AddVar(t, "C4Spd (Hz)", insC2);
        AddVar(t, "", insLoop);
        AddVar(t, "루프 시작", insLoopStart);
        AddVar(t, "루프 끝", insLoopEnd);
        AddVar(t, "", insInfo);
        var btns = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 6, 0, 0) };
        btns.Controls.Add(WideButton("악기 추가", delegate { AddInstrument(); }));
        btns.Controls.Add(WideButton("WAV 불러오기", delegate { ImportWav(); }));
        btns.Controls.Add(WideButton("WAV 저장", delegate { ExportWav(); }));
        btns.Controls.Add(WideButton("지우기", delegate { ClearInstrument(); }));
        wave.Dock = DockStyle.Top;
        wave.Height = 120;
        BuildAdlibPanel();
        detail.Controls.Add(adlibPanel);
        detail.Controls.Add(wave);
        detail.Controls.Add(btns);
        detail.Controls.Add(t);
        instrumentsPanel.Controls.Add(detail);
        instrumentsPanel.Controls.Add(insList);
    }

    private static readonly string[] AdlibLabels =
    {
        "D00 모듈 20 (AM VIB EG KSR MULT)", "D01 캐리어 20", "D02 모듈 40 (KSL TL)", "D03 캐리어 40",
        "D04 모듈 60 (AR DR)", "D05 캐리어 60", "D06 모듈 80 (SL RR)", "D07 캐리어 80",
        "D08 모듈 웨이브", "D09 캐리어 웨이브", "D0A 피드백·연결 (C0)", "D0B (미사용)",
    };

    private void BuildAdlibPanel()
    {
        adlibPanel.Dock = DockStyle.Top;
        adlibPanel.AutoSize = true;
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(0, 8, 0, 0) };
        t.Controls.Add(new Label { Text = "ADLIB PARAMETERS (16진, OPL2 레지스터 값)", Font = ShellTheme.Section, ForeColor = ShellTheme.Muted, AutoSize = true }, 0, 0);
        t.SetColumnSpan(t.GetControlFromPosition(0, 0), 4);
        for (int i = 0; i < 12; i++)
        {
            int k = i;
            var n = new NumericUpDown { Minimum = 0, Maximum = 255, Hexadecimal = true, Width = 60 };
            n.ValueChanged += delegate { EditIns(s => s.Adlib[k] = (byte)n.Value); };
            adlibBoxes[i] = n;
            int r = 1 + i / 2, c = (i % 2) * 2;
            t.Controls.Add(new Label { Text = AdlibLabels[i], AutoSize = true, Margin = new Padding(0, 7, 6, 0) }, c, r);
            t.Controls.Add(n, c + 1, r);
        }
        adlibPanel.Controls.Add(t);
    }

    private Button WideButton(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 28, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 6, 0) };
        b.Click += click;
        return b;
    }

    private static bool AllZero(byte[] b) { foreach (var x in b) if (x != 0) return false; return true; }

    /// <summary>새 AdLib 악기 기본값: 부드러운 사인 오르간.</summary>
    private static void DefaultAdlib(TrackerSample s)
    {
        byte[] d = { 0x21, 0x21, 0x10, 0x00, 0xF2, 0xF2, 0x43, 0x43, 0x00, 0x00, 0x06, 0x00 };
        Array.Copy(d, s.Adlib, 12);
        if (s.Name.Length == 0) s.Name = "AdLib";
    }

    private TrackerSample CurrentSample
    {
        get
        {
            int i = insList.SelectedIndices.Count > 0 ? insList.SelectedIndices[0] : grid.CurrentInstrument - 1;
            return i >= 0 && i < module.Samples.Count ? module.Samples[i] : null;
        }
    }

    private void EditIns(Action<TrackerSample> a)
    {
        if (binding) return;
        var s = CurrentSample;
        if (s == null) return;
        a(s);
        MarkDirty();
        RefreshInsRow(module.Samples.IndexOf(s));
    }

    private void BindInstruments()
    {
        binding = true;
        try
        {
            insList.BeginUpdate();
            insList.Items.Clear();
            for (int i = 0; i < module.Samples.Count; i++) insList.Items.Add(new ListViewItem(new string[7]));
            for (int i = 0; i < module.Samples.Count; i++) RefreshInsRow(i);
            insList.EndUpdate();
            SelectInstrumentRow(grid.CurrentInstrument - 1);
        }
        finally { binding = false; }
        BindInstrumentDetails();
    }

    private void RefreshInsRow(int i)
    {
        if (i < 0 || i >= insList.Items.Count) return;
        var s = module.Samples[i];
        var it = insList.Items[i];
        it.SubItems[0].Text = (i + 1).ToString("00");
        it.SubItems[1].Text = s.Name.Length > 0 ? s.Name : s.FileName;
        it.SubItems[2].Text = s.IsAdlib ? "AMel" : s.IsSample ? "SMP" : "---";
        it.SubItems[3].Text = s.IsEmpty ? "" : s.Volume.ToString();
        it.SubItems[4].Text = s.IsEmpty ? "" : s.C2Spd.ToString();
        it.SubItems[5].Text = s.IsSample ? s.Length.ToString() : "";
        it.SubItems[6].Text = s.IsSample && s.Loop ? "●" : "";
        it.ForeColor = s.IsEmpty ? Color.Gray : Color.Black;
    }

    private void SelectInstrumentRow(int i)
    {
        if (i < 0 || i >= insList.Items.Count) return;
        bool was = binding;
        binding = true;
        insList.Items[i].Selected = true;
        insList.Items[i].Focused = true;
        insList.EnsureVisible(i);
        binding = was;
        if (!was) BindInstrumentDetails();
    }

    private void BindInstrumentDetails()
    {
        var s = CurrentSample;
        bool was = binding;
        binding = true;
        try
        {
            bool on = s != null;
            foreach (Control c in new Control[] { insName, insFile, insType, insVol, insC2, insLoop, insLoopStart, insLoopEnd }) c.Enabled = on;
            if (s == null) { wave.Sample = null; adlibPanel.Visible = false; return; }
            insName.Text = s.Name; insFile.Text = s.FileName;
            insType.SelectedIndex = s.IsAdlib ? 2 : s.Type == TrackerSample.TypeSample ? 1 : 0;
            insVol.Value = Math.Min(64, s.Volume);
            insC2.Value = Math.Max(insC2.Minimum, Math.Min(insC2.Maximum, s.C2Spd));
            insLoop.Checked = s.Loop;
            insLoopStart.Maximum = insLoopEnd.Maximum = Math.Max(1, s.Length);
            insLoopStart.Value = Math.Min(insLoopStart.Maximum, s.LoopStart);
            insLoopEnd.Value = Math.Min(insLoopEnd.Maximum, s.LoopEnd);
            insLoop.Enabled = insLoopStart.Enabled = insLoopEnd.Enabled = s.IsSample;
            insInfo.Text = s.IsSample ? "길이 " + s.Length + " · " + (s.Is16Bit ? "16bit" : "8bit") + " · " + (s.Length / (double)Math.Max(1, s.C2Spd)).ToString("0.00") + "초 (C-" + (module.Kind == TrackerKind.Stm ? 2 : 4) + ")"
                : s.IsAdlib ? "AdLib: S3M의 A1~A9 채널에서 소리 남 (F1 채널)" : "빈 칸";
            wave.Sample = s.IsSample ? s : null;
            adlibPanel.Visible = s.IsAdlib && module.Kind == TrackerKind.S3m;
            if (s.IsAdlib) for (int i = 0; i < 12; i++) adlibBoxes[i].Value = s.Adlib[i];
            insType.Enabled = module.Kind == TrackerKind.S3m;
        }
        finally { binding = was; }
        wave.Invalidate();
    }

    private void AddInstrument()
    {
        int max = module.Kind == TrackerKind.Stm ? 31 : 99;
        if (module.Samples.Count >= max) { MessageBox.Show(FindForm(), "악기는 " + max + "개까지입니다.", "Tracker"); return; }
        module.Samples.Add(new TrackerSample { Type = TrackerSample.TypeEmpty, C2Spd = module.Kind == TrackerKind.Stm ? 8448 : 8363 });
        MarkDirty();
        grid.SetInstrument(module.Samples.Count);
        BindInstruments();
    }

    private void ClearInstrument()
    {
        var s = CurrentSample;
        if (s == null) return;
        int i = module.Samples.IndexOf(s);
        module.Samples[i] = new TrackerSample { Type = TrackerSample.TypeEmpty, C2Spd = s.C2Spd };
        MarkDirty();
        BindInstruments();
    }

    private void ImportWav()
    {
        var s = CurrentSample;
        if (s == null) { AddInstrument(); s = CurrentSample; if (s == null) return; }
        using (var dlg = new OpenFileDialog { Filter = "WAV (*.wav)|*.wav|All files (*.*)|*.*", Title = "샘플 불러오기" })
        {
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            try
            {
                int rate; bool is16;
                var data = WavIo.Read(dlg.FileName, out rate, out is16);
                int max = module.Kind == TrackerKind.Stm ? 65535 : 1 << 20;
                if (data.Length > max) Array.Resize(ref data, max);
                s.Type = TrackerSample.TypeSample;
                s.Data = data;
                s.Is16Bit = is16 && module.Kind == TrackerKind.S3m;
                s.C2Spd = Math.Max(1000, Math.Min(65535, rate));
                s.Loop = false; s.LoopStart = 0; s.LoopEnd = data.Length;
                s.FileName = Path.GetFileName(dlg.FileName).ToUpperInvariant();
                if (s.FileName.Length > 12) s.FileName = s.FileName.Substring(0, 12);
                if (s.Name.Length == 0) s.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
                if (s.Volume == 0) s.Volume = 64;
                MarkDirty();
                BindInstruments();
                AppLog.Info("트래커", "샘플 불러오기: " + Path.GetFileName(dlg.FileName) + " (" + data.Length + " 샘플, " + rate + "Hz)");
            }
            catch (Exception ex) { AppLog.Error("트래커", "WAV를 읽을 수 없습니다", ex); MessageBox.Show(FindForm(), "WAV를 읽을 수 없습니다.\n\n" + ex.Message, "Tracker"); }
        }
    }

    private void ExportWav()
    {
        var s = CurrentSample;
        if (s == null || !s.IsSample) return;
        using (var dlg = new SaveFileDialog { Filter = "WAV (*.wav)|*.wav", DefaultExt = "wav", AddExtension = true, FileName = (s.Name.Length > 0 ? s.Name : "sample").Replace('/', '_') })
        {
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            try { WavIo.Write(dlg.FileName, s.Data, s.C2Spd); AppLog.Info("트래커", "샘플 저장: " + dlg.FileName); }
            catch (Exception ex) { AppLog.Error("트래커", "WAV를 저장할 수 없습니다", ex); }
        }
    }

    /// <summary>F3에서 건반 키로 고른 악기를 들어 본다 (/ * = 옥타브).</summary>
    private bool InstrumentKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        if ((keyData & (Keys.Control | Keys.Alt | Keys.Shift)) != 0) return false;
        if (key == Keys.Divide || key == Keys.OemQuestion) { insBaseOctave = Math.Max(0, insBaseOctave - 1); return true; }
        if (key == Keys.Multiply) { insBaseOctave = Math.Min(module.Kind == TrackerKind.Stm ? 4 : 7, insBaseOctave + 1); return true; }
        int semi;
        if (!PatternGrid.TryPiano(key, out semi)) return false;
        int n = insBaseOctave * 12 + semi;
        int maxOct = module.Kind == TrackerKind.Stm ? 4 : 7;
        n = Math.Max(0, Math.Min((maxOct + 1) * 12 - 1, n));
        int ins = module.Samples.IndexOf(CurrentSample) + 1;
        if (ins > 0) Jam(ins, TrackerCell.MakeNote(n / 12, n % 12), 0);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tips.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>샘플 파형 그림 (루프 구간 표시).</summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class WaveView : Control
{
    public TrackerSample Sample;

    public WaveView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = PatternGrid.Back;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int mid = Height / 2;
        using (var p = new Pen(PatternGrid.Dim)) g.DrawLine(p, 0, mid, Width, mid);
        var s = Sample;
        if (s == null || s.Length == 0) return;
        if (s.Loop && s.LoopEnd > s.LoopStart)
        {
            float x0 = (float)s.LoopStart / s.Length * Width, x1 = (float)s.LoopEnd / s.Length * Width;
            using (var b = new SolidBrush(Color.FromArgb(40, 40, 160))) g.FillRectangle(b, x0, 0, x1 - x0, Height);
        }
        using (var p = new Pen(PatternGrid.VolFg))
        {
            for (int x = 0; x < Width; x++)
            {
                int a = (int)((long)x * s.Length / Width), b = (int)Math.Min(s.Length, (long)(x + 1) * s.Length / Width + 1);
                int lo = 0, hi = 0;
                for (int i = a; i < b; i++) { lo = Math.Min(lo, s.Data[i]); hi = Math.Max(hi, s.Data[i]); }
                g.DrawLine(p, x, mid - hi * (mid - 2) / 32768, x, mid - lo * (mid - 2) / 32768);
            }
        }
    }
}

/// <summary>간단한 PCM WAV 읽기/쓰기 (8/16bit, 모노/스테레오 → 모노).</summary>
internal static class WavIo
{
    public static short[] Read(string path, out int rate, out bool is16)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 44 || d[0] != 'R' || d[8] != 'W') throw new InvalidDataException("RIFF WAVE가 아닙니다.");
        int p = 12, channels = 1, bits = 16; rate = 22050;
        while (p + 8 <= d.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(d, p, 4);
            int size = BitConverter.ToInt32(d, p + 4);
            int body = p + 8;
            if (id == "fmt ")
            {
                int fmt = BitConverter.ToUInt16(d, body);
                if (fmt != 1 && fmt != 0xFFFE) throw new InvalidDataException("PCM WAV만 읽을 수 있습니다.");
                channels = BitConverter.ToUInt16(d, body + 2);
                rate = BitConverter.ToInt32(d, body + 4);
                bits = BitConverter.ToUInt16(d, body + 14);
            }
            else if (id == "data")
            {
                int bytes = bits / 8;
                int frames = Math.Min(size, d.Length - body) / Math.Max(1, bytes * channels);
                var r = new short[frames];
                for (int f = 0; f < frames; f++)
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int o = body + (f * channels + c) * bytes;
                        sum += bits == 8 ? (d[o] - 128) << 8 : bits == 16 ? BitConverter.ToInt16(d, o) : bits == 24 ? (short)(BitConverter.ToInt16(d, o + 1)) : (short)(BitConverter.ToInt32(d, o) >> 16);
                    }
                    r[f] = (short)(sum / channels);
                }
                is16 = bits > 8;
                return r;
            }
            p = body + size + (size & 1);
        }
        throw new InvalidDataException("data 덩어리가 없습니다.");
    }

    public static void Write(string path, short[] data, int rate)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data.Length * 2);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(data.Length * 2);
            foreach (var v in data) w.Write(v);
        }
    }
}
