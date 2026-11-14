using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AdlibComposer.App;

/// <summary>Preferences (현대식 UI): 곡 폴더, 뱅크 폴더, 기본 뱅크, 소리, 보이스 색. 저장하면 실행 폴더의 FMOPLTool.cfg에 쓴다.</summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class PreferencesForm : Form
{
    private static readonly Color Accent = Color.FromArgb(0, 103, 192);
    private static readonly Color Subtle = Color.FromArgb(96, 96, 96);

    private readonly AppSettings settings;
    private readonly TextBox songBox = new TextBox();
    private readonly TextBox imsBox = new TextBox();
    private readonly TextBox sopBox = new TextBox();
    private readonly TextBox trkBox = new TextBox();
    private readonly TextBox midBox = new TextBox();
    private readonly TextBox midiBankBox = new TextBox();
    private readonly CheckBox midiWinCheck = new CheckBox();
    private readonly ComboBox midiModuleBox = new ComboBox();
    private readonly TextBox mt32RomBox = new TextBox();
    private readonly TextBox bankFolderBox = new TextBox();
    private readonly TextBox bankBox = new TextBox();
    private readonly CheckBox loadBankCheck = new CheckBox();
    private readonly CheckBox loadNowCheck = new CheckBox();
    private readonly CheckBox nukedCheck = new CheckBox();
    private readonly Label bankStatus = new Label();
    private readonly Button[] colorButtons = new Button[Shell.VoiceColors.Count];
    private readonly Font titleFont = new Font("Segoe UI Semibold", 12f);
    private readonly Font headFont = new Font("Segoe UI Semibold", 9.5f);

    /// <summary>저장을 누르면 새 설정 (아니면 null).</summary>
    public AppSettings Result { get; private set; }
    /// <summary>"지금 이 뱅크 불러오기"를 골랐으면 true.</summary>
    public bool LoadBankNow { get { return Result != null && loadNowCheck.Checked; } }

    public PreferencesForm(AppSettings current)
    {
        settings = current.Clone();
        Text = "Preferences";
        AppIcon.Apply(this);
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(20, 16, 20, 16), Dock = DockStyle.Fill };
        Controls.Add(root);

        root.Controls.Add(new Label { Text = "Preferences", Font = titleFont, AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
        root.Controls.Add(new Label
        {
            Text = "Saved to " + AppSettings.FilePath + "\nPaths inside the program folder are stored relative to it.",
            ForeColor = Subtle, AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 0, 0, 14)
        });

        root.Controls.Add(Section("Folders"));
        root.Controls.Add(PathRow("ROL songs", songBox, settings.SongFolderPath, delegate { BrowseFolder(songBox, "ROL song folder"); }));
        root.Controls.Add(PathRow("IMS songs", imsBox, settings.ImsFolderPath, delegate { BrowseFolder(imsBox, "IMS song folder"); }));
        root.Controls.Add(PathRow("SOP songs (Jukebox)", sopBox, settings.SopFolderPath, delegate { BrowseFolder(sopBox, "SOP song folder"); }));
        root.Controls.Add(PathRow("S3M / STM (Tracker)", trkBox, settings.TrackerFolderPath, delegate { BrowseFolder(trkBox, "Scream Tracker song folder"); }));
        root.Controls.Add(PathRow("MIDI / WRK (Jukebox)", midBox, settings.MidiFolderPath, delegate { BrowseFolder(midBox, "MIDI song folder"); }));
        root.Controls.Add(PathRow("Instrument banks (BNK)", bankFolderBox, settings.BankFolderPath, delegate { BrowseFolder(bankFolderBox, "Instrument bank folder"); }));

        root.Controls.Add(Section("Default instrument bank"));
        root.Controls.Add(PathRow("Bank file", bankBox, settings.DefaultBankPath ?? "", delegate { BrowseBank(); }));
        bankStatus.AutoSize = true;
        bankStatus.Margin = new Padding(0, 0, 0, 6);
        root.Controls.Add(bankStatus);
        bankBox.TextChanged += delegate { UpdateBankStatus(); };

        loadBankCheck.Text = "Load the default bank when the program starts";
        loadBankCheck.AutoSize = true;
        loadBankCheck.Checked = settings.LoadDefaultBank;
        loadBankCheck.Margin = new Padding(0, 2, 0, 2);
        root.Controls.Add(loadBankCheck);
        loadNowCheck.Text = "Load this bank now";
        loadNowCheck.AutoSize = true;
        loadNowCheck.Margin = new Padding(0, 2, 0, 12);
        root.Controls.Add(loadNowCheck);

        root.Controls.Add(Section("Sound"));
        nukedCheck.Text = "Use Nuked OPL3 emulator (cycle-accurate, closest to a real chip; uses more CPU)";
        nukedCheck.AutoSize = true;
        nukedCheck.Checked = settings.UseNukedOpl;
        nukedCheck.Margin = new Padding(0, 2, 0, 2);
        root.Controls.Add(nukedCheck);
        root.Controls.Add(new Label
        {
            Text = "Applies everywhere: Composer, Instrument Maker preview, Jukebox (ROL / IMS / SOP) and S3M AdLib channels.\nOff = the built-in lightweight emulator. Switching while a song plays restarts the notes that are sounding.",
            ForeColor = Subtle, AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(18, 0, 0, 8)
        });
        midiWinCheck.Text = "Play MIDI (.mid / .wrk) with the Windows synthesizer instead of OPL FM";
        midiWinCheck.AutoSize = true;
        midiWinCheck.Checked = settings.MidiWindowsSynth;
        midiWinCheck.Margin = new Padding(0, 2, 0, 4);
        root.Controls.Add(midiWinCheck);
        root.Controls.Add(PathRow("MIDI FM bank (OP2 / BNK)", midiBankBox, settings.MidiBankPath ?? "", delegate { BrowseMidiBank(); }));
        var moduleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        moduleRow.Controls.Add(new Label { Text = "MIDI module", AutoSize = true, Margin = new Padding(0, 6, 8, 0), Width = 170 });
        midiModuleBox.DropDownStyle = ComboBoxStyle.DropDownList;
        midiModuleBox.Items.AddRange(new object[] { "General MIDI (GM)", "Roland MT-32" });
        midiModuleBox.SelectedIndex = settings.MidiMt32 ? 1 : 0;
        midiModuleBox.Width = 200;
        moduleRow.Controls.Add(midiModuleBox);
        root.Controls.Add(moduleRow);
        root.Controls.Add(PathRow("MT-32 ROM folder", mt32RomBox, settings.Mt32RomFolderPath, delegate { BrowseFolder(mt32RomBox, "MT-32 ROM folder (control + PCM ROM)"); }));
        root.Controls.Add(new Label
        {
            Text = "MT-32 uses the Munt emulator (mt32emu.dll) with your own Roland ROM files (e.g. MT32_CONTROL.ROM + MT32_PCM.ROM, or CM-32L).\nWithout ROMs, MT-32 songs play through GM (FM / Windows) with MT-32 instruments mapped to GM.",
            ForeColor = Subtle, AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 0, 0, 12)
        });
        root.Controls.Add(new Label
        {
            Text = "Empty = GENMIDI.OP2 in the bank folder if present, otherwise the default bank matched by instrument name.",
            ForeColor = Subtle, AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 0, 0, 12)
        });

        root.Controls.Add(Section("Voice colors"));
        root.Controls.Add(new Label
        {
            Text = "Composer note bars, Note heads and Jukebox channels (mixer + keyboard) use the same color per voice. Click a color to change it.\nRhythm mode: 7 = BD, 8 = SD, 9 = TO, 10 = CY, 11 = HH. Songs with more channels (SOP, MIDI, S3M) repeat these colors.",
            ForeColor = Subtle, AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 0, 0, 4)
        });
        var colorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(720, 0), Margin = new Padding(0, 0, 0, 12) };
        for (int i = 0; i < colorButtons.Length; i++)
        {
            int index = i;
            var cell = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 8, 0) };
            cell.Controls.Add(new Label { Text = Shell.VoiceColors.Labels[i], AutoSize = true, ForeColor = Subtle, Margin = new Padding(0, 0, 0, 2) });
            var b = new Button { Width = 52, Height = 26, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(0), Text = "" };
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            b.Click += delegate { PickColor(index); };
            colorButtons[i] = b;
            cell.Controls.Add(b);
            colorRow.Controls.Add(cell);
        }
        root.Controls.Add(colorRow);
        ShowColors(settings.VoiceColors);

        // 버튼 줄
        var buttons = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var reset = MakeButton("Reset to defaults", false);
        var save = MakeButton("Save", true);
        var cancel = MakeButton("Cancel", false);
        reset.Margin = new Padding(0);
        reset.Click += delegate { ResetDefaults(); };
        save.Click += delegate { SaveAndClose(); };
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(reset, 0, 0);
        buttons.Controls.Add(save, 2, 0);
        buttons.Controls.Add(cancel, 3, 0);
        root.Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;

        UpdateBankStatus();

        // 화면보다 길면: 저장 · 취소 줄은 아래에 고정하고 나머지는 스크롤
        Load += delegate
        {
            var wa = Screen.FromControl(this).WorkingArea;
            if (Height <= wa.Height - 20) return;
            int w = Width;
            AutoSize = false;
            root.Controls.Remove(buttons);
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = buttons.PreferredSize.Height + 24, Padding = new Padding(20, 10, 20 + SystemInformation.VerticalScrollBarWidth, 12), BackColor = Color.White };
            bottom.Paint += delegate (object o, PaintEventArgs e) { using (var p = new Pen(Color.FromArgb(225, 225, 225))) e.Graphics.DrawLine(p, 0, 0, bottom.Width, 0); };
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0);
            bottom.Controls.Add(buttons);
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            Controls.Remove(root);
            root.Dock = DockStyle.Top;
            scroller.Controls.Add(root);
            Controls.Add(scroller);
            Controls.Add(bottom);
            Size = new Size(w + SystemInformation.VerticalScrollBarWidth, wa.Height - 20);
            Location = new Point(Left, wa.Top + 10);
        };
    }

    // ---------------------------------------------------------------- 구성 요소

    private Label Section(string text)
    {
        return new Label { Text = text, Font = headFont, AutoSize = true, Margin = new Padding(0, 6, 0, 4) };
    }

    private Control PathRow(string label, TextBox box, string value, EventHandler browse)
    {
        var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 6) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var lb = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) };
        box.Text = value;
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0, 3, 8, 3);
        var b = MakeButton("Browse...", false);
        b.Margin = new Padding(0);
        b.Click += browse;
        row.Controls.Add(lb, 0, 0);
        row.Controls.Add(box, 1, 0);
        row.Controls.Add(b, 2, 0);
        return row;
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(6, 0, 0, 0), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(30, 30, 30)
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(200, 200, 200);
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0, 90, 170) : Color.FromArgb(243, 243, 243);
        return b;
    }

    // ---------------------------------------------------------------- 동작

    private void BrowseFolder(TextBox box, string title)
    {
        using (var dlg = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = true })
        {
            if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
        }
    }

    private void BrowseBank()
    {
        using (var dlg = new OpenFileDialog { Filter = "AdLib instrument bank (*.bnk)|*.bnk|All files (*.*)|*.*", Title = "Default instrument bank" })
        {
            string dir = File.Exists(bankBox.Text) ? Path.GetDirectoryName(bankBox.Text) : bankFolderBox.Text;
            if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
            if (dlg.ShowDialog(this) == DialogResult.OK) bankBox.Text = dlg.FileName;
        }
    }

    private void BrowseMidiBank()
    {
        using (var dlg = new OpenFileDialog { Filter = "GM FM bank (*.op2;*.bnk)|*.op2;*.bnk|All files (*.*)|*.*", Title = "MIDI FM bank" })
        {
            string dir = File.Exists(midiBankBox.Text) ? Path.GetDirectoryName(midiBankBox.Text) : bankFolderBox.Text;
            if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
            if (dlg.ShowDialog(this) == DialogResult.OK) midiBankBox.Text = dlg.FileName;
        }
    }

    private void ShowColors(Color[] c)
    {
        for (int i = 0; i < colorButtons.Length; i++)
        {
            var x = c != null && i < c.Length ? c[i] : Shell.VoiceColors.Defaults[i];
            colorButtons[i].BackColor = x;
            colorButtons[i].FlatAppearance.MouseOverBackColor = x;
        }
    }

    private void PickColor(int i)
    {
        using (var dlg = new ColorDialog { Color = colorButtons[i].BackColor, FullOpen = true, AnyColor = true })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            colorButtons[i].BackColor = dlg.Color;
            colorButtons[i].FlatAppearance.MouseOverBackColor = dlg.Color;
        }
    }

    private void UpdateBankStatus()
    {
        string p = bankBox.Text.Trim();
        bool ok = p.Length > 0 && File.Exists(SafeResolve(p));
        bankStatus.Text = p.Length == 0 ? "No default bank: new songs start with a small built-in bank."
            : ok ? "✓ Found" : "⚠ File not found";
        bankStatus.ForeColor = p.Length == 0 ? Subtle : ok ? Color.FromArgb(16, 124, 16) : Color.FromArgb(196, 43, 28);
    }

    private static string SafeResolve(string p)
    {
        try { return AppSettings.Resolve(p); } catch (Exception) { return p; }
    }

    private void ResetDefaults()
    {
        var d = new AppSettings();
        songBox.Text = d.SongFolderPath;
        imsBox.Text = d.ImsFolderPath;
        sopBox.Text = d.SopFolderPath;
        trkBox.Text = d.TrackerFolderPath;
        midBox.Text = d.MidiFolderPath;
        midiBankBox.Text = "";
        midiWinCheck.Checked = false;
        midiModuleBox.SelectedIndex = 0;
        mt32RomBox.Text = d.Mt32RomFolderPath;
        bankFolderBox.Text = d.BankFolderPath;
        bankBox.Text = d.DefaultBankPath;
        loadBankCheck.Checked = true;
        nukedCheck.Checked = false;
        ShowColors(Shell.VoiceColors.Defaults);
    }

    private void SaveAndClose()
    {
        try
        {
            settings.SongFolder = AppSettings.ToStored(SafeResolve(songBox.Text));
            settings.ImsFolder = AppSettings.ToStored(SafeResolve(imsBox.Text));
            settings.SopFolder = AppSettings.ToStored(SafeResolve(sopBox.Text));
            settings.TrackerFolder = AppSettings.ToStored(SafeResolve(trkBox.Text));
            settings.MidiFolder = AppSettings.ToStored(SafeResolve(midBox.Text));
            settings.MidiBank = midiBankBox.Text.Trim().Length == 0 ? "" : AppSettings.ToStored(SafeResolve(midiBankBox.Text));
            settings.MidiWindowsSynth = midiWinCheck.Checked;
            settings.MidiMt32 = midiModuleBox.SelectedIndex == 1;
            settings.Mt32RomFolder = AppSettings.ToStored(SafeResolve(mt32RomBox.Text));
            settings.BankFolder = AppSettings.ToStored(SafeResolve(bankFolderBox.Text));
            settings.DefaultBank = bankBox.Text.Trim().Length == 0 ? "" : AppSettings.ToStored(SafeResolve(bankBox.Text));
            settings.LoadDefaultBank = loadBankCheck.Checked;
            settings.UseNukedOpl = nukedCheck.Checked;
            var colors = new Color[colorButtons.Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = Color.FromArgb(colorButtons[i].BackColor.R, colorButtons[i].BackColor.G, colorButtons[i].BackColor.B);
            settings.VoiceColors = colors;
            settings.EnsureFolders();
            settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "설정을 저장할 수 없습니다.\n" + AppSettings.FilePath + "\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Result = settings;
        DialogResult = DialogResult.OK;
        Close();
    }
}
