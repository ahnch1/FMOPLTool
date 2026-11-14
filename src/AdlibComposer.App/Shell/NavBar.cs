using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AdlibComposer.App.Shell;

/// <summary>프로그램의 페이지 (메뉴 하나 = 페이지 하나).</summary>
internal enum AppPage { Composer, Instrument, Jukebox, Note, Orgel, Tracker, Organizer, Transcribe }

/// <summary>
/// 메뉴 줄(File …)과 툴바(새 곡 …) 사이의 가로 페이지 메뉴: 구역 머리글(CREATE / PLAY / SCORE / TOOLS) + 둥근 아이콘 메뉴.
/// 메뉴를 누르면 PageSelected가 불린다. 창이 좁으면 구역 머리글을 빼고, 더 좁으면 아이콘만 (이름은 풍선 도움말).
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class NavBar : Control
{
    private sealed class NavItem
    {
        public AppPage Page;
        public string Text;
        public string Hint;
        public char Glyph;
        public string Section;
        public Rectangle Bounds;
    }

    private const int BarHeight = 40;
    private const int IconSize = 22;
    private const int ItemPad = 10;
    private const int SectionGap = 14;

    private readonly List<NavItem> items = new List<NavItem>();
    private readonly List<(string Title, Rectangle Bounds)> sections = new List<(string, Rectangle)>();
    private readonly ToolTip tip = new ToolTip();
    private NavItem hover;
    private AppPage selected = AppPage.Composer;
    private bool iconsOnly;

    public event Action<AppPage> PageSelected;

    public NavBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = ShellTheme.SideBack;
        Height = BarHeight;
        Cursor = Cursors.Default;

        Add("CREATE", AppPage.Composer, "Composer", "건반 악보 작곡 · 재생 (ROL / IMS)", Glyphs.Composer);
        Add("CREATE", AppPage.Instrument, "Instrument Maker", "FM 악기 편집 · 시음 (BNK / INS)", Glyphs.Instrument);
        Add("CREATE", AppPage.Tracker, "Tracker", "트래커 곡 만들기 (S3M / STM / MOD / NST)", Glyphs.Tracker);
        Add("PLAY", AppPage.Jukebox, "Jukebox", "폴더의 곡 재생 (ROL · IMS · SOP · S3M · STM · MOD · MID · WRK) · 가사 · 채널 믹서", Glyphs.Jukebox);
        Add("SCORE", AppPage.Note, "Note", "오선 악보 보기 · MusicXML / MIDI / PNG · 인쇄", Glyphs.Note);
        Add("SCORE", AppPage.Orgel, "Orgel Note", "30음 오르골 띠 구멍 위치 · 시험 연주 · 실제 크기 인쇄", Glyphs.Orgel);
        Add("SCORE", AppPage.Transcribe, "Transcribe", "음악 파일(MP3 · WAV)의 주 멜로디를 악보로 채보 (이 PC에서 분석)", Glyphs.Transcribe);
        Add("TOOLS", AppPage.Organizer, "Organizer", "같은 곡 찾기 · 정리 (ROL / IMS / SOP): 삭제 · 이동 · 시뮬레이션", Glyphs.Organizer);
    }

    private void Add(string section, AppPage page, string text, string hint, char glyph)
    {
        items.Add(new NavItem { Section = section, Page = page, Text = text, Hint = hint, Glyph = glyph });
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

    // ---------------------------------------------------------------- 배치

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ArrangeItems();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ArrangeItems();
    }

    /// <summary>가로로 늘어놓는다. 넓이가 모자라면 머리글 → 글자 순으로 뺀다.</summary>
    private void ArrangeItems()
    {
        if (!TryLayout(true, false) && !TryLayout(false, false)) TryLayout(false, true);
        Invalidate();
    }

    private bool TryLayout(bool withSections, bool icons)
    {
        sections.Clear();
        iconsOnly = icons;
        int x = 10;
        string last = null;
        foreach (var it in items)
        {
            if (it.Section != last)
            {
                if (last != null) x += SectionGap;
                last = it.Section;
                if (withSections)
                {
                    int w = TextRenderer.MeasureText(it.Section, ShellTheme.Section).Width;
                    sections.Add((it.Section, new Rectangle(x, 0, w, BarHeight)));
                    x += w + 6;
                }
                else if (sections.Count > 0 || x > 10) sections.Add((null, new Rectangle(x - SectionGap / 2, 0, 1, BarHeight)));
            }
            int width = icons ? IconSize + ItemPad * 2 - 6 : IconSize + 8 + TextRenderer.MeasureText(it.Text, ShellTheme.Nav).Width + ItemPad * 2 - 4;
            it.Bounds = new Rectangle(x, 4, width, BarHeight - 8);
            x += width + 2;
        }
        return x + 10 <= Width;
    }

    // ---------------------------------------------------------------- 그리기

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 구역 머리글 (좁으면 세로 구분선)
        using (var muted = new SolidBrush(ShellTheme.SideMuted))
        using (var line = new Pen(ShellTheme.SideLine))
        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
            foreach (var s in sections)
            {
                if (s.Title == null) { g.DrawLine(line, s.Bounds.X, 9, s.Bounds.X, BarHeight - 9); continue; }
                g.DrawString(s.Title, ShellTheme.Section, muted, new RectangleF(s.Bounds.X, 0, s.Bounds.Width + 8, BarHeight), sf);
            }

        // 메뉴
        foreach (var it in items)
        {
            var r = it.Bounds;
            bool sel = it.Page == selected;
            if (sel || it == hover)
                using (var b = new SolidBrush(sel ? ShellTheme.SideSelected : ShellTheme.SideHover)) g.FillRectangle(b, r);
            if (sel)
                using (var b = new SolidBrush(ShellTheme.Accent)) g.FillRectangle(b, r.X + 4, r.Bottom - 3, r.Width - 8, 3);

            var icon = new Rectangle(r.X + (iconsOnly ? (r.Width - IconSize) / 2 : ItemPad - 2), r.Y + (r.Height - IconSize) / 2, IconSize, IconSize);
            GlyphImages.DrawCircleIcon(g, icon, it.Glyph, sel ? ShellTheme.Accent : ShellTheme.AccentDark, Color.White);
            if (iconsOnly) continue;

            using (var tb = new SolidBrush(sel ? Color.White : ShellTheme.SideText))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(it.Text, ShellTheme.Nav, tb, new RectangleF(icon.Right + 6, r.Y, r.Right - icon.Right, r.Height), sf);
        }

        using (var line = new Pen(ShellTheme.SideLine)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }

    // ---------------------------------------------------------------- 마우스

    private NavItem HitTest(Point p)
    {
        foreach (var it in items)
            if (it.Bounds.Contains(p)) return it;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitTest(e.Location);
        if (h == hover) return;
        hover = h;
        Cursor = h != null ? Cursors.Hand : Cursors.Default;
        tip.SetToolTip(this, h != null ? (iconsOnly ? h.Text + " — " : "") + h.Hint : null);
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
