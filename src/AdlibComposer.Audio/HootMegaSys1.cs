using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 잘레코 Mega System 1 (driver jaleco, type megasys1): 사운드 보드를 그대로 흉내 내고 68000 사운드 프로그램을 돌린다 (X68000용 MC68000 다시 씀).
/// <para>기계 (MAME megasys1 A · B · C sound_map): 68000 7MHz, 000000h~01FFFFh = ROM (rom code 두 개 = 짝수 · 홀수 바이트), 040000h (System C는 060000h) 읽기 = 메인 CPU의 명령,
/// 080000h~080003h = YM2151 (아랫 바이트: 080001h 주소 · 080003h 데이터 · 읽기 = 상태), 0A0001h · 0C0001h = OKIM6295 #1 · #2 (각 4MHz, 핀7 높음),
/// 0E0000h~0FFFFFh = RAM. 명령 · YM2151 타이머 = IRQ 4 (자동 벡터).</para>
/// <para>곡: 부팅 1초 뒤 곡 코드를 명령으로, 180초를 YM2151 + OKIM6295 둘 VGM으로.</para>
/// </summary>
public static class HootMegaSys1
{
    public const double CpuClock = 7000000, YmClock = 3500000, OkiClock = 4000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "jaleco", StringComparison.OrdinalIgnoreCase) && string.Equals((type ?? "").Trim(), "megasys1", StringComparison.OrdinalIgnoreCase);
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
        var p1 = new List<(long off, byte[] d)>();
        var p2 = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            var list = type == "code" ? code : type == "pcm1" ? p1 : type == "pcm2" ? p2 : null;
            if (list == null) { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            list.Add((off, d));
        }
        // 68000 ROM: hoot 목록 차례 (offset 0 · 10000h) = 짝수 · 홀수 바이트 (MAME ROM_LOAD16_BYTE)
        var rom = new byte[0x20000];
        var ordered = code.OrderBy(c => c.off).ToList();
        if (ordered.Count >= 2)
        {
            for (int i = 0; i < ordered[0].d.Length && i * 2 < rom.Length; i++) rom[i * 2] = ordered[0].d[i];
            for (int i = 0; i < ordered[1].d.Length && i * 2 + 1 < rom.Length; i++) rom[i * 2 + 1] = ordered[1].d[i];
        }
        else missing.Add("(code 짝수 · 홀수)");
        byte[] Join(List<(long off, byte[] d)> parts)
        {
            long size = parts.Count == 0 ? 0 : parts.Max(p => p.off + p.d.Length);
            var r = new byte[size];
            foreach (var (off, d) in parts) Array.Copy(d, 0, r, off, d.Length);
            return r;
        }
        var m = new Machine(rom, Join(p1), Join(p2));
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IM68kBus
    {
        public readonly byte[] Rom, Pcm1, Pcm2;
        public readonly byte[] Ram = new byte[0x20000];
        public readonly M68000 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151, 2 = OKI #1, 3 = OKI #2 (OKI 레지스터 0 = 명령).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public readonly HootOkiVoices Oki1, Oki2;
        public int LatchIrqs, YmIrqs, LatchReads;

        private int latch, ymAddr;
        private bool latchPending;
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;

        public Machine(byte[] rom, byte[] pcm1, byte[] pcm2)
        {
            Rom = rom; Pcm1 = pcm1; Pcm2 = pcm2;
            Oki1 = new HootOkiVoices(pcm1, OkiClock, CpuClock);
            Oki2 = new HootOkiVoices(pcm2, OkiClock, CpuClock);
            Cpu = new M68000(this) { AddressErrors = false };
            Cpu.Reset();
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            Send(code & 0xFF);
        }

        public void Send(int code) { latch = code; latchPending = true; }

        private long TimerAPeriod { get { return Math.Max(1, (long)(64.0 * (1024 - timerA) / YmClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(1024.0 * (256 - timerB) / YmClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                Cpu.IrqLevel = latchPending || timerFlags != 0 ? 4 : 0;
                Cpu.Step();
            }
        }

        /// <summary>IRQ 4 받음: 명령 인터럽트는 HOLD_LINE (받으면 내림), YM2151은 플래그를 지울 때까지.</summary>
        public int Acknowledge(int level)
        {
            if (latchPending) { latchPending = false; LatchIrqs++; } else YmIrqs++;
            return -1;   // 자동 벡터
        }

        public byte Read8(uint a)
        {
            if (a < 0x20000) return Rom[a];
            if (a >= 0xE0000 && a < 0x100000) return Ram[a - 0xE0000];
            switch (a & 0xFFFFE)
            {
                case 0x040000: case 0x060000: if ((a & 1) != 0) { LatchReads++; return (byte)latch; } return 0;   // 060000h = System C
                case 0x080000: case 0x080002: return (byte)(timerFlags & 3);   // YM2151 상태 (바쁨 없음)
                case 0x0A0000: case 0x0A0002: return Oki1.Status(Cpu.Cycles);
                case 0x0C0000: case 0x0C0002: return Oki2.Status(Cpu.Cycles);
            }
            return 0xFF;
        }

        public ushort Read16(uint a) { return (ushort)((Read8(a) << 8) | Read8(a + 1)); }

        public void Write8(uint a, byte v)
        {
            if (a >= 0xE0000 && a < 0x100000) { Ram[a - 0xE0000] = v; return; }
            if ((a & 1) == 0) return;   // 칩은 아랫 바이트
            switch (a & 0xFFFFE)
            {
                case 0x080000: ymAddr = v; return;
                case 0x080002: YmWrite(ymAddr, v); return;
                case 0x0A0000: case 0x0A0002: Oki1.Write(v, Cpu.Cycles); Emit(2, 0, v); return;
                case 0x0C0000: case 0x0C0002: Oki2.Write(v, Cpu.Cycles); Emit(3, 0, v); return;
            }
        }

        public void Write16(uint a, ushort v) { Write8(a, (byte)(v >> 8)); Write8(a + 1, (byte)v); }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        private void YmWrite(int reg, int v)
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
                        return;
                    }
            }
            Emit(0, reg, v);
        }

        /// <summary>진단: YM2151 타이머 레지스터.</summary>
        public string TimerInfo { get { return "TA " + timerA + " TB " + timerB + " ctl " + timerCtl.ToString("X2") + " flags " + timerFlags; } }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Jaleco Mega System 1 (hoot)", Notes = "hoot jaleco megasys1" };
            w.SetClock(VgmWriter.ClockYm2151, YmClock);
            bool o1 = Pcm1.Length > 0, o2 = Pcm2.Length > 0;
            if (o1 || o2) w.SetClock(VgmWriter.ClockOkim6295, OkiClock, 0x80000000u | (o1 && o2 ? 0x40000000u : 0));
            void Block(byte[] data, bool second)
            {
                var cmd = new byte[7 + 8 + data.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8B;
                uint size = (uint)(8 + data.Length) | (second ? 0x80000000u : 0);
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int total = data.Length;
                cmd[7] = (byte)total; cmd[8] = (byte)(total >> 8); cmd[9] = (byte)(total >> 16); cmd[10] = (byte)(total >> 24);
                Array.Copy(data, 0, cmd, 15, data.Length);
                w.Write(cmd);
            }
            if (o1) Block(Pcm1, false);
            if (o2) Block(Pcm2, o1);
            double total2 = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                switch (x.chip)
                {
                    case 0: w.Write(0x54, x.reg, x.val); break;
                    case 2: if (o1) w.Write(0xB8, x.reg, x.val); break;
                    case 3: if (o2) w.Write(0xB8, o1 ? x.reg | 0x80 : x.reg, x.val); break;
                }
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
