using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 데이터 이스트 6502 사운드 보드 (driver dataeast, type cobracom · drgninja · karnov · triothep · lastmisn · makyosen): 사운드 보드를 흉내 내고 6502 사운드 ROM을 돌린다.
/// <para>칩: YM2203 1.5MHz + YM3812 또는 YM3526 3MHz (이름에 YM3526이면 3526) + OKIM6295 1MHz (rom pcm이 있을 때). 6502 1.5MHz (10진 모드 있음), 명령 = 래치 + NMI, OPL 타이머 = IRQ.</para>
/// <para>배치 (MAME dec0 · dec8 · karnov · actfancr) — ROM이 래치를 읽는 자리 (LDA abs)로 고른다:
/// <list type="bullet">
/// <item>Dec0 (래치 3000h): RAM 0000h~07FFh, YM2203 0800h, OPL 1000h, OKI 3800h — Heavy Barrel · Bad Dudes · Birdie Try · Robocop · Stadium Hero · Hippodrome · Act-Fancer · Trio The Punch · Last Mission · Shackled</item>
/// <item>Dec8 (래치 6000h): YM2203 2000h, OPL 4000h — Cobra-Command · Garyo Retsuden · Meikyuu Hunter G · Gondomania · Ghostbusters</item>
/// <item>Karnov (래치 0800h): YM2203 1000h, OPL 1800h — Karnov · Wonder Planet · Chelnov</item>
/// </list>
/// ROM: code offset ≥ 8000h면 그 자리, offset 0 (64KB, Trio The Punch)이면 4000h~FFFFh를 파일 그대로. hoot 옵션 decrypt = DECO222 (ROM의 명령 바이트 bit5 · bit6 바꿈, Ghostbusters).</para>
/// <para>곡: 부팅 1초 뒤 곡 코드를 래치에 (NMI), 180초를 YM2203 + OPL (+ OKIM6295) VGM으로.</para>
/// </summary>
public static class HootDeco6502
{
    public const double CpuClock = 1500000, OpnClock = 1500000, OplClock = 3000000, OkiClock = 1000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static readonly string[] Types = { "cobracom", "drgninja", "karnov", "triothep", "lastmisn", "makyosen" };

    public sealed class Layout
    {
        public string Name;
        public int Latch, Opn, Opl, Oki = -1;
    }

    public static readonly Layout Dec0 = new Layout { Name = "Data East (dec0)", Latch = 0x3000, Opn = 0x0800, Opl = 0x1000, Oki = 0x3800 };
    public static readonly Layout Dec8 = new Layout { Name = "Data East (dec8)", Latch = 0x6000, Opn = 0x2000, Opl = 0x4000 };
    public static readonly Layout Karnov = new Layout { Name = "Data East (karnov)", Latch = 0x0800, Opn = 0x1000, Opl = 0x1800 };

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "dataeast", StringComparison.OrdinalIgnoreCase)
            && Types.Contains((type ?? "").Trim().ToLowerInvariant());
    }

    /// <summary>배치 고르기: ROM의 LDA abs (ADh) 중 래치 자리 셋 가운데 가장 많이 읽는 것. decrypt면 명령 바이트를 풀어 본다.</summary>
    public static Layout FindLayout(byte[] rom, bool decrypt)
    {
        int best = 0; Layout pick = Dec0;
        foreach (var l in new[] { Dec0, Dec8, Karnov })
        {
            int n = 0;
            for (int i = 0; i + 2 < rom.Length; i++)
            {
                int op = decrypt ? Deco222(rom[i]) : rom[i];
                if (op == 0xAD && rom[i + 1] == (l.Latch & 0xFF) && rom[i + 2] == l.Latch >> 8) n++;
            }
            if (n > best) { best = n; pick = l; }
        }
        return pick;
    }

    /// <summary>DECO222: 명령 바이트의 bit5 · bit6을 바꾼다 (MAME deco222).</summary>
    public static int Deco222(int op) { return (op & 0x9F) | ((op & 0x20) << 1) | ((op & 0x40) >> 1); }

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
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            list.Add((off, d));
        }
        if (code.Count == 0) missing.Add("(code)");
        var space = new byte[0x10000];
        foreach (var (off, d) in code) Array.Copy(d, 0, space, off, (int)Math.Min(d.Length, 0x10000 - off));
        int romStart = code.Count > 0 && code.Min(c => c.off) < 0x8000 ? 0x4000 : 0x8000;
        var pcmData = new byte[pcm.Count == 0 ? 0 : pcm.Max(p => p.off + p.d.Length)];
        foreach (var (off, d) in pcm) Array.Copy(d, 0, pcmData, off, d.Length);
        bool decrypt = e.Options.TryGetValue("decrypt", out var dv) && dv.Trim() != "0" && dv.Trim() != "0x0";
        var romOnly = new byte[0x10000 - romStart];
        Array.Copy(space, romStart, romOnly, 0, romOnly.Length);
        var layout = FindLayout(romOnly, decrypt);
        bool ym3526 = (e.Name ?? "").IndexOf("YM3526", StringComparison.OrdinalIgnoreCase) >= 0;
        var m = new Machine(layout, space, romStart, pcmData, ym3526, decrypt);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : ICpu6502Bus
    {
        public readonly Layout L;
        public readonly byte[] Space, Pcm;
        public readonly int RomStart;
        public readonly bool Ym3526, Decrypt;
        public readonly byte[] Ram = new byte[0x800];
        public readonly Cpu6502 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2203, 1 = OPL, 2 = OKIM6295 (VGM 레지스터).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, LatchReads, OkiStarts;
        /// <summary>진단: 알 수 없는 자리 쓰기 · 읽기.</summary>
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch, opnAddr, oplAddr;
        private long playStart = -1;
        private readonly Dictionary<int, int> oplRegs = new Dictionary<int, int>();
        private int timer1, timer2, timerCtl, timerFlags;
        private long nextT1 = long.MaxValue, nextT2 = long.MaxValue;
        private int okiPending = -1;
        private readonly long[] voiceEnd = new long[4];

        public Machine(Layout layout, byte[] space, int romStart, byte[] pcm, bool ym3526, bool decrypt)
        {
            L = layout; Space = space; RomStart = romStart; Pcm = pcm ?? new byte[0]; Ym3526 = ym3526; Decrypt = decrypt;
            Cpu = new Cpu6502(this, Cpu6502.Variant.Nes2A03) { DecimalMode = true };
            if (decrypt) Cpu.OpcodeMap = (a, op) => a >= 0x8000 ? Deco222(op) : op;
            Cpu.PC = Space[0xFFFC] | (Space[0xFFFD] << 8);
            Cpu.S = 0xFF;
        }

        public void Play(int code)
        {
            var init = Log.Where(x => x.chip != 2).Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            Send(code & 0xFF);
        }

        public void Send(int code)
        {
            latch = code;
            Nmis++;
            Cpu.Interrupt(0xFFFA);
        }

        // OPL 타이머: 1 = 72 × 4 클럭 × (256 − T1), 2 = 그 4배
        private long T1Period { get { return Math.Max(1, (long)(288.0 * (256 - timer1) / OplClock * CpuClock)); } }
        private long T2Period { get { return Math.Max(1, (long)(1152.0 * (256 - timer2) / OplClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextT1) { nextT1 += T1Period; if ((timerCtl & 0x40) == 0) timerFlags |= 0x40; }
                if (now >= nextT2) { nextT2 += T2Period; if ((timerCtl & 0x20) == 0) timerFlags |= 0x20; }
                if (timerFlags != 0 && !Cpu.I) { Irqs++; Cpu.Interrupt(0xFFFE); }   // IRQ 줄 = OPL (레벨)
                if (Cpu.Jammed) { Cpu.Cycles = end; break; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a >= RomStart) return Space[a];
            if (a < 0x800 && !(L == Karnov && a >= 0x600)) return Ram[a];
            if (a == L.Latch) { LatchReads++; return (byte)latch; }
            if (a == L.Opn || a == L.Opn + 1) return 0;   // YM2203 상태: 바쁘지 않음 · 타이머 없음
            if (a == L.Opl || a == L.Opl + 1) return (byte)(timerFlags != 0 ? 0x80 | timerFlags : 0);
            if (Pcm.Length > 0 && a == L.Oki) return OkiStatus();
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a < 0x800 && !(L == Karnov && a >= 0x600)) { Ram[a] = v; return; }
            if (a == L.Opn) { opnAddr = v; return; }
            if (a == L.Opn + 1) { Emit(0, opnAddr, v); return; }
            if (a == L.Opl) { oplAddr = v; return; }
            if (a == L.Opl + 1) { OplWrite(oplAddr, v); return; }
            if (Pcm.Length > 0 && a == L.Oki) { OkiWrite(v); return; }
            if (a >= RomStart) return;
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        private void OplWrite(int reg, int v)
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
            if (oplRegs.TryGetValue(reg, out int prev) && prev == v) return;   // OPL은 같은 값 다시 쓰기가 아무 일 없음
            oplRegs[reg] = v;
            Emit(1, reg, v);
        }

        private double OkiRate { get { return OkiClock / 132.0; } }
        private int PcmByte(long a) { return a >= 0 && a < Pcm.Length ? Pcm[a] : 0; }

        private void OkiWrite(int d)
        {
            Emit(2, 0x00, d);
            if (okiPending >= 0)
            {
                int phrase = okiPending;
                okiPending = -1;
                int voices = (d >> 4) & 15;
                long t = phrase * 8;
                long start = ((PcmByte(t) << 16) | (PcmByte(t + 1) << 8) | PcmByte(t + 2)) & 0x3FFFF;
                long stop = ((PcmByte(t + 3) << 16) | (PcmByte(t + 4) << 8) | PcmByte(t + 5)) & 0x3FFFF;
                long cycles = (long)(Math.Max(0, stop - start + 1) * 2 / OkiRate * CpuClock);
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

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = L.Name + " (hoot)", Notes = "hoot " + L.Name };
            w.SetClock(VgmWriter.ClockYm2203, OpnClock);
            if (Ym3526) w.SetClock(VgmWriter.ClockYm3526, OplClock); else w.SetClock(VgmWriter.ClockYm3812, OplClock);
            bool oki = Pcm.Length > 0 && Log.Any(x => x.chip == 2);
            if (oki)
            {
                w.SetClock(VgmWriter.ClockOkim6295, OkiClock, 0x80000000u);   // 핀7 높음 (÷132)
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
                if (x.chip == 2 && !oki) continue;
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x55, x.reg, x.val);
                else if (x.chip == 1) w.Write(Ym3526 ? (byte)0x5B : (byte)0x5A, x.reg, x.val);
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
