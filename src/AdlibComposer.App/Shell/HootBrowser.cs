using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AdlibComposer.App.Shell;

/// <summary>
/// Hoot 페이지의 왼쪽 목록: 위 [목록 고르기 상자 (설정의 hoot 목록 폴더들) · 설정] + 경로 줄 + hoot 트리 (<see cref="HootTree"/>).
/// <para>처음 Hoot 페이지를 열 때 (<see cref="EnsureLoaded"/>) 비로소 목록 xml을 읽고 zip 색인을 만든다 — 프로그램 시작 때는 아무것도 읽지 않는다.
/// 아카이브 zip은 설정의 데이터 폴더 아래에서 찾는다 (비면 목록 폴더의 위 폴더).</para>
/// <para>Jukebox와 같은 JukeboxView가 이 목록에 붙는다 (<see cref="ISongList"/>): 다음 곡 = 재생 중인 게임의 곡 순서.</para>
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class HootBrowser : UserControl, ISongList
{
    private readonly Func<AppSettings> settings;
    private readonly HootTree tree = new HootTree { Dock = DockStyle.Fill };
    private readonly ComboBox listBox = new ComboBox();
    private readonly Button settingsButton = new Button();
    private readonly Label pathLabel = new Label();
    private readonly ToolTip tips = new ToolTip();
    private List<string> folders = new List<string>();
    private string loadedList, loadedData;
    private bool filling;

    /// <summary>곡 재생 요청 ("항목.hoot::n").</summary>
    public event Action<string> SongActivated;
    /// <summary>아직 재생하지 못하는 기종: 그 zip을 Jukebox의 폴더 목록에서 열어 달라.</summary>
    public event Action<string> ZipRequested;
    /// <summary>[설정]: Preferences를 열어 달라 (Hoot 칸).</summary>
    public event Action PreferencesRequested;

    public HootBrowser(Func<AppSettings> settings)
    {
        this.settings = settings;
        BackColor = Color.White;

        var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = 2, Padding = new Padding(6, 5, 6, 0), BackColor = Color.FromArgb(246, 247, 249) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        listBox.DropDownStyle = ComboBoxStyle.DropDownList;
        listBox.Font = ShellTheme.Ui;
        listBox.Dock = DockStyle.Fill;
        listBox.Margin = new Padding(0, 1, 6, 0);
        listBox.SelectedIndexChanged += delegate { if (!filling) PickList(); };
        tips.SetToolTip(listBox, "hoot 목록 폴더 (Preferences → Hoot에서 더하고 뺀다)");
        settingsButton.Text = " 설정";
        settingsButton.Image = GlyphImages.Get(Glyphs.Settings, 16, Color.FromArgb(50, 54, 62));
        settingsButton.ImageAlign = ContentAlignment.MiddleLeft;
        settingsButton.TextImageRelation = TextImageRelation.ImageBeforeText;
        settingsButton.FlatStyle = FlatStyle.Flat;
        settingsButton.FlatAppearance.BorderColor = Color.FromArgb(210, 214, 220);
        settingsButton.BackColor = Color.White;
        settingsButton.Font = ShellTheme.Ui;
        settingsButton.AutoSize = true;
        settingsButton.Margin = new Padding(0);
        settingsButton.Click += delegate { PreferencesRequested?.Invoke(); };
        tips.SetToolTip(settingsButton, "Preferences: hoot 목록 폴더들 · 데이터 폴더 (아카이브 zip을 찾는 곳)");
        bar.Controls.Add(listBox, 0, 0);
        bar.Controls.Add(settingsButton, 1, 0);

        pathLabel.Dock = DockStyle.Top;
        pathLabel.Height = 24;
        pathLabel.Font = ShellTheme.Ui;
        pathLabel.ForeColor = ShellTheme.Muted;
        pathLabel.AutoEllipsis = true;
        pathLabel.Padding = new Padding(8, 4, 4, 0);
        pathLabel.Text = "hoot";

        tree.SongActivated += p => SongActivated?.Invoke(p);
        tree.ZipRequested += z => ZipRequested?.Invoke(z);
        tree.SummaryChanged += delegate { pathLabel.Text = tree.Summary; tips.SetToolTip(pathLabel, (tree.ListDir ?? "") + (tree.DataRoot != null ? "\n데이터 폴더: " + tree.DataRoot : "")); };

        Controls.Add(tree);
        Controls.Add(pathLabel);
        Controls.Add(bar);
    }

    /// <summary>읽었는가 (처음 Hoot 페이지를 연 뒤).</summary>
    public bool Loaded { get { return loadedList != null; } }

    /// <summary>
    /// Hoot 페이지를 열 때: 설정의 목록 폴더들로 고르기 상자를 채우고, 아직 안 읽었거나 고른 목록 · 데이터 폴더가 바뀌었으면 읽는다.
    /// </summary>
    public void EnsureLoaded()
    {
        var s = settings();
        var paths = s.HootXmlFolderPaths;
        if (!paths.SequenceEqual(folders, StringComparer.OrdinalIgnoreCase)) FillBox(paths, s.HootFolderPath);
        if (folders.Count == 0)
        {
            loadedList = null;
            tree.ShowStatus("hoot 목록 폴더가 없습니다.\n[설정] (Preferences → Hoot)에서 xml · xml_new … 폴더와\n데이터 폴더 (아카이브 zip이 있는 hoot 폴더)를 정하세요.");
            pathLabel.Text = "hoot: 목록 폴더 없음";
            return;
        }
        PickList();
    }

    private void FillBox(List<string> paths, string last)
    {
        filling = true;
        folders = paths;
        listBox.Items.Clear();
        foreach (var p in paths) listBox.Items.Add(Label(p));
        int pick = last == null ? 0 : Math.Max(0, paths.FindIndex(p => string.Equals(p, last, StringComparison.OrdinalIgnoreCase)));
        if (paths.Count > 0) listBox.SelectedIndex = pick;
        filling = false;
    }

    /// <summary>상자에 보일 이름: "xml_new  (D:\hoot)".</summary>
    private static string Label(string path)
    {
        string t = path.TrimEnd('\\');
        string parent = Path.GetDirectoryName(t);
        return Path.GetFileName(t) + (parent != null ? "  (" + parent + ")" : "");
    }

    /// <summary>고른 목록을 읽는다 (같은 목록 · 같은 데이터 폴더면 그대로).</summary>
    private void PickList()
    {
        int i = listBox.SelectedIndex;
        if (i < 0 || i >= folders.Count) return;
        string dir = folders[i];
        string data = settings().HootDataFolderPath;
        if (string.Equals(dir, loadedList, StringComparison.OrdinalIgnoreCase) && string.Equals(data ?? "", loadedData ?? "", StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(dir))
        {
            tree.ShowStatus("hoot 목록 폴더가 없습니다:\n" + dir);
            pathLabel.Text = "hoot: 폴더 없음";
            loadedList = null;
            return;
        }
        loadedList = dir;
        loadedData = data;
        tree.Open(dir, data);
        try { var s = settings(); string stored = AppSettings.ToStored(dir); if (s.HootFolder != stored) { s.HootFolder = stored; s.Save(); } }
        catch (Exception ex) { AppLog.Warn("hoot", "설정에 고른 목록을 남기지 못했습니다", ex.Message); }
    }

    // ---------------------------------------------------------------- ISongList

    public List<string> Paths { get { return tree.PlaylistPaths; } }
    public string SelectedPath { get { return null; } }   // 트리의 곡 줄은 두 번 클릭해야 zip을 푼다
    public void MarkCurrent(string path) { tree.MarkCurrent(path); }
    public void FocusList() { tree.FocusTree(); }
    public bool ListFocused { get { return tree.TreeFocused; } }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tips.Dispose();
        base.Dispose(disposing);
    }
}
