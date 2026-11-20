using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 남코 System 86 (driver namco, type system86: Sky Kid Deluxe · The Return of Ishtar · Genpei Toumaden · Rolling Thunder · Wonder Momo): 사운드를 맡은 MCU를 흉내 낸다.
/// <para>기계 (MAME namcos86 MCU + Sky Kid DX 디스어셈블): HD63701 (E = 1.536MHz), 00h~1Fh 내부 레지스터, 80h~FFh 내부 RAM, 1000h~13FFh = CUS30 (파형 · 레지스터 · 메인과 나누는 RAM),
/// 1400h~1FFFh = RAM, YM2151 = hoot 옵션 opmaddr (Sky Kid DX · Rolling Thunder 2000h, Wonder Momo 3800h, Ishtar 6000h — 없으면 ROM의 STA 확장 쓰기로 찾음, Genpei 2800h),
/// 4000h~BFFFh = 외부 ROM (8000h에 A6h 머리 → 진입점 [8001h], hoot가 2000h~에도 올리면 그 자리도 롬), F000h~FFFFh = 내부 ROM (cus60, 남코 MCU 공용 OS). IRQ = 화면 60Hz, OCF 타이머 = 작업 전환.</para>
/// <para>요청: System 86 = 1380h에 곡 번호 (드라이버가 받으면 FFh로). wsg63701 = 슬롯 [00BDh] (= 1285h) + 코드에 1 (118Ch bit0 = 게임 중 → +41h 데모 목록 꺼짐). 63701X 음성 (rom pcm)은 아직 없음.</para>
/// <para>곡: 부팅 1초 뒤 요청, 180초를 YM2151 + CUS30 VGM으로 (CUS30 = 이 프로그램 안 명령 D8h).</para>
/// </summary>
public static class HootNamcoSys86
{
    public const double CpuClock = 49152000 / 32.0, OpmClock = 3579545, IrqHz = 60.606, Cus30Clock = 49152000 / 2048.0;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "namco", StringComparison.OrdinalIgnoreCase)) return false;
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "system86" || t == "wsg63701";
    }

    /// <summary>wsg63701 (Pac-Land · Dragon Buster · Baraduke · Metro-Cross · Sky Kid · Hopping Mappy): 같은 MCU · OS, YM2151 없음, RAM C000h~C7FFh.</summary>
    public static bool IsWsgOnly(string type) { return string.Equals((type ?? "").Trim(), "wsg63701", StringComparison.OrdinalIgnoreCase); }

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    /// <summary>YM2151 자리 찾기: 외부 ROM (4000h~BFFFh)의 STAA · STAB 확장 쓰기 중 2000h~7FFFh에서 짝 (a, a+1)이 가장 많은 것.</summary>
    public static int FindOpm(byte[] mem) { return FindOpm(mem, 0); }

    /// <summary>찾은 짝의 쓰기 수가 minWrites보다 적으면 −1 (YM2151 없음).</summary>
    public static int FindOpm(byte[] mem, int minWrites)
    {
        var n = new Dictionary<int, int>();
        var odd = new HashSet<int>();
        for (int i = 0x4000; i < 0xBFFD; i++)
        {
            if (mem[i] != 0xB7 && mem[i] != 0xF7) continue;
            int a = (mem[i + 1] << 8) | mem[i + 2];
            if (a < 0x2000 || a >= 0x8000) continue;
            n.TryGetValue(a & ~1, out int c); n[a & ~1] = c + 1;
            if ((a & 1) != 0) odd.Add(a & ~1);
        }
        if (minWrites > 0) foreach (var k in n.Keys.Where(k => !odd.Contains(k)).ToList()) n.Remove(k);   // 자료 자리 (a + 1)도 써야 YM2151
        if (n.Count == 0) return minWrites > 0 ? -1 : 0x2000;
        var best = n.OrderByDescending(x => x.Value).First();
        return best.Value < minWrites ? -1 : best.Key;
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        var mem = new byte[0x10000];
        var loaded = new bool[0x100];   // hoot가 롬을 올린 256바이트 쪽
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type == "pcm") { unsupported.Add("63701X pcm"); continue; }
            if (type != "code") { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (off < 0 || off >= 0x10000) { unsupported.Add(type + " offset " + off); continue; }
            int len = (int)Math.Min(d.Length, 0x10000 - off);
            Array.Copy(d, 0, mem, off, len);
            for (long p = off; p < off + len; p += 0x100) loaded[p >> 8] = true;
            any = true;
        }
        if (!any) missing.Add("(code)");
        int opm = -1;
        if (e.Options.TryGetValue("opmaddr", out var ov))
        {
            ov = ov.Trim();
            try { opm = ov.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(ov.Substring(2), NumberStyles.HexNumber) : int.Parse(ov); } catch (FormatException) { }
        }
        bool slots = IsWsgOnly(e.Type);
        if (slots) { opm = FindOpm(mem, 3); if (opm < 0) opm = 0x2000; }   // Hopping Mappy는 hoot 형식이 wsg63701이지만 System 86 보드 (YM2151 2000h, 색인으로 써서 못 찾음) — 다른 게임은 2000h에 와치독만 써서 해 없음
        else if (opm < 0) opm = FindOpm(mem);
        var m = new Machine(mem, opm, slots);
        for (int p = 0x20; p < 0x100; p++) m.RomPage[p] = loaded[p];
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : ICpu6801Bus
    {
        public readonly byte[] Mem;
        public readonly int Opm;
        public readonly Cpu6801 Cpu;
        /// <summary>롬으로 읽는 쪽 (256바이트 단위, 2000h 위): 4000h~BFFFh · F000h~ 말고도 hoot가 올린 자리 (Return of Ishtar = 0000h에도 → 2000h~3FFFh가 6000h~의 거울).</summary>
        public readonly bool[] RomPage = new bool[0x100];
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151, 1 = CUS30 (reg = 0~13Fh, 바뀐 것만).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, TimerIrqs;
        /// <summary>진단: 요청 뒤 MCU가 읽은 공유 RAM 자리 (1140h~13FFh).</summary>
        public readonly Dictionary<int, int> SharedReads = new Dictionary<int, int>();
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int opmAddr;
        private long playStart = -1;
        private readonly double irqCycles = CpuClock / IrqHz;
        private double nextIrq;
        private bool irqLine;
        private readonly int[] last = Enumerable.Repeat(-1, 0x100).ToArray();

        /// <summary>wsg63701: 슬롯 요청 (1285h + 코드), RAM C000h~C7FFh.</summary>
        public readonly bool Slots;

        public Machine(byte[] mem, int opm, bool slots = false)
        {
            Mem = mem; Opm = opm; Slots = slots;
            Cpu = new Cpu6801(this);
            Cpu.Reset();
            nextIrq = irqCycles;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            if (Slots)
            {
                int bd = (Mem[0xBD] << 8) | Mem[0xBE];   // OS가 정한 요청 기준 (모두 1285h)
                if (bd < 0x1000 || bd >= 0x1400) bd = 0x1285;
                Mem[0x118C] |= 1;     // 메인 CPU의 "게임 중" 표시: 없으면 Baraduke 드라이버가 [BD+41h] = 1 (데모 목록 — 동전 소리만)을 화면마다 다시 세움
                Mem[bd + 0x41] = 0;
                Mem[bd + (code & 0x3F)] = 1;   // 슬롯 요청 (0 아니면 울림)
            }   // wsg63701: 슬롯 요청 (OS의 기준 [00BDh] = 1285h + 슬롯, 0 아니면 울림)
            else Mem[0x1380] = (byte)code;
        }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextIrq) { nextIrq += irqCycles; irqLine = true; }
                if (irqLine && Cpu.Irq()) { Irqs++; irqLine = false; }
                else if (Cpu.TimerIrq()) TimerIrqs++;
                if (Cpu.Waiting)
                {
                    long next = Math.Min((long)Math.Ceiling(nextIrq), Cpu.Cycles + Cpu.CyclesToTimerEvent());
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, next));
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a == Opm || a == Opm + 1) return 0;   // YM2151 상태: 바쁘지 않음
            if ((a >= 0x4000 && a < 0xC000) || RomPage[a >> 8]) return Mem[a];
            if (a >= 0xF000) return Mem[a];
            if (a >= 0x80 && a < 0x100) return Mem[a];
            if (a >= 0x1000 && a < 0x2000) { if (a >= 0x1140 && a < 0x1400 && playStart >= 0) { SharedReads.TryGetValue(a, out int sr); SharedReads[a] = sr + 1; } return Mem[a]; }
            if (Slots && a >= 0xC000 && a < 0xC800) return Mem[a];
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a == Opm) { opmAddr = v; return; }
            if (a == Opm + 1) { OpmWrite(opmAddr, v); return; }
            if (a >= 0x1000 && a < 0x1140) { if (Mem[a] != v) Emit(1, a - 0x1000, v); Mem[a] = v; return; }
            if ((a >= 0x80 && a < 0x100) || (a >= 0x1000 && a < 0x2000) || (Slots && a >= 0xC000 && a < 0xC800)) { Mem[a] = v; return; }
            if (a >= 0x4000 && a < 0xC000 || a >= 0xF000 || RomPage[a >> 8]) return;
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte PortRead(int port) { return 0xFF; }
        public void PortWrite(int port, byte value) { }

        private void OpmWrite(int reg, int v)
        {
            if (reg >= 0x10 && reg <= 0x12) return;   // 타이머는 아무 데도 안 이어짐 (MCU는 OCF로 박자)
            if (reg == 0x14) { Emit(0, reg, v & 0x80); return; }
            Emit(0, reg, v);
        }

        private void Emit(int chip, int reg, int val)
        {
            if (chip == 0)
            {
                if (reg != 0x08 && reg != 0x01 && last[reg] == val) return;
                last[reg] = val;
            }
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Namco System 86 (hoot)", Notes = "hoot namco system86" };
            w.SetClock(VgmWriter.ClockYm2151, OpmClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x54, x.reg, x.val);
                else w.Write(new byte[] { 0xD8, (byte)(x.reg >> 8), (byte)x.reg, (byte)x.val });
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Any(x => x.chip == 1)) v.Cus30Clock = Cus30Clock;
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
