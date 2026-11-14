using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Shell;

/// <summary>곡 파일 종류 (어느 페이지가 여는지 정할 때).</summary>
internal enum MusicFileKind { None, Adlib, Tracker, Midi, Audio }

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
    private static readonly string[] AudioExt = { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac" };

    /// <summary>목록 한 줄: 위 폴더(..) · 폴더 · 곡 파일.</summary>
    private sealed class Entry
    {
        public string Path;
        public bool IsDir, IsUp;
    }

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
        list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; } };
        list.Resize += delegate { UpdatePager(); };
        tips.SetToolTip(list, "폴더 두 번 클릭: 들어가기 · 곡 두 번 클릭 / Enter: 지금 페이지에서 열기 (못 여는 형식이면 그 형식을 여는 페이지로)\n제목 = 곡 안의 제목 · 가사 = 같은 이름 .ISS · 뱅크 = 같은 이름 .BNK");

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
        string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
        if (Array.IndexOf(AdlibExt, ext) >= 0) return MusicFileKind.Adlib;
        if (Array.IndexOf(TrackerExt, ext) >= 0) return MusicFileKind.Tracker;
        if (Array.IndexOf(MidiExt, ext) >= 0) return MusicFileKind.Midi;
        if (Array.IndexOf(AudioExt, ext) >= 0) return MusicFileKind.Audio;
        return MusicFileKind.None;
    }

    // ---------------------------------------------------------------- 공개

    /// <summary>지금 보이는 폴더의 곡 경로 (보이는 순서, 폴더 빼고).</summary>
    public List<string> Paths
    {
        get
        {
            var l = new List<string>();
            foreach (ListViewItem it in list.Items) { var e = (Entry)it.Tag; if (!e.IsDir) l.Add(e.Path); }
            return l;
        }
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

    /// <summary>지금 연(재생 중인) 파일을 굵게 표시하고 고른다.</summary>
    public void MarkCurrent(string path)
    {
        currentPath = path;
        foreach (ListViewItem it in list.Items)
        {
            var e = (Entry)it.Tag;
            bool cur = !e.IsDir && string.Equals(e.Path, currentPath, StringComparison.OrdinalIgnoreCase);
            it.Font = cur ? new Font(list.Font, FontStyle.Bold) : list.Font;
            if (cur) { it.Selected = true; it.Focused = true; it.EnsureVisible(); }
        }
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
                var it = new ListViewItem(Path.GetFileName(f), "song") { Tag = new Entry { Path = f }, ToolTipText = f };
                it.SubItems.Add(CachedTitle(f) ?? "");
                it.SubItems.Add(Path.GetExtension(f).TrimStart('.').ToUpperInvariant());
                it.SubItems.Add(names.Contains(stem + ".ISS") ? "●" : "");
                it.SubItems.Add(names.Contains(stem + ".BNK") ? "●" : "");
                if (string.Equals(f, currentPath, StringComparison.OrdinalIgnoreCase)) it.Font = new Font(list.Font, FontStyle.Bold);
                list.Items.Add(it);
                songCount++;
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
            if (!e.IsDir && KindOf(e.Path) != MusicFileKind.Audio && CachedTitle(e.Path) == null) todo.Add(e.Path);
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
            if (!e.IsDir && titles.TryGetValue(e.Path, out var t)) it.SubItems[1].Text = t;
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
