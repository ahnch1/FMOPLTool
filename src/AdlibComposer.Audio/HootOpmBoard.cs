using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 아케이드 Z80 + YM2151 + OKIM6295 사운드 보드 (capcom cps1 · technos ddragon2 · ddragon3 · ctribe): 보드를 그대로 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>보드 배치는 MAME와 같게 (<see cref="Layout"/>): CPS1 = ROM 0000h~7FFFh + 뱅크 8000h~BFFFh (F004h bit0 → ROM 8000h + n × 4000h), RAM D000h~D7FFh,
/// YM2151 F000h · F001h, OKI F002h, 핀7 F006h, 래치 F008h (드라이버가 바뀌었는지 보며 읽음, NMI 없음).
/// Double Dragon 2 · Combatribes = ROM 0000h~7FFFh, RAM 8000h~87FFh, YM2151 8800h, OKI 9800h, 래치 A000h (쓰면 NMI).
/// Double Dragon 3 = ROM 0000h~BFFFh, RAM C000h~C7FFh, YM2151 C800h, OKI D800h, 래치 E000h (NMI), OKI 뱅크 E800h.
/// YM2151 타이머 A · B 넘침 = IRQ (IM 1, 플래그를 지울 때까지). OKI 읽기 = 울리는 음성 (구절 표의 길이로 계산).</para>
/// <para>곡: rom code = Z80 ROM, pcm = OKI ROM. 부팅 1초 뒤 곡 코드를 래치에 (CPS1은 처음 래치 FFh), 180초를 YM2151 + OKIM6295 VGM으로.</para>
/// </summary>
public static class HootOpmBoard
{
    public const double DefaultSeconds = 180, DefaultFade = 4;

    /// <summary>보드 배치.</summary>
    public sealed class Layout
    {
        public string Name;
        public int FixedEnd = 0x8000, BankStart = -1, BankReg = -1, BankBase = 0x8000;
        public int RamStart, RamEnd;
        public int Ym, Oki, Pin7Reg = -1, OkiBankReg = -1, Latch;
        public bool LatchNmi;
        public int IdleLatch;
        public double CpuClock = 3579545, YmClock = 3579545, OkiClock = 1056000;
    }

    public static readonly Layout Cps1 = new Layout { Name = "CPS-1", FixedEnd = 0x8000, BankStart = 0x8000, BankReg = 0xF004, BankBase = 0x8000, RamStart = 0xD000, RamEnd = 0xD800, Ym = 0xF000, Oki = 0xF002, Pin7Reg = 0xF006, Latch = 0xF008, LatchNmi = false, IdleLatch = 0xFF, OkiClock = 1000000 };
    public static readonly Layout DoubleDragon2 = new Layout { Name = "Double Dragon 2", FixedEnd = 0x8000, RamStart = 0x8000, RamEnd = 0x8800, Ym = 0x8800, Oki = 0x9800, Latch = 0xA000, LatchNmi = true };
    public static readonly Layout DoubleDragon3 = new Layout { Name = "Double Dragon 3", FixedEnd = 0xC000, RamStart = 0xC000, RamEnd = 0xC800, Ym = 0xC800, Oki = 0xD800, Latch = 0xE000, LatchNmi = true, OkiBankReg = 0xE800 };

    public static Layout LayoutOf(string driver, string type)
    {
        string d = (driver ?? "").Trim().ToLowerInvariant(), t = (type ?? "").Trim().ToLowerInvariant();
        if (d == "capcom" && t == "cps1") return Cps1;
        if (d == "technos" && (t == "ddragon2" || t == "ctribe")) return DoubleDragon2;   // Combatribes = DD2와 같은 사운드 배치 (MAME ctribe_sound_map)
        if (d == "technos" && t == "ddragon3") return DoubleDragon3;
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
        var layout = LayoutOf(e.Driver, e.Type) ?? throw new InvalidDataException("YM2151 보드가 아닙니다: " + e.Driver + " / " + e.Type);
        var code = new List<(long off, byte[] d)>();
        var pcm = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            var list = type == "code" ? code : type == "pcm" ? pcm : null;
            if (list == null) { unsupported.Add(type); continue; }
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            list.Add((off, d));
        }
        if (code.Count == 0) missing.Add("(code)");
        byte[] Join(List<(long off, byte[] d)> parts, int min)
        {
            long size = Math.Max(min, parts.Count == 0 ? 0 : parts.Max(p => p.off + p.d.Length));
            var r = new byte[size];
            foreach (var (off, d) in parts) Array.Copy(d, 0, r, off, d.Length);
            return r;
        }
        var m = new Machine(layout, Join(code, 0x10000), Join(pcm, 0));
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly Layout L;
        public readonly byte[] Rom, Pcm;
        public readonly byte[] Ram;
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151 (reg, val), 1 = OKIM6295 (VGM 레지스터, val).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, LatchReads, OkiStarts;
        public Dictionary<int, int> PcCount;

        private int bank, latch, ymAddr;
        private long playStart = -1;
        private readonly byte[] ymRegs = new byte[256];
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        // OKIM6295 (울리는 음성 계산용)
        private int okiPending = -1, okiBank;
        private bool pin7 = true;
        private readonly long[] voiceEnd = new long[4];

        public Machine(Layout layout, byte[] rom, byte[] pcm)
        {
            L = layout; Rom = rom; Pcm = pcm;
            Ram = new byte[L.RamEnd - L.RamStart];
            latch = L.IdleLatch;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            Send(code & 0xFF);
        }

        public void Send(int code)
        {
            latch = code;
            if (!L.LatchNmi) return;
            Nmis++;
            Cpu.Halted = false;
            Cpu.Iff2 = Cpu.Iff1;
            Cpu.Iff1 = false;
            Push(Cpu.PC);
            Cpu.PC = 0x66;
            Cpu.Cycles += 11;
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        // YM2151 타이머: A = 64 × (1024 − TA) / 클럭, B = 1024 × (256 − TB) / 클럭
        private long TimerAPeriod { get { return Math.Max(1, (long)(64.0 * (1024 - timerA) / L.YmClock * L.CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(1024.0 * (256 - timerB) / L.YmClock * L.CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(L.CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
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
                    long next = Math.Min(end, Math.Min(nextTimerA, nextTimerB));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, next);
                    continue;
                }
                if (PcCount != null) { PcCount.TryGetValue(Cpu.PC, out int k); PcCount[Cpu.PC] = k + 1; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < L.FixedEnd) return a < Rom.Length ? Rom[a] : (byte)0xFF;
            if (L.BankStart >= 0 && a >= L.BankStart && a < L.BankStart + 0x4000)
            {
                long at = L.BankBase + (long)bank * 0x4000 + (a - L.BankStart);
                return Rom[at % Rom.Length];
            }
            if (a >= L.RamStart && a < L.RamEnd) return Ram[a - L.RamStart];
            if (a == L.Ym || a == L.Ym + 1) return (byte)(timerFlags & 3);   // YM2151 상태 (바쁨 없음)
            if (a == L.Oki) return OkiStatus();
            if (a == L.Latch) { LatchReads++; return (byte)latch; }
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= L.RamStart && a < L.RamEnd) { Ram[a - L.RamStart] = v; return; }
            if (a == L.Ym) { ymAddr = v; return; }
            if (a == L.Ym + 1) { YmWrite(ymAddr, v); return; }
            if (a == L.Oki) { OkiWrite(v); return; }
            if (a == L.BankReg) { bank = v & 1; return; }
            if (a == L.Pin7Reg) { pin7 = (v & 1) != 0; Emit(1, 0x0C, pin7 ? 1 : 0); return; }
            if (a == L.OkiBankReg) { okiBank = v & 1; Emit(1, 0x0F, okiBank); return; }
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / L.CpuClock), chip, reg, val));
        }

        private void YmWrite(int reg, int v)
        {
            ymRegs[reg] = (byte)v;
            switch (reg)
            {
                case 0x10: timerA = (timerA & 3) | (v << 2); return;
                case 0x11: timerA = (timerA & 0x3FC) | (v & 3); return;
                case 0x12: timerB = v; return;
                case 0x14:
                    {
                        int old = timerCtl;
                        timerCtl = v;
                        if ((v & 0x10) != 0) timerFlags &= ~1;
                        if ((v & 0x20) != 0) timerFlags &= ~2;
                        if ((v & 1) != 0 && (old & 1) == 0) nextTimerA = Cpu.Cycles + TimerAPeriod; else if ((v & 1) == 0) nextTimerA = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) nextTimerB = Cpu.Cycles + TimerBPeriod; else if ((v & 2) == 0) nextTimerB = long.MaxValue;
                        return;
                    }
            }
            Emit(0, reg, v);
        }

        private double OkiRate { get { return L.OkiClock / (pin7 ? 132.0 : 165.0); } }

        private int PcmByte(long a) { a += (long)okiBank * 0x40000; return a >= 0 && a < Pcm.Length ? Pcm[a] : 0; }

        private void OkiWrite(int d)
        {
            Emit(1, 0x00, d);
            if (okiPending >= 0)
            {
                int phrase = okiPending;
                okiPending = -1;
                int voices = (d >> 4) & 15;
                long t = phrase * 8;
                long start = ((PcmByte(t) << 16) | (PcmByte(t + 1) << 8) | PcmByte(t + 2)) & 0x3FFFF;
                long stop = ((PcmByte(t + 3) << 16) | (PcmByte(t + 4) << 8) | PcmByte(t + 5)) & 0x3FFFF;
                long nibbles = Math.Max(0, stop - start + 1) * 2;
                long cycles = (long)(nibbles / OkiRate * L.CpuClock);
                for (int i = 0; i < 4; i++)
                    if ((voices & (1 << i)) != 0 && Cpu.Cycles >= voiceEnd[i]) { voiceEnd[i] = Cpu.Cycles + cycles; OkiStarts++; }
                return;
            }
            if ((d & 0x80) != 0) { okiPending = d & 0x7F; return; }
            int stopMask = (d >> 3) & 15;
            for (int i = 0; i < 4; i++) if ((stopMask & (1 << i)) != 0) voiceEnd[i] = 0;
        }

        private byte OkiStatus()
        {
            int r = 0xF0;
            for (int i = 0; i < 4; i++) if (Cpu.Cycles < voiceEnd[i]) r |= 1 << i;
            return (byte)r;
        }

        public byte In(int port) { return 0xFF; }
        public void Out(int port, byte v) { }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = L.Name + " (hoot)", Notes = "hoot " + L.Name };
            w.SetClock(VgmWriter.ClockYm2151, L.YmClock);
            bool oki = Pcm != null && Pcm.Length > 0;
            if (oki)
            {
                w.SetClock(VgmWriter.ClockOkim6295, L.OkiClock, 0x80000000u);   // 핀7 높음 (÷132)
                var cmd = new byte[7 + 8 + Pcm.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8B;
                int size = 8 + Pcm.Length;
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int total = Pcm.Length;
                cmd[7] = (byte)total; cmd[8] = (byte)(total >> 8); cmd[9] = (byte)(total >> 16); cmd[10] = (byte)(total >> 24);
                Array.Copy(Pcm, 0, cmd, 15, Pcm.Length);
                w.Write(cmd);
            }
            double total2 = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 1 && !oki) continue;
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x54, x.reg, x.val);
                else w.Write(0xB8, x.reg, x.val);
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
