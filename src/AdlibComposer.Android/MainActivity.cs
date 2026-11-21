using System;
using System.IO;
using AdlibComposer.Player;
using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace AdlibComposer.Droid;

/// <summary>
/// A2 시험 화면: 내장 데모 곡(demo.rol + demo.bnk)을 재생해 엔진 · AudioTrack 출력을 확인한다. (A3에서 Jukebox 화면으로 바뀐다)
/// </summary>
[Activity(Label = "@string/app_name", MainLauncher = true, Theme = "@android:style/Theme.Material", ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize | Android.Content.PM.ConfigChanges.ScreenLayout)]
public class MainActivity : Activity
{
    private TextView title, info, pos;
    private Button playButton;
    private readonly Handler handler = new Handler(Looper.MainLooper);
    private Action tick;

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AppEnv.Init(this);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));
        title = new TextView(this) { TextSize = 24 };
        info = new TextView(this) { TextSize = 14 };
        pos = new TextView(this) { TextSize = 32 };
        root.AddView(title);
        root.AddView(info);
        root.AddView(pos);

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var demo = new Button(this) { Text = "데모 곡" };
        playButton = new Button(this) { Text = "재생" };
        var stop = new Button(this) { Text = "정지" };
        row.AddView(demo);
        row.AddView(playButton);
        row.AddView(stop);
        root.AddView(row);
        SetContentView(root);

        demo.Click += delegate { LoadDemo(); };
        playButton.Click += delegate { AppEnv.Session.Toggle(); Refresh(); };
        stop.Click += delegate { AppEnv.Session.Stop(); Refresh(); };

        string err = AppEnv.OpenAudio();
        info.Text = err != null ? "오디오 장치를 열 수 없습니다: " + err : "v" + typeof(AppEnv).Assembly.GetName().Version;
        tick = () => { Refresh(); handler.PostDelayed(tick, 100); };
        handler.Post(tick);
    }

    private void LoadDemo()
    {
        var s = AppEnv.Session;
        if (!s.Load(Path.Combine(AppEnv.DemoDir, "demo.rol"), out string err)) { info.Text = "열 수 없음: " + err; return; }
        s.Play();
        Refresh();
    }

    private void Refresh()
    {
        var s = AppEnv.Session;
        if (s.Update()) s.Play();   // 시험 화면: 끝나면 다시
        title.Text = s.HasSong ? s.Title : "곡 없음";
        if (s.HasSong) info.Text = s.Info;
        pos.Text = Clock(s.PositionSeconds) + " / " + Clock(s.LengthSeconds);
        playButton.Text = s.State == PlayState.Playing ? "일시정지" : "재생";
    }

    private static string Clock(double sec)
    {
        int t = (int)Math.Max(0, sec);
        return (t / 60).ToString("0") + ":" + (t % 60).ToString("00");
    }

    private int Dp(int v) { return (int)(v * Resources.DisplayMetrics.Density + 0.5f); }

    protected override void OnDestroy()
    {
        handler.RemoveCallbacks(tick);
        base.OnDestroy();
    }
}
