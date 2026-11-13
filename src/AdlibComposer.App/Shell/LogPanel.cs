using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace AdlibComposer.App.Shell;

/// <summary>
/// 화면 아래 "진행 로그" 패널: 머리줄(필터 · 상세 · 복사 · 지우기 · 경고/오류 수) + 어두운 콘솔.
/// AppLog에 쌓인 기록을 보여 준다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class LogPanel : UserControl
{
    private readonly Panel bar = new Panel();
    private readonly Label title = new Label();
    private readonly ComboBox filter = new ComboBox();
    private readonly CheckBox detail = new CheckBox();
    private readonly Button copy = new Button();
    private readonly Button clear = new Button();
    private readonly Label counts = new Label();
    private readonly RichTextBox console = new RichTextBox();

    public LogPanel()
    {
        BackColor = ShellTheme.LogBack;
        Height = 150;

        bar.Dock = DockStyle.Top;
        bar.Height = 30;
        bar.BackColor = ShellTheme.LogBar;

        title.Text = "진행 로그";
        title.Font = ShellTheme.UiSemibold;
        title.ForeColor = ShellTheme.SideText;
        title.AutoSize = false;
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.SetBounds(10, 0, 72, 30);

        filter.DropDownStyle = ComboBoxStyle.DropDownList;
        filter.Items.AddRange(new object[] { "전체", "정보", "경고", "오류" });
        filter.SelectedIndex = 0;
        filter.Font = ShellTheme.Ui;
        filter.SetBounds(86, 4, 100, 22);
        filter.SelectedIndexChanged += delegate { Rebuild(); };

        detail.Text = "상세";
        detail.ForeColor = ShellTheme.SideText;
        detail.Font = ShellTheme.Ui;
        detail.SetBounds(196, 5, 60, 20);
        detail.CheckedChanged += delegate { Rebuild(); };

        StyleBarButton(copy, "복사", 262);
        StyleBarButton(clear, "지우기", 336);
        copy.Click += delegate { CopyVisible(); };
        clear.Click += delegate { AppLog.Clear(); };

        counts.ForeColor = ShellTheme.SideMuted;
        counts.Font = ShellTheme.Ui;
        counts.AutoSize = false;
        counts.TextAlign = ContentAlignment.MiddleLeft;
        counts.SetBounds(418, 0, 200, 30);

        bar.Controls.AddRange(new Control[] { title, filter, detail, copy, clear, counts });

        console.Dock = DockStyle.Fill;
        console.ReadOnly = true;
        console.BorderStyle = BorderStyle.None;
        console.BackColor = ShellTheme.LogBack;
        console.ForeColor = ShellTheme.LogText;
        console.Font = ShellTheme.Console;
        console.WordWrap = false;
        console.DetectUrls = false;
        console.TabStop = false;

        Controls.Add(console);
        Controls.Add(bar);

        AppLog.Added += OnAdded;
        AppLog.Cleared += Rebuild;
        Rebuild();
    }

    private static void StyleBarButton(Button b, string text, int x)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = ShellTheme.SideLine;
        b.FlatAppearance.MouseOverBackColor = ShellTheme.SideSelected;
        b.BackColor = ShellTheme.SideHover;
        b.ForeColor = ShellTheme.SideText;
        b.Font = ShellTheme.Ui;
        b.SetBounds(x, 4, 68, 22);
        b.TabStop = false;
    }

    private bool IsShown(LogEntry e)
    {
        switch (filter.SelectedIndex)
        {
            case 1: return e.Level == LogLevel.Info;
            case 2: return e.Level == LogLevel.Warning;
            case 3: return e.Level == LogLevel.Error;
        }
        return true;
    }

    private void OnAdded(LogEntry e)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action<LogEntry>(OnAdded), e); return; }
        if (IsShown(e)) Append(e);
        UpdateCounts();
    }

    private void Rebuild()
    {
        if (IsDisposed) return;
        console.Clear();
        foreach (var e in AppLog.Entries) if (IsShown(e)) Append(e);
        UpdateCounts();
    }

    private void Append(LogEntry e)
    {
        AppendColored(e.Time.ToString("HH:mm:ss") + " ", ShellTheme.LogTime);
        Color cat = e.Level == LogLevel.Error ? ShellTheme.LogError : e.Level == LogLevel.Warning ? ShellTheme.LogWarn : ShellTheme.LogInfo;
        AppendColored("[" + e.Category + "] ", cat);
        AppendColored(e.Message + "\n", e.Level == LogLevel.Info ? ShellTheme.LogText : cat);
        if (detail.Checked && !string.IsNullOrEmpty(e.Detail))
            AppendColored("         " + e.Detail.Replace("\n", "\n         ") + "\n", ShellTheme.LogTime);
        console.SelectionStart = console.TextLength;
        console.ScrollToCaret();
    }

    private void AppendColored(string text, Color color)
    {
        console.SelectionStart = console.TextLength;
        console.SelectionLength = 0;
        console.SelectionColor = color;
        console.AppendText(text);
    }

    private void UpdateCounts()
    {
        counts.Text = "경고 " + AppLog.Count(LogLevel.Warning) + " · 오류 " + AppLog.Count(LogLevel.Error);
        counts.ForeColor = AppLog.Count(LogLevel.Error) > 0 ? ShellTheme.LogError : AppLog.Count(LogLevel.Warning) > 0 ? ShellTheme.LogWarn : ShellTheme.SideMuted;
    }

    private void CopyVisible()
    {
        var sb = new StringBuilder();
        foreach (var e in AppLog.Entries)
        {
            if (!IsShown(e)) continue;
            sb.Append(e.Time.ToString("HH:mm:ss")).Append(" [").Append(e.Category).Append("] ").AppendLine(e.Message);
            if (detail.Checked && !string.IsNullOrEmpty(e.Detail)) sb.AppendLine(e.Detail);
        }
        if (sb.Length > 0) Clipboard.SetText(sb.ToString());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { AppLog.Added -= OnAdded; AppLog.Cleared -= Rebuild; }
        base.Dispose(disposing);
    }
}
