using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 캡콤 YM2203 × 2 보드 (driver capcom, type opn2: Ghosts'n Goblins · Gun.Smoke · Section Z · Legendary Wings · Trojan · 1943 · 1943 Kai · Avengers,
/// type rushcrsh: The Speed Rumbler, type tigerroad: Tiger Road · F-1 Dream): 사운드 보드를 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME gng · gunsmoke · 1943 · lwings · srumbler · tigeroad sound_map): Z80 (IM 1), 0000h~7FFFh = ROM, C000h~C7FFh = RAM, 명령 = 래치 읽기 (폴링).
/// opn2: 래치 C800h, YM2203 E000h · E002h (Z80 3MHz, YM 1.5MHz), IRQ = 주기 (프레임마다 넷 = 240Hz, lwings 계열 222Hz). Avengers는 래치를 E006h에 썼다 읽어 비교 (쓴 값을 돌려줌).
/// rushcrsh: 래치 E000h, YM2203 8000h · A000h (Z80 3MHz, YM 4MHz), IRQ 240Hz.
/// tigerroad: 래치 E000h, YM2203 8000h · A000h (Z80 · YM 3.579545MHz), IRQ = YM2203 #1 타이머. 샘플 CPU (MSM5205)로 가는 포트는 버린다.</para>
/// <para>곡: rom code = Z80 ROM, 부팅 1초 뒤 곡 코드를 래치에, 180초를 YM2203 둘 VGM으로 (머리말 클럭 bit30 + 명령 A5h).</para>
/// </summary>
public static class HootCapcomOpn
{
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public sealed class Layout
    {
        public string Name;
        public double CpuClock, OpnClock;
        public int Latch, Opn1, Opn2;
        /// <summary>주기 IRQ (Hz), 0이면 YM2203 #1 타이머가 IRQ.</summary>
        public double IrqHz;
        /// <summary>쓴 값을 그대로 돌려주는 자리 (없으면 −1): Avengers는 래치를 E006h (soundlatch2)에 쓰고 다시 읽어 비교한다.</summary>
        public int Echo = -1;
    }

    public static readonly Layout Gng = new Layout { Name = "Capcom Ghosts'n Goblins", CpuClock = 3000000, OpnClock = 1500000, Latch = 0xC800, Opn1 = 0xE000, Opn2 = 0xE002, IrqHz = 240 };
    /// <summary>Section Z · Legendary Wings · Trojan · Avengers (MAME lwings: 222Hz).</summary>
    public static readonly Layout Lwings = new Layout { Name = "Capcom Legendary Wings", CpuClock = 3000000, OpnClock = 1500000, Latch = 0xC800, Opn1 = 0xE000, Opn2 = 0xE002, IrqHz = 222, Echo = 0xE006 };
    public static readonly Layout Srumbler = new Layout { Name = "Capcom Speed Rumbler", CpuClock = 3000000, OpnClock = 4000000, Latch = 0xE000, Opn1 = 0x8000, Opn2 = 0xA000, IrqHz = 240 };
    public static readonly Layout TigerRoad = new Layout { Name = "Capcom Tiger Road", CpuClock = 3579545, OpnClock = 3579545, Latch = 0xE000, Opn1 = 0x8000, Opn2 = 0xA000, IrqHz = 0 };

    private static readonly string[] LwingsGames = { "Section Z", "Legendary Wings", "Trojan", "Avengers" };

    public static Layout LayoutOf(string driver, string type, string name = null)
    {
        if (!string.Equals((driver ?? "").Trim(), "capcom", StringComparison.OrdinalIgnoreCase)) return null;
        switch ((type ?? "").Trim().ToLowerInvariant())
        {
            case "opn2": return name != null && LwingsGames.Any(g => name.IndexOf(g, StringComparison.OrdinalIgnoreCase) >= 0) ? Lwings : Gng;
            case "rushcrsh": return Srumbler;
            case "tigerroad": return TigerRoad;
        }
        return null;
    }

    public static bool Handles(string driver, string type) { return LayoutOf(driver, type) != null; }

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        var layout = LayoutOf(e.Driver, e.Type, e.Name) ?? throw new InvalidDataException("캡콤 YM2203 보드가 아닙니다: " + e.Type);
        var mem = new byte[0x10000];
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0 || off >= 0x8000) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            Array.Copy(d, 0, mem, off, (int)Math.Min(d.Length, 0x8000 - off));   // ROM은 7FFFh까지
            any = true;
        }
        if (!any) missing.Add("(code)");
        var m = new Machine(layout, mem);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly Layout L;
        public readonly byte[] Mem;
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2203 #1, 1 = YM2203 #2.</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, LatchReads;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>();

        private int latch, echo;
        private readonly int[] addr = new int[2];
        private long playStart = -1;
        private readonly double irqCycles;
        private double nextIrq;
        // YM2203 #1 타이머: A = 72 × (1024 − TA) / 클럭, B = 1152 × (256 − TB) / 클럭
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;

        public Machine(Layout layout, byte[] mem)
        {
            L = layout; Mem = mem;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            irqCycles = L.IrqHz > 0 ? L.CpuClock / L.IrqHz : double.MaxValue;
            nextIrq = irqCycles;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            latch = code & 0xFF;
        }

        private long TimerAPeriod { get { return Math.Max(1, (long)(72.0 * (1024 - timerA) / L.OpnClock * L.CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(1152.0 * (256 - timerB) / L.OpnClock * L.CpuClock)); } }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        public void RunSeconds(double s) { Run((long)(L.CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            bool pending = false;   // 주기 IRQ는 받을 때까지 걸려 있다 (irq0_line_hold)
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextIrq) { nextIrq += irqCycles; pending = true; }
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                bool line = L.IrqHz > 0 ? pending : timerFlags != 0;
                if (line && Cpu.Iff1)
                {
                    pending = false;
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted)
                {
                    long next = Math.Min((long)Math.Ceiling(nextIrq), Math.Min(nextTimerA, nextTimerB));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, next));
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a == L.Latch) { LatchReads++; return (byte)latch; }
            if (a == L.Opn1) return (byte)(timerFlags & 3);   // YM2203 #1 상태 (바쁨 없음)
            if (a == L.Opn1 + 1 || a == L.Opn2 || a == L.Opn2 + 1) return 0;
            if (a == L.Echo) return (byte)echo;
            return Mem[a];
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0xC000 && a < 0xC800) { Mem[a] = v; return; }
            if (a == L.Opn1) { addr[0] = v; return; }
            if (a == L.Opn1 + 1) { OpnWrite(0, addr[0], v); return; }
            if (a == L.Opn2) { addr[1] = v; return; }
            if (a == L.Opn2 + 1) { OpnWrite(1, addr[1], v); return; }
            if (a == L.Echo) { echo = v; return; }
            if (a < 0x8000) return;
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int port) { return 0xFF; }
        public void Out(int port, byte v) { OtherWrites.TryGetValue(0x10000 | (port & 0xFF), out int c); OtherWrites[0x10000 | (port & 0xFF)] = c + 1; }

        private void OpnWrite(int chip, int reg, int v)
        {
            if (chip == 0)
            {
                switch (reg)
                {
                    case 0x24: timerA = (timerA & 3) | (v << 2); return;
                    case 0x25: timerA = (timerA & 0x3FC) | (v & 3); return;
                    case 0x26: timerB = v; return;
                    case 0x27:
                        {
                            int old = timerCtl;
                            timerCtl = v;
                            if ((v & 0x10) != 0) timerFlags &= ~1;
                            if ((v & 0x20) != 0) timerFlags &= ~2;
                            if ((v & 1) != 0 && (old & 1) == 0) nextTimerA = Cpu.Cycles + TimerAPeriod; else if ((v & 1) == 0) nextTimerA = long.MaxValue;
                            if ((v & 2) != 0 && (old & 2) == 0) nextTimerB = Cpu.Cycles + TimerBPeriod; else if ((v & 2) == 0) nextTimerB = long.MaxValue;
                            Emit(0, reg, v & 0xC0);   // 3채널 모드 비트만
                            return;
                        }
                }
            }
            else if (reg >= 0x24 && reg <= 0x27) { if (reg == 0x27) Emit(1, reg, v & 0xC0); return; }   // 둘째 칩 타이머는 아무 데도 안 이어짐
            Emit(chip, reg, v);
        }

        private readonly int[] last = Enumerable.Repeat(-1, 0x200).ToArray();

        private void Emit(int chip, int reg, int val)
        {
            // 같은 값 다시 쓰기는 버린다 (드라이버가 IRQ마다 SSG를 모두 다시 써서 VGM이 부푼다) — 키온 28h · SSG 엔벨로프 모양 0Dh · 주파수 A0h~AEh (위 바이트는 래치, 아래 바이트를 써야 바뀜)는 다시 쓰기도 뜻이 있어 둔다
            bool keep = reg == 0x28 || reg == 0x0D || reg == 0x27 || (reg >= 0xA0 && reg <= 0xAE);
            if (!keep && last[chip << 8 | reg] == val) return;
            last[chip << 8 | reg] = val;
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / L.CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = L.Name + " (hoot)", Notes = "hoot capcom" };
            w.SetClock(VgmWriter.ClockYm2203, L.OpnClock, 0x40000000u);   // bit30 = YM2203 둘
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(x.chip == 0 ? (byte)0x55 : (byte)0xA5, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
