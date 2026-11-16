using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot X68000 (HootX68k · M68000): 68000 명령 시험, 만든 시작 코드로 hoot 포트 · OPM 타이머 IRQ · ADPCM DMA · MFP · .X 재배치, 실제 zip, 진단 조사.</summary>
public static class HootX68kTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Cpu(check);
        Synthetic(check);
        Real(check);
    }

    // ------------------------------------------------------------ CPU

    private sealed class Bus : IM68kBus
    {
        public readonly byte[] Mem = new byte[0x100000];
        public int Vector = -1;
        public byte Read8(uint a) { return Mem[a & 0xFFFFF]; }
        public ushort Read16(uint a) { a &= 0xFFFFF; return (ushort)(Mem[a] << 8 | Mem[a + 1]); }
        public void Write8(uint a, byte v) { Mem[a & 0xFFFFF] = v; }
        public void Write16(uint a, ushort v) { a &= 0xFFFFF; Mem[a] = (byte)(v >> 8); Mem[a + 1] = (byte)v; }
        public int Acknowledge(int level) { return Vector; }
        public void Put(uint a, params ushort[] words) { foreach (var w in words) { Write16(a, w); a += 2; } }
        public void Long(uint a, uint v) { Write16(a, (ushort)(v >> 16)); Write16(a + 2, (ushort)v); }
    }

    /// <summary>1000h부터 words를 돌린다 (SSP 8000h). 마지막 명령 뒤 BRA.S *에 닿으면 멈춤.</summary>
    private static M68000 RunCode(Bus b, params ushort[] words)
    {
        b.Long(0, 0x8000); b.Long(4, 0x1000);
        b.Put(0x1000, words);
        uint end = (uint)(0x1000 + words.Length * 2);
        b.Put(end, 0x60FE);   // BRA.S * (깃발을 바꾸지 않고 멈춤)
        var c = new M68000(b);
        c.Reset();
        for (int i = 0; i < 10000 && c.PC != end && !c.Stopped; i++) c.Step();
        return c;
    }

    private static void Cpu(Action<string, bool, string> check)
    {
        var b = new Bus();
        // MOVEQ #-1,D0 · ADDQ.L #1,D0 → 0, Z · C · X
        var c = RunCode(b, 0x70FF, 0x5280);
        check("m68k: moveq/addq carry → Z C X", c.D[0] == 0 && c.Z && c.C && c.X && !c.N, c.D[0].ToString("X") + " sr " + c.SR.ToString("X4"));
        // MOVE.W #7FFF,D1 · ADDQ.W #1,D1 → 8000, V · N
        c = RunCode(new Bus(), 0x323C, 0x7FFF, 0x5241);
        check("m68k: add word overflow → V N", (c.D[1] & 0xFFFF) == 0x8000 && c.V && c.N && !c.C, c.D[1].ToString("X") + " sr " + c.SR.ToString("X4"));
        // MOVE.L #100000,D0 · DIVU #7,D0 → q 14285 r 5
        c = RunCode(new Bus(), 0x203C, 0x0001, 0x86A0, 0x80FC, 0x0007);
        check("m68k: divu", c.D[0] == ((5u << 16) | 14285), c.D[0].ToString("X8"));
        // MOVE.L #-100,D0 · DIVS #7,D0 → q -14 r -2
        c = RunCode(new Bus(), 0x203C, 0xFFFF, 0xFF9C, 0x81FC, 0x0007);
        check("m68k: divs negative", c.D[0] == 0xFFFEFFF2u, c.D[0].ToString("X8"));
        // MOVE.W #-3,D0 · MULS #1000,D0 → -3000
        c = RunCode(new Bus(), 0x303C, 0xFFFD, 0xC1FC, 0x03E8);
        check("m68k: muls", (int)c.D[0] == -3000 && c.N, ((int)c.D[0]).ToString());
        // LEA 2000h,A0 · MOVEQ 1..3 → D1-D3 · MOVEM.L D1-D3,-(A0) · MOVEM.L (A0)+,D5-D7
        c = RunCode(new Bus(), 0x41F8, 0x2000, 0x7201, 0x7402, 0x7603, 0x48E0, 0x7000, 0x4CD8, 0x00E0);
        check("m68k: movem predecrement / postincrement", c.D[5] == 1 && c.D[6] == 2 && c.D[7] == 3 && c.A[0] == 0x2000, c.D[5] + " " + c.D[6] + " " + c.D[7] + " A0 " + c.A[0].ToString("X"));
        // MOVE.B #45h,D0 · MOVE.B #38h,D1 · ABCD D0,D1 → 83h
        c = RunCode(new Bus(), 0x103C, 0x0045, 0x123C, 0x0038, 0xC300);
        check("m68k: abcd", (c.D[1] & 0xFF) == 0x83 && !c.C, (c.D[1] & 0xFF).ToString("X2"));
        // MOVE.W #8001h,D0 · ASL.W #1,D0 → 0002, C · V · X
        c = RunCode(new Bus(), 0x303C, 0x8001, 0xE340);
        check("m68k: asl word", (c.D[0] & 0xFFFF) == 2 && c.C && c.V && c.X, (c.D[0] & 0xFFFF).ToString("X4") + " sr " + c.SR.ToString("X4"));
        // MOVE.L #80000001h,D0 · ROR.L #1,D0 → C0000000, C
        c = RunCode(new Bus(), 0x203C, 0x8000, 0x0001, 0xE298);
        check("m68k: ror long", c.D[0] == 0xC0000000 && c.C && c.N, c.D[0].ToString("X8"));
        // MOVEQ #5,D0 · loop: DBRA D0,loop → D0.W = FFFF
        c = RunCode(new Bus(), 0x7005, 0x51C8, 0xFFFE);
        check("m68k: dbra", (c.D[0] & 0xFFFF) == 0xFFFF, c.D[0].ToString("X"));
        // BSR sub · STOP; sub: MOVEQ #9,D2 · RTS
        c = RunCode(new Bus(), 0x6104, 0x4E72, 0x2700, 0x7409, 0x4E75);
        check("m68k: bsr / rts", c.D[2] == 9, c.D[2].ToString());
        // TRAP #3 → 벡터 35 (8Ch) = 1100h: MOVEQ #7,D3 · RTE
        b = new Bus();
        b.Long(0x8C, 0x1100);
        b.Put(0x1100, 0x7607, 0x4E73);
        c = RunCode(b, 0x4E43, 0x7801);
        check("m68k: trap → handler → rte", c.D[3] == 7 && c.D[4] == 1 && c.A[7] == 0x8000, c.D[3] + " " + c.D[4] + " SP " + c.A[7].ToString("X"));
        // 주소 오류: MOVE.W 1001h,D0 → 벡터 3 = 1200h: MOVEQ #3,D6 · STOP
        b = new Bus();
        b.Long(0x0C, 0x1200);
        b.Put(0x1200, 0x7C03, 0x4E72, 0x2700);
        c = RunCode(b, 0x3038, 0x1001);
        check("m68k: odd word read → address error (vector 3)", c.D[6] == 3 && c.LastException == 3, "D6 " + c.D[6] + " exc " + c.LastException);
        // 인터럽트: 레벨 6 자동 벡터 (78h) = 1300h: MOVEQ #6,D5 · RTE; SR 2000 · STOP 고리
        b = new Bus();
        b.Long(0x78, 0x1300);
        b.Put(0x1300, 0x7A06, 0x4E73);
        b.Long(0, 0x8000); b.Long(4, 0x1000);
        b.Put(0x1000, 0x46FC, 0x2000, 0x60FE);
        c = new M68000(b);
        c.Reset();
        for (int i = 0; i < 10; i++) c.Step();
        c.IrqLevel = 6;
        c.Step(); c.IrqLevel = 0;
        for (int i = 0; i < 4; i++) c.Step();
        check("m68k: level 6 autovector interrupt", c.D[5] == 6 && c.IntMask == 0, "D5 " + c.D[5] + " mask " + c.IntMask);
    }

    // ------------------------------------------------------------ 기계

    /// <summary>
    /// 만든 시작 코드: 0 = SSP 00F0FFFEh · PC 400h, 78h (레벨 6 자동 벡터) = 500h.
    /// 400h: OPM 타이머 A 켜기 (10h · 11h · 14h ← 15h), SR 2500 → E00000h를 보다 명령이 오면 곡 코드를 600h에 · ADPCM 재생 (DMA 채널 3: MAR 10000h · MTC 100h · CCR 88h, E92001h ← 2), 쉼 E00800h.
    /// 500h: OPM 08h ← 78h + 곡 코드 (키온), 14h ← 15h (플래그 지움), RTE.
    /// </summary>
    private static byte[] Program()
    {
        var p = new List<ushort>();
        var mem = new byte[0x800];
        void At(int a, params ushort[] w) { for (int i = 0; i < w.Length; i++) { mem[a + i * 2] = (byte)(w[i] >> 8); mem[a + i * 2 + 1] = (byte)w[i]; } }
        At(0, 0x00F0, 0xFFFE, 0x0000, 0x0400);
        At(0x78, 0x0000, 0x0500);
        At(0x400,
            0x13FC, 0x0010, 0x00E9, 0x0001, 0x13FC, 0x00F0, 0x00E9, 0x0003,   // 10h ← F0h
            0x13FC, 0x0014, 0x00E9, 0x0001, 0x13FC, 0x0015, 0x00E9, 0x0003,   // 14h ← 15h (A 시작 · IRQ)
            0x46FC, 0x2500,
            // loop (424h)
            0x4A39, 0x00E0, 0x0000, 0x6608,                                   // TST.B E00000 · BNE +8
            0x4A39, 0x00E0, 0x0800, 0x60F0,                                   // TST.B E00800 · BRA loop
            0x4239, 0x00E0, 0x0000,                                           // CLR.B E00000
            0x13F9, 0x00E0, 0x0001, 0x0000, 0x0600,                           // MOVE.B E00001,600h
            0x23FC, 0x0001, 0x0000, 0x00E8, 0x40CC,                           // MAR ← 10000h
            0x33FC, 0x0100, 0x00E8, 0x40CA,                                   // MTC ← 100h
            0x13FC, 0x0004, 0x00E8, 0x40C6,                                   // SCR: 메모리 증가
            0x13FC, 0x0088, 0x00E8, 0x40C7,                                   // CCR ← 88h
            0x13FC, 0x0002, 0x00E9, 0x2001,                                   // ADPCM 재생
            0x60B4);                                                          // BRA loop
        At(0x500,
            0x13FC, 0x0008, 0x00E9, 0x0001, 0x1039, 0x0000, 0x0600, 0x0600, 0x0078, 0x13C0, 0x00E9, 0x0003,   // 08h ← 78h + 곡
            0x13FC, 0x0014, 0x00E9, 0x0001, 0x13FC, 0x0015, 0x00E9, 0x0003,   // 14h ← 15h
            0x4E73);
        return mem;
    }

    private static void Synthetic(Action<string, bool, string> check)
    {
        var boot = Program();
        var pcm = new byte[0x100];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i * 7);
        // .X: text 8바이트 (LEA 절대 주소 → 재배치), 기준 0, 재배치 표 = 2 (2번째 바이트의 롱)
        var x = new byte[0x40 + 8 + 2];
        x[0] = (byte)'H'; x[1] = (byte)'U';
        x[15] = 8; x[27] = 2;   // text 8 · 재배치 2
        x[0x40] = 0x41; x[0x41] = 0xF9; x[0x42] = 0; x[0x43] = 0; x[0x44] = 0x00; x[0x45] = 0x04; x[0x46] = 0x4E; x[0x47] = 0x75;   // LEA 4.L,A0 · RTS
        x[0x48] = 0; x[0x49] = 2;
        var files = new Dictionary<string, byte[]> { ["boot.bin"] = boot, ["pcm.bin"] = pcm, ["drv.x"] = x };
        string xml = "<game><name>[X68000] Test</name><driver type=\"generic\">x68k</driver><options><option name=\"opm_mix\" value=\"0x100\"/></options>"
            + "<romlist archive=\"x68test\"><rom type=\"code\">boot.bin</rom><rom type=\"code\" offset=\"0x10000\">pcm.bin</rom><rom type=\"x\" offset=\"0x20000\">drv.x</rom></romlist>"
            + "<titlelist><title code=\"0x03\">three</title></titlelist></game>";
        var e = HootArchive.Parse(xml);
        check("hoot x68k: playable (generic)", HootArchive.Playable(e.Driver, e.Type), e.Driver + "/" + e.Type);
        var m = HootX68k.Boot(e, n => files.TryGetValue(n, out var d) ? d : null);
        check("hoot x68k: boot waits for command (E00000h)", m.Waiting && m.Errors.Count == 0, "PC " + m.Cpu.PC.ToString("X6") + " errors " + string.Join(",", m.Errors));
        check("hoot x68k: .X loaded at offset and relocated", m.XLoads == 1 && m.Ram[0x20002] == 0x00 && m.Ram[0x20003] == 0x02 && m.Ram[0x20004] == 0x00 && m.Ram[0x20005] == 0x04,
            BitConverter.ToString(m.Ram, 0x20000, 8));
        m.Play(3);
        m.RunSeconds(1);
        int keys = m.Log.Count(l => l.chip == 0 && l.reg == 0x08 && l.val == 0x7B);
        // 타이머 A: 64 × (1024 − 3C0h) / 4MHz = 1.024ms → 1초 약 976번
        check("hoot x68k: OPM timer A → level 6 autovector → key-on with song code", m.Ram[0x600] == 3 && keys > 900 && keys < 1050, keys + " key-ons, ints " + m.OpmInterrupts);
        int bytes = m.Log.Count(l => l.chip == 1 && l.reg == 1);
        check("hoot x68k: DMA channel 3 feeds 256 ADPCM bytes", bytes == 0x100 && m.DmaBytes == 0x100 && (m.Dma[0xC0] & 0x80) != 0, bytes + " bytes, CSR " + m.Dma[0xC0].ToString("X2"));
        var v = m.ToVgm(1, 0);
        check("hoot x68k: VGM YM2151 + OKIM6258", v.Ym2151Clock > 0 && v.Okim6258Clock > 0, v.ChipSummary);
    }

    private static void Real(Action<string, bool, string> check)
    {
        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "X68k");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot x68k real files (_incoming/Hootarchive/X68k not found)"); return; }
        foreach (var arc in new[] { "If68snd", "noble68snd" })   // 레벨 6 자동 벡터 · mfp
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot x68k " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => x.Driver == "x68k");
            if (hoot == null || hoot.Titles.Count == 0) { check("hoot x68k " + arc + ": .hoot entry", false, "none"); continue; }
            (bool sound, string cause, string note) r = (false, "", "");
            int k = 0;
            for (; k < Math.Min(4, hoot.Titles.Count) && !r.sound; k++) r = Measure(hoot, dir, hoot.Titles[k].Code, 10);
            check("hoot x68k " + arc + ": song " + k + " makes sound in 10 s", r.sound, r.note);
        }
    }

    /// <summary>조사: 처리하지 못한 IOCS · DOS 부름 → 그것을 부른 항목 수.</summary>
    private static readonly Dictionary<string, int> HostTotals = new Dictionary<string, int>();

    public static (bool sound, string cause, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootX68k.Boot(e, HootX68k.DirFiles(dir));
        bool waited = m.Waiting;
        m.Play(code);
        m.RunSeconds(seconds);
        int fm = m.Log.Count(x => x.chip == 0 && x.reg == 0x08 && (x.val & 0x78) != 0);
        int pcm = m.Log.Count(x => x.chip == 1 && x.reg == 1);
        bool sound = fm > 0 || pcm > 1000;
        string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : !waited ? "boot-not-waiting" : m.Errors.Count > 0 ? "exception-" + m.Errors[0] : m.Log.Count == 0 ? "no-chip-writes" : "writes-no-keyon";
        string note = "fm " + fm + " pcm " + pcm + ", writes " + m.Log.Count + ", opm-int " + m.OpmInterrupts + " mfp-int " + m.MfpInterrupts + ", PC " + m.Cpu.PC.ToString("X6")
            + " exc " + m.Cpu.LastException + (m.Errors.Count > 0 ? ", errors " + string.Join(",", m.Errors.Take(4)) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(2)) : "")
            + (m.Unhandled.Count > 0 ? ", host " + string.Join(" ", m.Unhandled.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key + "x" + kv.Value)) : "");
        foreach (var kv in m.Unhandled) { HostTotals.TryGetValue(kv.Key, out int n); HostTotals[kv.Key] = n + 1; }
        return (sound, cause, note);
    }

    /// <summary>진단: --hootx68trace zip [곡 번째] [초] — 예외 보고 · MFP 쓰기 (처음 200줄), 끝 상태.</summary>
    public static int TraceZip(string zip, int index, double seconds)
    {
        string dir = ZipFolder.Extract(zip);
        foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
        {
            var e = HootArchive.Load(h);
            if (!e.Driver.Equals("x68k", StringComparison.OrdinalIgnoreCase) || e.Titles.Count == 0) continue;
            int lines = 0;
            HootX68k.Machine m = null;
            HootX68k.BootHook = mm => mm.Cpu.OnException = (v, pc, addr) => { if (lines++ < 200) Console.WriteLine("  boot exception " + v + " at " + pc.ToString("X6") + (v == 3 ? " addr " + addr.ToString("X6") : "") + " op " + mm.Ram[pc & 0xFFFFFF].ToString("X2") + mm.Ram[(pc + 1) & 0xFFFFFF].ToString("X2")); };
            m = HootX68k.Boot(e, HootX68k.DirFiles(dir),
                s => { if (lines++ < 200) Console.WriteLine("  " + (m == null ? "boot" : (m.Cpu.Cycles / HootX68k.CpuClock).ToString("0.0000")) + " " + s); });
            Console.WriteLine(e.Name + " | waiting " + m.Waiting + " PC " + m.Cpu.PC.ToString("X6") + " errors " + string.Join(",", m.Errors));
            m.Cpu.OnException = (v, pc, addr) => { if (lines++ < 200) Console.WriteLine("  exception " + v + " at " + pc.ToString("X6") + (v == 3 ? " addr " + addr.ToString("X6") : "") + " op " + m.Ram[pc & 0xFFFFFF].ToString("X2") + m.Ram[(pc + 1) & 0xFFFFFF].ToString("X2")); };
            var (code, title) = e.Titles[Math.Min(index, e.Titles.Count - 1)];
            Console.WriteLine("play 0x" + code.ToString("X") + " " + title);
            m.Play(code);
            for (int s = 0; s < seconds; s++)
            {
                m.RunSeconds(1);
                Console.WriteLine("t " + (s + 1) + " PC " + m.Cpu.PC.ToString("X6") + " SR " + m.Cpu.SR.ToString("X4") + " opm-int " + m.OpmInterrupts + " mfp-int " + m.MfpInterrupts + " dma " + m.DmaBytes
                    + " writes " + m.Log.Count + " keyon " + m.Log.Count(x => x.chip == 0 && x.reg == 8 && (x.val & 0x78) != 0) + " exc " + m.Cpu.LastException + " n " + m.Cpu.Exceptions);
            }
            Console.WriteLine("errors " + string.Join(",", m.Errors) + " missing " + string.Join(",", m.Missing) + " unsupported " + string.Join(",", m.Unsupported));
        }
        return 0;
    }

    /// <summary>진단: --hootx68survey X68k폴더 [zip 수] [초].</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        var zips = Directory.GetFiles(folder, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(count).ToList();
        var stats = new SortedDictionary<string, (int total, int sound)>();
        var causes = new SortedDictionary<string, int>();
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception ex) { Console.WriteLine("ZIP?  " + Path.GetFileName(zip) + ": " + ex.Message); continue; }
            foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
            {
                var e = HootArchive.Load(h);
                if (!e.Driver.Equals("x68k", StringComparison.OrdinalIgnoreCase) || e.Titles.Count == 0) continue;
                string key = e.Type.ToLowerInvariant() + (e.Option("mfp", 0) != 0 ? " mfp" : "");
                stats.TryGetValue(key, out var st); st.total++;
                (bool ok, string cause, string note) r = (false, "none", "");
                var t0 = sw.Elapsed;
                try
                {
                    // "Stop" 같은 곡을 넘어 처음 3곡까지
                    int tried = 0;
                    foreach (var (code, title) in e.Titles)
                    {
                        if (title.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0 && e.Titles.Count > 1) continue;
                        r = Measure(e, dir, code, seconds);
                        if (r.ok || ++tried >= 3) break;
                    }
                }
                catch (Exception ex) { r = (false, "exception", ex.GetType().Name + ": " + ex.Message); }
                if (r.ok) st.sound++;
                stats[key] = st;
                causes.TryGetValue(r.cause, out int c); causes[r.cause] = c + 1;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + key + " | " + r.cause + " | " + r.note + " | " + (sw.Elapsed - t0).TotalSeconds.ToString("0.0") + "s");
            }
        }
        Console.WriteLine();
        foreach (var kv in stats) Console.WriteLine("x68k " + kv.Key + ": " + kv.Value.sound + " / " + kv.Value.total);
        foreach (var kv in causes) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        Console.WriteLine("unhandled host calls (songs): " + string.Join(", ", HostTotals.OrderByDescending(kv => kv.Value).Take(30).Select(kv => kv.Key + " " + kv.Value)));
        Console.WriteLine(sw.Elapsed.TotalSeconds.ToString("0") + " s");
        return 0;
    }
}
