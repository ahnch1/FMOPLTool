using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 타이토 F3 (HootTaitoF3, type f3system): ES5505 칩 · 실제 zip (Hootarchive/roms).</summary>
public static class HootTaitoF3Tests
{
    public static readonly string[] Archives = { "bublbob2", "pbobble2o", "dariusg", "elvactr", "kaiserkn", "groundfx", "gunbustr", "landmakr", "arabianm", "cleopatr" };

    public static void Run(Action<string, bool, string> check)
    {
        // ES5505: 목소리 0, ROM = 톱니 (낱말 0~1023), 주파수 1.0 (FC = 0x0800 → 소수 11비트에서 1 낱말), 되풀이, 음량 FFh
        var c = new Es5505(HootTaitoF3.ChipClock) { Rom = Enumerable.Range(0, 2048).Select(i => (short)((i & 1023) * 64 - 32768)).ToArray() };
        c.Write(15, 0);                       // 쪽 0 = 목소리 0
        c.Write(1, 0x0400);                   // FC: (0x400 & FFFE) << 1 = 0x800 = 1.0
        c.Write(2, 0); c.Write(3, 0);         // 시작 0
        c.Write(4, 0x0003); c.Write(5, 0xFF00);   // 끝 3FFh (정수 1023)
        c.Write(7, 0xFFF0); c.Write(6, 0xFFF0);   // K1 · K2 열림
        c.Write(8, 0xFF00); c.Write(9, 0xFF00);
        c.Write(0, 0x0008);                   // 돎 (멈춤 비트 없음), LPE 되풀이
        int lo = int.MaxValue, hi = int.MinValue;
        for (int i = 0; i < 3000; i++) { c.Step(out int l, out _); lo = Math.Min(lo, l); hi = Math.Max(hi, l); }
        check("ES5505: looping voice plays (swing), stays running, accum wraps inside start..end", hi - lo > 50000 && c.VoiceActive(0) && (c.Read(0) & 3) == 0, "swing " + (hi - lo) + ", CR " + c.Read(0).ToString("X4"));
        c.Write(0, 0x0000);                   // 되풀이 없음 → 끝에서 멈춤
        for (int i = 0; i < 3000; i++) c.Step(out _, out _);
        check("ES5505: non-looping voice stops at end (STOP0)", (c.Read(0) & 1) != 0, c.Read(0).ToString("X4"));

        check("hoot taito f3system: own board", HootTaitoF3.Handles("Taito", "F3System") && !HootTaitoF3.Handles("taito", "f2system"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot taito f3 real files (roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        var archives = all ? Directory.GetFiles(folder, "*.zip").Select(Path.GetFileNameWithoutExtension).Where(a => HootArchive.Index(HootArchive.FindListDir(Path.Combine(folder, a + ".zip")) ?? "").TryGetValue(a, out var gl) && gl.Any(g => g.Contains("type=\"f3system\""))).ToArray() : Archives;
        int entries = 0, sound = 0, songs = 0, songSound = 0;
        foreach (var arc in archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot taito f3 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootTaitoF3.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot taito f3 " + arc + ": .hoot entry", false, "none"); continue; }
            entries++;
            bool any = false;
            string note = "";
            foreach (var t in hoot.Titles.Take(all ? hoot.Titles.Count : 3))
            {
                var r = Measure(hoot, dir, t.Code, 4);
                songs++;
                if (r.sound) songSound++;
                if (all) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + r.note + " | " + t.Title);
                if (r.sound && !any) { any = true; note = "0x" + t.Code.ToString("X2") + " " + r.note; if (!all) break; }
                if (!any) note = "0x" + t.Code.ToString("X2") + " " + r.note;
            }
            if (any) sound++;
            if (!all) check("hoot taito f3 " + arc + ": a song makes sound in 4 s", any, note);
        }
        if (all) Console.WriteLine("  f3 entries " + sound + " / " + entries + ", songs " + songSound + " / " + songs);
        // 게임 이름 형식의 F3 보드 (ringrage · ridingf · gseeker · galastrm): 곡마다
        foreach (var arc in HootTaitoF3.GameTypes)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) continue;
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootTaitoF3.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot taito " + arc + ": .hoot entry", false, "none"); continue; }
            int ok = 0; string first = "";
            foreach (var t in hoot.Titles) { var r = Measure(hoot, dir, t.Code, 4); if (r.sound) ok++; else if (first.Length == 0) first = " (0x" + t.Code.ToString("X2") + " " + r.note + ")"; }
            check("hoot taito " + arc + " (F3 sound board, game-named type): songs make sound", HootArchive.Playable(hoot.Driver, hoot.Type) && ok * 2 >= hoot.Titles.Count, ok + " / " + hoot.Titles.Count + first);
        }
        if (all) return;
        // Jukebox 길: .hoot 곡 → ChipSongs.Load → 스스로 도는 칩 (VgmPlayer)
        string gz = Path.Combine(folder, "groundfx.zip");
        if (!File.Exists(gz)) return;
        string gdir = ZipFolder.Extract(gz);
        string hp = Directory.GetFiles(gdir, "*" + HootArchive.Extension).FirstOrDefault(h => HootTaitoF3.Handles(HootArchive.Load(h).Driver, HootArchive.Load(h).Type));
        var tracks = ChipSongs.Tracks(hp);
        var v = ChipSongs.Load(tracks[0].path);
        var pl = new VgmPlayer(); pl.Start(v, 0, null);
        double sum = 0; int cnt = 44100 * 3;
        for (int i = 0; i < cnt; i++) { pl.Step(); pl.Generate(out double l, out _); sum += l * l; }
        double rmsJ = Math.Sqrt(sum / cnt);
        check("hoot taito f3 groundfx '" + tracks[0].title + "': plays through the jukebox path (self-running ES5505 chip)", rmsJ > 50 && v.SelfRunning != null, "rms " + rmsJ.ToString("0") + ", " + v.SelfRunningName);
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootTaitoF3.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Request(code);
        double sum = 0; int peak = 0, maxVoices = 0, n = (int)(44100 * seconds);
        for (int i = 0; i < n; i++)
        {
            m.Generate(44100, out int l, out int r);
            sum += (double)l * l; peak = Math.Max(peak, Math.Abs(l));
            if ((i & 1023) == 0) maxVoices = Math.Max(maxVoices, Enumerable.Range(0, 32).Count(v => m.Otis.VoiceActive(v)));
        }
        double rms = Math.Sqrt(sum / n);
        return (rms > 30, "rms " + rms.ToString("0") + " peak " + peak + " voices " + maxVoices + ", irqs " + m.Irqs + ", PC " + m.Cpu.PC.ToString("X6") + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : ""));
    }
}
