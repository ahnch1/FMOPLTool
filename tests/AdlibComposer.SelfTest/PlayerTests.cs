using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Player;

namespace AdlibComposer.SelfTest;

/// <summary>
/// 안드로이드 Jukebox가 쓰는 플랫폼 없는 재생 묶음 (AdlibComposer.Player): 곡 열기 · 재생 · 일시정지 · 이동 · 끝 알림, 폴더 · zip 목록.
/// 장치 없이 엔진을 직접 렌더링해 본다.
/// </summary>
public static class PlayerTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }

        string samples = SopTests.FindDir("samples");
        if (samples == null || !File.Exists(Path.Combine(samples, "demo.rol"))) { Console.WriteLine("SKIP  player (samples/demo.rol not found)"); return; }

        string root = Path.Combine(Path.GetTempPath(), "FMOPLTool-playertest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            // 같은 이름 BNK는 소문자/대문자를 섞어 둔다 (안드로이드는 대소문자를 가리는 파일 시스템)
            File.Copy(Path.Combine(samples, "demo.rol"), Path.Combine(root, "demo.rol"));
            File.Copy(Path.Combine(samples, "demo.bnk"), Path.Combine(root, "DEMO.BNK"));
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            Directory.CreateDirectory(Path.Combine(root, ".hidden"));
            File.WriteAllText(Path.Combine(root, "readme.txt"), "x");
            string zip = Path.Combine(root, "pack.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                z.CreateEntryFromFile(Path.Combine(samples, "demo.rol"), "songs/demo.rol");
                z.CreateEntryFromFile(Path.Combine(samples, "demo.bnk"), "songs/demo.bnk");
            }

            // ---- 폴더 목록
            var list = SongFolder.List(root, true);
            check("Player list: up · folder · zip · song order",
                list.Count == 4 && list[0].Kind == EntryKind.Up && list[1].Kind == EntryKind.Folder && list[1].Name == "sub"
                && list[2].Kind == EntryKind.Zip && list[3].Kind == EntryKind.Song && list[3].Name == "demo.rol",
                string.Join(", ", list));
            check("Player list: playlist = songs only", SongFolder.Playlist(list).SequenceEqual(new[] { Path.Combine(root, "demo.rol") }));
            check("Player list: IsSong", SongFolder.IsSong("a.VGZ") && SongFolder.IsSong("x.hoot::3") && !SongFolder.IsSong("a.txt"));

            string unz = SongFolder.OpenZip(zip);
            var inner = SongFolder.List(Path.Combine(unz, "songs"), true);
            check("Player zip: extracted folder lists song", inner.Any(e => e.Kind == EntryKind.Song && e.Name == "demo.rol"), string.Join(", ", inner));

            // ---- 재생
            var engine = new AudioEngine();
            var session = new JukeboxSession(engine, new PlayerSettings());
            check("Player: empty state", session.State == PlayState.Empty && !session.HasSong);
            bool bad = session.Load(Path.Combine(root, "readme.txt"), out string err);
            check("Player: unreadable file fails, state unchanged", !bad && err != null && session.State == PlayState.Empty, err ?? "");

            bool ok = session.Load(Path.Combine(root, "demo.rol"), out err);
            check("Player: load ROL", ok && session.Kind == SongKind.Adlib && session.State == PlayState.Stopped, err ?? "");
            check("Player: same-name bank found case-insensitively", session.Info.Contains("DEMO.BNK") && session.Info.Contains("같은 이름"), session.Info);
            double len = session.LengthSeconds;
            check("Player: length > 1s", len > 1, len.ToString("0.00"));

            session.Play();
            var buf = new short[4410 * 2];
            engine.RenderStereo(buf, 4410);
            engine.RenderStereo(buf, 4410);
            int peak = buf.Max(s => Math.Abs((int)s));
            check("Player: playing renders sound", session.State == PlayState.Playing && peak > 100, "peak=" + peak);
            double p1 = session.PositionSeconds;
            check("Player: position advances (~0.2s)", p1 > 0.1 && p1 < 0.4, p1.ToString("0.000"));

            session.Pause();
            engine.RenderStereo(buf, 4410);
            double p2 = session.PositionSeconds;
            check("Player: pause keeps position", session.State == PlayState.Paused && Math.Abs(p2 - p1) < 0.05, p1.ToString("0.000") + " → " + p2.ToString("0.000"));

            session.Seek(1.0);
            check("Player: seek while paused", Math.Abs(session.PositionSeconds - 1.0) < 0.05, session.PositionSeconds.ToString("0.000"));
            session.Play();
            engine.RenderStereo(buf, 4410);
            check("Player: resume from seek", session.PositionSeconds > 1.05 && session.PositionSeconds < 1.3, session.PositionSeconds.ToString("0.000"));

            session.Seek(len - 0.2);
            check("Player: not ended yet", !session.Update());
            for (int i = 0; i < 40 && engine.IsPlaying; i++) engine.RenderStereo(buf, 4410);
            check("Player: end detected once", session.Update() && session.State == PlayState.Stopped && !session.Update());
            check("Player: stopped position = 0", session.PositionSeconds == 0, session.PositionSeconds.ToString("0.000"));

            session.Play();
            session.Stop();
            check("Player: stop", session.State == PlayState.Stopped && !engine.IsPlaying);

            // zip에서 푼 곡: 같은 폴더 뱅크
            ok = session.Load(Path.Combine(unz, "songs", "demo.rol"), out err);
            check("Player: load song from unzipped folder", ok && session.Info.Contains("demo.bnk"), err ?? session.Info);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }
}
