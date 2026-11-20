using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 코나미 System GX (HootKonamiGx, 68000 + K054539 × 2): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootKonamiGxTests
{
    public static readonly string[] Archives = { "fantjour", "le2", "opengolf", "puzldama", "racinfrc", "soccerss", "dragoona", "tbyahhoo", "tkmmpzdm", "daiskiss", "rungun2", "rushhero", "salmndr2", "sexyparo", "tokkae", "vsnetscr", "winspike" };

    public static void Run(Action<string, bool, string> check)
    {
                check("hoot konami systemgx: playable, own board", HootArchive.Playable("konami", "systemgx") && HootKonamiGx.Handles("Konami", "SystemGX") && !HootKonamiGx.Handles("konami", "054539"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot konami gx real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot konami gx " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootKonamiGx.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot konami gx " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            if (Environment.GetEnvironmentVariable("HOOT_GXSCAN") != null) { Console.WriteLine("  scan " + arc + ": " + ScanHandshake(hoot, dir)); continue; }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot konami gx " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    /// <summary>진단 (HOOT_GXSCAN=1): 부팅 전 메인 레지스터 0 값을 00h~FFh로 바꿔 0.3초 안에 K054539 쓰기가 생기는 값.</summary>
    public static string ScanHandshake(HootArchive.Entry e, string dir)
    {
        var hits = new System.Collections.Generic.List<string>();
        for (int v = 0; v < 256; v++)
        {
            var m = HootKonamiGx.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; }, 0, v);
            if (m.Log.Count > 0) hits.Add(v.ToString("X2") + ":" + m.Log.Count);
        }
        return string.Join(" ", hits);
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootKonamiGx.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int pcm = m.Log.Count(x => x.t > 0 && x.reg == 0x214 && x.val != 0);
        string note = "pcm " + pcm + ", writes " + m.Log.Count + ", irq cmd " + m.CmdIrqs + " timer " + m.TimerIrqs + ", PC " + m.Cpu.PC.ToString("X6") + " exc " + m.Cpu.LastException
            + ", comm " + string.Join(" ", m.CommReads.OrderBy(x => x.Key).Select(x => x.Key.ToString("X6") + ":" + x.Value))
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X6") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X6") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (pcm > 0, note);
    }
}
