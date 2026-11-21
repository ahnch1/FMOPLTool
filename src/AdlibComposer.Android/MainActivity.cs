using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AdlibComposer.Player;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace AdlibComposer.Droid;

/// <summary>
/// Jukebox 최소판: 왼쪽(세로 화면이면 위) 폴더 목록, 오른쪽(아래) 재생 데크.
/// <para>목록: 폴더 · zip(눌러서 캐시에 풀고 안으로) · 곡 · 곡 여럿 든 파일(눌러서 곡 목록). 위로 = 들어온 길을 거꾸로 (zip · 곡 목록에서 나오면 원래 폴더).</para>
/// <para>데크: 제목 · 형식 · 게임 정보, 위치 막대(끌어서 이동), 이전 · 재생/일시정지 · 정지 · 다음, 다음 곡 자동 · 반복.</para>
/// <para>곡 열기 · 폴더 읽기는 뒤 스레드에서 (hoot 항목은 곡을 미리 렌더해서 몇 초 걸릴 수 있다). 그동안 화면은 재생 판을 건드리지 않는다.</para>
/// </summary>
[Activity(Label = "@string/app_name", MainLauncher = true, Theme = "@android:style/Theme.Material", LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    private const int StoragePermissionRequest = 1;
    private const string PrefLastFolder = "lastFolder", PrefAutoNext = "autoNext", PrefRepeat = "repeat";

    // 화면
    private LinearLayout body, deck;
    private TextView pathText, emptyText, titleText, infoText, detailText, posText, lenText, statusText;
    private ListView list;
    private EntryAdapter adapter;
    private SeekBar seek;
    private Button playButton;
    private CheckBox autoNext, repeat;
    private bool seekDragging;

    // 목록 상태: 들어온 길 (폴더 · zip을 푼 폴더 · 곡 여럿 든 파일), 마지막이 지금 보는 곳
    private sealed class Place { public string Path; public string Shown; public bool Tracks; }
    private readonly List<Place> places = new List<Place>();
    private List<FolderEntry> entries = new List<FolderEntry>();

    // 재생 상태
    private List<string> playlist = new List<string>();
    private bool busy;
    private readonly Handler handler = new Handler(Looper.MainLooper);
    private Action tick;
    private ISharedPreferences prefs;
    private TextView loadText;
    private bool servicePlaying;

    private static string StorageRoot { get { return Android.OS.Environment.ExternalStorageDirectory.AbsolutePath; } }

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AppEnv.Init(this);
        prefs = GetSharedPreferences("jukebox", FileCreationMode.Private);
        BuildUi();
        ApplyOrientation();

        string err = AppEnv.OpenAudio();
        if (err != null) statusText.Text = "오디오 장치를 열 수 없습니다: " + err;

        tick = () => { Refresh(); handler.PostDelayed(tick, 100); };
        handler.Post(tick);

        // 재생 알림 (안드로이드 13+는 알림 권한을 묻는다 — 거절해도 재생은 된다)
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 2);

        if (HasStorageAccess()) OpenStart();
        else RequestStorageAccess();
    }

    // ================================================================ 화면 만들기

    private void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Color.ParseColor("#1E2228"));

        // 위 막대: [위로] [저장소] [데모] 경로
        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
        bar.AddView(SmallButton("▲ 위로", delegate { GoUp(); }));
        bar.AddView(SmallButton("저장소", delegate { if (HasStorageAccess()) OpenRoot(StorageRoot, "저장소"); else RequestStorageAccess(); }));
        bar.AddView(SmallButton("데모", delegate { OpenRoot(AppEnv.DemoDir, "데모 곡"); }));
        pathText = new TextView(this) { TextSize = 14 };
        pathText.SetTextColor(Color.ParseColor("#AEB6C2"));
        pathText.SetSingleLine(true);
        pathText.Ellipsize = Android.Text.TextUtils.TruncateAt.Start;
        pathText.SetPadding(Dp(8), 0, 0, 0);
        bar.AddView(pathText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        root.AddView(bar);

        body = new LinearLayout(this);
        root.AddView(body, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        // 목록
        var listBox = new FrameLayout(this);
        listBox.SetBackgroundColor(Color.ParseColor("#262B33"));
        list = new ListView(this);
        adapter = new EntryAdapter(this);
        list.Adapter = adapter;
        list.ItemClick += (s, e) => OnEntry(e.Position);
        listBox.AddView(list);
        emptyText = new TextView(this) { TextSize = 16, Gravity = GravityFlags.Center };
        emptyText.SetTextColor(Color.ParseColor("#8A93A0"));
        listBox.AddView(emptyText, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        body.AddView(listBox, new LinearLayout.LayoutParams(0, 0, 1) { Weight = 1 });

        // 데크
        deck = new LinearLayout(this) { Orientation = Orientation.Vertical };
        deck.SetPadding(Dp(20), Dp(16), Dp(20), Dp(16));
        titleText = DeckText(24, "#FFFFFF");
        titleText.Text = "곡 없음";
        infoText = DeckText(14, "#AEB6C2");
        detailText = DeckText(14, "#8A93A0");
        deck.AddView(titleText);
        deck.AddView(infoText);
        deck.AddView(detailText);

        seek = new SeekBar(this) { Max = 1000 };
        seek.SetPadding(Dp(8), Dp(24), Dp(8), Dp(8));
        seek.StartTrackingTouch += delegate { seekDragging = true; };
        seek.StopTrackingTouch += delegate
        {
            seekDragging = false;
            var s = AppEnv.Session;
            if (!busy && s.HasSong) s.Seek(seek.Progress / 1000.0 * s.LengthSeconds);
        };
        seek.ProgressChanged += (o, e) => { if (e.FromUser) posText.Text = Clock(e.Progress / 1000.0 * AppEnv.Session.LengthSeconds); };
        deck.AddView(seek);

        var times = new LinearLayout(this);
        posText = DeckText(18, "#FFFFFF");
        lenText = DeckText(18, "#AEB6C2");
        lenText.Gravity = GravityFlags.End;
        times.AddView(posText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        times.AddView(lenText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        deck.AddView(times);

        var buttons = new LinearLayout(this);
        buttons.SetGravity(GravityFlags.Center);
        buttons.SetPadding(0, Dp(12), 0, Dp(4));
        buttons.AddView(BigButton("⏮", delegate { PlayNeighbor(-1); }));
        playButton = BigButton("▶", delegate { TogglePlay(); });
        buttons.AddView(playButton);
        buttons.AddView(BigButton("■", delegate { if (!busy) { AppEnv.Session.Stop(); Refresh(); } }));
        buttons.AddView(BigButton("⏭", delegate { PlayNeighbor(1); }));
        deck.AddView(buttons);

        var opts = new LinearLayout(this);
        opts.SetGravity(GravityFlags.Center);
        autoNext = new CheckBox(this) { Text = "다음 곡 자동", Checked = prefs.GetBoolean(PrefAutoNext, true) };
        repeat = new CheckBox(this) { Text = "한 곡 반복", Checked = prefs.GetBoolean(PrefRepeat, false) };
        autoNext.CheckedChange += (s, e) => prefs.Edit().PutBoolean(PrefAutoNext, e.IsChecked).Apply();
        repeat.CheckedChange += (s, e) => prefs.Edit().PutBoolean(PrefRepeat, e.IsChecked).Apply();
        opts.AddView(autoNext);
        opts.AddView(repeat);
        deck.AddView(opts);

        statusText = DeckText(13, "#E0A060");
        deck.AddView(statusText);
        loadText = DeckText(12, "#6E7784");
        deck.AddView(loadText);
        body.AddView(deck, new LinearLayout.LayoutParams(0, 0, 1) { Weight = 1 });

        SetContentView(root);
    }

    /// <summary>가로 화면 = 목록 | 데크, 세로 화면 = 목록 위 · 데크 아래.</summary>
    private void ApplyOrientation()
    {
        bool landscape = Resources.Configuration.Orientation == Android.Content.Res.Orientation.Landscape;
        body.Orientation = landscape ? Orientation.Horizontal : Orientation.Vertical;
        for (int i = 0; i < body.ChildCount; i++)
        {
            var v = body.GetChildAt(i);
            v.LayoutParameters = landscape
                ? new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1)
                : v == deck ? new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                : new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1);
        }
    }

    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyOrientation();
    }

    private Button SmallButton(string text, EventHandler click)
    {
        var b = new Button(this) { Text = text, TextSize = 14 };
        b.SetAllCaps(false);
        b.Click += click;
        return b;
    }

    private Button BigButton(string text, EventHandler click)
    {
        var b = new Button(this) { Text = text, TextSize = 26 };
        b.SetMinWidth(Dp(72));
        b.Click += click;
        return b;
    }

    private TextView DeckText(float size, string color)
    {
        var t = new TextView(this) { TextSize = size };
        t.SetTextColor(Color.ParseColor(color));
        return t;
    }

    private int Dp(int v) { return (int)(v * Resources.DisplayMetrics.Density + 0.5f); }

    // ================================================================ 저장소 권한

    private bool HasStorageAccess()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R) return Android.OS.Environment.IsExternalStorageManager;
        return CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage) == Permission.Granted;
    }

    /// <summary>안드로이드 11 이상 = '모든 파일 접근' 설정 화면, 그 아래 = 읽기 권한 묻기.</summary>
    private void RequestStorageAccess()
    {
        emptyText.Text = "곡 폴더를 보려면 저장소 접근을 허용해 주세요.\n(위의 [데모]로 내장 데모 곡은 바로 들을 수 있습니다)";
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            try { StartActivity(new Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission, Android.Net.Uri.Parse("package:" + PackageName))); }
            catch (Exception) { StartActivity(new Intent(Android.Provider.Settings.ActionManageAllFilesAccessPermission)); }
        }
        else RequestPermissions(new[] { Android.Manifest.Permission.ReadExternalStorage }, StoragePermissionRequest);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == StoragePermissionRequest && HasStorageAccess()) OpenStart();
    }

    protected override void OnResume()
    {
        base.OnResume();
        // '모든 파일 접근' 화면에서 돌아왔을 때
        if (places.Count == 0 && HasStorageAccess()) OpenStart();
    }

    // ================================================================ 목록

    /// <summary>처음: 지난번 폴더 (있으면), 아니면 저장소 맨 위.</summary>
    private void OpenStart()
    {
        if (places.Count > 0) return;
        string last = prefs.GetString(PrefLastFolder, null);
        if (last != null && Directory.Exists(last)) OpenRoot(last, null);
        else OpenRoot(StorageRoot, "저장소");
    }

    /// <summary>길을 처음부터 (저장소 · 데모 · 지난 폴더).</summary>
    private void OpenRoot(string dir, string shown)
    {
        places.Clear();
        Enter(new Place { Path = dir, Shown = shown });
    }

    private void Enter(Place p)
    {
        places.Add(p);
        ShowPlace();
    }

    private void GoUp()
    {
        if (places.Count > 1) { places.RemoveAt(places.Count - 1); ShowPlace(); return; }
        if (places.Count == 1 && !places[0].Tracks)
        {
            // 처음 연 폴더의 위 (저장소 맨 위보다 위로는 안 감)
            string cur = places[0].Path;
            string parent = System.IO.Path.GetDirectoryName(cur.TrimEnd('/'));
            if (parent == null || !cur.StartsWith(StorageRoot, StringComparison.Ordinal) || cur.TrimEnd('/') == StorageRoot.TrimEnd('/')) return;
            places[0] = new Place { Path = parent };
            ShowPlace();
        }
    }

    private string ShownPath()
    {
        var parts = new List<string>();
        foreach (var p in places)
        {
            if (p.Shown != null) parts.Add(p.Shown);
            else if (parts.Count == 0) parts.Add(p.Path.StartsWith(StorageRoot, StringComparison.Ordinal) ? "저장소" + p.Path.Substring(StorageRoot.Length) : p.Path);
            else parts.Add(System.IO.Path.GetFileName(p.Path));
        }
        return string.Join(" › ", parts);
    }

    /// <summary>지금 곳을 뒤 스레드에서 읽어 목록에.</summary>
    private void ShowPlace()
    {
        var p = places[places.Count - 1];
        pathText.Text = ShownPath();
        emptyText.Text = "읽는 중…";
        adapter.Set(new List<FolderEntry>());
        bool canUp = places.Count > 1;
        Task.Run(() =>
        {
            List<FolderEntry> l; string err = null;
            try { l = p.Tracks ? SongFolder.Tracks(p.Path) : SongFolder.List(p.Path, false); }
            catch (Exception ex) { l = new List<FolderEntry>(); err = ex.Message; }
            RunOnUiThread(() =>
            {
                if (places.Count == 0 || places[places.Count - 1] != p) return;   // 그사이 다른 곳으로
                entries = l;
                adapter.Set(l);
                emptyText.Text = err != null ? "읽을 수 없습니다: " + err : l.Count == 0 ? "곡 · 폴더가 없습니다" : "";
                list.SetSelection(0);
                if (!p.Tracks && places.Count == 1 && !ZipCache(p.Path)) prefs.Edit().PutString(PrefLastFolder, p.Path).Apply();
            });
        });
    }

    private static bool ZipCache(string dir)
    {
        return dir.StartsWith(AdlibComposer.Formats.ZipFolder.CacheBase, StringComparison.Ordinal);
    }

    private void OnEntry(int index)
    {
        if (index < 0 || index >= entries.Count) return;
        var e = entries[index];
        switch (e.Kind)
        {
            case EntryKind.Up: GoUp(); break;
            case EntryKind.Folder:
                if (places.Count == 1 && !ZipCache(places[0].Path)) { places[0] = new Place { Path = e.Path, Shown = null }; ShowPlace(); }
                else Enter(new Place { Path = e.Path });
                break;
            case EntryKind.Multi: Enter(new Place { Path = e.Path, Shown = e.Name, Tracks = true }); break;
            case EntryKind.Zip: OpenZip(e); break;
            default: PlayPath(e.Path, SongFolder.Playlist(entries)); break;
        }
    }

    /// <summary>zip: 캐시에 풀고 (처음이면 시간이 걸린다) 그 폴더로.</summary>
    private void OpenZip(FolderEntry e)
    {
        if (busy) return;
        busy = true;
        statusText.Text = "zip 푸는 중: " + e.Name;
        Task.Run(() =>
        {
            string dir = null, err = null;
            try { dir = SongFolder.OpenZip(e.Path); }
            catch (Exception ex) { err = ex.Message; }
            RunOnUiThread(() =>
            {
                busy = false;
                statusText.Text = err != null ? "zip을 열 수 없습니다: " + err : "";
                if (dir != null) Enter(new Place { Path = dir, Shown = e.Name });
            });
        });
    }

    // ================================================================ 재생

    /// <summary>곡 열기 + 재생 (뒤 스레드에서 열기).</summary>
    private void PlayPath(string path, List<string> newPlaylist)
    {
        if (busy) return;
        busy = true;
        if (newPlaylist != null) playlist = newPlaylist;
        statusText.Text = "여는 중: " + System.IO.Path.GetFileName(AdlibComposer.Audio.ChipSongs.FilePart(path));
        adapter.Playing = path;
        Task.Run(() =>
        {
            var s = AppEnv.Session;
            bool ok = s.Load(path, out string err);
            if (ok) s.Play();
            RunOnUiThread(() =>
            {
                busy = false;
                statusText.Text = ok ? "" : "열 수 없습니다: " + err;
                if (!ok) adapter.Playing = s.Path;
                Refresh();
            });
        });
    }

    private void TogglePlay()
    {
        if (busy) return;
        var s = AppEnv.Session;
        if (!s.HasSong)
        {
            var first = SongFolder.Playlist(entries);
            if (first.Count > 0) PlayPath(first[0], first);
            return;
        }
        s.Toggle();
        Refresh();
    }

    private void PlayNeighbor(int dir)
    {
        if (busy || playlist.Count == 0) return;
        int i = playlist.IndexOf(AppEnv.Session.Path);
        int next = i < 0 ? 0 : i + dir;
        if (next < 0 || next >= playlist.Count) return;
        PlayPath(playlist[next], null);
    }

    private void Refresh()
    {
        if (busy) return;
        var s = AppEnv.Session;
        if (s.Update())
        {
            // 곡이 끝났다
            if (repeat.Checked) s.Play();
            else if (autoNext.Checked)
            {
                int i = playlist.IndexOf(s.Path);
                if (i >= 0 && i + 1 < playlist.Count) { PlayPath(playlist[i + 1], null); return; }
            }
        }
        titleText.Text = s.HasSong ? s.Title : "곡 없음";
        infoText.Text = s.Info;
        detailText.Text = s.Detail;
        detailText.Visibility = s.Detail.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        double len = s.LengthSeconds, pos = s.PositionSeconds;
        lenText.Text = Clock(len);
        if (!seekDragging)
        {
            seek.Progress = len > 0 ? (int)(pos / len * 1000) : 0;
            posText.Text = Clock(pos);
        }
        seek.Enabled = s.HasSong;
        playButton.Text = s.State == PlayState.Playing ? "❚❚" : "▶";

        // 재생 중이면 전경 서비스 (화면을 꺼도 계속)
        bool playing = s.State == PlayState.Playing;
        if (playing != servicePlaying) { servicePlaying = playing; PlaybackService.Update(this, playing, s.Title); }
        loadText.Text = playing ? "렌더 부하 " + (int)Math.Round(AndroidAudioOutput.Load * 100) + "%" + (AndroidAudioOutput.Load > 0.8 ? " — 끊길 수 있음" : "") : "";
    }

    private static string Clock(double sec)
    {
        int t = (int)Math.Max(0, sec);
        return (t / 60).ToString("0") + ":" + (t % 60).ToString("00");
    }

    protected override void OnDestroy()
    {
        handler.RemoveCallbacks(tick);
        if (IsFinishing && !servicePlaying) PlaybackService.Update(this, false, null);
        base.OnDestroy();
    }

    // ================================================================ 목록 줄

    private sealed class EntryAdapter : BaseAdapter<FolderEntry>
    {
        private readonly Activity owner;
        private List<FolderEntry> items = new List<FolderEntry>();
        private string playing;

        public EntryAdapter(Activity owner) { this.owner = owner; }

        public void Set(List<FolderEntry> l) { items = l; NotifyDataSetChanged(); }
        public string Playing { get { return playing; } set { playing = value; NotifyDataSetChanged(); } }

        public override FolderEntry this[int position] { get { return items[position]; } }
        public override int Count { get { return items.Count; } }
        public override long GetItemId(int position) { return position; }

        public override View GetView(int position, View convertView, ViewGroup parent)
        {
            var row = convertView as LinearLayout;
            TextView name, detail;
            if (row == null)
            {
                float d = owner.Resources.DisplayMetrics.Density;
                row = new LinearLayout(owner);
                row.SetGravity(GravityFlags.CenterVertical);
                row.SetPadding((int)(14 * d), (int)(12 * d), (int)(14 * d), (int)(12 * d));
                name = new TextView(owner) { TextSize = 17 };
                name.SetSingleLine(true);
                name.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
                detail = new TextView(owner) { TextSize = 13 };
                detail.SetTextColor(Color.ParseColor("#8A93A0"));
                detail.SetPadding((int)(8 * d), 0, 0, 0);
                row.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
                row.AddView(detail);
            }
            else { name = (TextView)row.GetChildAt(0); detail = (TextView)row.GetChildAt(1); }
            var e = items[position];
            string icon = e.Kind == EntryKind.Folder || e.Kind == EntryKind.Up ? "📁 " : e.Kind == EntryKind.Zip ? "🗜 " : e.Kind == EntryKind.Multi ? "▸ " : e.Kind == EntryKind.Track ? "   ♪ " : "♪ ";
            name.Text = icon + e.Name;
            bool now = playing != null && e.IsPlayable && e.Path == playing;
            name.SetTextColor(Color.ParseColor(now ? "#FFC860" : e.IsPlayable || e.Kind == EntryKind.Multi ? "#FFFFFF" : "#C8D0DA"));
            name.SetTypeface(null, now ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            detail.Text = e.Detail;
            return row;
        }
    }
}
