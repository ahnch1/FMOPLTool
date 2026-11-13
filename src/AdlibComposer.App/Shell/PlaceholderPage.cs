using System;
using System.Drawing;
using System.Windows.Forms;

namespace AdlibComposer.App.Shell;

/// <summary>
/// 셸에 들어가는 페이지가 구현하는 공통 인터페이스. 페이지가 보이거나 가려질 때 불린다
/// (예: Jukebox는 가려질 때 재생을 멈추고, Note는 보일 때 현재 곡으로 악보를 다시 만든다).
/// </summary>
internal interface IShellPage
{
    void OnShown();
    void OnHidden();
}

/// <summary>
/// 개발 예정 페이지의 기본 화면: 가운데 카드(아이콘 · 제목 · 설명 · 예정 기능 · 사용할 데이터).
/// 실제 기능을 만들 때는 이 클래스를 상속하지 않고 UserControl + IShellPage로 바꾸면 된다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal class PlaceholderPage : UserControl, IShellPage
{
    private readonly char glyph;
    private readonly string heading;
    private readonly string description;
    private readonly string[] planned;
    private readonly Func<string> dataInfo;

    public PlaceholderPage(char glyph, string heading, string description, string[] planned, Func<string> dataInfo)
    {
        this.glyph = glyph;
        this.heading = heading;
        this.description = description;
        this.planned = planned;
        this.dataInfo = dataInfo;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(246, 247, 249);
    }

    public virtual void OnShown() { Invalidate(); }
    public virtual void OnHidden() { }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int cardW = Math.Min(640, Width - 48);
        string info = dataInfo != null ? dataInfo() : null;
        // 높이 계산 (데이터 경로가 길면 여러 줄)
        int textW = cardW - (28 + 48 + 20) - 28;
        int infoH = string.IsNullOrEmpty(info) ? 0 : (int)Math.Ceiling(g.MeasureString(info, ShellTheme.Ui, textW).Height);
        int cardH = 120 + planned.Length * 24 + (infoH > 0 ? 36 + infoH : 0) + 28;
        var card = new Rectangle((Width - cardW) / 2, Math.Max(24, (Height - cardH) / 2 - 20), cardW, cardH);
        using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, card);
        using (var p = new Pen(ShellTheme.HeaderLine)) g.DrawRectangle(p, card);

        var icon = new Rectangle(card.X + 28, card.Y + 28, 48, 48);
        GlyphImages.DrawCircleIcon(g, icon, glyph, ShellTheme.Accent, Color.White);

        int x = icon.Right + 20, y = card.Y + 26, w = card.Right - x - 28;
        using (var dark = new SolidBrush(Color.FromArgb(32, 34, 38)))
        using (var muted = new SolidBrush(ShellTheme.Muted))
        using (var accent = new SolidBrush(ShellTheme.AccentDark))
        {
            g.DrawString(heading, ShellTheme.PageTitle, dark, x, y);
            var badge = new Rectangle(x + (int)g.MeasureString(heading, ShellTheme.PageTitle).Width + 6, y + 6, 76, 18);
            using (var bb = new SolidBrush(Color.FromArgb(230, 245, 243))) g.FillRectangle(bb, badge);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("개발 예정", ShellTheme.Ui, accent, badge, sf);
            y += 30;
            g.DrawString(description, ShellTheme.Ui, muted, new RectangleF(x, y, w, 40));
            y += 50;

            g.DrawString("예정 기능", ShellTheme.UiSemibold, dark, x, y);
            y += 24;
            foreach (var p in planned)
            {
                g.FillEllipse(accent, x + 2, y + 7, 5, 5);
                g.DrawString(p, ShellTheme.Ui, dark, new RectangleF(x + 14, y, w - 14, 22));
                y += 24;
            }
            if (!string.IsNullOrEmpty(info))
            {
                y += 10;
                g.DrawString("사용할 데이터", ShellTheme.UiSemibold, dark, x, y);
                g.DrawString(info, ShellTheme.Ui, muted, new RectangleF(x, y + 22, w, infoH + 4));
            }
        }
    }
}
