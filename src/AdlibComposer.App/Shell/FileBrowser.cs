using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Shell;

/// <summary>곡 파일 종류 (어느 페이지가 여는지 정할 때).</summary>
internal enum MusicFileKind { None, Adlib, Tracker, Midi, Vgm, Audio }

/// <summary>
/// 왼쪽 공용 폴더 / 곡 목록 (모든 페이지가 같이 쓴다).
/// [폴더 열기]로 고른 폴더(= 뿌리)와 그 아래 폴더만 오간다: 목록 = (위 폴더 ..) · 하위 폴더 · 곡 파일.
/// 폴더 두 번 클릭 = 들어가기, 곡 두 번 클릭 / Enter = FileActivated (지금 페이지가 열거나, 그 형식을 여는 페이지로).
/// 곡이 많으면 세로 스크롤바 + 아래 ◀ ▶ 버튼으로 한 쪽(보이는 줄 수)씩 넘긴다. 연 폴더는 설정(BrowserFolder)에 남겨 다음에 다시 연다.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class FileBrowser : UserControl
{
    private static readonly string[] AdlibExt = { ".rol", ".ims", ".sop" };
    private static readonly string[] TrackerExt = { ".s3m", ".stm", ".mod", ".nst" };
    private static readonly string[] MidiExt = { ".mid", ".midi", ".wrk" };
    private static readonly string[] VgmExt = AdlibComposer.Audio.ChipSongs.Extensions.ToArray();
    private static readonly string[] AudioExt = { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac" };

    /// <summary>목록 한 줄: 위 폴더(..) · 폴더 · 곡 파일 · 파일 안 곡 (KSS · NSF · HES, Path = "파일::n").</summary>
    private sealed class Entry
    {
        public string Path;
        public bool IsDir, IsUp;
        /// <summary>곡이 여럿 든 파일 (펼치면 곡마다 줄).</summary>
        public bool Multi;
        /// <summary>파일 안 곡 한 줄 (Path = "파일::n").</summary>
        public bool IsTrack;
    }

    /// <summary>펼쳐 둔 파일 (곡이 여럿 든 파일: 곡마다 줄). 폴더를 다시 그려도 그대로.</summary>
    private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly Workspace ws;
    private readonly Func<AppSettings> settings;
    private readonly ListView list = new ListView();
    private readonly Label pathLabel = new Label();
    private readonly Label emptyLabel = new Label();
    private readonly Button openButton = new Button(), upButton = new Button();
    private readonly Button prevPage = new Button(), nextPage = new Button();
    private readonly Label pageLabel = new Label();
    private readonly ImageList icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    private readonly ToolTip tips = new ToolTip();
    private readonly Timer pageTimer = new Timer { Interval = 200 };
    private string root, current, currentPath;
    private int songCount;
    private int lastTop = -1;

    /// <summary>목록의 곡을 두 번 클릭 / Enter.</summary>
    public event Action<string> FileActivated;

    public FileBrowser(Workspace ws, Func<AppSettings> settings)
    {
        this.ws = ws;
        this.settings = settings;
        BackColor = Color.White;

        icons.Images.Add("dir", GlyphImages.Get(Glyphs.Folder, 16, Color.FromArgb(214, 160, 40)));
        icons.Images.Add("up", GlyphImages.Get('', 16, ShellTheme.Muted));   // Up
        icons.Images.Add("song", GlyphImages.Get(Glyphs.Composer, 16, ShellTheme.Accent));
        icons.Images.Add("multi", GlyphImages.Get(Glyphs.Jukebox, 16, ShellTheme.Accent));
        icons.Images.Add("track", GlyphImages.Get(Glyphs.Play, 16, ShellTheme.Muted));

        // 위: [폴더 열기] [위로]
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 5, 6, 0), BackColor = Color.FromArgb(246, 247, 249), WrapContents = false };
        SetupButton(openButton, " 폴더 열기", Glyphs.Open, 104, "곡 폴더 열기 (폴더를 끌어다 놓아도 된다) — 그 폴더와 아래 폴더만 오간다");
        SetupButton(upButton, " 위로", '', 64, "위 폴더로 (연 폴더 위로는 가지 않는다) · Backspace");
        openButton.Click += delegate { BrowseFolder(); };
        upButton.Click += delegate { GoUp(); };
        bar.Controls.Add(openButton);
        bar.Controls.Add(upButton);

        pathLabel.Dock = DockStyle.Top;
        pathLabel.Height = 24;
        pathLabel.Font = ShellTheme.Ui;
        pathLabel.ForeColor = ShellTheme.Muted;
        pathLabel.AutoEllipsis = true;
        pathLabel.Padding = new Padding(8, 4, 4, 0);

        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.Font = ShellTheme.Ui;
        list.ShowItemToolTips = true;
        list.SmallImageList = icons;
        list.Scrollable = true;   // 세로 스크롤바
        list.Columns.Add("이름", 108);
        list.Columns.Add("제목", 75);
        list.Columns.Add("형식", 40);
        list.Columns.Add("가사", 30, HorizontalAlignment.Center);
        list.Columns.Add("뱅크", 30, HorizontalAlignment.Center);
        list.ItemActivate += delegate { ActivateSelected(); };
        list.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
            else if (e.KeyCode == Keys.Right && SelectedEntry != null && SelectedEntry.Multi) { SetExpanded(list.SelectedItems[0], true); e.Handled = true; }
            else if (e.KeyCode == Keys.Left && SelectedEntry != null && (SelectedEntry.Multi || SelectedEntry.IsTrack))
            {
                var fileItem = SelectedEntry.IsTrack ? FindItem(AdlibComposer.Audio.ChipSongs.FilePart(SelectedEntry.Path)) : list.SelectedItems[0];
                if (fileItem != null) { SetExpanded(fileItem, false); fileItem.Selected = true; fileItem.Focused = true; fileItem.EnsureVisible(); }
                e.Handled = true;
            }
        };
        list.Resize += delegate { UpdatePager(); };
        tips.SetToolTip(list, "폴더 두 번 클릭: 들어가기 · 곡 두 번 클릭 / Enter: 지금 페이지에서 열기 (못 여는 형식이면 그 형식을 여는 페이지로)\n"
            + "곡이 여럿 든 파일(KSS · NSF · HES, ▸): 두 번 클릭 / → 로 곡 목록 펼치기, ← 로 접기 — 곡 줄을 두 번 클릭하면 그 곡 재생\n"
            + "제목 = 곡 안의 제목 · 가사 = 같은 이름 .ISS · 뱅크 = 같은 이름 .BNK");

        emptyLabel.Dock = DockStyle.Fill;
        emptyLabel.Font = ShellTheme.Ui;
        emptyLabel.ForeColor = ShellTheme.Muted;
        emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
        emptyLabel.Text = "[폴더 열기]로 곡 폴더를 고르거나\n폴더를 여기에 끌어다 놓으세요";

        // 아래: ◀ n / m 쪽 ▶
        var pager = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 32, ColumnCount = 3, Padding = new Padding(6, 3, 6, 3), BackColor = Color.FromArgb(246, 247, 249) };
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        SetupButton(prevPage, "", '', 40, "앞 쪽 (한 화면 위로)");
        SetupButton(nextPage, "", '', 40, "다음 쪽 (한 화면 아래로)");
        prevPage.Dock = nextPage.Dock = DockStyle.Fill;
        prevPage.Margin = nextPage.Margin = new Padding(0);
        prevPage.Click += delegate { TurnPage(-1); };
        nextPage.Click += delegate { TurnPage(1); };
        pageLabel.Dock = DockStyle.Fill;
        pageLabel.Font = ShellTheme.Ui;
        pageLabel.ForeColor = ShellTheme.Muted;
        pageLabel.TextAlign = ContentAlignment.MiddleCenter;
        pager.Controls.Add(prevPage, 0, 0);
        pager.Controls.Add(pageLabel, 1, 0);
        pager.Controls.Add(nextPage, 2, 0);

        Controls.Add(list);
        Controls.Add(emptyLabel);
        Controls.Add(pager);
        Controls.Add(pathLabel);
        Controls.Add(bar);

        // 스크롤바를 끌어도 쪽 번호가 맞도록 (ListView에는 Scroll 이벤트가 없다)
        pageTimer.Tick += delegate
        {
            int top = list.Items.Count > 0 && list.TopItem != null ? list.TopItem.Index : -1;
            if (top != lastTop) UpdatePager();
        };
        pageTimer.Start();
        ShowEmpty();
    }

    private void SetupButton(Button b, string text, char glyph, int width, string tip)
    {
        b.Text = text;
        b.Image = GlyphImages.Get(glyph, 16, Color.FromArgb(50, 54, 62));
        b.ImageAlign = string.IsNullOrEmpty(text) ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
        b.TextImageRelation = string.IsNullOrEmpty(text) ? TextImageRelation.Overlay : TextImageRelation.ImageBeforeText;
        b.Font = ShellTheme.Ui;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(214, 218, 224);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(236, 239, 242);
        b.BackColor = Color.White;
        b.Width = width;
        b.Height = 26;
        b.Margin = new Padding(0, 0, 4, 0);
        b.Cursor = Cursors.Hand;
        b.TabStop = false;
        tips.SetToolTip(b, tip);
    }

    public static MusicFileKind KindOf(string path)
    {
        string ext = (Path.GetExtension(AdlibComposer.Audio.ChipSongs.FilePart(path)) ?? "").ToLowerInvariant();   // "파일::n" = 파일 안 곡
        if (Array.IndexOf(AdlibExt, ext) >= 0) return MusicFileKind.Adlib;
        if (Array.IndexOf(TrackerExt, ext) >= 0) return MusicFileKind.Tracker;
        if (Array.IndexOf(MidiExt, ext) >= 0) return MusicFileKind.Midi;
        if (Array.IndexOf(VgmExt, ext) >= 0) return MusicFileKind.Vgm;
        if (Array.IndexOf(AudioExt, ext) >= 0) return MusicFileKind.Audio;
        return MusicFileKind.None;
    }

    // ---------------------------------------------------------------- 공개

    /// <summary>지금 보이는 폴더의 곡 파일 경로 (보이는 순서, 폴더 · 펼친 곡 줄 빼고 — 곡이 여럿 든 파일은 Jukebox가 곡마다 펼친다).</summary>
    public List<string> Paths
    {
        get
        {
            var l = new List<string>();
            foreach (ListViewItem it in list.Items) { var e = (Entry)it.Tag; if (!e.IsDir && !e.IsTrack) l.Add(e.Path); }
            return l;
        }
    }

    private Entry SelectedEntry { get { return list.SelectedItems.Count > 0 ? (Entry)list.SelectedItems[0].Tag : null; } }

    private ListViewItem FindItem(string path)
    {
        foreach (ListViewItem it in list.Items)
            if (string.Equals(((Entry)it.Tag).Path, path, StringComparison.OrdinalIgnoreCase)) return it;
        return null;
    }

    /// <summary>목록에서 고른 곡 (폴더면 null).</summary>
    public string SelectedPath
    {
        get
        {
            if (list.SelectedItems.Count == 0) return null;
            var e = (Entry)list.SelectedItems[0].Tag;
            return e.IsDir ? null : e.Path;
        }
    }

    /// <summary>지금 보이는 폴더 (연 폴더가 없으면 null). 파일 대화상자 시작 폴더 등.</summary>
    public string CurrentFolder { get { return current; } }

    public bool ListFocused { get { return list.Focused; } }
    public void FocusList() { list.Focus(); }

    /// <summary>시작할 때: 지난번에 연 폴더를 다시 연다 (없으면 빈 목록).</summary>
    public void RestoreLastFolder()
    {
        string p = settings().BrowserFolderPath;
        if (p != null && Directory.Exists(p)) OpenFolder(p, false);
    }

    /// <summary>폴더를 연다 (이 폴더가 뿌리: 이 아래만 오간다). 열기 버튼 · 폴더 끌어다 놓기.</summary>
    public void OpenFolder(string dir, bool remember = true)
    {
        try { dir = Path.GetFullPath(dir); } catch (Exception) { }
        if (!Directory.Exists(dir)) return;
        root = dir.TrimEnd('\\', '/');
        if (root.Length == 2 && root[1] == ':') root += "\\";   // 드라이브 뿌리
        ShowFolder(root);
        if (remember)
        {
            AppLog.Info("파일", "폴더 열기: " + root);
            try { var s = settings(); s.BrowserFolder = AppSettings.ToStored(root); s.Save(); }
            catch (Exception ex) { AppLog.Warn("파일", "설정에 폴더를 남기지 못했습니다", ex.Message); }
        }
    }

    /// <summary>
    /// 지금 연(재생 중인) 파일을 굵게 표시하고 고른다. "파일::n"(파일 안 곡)이면 그 파일을 펼쳐 곡 줄을 고르고 파일 줄도 굵게.
    /// </summary>
    public void MarkCurrent(string path)
    {
        currentPath = path;
        string file = AdlibComposer.Audio.ChipSongs.FilePart(path);
        bool track = path != null && !string.Equals(file, path, StringComparison.OrdinalIgnoreCase);
        if (track)
        {
            var fileItem = FindItem(file);
            if (fileItem != null) SetExpanded(fileItem, true);
        }
        list.BeginUpdate();
        ListViewItem pick = null;
        foreach (ListViewItem it in list.Items)
        {
            var e = (Entry)it.Tag;
            bool exact = !e.IsDir && string.Equals(e.Path, currentPath, StringComparison.OrdinalIgnoreCase);
            bool parent = track && !e.IsDir && !e.IsTrack && string.Equals(e.Path, file, StringComparison.OrdinalIgnoreCase);
            it.Font = exact || parent ? new Font(list.Font, FontStyle.Bold) : list.Font;
            if (exact || (parent && pick == null)) pick = it;
        }
        list.EndUpdate();
        if (pick != null) { pick.Selected = true; pick.Focused = true; pick.EnsureVisible(); }
    }

    // ---------------------------------------------------------------- 폴더 오가기

    private void BrowseFolder()
    {
        using (var dlg = new FolderBrowserDialog { Description = "곡 폴더 (이 폴더와 아래 폴더를 왼쪽 목록에서 오간다)", UseDescriptionForTitle = true, ShowNewFolderButton = false })
        {
            if (current != null && Directory.Exists(current)) dlg.SelectedPath = current;
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) OpenFolder(dlg.SelectedPath);
        }
    }

    private bool AtRoot { get { return current == null || string.Equals(current.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); } }

    private void GoUp()
    {
        if (AtRoot) return;
        string from = current;
        string parent = Path.GetDirectoryName(current.TrimEnd('\\'));
        if (parent == null) return;
        ShowFolder(parent);
        // 나온 폴더를 고른다
        foreach (ListViewItem it in list.Items)
            if (string.Equals(((Entry)it.Tag).Path, from, StringComparison.OrdinalIgnoreCase)) { it.Selected = true; it.Focused = true; it.EnsureVisible(); break; }
    }

    private void ActivateSelected()
    {
        if (list.SelectedItems.Count == 0) return;
        var e = (Entry)list.SelectedItems[0].Tag;
        if (e.IsUp) GoUp();
        else if (e.IsDir) ShowFolder(e.Path);
        else if (e.Multi) SetExpanded(list.SelectedItems[0], !expanded.Contains(e.Path));
        else if (FileActivated != null) FileActivated(e.Path);
    }

    private void ShowEmpty()
    {
        list.Items.Clear();
        list.Visible = false;
        emptyLabel.Visible = true;
        pathLabel.Text = "열린 폴더 없음";
        upButton.Enabled = false;
        songCount = 0;
        UpdatePager();
    }

    /// <summary>폴더 하나를 목록으로: (..) · 하위 폴더 · 곡 파일 (이름순).</summary>
    private void ShowFolder(string dir)
    {
        current = dir;
        list.Visible = true;
        emptyLabel.Visible = false;
        list.BeginUpdate();
        list.Items.Clear();
        songCount = 0;
        if (!AtRoot)
            list.Items.Add(new ListViewItem("..", "up") { Tag = new Entry { Path = Path.GetDirectoryName(dir.TrimEnd('\\')), IsDir = true, IsUp = true }, ToolTipText = "위 폴더로", ForeColor = ShellTheme.Muted });
        try
        {
            foreach (var d in Directory.GetDirectories(dir).OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            {
                FileAttributes a;
                try { a = File.GetAttributes(d); } catch (Exception) { continue; }
                if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                var it = new ListViewItem(Path.GetFileName(d), "dir") { Tag = new Entry { Path = d, IsDir = true }, ToolTipText = d };
                it.SubItems.Add("");
                it.SubItems.Add("폴더");
                list.Items.Add(it);
            }
            var files = Directory.GetFiles(dir);
            var names = new HashSet<string>(files.Select(f => Path.GetFileName(f).ToUpperInvariant()));
            foreach (var f in files.OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
            {
                if (KindOf(f) == MusicFileKind.None) continue;
                string stem = Path.GetFileNameWithoutExtension(f).ToUpperInvariant();
                bool multi = AdlibComposer.Audio.ChipSongs.HasTracks(f);
                bool open = multi && expanded.Contains(f);
                var it = new ListViewItem(FileText(f, multi, open), multi ? "multi" : "song")
                {
                    Tag = new Entry { Path = f, Multi = multi },
                    ToolTipText = f + (multi ? "\n곡이 여럿 든 파일: 두 번 클릭 / → 로 곡 목록" : ""),
                };
                it.SubItems.Add(CachedTitle(f) ?? "");
                it.SubItems.Add(Path.GetExtension(f).TrimStart('.').ToUpperInvariant());
                it.SubItems.Add(names.Contains(stem + ".ISS") ? "●" : "");
                it.SubItems.Add(names.Contains(stem + ".BNK") ? "●" : "");
                if (string.Equals(f, AdlibComposer.Audio.ChipSongs.FilePart(currentPath), StringComparison.OrdinalIgnoreCase)) it.Font = new Font(list.Font, FontStyle.Bold);
                list.Items.Add(it);
                songCount++;
                if (open) foreach (var t in TrackItems(f)) list.Items.Add(t);
            }
        }
        catch (Exception ex) { AppLog.Warn("파일", "폴더를 읽을 수 없습니다: " + dir, ex.Message); }
        list.EndUpdate();
        upButton.Enabled = !AtRoot;
        // 경로: 연 폴더 이름 › 아래 폴더 …
        string rel = AtRoot ? "" : Path.GetRelativePath(root, dir);
        string rootName = Path.GetFileName(root.TrimEnd('\\'));
        pathLabel.Text = (rootName.Length > 0 ? rootName : root) + (rel.Length > 0 ? " › " + rel.Replace("\\", " › ") : "");
        tips.SetToolTip(pathLabel, dir);
        if (list.Items.Count > 0) list.Items[0].Focused = true;
        lastTop = -1;
        UpdatePager();
        LoadTitles();
    }

    // ---------------------------------------------------------------- 곡이 여럿 든 파일 (KSS · NSF · HES)

    private static string FileText(string path, bool multi, bool open)
    {
        return (multi ? (open ? "▾ " : "▸ ") : "") + Path.GetFileName(path);
    }

    /// <summary>파일 안 곡마다 한 줄 ("파일::n", 들여 쓰기). HES는 처음 한 번 곡을 찾느라 1초쯤 걸릴 수 있다.</summary>
    private List<ListViewItem> TrackItems(string file)
    {
        var l = new List<ListViewItem>();
        List<(string path, string title)> tracks;
        try { tracks = AdlibComposer.Audio.ChipSongs.Tracks(file); }
        catch (Exception ex) { AppLog.Warn("파일", "곡 목록을 읽을 수 없습니다: " + Path.GetFileName(file), ex.Message); return l; }
        for (int i = 0; i < tracks.Count; i++)
        {
            var (path, title) = tracks[i];
            var it = new ListViewItem((i + 1).ToString("00"), "track")
            {
                Tag = new Entry { Path = path, IsTrack = true },
                IndentCount = 1,
                ToolTipText = Path.GetFileName(file) + " — " + (i + 1) + "/" + tracks.Count + (title.Length > 0 ? ": " + title : "") + "\n두 번 클릭 / Enter: 이 곡 재생",
            };
            // 제목이 "이름 #n"뿐이면 (m3u 없음) 칸을 비운다: 번호는 이미 이름 칸에
            it.SubItems.Add(System.Text.RegularExpressions.Regex.IsMatch(title, @" #\d+$") ? "" : title);
            it.SubItems.Add("");
            it.SubItems.Add("");
            it.SubItems.Add("");
            if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) it.Font = new Font(list.Font, FontStyle.Bold);
            l.Add(it);
        }
        return l;
    }

    /// <summary>곡이 여럿 든 파일 줄을 펼치거나 접는다 (바로 아래에 곡 줄을 넣거나 뺀다).</summary>
    private void SetExpanded(ListViewItem fileItem, bool open)
    {
        var e = (Entry)fileItem.Tag;
        if (!e.Multi) return;
        bool isOpen = expanded.Contains(e.Path);
        int at = fileItem.Index;
        if (open == isOpen && (!open || (at + 1 < list.Items.Count && ((Entry)list.Items[at + 1].Tag).IsTrack))) return;
        list.BeginUpdate();
        // 있던 곡 줄을 먼저 뺀다
        while (at + 1 < list.Items.Count && ((Entry)list.Items[at + 1].Tag).IsTrack) list.Items.RemoveAt(at + 1);
        if (open)
        {
            var old = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                var rows = TrackItems(e.Path);
                for (int i = 0; i < rows.Count; i++) list.Items.Insert(at + 1 + i, rows[i]);
                expanded.Add(e.Path);
                if (rows.Count > 0) list.Items[at + rows.Count].EnsureVisible();
                fileItem.EnsureVisible();
            }
            finally { Cursor = old; }
        }
        else expanded.Remove(e.Path);
        fileItem.Text = FileText(e.Path, true, open);
        list.EndUpdate();
        UpdatePager();
    }

    // ---------------------------------------------------------------- 쪽 넘기기

    /// <summary>한 쪽 = 지금 목록에 보이는 줄 수.</summary>
    private int PageSize
    {
        get
        {
            if (list.Items.Count == 0) return 1;
            int rowH = Math.Max(1, list.GetItemRect(0).Height);
            int header = list.TopItem != null ? list.GetItemRect(list.TopItem.Index).Top : rowH;
            return Math.Max(1, (list.ClientSize.Height - Math.Max(0, header)) / rowH);
        }
    }

    private void TurnPage(int dir)
    {
        if (list.Items.Count == 0) return;
        int size = PageSize;
        int top = list.TopItem != null ? list.TopItem.Index : 0;
        int target = Math.Max(0, Math.Min(list.Items.Count - 1, top + dir * size));
        // 아래로: 그 쪽의 마지막 줄을 먼저 보이게 한 뒤 맨 위를 맞춘다 (TopItem만으로는 안 움직일 때가 있다)
        if (dir > 0) list.EnsureVisible(Math.Min(list.Items.Count - 1, target + size - 1));
        list.EnsureVisible(target);
        try { list.TopItem = list.Items[target]; } catch (Exception) { }
        UpdatePager();
    }

    private void UpdatePager()
    {
        int n = list.Items.Count;
        int size = PageSize;
        int pages = Math.Max(1, (n + size - 1) / size);
        int top = n > 0 && list.TopItem != null ? list.TopItem.Index : 0;
        lastTop = n > 0 && list.TopItem != null ? top : -1;
        int page = Math.Min(pages, top / size + 1);
        if (top + size >= n) page = pages;   // 맨 아래까지 내려왔으면 마지막 쪽
        pageLabel.Text = current == null ? "" : page + " / " + pages + " 쪽 · " + songCount + "곡";
        prevPage.Enabled = n > 0 && top > 0;
        nextPage.Enabled = n > 0 && top + size < n;
    }

    // ---------------------------------------------------------------- 목록의 곡 제목 (뒤에서 읽기)

    private readonly Dictionary<string, (DateTime Time, string Title)> titleCache = new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);
    private int titleGeneration;

    /// <summary>캐시에 있고 파일이 그대로면 제목, 아니면 null (아직 안 읽음).</summary>
    private string CachedTitle(string path)
    {
        try
        {
            lock (titleCache)
                if (titleCache.TryGetValue(path, out var c) && c.Time == File.GetLastWriteTimeUtc(path)) return c.Title;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>목록 파일의 곡 안 제목을 다른 스레드에서 읽어 '제목' 칸을 채운다. 폴더가 바뀌면 그만둔다.</summary>
    private void LoadTitles()
    {
        int gen = ++titleGeneration;
        var todo = new List<string>();
        foreach (ListViewItem it in list.Items)
        {
            var e = (Entry)it.Tag;
            if (!e.IsDir && !e.IsTrack && KindOf(e.Path) != MusicFileKind.Audio && CachedTitle(e.Path) == null) todo.Add(e.Path);
        }
        if (todo.Count == 0) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            var batch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < todo.Count && gen == titleGeneration; i++)
            {
                string t = ReadSongTitle(todo[i]);
                try { lock (titleCache) titleCache[todo[i]] = (File.GetLastWriteTimeUtc(todo[i]), t); } catch (Exception) { }
                batch[todo[i]] = t;
                if (batch.Count >= 25 || i == todo.Count - 1)
                {
                    var done = batch;
                    batch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try { BeginInvoke((Action)(() => ApplyTitles(done, gen))); } catch (Exception) { return; }
                }
            }
        });
    }

    private void ApplyTitles(Dictionary<string, string> titles, int gen)
    {
        if (gen != titleGeneration) return;
        list.BeginUpdate();
        foreach (ListViewItem it in list.Items)
        {
            var e = (Entry)it.Tag;
            if (!e.IsDir && !e.IsTrack && titles.TryGetValue(e.Path, out var t)) it.SubItems[1].Text = t;
        }
        list.EndUpdate();
    }

    /// <summary>곡 파일 안의 제목 (ROL은 제목 칸이 없어 빈 값). 파일 이름과 같거나 'untitled'면 빈 값.</summary>
    private string ReadSongTitle(string path)
    {
        string t = "";
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (MidiFiles.IsMidi(path)) t = MidiFiles.Load(path).Title;
            else if (AdlibComposer.Audio.ChipSongs.IsChipSong(path)) t = AdlibComposer.Audio.ChipSongs.Title(path);
            else if (SopFormat.IsSop(path)) t = SopFormat.Load(path).Title;
            else if (TrackerFiles.IsTracker(path)) t = TrackerFiles.Load(path).Title;
            else if (ext == ".ims")
            {
                var fmt = ws.Formats.FindSong(path);
                if (fmt != null && fmt.CanRead) using (var fs = File.OpenRead(path)) t = fmt.Read(fs).Title;
            }
        }
        catch (Exception) { return ""; }
        t = System.Text.RegularExpressions.Regex.Replace(t ?? "", @"\s+", " ").Trim();
        if (t.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
            || t.Equals("untitled", StringComparison.OrdinalIgnoreCase)) return "";
        return t;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { tips.Dispose(); pageTimer.Dispose(); icons.Dispose(); }
        base.Dispose(disposing);
    }
}
