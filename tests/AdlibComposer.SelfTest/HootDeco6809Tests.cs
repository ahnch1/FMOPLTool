using System;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>6809 CPU (합성 프로그램) · hoot 데이터 이스트 6809 보드 (HootDeco6809, type brkthru · exprraid): 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootDeco6809Tests
{
    public static readonly string[] Archives = { "brkthru", "darwin", "exprraid" };

    private sealed class FlatBus : ICpu6809Bus
    {
        public readonly byte[] M = new byte[0x10000];
        public byte Read(int a) { return M[a & 0xFFFF]; }
        public void Write(int a, byte v) { M[a & 0xFFFF] = v; }
    }

    // 1..10 합 (PSHS · ADDA ,S+ · DECB · BNE), MUL, ADDA + DAA, LDX · LDY ,X+ / ,Y+ 베끼기, JSR · RTS, ANDCC로 IRQ 켜고 BRA *; IRQ 처리기 = INC $0016 · RTI
    private static readonly byte[] Program =
    {
        0x86, 0x00, 0xC6, 0x0A, 0x34, 0x04, 0xAB, 0xE0, 0x5A, 0x26, 0xF9, 0xB7, 0x00, 0x10, 0x86, 0x12, 0xC6, 0x34, 0x3D, 0xFD, 0x00, 0x12, 0x86, 0x19, 0x8B, 0x28, 0x19, 0xB7, 0x00, 0x14,
        0x8E, 0x01, 0x00, 0x10, 0x8E, 0x02, 0x00, 0xC6, 0x03, 0xA6, 0x80, 0xA7, 0xA0, 0x5A, 0x26, 0xF9, 0xBD, 0x80, 0x38, 0xB7, 0x00, 0x15, 0x1C, 0xEF, 0x20, 0xFE, 0x86, 0x55, 0x39,
        0x7C, 0x00, 0x16, 0x3B,
    };

    public static void Run(Action<string, bool, string> check)
    {
        var bus = new FlatBus();
        Array.Copy(Program, 0, bus.M, 0x8000, Program.Length);
        bus.M[0xFFFE] = 0x80; bus.M[0xFFFF] = 0x00;     // 리셋 8000h
        bus.M[0xFFF8] = 0x80; bus.M[0xFFF9] = 0x3B;     // IRQ 803Bh
        bus.M[0x100] = 0xA1; bus.M[0x101] = 0xB2; bus.M[0x102] = 0xC3;
        var cpu = new Cpu6809(bus);
        cpu.Reset();
        cpu.S = 0x1000;
        for (int i = 0; i < 400 && cpu.PC != 0x8036; i++) cpu.Step();
        check("6809: loop · MUL · DAA · indexed copy · JSR", cpu.PC == 0x8036 && bus.M[0x10] == 55 && bus.M[0x12] == 0x03 && bus.M[0x13] == 0xA8 && bus.M[0x14] == 0x47
            && bus.M[0x200] == 0xA1 && bus.M[0x202] == 0xC3 && cpu.X == 0x103 && cpu.Y == 0x203 && bus.M[0x15] == 0x55 && cpu.S == 0x1000 && cpu.UnknownOps == 0,
            "PC " + cpu.PC.ToString("X4") + " sum " + bus.M[0x10] + " mul " + bus.M[0x12].ToString("X2") + bus.M[0x13].ToString("X2") + " daa " + bus.M[0x14].ToString("X2") + " S " + cpu.S.ToString("X4"));
        for (int i = 0; i < 10; i++) cpu.Step();
        bool took = cpu.Irq();
        for (int i = 0; i < 10; i++) cpu.Step();
        check("6809: IRQ pushes all, RTI returns", took && bus.M[0x16] == 1 && cpu.PC == 0x8036 && cpu.S == 0x1000 && (cpu.CC & Cpu6809.FlagI) == 0, "S " + cpu.S.ToString("X4") + " CC " + cpu.CC.ToString("X2"));

        check("hoot dataeast brkthru · exprraid: playable, own board", HootArchive.Playable("dataeast", "brkthru") && HootArchive.Playable("DataEast", "ExprRaid") && HootDeco6809.LayoutOf("dataeast", "brkthru") == HootDeco6809.Brkthru && !HootDeco6809.Handles("dataeast", "cobracom"), "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot dataeast 6809 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot dataeast 6809 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootDeco6809.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot dataeast 6809 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot dataeast 6809 " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootDeco6809.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int opn = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int ssg = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        int opl = m.Log.Count(x => x.t > 0 && x.chip == 1 && ((x.reg >= 0xB0 && x.reg <= 0xB8 && (x.val & 0x20) != 0) || (x.reg == 0xBD && (x.val & 0x1F) != 0)));
        string note = "opn " + opn + " ssg " + ssg + " opl " + opl + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads + ", PC " + m.Cpu.PC.ToString("X4") + " unk " + m.Cpu.UnknownOps
            + (m.OtherWrites.Count > 0 ? ", other " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", reads " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (opn + ssg + opl > 0, note);
    }
}
