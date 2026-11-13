using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AdlibComposer.App.Shell;

/// <summary>프로그램의 페이지 (사이드바 메뉴 하나 = 페이지 하나).</summary>
internal enum AppPage { Composer, Instrument, Jukebox, Note, Orgel, Tracker, Organizer }

/// <summary>
/// 왼쪽 어두운 사이드바: 프로그램 이름/버전, 구역 머리글(CREATE / PLAY / SCORE), 둥근 아이콘 메뉴.
/// 메뉴를 누르면 PageSelected가 불린다. 개발 중인 페이지는 "dev" 표시.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class NavSidebar : Control
{
    private sealed class NavItem
    {
        public AppPage Page;
        public string Text;
        public string Hint;
        public char Glyph;
        public bool InDevelopment;
        public Rectangle Bounds;
    }

    private sealed class NavSection
    {
        public string Title;
        public int Y;
    }

    private const int HeaderHeight = 78;
    private const int ItemHeight = 40;
    private const int SectionHeight = 30;

    private readonly List<NavItem> items = new List<NavItem>();
    private readonly List<NavSection> sections = new List<NavSection>();
    private readonly ToolTip tip = new ToolTip();
    private NavItem hover;
    private AppPage selected = AppPage.Composer;

    public event Action<AppPage> PageSelected;

    public string AppName { get; set; } = "FMOPL Tool";
    public string Version { get; set; } = "";
    public string Subtitle { get; set; } = "AdLib Composer";

    public NavSidebar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = ShellTheme.SideBack;
        Width = 212;
        Cursor = Cursors.Default;

        int y = HeaderHeight;
        y = AddSection("CREATE", y);
        y = AddItem(AppPage.Composer, "Composer", "건반 악보 작곡 · 재생 (ROL / IMS)", Glyphs.Composer, false, y);
        y = AddItem(AppPage.Instrument, "Instrument Maker", "FM 악기 편집 · 시음 (BNK / INS)", Glyphs.Instrument, false, y);
        y = AddItem(AppPage.Tracker, "Tracker", "트래커 곡 만들기 (S3M / STM / MOD / NST)", Glyphs.Tracker, false, y);
        y = AddSection("PLAY", y + 6);
        y = AddItem(AppPage.Jukebox, "Jukebox", "폴더의 곡 재생 (ROL · IMS · SOP · S3M · STM · MOD) · 가사 · 채널 믹서", Glyphs.Jukebox, false, y);
        y = AddSection("SCORE", y + 6);
        y = AddItem(AppPage.Note, "Note", "오선 악보 보기 · MusicXML / MIDI / PNG · 인쇄", Glyphs.Note, false, y);
        y = AddItem(AppPage.Orgel, "Orgel Note", "30음 오르골 띠 구멍 위치 · 시험 연주 · 실제 크기 인쇄", Glyphs.Orgel, false, y);
        y = AddSection("TOOLS", y + 6);
        AddItem(AppPage.Organizer, "Organizer", "같은 곡 찾기 · 정리 (ROL / IMS / SOP): 삭제 · 이동 · 시뮬레이션", Glyphs.Organizer, false, y);
    }

    private int AddSection(string title, int y)
    {
        sections.Add(new NavSection { Title = title, Y = y });
        return y + SectionHeight;
    }

    private int AddItem(AppPage page, string text, string hint, char glyph, bool dev, int y)
    {
        items.Add(new NavItem { Page = page, Text = text, Hint = hint, Glyph = glyph, InDevelopment = dev, Bounds = new Rectangle(8, y, 0, ItemHeight - 4) });
        return y + ItemHeight;
    }

    public AppPage Selected
    {
        get { return selected; }
        set { selected = value; Invalidate(); }
    }

    public string HintFor(AppPage page)
    {
        foreach (var it in items) if (it.Page == page) return it.Hint;
        return "";
    }

    // ---------------------------------------------------------------- 그리기

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 프로그램 이름 / 버전 / 부제
        using (var white = new SolidBrush(ShellTheme.SideText))
        using (var muted = new SolidBrush(ShellTheme.SideMuted))
        {
            g.DrawString(AppName, ShellTheme.AppTitle, white, 18, 16);
            var nameSize = g.MeasureString(AppName, ShellTheme.AppTitle);
            if (!string.IsNullOrEmpty(Version)) g.DrawString(Version, ShellTheme.Section, muted, 18 + nameSize.Width - 2, 20);
            g.DrawString(Subtitle, ShellTheme.Ui, muted, 19, 46);
        }

        // 구역 머리글 + 구분선
        using (var muted = new SolidBrush(ShellTheme.SideMuted))
        using (var line = new Pen(ShellTheme.SideLine))
            foreach (var s in sections)
            {
                g.DrawString(s.Title, ShellTheme.Section, muted, 16, s.Y + 10);
                var w = g.MeasureString(s.Title, ShellTheme.Section).Width;
                g.DrawLine(line, 20 + w, s.Y + 17, Width - 14, s.Y + 17);
            }

        // 메뉴
        foreach (var it in items)
        {
            var r = new Rectangle(8, it.Bounds.Y, Width - 16, it.Bounds.Height);
            bool sel = it.Page == selected;
            if (sel || it == hover)
                using (var b = new SolidBrush(sel ? ShellTheme.SideSelected : ShellTheme.SideHover)) g.FillRectangle(b, r);
            if (sel)
                using (var b = new SolidBrush(ShellTheme.Accent)) g.FillRectangle(b, r.X, r.Y + 4, 3, r.Height - 8);

            var icon = new Rectangle(r.X + 12, r.Y + (r.Height - 26) / 2, 26, 26);
            GlyphImages.DrawCircleIcon(g, icon, it.Glyph, sel ? ShellTheme.Accent : ShellTheme.AccentDark, Color.White);

            using (var tb = new SolidBrush(sel ? Color.White : ShellTheme.SideText))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
                {
                float textW = r.Right - (icon.Right + 12) - (it.InDevelopment ? 48 : 6);
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(it.Text, ShellTheme.Nav, tb, new RectangleF(icon.Right + 12, r.Y, textW, r.Height), sf);
            }

            if (it.InDevelopment)
            {
                var badge = new Rectangle(r.Right - 40, r.Y + (r.Height - 16) / 2, 32, 16);
                using (var bb = new SolidBrush(ShellTheme.SideLine)) g.FillRectangle(bb, badge);
                using (var bt = new SolidBrush(ShellTheme.SideMuted))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("dev", ShellTheme.Section, bt, badge, sf);
            }
        }

        using (var line = new Pen(ShellTheme.SideLine)) g.DrawLine(line, Width - 1, 0, Width - 1, Height);
    }

    // ---------------------------------------------------------------- 마우스

    private NavItem HitTest(Point p)
    {
        foreach (var it in items)
            if (p.Y >= it.Bounds.Y && p.Y < it.Bounds.Y + it.Bounds.Height && p.X >= 8 && p.X < Width - 8) return it;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitTest(e.Location);
        if (h == hover) return;
        hover = h;
        Cursor = h != null ? Cursors.Hand : Cursors.Default;
        tip.SetToolTip(this, h != null ? h.Hint : null);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover == null) return;
        hover = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var h = HitTest(e.Location);
        if (h == null || e.Button != MouseButtons.Left) return;
        if (PageSelected != null) PageSelected(h.Page);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tip.Dispose();
        base.Dispose(disposing);
    }
}
