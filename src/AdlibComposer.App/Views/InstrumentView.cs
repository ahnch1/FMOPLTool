using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AdlibComposer.App.Controls;
using AdlibComposer.Core;

namespace AdlibComposer.App.Views;

/// <summary>
/// Instrument Maker 화면: Carrier(왼쪽)/Modulator(오른쪽) 두 열의 13개 파라미터 + 하단 건반.
/// 스네어/탐탐/심벌/하이햇 모드는 단일 오퍼레이터라 Carrier 열 하나만 보인다
/// (값은 BNK의 모듈레이터 필드에 저장됨 — 실제 STANDARD.BNK snare1/tom1/cymbal1/hihat1로 확인).
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class InstrumentView : UserControl
{
    private const int HeaderHeight = 36;
    private const int RowsTop = 66;
    private const int KeyboardHeight = 84;

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
    private readonly TextBox txtName = new TextBox();
    private readonly Button btnPrev = new Button();
    private readonly Button btnNext = new Button();
    private readonly Button btnList = new Button();
    private readonly Label lblInfo = new Label();
    private readonly PianoKeyboardControl keyboard = new PianoKeyboardControl();
    private readonly Dictionary<Keys, int> keyMap = new Dictionary<Keys, int>();
    private readonly Dictionary<Keys, int> held = new Dictionary<Keys, int>();
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
        return current.GetOperator(carrierColumn);
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
        get { return current != null && current.Additive; }
        set
        {
            if (current == null || current.Additive == value) return;
            int con = value ? 0 : 1;
            current.Modulator.Con = con; current.Carrier.Con = con;
            Edited();
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
        BuildHeader();
        BuildRows();

        keyboard.NoteDown += NotePressed;
        keyboard.NoteUp += NoteReleased;
        Controls.Add(keyboard);
        RefreshKeyLabels();

        ws.CurrentInstrumentChanged += delegate { Bind(ws.CurrentInstrument); };
        ws.BankReplaced += delegate { Bind(ws.CurrentInstrument); };
        Bind(ws.CurrentInstrument);
    }

    // ------------------------------------------------------------ 구성

    private void BuildHeader()
    {
        btnPrev.Text = "<"; btnNext.Text = ">"; btnList.Text = "List...";
        btnPrev.SetBounds(6, 6, 28, 24); btnNext.SetBounds(36, 6, 28, 24); btnList.SetBounds(194, 6, 60, 24);
        foreach (var b in new[] { btnPrev, btnNext, btnList }) { b.FlatStyle = FlatStyle.Flat; b.Font = Theme.UiFont; b.BackColor = Color.White; }
        btnPrev.Click += delegate { Step(-1); };
        btnNext.Click += delegate { Step(1); };
        btnList.Click += delegate { OpenList(); };

        txtName.SetBounds(72, 7, 116, 22);
        txtName.MaxLength = AdlibInstrument.MaxNameLength;
        txtName.Font = Theme.UiFont;
        txtName.Leave += delegate { CommitName(); };
        txtName.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { CommitName(); e.SuppressKeyPress = true; } };

        lblInfo.Font = Theme.UiFont;
        lblInfo.TextAlign = ContentAlignment.MiddleRight;
        lblInfo.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        lblInfo.SetBounds(450, 8, 200, 22);

        carrierHeader.Text = "F1  Carrier";
        modulatorHeader.Text = "F2  Modulator";
        foreach (var h in new[] { carrierHeader, modulatorHeader })
        {
            h.Font = Theme.UiFont; h.TextAlign = ContentAlignment.MiddleCenter; h.Height = 22; h.AutoSize = false;
        }
        Controls.AddRange(new Control[] { btnPrev, btnNext, txtName, btnList, lblInfo, carrierHeader, modulatorHeader });
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
        if (changed && ins != null)
        {
            int mode = ins.Percussive && ins.VoiceNum >= 6 && ins.VoiceNum <= 10 ? ins.VoiceNum : 0;
            if (mode != previewVoice) { ws.Audio.PreviewAllOff(); previewVoice = mode; ApplyLayout(); ApplyModeRange(); }
        }
        binding = true;
        try
        {
            txtName.Text = ins != null ? ins.Name : "";
            foreach (var r in rows)
            {
                SetControl(r.Carrier, ins, OpFor(true), r.Info);
                SetControl(r.Modulator, ins, OpFor(false), r.Info);
            }
            var used = UsedList();
            int idx = ins != null ? used.IndexOf(ins) : -1;
            string bankName = string.IsNullOrEmpty(ws.Bank.FilePath) ? "(new bank)" : System.IO.Path.GetFileName(ws.Bank.FilePath);
            lblInfo.Text = bankName + "  " + (idx >= 0 ? (idx + 1) + "/" + used.Count : "-");
        }
        finally { binding = false; }
        Enabled = ins != null;
        ApplyPreview();
        if (ModeChanged != null) ModeChanged();
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

    private void Step(int dir)
    {
        var used = UsedList();
        if (used.Count == 0) return;
        int idx = used.IndexOf(current);
        idx = idx < 0 ? 0 : (idx + dir + used.Count) % used.Count;
        ws.SelectInstrument(used[idx]);
    }

    private void OpenList()
    {
        using (var f = new InstrumentListForm(ws.Bank, current))
        {
            var res = f.ShowDialog(FindForm());
            if (f.Modified) ws.BankDirty = true;
            AdlibInstrument pick = res == DialogResult.OK ? f.Selected : (ws.Bank.Instruments.Contains(current) ? current : null);
            if (pick == null) { var u = UsedList(); pick = u.Count > 0 ? u[0] : null; }
            ws.SelectInstrument(pick);
            if (InstrumentEdited != null) InstrumentEdited();
        }
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
    }

    private void Edited()
    {
        ws.BankDirty = true;
        ApplyPreview();
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

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int avail = Height - RowsTop - KeyboardHeight - 6;
        int rowH = Math.Max(20, Math.Min(30, avail / Math.Max(1, rows.Count)));
        int carrierX = Math.Max(230, Width * 32 / 100);
        int colW = Math.Max(120, (Width - carrierX - 60) / 2);
        int modX = carrierX + colW + 30;

        carrierHeader.SetBounds(carrierX, HeaderHeight + 4, colW, 22);
        modulatorHeader.SetBounds(modX, HeaderHeight + 4, colW, 22);
        lblInfo.Left = Math.Max(450, Width - lblInfo.Width - 8);

        for (int i = 0; i < rows.Count; i++)
        {
            int y = RowsTop + i * rowH;
            var r = rows[i];
            r.Label.SetBounds(20, y, carrierX - 30, 22);
            Place(r.Carrier, carrierX, y, colW);
            Place(r.Modulator, modX, y, colW);
        }
        keyboard.SetBounds(0, Height - KeyboardHeight, Width, KeyboardHeight);
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
        Invalidate();
    }

    private static void Place(Control c, int x, int y, int w)
    {
        if (c == null) return;
        if (c is CheckBox) c.SetBounds(x + w / 2 - 8, y, 22, 22);
        else c.SetBounds(x, y + 1, w, 20);
    }
}
