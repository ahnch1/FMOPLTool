using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot 데이터 이스트 6502 보드 (HootDeco6502): 6502 10진 모드 · DECO222, 배치 고르기, 실제 zip, 진단 (HOOT_SONGS = 곡마다).</summary>
public static class HootDeco6502Tests
{
    public static readonly string[] Archives =
    {
        "hbarrel", "baddudes", "birdtry", "robocop", "stadhero", "hippodrm", "actfancr", "triothep", "lastmisn", "shackled",
        "cobracom", "garyoret", "meikyuh", "gondo", "ghostb", "srdarwin", "oscar", "karnov", "wndrplnt", "chelnov",
    };

    private sealed class FlatBus : ICpu6502Bus
    {
        public readonly byte[] M = new byte[0x10000];
        public byte Read(int a) { return M[a & 0xFFFF]; }
        public void Write(int a, byte v) { M[a & 0xFFFF] = v; }
    }

    public static void Run(Action<string, bool, string> check)
    {
        check("hoot dataeast 6502 types: playable, own board", HootArchive.Playable("dataeast", "cobracom") && HootDeco6502.Handles("dataeast", "KARNOV")
            && !HootDeco.Handles("dataeast", "drgninja") && !HootDeco6502.Handles("dataeast", "deco32"), "");

        // 10진 모드: SED · LDA #$19 · CLC · ADC #$28 → 47h (켜면), 41h (끄면 = 2A03)
        foreach (bool dec in new[] { true, false })
        {
            var bus = new FlatBus();
            byte[] prog = { 0xF8, 0xA9, 0x19, 0x18, 0x69, 0x28 };
            Array.Copy(prog, 0, bus.M, 0x200, prog.Length);
            var cpu = new Cpu6502(bus, Cpu6502.Variant.Nes2A03) { DecimalMode = dec, PC = 0x200 };
            for (int i = 0; i < 4; i++) cpu.Step();
            check("6502 decimal mode " + (dec ? "on: 19h + 28h = 47h" : "off (2A03): 19h + 28h = 41h"), cpu.A == (dec ? 0x47 : 0x41), cpu.A.ToString("X2"));
        }
        // DECO222: 명령 바이트만 bit5 · bit6 바꿈 — 암호 A9h (LDA #) = 원래 C9h? 거꾸로: LDA # (A9h)를 암호로 쓰면 C9h
        {
            var bus = new FlatBus();
            bus.M[0x8000] = (byte)HootDeco6502.Deco222(0xA9); bus.M[0x8001] = 0x60;   // 피연산자 60h는 그대로
            var cpu = new Cpu6502(bus, Cpu6502.Variant.Nes2A03) { PC = 0x8000, OpcodeMap = (a, op) => a >= 0x8000 ? HootDeco6502.Deco222(op) : op };
            cpu.Step();
            check("6502 OpcodeMap (DECO222): opcode decrypted, operand untouched", bus.M[0x8000] == 0xC9 && cpu.A == 0x60 && cpu.PC == 0x8002, "A " + cpu.A.ToString("X2"));
        }
        // 배치: LDA $6000이 많으면 Dec8, LDA $0800이면 Karnov
        var r1 = new byte[0x100]; r1[0] = 0xAD; r1[1] = 0x00; r1[2] = 0x60;
        var r2 = new byte[0x100]; r2[0] = 0xAD; r2[1] = 0x00; r2[2] = 0x08;
        check("hoot dataeast 6502: layout from latch read", HootDeco6502.FindLayout(r1, false) == HootDeco6502.Dec8 && HootDeco6502.FindLayout(r2, false) == HootDeco6502.Karnov
            && HootDeco6502.FindLayout(new byte[16], false) == HootDeco6502.Dec0, "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot dataeast 6502 real files (_incoming/Hootarchive/roms not found)"); return; }
        bool all = Environment.GetEnvironmentVariable("HOOT_SONGS") != null;
        foreach (var arc in Archives)
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot dataeast 6502 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootDeco6502.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot dataeast 6502 " + arc + ": .hoot entry", false, "none"); continue; }
            if (all)
            {
                foreach (var t in hoot.Titles) Console.WriteLine("  " + arc + " 0x" + t.Code.ToString("X2") + " " + Measure(hoot, dir, t.Code, 8).note + " | " + t.Title);
                continue;
            }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(4)) { code = t.Code; r = Measure(hoot, dir, code, 8); if (r.sound) break; }
            check("hoot dataeast 6502 " + arc + " (" + hoot.Type + "): song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
            if (arc == "chelnov")
            {
                // 렌더한 VGM (YM2203 + YM3526)을 VgmPlayer로 재생하면 들린다
                var vgm = HootDeco6502.Render(hoot, dir, code, 6, 0);
                var p = new VgmPlayer(); p.Start(vgm, 0, null);
                double peak = 0;
                for (int i = 0; i < 6 * VgmSong.Rate; i++) { p.Step(); p.Generate(out double l, out _); peak = Math.Max(peak, Math.Abs(l)); }
                check("hoot dataeast 6502 chelnov: audible through VGM YM2203 + YM3526", peak > 0.02 && vgm.ChipSummary.Contains("YM2203"), "peak " + peak.ToString("0.000") + ", " + vgm.ChipSummary);
            }
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootDeco6502.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        int opn = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
        int ssg = m.Log.Count(x => x.t > 0 && x.chip == 0 && x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0);
        int opl = m.Log.Count(x => x.t > 0 && x.chip == 1 && ((x.reg >= 0xB0 && x.reg <= 0xB8 && (x.val & 0x20) != 0) || (x.reg == 0xBD && (x.val & 0x1F) != 0)));
        int oki = m.OkiStarts;
        string note = m.L.Name + (m.Decrypt ? " decrypt" : "") + ": opn " + opn + " ssg " + ssg + " opl " + opl + " oki " + oki + ", writes " + m.Log.Count + ", nmi " + m.Nmis + " irq " + m.Irqs + " latch " + m.LatchReads
            + ", PC " + m.Cpu.PC.ToString("X4") + (m.Cpu.UnknownOps > 0 ? ", unknown ops " + m.Cpu.UnknownOps : "") + (m.Cpu.Jammed ? ", JAM" : "")
            + (m.OtherWrites.Count > 0 ? ", other w " + string.Join(" ", m.OtherWrites.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.OtherReads.Count > 0 ? ", other r " + string.Join(" ", m.OtherReads.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key.ToString("X4") + ":" + x.Value)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing) : "") + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "");
        return (opn + ssg + opl + oki > 0, note);
    }
}
