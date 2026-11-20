using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>6801 계열 CPU (합성) · hoot 남코 System 86 (HootNamcoSys86): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootNamcoSys86Tests
{
    public static readonly string[] Archives = { "skykiddx", "roishtar", "genpeitd", "rthunder", "wndrmomo", "drgnbstr", "pacland", "aliensec", "metrocrs", "skykid", "hopmappy" };

    private sealed class FlatBus : ICpu6801Bus
    {
        public readonly byte[] M = new byte[0x10000];
        public byte Read(int a) { return M[a & 0xFFFF]; }
        public void Write(int a, byte v) { M[a & 0xFFFF] = v; }
        public byte PortRead(int port) { return 0xFF; }
        public void PortWrite(int port, byte v) { }
    }

    // LDS · MUL · STD · PSHX/PULX · ADDD · OIM · JSR/RTS, 그 뒤 OCR = 0100h · EOCI · CLI · BRA *; OCF 처리기 = INC $0098 · TCSR 읽고 OCR 다시 씀 · RTI
    private static readonly byte[] Program =
    {
        0x8E, 0x00, 0xFF, 0x86, 0x12, 0xC6, 0x34, 0x3D, 0xDD, 0x90, 0xCE, 0x12, 0x34, 0x3C, 0xCE, 0x00, 0x00, 0x38, 0xDF, 0x92, 0xC3, 0x01, 0x00, 0xDD, 0x94, 0x72, 0x80, 0x96, 0xBD, 0xF0, 0x2B,
        0xCC, 0x01, 0x00, 0xDD, 0x0B, 0x86, 0x08, 0x97, 0x08, 0x0E, 0x20, 0xFE, 0x86, 0x55, 0x97, 0x97, 0x39, 0x7C, 0x00, 0x98, 0x96, 0x08, 0xDC, 0x0B, 0xDD, 0x0B, 0x3B,
    };

    public static void Run(Action<string, bool, string> check)
    {
        var bus = new FlatBus();
        Array.Copy(Program, 0, bus.M, 0xF000, Program.Length);
        bus.M[0xFFFE] = 0xF0; bus.M[0xFFFF] = 0x00; bus.M[0xFFF4] = 0xF0; bus.M[0xFFF5] = 0x30;
        var cpu = new Cpu6801(bus);
        cpu.Reset();
        for (int i = 0; i < 200 && cpu.PC != 0xF029; i++) cpu.Step();
        check("6801: MUL · STD · PSHX/PULX · ADDD · OIM · JSR", cpu.PC == 0xF029 && bus.M[0x90] == 0x03 && bus.M[0x91] == 0xA8 && bus.M[0x92] == 0x12 && bus.M[0x93] == 0x34 && bus.M[0x94] == 0x04 && bus.M[0x95] == 0xA8 && bus.M[0x96] == 0x80 && bus.M[0x97] == 0x55 && cpu.SP == 0xFF && cpu.UnknownOps == 0,
            "PC " + cpu.PC.ToString("X4") + " SP " + cpu.SP.ToString("X4") + " " + BitConverter.ToString(bus.M, 0x90, 8));
        long end = cpu.Cycles + 300000;
        while (cpu.Cycles < end) { cpu.TimerIrq(); cpu.Step(); }
        check("6801: OCF timer interrupt every 65536 E cycles", bus.M[0x98] >= 4 && bus.M[0x98] <= 5, "count " + bus.M[0x98]);
        check("hoot namco system86: playable, own board, YM2151 finder", HootArchive.Playable("namco", "system86") && HootNamcoSys86.Handles("Namco", "System86") && !HootNamcoSys86.Handles("namco", "system1"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot namco system86 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot namco system86 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootNamcoSys86.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot namco system86 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot namco system86 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    private static string SlotList(HootNamcoSys86.Machine m)
    {
        int ae = (m.Mem[0xAE] << 8) | m.Mem[0xAF];
        int lst = (m.Mem[(ae + 0x46) & 0xFFFF] << 8) | m.Mem[(ae + 0x47) & 0xFFFF];
        int typ = (m.Mem[(ae + 0x42) & 0xFFFF] << 8) | m.Mem[(ae + 0x43) & 0xFFFF];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 64 && m.Mem[(lst + i) & 0xFFFF] != 0xFF; i++) { int b = m.Mem[(lst + i) & 0xFFFF]; sb.Append(b.ToString("X2") + ":" + m.Mem[(typ + b) & 0xFFFF] + ","); }
        return "ae " + ae.ToString("X4") + " [" + sb + "]";
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootNamcoSys86.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        int wsg = m.Log.Count(x => x.t > 0 && x.chip == 1 && x.reg >= 0x100 && ((x.reg & 7) == 0 || (x.reg & 7) == 4) && (x.val & 15) != 0);
        string note = "opm " + keyOn + " cus30 vol " + wsg + ", writes " + m.Log.Count + ", irq " + m.Irqs + " ocf " + m.TimerIrqs + ", opm@" + m.Opm.ToString("X4") + " PC " + m.Cpu.PC.ToString("X4") + " unk " + m.Cpu.UnknownOps + (m.Cpu.FirstUnknownPc >= 0 ? "@" + m.Cpu.FirstUnknownPc.ToString("X4") : "")
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        if (Environment.GetEnvironmentVariable("HOOT_DEBUG") != null) note += " 12C5.. " + BitConverter.ToString(m.Mem, 0x12C5, 4) + " req " + BitConverter.ToString(m.Mem, 0x1285, 8) + " bd " + ((m.Mem[0xBD] << 8) | m.Mem[0xBE]).ToString("X4") + " shared " + string.Join(" ", m.SharedReads.OrderByDescending(x => x.Value).Take(10).Select(x => x.Key.ToString("X4") + ":" + x.Value));
        return (keyOn + wsg > 0, note);
    }
}
