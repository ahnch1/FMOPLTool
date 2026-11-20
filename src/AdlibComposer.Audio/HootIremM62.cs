using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 아이렘 M6803 사운드 보드 (driver irem, type m62: Kung-Fu Master · Lode Runner 1~4 · Spelunker · Kid Niki · Battle Road · Lot Lot · Horizon · Youjyuden,
/// Moon Patrol · Traverse USA · 10-Yard Fight = M52 소형 보드): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME irem_audio m62 · m52_small): M6803 (3.58MHz, E = ÷4), 내부 RAM 80h~FFh, ROM, AY-3-8910 × 2 (0.89MHz)는 포트로:
/// 포트 1 = 자료 (읽기는 고른 AY의 지금 레지스터), 포트 2 bit0 1→0 = 쓰기 스트로브, bit2 = 1 주소 / 0 자료, bit3 = AY #1 (45M), bit4 = AY #2 (45L).
/// 명령 = AY #1 포트 A (레지스터 14) 읽기, 메인이 쓰면 IRQ — 확인 = M62 0800h (거울 F7FCh) · M52 1000h~1FFFh 쓰기. MSM5205 #1 (0801h / M52 홀수 0000h~0FFFh) — VCK (AY #1 포트 B: S96 4kHz · S48 8kHz · S64 6kHz)는 NMI (드라이버가 박자로 씀), VCK마다 받은 니블을 VGM OKIM6258로. MSM #2 (슬레이브)는 아직.</para>
/// <para>곡: 부팅 1초 뒤 명령, 180초를 AY × 2 VGM으로 (머리말 74h bit30 + 명령 A0h aa bit7).</para>
/// </summary>
public static class HootIremM62
{
    public const double CpuClock = 3579545 / 4.0, AyClock = 3579545 / 4.0;
    /// <summary>MSM5205 VCK = NMI (384kHz ÷ 96 = 4kHz): 드라이버가 AY 쓰기 박자를 이것으로 맞춘다.</summary>
    public const double NmiHz = 384000 / 96.0;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "irem", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "m62", StringComparison.OrdinalIgnoreCase);
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
        var mem = new byte[0x10000];
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        int romStart = 0x10000;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0x2000 || off >= 0x10000) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            Array.Copy(d, 0, mem, off, (int)Math.Min(d.Length, 0x10000 - off));
            romStart = (int)Math.Min(romStart, off);
            any = true;
        }
        if (!any) missing.Add("(code)");
        var m = new Machine(mem, romStart);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : ICpu6801Bus
    {
        public readonly byte[] Mem;
        public readonly int RomStart;
        public readonly Cpu6801 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>AY 쓰기: chip 0 = AY #1 (45M), 1 = AY #2 (45L).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, Nmis, AdpcmWrites, LatchReads;
        /// <summary>MSM5205 #1 니블 (VCK마다, 리셋 아닐 때): VGM OKIM6258로.</summary>
        public readonly List<(double t, int nibble)> Adpcm = new List<(double, int)>();
        /// <summary>MSM5205 VCK 속도 (Hz, 0 = 슬레이브 — VCK 없음).</summary>
        public double MsmRate = NmiHz;
        private int msmData, portB = 0xFF;
        private bool msmReset = true;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch, port1, port2 = 0xFF;
        private readonly int[] ayAddr = new int[2];
        private readonly int[,] ayReg = new int[2, 16];
        private bool irqLine;
        private readonly double nmiCycles = CpuClock / NmiHz;
        private double nextNmi;
        private long playStart = -1;

        public Machine(byte[] mem, int romStart)
        {
            Mem = mem; RomStart = romStart;
            Cpu = new Cpu6801(this);
            Cpu.Reset();
            nextNmi = nmiCycles;   // 첫 NMI는 한 주기 뒤 (리셋 코드가 스택을 잡은 다음)
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            Send(code & 0xFF);
        }

        public void Send(int code) { latch = code; irqLine = true; }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextNmi)
                {
                    if (MsmRate <= 0) nextNmi = double.MaxValue;
                    else
                    {
                        nextNmi += CpuClock / MsmRate;
                        if (!msmReset && playStart >= 0) Adpcm.Add(((Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock, msmData & 15));
                        Nmis++;
                        Cpu.Nmi();
                    }
                }
                if (irqLine && Cpu.Irq()) Irqs++;
                else Cpu.TimerIrq();
                if (Cpu.Waiting)
                {
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, Math.Min((long)Math.Ceiling(nextNmi), Cpu.Cycles + Cpu.CyclesToTimerEvent())));
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a >= RomStart || (a >= 0x80 && a < 0x100)) return Mem[a];
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0x80 && a < 0x100) { Mem[a] = v; return; }
            if (a >= RomStart) return;
            if ((a & 0x0803) == 0x0800 || ((a & 0x7FFF) >= 0x1000 && (a & 0x7FFF) < 0x2000) || a == 0x2000) { irqLine = false; return; }   // IRQ 확인 (M62 0800h · M52 1000h~ · 10-Yard 2000h)
            if ((a & 0x0803) == 0x0801 || (a & 0x7FFF) < 0x1000 && (a & 1) != 0) { AdpcmWrites++; msmData = v; return; }   // MSM5205 #1 자료 (M62 0801h · M52 홀수)
            if ((a & 0x0803) == 0x0802 || (a & 0x7FFF) < 0x1000) { AdpcmWrites++; return; }   // MSM5205 #2 (슬레이브 — 아직)
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte PortRead(int port)
        {
            if (port != 1) return 0xFF;
            int chip = (port2 & 0x08) != 0 ? 0 : (port2 & 0x10) != 0 ? 1 : -1;
            if (chip < 0) return 0xFF;
            int r = ayAddr[chip];
            if (chip == 0 && r == 14) { LatchReads++; return (byte)latch; }   // AY #1 포트 A = 명령
            return (byte)ayReg[chip, r & 15];
        }

        public void PortWrite(int port, byte v)
        {
            if (port == 1) { port1 = v; return; }
            if (port != 2) return;
            if ((port2 & 0x01) != 0 && (v & 0x01) == 0)   // 스트로브 1 → 0
            {
                bool address = (port2 & 0x04) != 0;
                for (int chip = 0; chip < 2; chip++)
                {
                    if ((port2 & (chip == 0 ? 0x08 : 0x10)) == 0) continue;
                    if (address) ayAddr[chip] = port1 & 0x0F;
                    else AyWrite(chip, ayAddr[chip], port1);
                }
            }
            port2 = v;
        }

        private void AyWrite(int chip, int reg, int v)
        {
            ayReg[chip, reg] = v;
            if (chip == 0 && reg == 15)   // AY #1 포트 B: bit0 = MSM #1 리셋, bit2~4 = 속도 (S96 4kHz · S48 8kHz · S64 6kHz · 슬레이브)
            {
                portB = v;
                msmReset = (v & 1) != 0;
                int sel = (v >> 2) & 3;
                double rate = sel == 0 ? 4000 : sel == 1 ? 8000 : sel == 2 ? 6000 : 0;
                if (rate != MsmRate) { MsmRate = rate; if (rate > 0) nextNmi = Cpu.Cycles + CpuClock / rate; }
            }
            if (reg >= 14) return;   // 포트 A · B (명령 · MSM 제어)
            if (reg != 13 && LastValue(chip, reg) == v) return;   // 같은 값 다시 쓰기는 버림 (엔벨로프 모양 13은 다시 시작이라 늘)
            last[chip, reg] = v;
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, v));
        }

        private readonly int[,] last = InitLast();
        private static int[,] InitLast() { var a = new int[2, 16]; for (int i = 0; i < 2; i++) for (int j = 0; j < 16; j++) a[i, j] = -1; return a; }
        private int LastValue(int chip, int reg) { return last[chip, reg]; }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Irem M62 (hoot)", Notes = "hoot irem m62" };
            w.SetClock(VgmWriter.ClockAy8910, AyClock, 0x40000000u);   // bit30 = AY 둘
            // MSM5205 #1 → OKIM6258 (같은 OKI 4비트 ADPCM): 클럭 = 속도 × 1024 (÷1024), 니블 둘을 한 바이트로 (먼저 온 것이 아래)
            bool adpcm = Adpcm.Count > 1 && Adpcm.Any(x => x.nibble != 0 && x.nibble != 8);
            double msmRate = Adpcm.Count > 1 ? VgmSong.Rate / Math.Max(1e-9, (Adpcm[Adpcm.Count - 1].t - Adpcm[0].t) / (Adpcm.Count - 1)) : NmiHz;
            var events = Log.Select(x => (x.t, kind: 0, a: x.reg | (x.chip << 7), b: x.val)).ToList();
            if (adpcm)
            {
                w.SetClock(VgmWriter.ClockOkim6258, Math.Round(msmRate) * 1024);
                w.SetHeaderByte(0x94, 0x08);   // ÷1024, 12비트 출력
                events.Add((Adpcm[0].t, 1, 0, 2));   // 재생
                for (int i = 0; i + 1 < Adpcm.Count; i += 2) events.Add((Adpcm[i].t, 2, 1, Adpcm[i].nibble | (Adpcm[i + 1].nibble << 4)));
            }
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in events.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.kind == 0) w.Write(0xA0, x.a, x.b);
                else w.Write(0xB7, x.a, x.b);   // OKIM6258
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
