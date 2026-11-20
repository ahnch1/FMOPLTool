using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 토아플랜 1 보드 (driver toaplan, type generic: Flying Shark · Twin Cobra · Rally Bike · Truxton · Hellfire · Zero Wing · Demon's World · Out Zone · Gulf War 2):
/// 사운드 보드를 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME toaplan1 · twincobr): Z80 3.5MHz, 0000h~7FFFh = ROM, 8000h~87FFh = 68000과 같이 쓰는 RAM, YM3812 = I/O 포트 (게임마다 자리가 다름 → ROM의 OUT (n),A에서 찾음,
/// <see cref="FindYmPort"/>), 상태 읽기 = 주소 포트 (bit7 IRQ · bit6 타이머 1 · bit5 타이머 2), 타이머 넘침 = IRQ (IM 1). I/O의 나머지 (딥 스위치 · 입력) = 0.</para>
/// <para>68000과 맞춤: 드라이버가 8001h ← 00h을 쓰고 68000이 AAh를 쓸 때까지 기다린다 (그 뒤 ROM 합 · RAM 시험) → 부팅에서 8001h ← AAh.
/// 명령 = 8000h 한 바이트 (FFh = 없음, 드라이버가 IRQ에서 읽고 FFh로 되돌림, 래치 · NMI 없음). 곡: rom code = Z80 ROM, 부팅 1초 뒤 곡 코드를 8000h에, 180초를 YM3812 VGM으로.</para>
/// </summary>
public static class HootToaplan
{
    public const double CpuClock = 3500000, YmClock = 3500000;
    public const double DefaultSeconds = 180, DefaultFade = 4;
    /// <summary>명령 바이트 · 맞춤 바이트 (같이 쓰는 RAM).</summary>
    public const int CommandAddr = 0x8000, HandshakeAddr = 0x8001;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "toaplan", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "generic", StringComparison.OrdinalIgnoreCase);
    }

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        var code = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            code.Add((off, d));
        }
        if (code.Count == 0) missing.Add("(code)");
        var rom = new byte[Math.Max(0x8000, code.Count == 0 ? 0 : code.Max(c => c.off + c.d.Length))];
        foreach (var (off, d) in code) Array.Copy(d, 0, rom, off, d.Length);
        var m = new Machine(rom) { YmPort = FindYmPort(rom) };
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.Start();
        return m;
    }

    /// <summary>YM3812 포트: ROM의 OUT (n),A (D3 n) 중 n (짝수) · n + 1이 둘 다 있는 것 가운데 가장 많은 n. 없으면 0.</summary>
    public static int FindYmPort(byte[] rom)
    {
        var count = new int[256];
        for (int i = 0; i + 1 < rom.Length; i++) if (rom[i] == 0xD3) count[rom[i + 1]]++;
        int best = 0, bestN = 0;
        for (int p = 0; p < 256; p += 2)
        {
            int n = Math.Min(count[p], count[p + 1]);
            if (n > bestN) { bestN = n; best = p; }
        }
        return best;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom;
        public readonly byte[] Ram = new byte[0x8000];   // 8000h~FFFFh (같이 쓰는 RAM은 8000h~87FFh)
        public readonly Z80 Cpu;
        /// <summary>YM3812 쓰기 (시간 = 44100Hz 샘플, 재생 명령부터).</summary>
        public readonly List<(double t, int reg, int val)> Log = new List<(double, int, int)>();
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        public int Irqs;
        public int YmPort = -1;
        /// <summary>진단: OUT · IN 포트 횟수, RAM 읽기 (주소 → 횟수), Z80이 RAM에 쓴 주소 (<see cref="Trace"/>일 때).</summary>
        public readonly Dictionary<int, int> Outs = new Dictionary<int, int>(), Ins = new Dictionary<int, int>(), RamReads = new Dictionary<int, int>();
        public readonly HashSet<int> RamWritten = new HashSet<int>();
        public bool Trace;

        private int ymAddr;
        private readonly SortedDictionary<int, int> regs = new SortedDictionary<int, int>();
        private long playStart = -1;
        private int timer1, timer2, timerCtl, timerFlags;
        private long nextT1 = long.MaxValue, nextT2 = long.MaxValue;

        public Machine(byte[] rom)
        {
            Rom = rom;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        /// <summary>부팅: 드라이버가 8001h ← 00h을 쓰게 두고 68000처럼 AAh로 답한 뒤 1초.</summary>
        public void Start()
        {
            RunSeconds(0.05);
            Ram[HandshakeAddr - 0x8000] = 0xAA;
            RunSeconds(1);
        }

        /// <summary>곡 명령: 68000처럼 8000h에.</summary>
        public void Play(int code)
        {
            Log.Clear();
            foreach (var kv in regs) Log.Add((0.0, kv.Key, kv.Value));   // 부팅 때 쓴 레지스터를 처음에
            playStart = Cpu.Cycles;
            Ram[CommandAddr - 0x8000] = (byte)code;
        }

        // OPL 타이머: 1 = 72 × 4 클럭 × (256 − T1), 2 = 그 4배
        private long T1Period { get { return Math.Max(1, (long)(288.0 * (256 - timer1) / YmClock * CpuClock)); } }
        private long T2Period { get { return Math.Max(1, (long)(1152.0 * (256 - timer2) / YmClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextT1) { nextT1 += T1Period; if ((timerCtl & 0x40) == 0) timerFlags |= 0x40; }
                if (now >= nextT2) { nextT2 += T2Period; if ((timerCtl & 0x20) == 0) timerFlags |= 0x20; }
                if (timerFlags != 0 && Cpu.Iff1)
                {
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted)
                {
                    long next = Math.Min(end, Math.Min(nextT1, nextT2));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, next);
                    continue;
                }
                Cpu.Step();
            }
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x8000) return a < Rom.Length ? Rom[a] : (byte)0xFF;
            if (Trace) { RamReads.TryGetValue(a, out int c); RamReads[a] = c + 1; }
            return Ram[a - 0x8000];
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a < 0x8000) return;
            if (Trace) RamWritten.Add(a);
            Ram[a - 0x8000] = v;
        }

        public byte In(int port)
        {
            port &= 0xFF;
            if (Trace) { Ins.TryGetValue(port, out int c); Ins[port] = c + 1; }
            if (YmPort >= 0 && port == YmPort) return (byte)(timerFlags != 0 ? 0x80 | timerFlags : 0);
            return 0;
        }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            if (Trace) { Outs.TryGetValue(port, out int c); Outs[port] = c + 1; }
            if (YmPort < 0) return;
            if (port == YmPort) ymAddr = v;
            else if (port == YmPort + 1) YmWrite(ymAddr, v);
        }

        private void YmWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x02: timer1 = v; return;
                case 0x03: timer2 = v; return;
                case 0x04:
                    if ((v & 0x80) != 0) { timerFlags = 0; return; }   // IRQ 리셋
                    {
                        int old = timerCtl;
                        timerCtl = v;
                        if ((v & 1) != 0 && (old & 1) == 0) nextT1 = Cpu.Cycles + T1Period; else if ((v & 1) == 0) nextT1 = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) nextT2 = Cpu.Cycles + T2Period; else if ((v & 2) == 0) nextT2 = long.MaxValue;
                        timerFlags &= ~(v & 0x60);
                    }
                    return;
            }
            // 드라이버가 IRQ마다 레지스터를 거의 다 다시 씀 (초당 약 6천) — OPL은 같은 값을 다시 써도 아무 일 없음 (키온도 레벨) → 바뀔 때만
            if (regs.TryGetValue(reg, out int prev) && prev == v) return;
            regs[reg] = v;
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), reg, v));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Toaplan (hoot)", Notes = "hoot toaplan" };
            w.SetClock(VgmWriter.ClockYm3812, YmClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(0x5A, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
