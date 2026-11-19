using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 캡콤 CPS-2 (driver capcom, type cps2): QSound 사운드 보드를 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME cps2 qsound_sub_map, 사운드 ROM을 읽어 확인): Z80 8MHz, 0000h~7FFFh = ROM, 8000h~BFFFh = 뱅크 (D003h 아래 4비트 → ROM 8000h + n × 4000h),
/// C000h~CFFFh = 68000과 같이 쓰는 RAM, D000h · D001h = QSound 데이터 위 · 아래 바이트, D002h = 레지스터 (쓰면 기록), D007h 읽기 = 준비 (80h), F000h~FFFFh = RAM.
/// IRQ = 250Hz (IM 1). 부팅 핸드셰이크 = Z80이 CFFFh ← 77h, 68000이 FFh로 답함. 명령 = 68000이 C000h~C00Eh에 16바이트 묶음을 쓰고 C00Fh ← 00h (드라이버가 큐로 옮기고 FFh로), CFFDh = 88h (68000 준비).</para>
/// <para>곡: rom code = Z80 ROM, pcm = QSound ROM (MAME ROM_LOAD16_WORD_SWAP처럼 두 바이트씩 바꿔 둔다). 180초를 QSound (DSP 없는 HLE) VGM으로.</para>
/// </summary>
public static class HootCps2
{
    public const double CpuClock = 8000000, QsClock = 4000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "capcom", StringComparison.OrdinalIgnoreCase) && string.Equals((type ?? "").Trim(), "cps2", StringComparison.OrdinalIgnoreCase);
    }

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file, int layout = 0)
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
        var q = Join(pcm, 0);
        for (int i = 0; i + 1 < q.Length; i += 2) { byte t = q[i]; q[i] = q[i + 1]; q[i + 1] = t; }
        var m = new Machine(Join(code, 0x10000), q) { Layout = layout };
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom, Pcm;
        public readonly byte[] Shared = new byte[0x1000], Ram = new byte[0x1000];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>QSound 쓰기 (시각, 레지스터, 16비트 값).</summary>
        public readonly List<(double t, int reg, int val)> Log = new List<(double, int, int)>();
        public int Irqs, Taken;
        /// <summary>명령 묶음 모양: 0 = C000h 00h · C001h 코드, 1 = C000h 코드.</summary>
        public int Layout;
        public Dictionary<int, int> PcCount;

        private int bank, dataHi, dataLo;
        private long playStart = -1;
        private long nextIrq;
        private bool irqPending;
        private readonly long irqPeriod = (long)(CpuClock / 250);

        public Machine(byte[] rom, byte[] pcm)
        {
            Rom = rom; Pcm = pcm;
            Shared[0xFFD] = 0x88;   // 68000 준비
            Shared[0x00F] = 0xFF;   // 명령 없음
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            nextIrq = irqPeriod;
        }

        public void Play(int code)
        {
            Log.Clear();
            playStart = Cpu.Cycles;
            Send(code);
        }

        /// <summary>68000처럼 명령 묶음을 쓴다 (드라이버가 C00Fh를 FFh로 되돌려 둔 뒤).</summary>
        public void Send(int code)
        {
            for (int i = 0; i < 0x0F; i++) Shared[i] = 0;
            if (Layout == 0) { Shared[0] = (byte)(code >> 8); Shared[1] = (byte)code; }
            else Shared[0] = (byte)code;
            Shared[0x0F] = 0x00;
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextIrq) { nextIrq += irqPeriod; irqPending = true; }
                if (irqPending && Cpu.Iff1)
                {
                    irqPending = false;
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted) { Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, nextIrq)); continue; }
                if (PcCount != null) { PcCount.TryGetValue(Cpu.PC, out int k); PcCount[Cpu.PC] = k + 1; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x8000) return Rom[a % Rom.Length];
            if (a < 0xC000) return Rom[(0x8000 + (long)bank * 0x4000 + (a - 0x8000)) % Rom.Length];
            if (a < 0xD000) return Shared[a - 0xC000];
            if (a >= 0xF000) return Ram[a - 0xF000];
            if (a == 0xD007) return 0x80;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0xC000 && a < 0xD000)
            {
                if (a == 0xC00F && v == 0xFF && Shared[0x0F] != 0xFF) Taken++;
                Shared[a - 0xC000] = v;
                if (a == 0xCFFF && v == 0x77) Shared[0xFFF] = 0xFF;   // 68000 핸드셰이크: Z80이 77h를 쓰면 FFh로 답한다
                return;
            }
            if (a >= 0xF000) { Ram[a - 0xF000] = v; return; }
            switch (a)
            {
                case 0xD000: dataHi = v; return;
                case 0xD001: dataLo = v; return;
                case 0xD002:
                    if (playStart < 0) playStart = Cpu.Cycles;
                    Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), v, (dataHi << 8) | dataLo));
                    return;
                case 0xD003: bank = v & 0x0F; return;
            }
        }

        public byte In(int port) { return 0xFF; }
        public void Out(int port, byte v) { }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Capcom CPS-2 (hoot)", Notes = "hoot capcom cps2 (QSound HLE)" };
            w.SetClock(VgmWriter.ClockQsound, QsClock);
            if (Pcm.Length > 0)
            {
                var cmd = new byte[7 + 8 + Pcm.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8F;
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
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(0xC4, (byte)(x.val >> 8), (byte)x.val, (byte)x.reg);
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
