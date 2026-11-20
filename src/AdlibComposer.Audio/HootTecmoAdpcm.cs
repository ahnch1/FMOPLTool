using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 테크모 YM3812 + MSM5205 보드 (driver tecmo, type rygar: Rygar, type gemini: Silk Worm · Gemini Wing · Back Fire): 사운드 보드를 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME tecmo rygar_sound_map · tecmo_sound_map): Z80 4MHz, YM3812 4MHz (타이머 = IRQ, IM 1), 명령 = 래치 (68000이 쓰면 NMI).
/// Rygar: ROM 0000h~3FFFh, RAM 4000h~47FFh, YM3812 8000h, C000h 읽기 = 명령 · 쓰기 = ADPCM 시작, D000h = 끝, E000h = 볼륨, F000h = NMI 확인.
/// Gemini (Silk Worm …): ROM 0000h~7FFFh, RAM 8000h~87FFh, YM3812 A000h, C000h 명령 / 시작, C400h = 끝, C800h = 볼륨, CC00h = NMI 확인.</para>
/// <para>MSM5205 (400kHz ÷ 48 = 8.33kHz): 하드웨어가 ADPCM ROM의 시작 × 100h부터 (끝 + 1) × 100h 앞까지 바이트를 읽어 위 니블 먼저 넣는다 (MAME tecmo adpcm_int).
/// VGM에 MSM5205가 없어 같은 OKI 4비트 ADPCM인 OKIM6258 (4.2667MHz ÷ 512)로 바이트마다 (니블 바꿔서).
/// 곡: rom code = Z80 ROM, pcm = ADPCM ROM. 부팅 1초 뒤 곡 코드를 명령으로, 180초를 YM3812 + OKIM6258 VGM으로.</para>
/// </summary>
public static class HootTecmoAdpcm
{
    public const double CpuClock = 4000000, OplClock = 4000000, MsmRate = 400000.0 / 48;
    public const uint VgmMsmClock = 4266667;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public sealed class Layout
    {
        public string Name;
        public int RomEnd, RamStart, Opl, Start, End, Vol, Ack;
        /// <summary>RAM 하나 더 (없으면 −1): Back Fire는 부팅 때 ROM 0103h~의 루틴을 2000h~에 베껴 놓고 부른다.</summary>
        public int Ram2Start = -1;
    }

    public static readonly Layout Rygar = new Layout { Name = "Tecmo Rygar", RomEnd = 0x4000, RamStart = 0x4000, Opl = 0x8000, Start = 0xC000, End = 0xD000, Vol = 0xE000, Ack = 0xF000 };
    /// <summary>Back Fire (hoot 형식은 gemini): Silk Worm 배치에 2000h~27FFh RAM이 더 있다.</summary>
    public static readonly Layout BackFire = new Layout { Name = "Tecmo Back Fire", RomEnd = 0x8000, RamStart = 0x8000, Opl = 0xA000, Start = 0xC000, End = 0xC400, Vol = 0xC800, Ack = 0xCC00, Ram2Start = 0x2000 };
    public static readonly Layout Gemini = new Layout { Name = "Tecmo Silk Worm", RomEnd = 0x8000, RamStart = 0x8000, Opl = 0xA000, Start = 0xC000, End = 0xC400, Vol = 0xC800, Ack = 0xCC00 };

    public static Layout LayoutOf(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "tecmo", StringComparison.OrdinalIgnoreCase)) return null;
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "rygar" ? Rygar : t == "gemini" ? Gemini : null;
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
        var layout = LayoutOf(e.Driver, e.Type) ?? throw new InvalidDataException("테크모 ADPCM 보드가 아닙니다: " + e.Type);
        if (layout == Gemini && (e.Name ?? "").IndexOf("Back Fire", StringComparison.OrdinalIgnoreCase) >= 0) layout = BackFire;
        var rom = new byte[0x10000];
        var pcm = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool code = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code" && type != "pcm") { unsupported.Add(type); continue; }
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (type == "code") { Array.Copy(d, 0, rom, off, (int)Math.Max(0, Math.Min(d.Length, layout.RomEnd - off))); code = true; }
            else pcm.Add((off, d));
        }
        if (!code) missing.Add("(code)");
        var p = new byte[pcm.Count == 0 ? 0 : pcm.Max(x => x.off + x.d.Length)];
        foreach (var (off, d) in pcm) Array.Copy(d, 0, p, off, d.Length);
        var m = new Machine(layout, rom, p);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly Layout L;
        public readonly byte[] Rom, Pcm;
        public readonly byte[] Ram = new byte[0x800];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM3812, 1 = MSM (OKIM6258 VGM 레지스터: 0 = 명령 1 멈춤 · 2 재생, 1 = 자료 바이트).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, LatchReads, MsmStarts;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>();

        private int latch, oplAddr;
        private long playStart = -1;
        private int t1, t2, tCtl, tFlags;
        private long nextT1 = long.MaxValue, nextT2 = long.MaxValue;
        private readonly Dictionary<int, int> regs = new Dictionary<int, int>();
        // MSM5205: 바이트 자리 · 끝, 돌아가는 중
        private int msmPos, msmEnd;
        private bool msmOn;
        private readonly double byteCycles = CpuClock / MsmRate * 2;
        private double nextByte;

        public Machine(Layout layout, byte[] rom, byte[] pcm)
        {
            L = layout; Rom = rom; Pcm = pcm ?? new byte[0];
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            nextByte = byteCycles;
        }

        public void Play(int code)
        {
            var init = Log.Where(x => x.chip == 0).Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            if (msmOn) Emit(1, 0, 2);
            Send(code & 0xFF);
        }

        public void Send(int code)
        {
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

        private long T1Period { get { return Math.Max(1, (long)(288.0 * (256 - t1) / OplClock * CpuClock)); } }
        private long T2Period { get { return Math.Max(1, (long)(1152.0 * (256 - t2) / OplClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextT1) { nextT1 += T1Period; if ((tCtl & 0x40) == 0) tFlags |= 0x40; }
                if (now >= nextT2) { nextT2 += T2Period; if ((tCtl & 0x20) == 0) tFlags |= 0x20; }
                if (now >= nextByte) { nextByte += byteCycles; MsmByte(); }
                if (tFlags != 0 && Cpu.Iff1)
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
                    long next = Math.Min(Math.Min(nextT1, nextT2), (long)Math.Ceiling(nextByte));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, next));
                    continue;
                }
                Cpu.Step();
            }
        }

        /// <summary>MSM5205 바이트 하나 (VCK 둘): 끝이면 멈춤, 아니면 ROM 바이트를 VGM에 (니블 바꿔서).</summary>
        private void MsmByte()
        {
            if (!msmOn) return;
            if (msmPos >= msmEnd || Pcm.Length == 0) { msmOn = false; Emit(1, 0, 1); return; }
            int b = Pcm[msmPos % Pcm.Length];
            msmPos++;
            Emit(1, 1, ((b & 15) << 4) | (b >> 4));
        }

        private readonly byte[] ram2 = new byte[0x800];

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (L.Ram2Start >= 0 && a >= L.Ram2Start && a < L.Ram2Start + 0x800) return ram2[a - L.Ram2Start];
            if (a < L.RomEnd) return Rom[a];
            if (a >= L.RamStart && a < L.RamStart + 0x800) return Ram[a - L.RamStart];
            if (a == L.Opl || a == L.Opl + 1) return (byte)(tFlags != 0 ? 0x80 | tFlags : 0);
            if (a == L.Start) { LatchReads++; return (byte)latch; }
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= L.RamStart && a < L.RamStart + 0x800) { Ram[a - L.RamStart] = v; return; }
            if (L.Ram2Start >= 0 && a >= L.Ram2Start && a < L.Ram2Start + 0x800) { ram2[a - L.Ram2Start] = v; return; }
            if (a == L.Opl) { oplAddr = v; return; }
            if (a == L.Opl + 1) { OplWrite(oplAddr, v); return; }
            if (a == L.Start)
            {
                msmPos = v << 8;
                if (!msmOn) { msmOn = true; Emit(1, 0, 1); Emit(1, 0, 2); }   // 칩을 처음부터
                MsmStarts++;
                return;
            }
            if (a == L.End) { msmEnd = (v + 1) << 8; return; }
            if (a == L.Vol || a == L.Ack) return;
            if (a < L.RomEnd) return;
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int port) { return 0xFF; }
        public void Out(int port, byte v) { }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        private void OplWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x02: t1 = v; return;
                case 0x03: t2 = v; return;
                case 0x04:
                    if ((v & 0x80) != 0) { tFlags = 0; return; }
                    {
                        int old = tCtl;
                        tCtl = v;
                        if ((v & 1) != 0 && (old & 1) == 0) nextT1 = Cpu.Cycles + T1Period; else if ((v & 1) == 0) nextT1 = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) nextT2 = Cpu.Cycles + T2Period; else if ((v & 2) == 0) nextT2 = long.MaxValue;
                        tFlags &= ~(v & 0x60);
                    }
                    return;
            }
            if (regs.TryGetValue(reg, out int prev) && prev == v) return;   // OPL은 같은 값 다시 쓰기가 아무 일 없음
            regs[reg] = v;
            Emit(0, reg, v);
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = L.Name + " (hoot)", Notes = "hoot tecmo" };
            w.SetClock(VgmWriter.ClockYm3812, OplClock);
            bool msm = Log.Any(x => x.chip == 1 && x.reg == 1);
            if (msm) { w.SetClock(VgmWriter.ClockOkim6258, VgmMsmClock); w.SetHeaderByte(0x94, 0x0A); }   // ÷512, 12비트 출력
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 1 && !msm) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x5A, x.reg, x.val);
                else w.Write(0xB7, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
