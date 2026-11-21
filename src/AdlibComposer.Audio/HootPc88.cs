using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot PC-8801 (driver pc88, type opn · opna): 문서가 없어 hoot 목록의 시작 코드(rom PATCH 등)를 거꾸로 읽어 맞춘 규약.
/// <para>기계: Z80 7.9872MHz (옵션 baseclock 4 = 3.9936MHz), 64KB 전부 RAM (ROM · 뱅크 무시), 인터럽트 = IM 2 벡터 (I·256 + 레벨×2; IM 1이면 0038h):
/// VRTC 레벨 1 (60Hz, use_vrtc 또는 E6h bit1), RTC 레벨 2 (1/600초, use_rtc 또는 E6h bit0), OPN 타이머 레벨 4 (타이머 A · B 넘침, 27h로 켬).
/// 칩: OPN (44h 주소 · 45h 데이터, 44h 읽기 = 상태), OPNA (46h · 47h = 뒤 256 레지스터).</para>
/// <para>hoot 포트: IN 00h = 명령 (1 = 재생, 2 = 정지; 읽으면 비움), OUT 00h = 받음, IN 01h = 곡 코드 아래 바이트,
/// OUT 01h,n = bgm n을 mdata_addr에 (mdata_size까지 0, mfile_size까지) + voice n을 vdata_addr에 (vdata_size · vfile_size), IN 80h = 곡 코드 맨 위 바이트 (음색 묶음 등).</para>
/// <para>순서: rom code · binary를 놓고 init_pc(없으면 0)부터 돌려 명령을 기다리게 한 뒤 재생 → 180초를 칩 쓰기 VGM으로 미리 렌더.
/// 아직 없음: adpcm rom (OPNA ADPCM 메모리), file · shell (디스크 BASIC), use_n88rom (N88-BASIC ROM).</para>
/// </summary>
public static class HootPc88
{
    public const double BaseClock = 3993600;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, HootArchive.Files(e, dir));
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        // hoot 옵션 baseclock = "Z80 execute base clock (MHz)" — 없으면 8MHz (hoot 20050924 "4MHzモードをサポート", 목록에 있는 값은 모두 4)
        int mhz = e.Option("baseclock", 0);
        string falcom = FalcomType(e.Type);
        int mul = mhz > 0 ? Math.Max(1, mhz / 4) : falcom != null ? 1 : e.Option("clock_mul", e.Option("clockmul", 2));   // 옛 팔콤 = hoot oldfalcom.cpp 4MHz
        var m = new Machine(mul >= 2 ? 2 : 1)
        {
            Opna = e.Type.Equals("opna", StringComparison.OrdinalIgnoreCase) || e.Type.Equals("generic", StringComparison.OrdinalIgnoreCase),
            Falcom = falcom,
            Rtc = e.Option("use_rtc", 0) != 0,
            Vrtc = e.Option("use_vrtc", 0) != 0,
            DataAddr = e.Option("mdata_addr", 0x4000) & 0xFFFF,   // 없으면 4000h (laplace · urusei: 드라이버가 4000h에서 읽음), 0도 주소 (cdtakara)
            DataSize = Math.Max(0, e.Option("mdata_size", 0)),
            FileSize = Math.Max(0, e.Option("mfile_size", 0)),
            VoiceAddr = e.Options.ContainsKey("vdata_addr") ? e.Option("vdata_addr", 0) & 0xFFFF : -1,
            VoiceSize = Math.Max(0, e.Option("vdata_size", 0)),
            VoiceFileSize = Math.Max(0, e.Option("vfile_size", 0)),
        };
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
                case "binary":
                    {
                        var d = HexBytes(text) ?? file(text);
                        if (d != null) m.Put((int)off, d);
                        break;
                    }
                case "bgm": bgm[(int)off] = text; break;
                case "voice": voice[(int)off] = text; break;
                case "prog" when falcom != null:
                    {
                        var d = file(text);
                        if (d == null) { m.Missing.Add(text); break; }
                        m.Progs[(int)off] = d;
                        break;
                    }
                default: m.Unsupported.Add(type); break;   // adpcm · file · shell · conin · device: 아직
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
        m.Start(e.Option("init_pc", falcom != null ? 0xE000 : 0) & 0xFFFF);
        m.Run((long)(m.CpuHz * 3), untilWaiting: true);
        return m;
    }

    /// <summary>hoot oldfalcom.cpp가 다루는 형식 (falcom romancia · xanadu · xanadu2 · asteka2), 아니면 null.</summary>
    public static string FalcomType(string type)
    {
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "romancia" || t == "xanadu" || t == "xanadu2" || t == "asteka2" ? t : null;
    }

    // hoot oldfalcom.cpp sndadr[]: 끼어들기 · 시작 · 멈춤 · 곡 번호 주소 · "LD A,nn / LD I,A"의 nn 자리 (→ E0h)
    private static readonly (int intAdr, int start, int stop, int codeAdr, int iffAdr)[] FalcomTable =
    {
        (0x1056, 0x103a, 0xa000, 0x0000, 0x1043),   // ROMANCIA Opening
        (0x3a39, 0x3bee, 0x3b98, 0x3db5, 0x0000),   //          Main
        (0x1b67, 0x1c41, 0x1c0a, 0x0000, 0x1c49),   // XANADU Training Ground
        (0x4351, 0x443b, 0x43f5, 0x0000, 0x4443),   //        Main
        (0x168a, 0x1762, 0x172b, 0x0000, 0x176a),   //        Boss
        (0x16b5, 0x178d, 0x1756, 0x0000, 0x1795),   //        King Dragon
        (0x03d6, 0x0495, 0x0489, 0x0000, 0x049d),   //        Ending 1
        (0x03d6, 0x053d, 0x0489, 0x0000, 0x0545),   //        Ending 2
        (0x0160, 0x03dc, 0xa000, 0x0000, 0x0000),   // XANADU scenario II Opening
        (0x431c, 0x4466, 0x4417, 0x6067, 0x0000),   //                    Main
        (0x1563, 0x16a9, 0x165a, 0x6067, 0x0000),   //                    Boss
        (0x159c, 0x16e2, 0x1693, 0x6067, 0x0000),   //                    King Dragon
        (0x03d5, 0x05d6, 0xa000, 0x0000, 0x0000),   //                    Ending
        (0xaeb7, 0xb103, 0xb05b, 0x7eec, 0x0000),   // ASTEKA II
    };

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

    /// <summary>PC-8801 기계 (hoot 흉내).</summary>
    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Ram = new byte[0x10000];
        public readonly Z80 Cpu;
        public readonly double CpuHz;
        public bool Opna, Rtc, Vrtc;
        /// <summary>hoot oldfalcom.cpp 형식 (romancia · xanadu · xanadu2 · asteka2) 또는 null.</summary>
        public string Falcom;
        public readonly Dictionary<int, byte[]> Progs = new Dictionary<int, byte[]>();
        private int falcomDrv;
        private bool falcomFoodEmpty;
        public int DataAddr, DataSize, FileSize, VoiceAddr, VoiceSize, VoiceFileSize;
        public Func<int, byte[]> Bgm = n => null, Voice = n => null;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기 (시간 = 44100Hz 샘플, 재생 명령부터; 0 = 뒤 256 레지스터) · 레지스터 · 값.</summary>
        public readonly List<(double t, int port, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Loads, Interrupts, Ignored;

        private int addr0, addr1, command, code, waitReads, portE6, portSintm;   // portSintm: 32h (· 사운드 보드 II AAh) bit7 = 사운드 인터럽트 막음
        private long playStart = -1;
        private readonly byte[] regs = new byte[0x200];

        // 인터럽트
        private readonly long vrtcPeriod, rtcPeriod;
        private long nextVrtc, nextRtc;
        private bool vrtcPending, rtcPending, opnPending;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;

        public Machine(int clockMul)
        {
            CpuHz = BaseClock * clockMul;
            Cpu = new Z80(this);
            vrtcPeriod = (long)(CpuHz / 60);
            rtcPeriod = (long)(CpuHz / 600);
        }

        public void Put(int at, byte[] d)
        {
            for (int i = 0; i < d.Length && at + i < 0x10000; i++) if (at + i >= 0) Ram[at + i] = d[i];
        }

        public void Start(int pc)
        {
            // hoot (RAZE z80_reset)처럼 레지스터 0으로 시작
            Cpu.A = Cpu.F = Cpu.B = Cpu.C = Cpu.D = Cpu.E = Cpu.H = Cpu.L = 0;
            Cpu.IX = Cpu.IY = 0;
            Cpu.PC = pc;
            Cpu.SP = 0x0000;
            Cpu.Im = 2;
            Cpu.I = 0;
            nextVrtc = vrtcPeriod;
            nextRtc = rtcPeriod;
        }

        public void Play(int songCode)
        {
            if (Falcom != null) { FalcomPrepare(songCode); songCode &= 0xFF; }
            code = songCode;
            command = 1;
            var init = Log.Select(x => (0.0, x.port, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
        }

        public bool Waiting { get { return waitReads >= 4; } }

        /// <summary>
        /// hoot oldfalcom.cpp Play: 곡 코드 위 바이트 = 드라이버 (prog 번호), prog (24KB)를 0000h (romancia 1 = 1000h, asteka2 = 8000h)에,
        /// 표의 주소를 E00Eh~E015h에 (시작 코드가 끼어들기 벡터 · 시작 · 멈춤으로 부른다), 32h bit7 (인터럽트 멈춤).
        /// 1xh (xanadu 12xx~14xx · xanadu2 12xx~)는 2001년판 뒤에 생긴 "음식 없음" 판 — 드라이버는 x, 음식 바이트를 안 채움.
        /// </summary>
        private void FalcomPrepare(int songCode)
        {
            int hi = (songCode >> 8) & 0xFF;
            int drv = hi & 0x0F;
            falcomDrv = drv;
            falcomFoodEmpty = (hi & 0xF0) == 0x10;
            portSintm |= 0x80;
            if (Progs.TryGetValue(drv, out var p))
            {
                int ofs = Falcom == "romancia" && drv == 1 ? 0x1000 : Falcom == "asteka2" ? 0x8000 : 0x0000;
                for (int i = 0; i < 0x6000; i++) Ram[(ofs + i) & 0xFFFF] = i < p.Length ? p[i] : (byte)0;
            }
            else for (int i = 0; i < 0x6000; i++) Ram[i] = 0xC9;
            int ind = -1;
            switch (Falcom)
            {
                case "romancia": ind = drv == 1 ? 0 : 1; break;
                case "xanadu":
                    if (drv >= 1 && drv <= 6) ind = drv + 1;
                    Ram[0x617A] = 0x09;                       // 음량
                    if (drv >= 2 && drv <= 4)
                    {
                        if (!falcomFoodEmpty) Ram[0x60A7] = 0xFF;   // 음식 ≠ 0
                        Ram[0x607D] = 0x00;                   // 거울 없음
                        Ram[0x6170] = 0x00;                   // 싸움 아님
                    }
                    else if (drv == 6)
                    {
                        // Ending 2
                        Word(0x06AF, 1); Word(0x06B8, 1); Word(0x06C1, 1);
                        Word(0x06B1, 0x09E2); Word(0x06B3, 0x09E2);
                        Word(0x06BA, 0x0B2F); Word(0x06BC, 0x0B2F);
                        Word(0x06C3, 0x0CA3); Word(0x06C5, 0x0CA3);
                        Ram[0x0226] = 0x0C;                   // 음량
                        Ram[0x0227] = 0xAA;                   // 타이머 B
                    }
                    break;
                case "xanadu2": if (drv >= 1 && drv <= 5) ind = drv + 7; break;
                case "asteka2": ind = 13; break;
            }
            if (ind < 0) return;
            var t = FalcomTable[ind];
            Word(0xE00E, t.intAdr); Word(0xE010, t.start); Word(0xE012, t.stop); Word(0xE014, t.codeAdr);
            if (t.iffAdr != 0) Ram[t.iffAdr] = 0xE0;
        }

        private void Word(int at, int v) { Ram[at & 0xFFFF] = (byte)v; Ram[(at + 1) & 0xFFFF] = (byte)(v >> 8); }

        /// <summary>xanadu2: OUT 02h,n = bgm n (13KB)을 5C00h에, 엔딩 (드라이버 5)이 아니면 60A5h = FFh (음식 ≠ 0, 아니면 음이 어긋남).</summary>
        private void FalcomBgm(int n)
        {
            var d = Bgm(n);
            if (d == null) return;
            for (int i = 0; i < 13 * 1024; i++) Ram[(0x5C00 + i) & 0xFFFF] = i < d.Length ? d[i] : (byte)0;
            if (falcomDrv != 5 && !falcomFoodEmpty) Ram[0x60A5] = 0xFF;
            Loads++;
        }

        private long TimerAPeriod { get { return Math.Max(1, (long)(72.0 * (1024 - timerA) / BaseClock * CpuHz)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(1152.0 * (256 - timerB) / BaseClock * CpuHz)); } }

        public void Run(long cycles, bool untilWaiting = false)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextVrtc) { nextVrtc += vrtcPeriod; if (Vrtc) vrtcPending = true; }
                if (now >= nextRtc) { nextRtc += rtcPeriod; if (Rtc) rtcPending = true; }
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; Overflow(0); }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; Overflow(1); }
                if (Cpu.Iff1 && (vrtcPending || rtcPending || opnPending))
                {
                    int level = vrtcPending ? 1 : rtcPending ? 2 : 4;
                    if (level == 1) vrtcPending = false; else if (level == 2) rtcPending = false; else opnPending = false;
                    Interrupt(level);
                }
                if (Cpu.Halted)
                {
                    long next = Math.Min(Math.Min(nextVrtc, nextRtc), Math.Min(nextTimerA, nextTimerB));
                    long to = Cpu.Iff1 ? Math.Min(end, next) : end;
                    if (to > Cpu.Cycles) Cpu.Cycles = to; else Cpu.Cycles++;
                    continue;
                }
                Cpu.Step();
                if (untilWaiting && Waiting) return;
            }
        }

        public void RunSeconds(double seconds) { Run((long)(CpuHz * seconds)); }

        private void Overflow(int which)
        {
            // hoot ssFMTimer · mucom88.cpp: 깃발은 LOAD만으로 서고, IRQ는 ENABLE일 때 넘칠 때마다 (32h bit7 = SINTM이면 버림)
            timerFlags |= which == 0 ? 1 : 2;
            if ((timerCtl & (which == 0 ? 4 : 8)) != 0 && (portSintm & 0x80) == 0 && !(which == 0 && Falcom != null)) opnPending = true;   // oldfalcom.cpp: 타이머 B만 IRQ
        }

        private void Interrupt(int level)
        {
            if (Cpu.Im != 1)
            {
                // 벡터 표 칸이 비어 있으면 (N88-BASIC이 채우던 F300h 표 등, 시작 코드가 그 칸을 안 씀) BASIC 기본 처리기처럼 그냥 넘긴다
                int at = ((Cpu.I << 8) | (level * 2)) & 0xFFFF;
                if ((Ram[at] | (Ram[(at + 1) & 0xFFFF] << 8)) == 0) { Ignored++; return; }
            }
            Interrupts++;
            Cpu.Halted = false;
            Cpu.Iff1 = Cpu.Iff2 = false;
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Ram[Cpu.SP] = (byte)Cpu.PC; Ram[(Cpu.SP + 1) & 0xFFFF] = (byte)(Cpu.PC >> 8);
            if (Cpu.Im == 1) Cpu.PC = 0x38;
            else
            {
                int v = ((Cpu.I << 8) | (level * 2)) & 0xFFFF;
                Cpu.PC = Ram[v] | (Ram[(v + 1) & 0xFFFF] << 8);
            }
            Cpu.Cycles += 19;
        }

        public byte Read(int a) { return Ram[a & 0xFFFF]; }
        public void Write(int a, byte v) { Ram[a & 0xFFFF] = v; }

        private void Emit(int port, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuHz), port, reg, val));
        }

        private void OpnWrite(int port, int reg, int v)
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
                            Emit(0, reg, v & 0xC0);   // CSM · 채널 3 모드만 칩에
                            return;
                        }
                }
            }
            Emit(port, reg, v);
        }

        public byte In(int port)
        {
            port &= 0xFF;
            if (port != 0x00) waitReads = 0;
            switch (port)
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
                case 0x80: return (byte)(code >> 24);
                case 0x44: case 0xA8: return (byte)(timerFlags & 3);   // A8h~ADh = 사운드 보드 II (hoot ed2 · mistyblue: 같은 칩)
                case 0x45: case 0xA9: return addr0 == 0xFF ? (byte)(Opna ? 1 : 0) : regs[addr0 & 0xFF];
                case 0x46: case 0xAC: return (byte)((timerFlags & 3) | (Opna ? 0x08 : 0));   // OPN 판도 FFh (바쁨)로 두면 f_crisis가 기다리다 멈춘다
                case 0x47: case 0xAD: return regs[0x100 + (addr1 & 0xFF)];
                case 0x32: case 0xAA: return (byte)portSintm;
                case 0x40: return 0xFF ^ 0x20;   // 시스템 포트: VRTC 비트 등 (대략)
                default: return 0xFF;
            }
        }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            switch (port)
            {
                case 0x00: command = 0; break;
                case 0x01: Load(v); break;
                case 0x02: if (Falcom == "xanadu2") FalcomBgm(v); break;
                case 0x44: case 0xA8: addr0 = v; break;
                case 0x45: case 0xA9: OpnWrite(0, addr0, v); break;
                case 0x46: case 0xAC: if (Opna) addr1 = v; break;
                case 0x47: case 0xAD: if (Opna) OpnWrite(1, addr1, v); break;
                case 0x32: case 0xAA: portSintm = v; break;
                case 0xE6: portE6 = v; break;   // 기억만: hoot는 RTC · VRTC를 옵션 use_rtc · use_vrtc로만 낸다 (Kami no Machi는 E6h ← 3 뒤 RTC 벡터가 EI 없는 RET라 인터럽트가 영영 꺼졌다)
            }
        }

        private void Load(int n)
        {
            var d = Bgm(n);
            if (d != null)
            {
                for (int i = 0; i < DataSize && DataAddr + i < 0x10000; i++) Ram[DataAddr + i] = 0;
                if (FileSize > DataSize && DataSize > 0)
                {
                    // mfile_size > mdata_size: 파일 (mfile_size까지) = 버퍼, 곡 코드 8~23비트 = 버퍼 안 위치에서 mdata_size만 (hoot snatcher.cpp:
                    // OUT 00h → bgm[code]에서 3000h; KOEI PATCH는 DE = 4000h를 바로 드라이버에 넘긴다 — 예전엔 늘 0에서 읽어 모든 곡이 첫 곡)
                    int ofs = (code >> 8) & 0xFFFF;
                    int lim = Math.Min(FileSize, d.Length);
                    for (int i = 0; i < DataSize && ofs + i < lim && DataAddr + i < 0x10000; i++) Ram[DataAddr + i] = d[ofs + i];
                }
                else
                {
                    int len = FileSize > 0 ? Math.Min(FileSize, d.Length) : d.Length;
                    for (int i = 0; i < len && DataAddr + i < 0x10000; i++) Ram[DataAddr + i] = d[i];
                }
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
            var w = new VgmWriter { System = "PC-8801 (hoot)", Notes = "hoot pc88" };
            if (Opna) w.SetClock(VgmWriter.ClockYm2608, BaseClock * 2);
            else w.SetClock(VgmWriter.ClockYm2203, BaseClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write((byte)(Opna ? 0x56 + x.port : 0x55), x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
