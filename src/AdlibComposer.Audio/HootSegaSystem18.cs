using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 세가 System 18 (driver sega, type system18: Shadow Dancer · Alien Storm · Bloxeed · Moonwalker · Clutch Hitter · Pontoon): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME segas18 sound_map / sound_portmap): Z80 8MHz, 0000h~9FFFh ROM, A000h~BFFFh 뱅크 (PCM 롬 8KB 단위 — 포트 A0h: bit6~7 = 롬 번호, 아래 6비트 = 뱅크),
/// C000h~C00Fh (거울 ~CFFFh) = RF5C68 레지스터, D000h~DFFFh = RF5C68 메모리 창 (4KB, 뱅크는 레지스터 7), E000h~FFFFh RAM.
/// 포트 80h~83h = YM3438 #1, 90h~93h = #2 (8MHz), C0h 읽기 = 명령 (메인이 쓰면 NMI). IRQ = YM3438 #1 타이머.</para>
/// <para>곡: 부팅 1초 뒤 명령, 180초를 YM2612 × 2 + RF5C68 VGM으로 (RF5C68 RAM은 처음 그림 = RAM 블록 C0h, 그 뒤 쓰기 = 명령 C1h).</para>
/// </summary>
public static class HootSegaSystem18
{
    public const double CpuClock = 8000000, OpnClock = 8000000, PcmClock = 10000000;
    public const double S32Clock = 8053975, S32PcmClock = 12500000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "sega", StringComparison.OrdinalIgnoreCase)) return false;
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "system18" || t == "system32";
    }

    /// <summary>System 32 (Rad Mobile? · Air Rescue · Spider-Man · Arabian Fight · Burning Rival · Golden Axe RoG · Holosseum · Dark Edge · F1 …): 같은 칩, Z80 · YM 8.05MHz, RF5C68 12.5MHz,
    /// 명령 = 공유 RAM E000h~E008h 큐 (80h 또는 00h = 빔, 드라이버가 한 틱에 하나씩 밀어냄), 박자 = YM3438 타이머 A 폴링, 뱅크 = 포트 A0h~AFh (아래 6비트) · B0h~BFh (bit2 → bank bit6, bit0~1 → bit7~8) → 사운드 영역 100000h + 뱅크 × 2000h
    /// (영역: 100000h~ = 코드 롬, 200000h + hoot 오프셋 = PCM 롬 — MAME segas32).</summary>
    public static bool IsSystem32(string type) { return string.Equals((type ?? "").Trim(), "system32", StringComparison.OrdinalIgnoreCase); }

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        var code = new byte[0x10000];
        var pcm = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code" && type != "pcm") { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (type == "pcm") { pcm.Add((off, d)); continue; }
            if (off < 0 || off >= 0x10000) { unsupported.Add(type + " offset " + off); continue; }
            Array.Copy(d, 0, code, off, (int)Math.Min(d.Length, 0x10000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        Machine m;
        if (IsSystem32(e.Type))
        {
            // 사운드 영역 (MAME segas32): 100000h~1FFFFFh = 코드 롬 (거울), 200000h + 오프셋 = PCM 롬 (1MB 칸을 거울로 채움)
            var region = new byte[0x500000];
            var codeFile = e.Roms.Where(r => r.Item1 == "code").Select(r => file(r.Item3)).FirstOrDefault(d => d != null) ?? code;
            for (int at = 0x100000; at < 0x200000; at += codeFile.Length) Array.Copy(codeFile, 0, region, at, Math.Min(codeFile.Length, 0x200000 - at));
            foreach (var (off, d) in pcm)
            {
                long slot = 0x200000 + off;
                int span = d.Length < 0x100000 && (off & 0xFFFFF) == 0 ? 0x100000 : d.Length;
                for (long at = slot; at < slot + span && at < region.Length; at += d.Length) Array.Copy(d, 0, region, at, (int)Math.Min(d.Length, region.Length - at));
            }
            m = new Machine(code, new[] { region }, true);
        }
        else m = new Machine(code, pcm.OrderBy(x => x.off).Select(x => x.d).ToArray());
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Code;
        public readonly byte[][] PcmRoms;
        public readonly byte[] Ram = new byte[0x2000];
        public readonly Rf5c68 Pcm = new Rf5c68();
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 · 1 = YM3438 #1 · #2 (reg | 포트 &lt;&lt; 8), 2 = RF5C68 레지스터, 3 = RF5C68 메모리 (reg = 자리).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, LatchReads;
        public readonly Dictionary<int, int> OtherPorts = new Dictionary<int, int>(), OtherWrites = new Dictionary<int, int>();

        private int latch, bankReg;
        private readonly int[] opnAddr = new int[4];
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x400).ToArray();
        private byte[] ramAtPlay;

        /// <summary>System 32 배치.</summary>
        public readonly bool S32;
        private int bankLo, bankHi;

        public Machine(byte[] code, byte[][] pcmRoms, bool system32 = false)
        {
            Code = code; PcmRoms = pcmRoms; S32 = system32;
            if (S32) for (int i = 0; i < 9; i++) Ram[i] = 0x80;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        public void Play(int code)
        {
            var init = Log.Where(x => x.chip != 3).Select(x => (0.0, x.chip, x.reg, x.val)).ToList();   // RF5C68 메모리는 그림으로
            Log.Clear();
            Log.AddRange(init);
            ramAtPlay = (byte[])Pcm.Ram.Clone();
            playStart = Cpu.Cycles;
            Send(code & 0xFF);
        }

        public void Send(int code)
        {
            if (S32) { int q = 0; while (q < 8 && Ram[q] != 0x80 && Ram[q] != 0) q++; if (q < 8) Ram[q] = (byte)code; return; }   // 공유 RAM 큐 (빈 칸 = 80h 또는 00h — 판마다)
            latch = code;
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

        private long TimerAPeriod { get { return Math.Max(1, (long)(144.0 * (1024 - timerA) / OpnClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(144.0 * 16 * (256 - timerB) / OpnClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

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
                if (Cpu.Halted) { Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, Math.Min(nextTimerA, nextTimerB))); continue; }
                Cpu.Step();
            }
        }

        private byte BankRead(int a)
        {
            if (S32)
            {
                int bank = bankLo | ((bankHi & 4) << 4) | ((bankHi & 3) << 7);
                var reg = PcmRoms[0];
                return reg[(0x100000 + bank * 0x2000 + (a - 0xA000)) % reg.Length];
            }
            int rom = (bankReg >> 6) & 3;
            if (rom >= PcmRoms.Length || PcmRoms[rom].Length == 0) return 0xFF;
            var r = PcmRoms[rom];
            return r[((bankReg & 0x3F) * 0x2000 + (a - 0xA000)) % r.Length];
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0xA000) return Code[a];
            if (a < 0xC000) return BankRead(a);
            if (a >= 0xE000) return Ram[a - 0xE000];
            if (a >= 0xD000) return Pcm.Ram[(Pcm.WindowBank << 12) | (a & 0xFFF)];
            return 0;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0xE000) { Ram[a - 0xE000] = v; return; }
            if (a >= 0xD000)
            {
                int adr = (Pcm.WindowBank << 12) | (a & 0xFFF);
                if (Pcm.Ram[adr] == v) return;
                Pcm.Write(1, adr, v);
                if (playStart >= 0) Emit(3, adr, v);
                return;
            }
            if (a >= 0xC000) { Pcm.Write(0, a & 0x0F, v); Emit(2, a & 0x0F, v); return; }
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int port)
        {
            port &= 0xFF;
            if ((port & 0xF0) == 0x80) return (byte)timerFlags;
            if ((port & 0xF0) == 0x90) return 0;
            if ((port & 0xE0) == 0xC0) { LatchReads++; return (byte)latch; }
            OtherPorts.TryGetValue(port, out int c); OtherPorts[port] = c + 1;
            return 0xFF;
        }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            if ((port & 0xE0) == 0x80)
            {
                int chip = (port >> 4) & 1, p = (port >> 1) & 1;
                if ((port & 1) == 0) opnAddr[chip * 2 + p] = v;
                else OpnWrite(chip, p, opnAddr[chip * 2 + p], v);
                return;
            }
            if (S32)
            {
                if ((port & 0xF0) == 0xA0) { bankLo = v & 0x3F; return; }
                if ((port & 0xF0) == 0xB0) { bankHi = v; return; }
                if ((port & 0xF0) == 0xC0 || (port & 0xF8) == 0xD0 || port == 0xF1) return;   // 인터럽트 제어 · 더미
            }
            if ((port & 0xE0) == 0xA0) { bankReg = v; return; }
            if ((port & 0xE0) == 0xC0) return;   // 메인으로 답
            OtherPorts.TryGetValue(0x100 | port, out int c); OtherPorts[0x100 | port] = c + 1;
        }

        private void OpnWrite(int chip, int port, int reg, int v)
        {
            if (chip == 0 && port == 0)
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
                            Emit(0, reg, v & 0xC0);
                            return;
                        }
                }
            }
            else if (port == 0 && reg >= 0x24 && reg <= 0x27) { if (reg == 0x27) Emit(chip, reg, v & 0xC0); return; }
            Emit(chip, reg | (port << 8), v);
        }

        private void Emit(int chip, int reg, int val)
        {
            if (chip <= 1)
            {
                int r = reg & 0xFF;
                bool keep = r == 0x28 || (r >= 0xA0 && r <= 0xAE) || r == 0x2A;
                int k = chip * 0x200 + reg;
                if (!keep && last[k] == val) return;
                last[k] = val;
            }
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = S32 ? "Sega System 32 (hoot)" : "Sega System 18 (hoot)", Notes = S32 ? "hoot sega system32" : "hoot sega system18" };
            bool two = Log.Any(x => x.chip == 1);
            w.SetClock(VgmWriter.ClockYm2612, S32 ? S32Clock : OpnClock, two ? 0x40000000u : 0);
            bool pcm = Log.Any(x => x.chip >= 2);
            if (pcm)
            {
                w.SetClock(VgmWriter.ClockRf5c68, S32 ? S32PcmClock : PcmClock);
                var ram = ramAtPlay ?? Pcm.Ram;
                var cmd = new byte[7 + 2 + ram.Length];   // RAM 블록 C0h: 시작 2바이트 + 64KB
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0xC0;
                int size = 2 + ram.Length;
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                Array.Copy(ram, 0, cmd, 9, ram.Length);
                w.Write(cmd);
            }
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                switch (x.chip)
                {
                    case 0: w.Write((x.reg & 0x100) != 0 ? (byte)0x53 : (byte)0x52, x.reg & 0xFF, x.val); break;
                    case 1: w.Write((x.reg & 0x100) != 0 ? (byte)0xA3 : (byte)0xA2, x.reg & 0xFF, x.val); break;
                    case 2: w.Write(0xB0, x.reg, x.val); break;
                    default: w.Write(new byte[] { 0xC1, (byte)x.reg, (byte)(x.reg >> 8), (byte)x.val }); break;
                }
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
