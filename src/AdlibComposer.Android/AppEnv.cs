using System;
using System.IO;
using AdlibComposer.Audio;
using AdlibComposer.Formats;
using AdlibComposer.Player;
using Android.Content;

namespace AdlibComposer.Droid;

/// <summary>
/// 앱 환경: 앱 폴더(FilesDir) 아래 resource · demo, zip 캐시(CacheDir/zip), 엔진 · 재생 판 하나 (앱 전체 공용).
/// 처음 실행할 때 (또는 앱 버전이 바뀌면) APK 안 자산(resource · demo)을 앱 폴더로 복사한다.
/// </summary>
public static class AppEnv
{
    public static string FilesDir { get; private set; }
    public static string ResourceDir { get; private set; }
    public static string DemoDir { get; private set; }
    public static AudioEngine Engine { get; private set; }
    public static JukeboxSession Session { get; private set; }

    private static readonly string[] Assets = { "resource/STANDARD.BNK", "resource/GENMIDI.OP2", "resource/GENMIDI.OP2.LICENSE.txt", "resource/systems.json", "demo/demo.rol", "demo/demo.bnk" };

    public static void Init(Context ctx)
    {
        if (Engine != null) return;
        FilesDir = ctx.FilesDir.AbsolutePath;
        ResourceDir = Path.Combine(FilesDir, "resource");
        DemoDir = Path.Combine(FilesDir, "demo");
        CopyAssets(ctx);

        AndroidAudio.Register();
        SystemDefinitions.OverridePath = Path.Combine(ResourceDir, "systems.json");
        ZipFolder.CacheBase = Path.Combine(ctx.CacheDir.AbsolutePath, "zip");

        Engine = new AudioEngine { ToneStandard = true };
        Session = new JukeboxSession(Engine, new PlayerSettings
        {
            DefaultBankPath = Path.Combine(ResourceDir, "STANDARD.BNK"),
            ResourceFolder = ResourceDir,
        });
    }

    /// <summary>출력 장치 열기 (처음 한 번). 실패하면 까닭.</summary>
    public static string OpenAudio()
    {
        try { Engine.OpenDevice(); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>자산 → 앱 폴더. 앱 버전이 바뀌었을 때만 다시 쓴다 (표시 파일 = 버전).</summary>
    private static void CopyAssets(Context ctx)
    {
        string mark = Path.Combine(FilesDir, ".assets-version");
        string ver = typeof(AppEnv).Assembly.GetName().Version?.ToString() ?? "";
        bool fresh = !File.Exists(mark) || File.ReadAllText(mark).Trim() != ver;
        foreach (var a in Assets)
        {
            string dst = Path.Combine(FilesDir, a.Replace('/', Path.DirectorySeparatorChar));
            if (!fresh && File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            using (var src = ctx.Assets.Open(a))
            using (var fs = File.Create(dst))
                src.CopyTo(fs);
        }
        File.WriteAllText(mark, ver);
    }
}
