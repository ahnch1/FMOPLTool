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
[Activity(Label = "@string/app_name", MainLauncher = true, Theme = "@android:style/Theme.Material.NoActionBar", LaunchMode = LaunchMode.SingleTask,
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
    private FrameLayout listBox;
    /// <summary>가로 화면 데크 (Windows Jukebox 배치). 세로 화면은 deck.</summary>
    private WideDeck wide;
    /// <summary>세로 화면 표시 묶음 (가사 · 표시 방식).</summary>
    private JukeboxStage stageP;
    private TextView speedValue, keyValue;
    private string status = "";
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
        if (err != null) SetStatus("오디오 장치를 열 수 없습니다: " + err);

        tick = () => { Refresh(); handler.PostDelayed(tick, 50); };
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
        bar.AddView(SmallButton("⚙ 설정", delegate { ShowSettings(); }));
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
        listBox = new FrameLayout(this);
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

        // 속도 · 키, MIDI 신디 · 거친 음색 · 그 시절 스피커 (세로 화면)
        var adjust = new FlowRow(this, Dp(16), 0);
        adjust.AddView(PortraitStepper("속도", "느리게", "빠르게", out speedValue, d => { if (!busy) AppEnv.Session.SetSpeed(Math.Round(AppEnv.Session.Options.Speed * 100 + d * 5) / 100.0); }));
        adjust.AddView(PortraitStepper("키", "♂", "♀", out keyValue, d => { if (!busy) AppEnv.Session.SetKey(AppEnv.Session.Options.Transpose + d); }));
        deck.AddView(adjust, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });
        var sound = new FlowRow(this, Dp(16), 0);
        var syncPortraitSound = SoundControls.Add(this, sound, true);
        JukeboxPrefs.Changed += () => syncPortraitSound();
        deck.AddView(sound);

        statusText = DeckText(13, "#E0A060");
        deck.AddView(statusText);
        loadText = DeckText(12, "#6E7784");
        deck.AddView(loadText);
        // 세로 화면 표시 묶음 (목록과 데크 사이): 표시 방식 단추 · 가사 · Implay / Ocplay / 노래방 / Cyberfunk
        stageP = new JukeboxStage(this, () => busy, 92);
        stageP.SetPadding(Dp(10), Dp(8), Dp(10), 0);
        body.AddView(stageP);
        body.AddView(deck, new LinearLayout.LayoutParams(0, 0, 1) { Weight = 1 });

        // 가로 화면 데크 (ApplyOrientation이 deck과 바꿔 끼운다). 다음 곡 자동 · 반복은 두 데크가 같은 값
        wide = new WideDeck(this, () => busy);
        wide.PrevSong += () => PlayNeighbor(-1);
        wide.NextSong += () => PlayNeighbor(1);
        wide.PlayPause += TogglePlay;
        wide.StopPressed += () => { if (!busy) { AppEnv.Session.Stop(); Refresh(); } };
        wide.SettingsRequested += ShowSettings;
        wide.AutoNext.Checked = autoNext.Checked;
        wide.Repeat.Checked = repeat.Checked;
        wide.AutoNext.CheckedChange += (s, e) => { if (autoNext.Checked != e.IsChecked) autoNext.Checked = e.IsChecked; };
        wide.Repeat.CheckedChange += (s, e) => { if (repeat.Checked != e.IsChecked) repeat.Checked = e.IsChecked; };
        autoNext.CheckedChange += (s, e) => { if (wide.AutoNext.Checked != e.IsChecked) wide.AutoNext.Checked = e.IsChecked; };
        repeat.CheckedChange += (s, e) => { if (wide.Repeat.Checked != e.IsChecked) wide.Repeat.Checked = e.IsChecked; };

        // 안드로이드 15+ (targetSdk 35 이상)는 화면을 상태 · 내비게이션 막대 밑까지 그린다 (edge-to-edge 강제)
        // → 막대 크기만큼 안쪽 여백. 그 아래 판은 시스템이 알아서 띄우므로 손대지 않는다.
        if (OperatingSystem.IsAndroidVersionAtLeast(35)) root.SetOnApplyWindowInsetsListener(new SystemBarPadding());
        SetContentView(root);
    }

    /// <summary>상태 막대 · 내비게이션 막대 · 화면 구멍(카메라) 만큼 안쪽 여백.</summary>
    private sealed class SystemBarPadding : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var i = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                v.SetPadding(i.Left, i.Top, i.Right, i.Bottom);
            }
            return insets;
        }
    }

    /// <summary>
    /// 가로 화면 = 목록 | Windows Jukebox 배치 데크 (곡 정보 · 조작 / 채널 믹서 / 건반), 세로 화면 = 목록 위 · 기존 데크 아래.
    /// </summary>
    /// <remarks>세로 화면: 목록 (곡 5줄 높이) / 표시 묶음 (남는 칸 전부) / 데크 (재생 · 속도 · 키 · 소리).</remarks>
    private void ApplyOrientation()
    {
        bool landscape = Resources.Configuration.Orientation == Android.Content.Res.Orientation.Landscape;
        body.RemoveAllViews();
        body.Orientation = landscape ? Orientation.Horizontal : Orientation.Vertical;
        if (landscape)
        {
            body.AddView(listBox, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 34));
            body.AddView(wide, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 66));
        }
        else
        {
            body.AddView(listBox, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ListRowHeight() * 5));
            body.AddView(stageP, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
            body.AddView(deck, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }
        Refresh();
    }

    /// <summary>목록 한 줄 높이 (EntryAdapter 줄: 위아래 12dp + 17sp 글자 한 줄 + 구분선).</summary>
    private int ListRowHeight()
    {
        var probe = new TextView(this) { TextSize = 17, Text = "가" };
        probe.Measure(View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified), View.MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
        return probe.MeasuredHeight + Dp(24) + Math.Max(1, list.DividerHeight);
    }

    /// <summary>세로 데크의 [이름] [−] 값 [+].</summary>
    private View PortraitStepper(string caption, string minus, string plus, out TextView value, Action<int> step)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        var cap = DeckText(15, "#AEB6C2");
        cap.Text = caption;
        cap.SetPadding(0, 0, Dp(6), 0);
        row.AddView(cap);
        row.AddView(SmallButton(minus, delegate { step(-1); Refresh(); }));
        var v = DeckText(16, "#5FD3C6");
        v.Gravity = GravityFlags.Center;
        v.SetMinWidth(Dp(60));
        v.SetTypeface(Typeface.Monospace, TypefaceStyle.Bold);
        row.AddView(v);
        row.AddView(SmallButton(plus, delegate { step(1); Refresh(); }));
        value = v;
        return row;
    }

    /// <summary>상태 글 (여는 중 · zip 푸는 중 · 오류): 세로 데크 아래 줄, 가로 데크는 상태 줄 끝.</summary>
    private void SetStatus(string text)
    {
        status = text ?? "";
        statusText.Text = status;
        if (wide != null) wide.SetStatus(status);
    }

    // ================================================================ 설정 창

    /// <summary>지금 목록이 보여 주는 실제 폴더 (곡 목록 · 없는 폴더면 null).</summary>
    private string CurrentFolder()
    {
        if (places.Count == 0) return null;
        var p = places[places.Count - 1];
        return !p.Tracks && Directory.Exists(p.Path) ? p.Path : null;
    }

    /// <summary>
    /// 설정: 소리 (거친 음색 · 스피커 · 원래 소리 / 8-bit · MIDI 신디 · 모듈), resource 폴더 (기본 = APK에 든 것), 노래방 그림 폴더.
    /// 폴더는 왼쪽 목록에서 그 폴더로 들어간 뒤 [지금 목록 폴더로]를 누른다.
    /// </summary>
    private void ShowSettings()
    {
        float d = Resources.DisplayMetrics.Density;
        int P(int v) => (int)(v * d + 0.5f);
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        box.SetPadding(P(20), P(12), P(20), P(8));

        TextView Head(string t)
        {
            var h = new TextView(this) { Text = t, TextSize = 17 };
            h.SetTypeface(null, TypefaceStyle.Bold);
            h.SetTextColor(Color.ParseColor("#5FD3C6"));
            h.SetPadding(0, P(14), 0, P(4));
            box.AddView(h);
            return h;
        }
        TextView Note(string t)
        {
            var n = new TextView(this) { Text = t, TextSize = 13 };
            n.SetTextColor(Color.ParseColor("#AEB6C2"));
            box.AddView(n);
            return n;
        }

        Head("소리");
        var sound = new FlowRow(this, P(12), 0);
        SoundControls.Add(this, sound);
        box.AddView(sound);
        Note("8-bit = MIDI · ROL · IMS 곡을 옛 게임기 칩 소리로. MT-32 = Munt 에뮬레이터 (ROM: resource 폴더에 .ROM이 있으면 그것, 없으면 APK에 든 것 · " + (AppEnv.Mt32RomFolder() != null ? "있음" : "없음 → MT-32 음색 번호를 GM으로") + ").");

        string here = CurrentFolder();
        string hereText = here != null ? "지금 목록 폴더: " + ShortPath(here) : "지금 목록이 폴더가 아닙니다 (곡 목록 · zip 안이면 폴더로 나와서)";

        Head("resource 폴더");
        var resText = Note("");
        Action showRes = () =>
        {
            var lines = new List<string> { JukeboxPrefs.ResourceFolder == null ? "APK에 든 파일만 씀 (기본)" : "폴더: " + ShortPath(JukeboxPrefs.ResourceFolder) };
            foreach (var f in JukeboxPrefs.ResourceFiles)
            {
                JukeboxPrefs.ResourceFile(f, out bool user);
                lines.Add("  " + f + " — " + (user ? "폴더의 것" : "APK에 든 것"));
            }
            lines.Add("STANDARD.BNK = ROL · IMS 기본 뱅크, GENMIDI.OP2 = MIDI FM 뱅크, systems.json = 기종 정의 (앱을 다시 시작한 뒤 적용)");
            resText.Text = string.Join("\n", lines);
        };
        showRes();
        var resRow = new FlowRow(this, P(8), 0);
        var setRes = SmallButton("지금 목록 폴더로", delegate { if (CurrentFolder() != null) { JukeboxPrefs.SetResourceFolder(CurrentFolder()); showRes(); } });
        setRes.Enabled = here != null;
        resRow.AddView(setRes);
        resRow.AddView(SmallButton("APK에 든 것만", delegate { JukeboxPrefs.SetResourceFolder(null); showRes(); }));
        box.AddView(resRow);

        Head("노래방 그림 폴더");
        var karText = Note("");
        Action showKar = () => karText.Text = "곡 폴더에 그림(JPG · PNG · BMP · GIF)이 없을 때 쓸 폴더: " + (JukeboxPrefs.KaraokeFolder == null ? "없음" : ShortPath(JukeboxPrefs.KaraokeFolder));
        showKar();
        var karRow = new FlowRow(this, P(8), 0);
        var setKar = SmallButton("지금 목록 폴더로", delegate { if (CurrentFolder() != null) { JukeboxPrefs.SetKaraokeFolder(CurrentFolder()); showKar(); wide.ReloadKaraoke(); stageP.ReloadKaraoke(); } });
        setKar.Enabled = here != null;
        karRow.AddView(setKar);
        karRow.AddView(SmallButton("없음", delegate { JukeboxPrefs.SetKaraokeFolder(null); showKar(); wide.ReloadKaraoke(); stageP.ReloadKaraoke(); }));
        box.AddView(karRow);
        Note(hereText);

        var scroll = new ScrollView(this);
        scroll.AddView(box);
        new AlertDialog.Builder(this).SetTitle("설정").SetView(scroll).SetPositiveButton("닫기", (EventHandler<DialogClickEventArgs>)null).Show();
    }

    private static string ShortPath(string p)
    {
        return p.StartsWith(StorageRoot, StringComparison.Ordinal) ? "저장소" + p.Substring(StorageRoot.Length) : p;
    }

    /// <summary>렌더 부하 글 (재생 중일 때만): 소리 블록 하나를 만드는 데 든 시간 ÷ 그 소리 길이.</summary>
    private static string LoadText(bool playing)
    {
        if (!playing) return "";
        double load = AndroidAudioOutput.Load;
        return "렌더 부하 " + (int)Math.Round(load * 100) + "%" + (load > 0.8 ? " — 끊길 수 있음" : "");
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

    /// <summary>위로 갈 곳이 있는가: 들어온 길이 둘 이상이거나, 처음 연 폴더가 저장소 맨 위보다 아래.</summary>
    private bool CanGoUp()
    {
        if (places.Count > 1) return true;
        return places.Count == 1 && RootParent(places[0]) != null;
    }

    /// <summary>처음 연 폴더의 위 폴더 (저장소 맨 위보다 위로는 안 감, 곡 목록 · 데모 · 캐시면 null).</summary>
    private static string RootParent(Place p)
    {
        if (p.Tracks) return null;
        string cur = p.Path.TrimEnd('/');
        if (!cur.StartsWith(StorageRoot, StringComparison.Ordinal) || cur == StorageRoot.TrimEnd('/')) return null;
        return System.IO.Path.GetDirectoryName(cur);
    }

    private void GoUp()
    {
        if (places.Count > 1) { places.RemoveAt(places.Count - 1); ShowPlace(); return; }
        string parent = places.Count == 1 ? RootParent(places[0]) : null;
        if (parent == null) return;
        places[0] = new Place { Path = parent };
        ShowPlace();
    }

    /// <summary>안드로이드 뒤로 (제스처 · ◁ 키): 위로 갈 곳이 있으면 위로, 없으면 앱을 뒤로 보냄 (재생은 계속).</summary>
#pragma warning disable CS0618, CS0672   // OnBackPressed: 안드로이드 13+에서 '사용 중단'이지만 enableOnBackInvokedCallback을 켜지 않으면 그대로 불린다
    public override void OnBackPressed()
    {
        if (CanGoUp()) GoUp();
        else MoveTaskToBack(true);
    }
#pragma warning restore CS0618, CS0672

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
        bool canUp = CanGoUp();
        Task.Run(() =>
        {
            List<FolderEntry> l; string err = null;
            try { l = p.Tracks ? SongFolder.Tracks(p.Path) : SongFolder.List(p.Path, false); }
            catch (Exception ex) { l = new List<FolderEntry>(); err = ex.Message; }
            // 맨 위 [..] 줄 = 위로 (zip · 곡 목록에서도 들어온 곳으로)
            if (canUp) l.Insert(0, new FolderEntry { Kind = EntryKind.Up, Path = "", Name = ".." });
            RunOnUiThread(() =>
            {
                if (places.Count == 0 || places[places.Count - 1] != p) return;   // 그사이 다른 곳으로
                entries = l;
                adapter.Set(l);
                emptyText.Text = err != null ? "읽을 수 없습니다: " + err : l.Count == (canUp ? 1 : 0) ? "곡 · 폴더가 없습니다" : "";
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
        SetStatus("zip 푸는 중: " + e.Name);
        Task.Run(() =>
        {
            string dir = null, err = null;
            try { dir = SongFolder.OpenZip(e.Path); }
            catch (Exception ex) { err = ex.Message; }
            RunOnUiThread(() =>
            {
                busy = false;
                SetStatus(err != null ? "zip을 열 수 없습니다: " + err : "");
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
        SetStatus("여는 중: " + System.IO.Path.GetFileName(AdlibComposer.Audio.ChipSongs.FilePart(path)));
        adapter.Playing = path;
        Task.Run(() =>
        {
            var s = AppEnv.Session;
            bool ok = s.Load(path, out string err);
            if (ok) s.Play();
            RunOnUiThread(() =>
            {
                busy = false;
                SetStatus(ok ? "" : "열 수 없습니다: " + err);
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
        bool nowPlaying = s.State == PlayState.Playing;
        if (wide.Parent != null)
        {
            wide.Update(LoadText(nowPlaying));
            if (nowPlaying != servicePlaying) { servicePlaying = nowPlaying; PlaybackService.Update(this, nowPlaying, s.Title); }
            return;
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
        loadText.Text = LoadText(playing);

        // 세로 화면: 속도 · 키 값, 표시 묶음 (가사 · 믹서 · 표시 방식)
        speedValue.Text = (int)Math.Round(s.Options.Speed * 100) + "%";
        keyValue.Text = WideDeck.KeyText(s.Options.Transpose);
        if (stageP.Parent != null) stageP.Update();
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
