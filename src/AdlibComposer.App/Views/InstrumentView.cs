using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AdlibComposer.App.Controls;
using AdlibComposer.Core;

namespace AdlibComposer.App.Views;

/// <summary>
/// Instrument Maker 화면.
/// <code>
/// ┌ 파일 정보: 뱅크 파일 · 경로 · 악기 수 · 지금 악기 번호 ───────────────────────┐
/// ├ 옵션 (F6 메뉴를 버튼으로): 이름 · 모드 6개 · Additive · 건반 옥타브 · Master Volume ┤
/// ├ 뱅크 악기 목록 │ Carrier(왼쪽) / Modulator(오른쪽) 13개 파라미터               │
/// │ New Copy …     │                                                              │
/// └ 건반 ─────────────────────────────────────────────────────────────────────────┘
/// </code>
/// 스네어/탐탐/심벌/하이햇 모드는 단일 오퍼레이터라 Carrier 열 하나만 보인다
/// (값은 BNK의 모듈레이터 필드에 저장됨 — 실제 STANDARD.BNK snare1/tom1/cymbal1/hihat1로 확인).
/// 예전의 &lt; &gt; 버튼과 List... 대화상자 대신 왼쪽 목록에서 고르고 편집한다 (↑ ↓로도 이동).
/// 4-op (SOP · OPL3, 이 프로그램의 추가 기능): 4-Op 버튼으로 켜고, Op 1·2 / Op 3·4 버튼으로 두 짝을 번갈아 편집한다.
/// Additive는 지금 보이는 짝의 연결 비트 (OPL3 CNT 두 개 = 연결 4가지). 4-op 악기는 OPL3로 시음한다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class InstrumentView : UserControl
{
    private const int InfoHeight = 30;
    private const int OptionTop = InfoHeight;
    private const int OptionHeight = 36;
    private const int TopHeight = InfoHeight + OptionHeight;
    private const int ListWidth = 230;
    private const int KeyboardHeight = 84;

    private static readonly Color Line = Color.FromArgb(200, 200, 200);
    private static readonly Color Muted = Color.FromArgb(110, 110, 110);
    private static readonly Color InfoBack = Color.FromArgb(244, 244, 244);
    private static readonly Font ButtonFont = new Font("Arial", 8.5f, FontStyle.Bold);

    private sealed class RowControls
    {
        public ParameterInfo Info;
        public Label Label;
        public Control Carrier;     // RetroSlider 또는 CheckBox (ModulatorOnly면 null)
        public Control Modulator;
    }

    private readonly Workspace ws;
    private readonly List<RowControls> rows = new List<RowControls>();
    private readonly Label carrierHeader = new Label();
    private readonly Label modulatorHeader = new Label();
    private readonly PianoKeyboardControl keyboard = new PianoKeyboardControl();
    private readonly Dictionary<Keys, int> keyMap = new Dictionary<Keys, int>();
    private readonly Dictionary<Keys, int> held = new Dictionary<Keys, int>();
    private readonly ToolTip tips = new ToolTip();

    // 위: 파일 정보
    private readonly Label lblBankFile = new Label();
    private readonly Label lblBankPath = new Label();
    private readonly Label lblInfo = new Label();
    // 위: 옵션 버튼 (원본 F6 Option 메뉴)
    private readonly Label lblName = new Label();
    private readonly TextBox txtName = new TextBox();
    private readonly List<Button> modeButtons = new List<Button>();
    private readonly Button btnAdditive = new Button();
    private readonly Button btn4Op = new Button();
    private readonly Button btnPair12 = new Button();
    private readonly Button btnPair34 = new Button();
    private readonly Label lblAlgo = new Label();
    /// <summary>4-op 악기의 둘째 짝(오퍼레이터 3·4)을 편집 중인지.</summary>
    private bool showPair2;
    /// <summary>둘째 짝을 푼 오퍼레이터 (편집하면 <see cref="AdlibInstrument.SetPairOperators"/>로 되돌려 쓴다).</summary>
    private OplOperator pairOp3, pairOp4;
    private readonly Button btnOctDown = new Button();
    private readonly Button btnOctUp = new Button();
    private readonly Label lblRange = new Label();
    private readonly Button btnVolume = new Button();
    private static readonly string[] ModeNames = { "Melodic", "Bass Drum", "Snare", "Tom", "Cymbal", "Hi-hat" };
    private static readonly int[] ModeVoices = { 0, 6, 7, 8, 9, 10 };
    // 왼쪽: 뱅크 악기 목록
    private readonly Label listHeader = new Label();
    private readonly ListView list = new ListView();
    private readonly Button btnNew = new Button(), btnCopy = new Button(), btnRename = new Button(), btnDelete = new Button();
    private string listSignature;
    private bool syncingList;

    private AdlibInstrument current;
    private bool binding;
    private bool activeCarrier = true;
    private int baseNote = Song.MiddleC;

    public event Action InstrumentEdited;

    private int previewVoice;

    /// <summary>악기 모드 = 시음 보이스 (0 = Melodic, 6~10 = Bass Drum/Snare/Tom/Cymbal/Hi-hat). 원본 Option 메뉴의 ... Mode.</summary>
    public int PreviewVoice
    {
        get { return previewVoice; }
        set { previewVoice = value; ApplyLayout(); ApplyModeRange(); }
    }

    private int melodicBaseNote = Song.MiddleC;
    private bool drumRange;

    /// <summary>드럼 모드(BD~HH)는 건반을 C2부터 (드럼은 낮은 음에서 드럼 소리가 난다), 멜로딕으로 돌아오면 이전 옥타브.</summary>
    private void ApplyModeRange()
    {
        bool drum = previewVoice >= 6;
        if (drum == drumRange) return;
        drumRange = drum;
        if (drum) { melodicBaseNote = baseNote; baseNote = ComposerDrumBase; }
        else baseNote = melodicBaseNote;
        RefreshKeyLabels();
    }

    private const int ComposerDrumBase = 36;

    /// <summary>단일 오퍼레이터 퍼커션 모드 (SD/TO/CY/HH): Carrier 열만 보이고, 값은 파일의 모듈레이터 필드.</summary>
    public bool SingleOperator { get { return previewVoice >= 7; } }

    public event Action ModeChanged;

    /// <summary>파일 값이 들어 있는 오퍼레이터. 단일 오퍼레이터 모드의 Carrier 열 = 파일의 모듈레이터 필드.</summary>
    private OplOperator OpFor(bool carrierColumn)
    {
        if (current == null) return null;
        if (SingleOperator) return current.Modulator;
        if (EditingPair2) return carrierColumn ? pairOp4 : pairOp3;
        return current.GetOperator(carrierColumn);
    }

    /// <summary>4-op는 멜로디 모드에서만 (드럼 채널은 짝을 지을 수 없다).</summary>
    public bool FourOpAvailable { get { return current != null && previewVoice == 0; } }

    private bool EditingPair2 { get { return showPair2 && current != null && current.Is4Op && pairOp3 != null && previewVoice == 0; } }

    /// <summary>4-Op 켜기 / 끄기. 켜면 오퍼레이터 3·4를 1·2와 같은 값으로 시작한다.</summary>
    public void Toggle4Op()
    {
        if (!FourOpAvailable) return;
        if (current.Is4Op)
        {
            if (MessageBox.Show(FindForm(), "'" + current.Name + "'의 오퍼레이터 3·4를 지우고 2-op 악기로 바꿀까요?", "4-Op",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            current.Sop4OpPair = null;
            showPair2 = false;
        }
        else
        {
            var op3 = current.Modulator.Clone();
            var op4 = current.Carrier.Clone();
            op3.Con = op4.Con = 1;   // 둘째 짝은 FM으로 시작 (1→2→3→4 또는 1 + 2→3→4)
            current.SetPairOperators(op3, op4);
        }
        LoadPair();
        Edited();
        Bind(current);
    }

    /// <summary>Op 1·2 / Op 3·4 짝 고르기 (4-op 악기).</summary>
    public void ShowPair(bool second)
    {
        if (second && (current == null || !current.Is4Op)) return;
        if (showPair2 == second) return;
        showPair2 = second;
        Bind(current);
    }

    private void LoadPair()
    {
        if (current != null && current.Is4Op) current.GetPairOperators(out pairOp3, out pairOp4);
        else { pairOp3 = pairOp4 = null; showPair2 = false; }
    }

    /// <summary>Option → Melodic/Bass Drum/.../Hi-hat Mode: 시음 보이스를 바꾸고 악기의 퍼커션 정보(BNK iPercussive/iVoiceNum)에 기록한다.</summary>
    public void SetMode(int voice)
    {
        ws.Audio.PreviewAllOff();
        PreviewVoice = voice;
        if (current != null)
        {
            bool perc = voice >= 6;
            int num = perc ? voice : 0;
            if (current.Percussive != perc || current.VoiceNum != num)
            {
                current.Percussive = perc;
                current.VoiceNum = num;
                ws.BankDirty = true;
                if (InstrumentEdited != null) InstrumentEdited();
            }
        }
        Bind(current);
    }

    /// <summary>Option → Additive Synthesis (connection 비트). 멜로딕/베이스드럼 모드에서만 의미가 있다.</summary>
    public bool Additive
    {
        get { return current != null && (EditingPair2 ? pairOp3.Con == 0 : current.Additive); }
        set
        {
            if (current == null || Additive == value) return;
            int con = value ? 0 : 1;
            if (EditingPair2) { pairOp3.Con = con; pairOp4.Con = con; }
            else { current.Modulator.Con = con; current.Carrier.Con = con; }
            Edited();
            UpdateOptionButtons();
            if (ModeChanged != null) ModeChanged();
        }
    }

    public bool AdditiveAvailable { get { return current != null && !SingleOperator; } }

    /// <summary>Option → Transpose Keyboard Up/Down (한 옥타브).</summary>
    public void TransposeKeyboard(int octaves)
    {
        baseNote = Math.Max(36, Math.Min(84, baseNote + 12 * octaves));
        RefreshKeyLabels();
    }

    public InstrumentView(Workspace ws)
    {
        this.ws = ws;
        BackColor = Theme.Back;
        DoubleBuffered = true;
        BuildKeyMap();
        BuildInfoBar();
        BuildOptionBar();
        BuildBankList();
        BuildColumnHeaders();
        BuildRows();

        keyboard.NoteDown += NotePressed;
        keyboard.NoteUp += NoteReleased;
        Controls.Add(keyboard);
        RefreshKeyLabels();

        ws.CurrentInstrumentChanged += delegate { Bind(ws.CurrentInstrument); };
        ws.BankReplaced += delegate { Bind(ws.CurrentInstrument); };
        Bind(ws.CurrentInstrument);
    }

    // ------------------------------------------------------------ 구성: 위쪽

    private void BuildInfoBar()
    {
        var bank = new Label { Text = "BANK", Font = Theme.SmallFont, ForeColor = Muted, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        bank.SetBounds(10, 0, 40, InfoHeight);
        lblBankFile.Font = Theme.UiFont;
        lblBankFile.AutoSize = true;
        lblBankFile.Location = new Point(50, 7);
        lblBankPath.Font = new Font("Arial", 8.5f);
        lblBankPath.ForeColor = Muted;
        lblBankPath.AutoSize = false;
        lblBankPath.AutoEllipsis = true;
        lblBankPath.TextAlign = ContentAlignment.MiddleLeft;
        lblInfo.Font = Theme.UiFont;
        lblInfo.AutoSize = false;
        lblInfo.TextAlign = ContentAlignment.MiddleRight;
        lblName.Text = "Name";
        lblName.Font = Theme.SmallFont;
        lblName.ForeColor = Muted;
        lblName.AutoSize = false;
        lblName.TextAlign = ContentAlignment.MiddleRight;
        txtName.MaxLength = AdlibInstrument.MaxNameLength;
        txtName.Font = Theme.UiFont;
        txtName.Leave += delegate { CommitName(); };
        txtName.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { CommitName(); e.SuppressKeyPress = true; } };
        tips.SetToolTip(txtName, "지금 악기 이름 (최대 8자, Enter로 적용)");
        foreach (var l in new[] { bank, lblBankFile, lblBankPath, lblInfo, lblName }) l.BackColor = InfoBack;
        Controls.AddRange(new Control[] { bank, lblBankFile, lblBankPath, lblInfo, lblName, txtName });
    }

    private void BuildOptionBar()
    {
        int y = OptionTop + 6, h = 24, x = 10;
        int[] widths = { 64, 78, 54, 44, 60, 54 };
        for (int i = 0; i < ModeNames.Length; i++)
        {
            int voice = ModeVoices[i];
            var b = OptionButton(ModeNames[i], x, y, widths[i], h);
            b.Click += delegate { SetMode(voice); };
            tips.SetToolTip(b, ModeNames[i] + " Mode (F6)");
            modeButtons.Add(b);
            x += b.Width - 1;   // 붙어 있는 버튼 묶음
        }
        x += 10;
        Setup(btnAdditive, "Additive", x, y, 68, h);
        btnAdditive.Click += delegate { if (AdditiveAvailable) Additive = !Additive; };
        tips.SetToolTip(btnAdditive, "Additive Synthesis (F6): 모듈레이터와 캐리어 소리를 더한다 (Melodic · Bass Drum 모드). 4-op면 지금 보이는 짝의 연결");
        x += 68 + 10;
        Setup(btn4Op, "4-Op", x, y, 48, h);
        btn4Op.Click += delegate { Toggle4Op(); };
        tips.SetToolTip(btn4Op, "4-오퍼레이터 악기 (SOP · OPL3): 켜기 / 끄기. Melodic 모드만. BNK · INS에는 저장되지 않고 SOP 곡에 들어간다");
        x += 47;
        Setup(btnPair12, "Op 1·2", x, y, 54, h);
        btnPair12.Click += delegate { ShowPair(false); };
        tips.SetToolTip(btnPair12, "오퍼레이터 1 (Modulator) · 2 (Carrier) 편집 (F3)");
        x += 53;
        Setup(btnPair34, "Op 3·4", x, y, 54, h);
        btnPair34.Click += delegate { ShowPair(true); };
        tips.SetToolTip(btnPair34, "오퍼레이터 3 (Modulator) · 4 (Carrier) 편집 (F4)");
        x += 58;
        lblAlgo.Font = Theme.SmallFont;
        lblAlgo.ForeColor = Muted;
        lblAlgo.AutoSize = false;
        lblAlgo.TextAlign = ContentAlignment.MiddleLeft;
        lblAlgo.SetBounds(x, y, 86, h);
        tips.SetToolTip(lblAlgo, "연결 (→ = 변조, + = 소리를 더함). 첫 짝 Additive + 둘째 짝 Additive로 4가지");
        Controls.Add(lblAlgo);
        x += 90;
        Setup(btnOctDown, "Oct −", x, y, 46, h);
        btnOctDown.Click += delegate { TransposeKeyboard(-1); };
        tips.SetToolTip(btnOctDown, "Transpose Keyboard Down (PgDn)");
        x += 45;
        Setup(btnOctUp, "Oct +", x, y, 46, h);
        btnOctUp.Click += delegate { TransposeKeyboard(1); };
        tips.SetToolTip(btnOctUp, "Transpose Keyboard Up (PgUp)");
        x += 50;
        lblRange.Font = Theme.SmallFont;
        lblRange.ForeColor = Muted;
        lblRange.AutoSize = false;
        lblRange.TextAlign = ContentAlignment.MiddleLeft;
        lblRange.SetBounds(x, y, 50, h);
        tips.SetToolTip(lblRange, "건반 시작 음 (왼쪽 끝 = Z 키)");
        Controls.Add(lblRange);
        x += 54;
        Setup(btnVolume, "Master Volume...", x, y, 112, h);
        btnVolume.Click += delegate { PromptVolume(); };
        tips.SetToolTip(btnVolume, "시음 · 재생 전체 음량 (F6 → Master Volume)");
    }

    private Button OptionButton(string text, int x, int y, int w, int h)
    {
        var b = new Button();
        Setup(b, text, x, y, w, h);
        return b;
    }

    private void Setup(Button b, string text, int x, int y, int w, int h)
    {
        b.Text = text;
        b.Font = ButtonFont;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.Black;
        b.BackColor = Color.White;
        b.ForeColor = Color.Black;
        b.TabStop = false;
        b.Cursor = Cursors.Hand;
        b.SetBounds(x, y, w, h);
        Controls.Add(b);
    }

    /// <summary>켜진 버튼은 원본처럼 검은 바탕 흰 글자.</summary>
    private static void SetOn(Button b, bool on)
    {
        b.BackColor = on ? Color.Black : Color.White;
        b.ForeColor = on ? Color.White : Color.Black;
        b.FlatAppearance.MouseOverBackColor = on ? Color.FromArgb(50, 50, 50) : Color.FromArgb(235, 235, 235);
    }

    private void UpdateOptionButtons()
    {
        for (int i = 0; i < modeButtons.Count; i++) SetOn(modeButtons[i], ModeVoices[i] == previewVoice);
        SetOn(btnAdditive, Additive && AdditiveAvailable);
        btnAdditive.Enabled = AdditiveAvailable;
        bool four = current != null && current.Is4Op;
        btn4Op.Enabled = FourOpAvailable;
        SetOn(btn4Op, four);
        btnPair12.Enabled = btnPair34.Enabled = four && FourOpAvailable;
        SetOn(btnPair12, four && FourOpAvailable && !EditingPair2);
        SetOn(btnPair34, EditingPair2);
        lblAlgo.Text = current == null || SingleOperator ? "" : current.FourOpAlgorithm;
        lblRange.Text = "from " + NoteHelper.Name(baseNote);
    }

    private void PromptVolume()
    {
        string v = Prompt.Show(FindForm(), "Master volume", "0 ~ 200 (%):", ((int)Math.Round(ws.Audio.MasterVolume * 100)).ToString());
        double d;
        if (v != null && double.TryParse(v, out d) && d >= 0 && d <= 200) ws.Audio.MasterVolume = d / 100.0;
    }

    // ------------------------------------------------------------ 구성: 왼쪽 뱅크 목록 (예전 List... 대화상자)

    private void BuildBankList()
    {
        listHeader.Text = "INSTRUMENTS";
        listHeader.Font = Theme.SmallFont;
        listHeader.ForeColor = Muted;
        listHeader.AutoSize = false;
        listHeader.TextAlign = ContentAlignment.MiddleLeft;
        listHeader.Padding = new Padding(8, 0, 0, 0);
        Controls.Add(listHeader);

        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.Font = Theme.UiFont;
        list.Columns.Add("#", 36, HorizontalAlignment.Right);
        list.Columns.Add("Name", 100);
        list.Columns.Add("Mode", 64);
        list.SelectedIndexChanged += delegate
        {
            if (syncingList || list.SelectedItems.Count == 0) return;
            var ins = list.SelectedItems[0].Tag as AdlibInstrument;
            if (ins != null && !ReferenceEquals(ins, current)) ws.SelectInstrument(ins);
        };
        tips.SetToolTip(list, "뱅크 안 악기 (↑ ↓로 이동, 고르면 바로 편집)");
        Controls.Add(list);


        foreach (var (b, text, tip, act) in new (Button, string, string, Action)[]
        {
            (btnNew, "New", "새 악기", AddNew),
            (btnCopy, "Copy", "고른 악기 복사", Duplicate),
            (btnRename, "Rename", "이름 바꾸기 (위쪽 Name 칸에서도 됨)", Rename),
            (btnDelete, "Delete", "고른 악기 삭제", Delete),
        })
        {
            Setup(b, text, 0, 0, 50, 24);
            b.Font = Theme.SmallFont;
            b.Click += delegate { act(); };
            tips.SetToolTip(b, tip);

        }
    }

    private static string ModeText(AdlibInstrument i)
    {
        if (i.Is4Op) return "4-op";
        if (!i.Percussive) return "Melodic";
        switch (i.VoiceNum) { case 6: return "BD"; case 7: return "SD"; case 8: return "Tom"; case 9: return "Cymbal"; case 10: return "Hi-hat"; }
        return "Melodic";
    }

    /// <summary>목록을 뱅크와 맞춘다. 악기 이름 · 순서가 그대로면 다시 만들지 않고 모드와 선택만 바꾼다.</summary>
    private void SyncList()
    {
        var used = UsedList();
        var sig = new System.Text.StringBuilder();
        foreach (var i in used) sig.Append(i.Name).Append('|').Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(i)).Append(';');
        syncingList = true;
        try
        {
            if (sig.ToString() != listSignature)
            {
                listSignature = sig.ToString();
                list.BeginUpdate();
                list.Items.Clear();
                for (int k = 0; k < used.Count; k++)
                {
                    var it = new ListViewItem((k + 1).ToString()) { Tag = used[k] };
                    it.SubItems.Add(used[k].Name);
                    it.SubItems.Add(ModeText(used[k]));
                    list.Items.Add(it);
                }
                list.EndUpdate();
            }
            foreach (ListViewItem it in list.Items)
            {
                var ins = (AdlibInstrument)it.Tag;
                string mode = ModeText(ins);
                if (it.SubItems[2].Text != mode) it.SubItems[2].Text = mode;
                bool sel = ReferenceEquals(ins, current);
                if (it.Selected != sel) it.Selected = sel;
                if (sel) { it.Focused = true; it.EnsureVisible(); }
            }
        }
        finally { syncingList = false; }
        btnCopy.Enabled = btnRename.Enabled = btnDelete.Enabled = current != null;
    }

    private void AddNew()
    {
        string name = Prompt.Show(FindForm(), "New instrument", "Name (max 8 chars):", ws.Bank.UniqueName("NEW"));
        if (name == null) return;
        name = AdlibInstrument.NormalizeName(name);
        if (name.Length == 0) return;
        if (ws.Bank.Find(name) != null) name = ws.Bank.UniqueName(name);
        var ins = AdlibInstrument.CreateDefault(name);
        ws.Bank.Add(ins);
        ListChanged(ins);
    }

    private void Duplicate()
    {
        if (current == null) return;
        var c = current.Clone();
        c.Name = ws.Bank.UniqueName(c.Name);
        ws.Bank.Add(c);
        ListChanged(c);
    }

    private void Rename()
    {
        var cur = current;
        if (cur == null) return;
        string name = Prompt.Show(FindForm(), "Rename", "Name (max 8 chars):", cur.Name);
        if (name == null) return;
        name = AdlibInstrument.NormalizeName(name);
        if (name.Length == 0) return;
        var other = ws.Bank.Find(name);
        if (other != null && !ReferenceEquals(other, cur)) { MessageBox.Show(FindForm(), "같은 이름의 악기가 이미 있습니다."); return; }
        cur.Name = name;
        ws.Bank.SortByName();
        ListChanged(cur);
    }

    private void Delete()
    {
        var cur = current;
        if (cur == null) return;
        if (MessageBox.Show(FindForm(), "'" + cur.Name + "' 악기를 삭제할까요?", "Delete", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        var used = UsedList();
        int idx = used.IndexOf(cur);
        ws.Bank.Remove(cur);
        used = UsedList();
        ListChanged(used.Count == 0 ? null : used[Math.Max(0, Math.Min(idx, used.Count - 1))]);
    }

    private void ListChanged(AdlibInstrument select)
    {
        ws.BankDirty = true;
        ws.SelectInstrument(select);
        Bind(select);   // 같은 악기를 고른 경우에도 목록을 다시 맞춘다
        if (InstrumentEdited != null) InstrumentEdited();
    }

    // ------------------------------------------------------------ 구성: 파라미터

    private void BuildColumnHeaders()
    {
        carrierHeader.Text = "F1  Carrier";
        modulatorHeader.Text = "F2  Modulator";
        foreach (var h in new[] { carrierHeader, modulatorHeader })
        {
            h.Font = Theme.UiFont; h.TextAlign = ContentAlignment.MiddleCenter; h.Height = 22; h.AutoSize = false;
        }
        Controls.AddRange(new Control[] { carrierHeader, modulatorHeader });
        UpdateHeaderHighlight();
    }

    private void BuildRows()
    {
        foreach (var info in ParameterInfo.All)
        {
            var row = new RowControls { Info = info };
            row.Label = new Label { Text = info.Label, Font = Theme.UiFont, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Height = 22 };
            Controls.Add(row.Label);
            if (!info.ModulatorOnly) row.Carrier = MakeControl(info, true);
            row.Modulator = MakeControl(info, false);
            rows.Add(row);
        }
    }

    private Control MakeControl(ParameterInfo info, bool carrier)
    {
        Control c;
        if (info.IsToggle)
        {
            var cb = new CheckBox { Text = "", Width = 22, Height = 22 };
            cb.CheckedChanged += delegate
            {
                if (binding || current == null) return;
                OpFor(carrier).Set(info.Id, cb.Checked ? 1 : 0);
                Edited();
            };
            c = cb;
        }
        else
        {
            var sl = new RetroSlider { Minimum = info.Min, Maximum = info.Max, ShowWaveGlyph = info.Id == OperatorParameter.WaveForm };
            sl.ValueChanged += delegate
            {
                if (binding || current == null) return;
                OpFor(carrier).Set(info.Id, sl.Value);
                Edited();
            };
            c = sl;
        }
        c.Enter += delegate { activeCarrier = carrier; UpdateHeaderHighlight(); };
        Controls.Add(c);
        return c;
    }

    /// <summary>열 머리글: 4-op 악기는 오퍼레이터 번호를 붙인다.</summary>
    private void UpdateHeaderText()
    {
        bool four = current != null && current.Is4Op && !SingleOperator;
        if (!four) { carrierHeader.Text = "F1  Carrier"; modulatorHeader.Text = "F2  Modulator"; return; }
        carrierHeader.Text = EditingPair2 ? "F1  Op 4  Carrier" : "F1  Op 2  Carrier";
        modulatorHeader.Text = EditingPair2 ? "F2  Op 3  Modulator" : "F2  Op 1  Modulator";
    }

    private void UpdateHeaderHighlight()
    {
        carrierHeader.BackColor = activeCarrier ? Color.Black : Color.White;
        carrierHeader.ForeColor = activeCarrier ? Color.White : Color.Black;
        modulatorHeader.BackColor = activeCarrier ? Color.White : Color.Black;
        modulatorHeader.ForeColor = activeCarrier ? Color.Black : Color.White;
    }

    // ------------------------------------------------------------ 바인딩

    public AdlibInstrument Current { get { return current; } }

    public void Bind(AdlibInstrument ins)
    {
        bool changed = !ReferenceEquals(current, ins);
        current = ins;
        if (changed)
        {
            showPair2 = false;
            if (ins != null)
            {
                int mode = ins.Percussive && ins.VoiceNum >= 6 && ins.VoiceNum <= 10 ? ins.VoiceNum : 0;
                if (mode != previewVoice) { ws.Audio.PreviewAllOff(); previewVoice = mode; ApplyLayout(); ApplyModeRange(); }
            }
        }
        LoadPair();
        UpdateHeaderText();
        binding = true;
        try
        {
            txtName.Text = ins != null ? ins.Name : "";
            foreach (var r in rows)
            {
                // 웨이브폼: 4-op(OPL3) 악기는 0~7, 나머지(OPL2)는 0~3
                var wave = r.Info.Id == OperatorParameter.WaveForm;
                int waveMax = ins != null && ins.Is4Op ? 7 : 3;
                if (wave && r.Carrier is RetroSlider) ((RetroSlider)r.Carrier).Maximum = waveMax;
                if (wave && r.Modulator is RetroSlider) ((RetroSlider)r.Modulator).Maximum = waveMax;
                SetControl(r.Carrier, ins, OpFor(true), r.Info);
                SetControl(r.Modulator, ins, OpFor(false), r.Info);
            }
        }
        finally { binding = false; }
        RefreshFileInfo();
        SyncList();
        UpdateOptionButtons();
        bool on = ins != null;
        txtName.Enabled = on;
        foreach (var b in modeButtons) b.Enabled = on;
        foreach (var r in rows) { if (r.Carrier != null) r.Carrier.Enabled = on; r.Modulator.Enabled = on; }
        ApplyPreview();
        if (ModeChanged != null) ModeChanged();
    }

    /// <summary>위쪽 파일 정보: 뱅크 파일 이름(고친 뒤 저장 안 했으면 *), 경로, 악기 수, 지금 악기 번호.</summary>
    public void RefreshFileInfo()
    {
        bool isNew = string.IsNullOrEmpty(ws.Bank.FilePath);
        lblBankFile.Text = (isNew ? "(new bank)" : Path.GetFileName(ws.Bank.FilePath)) + (ws.BankDirty ? " *" : "");
        lblBankPath.Text = isNew ? "저장하지 않은 새 뱅크" : Path.GetDirectoryName(ws.Bank.FilePath);
        tips.SetToolTip(lblBankPath, isNew ? "" : ws.Bank.FilePath);
        var used = UsedList();
        int idx = current != null ? used.IndexOf(current) : -1;
        lblInfo.Text = "악기 " + used.Count + "개" + (idx >= 0 ? "  ·  " + (idx + 1) + "번" : "");
        LayoutInfo();
    }

    private static void SetControl(Control c, AdlibInstrument ins, OplOperator op, ParameterInfo info)
    {
        if (c == null) return;
        int v = op != null ? op.Get(info.Id) : 0;
        var cb = c as CheckBox;
        if (cb != null) { cb.Checked = v != 0; return; }
        var sl = c as RetroSlider;
        if (sl != null) sl.SetValueSilently(v);
    }

    private List<AdlibInstrument> UsedList()
    {
        var l = new List<AdlibInstrument>();
        foreach (var i in ws.Bank.Instruments) if (i.IsUsed) l.Add(i);
        return l;
    }

    private void CommitName()
    {
        if (binding || current == null) return;
        string name = AdlibInstrument.NormalizeName(txtName.Text);
        var other = ws.Bank.Find(name);
        if (name.Length == 0 || (other != null && !ReferenceEquals(other, current))) { txtName.Text = current.Name; return; }
        if (name == current.Name) return;
        current.Name = name;
        ws.Bank.SortByName();
        Edited();
        RefreshFileInfo();
        SyncList();
    }

    private void Edited()
    {
        if (EditingPair2) current.SetPairOperators(pairOp3, pairOp4);
        ws.BankDirty = true;
        ApplyPreview();
        RefreshFileInfo();
        if (InstrumentEdited != null) InstrumentEdited();
    }

    /// <summary>현재 악기를 시음 보이스에 로드한다 (편집 즉시 소리에 반영).</summary>
    public void ApplyPreview()
    {
        ws.Audio.PreviewSetMode(PreviewVoice >= 6);
        ws.Audio.PreviewSetInstrument(PreviewVoice, current);
    }

    // ------------------------------------------------------------ 키보드 시음

    private void BuildKeyMap()
    {
        foreach (var kv in ComputerKeyboard.Map) keyMap[kv.Key] = kv.Value;
    }

    private static string KeyLabel(Keys k) { return ComputerKeyboard.Label(k); }

    private void RefreshKeyLabels()
    {
        keyboard.KeyLabels.Clear();
        foreach (var kv in keyMap) keyboard.KeyLabels[baseNote + kv.Value] = KeyLabel(kv.Key);
        keyboard.Invalidate();
        lblRange.Text = "from " + NoteHelper.Name(baseNote);
    }

    private void NotePressed(int note)
    {
        ApplyPreview();
        ws.Audio.PreviewNoteOn(PreviewVoice, note);
    }

    private void NoteReleased(int note) { ws.Audio.PreviewNoteOff(PreviewVoice); }

    public bool HandleKeyDown(Keys key)
    {
        if (key == Keys.PageUp || key == Keys.PageDown)
        {
            TransposeKeyboard(key == Keys.PageUp ? 1 : -1);
            return true;
        }
        int offset;
        if (!keyMap.TryGetValue(key, out offset)) return false;
        if (held.ContainsKey(key)) return true;
        int note = baseNote + offset;
        held[key] = note;
        keyboard.SetPressed(note, true);
        NotePressed(note);
        return true;
    }

    public bool HandleKeyUp(Keys key)
    {
        int note;
        if (!held.TryGetValue(key, out note)) return false;
        held.Remove(key);
        keyboard.SetPressed(note, false);
        NoteReleased(note);
        return true;
    }

    public bool IsEditingText { get { return txtName.Focused; } }

    /// <summary>F1 = Carrier, F2 = Modulator 열로 포커스 이동.</summary>
    public void FocusColumn(bool carrier)
    {
        if (SingleOperator) carrier = true;
        activeCarrier = carrier;
        UpdateHeaderHighlight();
        foreach (var r in rows)
        {
            var c = carrier ? r.Carrier : r.Modulator;
            if (c != null) { c.Focus(); return; }
        }
    }

    // ------------------------------------------------------------ 배치

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using (var b = new SolidBrush(InfoBack)) g.FillRectangle(b, 0, 0, Width, InfoHeight);
        using (var p = new Pen(Line))
        {
            g.DrawLine(p, 0, InfoHeight - 1, Width, InfoHeight - 1);
            g.DrawLine(p, 0, TopHeight - 1, Width, TopHeight - 1);
            g.DrawLine(p, ListWidth, TopHeight, ListWidth, Height - KeyboardHeight);
        }
    }

    private void LayoutInfo()
    {
        txtName.SetBounds(Width - 100, 4, 92, 22);
        lblName.SetBounds(txtName.Left - 44, 0, 40, InfoHeight - 1);
        int infoW = TextRenderer.MeasureText(lblInfo.Text, lblInfo.Font).Width + 24;
        lblInfo.SetBounds(lblName.Left - infoW - 6, 0, infoW, InfoHeight - 1);
        int pathX = lblBankFile.Right + 12;
        lblBankPath.SetBounds(pathX, 8, Math.Max(20, lblInfo.Left - pathX - 8), 15);   // 한 줄 (넘치면 …)
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutInfo();

        // 왼쪽 목록
        int listTop = TopHeight;
        int listBottom = Height - KeyboardHeight;
        listHeader.SetBounds(0, listTop + 1, ListWidth, 24);
        int btnY = listBottom - 30;
        list.SetBounds(8, listTop + 26, ListWidth - 16, Math.Max(40, btnY - listTop - 32));
        var btns = new[] { btnNew, btnCopy, btnRename, btnDelete };
        int[] bw = { 46, 48, 60, 56 };   // 글자 길이에 맞춘 너비 (합 210 + 간격 = 목록 너비)
        int bx = 8;
        for (int i = 0; i < btns.Length; i++) { btns[i].SetBounds(bx, btnY, bw[i], 24); bx += bw[i] + 2; }
        list.Columns[1].Width = Math.Max(60, list.ClientSize.Width - list.Columns[0].Width - list.Columns[2].Width - 2);

        // 오른쪽 파라미터
        int left = ListWidth + 16;
        int headerY = TopHeight + 6;
        int rowsTop = headerY + 26;
        int avail = Height - rowsTop - KeyboardHeight - 4;
        int rowH = Math.Max(14, Math.Min(30, avail / Math.Max(1, rows.Count)));
        sliderH = Math.Min(20, rowH - 2);
        int areaW = Width - left;
        int labelW = Math.Max(150, Math.Min(230, areaW * 30 / 100));
        int carrierX = left + labelW;
        int colW = Math.Max(110, (Width - carrierX - 50) / 2);
        int modX = carrierX + colW + 24;

        carrierHeader.SetBounds(carrierX, headerY, colW, 22);
        modulatorHeader.SetBounds(modX, headerY, colW, 22);

        for (int i = 0; i < rows.Count; i++)
        {
            int y = rowsTop + i * rowH;
            var r = rows[i];
            r.Label.SetBounds(left, y, labelW - 10, Math.Min(22, rowH));
            Place(r.Carrier, carrierX, y, colW);
            Place(r.Modulator, modX, y, colW);
        }
        keyboard.SetBounds(0, Height - KeyboardHeight, Width, KeyboardHeight);
        Invalidate();
    }

    /// <summary>모드에 맞춰 열을 보이고 숨긴다 (단일 오퍼레이터 = Carrier 열만).</summary>
    private void ApplyLayout()
    {
        bool single = SingleOperator;
        modulatorHeader.Visible = !single;
        foreach (var r in rows)
        {
            if (r.Modulator != null) r.Modulator.Visible = !single;
            // 모듈레이터 전용 행(Modulation Feedback)은 단일 오퍼레이터 모드에서 뜻이 없다
            if (r.Info.ModulatorOnly) r.Label.Visible = !single;
        }
        if (single && !activeCarrier) { activeCarrier = true; UpdateHeaderHighlight(); }
        UpdateOptionButtons();
        Invalidate();
    }

    private int sliderH = 20;

    private void Place(Control c, int x, int y, int w)
    {
        if (c == null) return;
        if (c is CheckBox) c.SetBounds(x + w / 2 - 8, y, 22, Math.Min(22, sliderH + 2));
        else c.SetBounds(x, y + 1, w, sliderH);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tips.Dispose();
        base.Dispose(disposing);
    }
}
