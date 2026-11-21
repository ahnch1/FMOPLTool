using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace AdlibComposer.Droid;

/// <summary>
/// 화면을 끄거나 다른 앱으로 가도 소리가 이어지게 하는 전경 서비스 (알림 하나 + 부분 wake lock).
/// 재생을 시작하면 <see cref="Update"/>로 켜고, 정지 · 일시정지하면 끈다. 소리는 엔진의 AudioTrack 스레드가 그대로 내고, 서비스는 프로세스를 살려 두기만 한다.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public class PlaybackService : Service
{
    private const string Channel = "playback";
    private const int NotificationId = 1;
    private const string ExtraTitle = "title";
    private PowerManager.WakeLock wakeLock;
    private static bool running;

    /// <summary>재생 중이면 서비스를 켜고 알림 제목을 바꾼다, 아니면 끈다.</summary>
    public static void Update(Context ctx, bool playing, string title)
    {
        var intent = new Intent(ctx, typeof(PlaybackService));
        if (playing)
        {
            intent.PutExtra(ExtraTitle, title ?? "");
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O) ctx.StartForegroundService(intent);
                else ctx.StartService(intent);
                running = true;
            }
            catch (Exception) { }
        }
        else if (running)
        {
            running = false;
            ctx.StopService(intent);
        }
    }

    public override IBinder OnBind(Intent intent) { return null; }

    public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
    {
        string title = intent?.GetStringExtra(ExtraTitle) ?? "";
        var n = BuildNotification(title);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) StartForeground(NotificationId, n, ForegroundService.TypeMediaPlayback);
        else StartForeground(NotificationId, n);
        if (wakeLock == null)
        {
            var pm = (PowerManager)GetSystemService(PowerService);
            wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "FMOPLJukebox:play");
            wakeLock.SetReferenceCounted(false);
            wakeLock.Acquire();
        }
        return StartCommandResult.NotSticky;
    }

    private Notification BuildNotification(string title)
    {
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        Notification.Builder b;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var nm = (NotificationManager)GetSystemService(NotificationService);
            if (nm.GetNotificationChannel(Channel) == null)
                nm.CreateNotificationChannel(new NotificationChannel(Channel, "재생", NotificationImportance.Low));
            b = new Notification.Builder(this, Channel);
        }
        else b = new Notification.Builder(this);
        return b.SetContentTitle(title.Length > 0 ? title : "FMOPL Jukebox")
            .SetContentText("재생 중")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentIntent(open)
            .SetOngoing(true)
            .Build();
    }

    public override void OnDestroy()
    {
        try { if (wakeLock != null && wakeLock.IsHeld) wakeLock.Release(); } catch (Exception) { }
        wakeLock = null;
        running = false;
        base.OnDestroy();
    }
}
