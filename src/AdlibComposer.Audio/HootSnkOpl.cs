using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot SNK Z80 + OPL 둘 보드 (driver snk, type 3526_8950: Victory Road · Psycho Soldier · Bermuda Triangle · TouchDown Fever · Guerrilla War,
/// type 3526x2: Athena · Ikari Warriors, type fitegolf: Fighting Golf · Country Club — YM3812 하나 (MAME YM3812_sound_map), VGM YM3812 그대로): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME snk.cpp YM3526_YM3526_sound_map · YM3526_Y8950_sound_map): Z80 4MHz, 0000h~BFFFh ROM, C000h~CFFFh RAM, E000h = 명령,
/// E800h 상태 · 주소 / EC00h 자료 = YM3526 #1, F000h · F400h = YM #2 (YM3526 또는 Y8950), F800h = 사운드 상태 (bit0 = YM1 IRQ, bit1 = YM2 IRQ, bit2 = 바쁨, bit3 = 명령 IRQ;
/// 쓰기 bit4 · 5 · 6 · 7이 0이면 각각 지움). IRQ 줄 = 상태 &amp; 0Bh (레벨).</para>
/// <para>VGM 재생기에 OPL이 하나라 YMF262 (16MHz) 하나로 옮긴다: 리듬 (BDh)을 쓰는 칩 = 뱅크 0, 다른 칩 = 뱅크 1, C0h에 왼 · 오 bit, 파형 = 0 (YM3526 · Y8950에 없음).
/// Y8950 ADPCM은 아직 (FM만). 곡: 부팅 1초 뒤 명령, 180초.</para>
/// </summary>
public static class HootSnkOpl
{
    public const double CpuClock = 4000000, OplClock = 4000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "snk", StringComparison.OrdinalIgnoreCase)) return false;
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "3526_8950" || t == "3526x2" || t == "fitegolf";
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
        var rom = new byte[0xC000];
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type == "pcm") { unsupported.Add("pcm (Y8950 ADPCM)"); continue; }
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0 || off >= 0xC000) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            Array.Copy(d, 0, rom, off, (int)Math.Min(d.Length, 0xC000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        var m = new Machine(rom) { Single3812 = (e.Type ?? "").Trim().ToLowerInvariant() == "fitegolf" };
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        // 게임이 켤 때 보내는 명령 0Dh (00h~0Fh = 설정: bit0 = 켬 · bit1 = 끔, bit2 = 음악, bit3 = 효과음) — 이것 없이는 드라이버가 곡 명령을 버림
        m.Command(0x0D);
        m.RunSeconds(0.25);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        private sealed class Opl
        {
            public int Addr, T1, T2, Ctl, Flags;
            public long Next1 = long.MaxValue, Next2 = long.MaxValue;
        }

        public readonly byte[] Rom;
        public readonly byte[] Ram = new byte[0x1000];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>OPL 쓰기: chip 0 = YM #1 (E800h), 1 = YM #2 (F000h).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, LatchReads;
        /// <summary>fitegolf: YM3812 하나 (YM #2 없음) → VGM YM3812 (5Ah) 그대로.</summary>
        public bool Single3812;
        /// <summary>진단: 타이머 상태.</summary>
        public string TimerState { get { return string.Join(" ", ym.Select((y, i) => "ym" + (i + 1) + " ctl " + y.Ctl.ToString("X2") + " t1 " + y.T1.ToString("X2") + " t2 " + y.T2.ToString("X2") + " fl " + y.Flags.ToString("X2"))) + " st " + status.ToString("X2") + " c100 " + Ram[0x100].ToString("X2") + " c002 " + Ram[2].ToString("X2"); } }
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private readonly Opl[] ym = { new Opl(), new Opl() };
        private int latch, status;
        private long playStart = -1;
        private readonly int[,] last = new int[2, 0x100];

        public Machine(byte[] rom)
        {
            Rom = rom;
            for (int c = 0; c < 2; c++) for (int r = 0; r < 0x100; r++) last[c, r] = -1;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        /// <summary>곡 시작 전 명령 (기록은 남김).</summary>
        public void Command(int code) { latch = code & 0xFF; status |= 0x0C; }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            latch = code & 0xFF;
            status |= 0x0C;   // 명령 IRQ + 바쁨
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        private static long Period(int ticks, int count) { return Math.Max(1, (long)((double)ticks * (256 - count) / OplClock * CpuClock)); }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                for (int c = 0; c < 2; c++)
                {
                    var y = ym[c];
                    if (now >= y.Next1) { y.Next1 += Period(288, y.T1); if ((y.Ctl & 0x40) == 0) { y.Flags |= 0xC0; status |= 1 << c; } }
                    if (now >= y.Next2) { y.Next2 += Period(1152, y.T2); if ((y.Ctl & 0x20) == 0) { y.Flags |= 0xA0; status |= 1 << c; } }
                }
                if ((status & 0x0B) != 0 && Cpu.Iff1)
                {
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted && (status & 0x0B) == 0)
                {
                    long next = Math.Min(Math.Min(ym[0].Next1, ym[0].Next2), Math.Min(ym[1].Next1, ym[1].Next2));
                    if (next >= end) { Cpu.Cycles = end; break; }
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 4, next);
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0xC000) return Rom[a];
            if (a < 0xD000) return Ram[a - 0xC000];
            if (a == 0xE000) { LatchReads++; return (byte)latch; }
            if (a == 0xE800) return (byte)ym[0].Flags;
            if (a == 0xF000) return (byte)ym[1].Flags;
            if (a == 0xF800) return (byte)status;
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0xC000 && a < 0xD000) { Ram[a - 0xC000] = v; return; }
            if (a < 0xC000) return;
            switch (a)
            {
                case 0xE800: ym[0].Addr = v; return;
                case 0xEC00: OplWrite(0, v); return;
                case 0xF000: ym[1].Addr = v; return;
                case 0xF400: OplWrite(1, v); return;
                case 0xF800:
                    if ((v & 0x10) == 0) status &= ~1;
                    if ((v & 0x20) == 0) status &= ~2;
                    if ((v & 0x40) == 0) status &= ~4;
                    if ((v & 0x80) == 0) status &= ~8;
                    return;
            }
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int p) { return 0xFF; }
        public void Out(int p, byte v) { }

        private void OplWrite(int c, int v)
        {
            var y = ym[c];
            int reg = y.Addr;
            switch (reg)
            {
                case 0x02: y.T1 = v; return;
                case 0x03: y.T2 = v; return;
                case 0x04:
                    {
                        if ((v & 0x80) != 0) { y.Flags = 0; return; }
                        int old = y.Ctl;
                        y.Ctl = v;
                        if ((v & 1) != 0 && (old & 1) == 0) y.Next1 = Cpu.Cycles + Period(288, y.T1); else if ((v & 1) == 0) y.Next1 = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) y.Next2 = Cpu.Cycles + Period(1152, y.T2); else if ((v & 2) == 0) y.Next2 = long.MaxValue;
                        return;
                    }
            }
            if (reg < 0x20 && reg != 0x08 && !(Single3812 && reg == 0x01)) return;   // 시험 · Y8950 ADPCM · 키보드 · I/O (YM3812 하나면 01h 파형 허락은 남김)
            bool keep = (reg >= 0xB0 && reg <= 0xB8) || reg == 0xBD;
            if (!keep) { if (last[c, reg] == v) return; last[c, reg] = v; }
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), c, reg, v));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            if (Single3812) return ToVgm3812(seconds, fade);
            var w = new VgmWriter { System = "SNK OPL × 2 (hoot)", Notes = "hoot snk" };
            w.SetClock(VgmWriter.ClockYmf262, OplClock * 4);
            w.Write(0x5F, 0x05, 0x01);   // NEW = 1
            // 리듬 (BDh bit5)을 쓰는 칩을 뱅크 0에
            bool rhythm2 = Log.Any(x => x.chip == 1 && x.reg == 0xBD && (x.val & 0x20) != 0);
            bool rhythm1 = Log.Any(x => x.chip == 0 && x.reg == 0xBD && (x.val & 0x20) != 0);
            int bank0 = rhythm2 && !rhythm1 ? 1 : 0;
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                int bank = x.chip == bank0 ? 0 : 1, r = x.reg, v = x.val;
                if (r >= 0xE0 && r <= 0xF5) v = 0;
                if (r >= 0xC0 && r <= 0xC8) v = (v & 0x0F) | 0x30;
                if (bank == 1 && (r == 0xBD || r == 0x08)) continue;
                w.Write((byte)(bank == 0 ? 0x5E : 0x5F), r, v);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var song = VgmFormat.Read(w.ToArray());
            song.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) song.UnsupportedChips.Add("(칩 쓰기 없음)");
            return song;
        }

        private VgmSong ToVgm3812(double seconds, double fade)
        {
            var w = new VgmWriter { System = "SNK YM3812 (hoot)", Notes = "hoot snk fitegolf" };
            w.SetClock(VgmWriter.ClockYm3812, OplClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.Where(x => x.chip == 0).OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(0x5A, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var song = VgmFormat.Read(w.ToArray());
            song.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) song.UnsupportedChips.Add("(칩 쓰기 없음)");
            return song;
        }
    }
}
