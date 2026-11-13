using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AdlibComposer.Core;

namespace AdlibComposer.App;

/// <summary>
/// 뱅크 브라우저 (현대식 UI): 폴더의 BNK 파일 목록 → 뱅크의 악기 목록(검색, 종류, 곡 사용 여부) → 시음 →
/// 뱅크 전체를 현재 뱅크로 쓰거나, 고른 악기만 현재 뱅크에 추가한다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class BankBrowserForm : Form
{
    private static string lastFolder;

    private static readonly Color Accent = Color.FromArgb(0, 103, 192);
    private static readonly Color Subtle = Color.FromArgb(96, 96, 96);
    private static readonly Color PaneBack = Color.FromArgb(249, 249, 249);
    private static readonly Color Border = Color.FromArgb(224, 224, 224);

    private readonly Workspace ws;
    private readonly Func<bool> confirmDiscardBank;
    private readonly Dictionary<string, BankEntry> cache = new Dictionary<string, BankEntry>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> songInstruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly Font baseFont = new Font("Segoe UI", 9f);
    private readonly Font titleFont = new Font("Segoe UI Semibold", 10.5f);
    private readonly Font monoFont = new Font("Consolas", 9f);

    private readonly TextBox folderBox = new TextBox();
    private readonly Button browseButton = new Button();
    private readonly Button openFileButton = new Button();
    private readonly ListView bankList = new ListView();
    private readonly Label bankTitle = new Label();
    private readonly Label bankSubtitle = new Label();
    private readonly TextBox searchBox = new TextBox();
    private readonly ComboBox typeFilter = new ComboBox();
    private readonly ListView instList = new ListView();
    private readonly Label detailName = new Label();
    private readonly Label detailText = new Label();
    private readonly Button previewButton = new Button();
    private readonly Button useBankButton = new Button();
    private readonly Button addButton = new Button();
    private readonly Button closeButton = new Button();
    private readonly Label statusLabel = new Label();
    private readonly Timer previewTimer = new Timer { Interval = 700 };

    private BankEntry currentEntry;
    private int previewVoice = -1;

    private sealed class BankEntry
    {
        public string Path;
        public InstrumentBank Bank;
        public string Error;
        public int Used { get { return Bank == null ? 0 : Bank.UsedCount; } }
    }

    /// <summary>뱅크를 바꾸거나 악기를 추가했으면 true.</summary>
    public bool Changed { get; private set; }

    public BankBrowserForm(Workspace ws, Func<bool> confirmDiscardBank)
    {
        this.ws = ws;
        this.confirmDiscardBank = confirmDiscardBank;
        foreach (var v in ws.Song.Voices)
            foreach (var t in v.Timbres)
                if (!string.IsNullOrEmpty(t.InstrumentName)) songInstruments.Add(t.InstrumentName);

        Text = "Bank Browser";
        Font = baseFont;
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1040, 640);
        MinimumSize = new Size(820, 480);
        KeyPreview = true;

        BuildLayout();
        previewTimer.Tick += delegate { StopPreview(); };
        FormClosed += delegate { StopPreview(); previewTimer.Dispose(); };

        string start = lastFolder;
        if (start == null && !string.IsNullOrEmpty(ws.Bank.FilePath)) start = Path.GetDirectoryName(ws.Bank.FilePath);
        if (start == null && !string.IsNullOrEmpty(ws.Song.FilePath)) start = Path.GetDirectoryName(ws.Song.FilePath);
        if (start == null) start = Directory.GetCurrentDirectory();
        Shown += delegate { LoadFolder(start, ws.Bank.FilePath); };
    }

    // ================================================================ 화면 구성

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16, 12, 16, 12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // ---- 상단: 폴더
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var folderLabel = new Label { Text = "Folder", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Subtle, Margin = new Padding(0, 0, 8, 0) };
        folderBox.ReadOnly = true;
        folderBox.BackColor = Color.White;
        folderBox.Dock = DockStyle.Fill;
        folderBox.Margin = new Padding(0, 2, 8, 2);
        StyleButton(browseButton, "Change folder...", false);
        StyleButton(openFileButton, "Open BNK file...", false);
        browseButton.Click += delegate { ChooseFolder(); };
        openFileButton.Click += delegate { ChooseFile(); };
        top.Controls.Add(folderLabel, 0, 0);
        top.Controls.Add(folderBox, 1, 0);
        top.Controls.Add(browseButton, 2, 0);
        top.Controls.Add(openFileButton, 3, 0);
        root.Controls.Add(top, 0, 0);

        // ---- 가운데: 뱅크 목록 | 악기 목록
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 8, FixedPanel = FixedPanel.Panel1, BackColor = Color.White };
        split.Panel1.Padding = new Padding(0);
        root.Controls.Add(split, 0, 1);
        split.HandleCreated += delegate { split.SplitterDistance = 330; };

        var leftHeader = new Label { Text = "Bank files", Font = titleFont, Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
        SetupList(bankList);
        bankList.MultiSelect = false;
        bankList.Columns.Add("File", 150);
        bankList.Columns.Add("Instruments", 80, HorizontalAlignment.Right);
        bankList.Columns.Add("Song", 70, HorizontalAlignment.Right);
        bankList.SelectedIndexChanged += delegate { if (bankList.SelectedItems.Count > 0) ShowBank((BankEntry)bankList.SelectedItems[0].Tag); };
        bankList.DoubleClick += delegate { UseBank(); };
        var bankHint = new Label { Text = "Song = instruments of the current song found in the bank", Dock = DockStyle.Bottom, Height = 22, ForeColor = Subtle, TextAlign = ContentAlignment.MiddleLeft };
        split.Panel1.Controls.Add(bankList);
        split.Panel1.Controls.Add(bankHint);
        split.Panel1.Controls.Add(leftHeader);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        split.Panel2.Controls.Add(right);

        var titleRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        bankTitle.Font = titleFont; bankTitle.AutoSize = true; bankTitle.Margin = new Padding(0, 4, 8, 0);
        bankSubtitle.ForeColor = Subtle; bankSubtitle.AutoSize = true; bankSubtitle.Margin = new Padding(0, 7, 0, 0);
        titleRow.Controls.Add(bankTitle);
        titleRow.Controls.Add(bankSubtitle);
        right.Controls.Add(titleRow, 0, 0);

        var filterRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchBox.Dock = DockStyle.Fill;
        searchBox.Margin = new Padding(0, 0, 8, 0);
        searchBox.PlaceholderText = "Search instruments";
        searchBox.TextChanged += delegate { FillInstruments(); };
        typeFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        typeFilter.Width = 150;
        typeFilter.Items.AddRange(new object[] { "All types", "Melodic", "Percussion", "Used in song" });
        typeFilter.SelectedIndex = 0;
        typeFilter.SelectedIndexChanged += delegate { FillInstruments(); };
        filterRow.Controls.Add(searchBox, 0, 0);
        filterRow.Controls.Add(typeFilter, 1, 0);
        right.Controls.Add(filterRow, 0, 1);

        SetupList(instList);
        instList.MultiSelect = true;
        instList.Columns.Add("Name", 130);
        instList.Columns.Add("Type", 90);
        instList.Columns.Add("Synthesis", 80);
        instList.Columns.Add("In song", 64, HorizontalAlignment.Center);
        instList.Columns.Add("In current bank", 110, HorizontalAlignment.Center);
        instList.SelectedIndexChanged += delegate { ShowDetail(); UpdateButtons(); };
        instList.DoubleClick += delegate { PreviewSelected(true); };
        instList.KeyDown += delegate (object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { PreviewSelected(true); e.Handled = true; e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.A && e.Control) { foreach (ListViewItem it in instList.Items) it.Selected = true; e.Handled = true; }
        };
        right.Controls.Add(instList, 0, 2);

        // 상세 + 시음
        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Padding = new Padding(12, 10, 12, 10), Margin = new Padding(0, 8, 0, 0), BackColor = PaneBack };
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        detail.Paint += delegate (object s, PaintEventArgs e) { using (var p = new Pen(Border)) e.Graphics.DrawRectangle(p, 0, 0, detail.Width - 1, detail.Height - 1); };
        var detailStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, BackColor = PaneBack, Margin = new Padding(0) };
        detailName.Font = titleFont; detailName.AutoSize = true; detailName.Margin = new Padding(0, 0, 0, 4);
        detailText.Font = monoFont; detailText.AutoSize = true; detailText.ForeColor = Color.FromArgb(40, 40, 40); detailText.Margin = new Padding(0);
        detailStack.Controls.Add(detailName);
        detailStack.Controls.Add(detailText);
        StyleButton(previewButton, "▶  Preview", false);
        previewButton.Anchor = AnchorStyles.Right;
        previewButton.MouseDown += delegate { PreviewSelected(false); };
        previewButton.MouseUp += delegate { StopPreview(); };
        detail.Controls.Add(detailStack, 0, 0);
        detail.Controls.Add(previewButton, 1, 0);
        right.Controls.Add(detail, 0, 3);

        // ---- 하단: 상태 + 동작
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusLabel.AutoSize = true; statusLabel.ForeColor = Subtle; statusLabel.Anchor = AnchorStyles.Left;
        StyleButton(addButton, "Add selected to current bank", false);
        StyleButton(useBankButton, "Use this bank", true);
        StyleButton(closeButton, "Close", false);
        addButton.Click += delegate { AddSelected(); };
        useBankButton.Click += delegate { UseBank(); };
        closeButton.Click += delegate { Close(); };
        CancelButton = closeButton;
        bottom.Controls.Add(statusLabel, 0, 0);
        bottom.Controls.Add(addButton, 1, 0);
        bottom.Controls.Add(useBankButton, 2, 0);
        bottom.Controls.Add(closeButton, 3, 0);
        root.Controls.Add(bottom, 0, 2);

        UpdateButtons();
    }

    private void StyleButton(Button b, string text, bool primary)
    {
        b.Text = text;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(10, 3, 10, 3);
        b.Margin = new Padding(6, 0, 0, 0);
        b.FlatStyle = FlatStyle.Flat;
        b.Cursor = Cursors.Hand;
        if (primary)
        {
            b.BackColor = Accent; b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = Accent;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 90, 170);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 75, 145);
        }
        else
        {
            b.BackColor = Color.White; b.ForeColor = Color.FromArgb(30, 30, 30);
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 243, 243);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(230, 230, 230);
        }
        b.EnabledChanged += delegate { b.ForeColor = !b.Enabled ? Color.FromArgb(160, 160, 160) : (primary ? Color.White : Color.FromArgb(30, 30, 30)); };
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    private static void SetupList(ListView lv)
    {
        lv.Dock = DockStyle.Fill;
        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.HideSelection = false;
        lv.BorderStyle = BorderStyle.FixedSingle;
        lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        lv.HandleCreated += delegate { try { SetWindowTheme(lv.Handle, "Explorer", null); } catch (Exception) { } };
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(lv, true);
    }

    // ================================================================ 폴더 / 뱅크 목록

    private void ChooseFolder()
    {
        using (var dlg = new FolderBrowserDialog { SelectedPath = folderBox.Text, Description = "Folder with .BNK files", UseDescriptionForTitle = true })
            if (dlg.ShowDialog(this) == DialogResult.OK) LoadFolder(dlg.SelectedPath, null);
    }

    private void ChooseFile()
    {
        using (var dlg = new OpenFileDialog { Filter = "AdLib instrument bank (*.bnk)|*.bnk|All files (*.*)|*.*", InitialDirectory = folderBox.Text, Title = "Open instrument bank" })
            if (dlg.ShowDialog(this) == DialogResult.OK) LoadFolder(Path.GetDirectoryName(dlg.FileName), dlg.FileName);
    }

    private void LoadFolder(string folder, string select)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = Directory.GetCurrentDirectory();
        lastFolder = folder;
        folderBox.Text = folder;
        Cursor = Cursors.WaitCursor;
        bankList.BeginUpdate();
        try
        {
            bankList.Items.Clear();
            string[] files;
            try { files = Directory.GetFiles(folder, "*.bnk"); }
            catch (Exception ex) { files = new string[0]; statusLabel.Text = ex.Message; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            ListViewItem toSelect = null;
            foreach (var f in files)
            {
                var e = Load(f);
                var it = new ListViewItem(Path.GetFileName(f)) { Tag = e };
                if (e.Bank == null)
                {
                    it.SubItems.Add("error"); it.SubItems.Add("");
                    it.ForeColor = Color.Firebrick;
                    it.ToolTipText = e.Error;
                }
                else
                {
                    it.SubItems.Add(e.Used.ToString());
                    int match = songInstruments.Count(n => e.Bank.Find(n) != null);
                    it.SubItems.Add(songInstruments.Count == 0 ? "-" : match + "/" + songInstruments.Count);
                    if (songInstruments.Count > 0 && match == songInstruments.Count) it.ForeColor = Accent;
                }
                if (string.Equals(f, ws.Bank.FilePath, StringComparison.OrdinalIgnoreCase)) it.Font = new Font(baseFont, FontStyle.Bold);
                bankList.Items.Add(it);
                if (select != null && string.Equals(f, select, StringComparison.OrdinalIgnoreCase)) toSelect = it;
            }
            if (toSelect == null && bankList.Items.Count > 0) toSelect = BestMatch() ?? bankList.Items[0];
            if (toSelect != null) { toSelect.Selected = true; toSelect.Focused = true; toSelect.EnsureVisible(); }
            else ShowBank(null);
            if (files.Length == 0) statusLabel.Text = "No .bnk files in this folder.";
        }
        finally { bankList.EndUpdate(); Cursor = Cursors.Default; }
    }

    /// <summary>곡의 악기를 가장 많이 가진 뱅크.</summary>
    private ListViewItem BestMatch()
    {
        if (songInstruments.Count == 0) return null;
        ListViewItem best = null; int bestHit = 0;
        foreach (ListViewItem it in bankList.Items)
        {
            var e = (BankEntry)it.Tag;
            if (e.Bank == null) continue;
            int hit = songInstruments.Count(n => e.Bank.Find(n) != null);
            if (hit > bestHit) { bestHit = hit; best = it; }
        }
        return best;
    }

    private BankEntry Load(string path)
    {
        BankEntry e;
        if (cache.TryGetValue(path, out e)) return e;
        e = new BankEntry { Path = path };
        try
        {
            var fmt = ws.Formats.FindBank(path);
            using (var fs = File.OpenRead(path)) e.Bank = fmt.Read(fs);
            e.Bank.FilePath = path;
        }
        catch (Exception ex) { e.Error = ex.Message; }
        cache[path] = e;
        return e;
    }

    // ================================================================ 악기 목록

    private void ShowBank(BankEntry e)
    {
        StopPreview();
        currentEntry = e;
        if (e == null || e.Bank == null)
        {
            bankTitle.Text = e == null ? "No bank selected" : Path.GetFileName(e.Path);
            bankSubtitle.Text = e != null ? "Could not read: " + e.Error : "";
            instList.Items.Clear();
            ShowDetail();
            UpdateButtons();
            return;
        }
        bankTitle.Text = Path.GetFileName(e.Path);
        int perc = e.Bank.Instruments.Count(i => i.IsUsed && IsPercussion(i));
        bankSubtitle.Text = e.Used + " instruments  ·  " + (e.Used - perc) + " melodic, " + perc + " percussion  ·  v" + e.Bank.VersionMajor + "." + e.Bank.VersionMinor;
        FillInstruments();
    }

    private static bool IsPercussion(AdlibInstrument i) { return i.Percussive && i.VoiceNum >= 6 && i.VoiceNum <= 10; }

    private static string TypeName(AdlibInstrument i)
    {
        if (!IsPercussion(i)) return "Melodic";
        switch (i.VoiceNum)
        {
            case 6: return "Bass drum";
            case 7: return "Snare drum";
            case 8: return "Tom";
            case 9: return "Cymbal";
            default: return "Hi-hat";
        }
    }

    private static bool SingleOperator(AdlibInstrument i) { return IsPercussion(i) && i.VoiceNum >= 7; }

    private void FillInstruments()
    {
        instList.BeginUpdate();
        try
        {
            instList.Items.Clear();
            if (currentEntry == null || currentEntry.Bank == null) return;
            string q = searchBox.Text.Trim();
            int type = typeFilter.SelectedIndex;
            foreach (var ins in currentEntry.Bank.Instruments)
            {
                if (!ins.IsUsed) continue;
                if (q.Length > 0 && ins.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool inSong = songInstruments.Contains(ins.Name);
                if (type == 1 && IsPercussion(ins)) continue;
                if (type == 2 && !IsPercussion(ins)) continue;
                if (type == 3 && !inSong) continue;
                var it = new ListViewItem(ins.Name) { Tag = ins };
                it.SubItems.Add(TypeName(ins));
                it.SubItems.Add(SingleOperator(ins) ? "1 operator" : ins.Additive ? "Additive" : "FM");
                it.SubItems.Add(inSong ? "♪" : "");
                it.SubItems.Add(ws.Bank.Find(ins.Name) != null && !ReferenceEquals(currentEntry.Bank, ws.Bank) ? "✓" : "");
                instList.Items.Add(it);
            }
            if (instList.Items.Count > 0) { instList.Items[0].Selected = true; instList.Items[0].Focused = true; }
        }
        finally { instList.EndUpdate(); }
        ShowDetail();
        UpdateButtons();
    }

    private List<AdlibInstrument> SelectedInstruments()
    {
        var l = new List<AdlibInstrument>();
        foreach (ListViewItem it in instList.SelectedItems) l.Add((AdlibInstrument)it.Tag);
        return l;
    }

    private static string OpLine(string label, OplOperator o)
    {
        return string.Format("{0}  AR {1,2}  DR {2,2}  SL {3,2}  RR {4,2}  Out {5,2}  Mult {6,2}  Wave {7}  KSL {8}{9}{10}{11}{12}",
            label, o.Attack & 15, o.Decay & 15, o.Sustain & 15, o.ReleaseRate & 15, 63 - (o.TotalLevel & 63), o.Multiple & 15, o.WaveSelect & 3, o.Ksl & 3,
            o.Eg != 0 ? "  Sus" : "", o.Ksr != 0 ? "  KSR" : "", o.Vib != 0 ? "  Vib" : "", o.Am != 0 ? "  Trem" : "");
    }

    private void ShowDetail()
    {
        var sel = SelectedInstruments();
        if (sel.Count == 0)
        {
            detailName.Text = currentEntry != null && currentEntry.Bank != null ? "Select an instrument" : "";
            detailText.Text = "";
        }
        else if (sel.Count > 1)
        {
            detailName.Text = sel.Count + " instruments selected";
            detailText.Text = string.Join(", ", sel.Take(12).Select(i => i.Name)) + (sel.Count > 12 ? ", ..." : "");
        }
        else
        {
            var i = sel[0];
            detailName.Text = i.Name + "   " + TypeName(i);
            if (SingleOperator(i)) detailText.Text = OpLine("Op ", i.Modulator);
            else detailText.Text = OpLine("Car", i.Carrier) + "\n" + OpLine("Mod", i.Modulator) + "\nFeedback " + (i.Modulator.Feedback & 7) + "   " + (i.Additive ? "Additive" : "FM");
        }
        previewButton.Enabled = sel.Count >= 1;
    }

    private void UpdateButtons()
    {
        bool hasBank = currentEntry != null && currentEntry.Bank != null;
        bool isCurrent = hasBank && string.Equals(currentEntry.Path, ws.Bank.FilePath, StringComparison.OrdinalIgnoreCase);
        useBankButton.Enabled = hasBank && !isCurrent;
        useBankButton.Text = isCurrent ? "Current bank" : "Use this bank";
        int n = instList.SelectedItems.Count;
        addButton.Enabled = hasBank && !isCurrent && n > 0;
        addButton.Text = n > 1 ? "Add " + n + " instruments to current bank" : "Add selected to current bank";
        string cur = string.IsNullOrEmpty(ws.Bank.FilePath) ? "(new bank)" : Path.GetFileName(ws.Bank.FilePath);
        statusLabel.Text = "Current bank: " + cur + " (" + ws.Bank.UsedCount + ")" + (ws.BankDirty ? " *" : "");
    }

    // ================================================================ 시음

    private void PreviewSelected(bool timed)
    {
        var sel = SelectedInstruments();
        if (sel.Count == 0) return;
        StopPreview();
        var ins = sel[0];
        int voice = IsPercussion(ins) ? ins.VoiceNum : 0;
        int note = voice == 6 ? 36 : voice == 8 ? 43 : Song.MiddleC;
        ws.Audio.PreviewSetMode(voice >= 6);
        ws.Audio.PreviewSetInstrument(voice, ins);
        ws.Audio.PreviewNoteOn(voice, note);
        previewVoice = voice;
        if (timed) previewTimer.Start();
    }

    private void StopPreview()
    {
        previewTimer.Stop();
        if (previewVoice >= 0) ws.Audio.PreviewNoteOff(previewVoice);
        previewVoice = -1;
    }

    // ================================================================ 동작

    private void UseBank()
    {
        if (currentEntry == null || currentEntry.Bank == null) return;
        if (string.Equals(currentEntry.Path, ws.Bank.FilePath, StringComparison.OrdinalIgnoreCase)) return;
        if (!confirmDiscardBank()) return;
        try
        {
            ws.OpenBank(currentEntry.Path);
            cache.Remove(currentEntry.Path);   // 현재 뱅크는 편집되므로 캐시와 분리
            Changed = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, "뱅크를 열 수 없습니다.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void AddSelected()
    {
        var sel = SelectedInstruments();
        if (sel.Count == 0) return;
        var clashes = sel.Where(i => ws.Bank.Find(i.Name) != null).Select(i => i.Name).ToList();
        bool replace = false;
        if (clashes.Count > 0)
        {
            var r = MessageBox.Show(this,
                "현재 뱅크에 같은 이름의 악기가 " + clashes.Count + "개 있습니다: " + string.Join(", ", clashes.Take(6)) + (clashes.Count > 6 ? ", ..." : "") +
                "\n\n예: 덮어쓰기   아니요: 새 이름으로 추가   취소: 그만두기",
                "Add instruments", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return;
            replace = r == DialogResult.Yes;
        }
        int added = ws.AddInstruments(sel, replace);
        Changed = true;
        FillInstruments();
        statusLabel.Text = added + " instrument(s) added to the current bank.";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F)) { searchBox.Focus(); searchBox.SelectAll(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
