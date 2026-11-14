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
/// 왼쪽 공용 폴더 트리(위) + 곡 파일 목록(아래). 모든 페이지(Composer · Tracker · Jukebox · Note · Orgel Note · Transcribe)가 같이 쓴다.
/// 두 번 클릭 / Enter = FileActivated (지금 페이지가 열거나, 그 형식을 여는 페이지로 간다).
/// 목록: ROL · IMS · SOP · S3M · STM · MOD · NST · MID · WRK · MP3 · WAV …, 제목 = 곡 안 제목, 가사 = 같은 이름 .ISS, 뱅크 = 같은 이름 .BNK.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class FileBrowser : UserControl
{
    private static readonly string[] AdlibExt = { ".rol", ".ims", ".sop" };
    private static readonly string[] TrackerExt = { ".s3m", ".stm", ".mod", ".nst" };
    private static readonly string[] MidiExt = { ".mid", ".midi", ".wrk" };
    private static readonly string[] AudioExt = { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac" };

    private readonly Workspace ws;
    private readonly Func<AppSettings> settings;
    private readonly TreeView tree = new TreeView();
    private readonly ListView list = new ListView();
    private readonly Label folderLabel = new Label();
    private readonly ToolTip tips = new ToolTip();
    private string currentPath;

    /// <summary>목록의 파일을 두 번 클릭 / Enter.</summary>
    public event Action<string> FileActivated;

    public FileBrowser(Workspace ws, Func<AppSettings> settings)
    {
        this.ws = ws;
        this.settings = settings;
        BackColor = Color.White;

        var inner = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 4, BackColor = Color.FromArgb(225, 228, 232) };
        inner.Panel1.BackColor = Color.White;
        inner.Panel2.BackColor = Color.White;
        Controls.Add(inner);
        // 처음 크기가 정해진 뒤 폴더 트리 40% / 파일 목록 60%
        bool sized = false;
        inner.SizeChanged += delegate
        {
            if (sized || inner.Height < 200) return;
            sized = true;
            try { inner.SplitterDistance = inner.Height * 2 / 5; } catch (Exception) { }
        };

        tree.Dock = DockStyle.Fill;
        tree.BorderStyle = BorderStyle.None;
        tree.HideSelection = false;
        tree.ShowLines = false;
        tree.FullRowSelect = true;
        tree.ItemHeight = 22;
        tree.Font = ShellTheme.Ui;
        tree.BeforeExpand += (s, e) => FillChildren(e.Node);
        tree.AfterSelect += (s, e) => ShowFolder(e.Node.Tag as string);
        inner.Panel1.Controls.Add(tree);
        inner.Panel1.Controls.Add(SectionHeader("폴더"));

        list.Dock = DockStyle.Fill;
        list.BorderStyle = BorderStyle.None;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.Font = ShellTheme.Ui;
        list.ShowItemToolTips = true;
        list.Columns.Add("파일", 105);
        list.Columns.Add("제목", 75);
        list.Columns.Add("형식", 40);
        list.Columns.Add("가사", 30, HorizontalAlignment.Center);
        list.Columns.Add("뱅크", 30, HorizontalAlignment.Center);
        list.ItemActivate += delegate { if (list.SelectedItems.Count > 0 && FileActivated != null) FileActivated(list.SelectedItems[0].Tag as string); };
        tips.SetToolTip(list, "두 번 클릭 또는 Enter: 지금 페이지에서 열기 (그 페이지가 못 여는 형식이면 여는 페이지로)\n제목 = 곡 안의 제목 · 가사 = 같은 이름 .ISS · 뱅크 = 같은 이름 .BNK");
        inner.Panel2.Controls.Add(list);
        folderLabel.Dock = DockStyle.Top;
        folderLabel.Height = 22;
        folderLabel.Font = ShellTheme.Ui;
        folderLabel.ForeColor = ShellTheme.Muted;
        folderLabel.AutoEllipsis = true;
        folderLabel.Padding = new Padding(8, 3, 4, 0);
        inner.Panel2.Controls.Add(folderLabel);
        inner.Panel2.Controls.Add(SectionHeader("곡 파일"));
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

    private static Control SectionHeader(string text)
    {
        return new Label
        {
            Text = text.ToUpperInvariant(), Dock = DockStyle.Top, Height = 26, Font = ShellTheme.Section,
            ForeColor = ShellTheme.Muted, BackColor = Color.FromArgb(246, 247, 249), Padding = new Padding(8, 7, 0, 0),
        };
    }

    // ---------------------------------------------------------------- 공개

    /// <summary>지금 목록의 파일 경로 (보이는 순서).</summary>
    public List<string> Paths
    {
        get
        {
            var l = new List<string>();
            foreach (ListViewItem it in list.Items) l.Add((string)it.Tag);
            return l;
        }
    }

    /// <summary>목록에서 고른 파일 (없으면 null).</summary>
    public string SelectedPath { get { return list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as string : null; } }

    public bool TreeFocused { get { return tree.Focused; } }
    public bool ListFocused { get { return list.Focused; } }
    public void FocusList() { list.Focus(); }

    /// <summary>처음 보일 때 한 번 트리를 만든다.</summary>
    public void EnsureTree()
    {
        if (tree.Nodes.Count == 0) BuildTree();
    }

    /// <summary>설정(폴더)이 바뀌면 트리를 다시 만든다.</summary>
    public void SettingsChanged()
    {
        if (tree.Nodes.Count > 0) BuildTree();
    }

    /// <summary>지금 연(재생 중인) 파일을 굵게 표시하고 고른다.</summary>
    public void MarkCurrent(string path)
    {
        currentPath = path;
        foreach (ListViewItem it in list.Items)
        {
            bool cur = string.Equals((string)it.Tag, currentPath, StringComparison.OrdinalIgnoreCase);
            it.Font = cur ? new Font(list.Font, FontStyle.Bold) : list.Font;
            if (cur) { it.Selected = true; it.Focused = true; it.EnsureVisible(); }
        }
    }

    // ---------------------------------------------------------------- 트리

    /// <summary>트리 뿌리: ROL · IMS · SOP · 트래커 · MIDI 폴더, 내 음악, 드라이브.</summary>
    private void BuildTree()
    {
        tree.BeginUpdate();
        tree.Nodes.Clear();
        var s = settings();
        AddRoot("ROL 폴더", s.SongFolderPath);
        AddRoot("IMS 폴더", s.ImsFolderPath);
        AddRoot("SOP 폴더", s.SopFolderPath);
        AddRoot("S3M · STM · MOD 폴더", s.TrackerFolderPath);
        AddRoot("MIDI · WRK 폴더", s.MidiFolderPath);
        string music = null;
        try { music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic); } catch (Exception) { }
        if (!string.IsNullOrEmpty(music) && Directory.Exists(music)) AddRoot("내 음악", music);
        var pc = new TreeNode("내 PC");
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                string label = d.Name.TrimEnd('\\');
                try { if (!string.IsNullOrEmpty(d.VolumeLabel)) label = d.VolumeLabel + " (" + label + ")"; } catch (Exception) { }
                pc.Nodes.Add(FolderNode(label, d.RootDirectory.FullName));
            }
        }
        catch (Exception) { }
        tree.Nodes.Add(pc);
        pc.Expand();
        tree.EndUpdate();
        if (tree.Nodes.Count > 0 && tree.SelectedNode == null) tree.SelectedNode = tree.Nodes[0];
    }

    private void AddRoot(string label, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var n = FolderNode(label + "  ·  " + Path.GetFileName(path.TrimEnd('\\', '/')), path);
        n.NodeFont = new Font(ShellTheme.Ui, FontStyle.Bold);
        tree.Nodes.Add(n);
    }

    private static TreeNode FolderNode(string text, string path)
    {
        var n = new TreeNode(text) { Tag = path, ToolTipText = path };
        if (HasSubdirectories(path)) n.Nodes.Add(new TreeNode("…"));
        return n;
    }

    private static bool HasSubdirectories(string path)
    {
        try { return Directory.Exists(path) && Directory.EnumerateDirectories(path).Any(); }
        catch (Exception) { return false; }
    }

    private void FillChildren(TreeNode node)
    {
        string path = node.Tag as string;
        if (path == null || node.Nodes.Count != 1 || node.Nodes[0].Tag != null) return;
        node.Nodes.Clear();
        try
        {
            foreach (var d in Directory.GetDirectories(path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var info = new DirectoryInfo(d);
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                node.Nodes.Add(FolderNode(info.Name, d));
            }
        }
        catch (Exception) { }
    }

    // ---------------------------------------------------------------- 파일 목록

    private void ShowFolder(string dir)
    {
        list.BeginUpdate();
        list.Items.Clear();
        folderLabel.Text = dir ?? "";
        try { if (dir != null) dir = Path.GetFullPath(dir); } catch (Exception) { }
        if (dir != null && Directory.Exists(dir))
        {
            try
            {
                var files = Directory.GetFiles(dir);
                var names = new HashSet<string>(files.Select(f => Path.GetFileName(f).ToUpperInvariant()));
                foreach (var f in files.OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
                {
                    if (KindOf(f) == MusicFileKind.None) continue;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    string stem = Path.GetFileNameWithoutExtension(f).ToUpperInvariant();
                    var it = new ListViewItem(Path.GetFileName(f)) { Tag = f, ToolTipText = f };
                    it.SubItems.Add(CachedTitle(f) ?? "");
                    it.SubItems.Add(ext.TrimStart('.').ToUpperInvariant());
                    it.SubItems.Add(names.Contains(stem + ".ISS") ? "●" : "");
                    it.SubItems.Add(names.Contains(stem + ".BNK") ? "●" : "");
                    if (string.Equals(f, currentPath, StringComparison.OrdinalIgnoreCase)) it.Font = new Font(list.Font, FontStyle.Bold);
                    list.Items.Add(it);
                }
            }
            catch (Exception ex) { AppLog.Warn("파일", "폴더를 읽을 수 없습니다: " + dir, ex.Message); }
        }
        list.EndUpdate();
        folderLabel.Text = (dir ?? "") + "   (" + list.Items.Count + "곡)";
        LoadTitles();
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
            if (KindOf((string)it.Tag) != MusicFileKind.Audio && CachedTitle((string)it.Tag) == null) todo.Add((string)it.Tag);
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
            if (titles.TryGetValue((string)it.Tag, out var t)) it.SubItems[1].Text = t;
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
        if (disposing) tips.Dispose();
        base.Dispose(disposing);
    }
}
