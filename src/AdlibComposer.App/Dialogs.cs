using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AdlibComposer.Core;

namespace AdlibComposer.App;

internal static class DialogUtil
{
    public static void Setup(Form f, string title, int w, int h)
    {
        f.Text = title;
        f.ClientSize = new Size(w, h);
        f.StartPosition = FormStartPosition.CenterParent;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = false;
        f.MinimizeBox = false;
        f.ShowInTaskbar = false;
        f.Font = new Font("Arial", 9f);
    }

    public static Button MakeButton(string text, int x, int y, int w, DialogResult result)
    {
        var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = 26, DialogResult = result };
        return b;
    }
}

/// <summary>한 줄 입력 대화상자.</summary>
internal static class Prompt
{
    public static string Show(IWin32Window owner, string title, string label, string initial)
    {
        using (var f = new Form())
        {
            DialogUtil.Setup(f, title, 340, 110);
            var lb = new Label { Text = label, Left = 12, Top = 12, Width = 316, Height = 18 };
            var tb = new TextBox { Left = 12, Top = 34, Width = 316, Text = initial ?? "" };
            var ok = DialogUtil.MakeButton("OK", 172, 70, 76, DialogResult.OK);
            var cancel = DialogUtil.MakeButton("Cancel", 252, 70, 76, DialogResult.Cancel);
            f.Controls.AddRange(new Control[] { lb, tb, ok, cancel });
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            tb.SelectAll();
            return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
        }
    }
}

/// <summary>뱅크 안의 악기 고르기/추가/복제/이름 변경/삭제.</summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class InstrumentListForm : Form
{
    private readonly InstrumentBank bank;
    private readonly ListBox list = new ListBox();

    public AdlibInstrument Selected { get; private set; }
    public bool Modified { get; private set; }

    public InstrumentListForm(InstrumentBank bank, AdlibInstrument current)
    {
        this.bank = bank;
        DialogUtil.Setup(this, "Instruments - " + (string.IsNullOrEmpty(bank.FilePath) ? "(new bank)" : System.IO.Path.GetFileName(bank.FilePath)), 372, 430);

        list.Left = 10; list.Top = 10; list.Width = 352; list.Height = 340;
        list.DisplayMember = "Name";
        list.Font = Theme.UiFont;
        list.DoubleClick += delegate { Choose(); };
        Controls.Add(list);

        var select = DialogUtil.MakeButton("Select", 10, 360, 80, DialogResult.None);
        select.Click += delegate { Choose(); };
        var add = DialogUtil.MakeButton("New", 96, 360, 60, DialogResult.None);
        add.Click += delegate { AddNew(); };
        var dup = DialogUtil.MakeButton("Copy", 160, 360, 60, DialogResult.None);
        dup.Click += delegate { Duplicate(); };
        var ren = DialogUtil.MakeButton("Rename", 224, 360, 70, DialogResult.None);
        ren.Click += delegate { Rename(); };
        var del = DialogUtil.MakeButton("Delete", 298, 360, 64, DialogResult.None);
        del.Click += delegate { Delete(); };
        var close = DialogUtil.MakeButton("Close", 282, 394, 80, DialogResult.Cancel);
        Controls.AddRange(new Control[] { select, add, dup, ren, del, close });
        CancelButton = close;

        Refill(current);
    }

    private void Refill(AdlibInstrument pick)
    {
        list.Items.Clear();
        foreach (var i in bank.Instruments) if (i.IsUsed) list.Items.Add(i);
        if (pick != null && list.Items.Contains(pick)) list.SelectedItem = pick;
        else if (list.Items.Count > 0) list.SelectedIndex = 0;
    }

    private AdlibInstrument Current { get { return list.SelectedItem as AdlibInstrument; } }

    private void Choose()
    {
        if (Current == null) return;
        Selected = Current;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void AddNew()
    {
        string name = Prompt.Show(this, "New instrument", "Name (max 8 chars):", "NEW");
        if (name == null) return;
        var ins = AdlibInstrument.CreateDefault(name);
        bank.Add(ins);
        Modified = true;
        Refill(ins);
    }

    private void Duplicate()
    {
        if (Current == null) return;
        var c = Current.Clone();
        c.Name = bank.UniqueName(c.Name);
        bank.Add(c);
        Modified = true;
        Refill(c);
    }

    private void Rename()
    {
        var cur = Current;
        if (cur == null) return;
        string name = Prompt.Show(this, "Rename", "Name (max 8 chars):", cur.Name);
        if (name == null) return;
        name = AdlibInstrument.NormalizeName(name);
        if (name.Length == 0) return;
        var other = bank.Find(name);
        if (other != null && !ReferenceEquals(other, cur)) { MessageBox.Show(this, "같은 이름의 악기가 이미 있습니다."); return; }
        cur.Name = name;
        bank.SortByName();
        Modified = true;
        Refill(cur);
    }

    private void Delete()
    {
        var cur = Current;
        if (cur == null) return;
        if (MessageBox.Show(this, "'" + cur.Name + "' 악기를 삭제할까요?", "Delete", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        bank.Remove(cur);
        Modified = true;
        Refill(null);
    }
}

/// <summary>틱/값 목록 편집 (템포 배수, 볼륨, 피치 이벤트).</summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class EventEditorForm : Form
{
    private readonly DataGridView grid = new DataGridView();
    private readonly double min, max;

    public List<KeyValuePair<int, double>> Result { get; private set; }

    public EventEditorForm(string title, string valueHeader, IEnumerable<KeyValuePair<int, double>> rows, double min, double max)
    {
        this.min = min; this.max = max;
        DialogUtil.Setup(this, title, 330, 400);
        grid.Left = 10; grid.Top = 10; grid.Width = 310; grid.Height = 340;
        grid.AllowUserToAddRows = true;
        grid.AllowUserToDeleteRows = true;
        grid.RowHeadersVisible = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns.Add("tick", "Tick");
        grid.Columns.Add("value", valueHeader + " (" + min.ToString(CultureInfo.InvariantCulture) + "~" + max.ToString(CultureInfo.InvariantCulture) + ")");
        foreach (var r in rows) grid.Rows.Add(r.Key.ToString(), r.Value.ToString("0.###", CultureInfo.InvariantCulture));
        Controls.Add(grid);

        var ok = DialogUtil.MakeButton("OK", 154, 362, 76, DialogResult.None);
        ok.Click += delegate { Commit(); };
        var cancel = DialogUtil.MakeButton("Cancel", 234, 362, 76, DialogResult.Cancel);
        Controls.AddRange(new Control[] { ok, cancel });
        CancelButton = cancel;
    }

    private static bool ParseNumber(object cell, out double v)
    {
        string s = Convert.ToString(cell) ?? "";
        s = s.Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            || double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v);
    }

    private void Commit()
    {
        var list = new List<KeyValuePair<int, double>>();
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow) continue;
            double t, v;
            if (!ParseNumber(row.Cells[0].Value, out t) || !ParseNumber(row.Cells[1].Value, out v) || t < 0 || t > 65535)
            { MessageBox.Show(this, "틱(0~65535)과 값을 숫자로 입력하세요."); return; }
            if (v < min || v > max) { MessageBox.Show(this, "값은 " + min + " ~ " + max + " 범위여야 합니다."); return; }
            list.Add(new KeyValuePair<int, double>((int)t, v));
        }
        list.Sort((a, b) => a.Key.CompareTo(b.Key));
        Result = list;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>곡 설정: 틱/박, 박/마디, 템포, 멜로딕/리듬 모드.</summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class SongSettingsForm : Form
{
    private readonly NumericUpDown tickBeat = new NumericUpDown();
    private readonly NumericUpDown beats = new NumericUpDown();
    private readonly NumericUpDown tempo = new NumericUpDown();
    private readonly CheckBox rhythm = new CheckBox();

    public int TickBeat { get { return (int)tickBeat.Value; } }
    public int BeatsPerMeasure { get { return (int)beats.Value; } }
    public float Tempo { get { return (float)tempo.Value; } }
    public bool Melodic { get { return !rhythm.Checked; } }

    public SongSettingsForm(Song song)
    {
        DialogUtil.Setup(this, "Song settings", 300, 210);
        AddRow("Ticks per beat", tickBeat, 1, 960, 0, song.TickBeat, 14);
        AddRow("Beats per measure", beats, 1, 32, 0, song.BeatsPerMeasure, 44);
        AddRow("Tempo (BPM)", tempo, 20, 400, 1, (decimal)song.BasicTempo, 74);
        rhythm.Text = "Rhythm mode (6 melodic + BD/SD/TOM/CYM/HH)";
        rhythm.Left = 14; rhythm.Top = 108; rhythm.Width = 272;
        rhythm.Checked = !song.IsMelodic;
        Controls.Add(rhythm);
        var ok = DialogUtil.MakeButton("OK", 130, 168, 76, DialogResult.OK);
        var cancel = DialogUtil.MakeButton("Cancel", 210, 168, 76, DialogResult.Cancel);
        Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok; CancelButton = cancel;
    }

    private void AddRow(string label, NumericUpDown n, int min, int max, int decimals, decimal value, int top)
    {
        Controls.Add(new Label { Text = label, Left = 14, Top = top + 3, Width = 150 });
        n.Left = 180; n.Top = top; n.Width = 100;
        n.Minimum = min; n.Maximum = max; n.DecimalPlaces = decimals;
        n.Value = Math.Max(min, Math.Min(max, value));
        Controls.Add(n);
    }
}
