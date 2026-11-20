using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 코나미 Z80 + YM2151 + K007232 (HootKonamiK007232, type scontra · thundercross): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootKonamiK007232Tests
{
    public static readonly string[] Archives = { "scontra", "garuka", "crazycop", "thunderx", "thunderxa", "blockhl", "lifefrce", "salamand", "lifefrcej", "hcastle", "citybomb", "kittenk", "crimfght", "aliens", "devilw", "vulcan", "fround", "cuebrickj", "mia" };

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot konami scontra · thundercross · salamander · hcastle · crimfght: playable, own board", HootArchive.Playable("konami", "scontra") && HootArchive.Playable("Konami", "ThunderCross") && HootArchive.Playable("konami", "salamander") && HootArchive.Playable("konami", "hcastle") && HootArchive.Playable("konami", "crimfght") && HootArchive.Playable("konami", "twin16") && !HootKonamiK007232.Handles("konami", "gx400"), "");

        {
            // uPD7759 마스터 모드: 머리 표 (0 = 마지막 번호, 5 · 6 = 주소 ÷ 2) → 블록 40h (256니블) → 00h 끝
            var rom = new byte[0x200];
            rom[0] = 1; rom[7] = 0; rom[8] = 0x10;              // 번호 1 → 주소 20h, 빈 바이트 뒤 21h부터
            rom[0x21] = 0x40; for (int i = 0; i < 128; i++) rom[0x22 + i] = 0x77; rom[0x22 + 128] = 0;
            var core = new AdlibComposer.Audio.Chips.Upd7759Core { Rom = rom };
            core.SetReset(true); core.Data(1); core.SetStart(true); core.SetStart(false);
            int peak = 0; bool busy = false;
            for (int i = 0; i < 4000; i++) { core.Advance(8); busy |= core.Busy; peak = Math.Max(peak, core.Sample); }
            check("uPD7759 master mode: reads its ROM table, plays block, goes idle", busy && peak > 50 && !core.Busy, "peak " + peak + " busy " + core.Busy);
            var v = new AdlibComposer.Audio.Chips.Upd7759(640000, 44100, true);
            v.LoadRom(rom, 0); v.Write(0, 0, 1); v.Write(0, 2, 1); v.Write(0, 1, 1);
            int peak2 = 0;
            for (int i = 0; i < 2000; i++) { v.Generate(out int l, out _); peak2 = Math.Max(peak2, l); }
            check("uPD7759 VGM chip master mode (ROM block 92h) makes sound", peak2 > 1000, "peak " + peak2);
        }

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot konami k007232 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot konami k007232 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootKonamiK007232.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot konami k007232 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot konami k007232 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootKonamiK007232.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && ((x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0) || (x.chip == 2 && x.reg >= 0xB0 && x.reg <= 0xB8 && (x.val & 0x20) != 0)
            || (x.chip == 3 && x.reg == 0x300 && x.val != 0) || (x.chip == 4 && x.reg == 1 && x.val != 0)));
        int pcm = m.Log.Count(x => x.t > 0 && x.chip == 1 && (x.reg == 5 || x.reg == 0x0B));
        string note = "opm " + keyOn + " pcm " + pcm + (m.UsedUpd ? " upd " + m.Log.Count(x => x.chip == 4 && x.reg == 1 && x.val != 0) : "") + ", writes " + m.Log.Count + ", irq " + m.Irqs + " nmi " + m.Nmis + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (keyOn + pcm > 0, note);
    }
}
