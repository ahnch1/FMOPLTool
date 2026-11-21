using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using Android.Content;

namespace AdlibComposer.Droid;

/// <summary>
/// Jukebox 설정 (앱 설정 파일에 남김): 모노 / 스테레오 · 거친 음색 · 그 시절 스피커 · 원래 소리 / 8-bit · MIDI 모듈 · 표시 방식 · 노래방 그림 폴더 · Cyberfunk 층 · resource 폴더.
/// 바꾸면 <see cref="Changed"/> (가로 데크의 상자들이 맞춘다).
/// </summary>
internal static class JukeboxPrefs
{
    /// <summary>표시 방식 (Windows와 같은 순서): Implay = 가사 + 믹서, Ocplay = 가사 + 작은 악보, 노래방 = 그림 + 가사, Cyberfunk = 별 · 도형 + 가사.</summary>
    public static readonly string[] ViewModeNames = { "Implay", "Ocplay", "노래방", "노래방(Cyberfunk)" };
    public const int Implay = 0, Ocplay = 1, Karaoke = 2, Cyber = 3;

    /// <summary>resource 폴더에서 쓰는 파일 (없는 파일은 APK에 든 것).</summary>
    public static readonly string[] ResourceFiles = { "STANDARD.BNK", "GENMIDI.OP2", "systems.json" };

    private static ISharedPreferences prefs;

    public static bool RawTone { get; private set; }
    public static bool VintageSpeaker { get; private set; }
    /// <summary>true = 모노 (왼쪽 · 오른쪽을 섞음), false = 스테레오.</summary>
    public static bool Mono { get; private set; }
    /// <summary>-1 = 원래 소리 (OPL FM), 0~ = <see cref="Player.JukeboxSession.ChipStyles"/> 번호.</summary>
    public static int Chip { get; private set; } = -1;
    public static bool Mt32 { get; private set; }
    public static int ViewMode { get; private set; }
    /// <summary>곡 폴더에 그림이 없을 때 쓸 노래방 그림 폴더 (null = 없음).</summary>
    public static string KaraokeFolder { get; private set; }
    /// <summary>사용자 resource 폴더 (null = APK에 든 것만).</summary>
    public static string ResourceFolder { get; private set; }
    public static string CyberLayers { get; private set; } = "1111";

    public static event Action Changed;

    public static void Load(Context ctx)
    {
        if (prefs != null) return;
        prefs = ctx.GetSharedPreferences("jukebox", FileCreationMode.Private);
        RawTone = prefs.GetBoolean("rawTone", false);
        VintageSpeaker = prefs.GetBoolean("vintageSpeaker", false);
        Mono = prefs.GetBoolean("mono", false);
        Chip = Math.Max(-1, Math.Min(Player.JukeboxSession.ChipStyles.Length - 1, prefs.GetInt("chip", -1)));
        Mt32 = prefs.GetBoolean("mt32", false);
        ViewMode = Math.Max(0, Math.Min(ViewModeNames.Length - 1, prefs.GetInt("viewMode", 0)));
        KaraokeFolder = prefs.GetString("karaokeFolder", null);
        ResourceFolder = prefs.GetString("resourceFolder", null);
        CyberLayers = prefs.GetString("cyberLayers", "1111");
    }

    /// <summary>소리 설정을 엔진 · 재생 판에 (재생 중이면 같은 자리에서 새 소리로).</summary>
    public static void ApplySound()
    {
        var s = AppEnv.Session;
        s.RawTone = RawTone;
        s.VintageSpeaker = VintageSpeaker;
        s.Mono = Mono;
        s.SetSound(Chip >= 0 ? Player.JukeboxSession.ChipStyles[Chip] : (ChipSound?)null, Mt32);
    }

    public static void SetRawTone(bool v) { RawTone = v; Save(e => e.PutBoolean("rawTone", v)); ApplySound(); }
    public static void SetVintageSpeaker(bool v) { VintageSpeaker = v; Save(e => e.PutBoolean("vintageSpeaker", v)); ApplySound(); }
    public static void SetMono(bool v) { Mono = v; Save(e => e.PutBoolean("mono", v)); ApplySound(); }
    public static void SetChip(int chip) { Chip = chip; Save(e => e.PutInt("chip", chip)); ApplySound(); }
    public static void SetMt32(bool v) { Mt32 = v; Save(e => e.PutBoolean("mt32", v)); ApplySound(); }
    public static void SetViewMode(int m) { ViewMode = m; Save(e => e.PutInt("viewMode", m)); }
    public static void SetCyberLayers(string l) { CyberLayers = l; Save(e => e.PutString("cyberLayers", l)); }
    public static void SetKaraokeFolder(string dir) { KaraokeFolder = dir; Save(e => e.PutString("karaokeFolder", dir)); }

    public static void SetResourceFolder(string dir)
    {
        ResourceFolder = dir;
        Save(e => e.PutString("resourceFolder", dir));
        AppEnv.ApplyResourceFolder();
    }

    /// <summary>resource 파일 하나의 실제 경로: 사용자 폴더에 있으면 그것 (대소문자 무시), 없으면 APK에 든 것.</summary>
    public static string ResourceFile(string name, out bool user)
    {
        user = false;
        string dir = ResourceFolder;
        try
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                var f = Directory.GetFiles(dir).FirstOrDefault(x => string.Equals(Path.GetFileName(x), name, StringComparison.OrdinalIgnoreCase));
                if (f != null) { user = true; return f; }
            }
        }
        catch (Exception) { }
        return Path.Combine(AppEnv.ResourceDir, name);
    }

    private static void Save(Action<ISharedPreferencesEditor> set)
    {
        if (prefs != null)
        {
            var e = prefs.Edit();
            set(e);
            e.Apply();
        }
        Changed?.Invoke();
    }
}
