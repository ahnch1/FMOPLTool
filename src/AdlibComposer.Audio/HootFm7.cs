using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 후지쯔 FM-7 · FM77AV (driver fm7 · fm77av, type generic): 문서가 없어 hoot 목록의 PATCH.BIN을 거꾸로 읽어 맞춘 규약.
/// <para>기계: 6809 2MHz, 64KB 전부 RAM (FD00h~FDFFh = I/O, 벡터 FFF0h~ = RAM — 시작 코드가 IRQ FFF8h · FIRQ FFF6h를 써 넣음).
/// 타이머 IRQ = 2.0345ms (FD02h bit2 = 켬, FD03h 읽기 = 원인 (0이면 그 원인): bit2 타이머 (읽으면 내려감) · bit3 확장 (OPN 타이머) · bit0 키보드).
/// FD17h bit3 = 0이면 OPN 인터럽트.</para>
/// <para>칩 (명령 · 자료 포트 둘: 명령 0 쉼 · 1 읽기 · 2 쓰기 · 3 주소 · 4 상태 · 9 조이스틱): PSG AY-3-8910 FD0Dh · FD0Eh, OPN YM2203 FD15h · FD16h (FM 음원 카드 · FM77AV 안),
/// WHG 둘째 OPN FD45h · FD46h. 클럭 1.2288MHz. driver fm7 = FM 음원 카드 없음 (OPN 읽기 FFh → PSG 판), fm77av = OPN 있음.</para>
/// <para>hoot 포트: FD58h 읽기 = 명령 (1 재생 · 2 멈춤, 읽으면 비움), FD59h~FD5Ch = 곡 코드 바이트 0~3,
/// FD59h 쓰기 n = bgm n을 mdata_addr에 (FFh = 받음만). 180초 미리 렌더 → VGM (YM2203 (둘) + AY-3-8910).</para>
/// </summary>
public static class HootFm7
{
    public const double CpuClock = 2000000, ChipClock = 1228800, TimerSeconds = 0.0020345;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        string d = (driver ?? "").Trim().ToLowerInvariant();
        return (d == "fm7" || d == "fm77av") && string.Equals((type ?? "").Trim(), "generic", StringComparison.OrdinalIgnoreCase);
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
        var m = new Machine
        {
            HasOpn = !string.Equals((e.Driver ?? "").Trim(), "fm7", StringComparison.OrdinalIgnoreCase),   // fm7 = FM 음원 카드 없는 FM-7 (PSG 판), fm77av = OPN 안에 있음
            DataAddr = e.Option("mdata_addr", 0x4000) & 0xFFFF,
            DataSize = Math.Max(0, e.Option("mdata_size", 0)),
            FileSize = Math.Max(0, e.Option("mfile_size", 0)),
        };
        var bgm = new Dictionary<int, string>();
        foreach (var (type, off, text) in e.Roms)
        {
            switch (type)
            {
                case "code":
                case "data":
                    {
                        var d = file(text);
                        if (d == null) { m.Missing.Add(text); break; }
                        m.Put((int)off, d);
                        break;
                    }
                case "bgm": bgm[(int)off] = text; break;
                case "irom": break;   // FM77AV 이니시에이터 ROM: 시작 코드가 쓰지 않음
                default: m.Unsupported.Add(type); break;
            }
        }
        m.Bgm = n =>
        {
            if (!bgm.TryGetValue(n, out var name)) return null;
            var d = file(name);
            if (d == null && m.Missing.Count < 64) m.Missing.Add(name);
            return d;
        };
        m.Cpu.PC = e.Option("init_pc", 0) & 0xFFFF;
        m.Cpu.CC = Cpu6809.FlagI | Cpu6809.FlagF;
        m.RunSeconds(2);
        return m;
    }

    /// <summary>OPN 하나 (YM2203: 주소 · 타이머 · 상태).</summary>
    private sealed class Opn
    {
        public int Addr, Data, TimerA, TimerB, Ctl, Flags;
        public long NextA = long.MaxValue, NextB = long.MaxValue;
        public readonly byte[] Regs = new byte[0x100];
        public long PeriodA { get { return Math.Max(1, (long)(72.0 * (1024 - TimerA) / ChipClock * CpuClock)); } }
        public long PeriodB { get { return Math.Max(1, (long)(1152.0 * (256 - TimerB) / ChipClock * CpuClock)); } }

        public void Tick(long now)
        {
            if (now >= NextA) { NextA += PeriodA; if ((Ctl & 4) != 0) Flags |= 1; }
            if (now >= NextB) { NextB += PeriodB; if ((Ctl & 8) != 0) Flags |= 2; }
        }

        /// <summary>레지스터 쓰기 (타이머 처리), 칩으로 보낼 것이면 true.</summary>
        public bool Write(int reg, int v, long now)
        {
            Regs[reg] = (byte)v;
            switch (reg)
            {
                case 0x24: TimerA = (TimerA & 3) | (v << 2); return false;
                case 0x25: TimerA = (TimerA & 0x3FC) | (v & 3); return false;
                case 0x26: TimerB = v; return false;
                case 0x27:
                    {
                        int old = Ctl;
                        Ctl = v;
                        if ((v & 0x10) != 0) Flags &= ~1;
                        if ((v & 0x20) != 0) Flags &= ~2;
                        if ((v & 1) != 0 && (old & 1) == 0) NextA = now + PeriodA; else if ((v & 1) == 0) NextA = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) NextB = now + PeriodB; else if ((v & 2) == 0) NextB = long.MaxValue;
                        return true;   // 3채널 모드 비트 (bit6 · 7)
                    }
            }
            return true;
        }
    }

    public sealed class Machine : ICpu6809Bus
    {
        public readonly byte[] Ram = new byte[0x10000];
        public readonly Cpu6809 Cpu;
        public int DataAddr, DataSize, FileSize;
        public Func<int, byte[]> Bgm = n => null;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기 (시간 = 44100Hz 샘플, 재생 명령부터; chip 0 = PSG, 1 = OPN, 2 = WHG OPN).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Loads, Irqs, Commands;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();
        public bool UsesWhg, UsesOpn, UsesPsg;
        /// <summary>OPN (FD15h · FD16h)이 있음 — false면 읽기 FFh (드라이버가 PSG로 내려감).</summary>
        public bool HasOpn = true;

        private readonly Opn opn = new Opn(), whg = new Opn();
        private int psgAddr, psgData, irqMask, command, code;
        private bool timerPending, subHalted;
        private long playStart = -1, nextTimer;
        private readonly long timerPeriod = (long)(TimerSeconds * CpuClock);
        private readonly int[] last = Enumerable.Repeat(-1, 0x300).ToArray();

        public Machine()
        {
            Cpu = new Cpu6809(this);
            nextTimer = timerPeriod;
        }

        public void Put(int at, byte[] d)
        {
            for (int i = 0; i < d.Length && at + i < 0x10000; i++) if (at + i >= 0) Ram[at + i] = d[i];
        }

        public void Play(int songCode)
        {
            code = songCode;
            command = 1;
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
        }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        private bool IrqLine { get { return (timerPending && (irqMask & 4) != 0) || opn.Flags != 0 || whg.Flags != 0; } }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextTimer) { nextTimer += timerPeriod; timerPending = true; }
                opn.Tick(now); whg.Tick(now);
                if (IrqLine && Cpu.Irq()) Irqs++;
                if (Cpu.Waiting)
                {
                    long next = Math.Min(nextTimer, Math.Min(Math.Min(opn.NextA, opn.NextB), Math.Min(whg.NextA, whg.NextB)));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, next));
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0xFD00 || a > 0xFDFF) return Ram[a];
            switch (a)
            {
                case 0xFD03:
                    {
                        // 원인 (0 = 그 원인): bit2 타이머 (읽으면 내려감), bit3 확장 (OPN)
                        int v = 0xFF;
                        if (timerPending) { v &= ~0x04; timerPending = false; }
                        if (opn.Flags != 0 || whg.Flags != 0) v &= ~0x08;
                        return (byte)v;
                    }
                case 0xFD05: return (byte)(subHalted ? 0xFE : 0x7E);   // 서브 CPU (화면): bit7 = 0 일 없음 · 1 = 멈춤 요청을 받아 멈춤 (메인이 0을 기다려 80h를 쓰고 1을 기다림)
                case 0xFD0E: return (byte)psgData;
                case 0xFD16: return HasOpn ? (byte)opn.Data : (byte)0xFF;
                case 0xFD46: return (byte)whg.Data;
                case 0xFD17: return (byte)(HasOpn && opn.Flags != 0 ? 0xF7 : 0xFF);
                case 0xFD47: return (byte)(whg.Flags != 0 ? 0xF7 : 0xFF);
                case 0xFD58: { int c = command; command = 0; if (c != 0) Commands++; return (byte)c; }
                case 0xFD59: return (byte)code;
                case 0xFD5A: return (byte)(code >> 8);
                case 0xFD5B: return (byte)(code >> 16);
                case 0xFD5C: return (byte)(code >> 24);
            }
            OtherReads.TryGetValue(a, out int n); OtherReads[a] = n + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a < 0xFD00 || a > 0xFDFF) { Ram[a] = v; return; }
            switch (a)
            {
                case 0xFD02: irqMask = v; return;
                case 0xFD05: subHalted = (v & 0x80) != 0; return;   // bit7 = 서브 CPU 멈춤 요청
                case 0xFD03: return;   // 부저
                case 0xFD0D: PsgCommand(v); return;
                case 0xFD0E: psgData = v; return;
                case 0xFD15: if (HasOpn) OpnCommand(opn, 1, v); return;
                case 0xFD16: opn.Data = v; return;
                case 0xFD45: OpnCommand(whg, 2, v); return;
                case 0xFD46: whg.Data = v; return;
                case 0xFD59:
                    if (v != 0xFF)
                    {
                        var d = Bgm(v);
                        if (d != null)
                        {
                            int n = d.Length;
                            if (FileSize > 0) n = Math.Min(n, FileSize);
                            if (DataSize > 0) n = Math.Min(n, DataSize);
                            for (int i = 0; i < n && DataAddr + i < 0xFD00; i++) Ram[DataAddr + i] = d[i];
                            Loads++;
                        }
                    }
                    return;
            }
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        private void PsgCommand(int cmd)
        {
            switch (cmd & 0x0F)
            {
                case 1: psgData = psgAddr == 0x0E || psgAddr == 0x0F ? 0xFF : last[psgAddr & 0x0F] & 0xFF; return;
                case 2: UsesPsg = true; if ((psgAddr & 0x0F) < 14) Emit(0, psgAddr & 0x0F, psgData); last[psgAddr & 0x0F] = psgData; return;
                case 3: psgAddr = psgData & 0x0F; return;
            }
        }

        private void OpnCommand(Opn o, int chip, int cmd)
        {
            switch (cmd & 0x0F)
            {
                case 1: o.Data = o.Addr == 0x0E || o.Addr == 0x0F ? 0xFF : o.Regs[o.Addr]; return;   // 읽기 (SSG 포트 = 조이스틱 없음)
                case 2:
                    if (chip == 1) UsesOpn = true; else UsesWhg = true;
                    if (o.Write(o.Addr, o.Data, Cpu.Cycles) && (o.Addr < 0x0E || o.Addr >= 0x10)) Emit(chip, o.Addr, o.Data);
                    return;
                case 3: o.Addr = o.Data; return;
                case 4: o.Data = o.Flags & 3; return;   // 상태 (바쁨 없음)
                case 9: o.Data = 0xFF; return;          // 조이스틱
            }
        }

        private void Emit(int chip, int reg, int val)
        {
            bool keep = chip > 0 && (reg == 0x28 || reg == 0x0D || reg == 0x27 || (reg >= 0xA0 && reg <= 0xAE));
            if (chip == 0 && reg == 0x0D) keep = true;
            int k = chip << 8 | reg;
            if (!keep && last[k] == val) return;
            last[k] = val;
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "FM-7 / FM77AV (hoot)", Notes = "hoot fm7" };
            bool two = Log.Any(x => x.chip == 2);
            if (Log.Any(x => x.chip >= 1)) w.SetClock(VgmWriter.ClockYm2203, ChipClock, two ? 0x40000000u : 0u);
            if (Log.Any(x => x.chip == 0)) w.SetClock(VgmWriter.ClockAy8910, ChipClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write((byte)0xA0, x.reg, x.val);
                else w.Write(x.chip == 1 ? (byte)0x55 : (byte)0xA5, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
