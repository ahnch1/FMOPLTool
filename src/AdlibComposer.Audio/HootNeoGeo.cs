using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 네오지오 (driver neogeo, type generic): 사운드 보드를 그대로 흉내 내고 M1 ROM(Z80 사운드 프로그램)을 돌린다 (hoot 전용 시작 코드 없음).
/// <para>기계 (MAME neogeo와 같은 배치): Z80 4MHz, 0000h~7FFFh = M1 고정, 8000h~BFFFh · C000h~DFFFh · E000h~EFFFh · F000h~F7FFh = 16 · 8 · 4 · 2KB 뱅크 창
/// (IN 0Bh · 0Ah · 09h · 08h = 주소 상위 바이트가 뱅크 번호, 처음 02h · 06h · 0Eh · 1Eh = 평평하게), F800h~FFFFh = RAM.
/// 포트: 00h 읽기 = 68000이 보낸 명령 (보낼 때 NMI, 08h 쓰기 = NMI 켬 · 18h = 끔), 0Ch 쓰기 = 68000에 답, 04h~07h = YM2610 (04h 읽기 = 상태 · 타이머 플래그).
/// YM2610 타이머 A · B (8MHz) 넘침 = IRQ (IM 1 → 0038h, 플래그를 지울 때까지).</para>
/// <para>곡: rom code = M1 (offset에), adpcma = ADPCM-A ROM, adpcmb = ADPCM-B ROM (없으면 ADPCM-A ROM을 같이 씀). 부팅 1초 뒤 (SNK 사운드 드라이버면 먼저 명령 08h = 음악 켬) 곡 코드를 명령으로 보낸다 (옵션 code_play가 있으면 그 뒤에 그 명령도).
/// 180초를 칩 쓰기 VGM으로 (앞에 ADPCM ROM 데이터 블록 82h · 83h). 아직 없음: 암호화 V ROM (neo_pcm2_swap · neo_pcm2_snk_1999), m1_decrypt.</para>
/// </summary>
public static class HootNeoGeo
{
    public const double CpuClock = 4000000, YmClock = 8000000;
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
        var m1 = new List<(long off, byte[] d)>();
        var a = new List<(long off, byte[] d)>();
        var b = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            var list = type == "code" ? m1 : type == "adpcma" ? a : type == "adpcmb" ? b : null;
            if (list == null) { unsupported.Add(type); continue; }
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }   // wjammers: 음수 offset 줄
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            list.Add((off, d));
        }
        foreach (var o in new[] { "neo_pcm2_swap", "neo_pcm2_snk_1999", "m1_decrypt" }) if (e.Options.ContainsKey(o)) unsupported.Add(o);
        byte[] Join(List<(long off, byte[] d)> parts, int min)
        {
            long size = Math.Max(min, parts.Count == 0 ? 0 : parts.Max(p => p.off + p.d.Length));
            var r = new byte[size];
            foreach (var (off, d) in parts) Array.Copy(d, 0, r, off, d.Length);
            return r;
        }
        var m = new Machine(Join(m1, 0x10000), Join(a, 0), b.Count > 0 ? Join(b, 0) : null);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.CodePlay = e.Options.ContainsKey("code_play") ? e.Option("code_play", -1) & 0xFF : -1;
        m.RunSeconds(1);
        // SNK 사운드 드라이버 (Mr.Pac Ver 2.0 · MAKOTO Ver 3.0 · "Sound Driver Ver … To SNK"): 처음엔 음악이 꺼져 있고 (aof: FE34h = FFh)
        // 68000이 명령 08h를 보내야 곡을 받는다. 다른 드라이버 (ADK · Nazca 등)에는 보내지 않는다 (mslug는 08h를 받으면 오히려 안 남)
        if (new[] { "by MAKOTO", "by Mr.Pac", "To SNK", "to SK" }.Any(t => FindText(m.Rom, t))) { m.Send(0x08); m.RunSeconds(0.2); m.Makoto = true; }
        return m;
    }

    private static bool FindText(byte[] d, string text)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(text);
        int n = Math.Min(d.Length, 0x10000) - t.Length;
        for (int i = 0; i < n; i++)
        {
            int k = 0;
            while (k < t.Length && d[i + k] == t[k]) k++;
            if (k == t.Length) return true;
        }
        return false;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom, AdpcmA, AdpcmB;
        public readonly byte[] Ram = new byte[0x800];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        public readonly List<(double t, int port, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int CodePlay = -1;
        /// <summary>SNK 사운드 드라이버: 부팅 뒤 명령 08h (음악 켬)를 보냈다.</summary>
        public bool Makoto;
        public int Nmis, Irqs, Replies;

        private readonly int[] bank = { 0x1E, 0x0E, 0x06, 0x02 };   // F000h 2KB · E000h 4KB · C000h 8KB · 8000h 16KB
        private int latch, addrA, addrB;
        private bool nmiEnabled = true;
        private long playStart = -1;
        private readonly byte[] regs = new byte[0x200];
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private readonly List<(long at, int code)> queue = new List<(long, int)>();

        public Machine(byte[] rom, byte[] adpcmA, byte[] adpcmB)
        {
            Rom = rom; AdpcmA = adpcmA; AdpcmB = adpcmB;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        /// <summary>곡 명령 보내기 (code_play가 있으면 0.1초 뒤 그것도).</summary>
        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.port, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            Send(code & 0xFF);
            if (CodePlay >= 0) queue.Add((Cpu.Cycles + (long)(CpuClock / 10), CodePlay));
        }

        public void Send(int code)
        {
            latch = code;
            if (!nmiEnabled) return;
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

        private long TimerAPeriod { get { return Math.Max(1, (long)(144.0 * (1024 - timerA) / YmClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(2304.0 * (256 - timerB) / YmClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                for (int i = queue.Count - 1; i >= 0; i--) if (now >= queue[i].at) { int c = queue[i].code; queue.RemoveAt(i); Send(c); }
                if (timerFlags != 0 && Cpu.Iff1)
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
                    long next = Math.Min(end, Math.Min(nextTimerA, nextTimerB));
                    if (queue.Count > 0) next = Math.Min(next, queue.Min(q => q.at));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, next);
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x8000) return a < Rom.Length ? Rom[a] : (byte)0xFF;
            if (a >= 0xF800) return Ram[a - 0xF800];
            long at;
            if (a < 0xC000) at = (long)bank[3] * 0x4000 + (a - 0x8000);
            else if (a < 0xE000) at = (long)bank[2] * 0x2000 + (a - 0xC000);
            else if (a < 0xF000) at = (long)bank[1] * 0x1000 + (a - 0xE000);
            else at = (long)bank[0] * 0x800 + (a - 0xF000);
            return Rom.Length > 0 ? Rom[at % Rom.Length] : (byte)0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0xF800) Ram[a - 0xF800] = v;
        }

        private void Emit(int port, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), port, reg, val));
        }

        private void YmWrite(int port, int reg, int v)
        {
            regs[port * 0x100 + reg] = (byte)v;
            if (port == 0)
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
            Emit(port, reg, v);
        }

        public byte In(int port)
        {
            int lo = port & 0xFF, hi = (port >> 8) & 0xFF;
            switch (lo)
            {
                case 0x00: return (byte)latch;
                case 0x04: return (byte)(timerFlags & 3);
                case 0x05: return regs[addrA & 0xFF];
                case 0x06: return 0x00;
                case 0x07: return regs[0x100 + (addrB & 0xFF)];
                case 0x08: bank[0] = hi; return 0;
                case 0x09: bank[1] = hi; return 0;
                case 0x0A: bank[2] = hi; return 0;
                case 0x0B: bank[3] = hi; return 0;
            }
            return 0xFF;
        }

        public void Out(int port, byte v)
        {
            switch (port & 0xFF)
            {
                case 0x04: addrA = v; break;
                case 0x05: YmWrite(0, addrA, v); break;
                case 0x06: addrB = v; break;
                case 0x07: YmWrite(1, addrB, v); break;
                case 0x08: nmiEnabled = true; break;
                case 0x18: nmiEnabled = false; break;
                case 0x0C: Replies++; break;
            }
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Neo Geo (hoot)", Notes = "hoot neogeo" };
            w.SetClock(VgmWriter.ClockYm2610, YmClock);
            void Block(int type, byte[] data)
            {
                if (data == null || data.Length == 0) return;
                var cmd = new byte[7 + 8 + data.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = (byte)type;
                int size = 8 + data.Length;
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int total = data.Length;
                cmd[7] = (byte)total; cmd[8] = (byte)(total >> 8); cmd[9] = (byte)(total >> 16); cmd[10] = (byte)(total >> 24);
                Array.Copy(data, 0, cmd, 15, data.Length);   // 시작 주소 0
                w.Write(cmd);
            }
            Block(0x82, AdpcmA);
            Block(0x83, AdpcmB ?? AdpcmA);
            double total2 = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write((byte)(0x58 + x.port), x.reg, x.val);
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
