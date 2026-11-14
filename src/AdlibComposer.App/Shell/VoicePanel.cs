using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AdlibComposer.App.Shell;

/// <summary>
/// 페이지 오른쪽 보이스 목록 (Note · Orgel Note): 제목(보이는 수 / 전체) · 모두 켜기 / 모두 끄기 · 보이스 체크박스 · (선택) 아래 추가 체크박스.
/// 체크를 바꾸면 VoiceToggled, 버튼은 AllToggled. 목록은 SetVoices로 채운다 (채우는 동안은 이벤트 없음).
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class VoicePanel : Panel
{
    private sealed class Entry
    {
        public int Voice;
        public string Text;
        public override string ToString() { return Text; }
    }

    private readonly Label title = new Label();
    private readonly CheckedListBox list = new CheckedListBox();
    private readonly string caption;
    private bool filling;

    /// <summary>보이스 하나를 켰다 / 껐다.</summary>
    public event Action<int, bool> VoiceToggled;
    /// <summary>모두 켜기(true) / 모두 끄기(false).</summary>
    public event Action<bool> AllToggled;

    /// <summary>목록 아래 추가 체크박스 (예: 드럼도 넣기). 기본은 숨김.</summary>
    public readonly CheckBox Extra = new CheckBox();

    public VoicePanel(string caption)
    {
        this.caption = caption;
        Dock = DockStyle.Right;
        Width = 210;
        BackColor = Color.White;
        Padding = new Padding(1, 0, 0, 0);   // 왼쪽 1px = 경계선

        title.Dock = DockStyle.Top;
        title.Height = 26;
        title.Font = ShellTheme.Section;
        title.ForeColor = ShellTheme.Muted;
        title.BackColor = Color.FromArgb(246, 247, 249);
        title.Padding = new Padding(8, 7, 0, 0);
        title.Text = caption;

        var buttons = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = 2, RowCount = 1, Padding = new Padding(6, 5, 6, 3), BackColor = Color.White };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.Controls.Add(MakeButton("모두 켜기", true), 0, 0);
        buttons.Controls.Add(MakeButton("모두 끄기", false), 1, 0);

        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.CheckOnClick = true;
        list.IntegralHeight = false;
        list.Font = ShellTheme.Ui;
        list.ItemCheck += delegate (object o, ItemCheckEventArgs e)
        {
            if (filling || VoiceToggled == null) return;
            int voice = ((Entry)list.Items[e.Index]).Voice;
            bool on = e.NewValue == CheckState.Checked;
            BeginInvoke((Action)(() => VoiceToggled(voice, on)));   // ItemCheck는 체크가 바뀌기 전에 온다
        };

        Extra.Dock = DockStyle.Bottom;
        Extra.Height = 30;
        Extra.Font = ShellTheme.Ui;
        Extra.Padding = new Padding(8, 0, 0, 0);
        Extra.Visible = false;

        Controls.Add(list);
        Controls.Add(Extra);
        Controls.Add(buttons);
        Controls.Add(title);
    }

    private Button MakeButton(string text, bool on)
    {
        var b = new Button { Text = text, Dock = DockStyle.Fill, Margin = new Padding(2, 0, 2, 0), FlatStyle = FlatStyle.System, Font = ShellTheme.Ui };
        b.Click += delegate { if (AllToggled != null) AllToggled(on); };
        return b;
    }

    /// <summary>목록의 보이스 번호 (보이는 순서).</summary>
    public List<int> Voices
    {
        get
        {
            var l = new List<int>();
            foreach (Entry e in list.Items) l.Add(e.Voice);
            return l;
        }
    }

    /// <summary>목록을 다시 채운다: (보이스, 이름, 켜짐).</summary>
    public void SetVoices(IEnumerable<(int Voice, string Text, bool On)> voices)
    {
        filling = true;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var v in voices) list.Items.Add(new Entry { Voice = v.Voice, Text = v.Text }, v.On);
        list.EndUpdate();
        filling = false;
        UpdateTitle();
    }

    /// <summary>제목: "보이스  켜진 수 / 전체".</summary>
    public void UpdateTitle()
    {
        title.Text = caption + "  " + list.CheckedItems.Count + " / " + list.Items.Count;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var pen = new Pen(Color.FromArgb(225, 228, 232))) e.Graphics.DrawLine(pen, 0, 0, 0, Height);
    }
}

/// <summary>툴바의 공통 재생 · 현재 위치부터 재생 · 정지 버튼을 쓰는 페이지 (Note · Orgel Note · Transcribe).</summary>
internal interface IPlaybackPage
{
    /// <summary>재생할 것이 있는가 (버튼 켜기/끄기).</summary>
    bool CanPlay { get; }
    bool IsPlaying { get; }
    void PlayFromStart();
    void PlayFromCursor();
    void StopPlay();
}
