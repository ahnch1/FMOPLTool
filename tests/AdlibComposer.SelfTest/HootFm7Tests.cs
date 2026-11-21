using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot FM-7 · FM77AV (HootFm7, type generic): 기계 단위 시험 · 실제 zip (Hootarchive/fm7, HOOT_SONGS = 곡마다).</summary>
public static class HootFm7Tests
{
    public static void Run(Action<string, bool, string> check)
    {
        var noTitles = HootArchive.Parse("<game><name>X</name><driver type=\"generic\">fm7</driver><romlist archive=\"x\"><rom type=\"bgm\" offset=\"0x00\">MUS00.BIN</rom><rom type=\"bgm\" offset=\"0x01\">MUS01.BIN</rom></romlist><titlelist></titlelist></game>");
        check("hoot entry without titles or range: bgm files become the song list", noTitles.Titles.Count == 2 && noTitles.Titles[1].Code == 1 && noTitles.Titles[1].Title == "MUS01", string.Join(",", noTitles.Titles));
        check("hoot fm7 / fm77av generic: playable, own machine", HootArchive.Playable("fm7", "generic") && HootArchive.Playable("fm77av", "generic") && HootFm7.Handles("FM77AV", "Generic") && !HootFm7.Handles("fm7", "xanadu"), "");

        // 기계: 시작 코드가 OPN에 쓰기 (FD16h 자료, FD15h 명령 3 주소 · 2 쓰기) → 칩 기록, 타이머 IRQ (FD02h bit2) → FD03h bit2 = 0
        var m = new HootFm7.Machine();
        int[] code =
        {
            0x86, 0x28, 0xB7, 0xFD, 0x16, 0x86, 0x03, 0xB7, 0xFD, 0x15,   // LDA #28h, STA FD16h, LDA #3, STA FD15h (주소 28h)
            0x86, 0xF0, 0xB7, 0xFD, 0x16, 0x86, 0x02, 0xB7, 0xFD, 0x15,   // LDA #F0h, STA FD16h, LDA #2, STA FD15h (키온)
            0x86, 0x04, 0xB7, 0xFD, 0x02,                                 // 타이머 IRQ 켬
            0x8E, 0x01, 0x00, 0xBF, 0xFF, 0xF8,                           // IRQ 벡터 = 0100h
            0x1C, 0xEF, 0x20, 0xFE,                                       // CLI, BRA *
        };
        for (int i = 0; i < code.Length; i++) m.Ram[i] = (byte)code[i];
        int[] irq = { 0xB6, 0xFD, 0x03, 0xB7, 0x02, 0x00, 0x7C, 0x02, 0x01, 0x3B };   // LDA FD03h, STA 0200h, INC 0201h, RTI
        for (int i = 0; i < irq.Length; i++) m.Ram[0x100 + i] = (byte)irq[i];
        m.Cpu.PC = 0; m.Cpu.S = 0xFC00; m.Cpu.CC = 0x50;
        m.Play(0);
        m.Run((long)(HootFm7.CpuClock * 0.1));
        check("hoot fm7 machine: OPN key-on via FD15h/FD16h, 2 ms timer IRQ (FD03h bit2 = 0)", m.Log.Any(x => x.chip == 1 && x.reg == 0x28 && x.val == 0xF0) && m.Ram[0x201] >= 40 && (m.Ram[0x200] & 4) == 0,
            "irqs " + m.Ram[0x201] + ", FD03 " + m.Ram[0x200].ToString("X2"));

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "fm7");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot fm7 real files (_incoming/Hootarchive/fm7 not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        int entries = 0, sound = 0;
        foreach (var zip in Directory.GetFiles(folder, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string dir = ZipFolder.Extract(zip);
            foreach (var hoot in Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).Where(x => HootFm7.Handles(x.Driver, x.Type)))
            {
                entries++;
                var codes = Codes(hoot);
                var r = (sound: false, note: "no songs");
                int first = -1;
                foreach (var c in codes.Take(all ? codes.Length : 3)) { var t = Measure(hoot, dir, c, 8); if (first < 0) { first = c; r = t; } if (t.sound && !r.sound) { r = t; first = c; } if (all) Console.WriteLine("  " + Path.GetFileName(zip) + " 0x" + c.ToString("X") + " " + t.note); if (r.sound && !all) break; }
                if (r.sound) sound++;
                Console.WriteLine((r.sound ? "  sound " : "  quiet ") + Path.GetFileName(zip) + " | " + hoot.Name + " | 0x" + first.ToString("X") + " " + r.note);
            }
        }
        check("hoot fm7 real entries: most make sound in 8 s", entries > 0 && sound * 10 >= entries * 8, sound + " / " + entries);
    }

    /// <summary>곡 코드: titlelist, 없으면 bgm 번호.</summary>
    public static int[] Codes(HootArchive.Entry e) { return e.Titles.Select(t => t.Code).ToArray(); }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootFm7.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOns = m.Log.Count(x => x.t > 0 && x.chip >= 1 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int psg = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        int ssg = m.Log.Count(x => x.t > 0 && x.chip >= 1 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        string note = "key-ons " + keyOns + ", psg " + psg + ", ssg " + ssg + ", writes " + m.Log.Count + ", irqs " + m.Irqs + ", cmds " + m.Commands + ", loads " + m.Loads + ", PC " + m.Cpu.PC.ToString("X4") + " unk " + m.Cpu.UnknownOps
            + (m.OtherWrites.Count > 0 ? ", w " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(4).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", r " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(4).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (keyOns + psg + ssg > 0, note);
    }
}
