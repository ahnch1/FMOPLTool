using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 테크모 Ninja Gaiden 사운드 보드 (driver tecmo, type gaiden: Shadow Warriors · 龍剣伝 · Tecmo Knight · Raiga Strato Fighter):
/// 사운드 보드를 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME gaiden sound_map): Z80 4MHz, 0000h~DFFFh = ROM, F000h~F7FFh = RAM (SP F800h), F800h = OKIM6295 (1MHz, 핀7 높음),
/// F810h · F811h = YM2203 #1, F820h · F821h = YM2203 #2 (각 4MHz), FC00h = 와치독, FC20h 읽기 = 명령 (68000이 쓰면 NMI).
/// YM2203 #1 타이머 = IRQ (IM 1, #2의 IRQ는 연결 안 됨).</para>
/// <para>곡: rom code = Z80 ROM, pcm = OKI ROM. 부팅 1초 뒤 곡 코드를 명령으로, 180초를 YM2203 둘 (VGM 클럭 bit30 + A5h) + OKIM6295 VGM으로.</para>
/// </summary>
public static class HootGaiden
{
    public const double CpuClock = 4000000, OpnClock = 4000000, OkiClock = 1000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "tecmo", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "gaiden", StringComparison.OrdinalIgnoreCase);
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
            if (type == "code") { Array.Copy(d, 0, rom, off, (int)Math.Max(0, Math.Min(d.Length, 0xE000 - off))); code = true; }
            else pcm.Add((off, d));
        }
        if (!code) missing.Add("(code)");
        var p = new byte[pcm.Count == 0 ? 0 : pcm.Max(x => x.off + x.d.Length)];
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
        public readonly byte[] Ram = new byte[0x800];
        public readonly Z80 Cpu;
        public readonly HootOkiVoices Oki;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2203 #1, 1 = YM2203 #2, 2 = OKIM6295 (VGM 레지스터 0 = 명령).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, LatchReads;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>();

        private int latch;
        private readonly int[] addr = new int[2];
        private long playStart = -1;
        // YM2203 두 칩의 타이머: A = 72 × (1024 − TA) / 클럭, B = 1152 × (256 − TB) / 클럭
        private readonly int[] tA = new int[2], tB = new int[2], tCtl = new int[2], tFlags = new int[2];
        private readonly long[] nextA = { long.MaxValue, long.MaxValue }, nextB = { long.MaxValue, long.MaxValue };

        public Machine(byte[] rom, byte[] pcm)
        {
            Rom = rom; Pcm = pcm ?? new byte[0];
            Oki = new HootOkiVoices(Pcm, OkiClock, CpuClock);
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
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

        private long PeriodA(int c) { return Math.Max(1, (long)(72.0 * (1024 - tA[c]) / OpnClock * CpuClock)); }
        private long PeriodB(int c) { return Math.Max(1, (long)(1152.0 * (256 - tB[c]) / OpnClock * CpuClock)); }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                for (int c = 0; c < 2; c++)
                {
                    if (now >= nextA[c]) { nextA[c] += PeriodA(c); if ((tCtl[c] & 4) != 0) tFlags[c] |= 1; }
                    if (now >= nextB[c]) { nextB[c] += PeriodB(c); if ((tCtl[c] & 8) != 0) tFlags[c] |= 2; }
                }
                if (tFlags[0] != 0 && Cpu.Iff1)   // IRQ = YM2203 #1만 (둘째는 연결 안 됨 — OR로 하면 드라이버가 #2를 안 지워 IRQ가 계속 → 스택이 넘침)
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
                    long next = Math.Min(Math.Min(nextA[0], nextB[0]), Math.Min(nextA[1], nextB[1]));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, next));
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0xE000) return Rom[a];
            if (a >= 0xF000 && a < 0xF800) return Ram[a - 0xF000];
            switch (a)
            {
                case 0xF800: return Oki.Status(Cpu.Cycles);
                case 0xF810: return (byte)(tFlags[0] & 3);   // YM2203 #1 상태 (바쁨 없음)
                case 0xF820: return (byte)(tFlags[1] & 3);
                case 0xF811: case 0xF821: return 0;
                case 0xFC20: LatchReads++; return (byte)latch;
            }
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0xF000 && a < 0xF800) { Ram[a - 0xF000] = v; return; }
            switch (a)
            {
                case 0xF800: Oki.Write(v, Cpu.Cycles); Emit(2, 0, v); return;
                case 0xF810: addr[0] = v; return;
                case 0xF811: OpnWrite(0, addr[0], v); return;
                case 0xF820: addr[1] = v; return;
                case 0xF821: OpnWrite(1, addr[1], v); return;
                case 0xFC00: return;   // 와치독
            }
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int port) { return 0xFF; }
        public void Out(int port, byte v) { }

        private void OpnWrite(int c, int reg, int v)
        {
            switch (reg)
            {
                case 0x24: tA[c] = (tA[c] & 3) | (v << 2); return;
                case 0x25: tA[c] = (tA[c] & 0x3FC) | (v & 3); return;
                case 0x26: tB[c] = v; return;
                case 0x27:
                    {
                        int old = tCtl[c];
                        tCtl[c] = v;
                        if ((v & 0x10) != 0) tFlags[c] &= ~1;
                        if ((v & 0x20) != 0) tFlags[c] &= ~2;
                        if ((v & 1) != 0 && (old & 1) == 0) nextA[c] = Cpu.Cycles + PeriodA(c); else if ((v & 1) == 0) nextA[c] = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) nextB[c] = Cpu.Cycles + PeriodB(c); else if ((v & 2) == 0) nextB[c] = long.MaxValue;
                        Emit(c, reg, v & 0xC0);   // 3채널 모드 비트만
                        return;
                    }
            }
            // SSG 레지스터 (00h~0Ch)는 드라이버가 틱마다 거의 다 다시 씀 — 같은 값은 아무 일 없으므로 바뀔 때만 (0Dh 엔벌로프 모양은 쓰면 다시 시작이라 늘)
            if (reg < 0x0D)
            {
                int k = c * 16 + reg;
                if (ssg[k] == v) return;
                ssg[k] = v;
            }
            Emit(c, reg, v);
        }

        private readonly int[] ssg = Enumerable.Repeat(-1, 32).ToArray();

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Tecmo Ninja Gaiden (hoot)", Notes = "hoot tecmo gaiden" };
            w.SetClock(VgmWriter.ClockYm2203, OpnClock, 0x40000000u);   // bit30 = YM2203 둘
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
                w.Write(x.chip == 0 ? (byte)0x55 : x.chip == 1 ? (byte)0xA5 : (byte)0xB8, x.reg, x.val);
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
