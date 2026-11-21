using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot X1 (driver x1, type psg · opm): 문서가 없어 hoot 목록의 시작 코드를 거꾸로 읽어 맞춘 규약.
/// <para>기계: Z80 4MHz, 64KB 전부 RAM, I/O는 16비트 주소 (OUT (C) = BC). 칩: PSG (1C00h 레지스터 · 1B00h 데이터, 2MHz), OPM (0700h 주소 · 0701h 데이터, 4MHz).
/// 인터럽트 = Z80 CTC (본체 1FA0h~1FA3h, FM 보드 0704h~0707h) IM 2: 채널 0에 벡터 바이트(bit0 = 0), 제어어 (bit7 인터럽트 · bit6 카운터 · bit5 ÷256 · bit2 시간 상수 따라옴 · bit1 리셋),
/// 타이머 = 4MHz ÷ 16/256 ÷ 시간 상수, 채널 3 카운터 = 채널 0이 0이 될 때마다 (X1 배선). 옵션 use_ctcN = 시작 코드가 시간 상수를 안 쓸 때 쓸 값.
/// I/O 2000h~FFFFh = VRAM (쓰고 읽는 메모리), 8255 PPI 1A01h = 수직 귀선 · 서브 CPU 늘 준비됨, 서브 CPU 1900h 읽기 = 0. 벡터 칸이 0이면 넘긴다. FM 보드 (OPM · 0704h CTC)는 opm 판에만 꽂는다.</para>
/// <para>hoot 포트 (상위 바이트 0): IN 0000h = 명령 (1 = 재생; 읽으면 비움), OUT 0000h = 받음, IN 0001h = 곡 코드 아래 바이트 (0002h · 0003h = 그 위),
/// OUT 0001h,n = bgm n을 mdata_addr에 (없으면 4000h) + voice n을 vdata_addr에, IN 000Fh = 곡 코드 맨 위 바이트.</para>
/// <para>rom: code · data (주소에 파일), binary, bgm, voice. 180초 미리 렌더 → VGM (AY-3-8910 / YM2151).</para>
/// </summary>
public static class HootX1
{
    public const double CpuClock = 4000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        int mul = Math.Max(1, e.Option("clock_mul", 1));
        var m = new Machine(mul)
        {
            Opm = e.Type.StartsWith("opm", StringComparison.OrdinalIgnoreCase),
            DataAddr = e.Option("mdata_addr", 0x4000) & 0xFFFF,
            DataSize = Math.Max(0, e.Option("mdata_size", 0)),
            FileSize = Math.Max(0, e.Option("mfile_size", 0)),
            VoiceAddr = e.Options.ContainsKey("vdata_addr") ? e.Option("vdata_addr", 0) & 0xFFFF : -1,
            VoiceSize = Math.Max(0, e.Option("vdata_size", 0)),
            VoiceFileSize = Math.Max(0, e.Option("vfile_size", 0)),
        };
        for (int ch = 0; ch < 4; ch++)
        {
            int tc = e.Option("use_ctc" + ch, e.Option("ctc" + ch, -1));
            if (tc >= 0) m.DefaultTc[ch] = tc & 0xFF;
        }
        var bgm = new Dictionary<int, string>();
        var voice = new Dictionary<int, string>();
        foreach (var (type, off, text) in e.Roms)
        {
            switch (type)
            {
                case "code":
                    {
                        var d = file(text);
                        if (d == null) { m.Missing.Add(text); break; }
                        m.Put((int)off, d);
                        break;
                    }
                case "data":
                    {
                        // hoot mucomx1.cpp: data = "I/O port memory" (KOEI 드라이버가 IN r,(C)로 곡 표를 읽는다)
                        var d = file(text);
                        if (d == null) { m.Missing.Add(text); break; }
                        for (int i = 0; i < d.Length && off + i < 0x10000; i++) m.IoRam[(int)off + i] = d[i];
                        break;
                    }
                case "binary":
                    {
                        var d = HexBytes(text) ?? file(text);
                        if (d != null) m.Put((int)off, d);
                        break;
                    }
                case "bgm": bgm[(int)off] = text; break;
                case "voice": voice[(int)off] = text; break;
                default: m.Unsupported.Add(type); break;
            }
        }
        byte[] FileOf(Dictionary<int, string> names, int n)
        {
            if (!names.TryGetValue(n, out var name)) return null;
            var d = file(name);
            if (d == null && m.Missing.Count < 64) m.Missing.Add(name);
            return d;
        }
        m.Bgm = n => FileOf(bgm, n);
        m.Voice = n => FileOf(voice, n);
        m.Start(e.Option("init_pc", 0) & 0xFFFF);
        m.Run((long)(m.CpuHz * 3), untilWaiting: true);
        return m;
    }

    private static byte[] HexBytes(string s)
    {
        var parts = (s ?? "").Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var b = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[i].Substring(2) : parts[i];
            if (p.Length == 0 || p.Length > 2 || !byte.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out b[i])) return null;
        }
        return b;
    }

    /// <summary>Z80 CTC 하나 (채널 4개).</summary>
    public sealed class Ctc
    {
        public int Vector;
        public readonly int[] Control = new int[4], Tc = new int[4];
        public readonly long[] Next = { long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue };
        public readonly int[] Count = new int[4];
        public readonly bool[] WaitTc = new bool[4], Pending = new bool[4];
        public readonly int[] DefaultTc = { -1, -1, -1, -1 };

        private static int Prescale(int c) { return (c & 0x20) != 0 ? 256 : 16; }
        public static bool CounterMode(int c) { return (c & 0x40) != 0; }

        public long Period(int ch) { return (long)Prescale(Control[ch]) * (Tc[ch] == 0 ? 256 : Tc[ch]); }

        public void Write(int ch, int v, long now)
        {
            if (WaitTc[ch])
            {
                WaitTc[ch] = false;
                Control[ch] &= ~0x02;   // 시간 상수를 받으면 리셋이 풀리고 다시 센다
                Tc[ch] = v;
                Count[ch] = v == 0 ? 256 : v;
                if (!CounterMode(Control[ch])) Next[ch] = now + Period(ch);
                return;
            }
            if ((v & 1) == 0)
            {
                if (ch == 0) Vector = v & 0xF8;
                return;
            }
            Control[ch] = v;
            if ((v & 0x02) != 0) Next[ch] = long.MaxValue;   // 리셋: 멈춤
            if ((v & 0x04) != 0)
            {
                WaitTc[ch] = true;
                if (DefaultTc[ch] >= 0)
                {
                    // hoot 옵션 use_ctcN: 시작 코드가 시간 상수를 안 쓰면 이 값으로 돈다 (다음 쓰기는 그대로 시간 상수로 받아 바꾼다)
                    Tc[ch] = DefaultTc[ch];
                    Control[ch] &= ~0x02;
                    Count[ch] = Tc[ch] == 0 ? 256 : Tc[ch];
                    if (!CounterMode(v)) Next[ch] = now + Period(ch);
                }
                else if (CounterMode(v)) Count[ch] = 256;   // 시간 상수가 안 와도 카운터는 256으로 센다 (x1hydlide: ch3 D5h 뒤 시간 상수 없음)
            }
            else if ((v & 0x02) == 0 && Tc[ch] != 0 && !CounterMode(v)) Next[ch] = now + Period(ch);
            if ((v & 0x80) == 0) Pending[ch] = false;
        }

        /// <summary>채널 읽기 = 지금 내려가는 수 (드라이버가 CTC가 있는지 시간 상수를 써 보고 읽어서 확인한다).</summary>
        public byte Read(int ch, long now)
        {
            if (CounterMode(Control[ch]) || Next[ch] == long.MaxValue) return (byte)Count[ch];
            int pre = Prescale(Control[ch]);
            long left = Math.Max(0, Next[ch] - now);
            return (byte)Math.Min(255, (left + pre - 1) / pre);
        }

        /// <summary>채널 ch가 0이 됨: 인터럽트 (켜져 있으면).</summary>
        public void Zero(int ch)
        {
            if ((Control[ch] & 0x80) != 0) Pending[ch] = true;
        }

        /// <summary>카운터 모드 채널에 트리거 하나.</summary>
        public bool Trigger(int ch)
        {
            if (!CounterMode(Control[ch]) || (Control[ch] & 0x02) != 0) return false;
            if (--Count[ch] > 0) return false;
            Count[ch] = Tc[ch] == 0 ? 256 : Tc[ch];
            Zero(ch);
            return true;
        }
    }

    /// <summary>X1 기계 (hoot 흉내).</summary>
    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Ram = new byte[0x10000];
        /// <summary>I/O 공간의 VRAM (텍스트 2000h~3FFFh · 그래픽 4000h~FFFFh): 드라이버가 작업 메모리로 쓰기도 한다 (Sorcerian).</summary>
        public readonly byte[] IoRam = new byte[0x10000];
        public readonly Z80 Cpu;
        public readonly double CpuHz;
        public bool Opm;
        public int DataAddr, DataSize, FileSize, VoiceAddr, VoiceSize, VoiceFileSize;
        public readonly int[] DefaultTc = { -1, -1, -1, -1 };
        public Func<int, byte[]> Bgm = n => null, Voice = n => null;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기 (시간 = 44100Hz 샘플, 재생 명령부터; chip 0 = PSG, 1 = OPM).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Loads, Interrupts, Ignored;
        public readonly Ctc Main = new Ctc(), Fm = new Ctc();

        private int psgAddr, opmAddr, command, code, waitReads;
        private long playStart = -1;
        private readonly byte[] psgRegs = new byte[16];

        public Machine(int clockMul)
        {
            CpuHz = CpuClock * clockMul;
            Cpu = new Z80(this);
        }

        public void Put(int at, byte[] d)
        {
            for (int i = 0; i < d.Length && at + i < 0x10000; i++) if (at + i >= 0) Ram[at + i] = d[i];
        }

        public void Start(int pc)
        {
            // hoot (RAZE z80_reset)처럼 레지스터 0: 維新の嵐 · 水滸伝 PATCH는 E239h (NOP · RET M)로 들어가 S = 0 · B = 0에 기댄다
            Cpu.A = Cpu.F = Cpu.B = Cpu.C = Cpu.D = Cpu.E = Cpu.H = Cpu.L = 0;
            Cpu.IX = Cpu.IY = 0;
            Cpu.PC = pc;
            Cpu.SP = 0x0000;
            Cpu.Im = 2;
            Cpu.I = 0;
            for (int ch = 0; ch < 4; ch++) { Main.DefaultTc[ch] = DefaultTc[ch]; }
        }

        public void Play(int songCode)
        {
            code = songCode;
            command = 1;
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
        }

        public bool Waiting { get { return waitReads >= 4; } }

        public void Run(long cycles, bool untilWaiting = false)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                Tick(Main);
                Tick(Fm);
                if (Cpu.Iff1 && TryInterrupt(Main)) { }
                else if (Cpu.Iff1) TryInterrupt(Fm);
                if (Cpu.Halted)
                {
                    long next = Math.Min(NextOf(Main), NextOf(Fm));
                    long to = Cpu.Iff1 ? Math.Min(end, next) : end;
                    if (to > Cpu.Cycles) Cpu.Cycles = to; else Cpu.Cycles++;
                    continue;
                }
                Cpu.Step();
                if (untilWaiting && Waiting) return;
            }
        }

        private static long NextOf(Ctc c) { return Math.Min(Math.Min(c.Next[0], c.Next[1]), Math.Min(c.Next[2], c.Next[3])); }

        private void Tick(Ctc c)
        {
            long now = Cpu.Cycles;
            for (int ch = 0; ch < 4; ch++)
            {
                if (c.Next[ch] > now) continue;
                long p = Math.Max(1, c.Period(ch));
                while (c.Next[ch] <= now)
                {
                    c.Next[ch] += p;
                    c.Zero(ch);
                    if (ch == 0) c.Trigger(3);   // X1: 채널 0 출력 → 채널 3 트리거
                }
            }
        }

        private bool TryInterrupt(Ctc c)
        {
            for (int ch = 0; ch < 4; ch++)
            {
                if (!c.Pending[ch]) continue;
                c.Pending[ch] = false;
                int at = ((Cpu.I << 8) | ((c.Vector + ch * 2) & 0xFF)) & 0xFFFF;
                int target = Ram[at] | (Ram[(at + 1) & 0xFFFF] << 8);
                if (target == 0) { Ignored++; return false; }
                Interrupts++;
                Cpu.Halted = false;
                Cpu.Iff1 = Cpu.Iff2 = false;
                Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
                Ram[Cpu.SP] = (byte)Cpu.PC; Ram[(Cpu.SP + 1) & 0xFFFF] = (byte)(Cpu.PC >> 8);
                Cpu.PC = Cpu.Im == 1 ? 0x38 : target;
                Cpu.Cycles += 19;
                return true;
            }
            return false;
        }

        public void RunSeconds(double seconds) { Run((long)(CpuHz * seconds)); }

        public byte Read(int a) { return Ram[a & 0xFFFF]; }
        public void Write(int a, byte v) { Ram[a & 0xFFFF] = v; }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuHz), chip, reg, val));
        }

        public byte In(int port)
        {
            port &= 0xFFFF;
            int hi = port >> 8;
            if (port != 0x0000) waitReads = 0;
            if (hi == 0x00)
            {
                switch (port & 0xFF)
                {
                    case 0x00:
                        {
                            int c = command;
                            command = 0;
                            if (c == 0) waitReads++; else waitReads = 0;
                            return (byte)c;
                        }
                    case 0x01: return (byte)code;
                    case 0x02: return (byte)(code >> 8);
                    case 0x03: return (byte)(code >> 16);
                    case 0x0F: return (byte)(code >> 24);
                }
                return 0xFF;
            }
            if (hi == 0x1B) return psgRegs[psgAddr & 15];
            if (port == 0x1A01)
            {
                // 8255 PPI 포트 B: bit7 = 수직 귀선 (60Hz, 앞 1.3ms), bit6 = 서브 CPU 바쁨 · bit5 = 서브 CPU 데이터 (늘 준비됨 = 0)
                long frame = (long)(CpuHz / 60);
                bool blank = Cpu.Cycles % frame < frame / 12;
                return (byte)((blank ? 0x80 : 0x00) | 0x1F);
            }
            if (hi == 0x19) return 0x00;   // 서브 CPU (80C49) 응답
            if (!Opm && port >= 0x0700 && port <= 0x0707) return 0xFF;   // PSG 판: FM 보드 없음 (드라이버가 CTC · OPM을 보고 FM으로 고르지 않게)
            if (port == 0x0701 || port == 0x0700) return 0x00;   // OPM 상태: 바쁘지 않음
            if (port >= 0x1FA0 && port <= 0x1FA3) return Main.Read(port - 0x1FA0, Cpu.Cycles);
            if (port >= 0x0704 && port <= 0x0707) return Fm.Read(port - 0x0704, Cpu.Cycles);
            return IoRam[port];   // hoot mucomx1.cpp: 나머지 포트 = 쓴 값을 되읽는 I/O 메모리 (data 줄이 미리 채움)
        }

        public void Out(int port, byte v)
        {
            port &= 0xFFFF;
            int hi = port >> 8;
            if (hi == 0x00)
            {
                if ((port & 0xFF) == 0x00) command = 0;
                else if ((port & 0xFF) == 0x01) Load(v);
                return;
            }
            if (hi == 0x1C) { psgAddr = v & 15; return; }
            if (hi == 0x1B) { psgRegs[psgAddr & 15] = v; Emit(0, psgAddr & 15, v); return; }
            if (!Opm && port >= 0x0700 && port <= 0x0707) return;
            if (port == 0x0700) { opmAddr = v; return; }
            if (port == 0x0701) { Emit(1, opmAddr, v); return; }
            if (port >= 0x1FA0 && port <= 0x1FA3) { Main.Write(port - 0x1FA0, v, Cpu.Cycles); return; }
            if (port >= 0x0704 && port <= 0x0707) { Fm.Write(port - 0x0704, v, Cpu.Cycles); return; }
            IoRam[port] = v;
        }

        private void Load(int n)
        {
            var d = Bgm(n);
            if (d != null)
            {
                for (int i = 0; i < DataSize && DataAddr + i < 0x10000; i++) Ram[DataAddr + i] = 0;
                int len = FileSize > 0 ? Math.Min(FileSize, d.Length) : d.Length;
                for (int i = 0; i < len && DataAddr + i < 0x10000; i++) Ram[DataAddr + i] = d[i];
                Loads++;
            }
            var vd = Voice(n);
            if (vd != null && VoiceAddr >= 0)
            {
                for (int i = 0; i < VoiceSize && VoiceAddr + i < 0x10000; i++) Ram[VoiceAddr + i] = 0;
                int len = VoiceFileSize > 0 ? Math.Min(VoiceFileSize, vd.Length) : vd.Length;
                for (int i = 0; i < len && VoiceAddr + i < 0x10000; i++) Ram[VoiceAddr + i] = vd[i];
            }
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "X1 (hoot)", Notes = "hoot x1" };
            bool psg = Log.Any(x => x.chip == 0), opm = Log.Any(x => x.chip == 1);
            if (psg) { w.SetClock(VgmWriter.ClockAy8910, CpuClock / 2); w.SetHeaderByte(0x78, 0x00); }
            if (opm) w.SetClock(VgmWriter.ClockYm2151, CpuClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0xA0, x.reg, x.val); else w.Write(0x54, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
