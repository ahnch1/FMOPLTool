using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AdlibComposer.App.Controls;
using AdlibComposer.Core;

namespace AdlibComposer.App.Views;

/// <summary>Instrument Maker 화면: Carrier(왼쪽)/Modulator(오른쪽) 두 열의 13개 파라미터 + 하단 건반.</summary>
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
    private readonly CheckBox chkAdditive = new CheckBox();
    private readonly Label lblInfo = new Label();
    private readonly PianoKeyboardControl keyboard = new PianoKeyboardControl();
    private readonly Dictionary<Keys, int> keyMap = new Dictionary<Keys, int>();
    private readonly Dictionary<Keys, int> held = new Dictionary<Keys, int>();
    private AdlibInstrument current;
    private bool binding;
    private bool activeCarrier = true;
    private int baseNote = Song.MiddleC;

    public event Action InstrumentEdited;

    /// <summary>시음에 쓰는 보이스 (0 = 멜로딕 1번, 6~10 = BD/SD/TO/CY/HH).</summary>
    public int PreviewVoice { get; set; }

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

        chkAdditive.Text = "Additive connection";
        chkAdditive.Font = Theme.UiFont;
        chkAdditive.SetBounds(264, 8, 180, 22);
        chkAdditive.CheckedChanged += delegate
        {
            if (binding || current == null) return;
            int con = chkAdditive.Checked ? 0 : 1;
            current.Modulator.Con = con; current.Carrier.Con = con;
            Edited();
        };

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
        Controls.AddRange(new Control[] { btnPrev, btnNext, txtName, btnList, chkAdditive, lblInfo, carrierHeader, modulatorHeader });
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
                current.GetOperator(carrier).Set(info.Id, cb.Checked ? 1 : 0);
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
                current.GetOperator(carrier).Set(info.Id, sl.Value);
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
        current = ins;
        binding = true;
        try
        {
            txtName.Text = ins != null ? ins.Name : "";
            chkAdditive.Checked = ins != null && ins.Additive;
            foreach (var r in rows)
            {
                SetControl(r.Carrier, ins, ins != null ? ins.Carrier : null, r.Info);
                SetControl(r.Modulator, ins, ins != null ? ins.Modulator : null, r.Info);
            }
            var used = UsedList();
            int idx = ins != null ? used.IndexOf(ins) : -1;
            string bankName = string.IsNullOrEmpty(ws.Bank.FilePath) ? "(new bank)" : System.IO.Path.GetFileName(ws.Bank.FilePath);
            lblInfo.Text = bankName + "  " + (idx >= 0 ? (idx + 1) + "/" + used.Count : "-");
        }
        finally { binding = false; }
        Enabled = ins != null;
        ApplyPreview();
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
        Keys[] low = { Keys.Z, Keys.S, Keys.X, Keys.D, Keys.C, Keys.V, Keys.G, Keys.B, Keys.H, Keys.N, Keys.J, Keys.M, Keys.Oemcomma };
        for (int i = 0; i < low.Length; i++) keyMap[low[i]] = i;
        Keys[] high = { Keys.Q, Keys.D2, Keys.W, Keys.D3, Keys.E, Keys.R, Keys.D5, Keys.T, Keys.D6, Keys.Y, Keys.D7, Keys.U, Keys.I };
        for (int i = 0; i < high.Length; i++) keyMap[high[i]] = 12 + i;
    }

    private static string KeyLabel(Keys k)
    {
        if (k == Keys.Oemcomma) return ",";
        if (k >= Keys.D0 && k <= Keys.D9) return ((int)k - (int)Keys.D0).ToString();
        return k.ToString();
    }

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
            baseNote = Math.Max(36, Math.Min(84, baseNote + (key == Keys.PageUp ? 12 : -12)));
            RefreshKeyLabels();
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

    private static void Place(Control c, int x, int y, int w)
    {
        if (c == null) return;
        if (c is CheckBox) c.SetBounds(x + w / 2 - 8, y, 22, 22);
        else c.SetBounds(x, y + 1, w, 20);
    }
}
