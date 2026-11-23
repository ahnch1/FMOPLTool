using System;
using System.IO;
using System.Linq;
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
    /// <summary>있으면 복사하는 자산 (개인용 MT-32 ROM: 빌드한 PC의 _incoming/MT32에 있을 때만 APK에 들어간다).</summary>
    private static readonly string[] OptionalAssets = { "mt32/MT32_CONTROL.ROM", "mt32/MT32_PCM.ROM" };

    /// <summary>APK에 든 MT-32 ROM을 복사한 폴더.</summary>
    public static string Mt32Dir { get; private set; }

    public static void Init(Context ctx)
    {
        if (Engine != null) return;
        FilesDir = ctx.FilesDir.AbsolutePath;
        ResourceDir = Path.Combine(FilesDir, "resource");
        DemoDir = Path.Combine(FilesDir, "demo");
        Mt32Dir = Path.Combine(FilesDir, "mt32");
        CopyAssets(ctx);

        AndroidAudio.Register();
        ZipFolder.CacheBase = Path.Combine(ctx.CacheDir.AbsolutePath, "zip");
        JukeboxPrefs.Load(ctx);

        Engine = new AudioEngine { ToneStandard = true };
        Session = new JukeboxSession(Engine, settings);
        ApplyResourceFolder();
        JukeboxPrefs.ApplySound();
    }

    private static readonly PlayerSettings settings = new PlayerSettings();

    /// <summary>
    /// resource 파일 위치: 설정의 resource 폴더에 있는 파일 (STANDARD.BNK · GENMIDI.OP2 · systems.json)은 그것, 없는 것은 APK에 든 것.
    /// 뱅크는 다음 곡부터, systems.json은 앱을 다시 시작한 뒤부터 (기종 정의는 처음 쓸 때 한 번 읽는다).
    /// </summary>
    public static void ApplyResourceFolder()
    {
        settings.DefaultBankPath = JukeboxPrefs.ResourceFile("STANDARD.BNK", out _);
        settings.MidiBankPath = JukeboxPrefs.ResourceFile("GENMIDI.OP2", out _);
        settings.ResourceFolder = ResourceDir;
        settings.Mt32RomFolder = Mt32RomFolder();
        SystemDefinitions.OverridePath = JukeboxPrefs.ResourceFile("systems.json", out _);
    }

    /// <summary>MT-32 ROM 폴더: 설정의 resource 폴더에 ROM(.rom)이 있으면 그 폴더, 아니면 APK에 든 ROM을 복사한 폴더 (없으면 null).</summary>
    public static string Mt32RomFolder()
    {
        foreach (var dir in new[] { JukeboxPrefs.ResourceFolder, Mt32Dir })
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)
                    && Directory.GetFiles(dir).Any(f => string.Equals(Path.GetExtension(f), ".rom", StringComparison.OrdinalIgnoreCase)))
                    return dir;
            }
            catch (Exception) { }
        }
        return null;
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
        // 앱 버전 + 설치 시각: 버전을 올리지 않고 다시 설치해도 (자산을 고쳐 다시 빌드) 새로 복사한다
        string ver = typeof(AppEnv).Assembly.GetName().Version?.ToString() ?? "";
        try
        {
#pragma warning disable CS0618, CA1422   // GetPackageInfo(string, int): 33+에서 '사용 중단'이지만 그대로 동작
            ver += "|" + ctx.PackageManager.GetPackageInfo(ctx.PackageName, 0).LastUpdateTime;
#pragma warning restore CS0618, CA1422
        }
        catch (Exception) { }
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
        var inApk = ctx.Assets.List("mt32") ?? new string[0];
        foreach (var a in OptionalAssets)
        {
            string dst = Path.Combine(FilesDir, a.Replace('/', Path.DirectorySeparatorChar));
            if (!inApk.Contains(Path.GetFileName(a))) { if (fresh && File.Exists(dst)) File.Delete(dst); continue; }
            if (!fresh && File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            using (var src = ctx.Assets.Open(a))
            using (var fs = File.Create(dst))
                src.CopyTo(fs);
        }
        File.WriteAllText(mark, ver);
    }
}
