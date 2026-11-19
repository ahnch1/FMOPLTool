using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 타이토 YM2610 보드 (driver taito, type f2system · bsystem · dual68 · xsystem · fx1a · sonicb): 사운드 보드를 그대로 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME taito_f2 · taito_b · taito_h · ninjaw · taito_x · taitofx1과 같은 배치): Z80 4MHz, 0000h~3FFFh = ROM 처음 16KB, 4000h~7FFFh = 뱅크 창
/// (F200h 쓰기 = 뱅크 번호 n → ROM n × 4000h, 처음 1), C000h~DFFFh = RAM, E000h~E003h = YM2610 (E000h 읽기 = 상태 · 타이머 플래그),
/// E200h = TC0140SYT 슬레이브 모드, E201h = 통신 (모드 0~3 = 68000이 보낸 니블, 4 = 상태, 쓰기 5 · 6 = NMI 끔 · 켬).
/// 68000이 포트 0 · 1(낮은 · 높은 니블)을 채우면 NMI (켜져 있을 때, 가장자리). YM2610 타이머 넘침 = IRQ (IM 1 → 0038h).</para>
/// <para>곡: rom code = Z80 ROM (offset은 hoot 보드마다 다른 자리라 가장 작은 offset을 0으로), adpcma · adpcm = ADPCM-A ROM, adpcmb = ADPCM-B ROM (없으면 A를 같이).
/// 부팅 1초 뒤 곡 코드를 68000 명령처럼 보내고 180초를 칩 쓰기 VGM으로. YM2610B (fx1a · sonicb · 이름에 YM2610B)는 FM 6채널.</para>
/// </summary>
public static class HootTaito
{
    public const double CpuClock = 4000000, YmClock = 8000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    /// <summary>이 길로 재생하는 hoot taito 형식 (Z80 + YM2610 + TC0140SYT).</summary>
    public static readonly string[] Types = { "f2system", "bsystem", "dual68", "xsystem", "fx1a", "sonicb" };

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "taito", StringComparison.OrdinalIgnoreCase)
            && Types.Contains((type ?? "").Trim().ToLowerInvariant());
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
        var a = new List<(long off, byte[] d)>();
        var b = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            var list = type == "code" ? code : type == "adpcma" || type == "adpcm" ? a : type == "adpcmb" ? b : null;
            if (list == null) { unsupported.Add(type); continue; }
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            list.Add((off, d));
        }
        if (code.Count > 0)
        {
            long min = code.Min(c => c.off);
            for (int i = 0; i < code.Count; i++) code[i] = (code[i].off - min, code[i].d);
        }
        byte[] Join(List<(long off, byte[] d)> parts, int minSize)
        {
            long size = Math.Max(minSize, parts.Count == 0 ? 0 : parts.Max(p => p.off + p.d.Length));
            var r = new byte[size];
            foreach (var (off, d) in parts) Array.Copy(d, 0, r, off, d.Length);
            return r;
        }
        string t = (e.Type ?? "").ToLowerInvariant();
        bool ym2610b = t == "fx1a" || t == "sonicb" || (e.Name ?? "").IndexOf("YM2610B", StringComparison.OrdinalIgnoreCase) >= 0;
        var m = new Machine(Join(code, 0x8000), Join(a, 0), b.Count > 0 ? Join(b, 0) : null, ym2610b);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        // 타이토 사운드 드라이버 (Kitaro · F2 계열): 처음엔 소리가 꺼져 있고 68000이 명령 EFh (소리 켬)를 보내야 곡을 낸다 — 처리 코드가 있으면 먼저 보낸다
        if (FindBytes(m.Rom, 0xFE, 0xEF, 0xC0, 0x3E)) { m.Send(0xEF, 0); m.RunSeconds(0.2); m.SoundOn = true; }
        return m;
    }

    private static bool FindBytes(byte[] d, params byte[] pat)
    {
        for (int i = 0; i + pat.Length <= d.Length; i++)
        {
            int k = 0;
            while (k < pat.Length && d[i + k] == pat[k]) k++;
            if (k == pat.Length) return true;
        }
        return false;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom, AdpcmA, AdpcmB;
        public readonly bool Ym2610B;
        public readonly byte[] Ram = new byte[0x2000];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        public readonly List<(double t, int port, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, Polls;
        /// <summary>부팅 뒤 명령 EFh (소리 켬)를 보냈다.</summary>
        public bool SoundOn;
        /// <summary>진단: null이 아니면 명령마다 PC를 센다.</summary>
        public Dictionary<int, int> PcCount;
        /// <summary>진단: 이 주소를 읽은 PC (−1 = 끔).</summary>
        public int WatchAddr = -1;
        public readonly HashSet<int> WatchPcs = new HashSet<int>();

        private int bank = 1;
        private int addrA, addrB;
        private long playStart = -1;
        private readonly byte[] regs = new byte[0x200];
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        // TC0140SYT (슬레이브 쪽)
        private readonly int[] fromMain = new int[4];
        private int subMode, status;
        private bool nmiEnabled, nmiLine;
        private const int Port01Full = 1, Port23Full = 2;

        public Machine(byte[] rom, byte[] adpcmA, byte[] adpcmB, bool ym2610b)
        {
            Rom = rom; AdpcmA = adpcmA; AdpcmB = adpcmB; Ym2610B = ym2610b;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        /// <summary>곡 명령을 어느 포트로 보낼지: 2 = 포트 2 · 3 (곡), 0 = 포트 0 · 1.</summary>
        public int CommandPort = 0;

        /// <summary>곡 명령 보내기: 68000처럼 포트 2 · 3 (또는 0 · 1)에 낮은 · 높은 니블.</summary>
        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.port, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            if (code > 0xFF) Send(code >> 8, 2);   // 2바이트 코드 (fx1a 등): 위 바이트 = 포트 2 · 3
            Send(code, CommandPort);
        }

        public void Send(int code, int port)
        {
            code &= 0xFF;
            fromMain[port] = code & 15; fromMain[port + 1] = (code >> 4) & 15;
            status |= port == 0 ? Port01Full : Port23Full;
            UpdateNmi();
        }

        private void UpdateNmi()
        {
            bool line = nmiEnabled && (status & (Port01Full | Port23Full)) != 0;
            if (line && !nmiLine)
            {
                Nmis++;
                Cpu.Halted = false;
                Cpu.Iff2 = Cpu.Iff1;
                Cpu.Iff1 = false;
                Push(Cpu.PC);
                Cpu.PC = 0x66;
                Cpu.Cycles += 11;
            }
            nmiLine = line;
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
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, next);
                    continue;
                }
                if (PcCount != null) { PcCount.TryGetValue(Cpu.PC, out int k); PcCount[Cpu.PC] = k + 1; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x4000) return a < Rom.Length ? Rom[a] : (byte)0xFF;
            if (a < 0x8000) return Rom.Length > 0 ? Rom[((long)bank * 0x4000 + (a - 0x4000)) % Rom.Length] : (byte)0xFF;
            if (a == WatchAddr) WatchPcs.Add(Cpu.PC);
            if (a >= 0xC000 && a < 0xE000) return Ram[a - 0xC000];
            switch (a)
            {
                case 0xE000: return (byte)(timerFlags & 3);
                case 0xE001: return regs[addrA & 0xFF];
                case 0xE002: return 0x00;
                case 0xE003: return regs[0x100 + (addrB & 0xFF)];
                case 0xE201: return CommRead();
            }
            return a >= 0xE000 ? (byte)0 : (byte)0xFF;   // E400h · EA00h … (팬 · 딥 스위치 자리) = 0 (MAME nopr)
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0xC000 && a < 0xE000) { Ram[a - 0xC000] = v; return; }
            switch (a)
            {
                case 0xE000: addrA = v; return;
                case 0xE001: YmWrite(0, addrA, v); return;
                case 0xE002: addrB = v; return;
                case 0xE003: YmWrite(1, addrB, v); return;
                case 0xE200: subMode = v & 15; return;
                case 0xE201: CommWrite(v & 15); return;
                case 0xF200: bank = v; return;
            }
        }

        private byte CommRead()
        {
            int r = 0;
            switch (subMode)
            {
                case 0: r = fromMain[subMode++]; break;
                case 1: status &= ~Port01Full; r = fromMain[subMode++]; Polls++; break;
                case 2: r = fromMain[subMode++]; break;
                case 3: status &= ~Port23Full; r = fromMain[subMode++]; break;
                case 4: r = status; break;
            }
            UpdateNmi();
            return (byte)r;
        }

        private void CommWrite(int v)
        {
            switch (subMode)
            {
                case 0: case 1: case 2: case 3: subMode++; break;   // 68000에 답 (버림)
                case 5: nmiEnabled = false; break;
                case 6: nmiEnabled = true; break;
            }
            UpdateNmi();
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

        public byte In(int port) { return 0xFF; }
        public void Out(int port, byte v) { }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Taito (hoot)", Notes = "hoot taito" };
            w.SetClock(VgmWriter.ClockYm2610, YmClock, Ym2610B ? 0x80000000u : 0);
            void Block(int type, byte[] data)
            {
                if (data == null || data.Length == 0) return;
                var cmd = new byte[7 + 8 + data.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = (byte)type;
                int size = 8 + data.Length;
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int total = data.Length;
                cmd[7] = (byte)total; cmd[8] = (byte)(total >> 8); cmd[9] = (byte)(total >> 16); cmd[10] = (byte)(total >> 24);
                Array.Copy(data, 0, cmd, 15, data.Length);
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
