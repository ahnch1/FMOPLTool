using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 타이토 YM2151 보드 (driver taito, type fullt: Rastan · Top Speed · Asuka · Cadash · Exzisus · Visco 게임들): 사운드 보드를 그대로 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME rastan · taito_asuka · topspeed · exzisus와 같은 배치): Z80 4MHz, 0000h~3FFFh = ROM, 4000h~7FFFh = 뱅크 창 (YM2151 CT1 · CT2 = 1Bh bit6~7 → ROM n × 4000h, 처음 1;
/// ROM이 32KB 이하면 뱅크 없음), 8000h~8FFFh = RAM, 9000h · 9001h = YM2151 (타이머 넘침 = IRQ, IM 1),
/// A000h = TC0140SYT (PC060HA) 슬레이브 모드, A001h = 통신 (모드 0~3 = 68000이 보낸 니블, 4 = 상태, 쓰기 5 · 6 = NMI 끔 · 켬).</para>
/// <para>MSM5205 (384kHz ÷ 48 = 8kHz): 하드웨어가 VCK마다 ADPCM ROM에서 니블을 넣는다 (바이트의 위 니블 먼저).
/// Rastan · Asuka 배치 = B000h 쓰기 → 시작 주소 위 바이트, C000h = 시작, D000h = 멈춤 (주소 아래 바이트 0).
/// Top Speed = B000h~BFFFh 칩 1 · C000h~CFFFh 칩 2, 주소 bit10~11: 0 = 주소 위 바이트, 1 (B400h) = 시작, 2 (B800h) = 멈춤 (드라이버가 쉴 때 틱마다 씀), D000h~D600h = 볼륨 (안 씀).</para>
/// <para>type masterw (Master of Weapon · Tetris · Champion Wrestler) · viofight (Violence Fight · Hit the Ice · Camel Try) = 타이토 B 시스템 (MAME taito_b masterw · viofight_sound_map): 같은 배치에 9000h = YM2203 3MHz (IRQ = 타이머, 포트 A (0Eh) = 사운드 ROM 뱅크 아래 2비트),
/// viofight는 B000h = OKIM6295 (1.056MHz, 핀7 높음, ROM = pcm). MSM5205 없음.</para>
/// <para>곡: rom code = Z80 ROM, pcm = ADPCM ROM. 부팅 1초 뒤 곡 코드를 68000 명령처럼 포트 0 · 1에, 180초를 YM2151 + OKIM6258 (MSM5205 대신, 같은 4비트 ADPCM 풀기 · 4.096MHz ÷ 512 = 8kHz) VGM으로.
/// Top Speed의 둘째 MSM5205는 VGM에 하나뿐이라 첫째가 쉬는 동안만 낸다.</para>
/// </summary>
public static class HootTaitoOpm
{
    public const double CpuClock = 4000000, YmClock = 4000000, MsmRate = 384000.0 / 48, OpnClock = 3000000, OkiClock = 1056000;
    public const double DefaultSeconds = 180, DefaultFade = 4;
    /// <summary>VGM에서 MSM5205 대신 쓰는 OKIM6258 클럭 (÷ 512 = 8kHz).</summary>
    public const uint VgmMsmClock = 4096000;

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "taito", StringComparison.OrdinalIgnoreCase)) return false;
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "fullt" || t == "masterw" || t == "viofight"
            || t == "rainbow" || t == "daisenpuu" || t == "opwolf" || t == "volfied" || t == "darius";   // MAME rainbow · taito_x (twinhawk) · opwolf: Rastan과 같은 사운드 맵 (9000h YM2151 · A000h TC0140SYT)
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
        byte[] Join(List<(long off, byte[] d)> parts, int min)
        {
            long size = Math.Max(min, parts.Count == 0 ? 0 : parts.Max(p => p.off + p.d.Length));
            var r = new byte[size];
            foreach (var (off, d) in parts) Array.Copy(d, 0, r, off, d.Length);
            return r;
        }
        string ty = (e.Type ?? "").Trim().ToLowerInvariant();
        var m = new Machine(Join(code, 0x8000), Join(pcm, 0), (e.Name ?? "").IndexOf("Top Speed", StringComparison.OrdinalIgnoreCase) >= 0) { Opn = ty == "masterw" || ty == "viofight" || ty == "volfied" || ty == "darius", Oki = ty == "viofight", XSystem = ty == "daisenpuu", Volfied = ty == "volfied", Darius = ty == "darius", OpnHz = ty == "darius" ? 4000000 : OpnClock };
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        // 타이토 사운드 드라이버: 처음엔 소리가 꺼져 있고 68000이 명령 EFh (소리 켬)를 보내야 곡을 낸다 (HootTaito와 같음) — 처리 코드가 있으면 먼저 보낸다
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
        public readonly byte[] Rom, Pcm;
        public readonly bool TopSpeed;
        /// <summary>타이토 B: YM2203 (chip 0 = YM2203), Oki = B000h OKIM6295 (chip 2).</summary>
        public bool Opn, Oki;
        /// <summary>타이토 X 배치 (MAME taito_x · twinhawk): RAM C000h~DFFFh, YM2151 E000h, TC0140SYT E200h · E201h, 뱅크 F200h.</summary>
        public bool XSystem;
        /// <summary>Volfied 배치 (MAME volfied_sound_map): Taito B와 같고 TC0140SYT만 8800h · 8801h (RAM 8000h~87FFh).</summary>
        public bool Volfied;
        /// <summary>Darius 배치 (MAME darius_sound_map): YM2203 둘 (9000h = IRQ · A000h), TC0140SYT B000h · B001h, 뱅크 DC00h, 4MHz. ADPCM Z80 (MSM5205)은 빠짐.</summary>
        public bool Darius;
        public double OpnHz = OpnClock;
        public readonly byte[] Ram = new byte[0x2000];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151 (reg, val), 1 = MSM (OKIM6258 VGM 레지스터: 0 = 명령 (1 멈춤 · 2 재생), 1 = 자료 바이트 (아래 니블 먼저)).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, Polls, MsmStarts;
        /// <summary>부팅 뒤 명령 EFh (소리 켬)를 보냈다.</summary>
        public bool SoundOn;
        /// <summary>진단: 알 수 없는 자리에 쓴 것 (주소 → 횟수).</summary>
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>();
        public Dictionary<int, int> PcCount;
        /// <summary>진단: null이 아니면 Top Speed MSM 자리 쓰기 (사이클, 주소, 값).</summary>
        public List<(long cyc, int a, int v)> PortLog;

        private int bank = 1, ymAddr, last1B = -1;
        private readonly int[] emittedYm = Enumerable.Repeat(-1, 256).ToArray();
        private long playStart = -1;
        private readonly byte[] ymRegs = new byte[256];
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        // TC0140SYT (슬레이브 쪽)
        private readonly int[] fromMain = new int[4];
        private int subMode, status;
        private bool nmiEnabled, nmiLine;
        private const int Port01Full = 1, Port23Full = 2;
        // MSM5205 둘 (Top Speed는 둘, 나머지는 첫째만): 자리 · 돌아가는 중 · 다음 니블이 아래 니블
        private readonly int[] msmPos = new int[2], msmBase = new int[2];
        private readonly bool[] msmOn = new bool[2];
        private int msmOwner = -1;   // VGM 칩을 지금 쓰는 MSM
        private double nextVck;
        private readonly double vckCycles = CpuClock / MsmRate * 2;   // 바이트 하나 (니블 둘)마다

        public Machine(byte[] rom, byte[] pcm, bool topSpeed)
        {
            Rom = rom; Pcm = pcm ?? new byte[0]; TopSpeed = topSpeed;
            if (topSpeed) msmBase[1] = 0x10000;   // pcm 둘째 ROM (offset 10000h) = 칩 2
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            nextVck = vckCycles;
        }

        /// <summary>곡 명령 보내기: 68000처럼 포트 0 · 1에 낮은 · 높은 니블 (2바이트 코드면 위 바이트를 포트 2 · 3에 먼저).</summary>
        public void Play(int code)
        {
            var init = Log.Where(x => x.chip == 0).Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            for (int i = 0; i < 2; i++) if (msmOn[i] && msmOwner == i) Emit(1, 0, 2);
            if (code > 0xFF) Send(code >> 8, 2);
            Send(code, 0);
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

        // YM2151 타이머: A = 64 × (1024 − TA) / 클럭, B = 1024 × (256 − TB) / 클럭. YM2203 (3MHz): A = 72 × (1024 − TA), B = 1152 × (256 − TB)
        private long TimerAPeriod { get { return Math.Max(1, Opn ? (long)(72.0 * (1024 - timerA) / OpnHz * CpuClock) : (long)(64.0 * (1024 - timerA) / YmClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, Opn ? (long)(1152.0 * (256 - timerB) / OpnHz * CpuClock) : (long)(1024.0 * (256 - timerB) / YmClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                if (now >= nextVck) { nextVck += vckCycles; if (!Opn) Vck(); }
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
                    long next = Math.Min(end, Math.Min((long)Math.Ceiling(nextVck), Math.Min(nextTimerA, nextTimerB)));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, next);
                    continue;
                }
                if (PcCount != null) { PcCount.TryGetValue(Cpu.PC, out int k); PcCount[Cpu.PC] = k + 1; }
                Cpu.Step();
            }
        }

        /// <summary>MSM 바이트 하나 (VCK 둘): ROM에서 읽어 VGM 칩에 (위 니블 먼저 → OKIM6258은 아래 니블 먼저라 바꿔서).</summary>
        private void Vck()
        {
            for (int i = 0; i < 2; i++)
            {
                if (!msmOn[i]) continue;
                int b = PcmAt(i, msmPos[i]);
                msmPos[i] = (msmPos[i] + 1) & 0xFFFF;
                if (msmOwner == i) Emit(1, 1, ((b & 15) << 4) | (b >> 4));
            }
        }

        private int PcmAt(int chip, int pos)
        {
            long a = msmBase[chip] + pos;
            return Pcm.Length > 0 ? Pcm[a % Pcm.Length] : 0;
        }

        private void MsmStart(int chip)
        {
            if (msmOn[chip]) return;
            msmOn[chip] = true;
            MsmStarts++;
            if (msmOwner < 0 || msmOwner == chip || !msmOn[msmOwner]) { msmOwner = chip; Emit(1, 0, 1); Emit(1, 0, 2); }
        }

        private void MsmStop(int chip)
        {
            if (!msmOn[chip]) return;
            msmOn[chip] = false;
            msmPos[chip] &= 0xFF00;
            if (msmOwner == chip) Emit(1, 0, 1);
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (XSystem)
            {
                if (a >= 0xC000 && a < 0xE000) return Ram[a - 0xC000];
                if (a == 0xE000 || a == 0xE001) return (byte)(timerFlags & 3);
                if (a == 0xE201) return CommRead();
                if (a >= 0x8000) return 0;
            }
            if (a < 0x4000) return a < Rom.Length ? Rom[a] : (byte)0xFF;
            if (a < 0x8000) return Rom.Length <= 0x8000 ? Rom[a % Rom.Length] : Rom[((long)bank * 0x4000 + (a - 0x4000)) % Rom.Length];
            if (Volfied && a == 0x8801) return CommRead();
            if (Darius)
            {
                if (a == 0xB001) return CommRead();
                if (a == 0xA000 || a == 0xA001) return 0;   // 둘째 YM2203 상태
                if (a == 0xA001 || a >= 0xB000) return 0;
            }
            if (a < 0x9000) return Ram[a - 0x8000];
            if (a == 0x9000 || a == 0x9001) return (byte)(timerFlags & 3);   // YM2151 · YM2203 상태 (바쁨 없음)
            if (Oki && (a == 0xB000 || a == 0xB001)) return 0;   // OKIM6295 상태: 모두 쉼
            if (a == 0xA001) return CommRead();
            return 0;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (XSystem)
            {
                if (a >= 0xC000 && a < 0xE000) { Ram[a - 0xC000] = v; return; }
                switch (a)
                {
                    case 0xE000: ymAddr = v; return;
                    case 0xE001: YmWrite(ymAddr, v); return;
                    case 0xE200: subMode = v & 15; return;
                    case 0xE201: CommWrite(v & 15); return;
                    case 0xF200: bank = v & 3; return;
                }
                return;
            }
            if (Volfied && a == 0x8800) { subMode = v & 15; return; }
            if (Darius)
            {
                switch (a)
                {
                    case 0xA000: ym2Addr = v; return;
                    case 0xA001: Opn2Write(ym2Addr, v); return;
                    case 0xB000: subMode = v & 15; return;
                    case 0xB001: CommWrite(v & 15); return;
                    case 0xDC00: bank = v & 3; return;
                }
                if (a >= 0xB000) return;   // 좌우 · ADPCM 명령: 빠짐
            }
            if (Volfied && a == 0x8801) { CommWrite(v & 15); return; }
            if (a >= 0x8000 && a < 0x9000) { Ram[a - 0x8000] = v; return; }
            switch (a)
            {
                case 0x9000: ymAddr = v; return;
                case 0x9001: if (Opn) OpnWrite(ymAddr, v); else YmWrite(ymAddr, v); return;
                case 0xA000: subMode = v & 15; return;
                case 0xA001: CommWrite(v & 15); return;
            }
            if (Opn)
            {
                if (Oki && (a == 0xB000 || a == 0xB001)) { Emit(2, 0, v); return; }   // OKIM6295 (B001h 거울)
            }
            else if (TopSpeed && a >= 0xB000 && a < 0xD000)
            {
                PortLog?.Add((Cpu.Cycles, a, v));
                int chip = (a >> 12) & 1 ^ 1;   // B000h = 칩 0, C000h = 칩 1
                switch ((a >> 8) & 0x0C)
                {
                    case 0x00: msmPos[chip] = v << 8; return;   // 주소 위 바이트
                    case 0x04: MsmStart(chip); return;
                    case 0x08: MsmStop(chip); return;
                }
            }
            else if (!TopSpeed)
            {
                switch (a & 0xF000)
                {
                    case 0xB000: msmPos[0] = (msmPos[0] & 0xFF) | (v << 8); return;
                    case 0xC000: MsmStart(0); return;
                    case 0xD000: MsmStop(0); return;
                }
            }
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
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

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        private void YmWrite(int reg, int v)
        {
            ymRegs[reg] = (byte)v;
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
                case 0x1B:
                    bank = (v >> 6) & 3;   // CT1 · CT2 = 사운드 ROM 뱅크 — 드라이버가 쉴 새 없이 쓰므로 LFO 파형 (bit0~1)이 바뀔 때만 VGM에
                    if ((v & 3) == last1B) return;
                    last1B = v & 3;
                    Emit(0, reg, v & 3);
                    return;
            }
            // 틱마다 모든 레지스터를 다시 쓰는 드라이버 (Twin Hawk): 값이 같으면 VGM에 안 씀 (키온 08h · LFO 01h · 두 뜻 19h는 늘)
            if (reg != 0x08 && reg != 0x01 && reg != 0x19)
            {
                if (emittedYm[reg] == v) return;
                emittedYm[reg] = v;
            }
            Emit(0, reg, v);
        }

        private readonly int[] opnLast = Enumerable.Repeat(-1, 0x100).ToArray();

        private int ym2Addr;
        private readonly int[] opn2Last = Enumerable.Repeat(-1, 256).ToArray();

        /// <summary>Darius 둘째 YM2203 (VGM A5h): 타이머 · 포트는 안 씀.</summary>
        private void Opn2Write(int reg, int v)
        {
            if (reg == 0x0E || reg == 0x0F || (reg >= 0x24 && reg <= 0x27)) return;
            bool keep = reg == 0x28 || reg == 0x0D || (reg >= 0xA0 && reg <= 0xAE);
            if (!keep) { if (opn2Last[reg] == v) return; opn2Last[reg] = v; }
            Emit(3, reg, v);
        }

        private void OpnWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x0E: if (!Darius) bank = v & 3; return;   // 포트 A = 뱅크 (Taito B)
                case 0x0F: return;
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
                        v &= 0xC0;
                        break;
                    }
            }
            bool keep = reg == 0x28 || reg == 0x0D || reg == 0x27 || (reg >= 0xA0 && reg <= 0xAE);
            if (!keep) { if (opnLast[reg] == v) return; opnLast[reg] = v; }
            Emit(0, reg, v);
        }

        public byte In(int port) { return 0xFF; }
        public void Out(int port, byte v) { }

        public VgmSong ToVgm(double seconds, double fade)
        {
            if (Opn) return ToVgmOpn(seconds, fade);
            var w = new VgmWriter { System = "Taito YM2151 (hoot)", Notes = "hoot taito fullt" };
            w.SetClock(VgmWriter.ClockYm2151, YmClock);
            bool msm = Log.Any(x => x.chip == 1 && x.reg == 1);
            if (msm) { w.SetClock(VgmWriter.ClockOkim6258, VgmMsmClock); w.SetHeaderByte(0x94, 0x0A); }   // ÷512, 12비트 출력
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 1 && !msm) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x54, (byte)x.reg, (byte)x.val);
                else w.Write(0xB7, (byte)x.reg, (byte)x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }

        private VgmSong ToVgmOpn(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Taito B YM2203 (hoot)", Notes = "hoot taito b" };
            bool two = Log.Any(x => x.chip == 3);
            w.SetClock(VgmWriter.ClockYm2203, OpnHz, two ? 0x40000000u : 0);   // bit30 = YM2203 둘
            bool oki = Oki && Pcm.Length > 0 && Log.Any(x => x.chip == 2);
            if (oki)
            {
                w.SetClock(VgmWriter.ClockOkim6295, OkiClock, 0x80000000u);   // 핀7 높음
                var cmd = new byte[7 + 8 + Pcm.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8B;
                int size = 8 + Pcm.Length;
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int len = Pcm.Length;
                cmd[7] = (byte)len; cmd[8] = (byte)(len >> 8); cmd[9] = (byte)(len >> 16); cmd[10] = (byte)(len >> 24);
                Array.Copy(Pcm, 0, cmd, 15, Pcm.Length);
                w.Write(cmd);
            }
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 2 && !oki) continue;
                if (x.chip == 1) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x55, (byte)x.reg, (byte)x.val);
                else if (x.chip == 3) w.Write(0xA5, (byte)x.reg, (byte)x.val);
                else w.Write((byte)0xB8, 0, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
