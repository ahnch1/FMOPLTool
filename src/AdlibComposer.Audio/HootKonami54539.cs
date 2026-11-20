using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 코나미 Z80 + K054539 보드 (driver konami, type 054539: Xexex · Bucky O'Hare · Wild West C.O.W.-Boys of Moo Mesa · X-Men,
/// type 054539x2: Poly-Net Warriors · Run and Gun · Metamorphic Force · Martial Champion · Mystic Warriors · Violent Storm): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME xexex · xmen · mystwarr sound_map + Xexex · X-Men 디스어셈블): Z80 8MHz, 0000h~7FFFh ROM, 8000h~BFFFh 뱅크 (F800h에 쓴 값 아래 4비트 × 16KB), C000h~DFFFh RAM,
/// E000h~E22Fh K054539 #1 (E230h~E3FFh RAM), E400h~E62Fh K054539 #2 (E630h~E7FFh RAM), E800h · E801h = YM2151 (EC00h 거울), F000h~F003h = K054321 (F002h 읽기 = 명령, F003h = 음량).
/// IRQ = 명령 (K054321, hold) 또는 YM2151 타이머, NMI = K054539 #1 타이머 (227h 주기 × 2, 22Fh bit5 켬) — 드라이버마다 하나를 씀.</para>
/// <para>곡: 부팅 1초 뒤 F002h = 곡 코드 (드라이버가 IRQ · NMI마다 봄), 180초를 YM2151 + K054539 VGM으로 (ROM = rom pcm, VGM 블록 8Ch).</para>
/// </summary>
public static class HootKonami54539
{
    public const double CpuClock = 8000000, OpmClock = 3579545, PcmClock = 18432000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "konami", StringComparison.OrdinalIgnoreCase)) return false;
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "054539" || t == "054539x2";
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
        var pcm = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            var list = type == "code" ? code : type == "pcm" ? pcm : null;
            if (list == null) { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }
            list.Add((off, d));
        }
        if (code.Count == 0) missing.Add("(code)");
        var rom = new byte[Math.Max(0x10000, code.Count == 0 ? 0 : (int)code.Max(c => c.off + c.d.Length))];
        foreach (var (off, d) in code) Array.Copy(d, 0, rom, off, d.Length);
        var p = new byte[pcm.Count == 0 ? 0 : (int)pcm.Max(x => x.off + x.d.Length)];
        foreach (var (off, d) in pcm) Array.Copy(d, 0, p, off, d.Length);
        var m = new Machine(rom, p);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom, Pcm;
        public readonly byte[] Ram = new byte[0x2000], Ram2 = new byte[0x800];
        public readonly K054539[] Chips = { new K054539(), new K054539() };
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151, 1 = K054539 #1, 2 = K054539 #2 (reg 0~22Fh).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, Nmis, LatchReads;
        /// <summary>진단: YM2151 타이머 상태.</summary>
        public string TimerState { get { return "ctl " + timerCtl.ToString("X2") + " flags " + timerFlags + " B " + timerB.ToString("X2") + " nextB " + (nextTimerB == long.MaxValue ? "-" : (nextTimerB - Cpu.Cycles).ToString()) + " opmAddr " + opmAddr.ToString("X2"); } }
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int command, opmAddr, bank;
        private bool cmdIrq;
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x100).ToArray();
        private readonly int[][] lastPcm = { Enumerable.Repeat(-1, 0x230).ToArray(), Enumerable.Repeat(-1, 0x230).ToArray() };
        private readonly byte[][] pcmReg = { new byte[0x230], new byte[0x230] };
        private long nextNmiToggle = long.MaxValue;
        private bool nmiState;

        public Machine(byte[] rom, byte[] pcm)
        {
            Rom = rom; Pcm = pcm;
            foreach (var c in Chips) c.LoadRom(pcm, 0);
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            command = code & 0xFF;
            cmdIrq = true;   // K054321: 메인이 쓰면 사운드 CPU IRQ
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        private long TimerAPeriod { get { return Math.Max(1, (long)(64.0 * (1024 - timerA) / OpmClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(1024.0 * (256 - timerB) / OpmClock * CpuClock)); } }
        private long PcmTimerPeriod { get { return Math.Max(1, (long)(38.0 * (255 - pcmReg[0][0x227]) / PcmClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                if (now >= nextNmiToggle)
                {
                    nextNmiToggle += PcmTimerPeriod;
                    nmiState = !nmiState;
                    if (nmiState)   // 올라가는 끝 = NMI
                    {
                        Nmis++;
                        Cpu.Halted = false;
                        Cpu.Iff2 = Cpu.Iff1;
                        Cpu.Iff1 = false;
                        Push(Cpu.PC);
                        Cpu.PC = 0x66;
                        Cpu.Cycles += 11;
                    }
                }
                if ((cmdIrq || (timerFlags & (timerCtl >> 2) & 3) != 0) && Cpu.Iff1)
                {
                    cmdIrq = false;
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted)
                {
                    long next = Math.Min(nextNmiToggle, Math.Min(nextTimerA, nextTimerB));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, next));
                    continue;
                }
                Cpu.Step();
            }
        }

        private static bool IsOpm(int a) { return (a & 0xFBFE) == 0xE800; }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x8000) return Rom[a];
            if (a < 0xC000) return Rom[(((bank * 0x4000) % Math.Max(0x4000, Rom.Length)) + a - 0x8000) % Rom.Length];
            if (a < 0xE000) return Ram[a - 0xC000];
            if (a < 0xE230) return PcmRead(0, a - 0xE000);
            if (a < 0xE400) return Ram2[a - 0xE230];
            if (a < 0xE630) return PcmRead(1, a - 0xE400);
            if (a < 0xE800) return Ram2[0x400 + ((a - 0xE630) & 0x3FF)];
            if (IsOpm(a)) return (byte)timerFlags;
            if (a == 0xF002) { LatchReads++; return (byte)command; }
            if (a == 0xF003) return 0;
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        private byte PcmRead(int chip, int r)
        {
            if (r == 0x22C) return (byte)Chips[chip].Active;
            if (r == 0x22D) return (byte)(Pcm.Length == 0 ? 0 : Pcm[(pcmReg[chip][0x22E] * 0x10000 + romPos[chip]++) % Pcm.Length]);   // ROM 읽기 포트
            return pcmReg[chip][r];
        }

        private readonly int[] romPos = new int[2];

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a < 0xC000) return;
            if (a < 0xE000) { Ram[a - 0xC000] = v; return; }
            if (a < 0xE230) { PcmWrite(0, a - 0xE000, v); return; }
            if (a < 0xE400) { Ram2[a - 0xE230] = v; return; }
            if (a < 0xE630) { PcmWrite(1, a - 0xE400, v); return; }
            if (a < 0xE800) { Ram2[0x400 + ((a - 0xE630) & 0x3FF)] = v; return; }
            if (IsOpm(a)) { if ((a & 1) == 0) opmAddr = v; else OpmWrite(opmAddr, v); return; }
            if (a == 0xF800) { bank = v & 0x0F; return; }
            if (a >= 0xF000 && a < 0xF004) return;   // K054321 (응답 · 음량)
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int p) { return 0xFF; }
        public void Out(int p, byte v) { }

        private void PcmWrite(int chip, int r, int v)
        {
            if (r == 0x22D) return;   // ROM/RAM 쓰기 포트 (없음)
            if (r == 0x22E) romPos[chip] = 0;
            pcmReg[chip][r] = (byte)v;
            Chips[chip].Write(0, r, v);
            if (chip == 0 && r == 0x227) { if ((pcmReg[0][0x22F] & 0x20) != 0) nextNmiToggle = Cpu.Cycles + PcmTimerPeriod; }
            if (chip == 0 && r == 0x22F) { if ((v & 0x20) != 0) { if (nextNmiToggle == long.MaxValue) nextNmiToggle = Cpu.Cycles + PcmTimerPeriod; } else { nextNmiToggle = long.MaxValue; nmiState = false; } }
            if (r == 0x22C || r == 0x227) return;
            Emit(1 + chip, r, v);
        }

        private void OpmWrite(int reg, int v)
        {
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
                        Emit(0, reg, v & 0x80);
                        return;
                    }
            }
            Emit(0, reg, v);
        }

        private void Emit(int chip, int reg, int val)
        {
            if (chip == 0)
            {
                if (reg != 0x08 && reg != 0x01 && last[reg] == val) return;
                last[reg] = val;
            }
            else if (reg != 0x214 && reg != 0x215)   // K054539: 같은 값 다시 쓰기는 버림 (키온 · 키오프는 늘)
            {
                var l = lastPcm[chip - 1];
                if (l[reg] == val) return;
                l[reg] = val;
            }
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Konami K054539 (hoot)", Notes = "hoot konami 054539" };
            bool opm = Log.Any(x => x.chip == 0);
            if (opm) w.SetClock(VgmWriter.ClockYm2151, OpmClock);
            bool two = Log.Any(x => x.chip == 2);
            bool pcm = Pcm.Length > 0 && Log.Any(x => x.chip >= 1);
            if (pcm)
            {
                w.SetClock(VgmWriter.ClockK054539, PcmClock, two ? 0x40000000u : 0);
                w.SetHeaderByte(0x95, 0x02);   // 잔향 끔 (흉내 안 함)
                for (int c = 0; c < (two ? 2 : 1); c++)
                {
                    var cmd = new byte[7 + 8 + Pcm.Length];
                    cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8C;
                    uint size = (uint)(8 + Pcm.Length) | (c == 1 ? 0x80000000u : 0);
                    cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                    int total = Pcm.Length;
                    cmd[7] = (byte)total; cmd[8] = (byte)(total >> 8); cmd[9] = (byte)(total >> 16); cmd[10] = (byte)(total >> 24);
                    Array.Copy(Pcm, 0, cmd, 15, Pcm.Length);
                    w.Write(cmd);
                }
            }
            double total2 = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip >= 1 && !pcm) continue;
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x54, x.reg, x.val);
                else w.Write(new byte[] { 0xD3, (byte)((x.reg >> 8) | (x.chip == 2 ? 0x80 : 0)), (byte)x.reg, (byte)x.val });
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
