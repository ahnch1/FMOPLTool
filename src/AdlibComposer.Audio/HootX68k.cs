using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot X68000 (driver x68k, type generic): 문서가 없어 hoot 목록의 시작 코드 ("X680x0 SOUND DRIVER EMULATOR for 'hoot'")를 거꾸로 읽어 맞춘 규약.
/// <para>기계: MC68000 10MHz, 16MB 메모리 (I/O 자리 빼고 전부 RAM — 시작 코드의 SSP가 00F0FFFEh), 리셋 = 0번지 SSP · 4번지 PC.
/// 칩: OPM (YM2151 4MHz, E90001h 주소 · E90003h 데이터 / 상태), ADPCM (MSM6258, E92001h 명령 · 상태 · E92003h 데이터, 클럭 = OPM 1Bh bit7 (1 = 4MHz, 0 = 8MHz),
/// 분주 · 팬 = 8255 PPI 포트 C E9A005h bit2~3 · bit0~1), DMA (HD63450 채널 3 E840C0h~ = ADPCM 바이트를 칩 속도로 넣음: 보통 · 배열 연결 · 링크 배열 연결 · 계속 모드).</para>
/// <para>인터럽트: OPM 타이머 IRQ = 레벨 6 자동 벡터 (벡터 30, 시작 코드의 78h 처리기가 드라이버를 부름). 옵션 mfp면 MFP (MC68901, E88001h~)를 흉내 내어
/// OPM IRQ = GPIP3, 타이머 A~D (4MHz ÷ 4~200), V-DISP (GPIP4 · 타이머 A 이벤트) → 레벨 6 벡터 (VR & F0h) + 채널. 옵션 dmaint면 DMA 끝 인터럽트 (레벨 3, NIV / EIV).</para>
/// <para>hoot 포트: E00000h = 명령 (0이 아니면 재생; 시작 코드가 CLR로 비움), E00001h · E00002h = 곡 코드 아래 · 위 바이트, E00010h ← 예외 번호 (시작 코드의 예외 처리기가 씀),
/// E00800h 읽기 = 쉼 (다음 일까지 건너뜀), E00801h · E00802h = midiout · midiout_type.</para>
/// <para>rom: code · data = 주소에 파일, x = Human68k 실행 파일 (머리말 64바이트 빼고 text · data를 주소에, bss 0, 재배치). 180초 미리 렌더 → VGM (YM2151 + OKIM6258).</para>
/// </summary>
public static class HootX68k
{
    public const double CpuClock = 10000000, OpmClock = 4000000, MfpClock = 4000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;
    /// <summary>V-DISP 주기 (31kHz 모드 약 55.46Hz) · 화면 표시 몫.</summary>
    public const double FrameHz = 55.46, DisplayPart = 0.9;

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, DirFiles(dir));
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    /// <summary>zip을 푼 폴더의 파일, 없으면 같은 폴더의 다른 zip에서 같은 이름 (여러 게임이 같이 쓰는 zmusic.x · cos68snd.bin · boot.bin …).</summary>
    public static Func<string, byte[]> DirFiles(string dir)
    {
        string zip = ZipFolder.SourceZip(dir);
        string library = zip != null ? Path.GetDirectoryName(zip) : null;
        return name =>
        {
            string f = HootArchive.FindFile(dir, name);
            if (f != null) return File.ReadAllBytes(f);
            var shared = library != null ? Dos.SharedPrograms.Find(library, name, zip) : null;
            return shared?.data;
        };
    }

    /// <summary>진단: 부팅 전에 기계를 받아 볼 곳 (시험의 추적).</summary>
    public static Action<Machine> BootHook;

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file, Action<string> trace = null)
    {
        var m = new Machine
        {
            Trace = trace,
            Files = file,
            UseMfp = e.Option("mfp", 0) != 0,
            DmaInterrupt = e.Option("dmaint", 0) != 0,
            MidiOut = e.Option("midiout", 0),
            MidiType = e.Option("midiout_type", 0),
        };
        foreach (var (type, off, text) in e.Roms)
        {
            long at = Math.Max(0, off);
            switch (type)
            {
                case "code":
                case "data":
                    {
                        var d = file(text);
                        if (d == null) { m.Missing.Add(text); break; }
                        m.Put(at, d);
                        break;
                    }
                case "x":
                    {
                        var d = file(text);
                        if (d == null) { m.Missing.Add(text); break; }
                        if (!LoadX(m, at, d)) m.Put(at, d);
                        break;
                    }
                default: m.Unsupported.Add(type); break;
            }
        }
        BootHook?.Invoke(m);
        m.Start();
        m.Run((long)(CpuClock * 3), untilWaiting: true);
        return m;
    }

    // ------------------------------------------------------------ MIDI 판 (midiout)

    /// <summary>
    /// "항목.hoot::n" (MIDI 판, YM3802 MIDI 보드) → 살아 있는 MIDI 곡: 재생기가 틱을 달라고 할 때마다 기계를 그만큼 돌려 보낸 바이트를 이벤트로 붙인다
    /// (<see cref="Dos.HootPc98.LoadMidi"/>와 같은 방식, 4분음표 480틱 · 1초 960틱). 기계는 곡을 열 때 뒤에서 세운다.
    /// </summary>
    public static MidiSong LoadMidi(string path)
    {
        string file = ChipSongs.FilePart(path);
        NezM3u.SplitTrack(path, out int n);
        var e = HootArchive.Load(file);
        if (e.Titles.Count == 0) throw new InvalidDataException("hoot 항목에 곡 목록(titlelist)이 없습니다.");
        n = Math.Max(0, Math.Min(n, e.Titles.Count - 1));
        var (code, title) = e.Titles[n];
        string dir = Path.GetDirectoryName(Path.GetFullPath(file));
        const int tps = Dos.HootPc98.MidiTicksPerSecond;
        var song = new MidiSong
        {
            FilePath = path, Title = title, Division = Dos.HootPc98.MidiDivision,
            FormatName = "hoot x68k MIDI (YM3802)", Comment = e.Name,
        };
        song.PrepareLive((int)(DefaultSeconds * tps), Dos.HootPc98.StandardOf(e));
        var pre = System.Threading.Tasks.Task.Run(() => Boot(e, DirFiles(dir)));
        Machine m = null;
        long t0 = 0;
        int curTick = 0, fed = 0;
        var parser = new Dos.MidiByteParser();
        parser.Message = (st, a, b, sx) => song.Events.Add(new MidiEvent { Tick = curTick, Status = (byte)st, Data1 = (byte)a, Data2 = (byte)b, SysEx = sx });
        song.Extend = target =>
        {
            try
            {
                if (m == null)
                {
                    m = pre.GetAwaiter().GetResult();
                    foreach (var x in m.MidiOutLog) parser.Feed(x.b);   // 드라이버 초기화 (리셋 SysEx · 음색) = 0틱
                    fed = m.MidiOutLog.Count;
                    m.Play(code);
                    t0 = m.Cpu.Cycles;
                }
                target = Math.Min(target, song.LengthTicks + tps);
                long until = t0 + (long)(target / (double)tps * CpuClock);
                if (until > m.Cpu.Cycles) m.Run(until - m.Cpu.Cycles);
                for (; fed < m.MidiOutLog.Count; fed++)
                {
                    var x = m.MidiOutLog[fed];
                    curTick = Math.Max(curTick, (int)((x.t - t0) / CpuClock * tps));
                    parser.Feed(x.b);
                }
                song.RenderedTicks = Math.Max(song.RenderedTicks, target);
            }
            catch (Exception)
            {
                song.RenderedTicks = int.MaxValue;   // 기계가 멈춤: 더 채우지 않는다
            }
        };
        return song;
    }

    /// <summary>Human68k .X: 머리말 ("HU", 기준 주소, 시작, text · data · bss · 재배치 크기) 뒤 text · data를 at에 올리고 재배치 (기준 주소 → at).</summary>
    public static bool LoadX(Machine m, long at, byte[] d)
    {
        if (d.Length < 0x40 || d[0] != 'H' || d[1] != 'U') return false;
        uint Be32(int i) { return (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]); }
        uint baseAddr = Be32(4), text = Be32(12), data = Be32(16), bss = Be32(20), rel = Be32(24);
        long body = (long)text + data;
        if (0x40 + body > d.Length) return false;
        var img = new byte[body];
        Array.Copy(d, 0x40, img, 0, body);
        m.Put(at, img);
        for (long i = 0; i < bss && at + body + i < m.Ram.Length; i++) m.Ram[at + body + i] = 0;
        uint delta = (uint)(at - baseAddr);
        long p = 0x40 + body, end = Math.Min(d.Length, p + rel);
        long pos = at;
        while (p + 2 <= end)
        {
            uint w = (uint)(d[p] << 8 | d[p + 1]); p += 2;
            if (w == 1)
            {
                if (p + 4 > end) break;
                w = Be32((int)p); p += 4;
            }
            bool word = (w & 1) != 0;
            pos += w & ~1u;
            if (pos < 0 || pos + 4 > m.Ram.Length) break;
            if (word)
            {
                uint v = (uint)(m.Ram[pos] << 8 | m.Ram[pos + 1]) + delta;
                m.Ram[pos] = (byte)(v >> 8); m.Ram[pos + 1] = (byte)v;
            }
            else
            {
                uint v = (uint)(m.Ram[pos] << 24 | m.Ram[pos + 1] << 16 | m.Ram[pos + 2] << 8 | m.Ram[pos + 3]) + delta;
                m.Ram[pos] = (byte)(v >> 24); m.Ram[pos + 1] = (byte)(v >> 16); m.Ram[pos + 2] = (byte)(v >> 8); m.Ram[pos + 3] = (byte)v;
            }
        }
        m.XLoads++;
        return true;
    }

    /// <summary>MFP (MC68901) 타이머 하나.</summary>
    private struct MfpTimer
    {
        public int Control, Data, Count;
        public long Next, Period;
    }

    /// <summary>X68000 기계 (hoot 흉내).</summary>
    public sealed class Machine : IM68kBus
    {
        public readonly byte[] Ram = new byte[0x1000000];
        public readonly M68000 Cpu;
        public bool UseMfp, DmaInterrupt;
        /// <summary>mfp 옵션 없이도 IOCS (TIMERDST · VDISPST)가 MFP 타이머를 켬 (OPM IRQ는 그대로 자동 벡터).</summary>
        public bool MfpActive;
        private bool MfpOn { get { return UseMfp || MfpActive; } }
        public int MidiOut, MidiType;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기 (시간 = 44100Hz 샘플, 재생 명령부터; chip 0 = OPM, 1 = MSM6258 (reg 0 명령 · 1 데이터 · 2 팬 · 8~0Bh 클럭 · 0Ch 분주)).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        /// <summary>시작 코드가 E00010h에 쓴 예외 번호 (처음 몇 개).</summary>
        public readonly List<int> Errors = new List<int>();
        public int XLoads, OpmInterrupts, MfpInterrupts, DmaBytes, DmaInterrupts;
        /// <summary>진단: 예외 보고 · MFP 쓰기 등.</summary>
        public Action<string> Trace;

        private int command, code, waitReads;
        private bool idle;
        private long playStart = -1;

        public Machine()
        {
            Cpu = new M68000(this) { AddressErrors = false };
            for (int i = 0; i < 4; i++) timers[i].Next = long.MaxValue;
        }

        public void Put(long at, byte[] d)
        {
            for (long i = 0; i < d.Length; i++) { long a = at + i; if (a >= 0 && a < Ram.Length) Ram[a] = d[i]; }
        }

        public void Start()
        {
            Mfp[0x17] = 0x40;   // VR: X68000 IOCS와 같은 벡터 40h~ (자동 EOI)
            for (int ch = 0; ch < 4; ch++) { Dma[ch * 0x40 + 0x25] = 0x0F; Dma[ch * 0x40 + 0x27] = 0x0F; }   // NIV · EIV 처음 값
            Cpu.Reset();
            nextFrame = frameCycles - (long)(frameCycles * DisplayPart);
            Reschedule();
        }

        public void Play(int songCode)
        {
            code = songCode;
            command = 1;
            // 재생 전 칩 설정은 0초에 (ADPCM 데이터는 버림)
            var init = Log.Where(x => !(x.chip == 1 && x.reg == 1)).Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
        }

        public bool Waiting { get { return waitReads >= 4; } }

        public void RunSeconds(double seconds) { Run((long)(CpuClock * seconds)); }

        // ------------------------------------------------------------ 시간 · 일

        private long nextEvent = long.MaxValue;
        private readonly long frameCycles = (long)(CpuClock / FrameHz);
        private long nextFrame;
        private bool vdisp;

        private void Reschedule()
        {
            long n = nextFrame;
            n = Math.Min(n, Math.Min(opmNextA, opmNextB));
            if (MfpOn) for (int i = 0; i < 4; i++) n = Math.Min(n, timers[i].Next);
            n = Math.Min(n, dmaNext);
            n = Math.Min(n, midiNextGt);
            nextEvent = n;
        }

        public void Run(long cycles, bool untilWaiting = false)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextEvent) Events();
                Cpu.IrqLevel = IrqLevel();
                idle = false;
                Cpu.Step();
                if (untilWaiting && Waiting) return;
                if (!idle && !Cpu.Stopped)
                {
                    // nop … BRA 뒤로 (예외 뒤 쉬는 고리): 쉼으로 본다
                    ushort ir = Cpu.Ir;
                    if ((ir & 0xFF00) == 0x6000 && (ir & 0x80) != 0 && IsIdleLoop(Cpu.PC)) idle = true;
                }
                if (!(idle || Cpu.Stopped)) continue;
                int lv = IrqLevel();
                if (lv == 0 || (lv <= Cpu.IntMask && lv != 7))
                {
                    long to = Math.Min(end, nextEvent);
                    if (to > Cpu.Cycles) Cpu.Cycles = to;
                }
            }
        }

        private readonly Dictionary<uint, bool> idleLoops = new Dictionary<uint, bool>();

        /// <summary>pc가 nop만 있는 고리의 처음인가 (BRA.S가 돌아간 곳).</summary>
        private bool IsIdleLoop(uint pc)
        {
            if (idleLoops.TryGetValue(pc, out bool y)) return y;
            bool ok = false;
            for (uint a = pc; a < pc + 32 && a + 1 < Ram.Length; a += 2)
            {
                int w = Ram[a] << 8 | Ram[a + 1];
                if (w == 0x4E71) continue;
                if ((w & 0xFF00) == 0x6000 && (w & 0xFF) != 0 && (uint)(a + 2 + (sbyte)w) == pc) ok = true;
                break;
            }
            idleLoops[pc] = ok;
            return ok;
        }

        private void Events()
        {
            long now = Cpu.Cycles;
            while (now >= nextFrame)
            {
                // V-DISP: 귀선 끝 = 표시 시작 (상승) → 표시 끝 (하강)
                vdisp = !vdisp;
                long disp = (long)(frameCycles * DisplayPart);
                nextFrame += vdisp ? disp : frameCycles - disp;
                if (MfpOn) GpipChanged();
            }
            while (now >= opmNextA) { opmNextA += OpmPeriodA; if ((opmCtl & 4) != 0) { opmFlags |= 1; opmEvent = true; } }
            while (now >= opmNextB) { opmNextB += OpmPeriodB; if ((opmCtl & 8) != 0) { opmFlags |= 2; opmEvent = true; } }
            if (MfpOn)
            {
                GpipChanged();
                for (int i = 0; i < 4; i++)
                {
                    if (timers[i].Next > now) continue;
                    while (timers[i].Next <= now)
                    {
                        timers[i].Next += Math.Max(1, timers[i].Period);
                        MfpTimerZero(i);
                    }
                }
            }
            while (now >= dmaNext) DmaTick();
            while (now >= midiNextGt) { midiNextGt += MidiGtPeriod; MidiGtFired++; midiIsr |= MidiGtBit; }
            Reschedule();
        }

        private int IrqLevel()
        {
            int lv = 0;
            if (UseMfp) { if (MfpIrq()) lv = 6; }
            else if (opmEvent || (MfpActive && MfpIrq())) lv = 6;
            if (lv == 0 && MidiIrq) lv = 4;
            if (lv == 0 && DmaInterrupt && dmaIrq) lv = 3;
            return lv;
        }

        public int Acknowledge(int level)
        {
            if (level == 6)
            {
                if (UseMfp || !opmEvent)
                {
                    int ch = MfpHighest();
                    if (ch < 0) return 24;   // 가짜 인터럽트
                    MfpInterrupts++;
                    int bit = 1 << ch;
                    mfpIpr &= ~bit;
                    if ((Mfp[0x17] & 8) != 0) mfpIsr |= bit;
                    return (Mfp[0x17] & 0xF0) | ch;
                }
                OpmInterrupts++;
                opmEvent = false;
                return opmVector ? 0x43 : -1;
            }
            if (level == 4) return MidiAcknowledge();
            if (level == 3)
            {
                DmaInterrupts++;
                dmaIrq = false;
                bool err = (Dma[0xC1] != 0);
                int v = err ? Dma[0xE7] : Dma[0xE5];
                return v;
            }
            return -1;
        }

        // ------------------------------------------------------------ 메모리

        public byte Read8(uint a)
        {
            if (a >= 0xE00000 && a < 0xF00000) return IoRead(a);
            return Ram[a];
        }

        public ushort Read16(uint a)
        {
            if (a >= 0xE00000 && a < 0xF00000) return (ushort)(IoRead(a) << 8 | IoRead(a + 1));
            return (ushort)(Ram[a] << 8 | Ram[a + 1]);
        }

        public void Write8(uint a, byte v)
        {
            if (a >= 0xE00000 && a < 0xF00000) { IoWrite(a, v); return; }
            Ram[a] = v;
        }

        public void Write16(uint a, ushort v)
        {
            if (a >= 0xE00000 && a < 0xF00000) { IoWrite(a, (byte)(v >> 8)); IoWrite(a + 1, (byte)v); return; }
            Ram[a] = (byte)(v >> 8); Ram[a + 1] = (byte)v;
        }

        private byte IoRead(uint a)
        {
            if (a < 0xE01000)
            {
                switch (a)
                {
                    case 0xE00000:
                        if (command == 0) waitReads++; else waitReads = 0;
                        return (byte)command;
                    case 0xE00001: return (byte)code;
                    case 0xE00002: return (byte)(code >> 8);
                    case 0xE00003: return (byte)(code >> 16);
                    case 0xE00004: return (byte)(code >> 24);
                    case 0xE00800: idle = true; return 0;
                    case 0xE00801: return (byte)MidiOut;
                    case 0xE00802: return (byte)MidiType;
                }
                return 0;
            }
            if (a >= 0xE84000 && a < 0xE84100) return DmaRead((int)(a & 0xFF));
            if (a >= 0xE88000 && a < 0xE8A000) return MfpRead((int)(a & 0x3F));
            if (a >= 0xE90000 && a < 0xE92000) return (byte)((a & 1) != 0 ? opmFlags & 3 : 0xFF);   // OPM 상태 (바쁘지 않음)
            if (a >= 0xE92000 && a < 0xE94000)
            {
                if ((a & 3) == 1) return (byte)(adpcmPlaying ? 0x00 : 0x80);   // MSM6258 상태: bit7 = 쉬는 중
                return 0xFF;
            }
            if (a >= 0xE9A000 && a < 0xE9C000)
            {
                switch (a & 7)
                {
                    case 1: case 3: return 0xFF;   // 조이스틱: 누름 없음
                    case 5: return (byte)ppiC;
                }
                return 0xFF;
            }
            if (a >= 0xEAFA00 && a < 0xEAFA10 && MidiOut != 0) return (a & 1) != 0 ? MidiRead((int)(a & 0xF) >> 1) : (byte)0xFF;
            if (a >= 0xE80000 && a < 0xEC0000) return 0xFF;   // 다른 I/O: 없음
            return Ram[a];   // 텍스트 VRAM 등: 메모리
        }

        private void IoWrite(uint a, byte v)
        {
            if (a < 0xE01000)
            {
                switch (a)
                {
                    case 0xE00000: command = v; break;
                    case 0xE00010: HostCall(v); break;
                }
                return;
            }
            if (a >= 0xE84000 && a < 0xE84100) { DmaWrite((int)(a & 0xFF), v); return; }
            if (a >= 0xE88000 && a < 0xE8A000) { MfpWrite((int)(a & 0x3F), v); return; }
            if (a >= 0xE90000 && a < 0xE92000)
            {
                if ((a & 3) == 1) opmAddr = v;
                else if ((a & 3) == 3) OpmWrite(opmAddr, v);
                return;
            }
            if (a >= 0xE92000 && a < 0xE94000)
            {
                if ((a & 3) == 1) AdpcmCommand(v);
                else if ((a & 3) == 3) Emit(1, 1, v);
                return;
            }
            if (a >= 0xE9A000 && a < 0xE9C000)
            {
                if ((a & 7) == 5) PpiC(v);
                else if ((a & 7) == 7 && (v & 0x80) == 0)
                {
                    int bit = (v >> 1) & 7;
                    PpiC((v & 1) != 0 ? ppiC | (1 << bit) : ppiC & ~(1 << bit));
                }
                return;
            }
            if (a >= 0xEAFA00 && a < 0xEAFA10 && MidiOut != 0) { if ((a & 1) != 0) MidiWrite((int)(a & 0xF) >> 1, v); return; }
            if (a >= 0xE80000 && a < 0xEC0000) return;
            Ram[a] = v;
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        // ------------------------------------------------------------ OPM

        private int opmAddr, opmTa, opmTb, opmCtl, opmFlags, opmCt = -1, opm1B;
        /// <summary>mfp 없는 판: 타이머가 넘칠 때마다 레벨 6 자동 벡터 하나 (플래그를 안 지워도 다음 넘침에 또).</summary>
        private bool opmEvent;
        /// <summary>mfp 없는 판에서 IOCS OPMINTST를 hoot가 처리했으면 OPM 인터럽트를 벡터 43h로 (아니면 자동 벡터 6).</summary>
        private bool opmVector;
        private long opmNextA = long.MaxValue, opmNextB = long.MaxValue;
        private bool OpmIrq { get { return (opmFlags & 3) != 0; } }
        private long OpmPeriodA { get { return Math.Max(1, (long)(64.0 * (1024 - opmTa) / OpmClock * CpuClock)); } }
        private long OpmPeriodB { get { return Math.Max(1, (long)(1024.0 * (256 - opmTb) / OpmClock * CpuClock)); } }

        private void OpmWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x10: opmTa = (opmTa & 3) | (v << 2); return;
                case 0x11: opmTa = (opmTa & 0x3FC) | (v & 3); return;
                case 0x12: opmTb = v; return;
                case 0x14:
                    {
                        int old = opmCtl;
                        opmCtl = v;
                        if ((v & 0x10) != 0) opmFlags &= ~1;
                        if ((v & 0x20) != 0) opmFlags &= ~2;
                        long now = Cpu.Cycles;
                        if ((v & 1) != 0 && (old & 1) == 0) opmNextA = now + OpmPeriodA; else if ((v & 1) == 0) opmNextA = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) opmNextB = now + OpmPeriodB; else if ((v & 2) == 0) opmNextB = long.MaxValue;
                        Reschedule();
                        if (MfpOn) GpipChanged();
                        Emit(0, reg, v & 0x80);
                        return;
                    }
                case 0x1B:
                    {
                        // CT2 (bit7) = ADPCM 클럭 (1 = 4MHz, 0 = 8MHz)
                        int ct = (v >> 7) & 1;
                        opm1B = v;
                        if (ct != opmCt) { opmCt = ct; AdpcmClockChanged(); }
                        break;
                    }
            }
            Emit(0, reg, v);
        }

        // ------------------------------------------------------------ ADPCM (MSM6258) · PPI

        private bool adpcmPlaying;
        private int ppiC = 0x0B, emittedClock = -1, emittedDiv = -1, emittedPan = -1;
        private static readonly int[] Dividers = { 1024, 768, 512, 512 };

        private int AdpcmClock { get { return opmCt == 1 ? 4000000 : 8000000; } }
        private int AdpcmDivider { get { return (ppiC >> 2) & 3; } }

        /// <summary>ADPCM 바이트 하나의 CPU 클럭 (니블 둘).</summary>
        private long AdpcmByteCycles { get { return Math.Max(1, (long)(2.0 * Dividers[AdpcmDivider] / AdpcmClock * CpuClock)); } }

        private void AdpcmSettings()
        {
            int clk = AdpcmClock, div = AdpcmDivider, pan = ppiC & 3;
            if (clk != emittedClock)
            {
                emittedClock = clk;
                for (int i = 0; i < 4; i++) Emit(1, 8 + i, (clk >> (i * 8)) & 0xFF);
            }
            if (div != emittedDiv) { emittedDiv = div; Emit(1, 0x0C, div); }
            if (pan != emittedPan) { emittedPan = pan; Emit(1, 0x02, pan); }
        }

        private void AdpcmClockChanged()
        {
            AdpcmSettings();
            if (dmaNext != long.MaxValue) { dmaNext = Cpu.Cycles + AdpcmByteCycles; Reschedule(); }
        }

        private void PpiC(int v)
        {
            int old = ppiC;
            ppiC = v & 0xFF;
            if (((old ^ ppiC) & 0x0F) != 0) AdpcmClockChanged();
        }

        private void AdpcmCommand(int v)
        {
            if ((v & 1) != 0)
            {
                adpcmPlaying = false;
                Emit(1, 0, 1);
            }
            else if ((v & 2) != 0)
            {
                AdpcmSettings();
                if (!adpcmPlaying) Emit(1, 0, 2);
                adpcmPlaying = true;
            }
            DmaSchedule();
        }

        // ------------------------------------------------------------ DMA (HD63450 채널 3 → ADPCM)

        /// <summary>DMA 레지스터 E84000h~E840FFh (채널 3 = C0h~).</summary>
        public readonly byte[] Dma = new byte[0x100];
        private long dmaNext = long.MaxValue;
        private bool dmaIrq;
        private uint dmaMar, dmaBar;
        private int dmaMtc, dmaBtc;
        private bool dmaActive;

        private byte DmaRead(int r)
        {
            if (r >= 0xC0)
            {
                switch (r - 0xC0)
                {
                    case 0x0A: return (byte)(dmaMtc >> 8);
                    case 0x0B: return (byte)dmaMtc;
                    case 0x0C: return (byte)(dmaMar >> 24);
                    case 0x0D: return (byte)(dmaMar >> 16);
                    case 0x0E: return (byte)(dmaMar >> 8);
                    case 0x0F: return (byte)dmaMar;
                    case 0x1A: return (byte)(dmaBtc >> 8);
                    case 0x1B: return (byte)dmaBtc;
                    case 0x1C: return (byte)(dmaBar >> 24);
                    case 0x1D: return (byte)(dmaBar >> 16);
                    case 0x1E: return (byte)(dmaBar >> 8);
                    case 0x1F: return (byte)dmaBar;
                }
            }
            return Dma[r];
        }

        private void DmaWrite(int r, byte v)
        {
            if (r < 0xC0) { Dma[r] = v; return; }
            switch (r - 0xC0)
            {
                case 0x00:
                    // CSR: 1인 bit를 지움 (ACT · PCS 빼고)
                    Dma[r] &= (byte)~(v & 0xF6);
                    if ((Dma[r] & 0xF0) == 0) dmaIrq = false;
                    return;
                case 0x01: return;   // CER: 읽기만
                case 0x07: DmaControl(v); return;
                case 0x0A: dmaMtc = (dmaMtc & 0xFF) | (v << 8); return;
                case 0x0B: dmaMtc = (dmaMtc & 0xFF00) | v; return;
                case 0x0C: dmaMar = (dmaMar & 0x00FFFFFF) | ((uint)v << 24); return;
                case 0x0D: dmaMar = (dmaMar & 0xFF00FFFF) | ((uint)v << 16); return;
                case 0x0E: dmaMar = (dmaMar & 0xFFFF00FF) | ((uint)v << 8); return;
                case 0x0F: dmaMar = (dmaMar & 0xFFFFFF00) | v; return;
                case 0x1A: dmaBtc = (dmaBtc & 0xFF) | (v << 8); return;
                case 0x1B: dmaBtc = (dmaBtc & 0xFF00) | v; return;
                case 0x1C: dmaBar = (dmaBar & 0x00FFFFFF) | ((uint)v << 24); return;
                case 0x1D: dmaBar = (dmaBar & 0xFF00FFFF) | ((uint)v << 16); return;
                case 0x1E: dmaBar = (dmaBar & 0xFFFF00FF) | ((uint)v << 8); return;
                case 0x1F: dmaBar = (dmaBar & 0xFFFFFF00) | v; return;
            }
            Dma[r] = v;
        }

        private int DmaChain { get { return (Dma[0xC5] >> 2) & 3; } }

        private void DmaControl(int v)
        {
            int csr = 0xC0, ccr = 0xC7;
            if ((v & 0x10) != 0)
            {
                // SAB: 멈춤 (오류 11h)
                if (dmaActive)
                {
                    dmaActive = false;
                    Dma[0xC1] = 0x11;
                    Dma[csr] = (byte)((Dma[csr] & ~0x08) | 0x90);
                    if ((Dma[ccr] & 0x08) != 0) dmaIrq = true;
                }
                Dma[ccr] = (byte)(v & 0x68);
                DmaSchedule();
                return;
            }
            bool start = (v & 0x80) != 0 && !dmaActive;
            Dma[ccr] = (byte)v;
            if (start)
            {
                Dma[0xC1] = 0;
                Dma[csr] = (byte)((Dma[csr] & ~0xF0) | 0x08);
                dmaActive = true;
                if (DmaChain == 2 || DmaChain == 3)
                {
                    if (!DmaLoadChain()) { DmaFinish(); return; }
                }
            }
            DmaSchedule();
        }

        /// <summary>배열 연결 (2) · 링크 배열 연결 (3): 다음 덩어리를 MAR · MTC에. 없으면 false.</summary>
        private bool DmaLoadChain()
        {
            if (DmaChain == 2)
            {
                if (dmaBtc <= 0) return false;
                dmaMar = Rd32(dmaBar); dmaMtc = Rd16(dmaBar + 4); dmaBar += 6; dmaBtc--;
                return true;
            }
            if (dmaBar == 0) return false;
            uint b = dmaBar;
            dmaMar = Rd32(b); dmaMtc = Rd16(b + 4); dmaBar = Rd32(b + 6);
            return true;
        }

        private uint Rd32(uint a) { a &= 0xFFFFFF; return (uint)(Ram[a] << 24 | Ram[(a + 1) & 0xFFFFFF] << 16 | Ram[(a + 2) & 0xFFFFFF] << 8 | Ram[(a + 3) & 0xFFFFFF]); }
        private int Rd16(uint a) { a &= 0xFFFFFF; return Ram[a] << 8 | Ram[(a + 1) & 0xFFFFFF]; }

        private void DmaSchedule()
        {
            bool run = dmaActive && adpcmPlaying && (Dma[0xC7] & 0x20) == 0;
            if (run) { if (dmaNext == long.MaxValue) dmaNext = Cpu.Cycles + AdpcmByteCycles; }
            else dmaNext = long.MaxValue;
            Reschedule();
        }

        private void DmaTick()
        {
            dmaNext += AdpcmByteCycles;
            if (!dmaActive) { dmaNext = long.MaxValue; return; }
            if (dmaMtc <= 0)
            {
                if (!DmaBlockDone()) return;
            }
            int b = Read8(dmaMar & 0xFFFFFF);
            Emit(1, 1, b);
            DmaBytes++;
            int mac = (Dma[0xC6] >> 2) & 3;
            if (mac == 1) dmaMar++; else if (mac == 2) dmaMar--;
            dmaMtc--;
            if (dmaMtc <= 0) DmaBlockDone();
        }

        /// <summary>덩어리 하나 끝: 연결 · 계속 모드면 다음 덩어리, 아니면 끝. 계속하면 true.</summary>
        private bool DmaBlockDone()
        {
            int csr = 0xC0, ccr = 0xC7;
            if ((DmaChain == 2 || DmaChain == 3) && DmaLoadChain()) return true;
            if ((Dma[ccr] & 0x40) != 0 && DmaChain == 0)
            {
                // 계속 모드: BAR · BTC → MAR · MTC, BTC 표시
                dmaMar = dmaBar; dmaMtc = dmaBtc;
                Dma[ccr] &= 0xBF;
                Dma[csr] |= 0x40;
                if ((Dma[ccr] & 0x08) != 0) dmaIrq = true;
                return dmaMtc > 0;
            }
            DmaFinish();
            return false;
        }

        private void DmaFinish()
        {
            int csr = 0xC0, ccr = 0xC7;
            dmaActive = false;
            dmaMtc = 0;
            Dma[csr] = (byte)((Dma[csr] & ~0x08) | 0x80);
            Dma[ccr] &= 0x3F;
            if ((Dma[ccr] & 0x08) != 0) dmaIrq = true;
            dmaNext = long.MaxValue;
            if (hleAdpcm != 0) { hleAdpcm = 0; AdpcmCommand(1); }   // IOCS ADPCM 출력 끝: ADPCM도 멈춤
        }

        // ------------------------------------------------------------ MIDI 보드 (YM3802, CZ-6BM1 EAFA00h~)

        /// <summary>YM3802 레지스터: 번호 = 묶음 × 10h + n (R00~R03은 늘 보임, Rg4~Rg7은 R01의 묶음 번호 g로).</summary>
        public readonly byte[] MidiRegs = new byte[0x100];
        /// <summary>MIDI로 나간 바이트 (CPU 클럭, 바이트).</summary>
        public readonly List<(long t, byte b)> MidiOutLog = new List<(long, byte)>();
        /// <summary>진단: 레지스터별 쓰기 횟수 · 마지막 값.</summary>
        public readonly SortedDictionary<int, (int n, int last)> MidiWrites = new SortedDictionary<int, (int, int)>();
        public long MidiGtFired;
        /// <summary>범용 타이머 (R84 · R85, 14비트)가 세는 클럭.</summary>
        public static double MidiGtClock = 1000000;
        /// <summary>ISR bit: 범용 타이머 · TX 빔 (0 = 그 인터럽트 없음 — robokid 등은 TX 빔 인터럽트가 걸리면 처리기가 맴돈다).</summary>
        public static int MidiGtBit = 0x80, MidiTxBit = 0x00;
        private int midiIsr;
        private long midiNextGt = long.MaxValue;

        private int MidiGtr { get { return (MidiRegs[0x84] | (MidiRegs[0x85] & 0x3F) << 8); } }
        private long MidiGtPeriod { get { return Math.Max(1, (long)(Math.Max(1, MidiGtr) * CpuClock / MidiGtClock)); } }
        /// <summary>보낼 버퍼는 늘 비어 있음: TX 빔 인터럽트 원인은 켜 두면 늘 걸린다.</summary>
        private int MidiIsrNow { get { return midiIsr | MidiTxBit; } }
        private bool MidiIrq { get { return (MidiIsrNow & MidiRegs[0x06]) != 0; } }

        private int MidiHighest()
        {
            int p = MidiIsrNow & MidiRegs[0x06];
            for (int b = 7; b >= 0; b--) if ((p & (1 << b)) != 0) return b;
            return -1;
        }

        private int MidiAcknowledge()
        {
            int b = MidiHighest();
            if (b < 0) return 24;
            return (MidiRegs[0x04] & 0xE0) | (b << 1);
        }

        private byte MidiRead(int r)
        {
            switch (r)
            {
                case 0: { int b = Math.Max(0, MidiHighest()); return (byte)((MidiRegs[0x04] & 0xE0) | (b << 1)); }   // IVR
                case 1: return MidiRegs[0x01];
                case 2: return (byte)MidiIsrNow;   // ISR
                case 3: return 0;
            }
            int idx = ((MidiRegs[0x01] & 0x0F) << 4) | r;
            switch (idx)
            {
                case 0x54: return 0xC0;   // TSR: 보낼 버퍼 비었음 · 받을 수 있음
                case 0x34: return 0x00;   // RSR: 받은 것 없음
                case 0x36: return 0x00;
                case 0x16: return 0x00;   // FSR
            }
            return MidiRegs[idx];
        }

        private void MidiWrite(int r, byte v)
        {
            int idx = r <= 3 ? r : ((MidiRegs[0x01] & 0x0F) << 4) | r;
            MidiWrites.TryGetValue(idx, out var w);
            MidiWrites[idx] = (w.n + 1, v);
            switch (idx)
            {
                case 0x01:
                    MidiRegs[0x01] = (byte)(v & 0x0F);
                    if ((v & 0x80) != 0) { midiIsr = 0; Array.Clear(MidiRegs, 2, MidiRegs.Length - 2); midiNextGt = long.MaxValue; Reschedule(); }
                    return;
                case 0x03: midiIsr &= ~v; return;   // ICR: 그 bit 지움
                case 0x56: MidiOutLog.Add((Cpu.Cycles, v)); return;   // TDR: 보냄
                case 0x84:
                case 0x85:
                    MidiRegs[idx] = v;
                    midiNextGt = MidiGtr != 0 ? Cpu.Cycles + MidiGtPeriod : long.MaxValue;
                    Reschedule();
                    return;
            }
            MidiRegs[idx] = v;
        }

        // ------------------------------------------------------------ MFP (MC68901)

        /// <summary>MFP 레지스터 (홀수 주소 01h~2Fh를 그대로 번호로).</summary>
        public readonly byte[] Mfp = new byte[0x40];
        private int mfpIpr, mfpIsr, gpipIn = -1;
        private readonly MfpTimer[] timers = new MfpTimer[4];
        private static readonly int[] Prescale = { 0, 4, 10, 16, 50, 64, 100, 200 };
        /// <summary>타이머 A~D의 인터럽트 채널.</summary>
        private static readonly int[] TimerChannel = { 13, 8, 5, 4 };

        private int Ier { get { return Mfp[0x07] << 8 | Mfp[0x09]; } }
        private int Imr { get { return Mfp[0x13] << 8 | Mfp[0x15]; } }

        /// <summary>GPIP 입력: bit3 = OPM IRQ (낮음 = 걸림), bit4 = V-DISP, 나머지 1.</summary>
        private int GpipInput { get { return 0xE7 | (OpmIrq ? 0 : 0x08) | (vdisp ? 0x10 : 0); } }

        private void GpipChanged()
        {
            int now = GpipInput;
            if (gpipIn < 0) { gpipIn = now; return; }
            int diff = now ^ gpipIn;
            if (diff == 0) return;
            int aer = Mfp[0x03];
            for (int bit = 0; bit < 8; bit++)
            {
                int m = 1 << bit;
                if ((diff & m) == 0) continue;
                bool rising = (now & m) != 0;
                bool active = ((aer & m) != 0) == rising;
                if (!active) continue;
                int ch = bit < 4 ? bit : bit + 2;   // GPIP0~3 = 채널 0~3, GPIP4 · 5 = 6 · 7, GPIP6 · 7 = 14 · 15
                if (bit >= 6) ch = bit + 8;
                MfpRequest(ch);
                if (bit == 4) MfpTimerEvent(0);   // 타이머 A 이벤트 입력 = V-DISP
            }
            gpipIn = now;
        }

        private void MfpRequest(int ch)
        {
            if ((Ier & (1 << ch)) != 0) mfpIpr |= 1 << ch;
        }

        private bool MfpIrq() { return MfpHighest() >= 0; }

        private int MfpHighest()
        {
            int p = mfpIpr & Imr;
            if (p == 0) return -1;
            for (int ch = 15; ch >= 0; ch--)
            {
                if ((mfpIsr & (1 << ch)) != 0) return -1;   // 이보다 높은 것이 처리 중
                if ((p & (1 << ch)) != 0) return ch;
            }
            return -1;
        }

        private byte MfpRead(int r)
        {
            switch (r)
            {
                case 0x01: return (byte)((GpipInput & ~Mfp[0x05]) | (Mfp[0x01] & Mfp[0x05]));
                case 0x0B: return (byte)(mfpIpr >> 8);
                case 0x0D: return (byte)mfpIpr;
                case 0x0F: return (byte)(mfpIsr >> 8);
                case 0x11: return (byte)mfpIsr;
                case 0x1F: case 0x21: case 0x23: case 0x25: return (byte)TimerCount((r - 0x1F) / 2);
                case 0x2D: return 0x80;   // TSR: 보낼 수 있음
            }
            return Mfp[r];
        }

        private void MfpWrite(int r, byte v)
        {
            Trace?.Invoke("mfp " + r.ToString("X2") + " <- " + v.ToString("X2") + " at " + Cpu.PC.ToString("X6"));
            switch (r)
            {
                case 0x07: Mfp[r] = v; mfpIpr &= (v << 8) | 0xFF; return;
                case 0x09: Mfp[r] = v; mfpIpr &= 0xFF00 | v; return;
                case 0x0B: mfpIpr &= (v << 8) | 0xFF; return;
                case 0x0D: mfpIpr &= 0xFF00 | v; return;
                case 0x0F: mfpIsr &= (v << 8) | 0xFF; return;
                case 0x11: mfpIsr &= 0xFF00 | v; return;
                case 0x19: Mfp[r] = v; TimerSet(0, v & 0x0F); return;
                case 0x1B: Mfp[r] = v; TimerSet(1, v & 0x0F); return;
                case 0x1D: Mfp[r] = v; TimerSet(2, (v >> 4) & 7); TimerSet(3, v & 7); return;
                case 0x1F: case 0x21: case 0x23: case 0x25:
                    {
                        int i = (r - 0x1F) / 2;
                        Mfp[r] = v;
                        timers[i].Data = v;
                        if (timers[i].Control == 0) timers[i].Count = v == 0 ? 256 : v;
                        return;
                    }
            }
            Mfp[r] = v;
            if (r == 0x03 || r == 0x05) GpipChanged();
        }

        private void TimerSet(int i, int ctl)
        {
            int old = timers[i].Control;
            timers[i].Control = ctl;
            if (ctl == 0 || ctl >= 8 || !MfpOn)
            {
                timers[i].Next = long.MaxValue;
                if (ctl == 8 && old != 8) timers[i].Count = timers[i].Data == 0 ? 256 : timers[i].Data;
            }
            else
            {
                int count = timers[i].Data == 0 ? 256 : timers[i].Data;
                timers[i].Period = Math.Max(1, (long)((double)Prescale[ctl] * count / MfpClock * CpuClock));
                if (old != ctl || timers[i].Next == long.MaxValue) timers[i].Next = Cpu.Cycles + timers[i].Period;
            }
            Reschedule();
        }

        private int TimerCount(int i)
        {
            var t = timers[i];
            if (t.Control == 0 || t.Control >= 8 || t.Next == long.MaxValue) return t.Count & 0xFF;
            long left = Math.Max(0, t.Next - Cpu.Cycles);
            return (int)Math.Min(255, left * timers[i].Data / Math.Max(1, t.Period) + 1) & 0xFF;
        }

        private void MfpTimerZero(int i)
        {
            // 주기 = 데이터 레지스터 (다시 읽음)
            int count = timers[i].Data == 0 ? 256 : timers[i].Data;
            if (timers[i].Control >= 1 && timers[i].Control <= 7)
                timers[i].Period = Math.Max(1, (long)((double)Prescale[timers[i].Control] * count / MfpClock * CpuClock));
            MfpRequest(TimerChannel[i]);
        }

        /// <summary>이벤트 모드 타이머에 입력 하나.</summary>
        private void MfpTimerEvent(int i)
        {
            if (timers[i].Control != 8) return;
            if (--timers[i].Count > 0) return;
            timers[i].Count = timers[i].Data == 0 ? 256 : timers[i].Data;
            MfpRequest(TimerChannel[i]);
        }

        // ------------------------------------------------------------ hoot 바깥 부름 (E00010h)

        /// <summary>시작 코드가 처리하지 못한 IOCS · DOS 부름 (진단: "iocs 6D" · "dos FF09" → 횟수).</summary>
        public readonly Dictionary<string, int> Unhandled = new Dictionary<string, int>();
        public int IocsCalls, DosCalls, Pcm8Calls;
        /// <summary>IOCS ADPCM 출력 중: 0 = 아님, 2 = ADPCMOUT, 12h = 배열, 22h = 링크 배열.</summary>
        private int hleAdpcm;
        private uint heapNext = 0x800000;
        /// <summary>DOS가 여는 파일 (zip 안 이름 → 내용).</summary>
        public Func<string, byte[]> Files;
        private readonly Dictionary<int, (byte[] data, int pos)> open = new Dictionary<int, (byte[], int)>();

        /// <summary>"A:\DIR\FILE.DAT" → "FILE.DAT".</summary>
        private static string BareName(string path)
        {
            string p = path.Replace('/', '\\');
            return p.Substring(Math.Max(p.LastIndexOf('\\'), p.LastIndexOf(':')) + 1);
        }

        private string CString(uint a)
        {
            var b = new List<byte>();
            for (int i = 0; i < 256; i++) { byte c = Ram[(a + i) & 0xFFFFFF]; if (c == 0) break; b.Add(c); }
            return KoreanText.ShiftJis.GetString(b.ToArray()).Trim();
        }

        private void Count(string what)
        {
            Unhandled.TryGetValue(what, out int n);
            Unhandled[what] = n + 1;
        }

        /// <summary>
        /// E00010h 쓰기: 2Fh = IOCS (TRAP #15, 시작 코드가 처리하지 않은 번호 — hoot가 대신 함), FFh = DOS (F줄 FFxxh, 쌓인 PC를 다음 명령으로),
        /// 22h = PCM8 (TRAP #2), 그 밖 = 시작 코드의 예외 보고 (버스 · 주소 오류 등).
        /// </summary>
        private void HostCall(int v)
        {
            Trace?.Invoke("host " + v.ToString("X2") + " at " + Rd32(Cpu.A[7] + 2).ToString("X6") + " D0 " + Cpu.D[0].ToString("X8") + " D1 " + Cpu.D[1].ToString("X8") + " D2 " + Cpu.D[2].ToString("X8") + " A1 " + Cpu.A[1].ToString("X6"));
            switch (v)
            {
                case 0x2F: IocsCalls++; Iocs((int)(Cpu.D[0] & 0xFF)); return;
                case 0xFF: DosCalls++; Dos(); return;
                case 0x22: Pcm8Calls++; Count("pcm8 " + (Cpu.D[0] & 0xFFFF).ToString("X4")); Cpu.D[0] = 0; return;
            }
            if (Errors.Count < 16) Errors.Add(v);
        }

        private void Wr32(uint a, uint v) { a &= 0xFFFFFF; Ram[a] = (byte)(v >> 24); Ram[(a + 1) & 0xFFFFFF] = (byte)(v >> 16); Ram[(a + 2) & 0xFFFFFF] = (byte)(v >> 8); Ram[(a + 3) & 0xFFFFFF] = (byte)v; }

        /// <summary>MFP 채널 켜기 · 끄기 (IER · IMR 둘 다).</summary>
        private void MfpEnable(int ch, bool on)
        {
            int ier = ch >= 8 ? 0x07 : 0x09, bit = 1 << (ch & 7);
            if (on) { Mfp[ier] |= (byte)bit; Mfp[ier + 0x0C] |= (byte)bit; }
            else { Mfp[ier] &= (byte)~bit; Mfp[ier + 0x0C] &= (byte)~bit; mfpIpr &= ~(1 << ch); }
        }

        private void Iocs(int fn)
        {
            uint d1 = Cpu.D[1], d2 = Cpu.D[2], a1 = Cpu.A[1];
            uint result = 0;
            switch (fn)
            {
                case 0x00: case 0x01: case 0x02: case 0x04: result = 0; break;   // 키보드: 누름 없음
                case 0x60: AdpcmOut(0, a1, (int)d1, (int)d2); break;   // ADPCMOUT
                case 0x62: AdpcmOut(2, a1, (int)d1, (int)(d2 & 0xFFFF)); break;   // ADPCMAOT (배열)
                case 0x64: AdpcmOut(3, a1, (int)d1, 0); break;   // ADPCMLOT (링크 배열)
                case 0x66: result = (uint)(dmaActive && hleAdpcm != 0 ? hleAdpcm : 0); break;   // ADPCMSNS
                case 0x67:
                    // ADPCMMOD: 0 = 끝, 1 = 쉼, 2 = 다시
                    switch (d1 & 0xFF)
                    {
                        case 0: if (dmaActive) DmaControl(0x10); hleAdpcm = 0; AdpcmCommand(1); break;
                        case 1: DmaControl(Dma[0xC7] | 0x20); break;
                        case 2: DmaControl(Dma[0xC7] & ~0x20); break;
                    }
                    break;
                case 0x61: case 0x63: case 0x65: result = 0xFFFFFFFF; break;   // ADPCM 녹음: 없음
                case 0x68: OpmWrite((int)(d1 & 0xFF), (int)(d2 & 0xFF)); break;   // OPMSET
                case 0x69: result = (uint)(opmFlags & 3); break;   // OPMSNS
                case 0x6A:
                    // OPMINTST: 벡터 43h (MFP GPIP3)
                    Wr32(0x43 * 4, a1);
                    if (UseMfp) MfpEnable(3, a1 != 0);
                    else opmVector = a1 != 0;   // 시작 코드가 OPMINTST를 hoot에 맡김: OPM 인터럽트 = 벡터 43h (자동 벡터 처리기는 RTE뿐)
                    break;
                case 0x6B:
                    // TIMERDST: d1 = 분주 << 8 | 수, 벡터 44h (MFP 타이머 D)
                    Wr32(0x44 * 4, a1);
                    MfpActive = true;
                    MfpWrite(0x25, (byte)d1);
                    MfpWrite(0x1D, (byte)((Mfp[0x1D] & 0x70) | (a1 != 0 ? (int)((d1 >> 8) & 7) : 0)));
                    MfpEnable(4, a1 != 0);
                    break;
                case 0x6C:
                    // VDISPST: d1 = 수, 벡터 4Dh (MFP 타이머 A 이벤트 = V-DISP)
                    Wr32(0x4D * 4, a1);
                    MfpActive = true;
                    MfpWrite(0x1F, (byte)d1);
                    MfpWrite(0x19, (byte)(a1 != 0 ? 8 : 0));
                    MfpEnable(13, a1 != 0);
                    break;
                case 0x7F: { double t = Cpu.Cycles / CpuClock; result = (uint)(t * 100); Cpu.D[1] = 0; break; }   // ONTIME
                case 0x80:
                    {
                        // B_INTVCS: d1 = 벡터 번호, a1 = 새 주소 → d0 = 옛 주소
                        uint vec = d1 & 0xFFFF;
                        if (vec < 0x200) { result = Rd32(vec * 4); Wr32(vec * 4, a1); }   // 100h~ = IOCS 부름 표 (400h~)
                        else Count("iocs 80 " + vec.ToString("X3"));
                        break;
                    }
                case 0x8A: DmaMove(a1, Cpu.A[2], (int)d1, (int)d2); break;   // DMAMOVE: 바로 옮김
                case 0x8B:
                    // DMAMOV_A: a1 = 배열 (주소.l · 길이.w) × d2
                    for (uint i = 0, t = a1; i < (d2 & 0xFFFF); i++, t += 6) { DmaMove(Rd32(t), Cpu.A[2], (int)d1, Rd16(t + 4)); if (((d1 >> 2) & 3) == 1) Cpu.A[2] += (uint)Rd16(t + 4); }
                    break;
                case 0x8C:
                    // DMAMOV_L: a1 = 링크 배열 (주소.l · 길이.w · 다음.l)
                    for (uint t = a1, n = 0; t != 0 && n < 4096; n++) { DmaMove(Rd32(t), Cpu.A[2], (int)d1, Rd16(t + 4)); if (((d1 >> 2) & 3) == 1) Cpu.A[2] += (uint)Rd16(t + 4); t = Rd32(t + 6); }
                    break;
                case 0x8D: result = 0; break;   // DMAMODE: 쉼
                case 0x81: result = 0; break;   // B_SUPER
                case 0x8E: result = 0; break;   // BOOTINF
                case 0x8F: result = 0x13921127; break;   // ROMVER
                case 0xAC: result = 0; break;   // SYS_STAT
                default: Count("iocs " + fn.ToString("X2")); result = 0; break;
            }
            Cpu.D[0] = result;
        }

        /// <summary>IOCS DMAMOVE: d1 bit7 = 방향 (0 = a1 → a2), bit0~1 · bit2~3 = a1 · a2 주소 (0 고정 · 1 증가 · 2 감소), d2 = 바이트 수.</summary>
        private void DmaMove(uint a1, uint a2, int mode, int len)
        {
            int m1 = mode & 3, m2 = (mode >> 2) & 3;
            bool rev = (mode & 0x80) != 0;
            for (int i = 0; i < len && i < 0x1000000; i++)
            {
                uint p1 = (uint)(a1 + (m1 == 1 ? i : m1 == 2 ? -i : 0)) & 0xFFFFFF, p2 = (uint)(a2 + (m2 == 1 ? i : m2 == 2 ? -i : 0)) & 0xFFFFFF;
                if (rev) Write8(p1, Read8(p2)); else Write8(p2, Read8(p1));
            }
        }

        /// <summary>그 주소에 장치가 있는가 (_BUS_ERR 조사용): RAM · 흉내 내는 I/O는 있음, 확장 보드 자리 (SCSI · MIDI EAxxxxh, 사용자 I/O ECxxxxh, FPU E9Exxxh)는 없음.</summary>
        public bool Present(uint a)
        {
            a &= 0xFFFFFF;
            if (a >= 0xEA0000 && a < 0xEB0000) return MidiOut != 0 && a >= 0xEAFA00 && a < 0xEAFA10;
            if (a >= 0xEC0000 && a < 0xED0000) return false;
            if (a >= 0xE9E000 && a < 0xEA0000) return false;
            return true;
        }

        /// <summary>IOCS ADPCM 출력: d1 = 속도 &lt;&lt; 8 | 팬 (0 끔 · 1 왼쪽 · 2 오른쪽 · 3 둘 다), chain 0 = 보통 (a1 · 길이) · 2 = 배열 (a1 = 표, n개) · 3 = 링크 배열 (a1 = 표).</summary>
        private void AdpcmOut(int chain, uint a1, int d1, int len)
        {
            if (dmaActive) DmaControl(0x10);
            int rate = (d1 >> 8) & 7, pan = d1 & 3;
            if (rate > 4) rate = 4;
            int ct = rate <= 1 ? 1 : 0, div = rate <= 1 ? rate : rate - 2;
            int ppiPan = pan == 3 ? 0 : pan == 0 ? 3 : pan;   // PPI bit0 = 오른쪽 끔 · bit1 = 왼쪽 끔
            OpmWrite(0x1B, (opm1B & 0x7F) | (ct << 7));
            PpiC((ppiC & ~0x0F) | (div << 2) | ppiPan);
            Dma[0xC5] = (byte)(0x32 | (chain << 2));
            Dma[0xC6] = 0x04;
            Dma[0xC0] = 0;
            if (chain == 0) { dmaMar = a1; dmaMtc = len; }
            else if (chain == 2) { dmaBar = a1; dmaBtc = len; }
            else dmaBar = a1;
            hleAdpcm = chain == 0 ? 2 : chain == 2 ? 0x12 : 0x22;
            DmaControl(0x80);
            if (!dmaActive) { hleAdpcm = 0; return; }
            AdpcmCommand(2);
        }

        /// <summary>DOS (F줄 FFxxh): 인자는 부른 쪽 스택 (슈퍼바이저면 예외 틀 6바이트 위, 사용자 모드면 USP). 쌓인 PC를 2 올려 다음 명령으로 돌아가게 한다.</summary>
        private void Dos()
        {
            uint frame = Cpu.A[7];
            uint pc = Rd32(frame + 2);
            int op = Rd16(pc);
            Wr32(frame + 2, pc + 2);
            bool super = (Rd16(frame) & 0x2000) != 0;
            uint args = super ? frame + 6 : Cpu.Usp;
            int fn = op & 0xFF;
            uint result = 0;
            switch (fn)
            {
                case 0x00: case 0x4C: case 0x31: Count("dos exit " + fn.ToString("X2")); break;   // EXIT · EXIT2 · KEEPPR: 부른 곳으로 그냥 돌아감
                case 0x01: case 0x06: case 0x07: case 0x08: case 0x0B: result = 0; break;   // 입력: 없음
                case 0x02: case 0x05: case 0x09: case 0x0C: case 0x10: case 0x1E: case 0x1F: case 0x23: result = 0; break;   // 화면 · 프린터 출력
                case 0x0D: case 0x0E: result = 0; break;
                case 0x20:
                    {
                        // _SUPER: 인자 0 = 슈퍼바이저로 (옛 SSP를 돌려줌), 아니면 사용자 모드로 (여기서는 늘 슈퍼바이저로 둔다)
                        uint a = Rd32(args);
                        result = a == 0 ? Cpu.Ssp : 0;
                        break;
                    }
                case 0x25:
                    {
                        // _INTVCS (번호.w, 주소.l) → 옛 주소
                        uint vec = (uint)Rd16(args), addr = Rd32(args + 2);
                        if (vec < 0x100) { result = Rd32(vec * 4); Wr32(vec * 4, addr); }
                        else { Count("dos intvcs " + vec.ToString("X3")); result = 0; }
                        break;
                    }
                case 0x35:
                    {
                        uint vec = (uint)Rd16(args);
                        result = vec < 0x100 ? Rd32(vec * 4) : 0;   // _INTVCG
                        break;
                    }
                case 0x27: case 0x28: case 0x29: case 0x2A: case 0x2B: case 0x2C: case 0x2D: result = 0; break;   // 시각 · 날짜
                case 0x30: result = 0x36380302; break;   // _VERNUM: Human68k 3.02
                case 0x48: case 0x88:
                    {
                        // _MALLOC: 800000h~에서 차례로
                        uint size = Rd32(args);
                        if (size >= 0x00FFFFF0) { result = 0x81000000 | 0x400000; break; }
                        uint at = (heapNext + 15) & ~15u;
                        if (at + size >= 0xE00000) { result = 0x82000000; break; }
                        heapNext = at + size;
                        result = at;
                        break;
                    }
                case 0x49: case 0x4A: case 0x89: case 0x8A: result = 0; break;   // _MFREE · _SETBLOCK
                case 0x51: case 0x81: result = 0x7F00; break;   // _GETPDB
                case 0x44: result = 0; break;   // _IOCTRL
                case 0x3D:
                    {
                        // _OPEN (이름.l, 모드.w): zip 안 파일 (읽기만)
                        string name = CString(Rd32(args));
                        string bare = BareName(name);
                        var d = bare.Length > 0 ? Files?.Invoke(bare) : null;
                        if (d == null) { Count("dos open? " + bare); result = 0xFFFFFFFE; break; }   // -2: 파일 없음
                        int h = 5;
                        while (open.ContainsKey(h)) h++;
                        open[h] = (d, 0);
                        result = (uint)h;
                        break;
                    }
                case 0x3E: { open.Remove(Rd16(args)); result = 0; break; }   // _CLOSE
                case 0x3F:
                    {
                        // _READ (핸들.w, 버퍼.l, 길이.l)
                        int h = Rd16(args);
                        if (!open.TryGetValue(h, out var f)) { result = 0xFFFFFFFA; break; }
                        uint buf = Rd32(args + 2), len = Rd32(args + 6);
                        int n = (int)Math.Max(0, Math.Min(len, (long)f.data.Length - f.pos));
                        for (int i = 0; i < n; i++) Ram[(buf + i) & 0xFFFFFF] = f.data[f.pos + i];
                        open[h] = (f.data, f.pos + n);
                        result = (uint)n;
                        break;
                    }
                case 0x42:
                    {
                        // _SEEK (핸들.w, 거리.l, 기준.w)
                        int h = Rd16(args);
                        if (!open.TryGetValue(h, out var f)) { result = 0xFFFFFFFA; break; }
                        int dist = (int)Rd32(args + 2), mode = Rd16(args + 6);
                        long np = (mode == 0 ? 0 : mode == 1 ? f.pos : f.data.Length) + (long)dist;
                        np = Math.Max(0, Math.Min(f.data.Length, np));
                        open[h] = (f.data, (int)np);
                        result = (uint)np;
                        break;
                    }
                case 0x43: { string name = CString(Rd32(args)); result = Files?.Invoke(BareName(name)) != null ? 0x20u : 0xFFFFFFFE; break; }   // _CHMOD: 있으면 보관 속성
                case 0xF7:
                    {
                        // _BUS_ERR (크기.w, 읽을 곳.l, 쓸 곳.l): 0 = 됨, 2 = 읽기 버스 오류, 1 = 쓰기 버스 오류
                        uint src = Rd32(args + 2), dst = Rd32(args + 6);
                        result = !Present(src) ? 2u : !Present(dst) ? 1u : 0u;
                        if (result == 0) { int size = Rd16(args); if (size == 1) Write8(dst, Read8(src)); else if (size == 2) Write16(dst & ~1u, Read16(src & ~1u)); else if (size == 4) { Write16(dst & ~1u, Read16(src & ~1u)); Write16((dst & ~1u) + 2, Read16((src & ~1u) + 2)); } }
                        break;
                    }
                default: Count("dos " + op.ToString("X4")); result = 0xFFFFFFFF; break;
            }
            Cpu.D[0] = result;
        }

        // ------------------------------------------------------------ VGM

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "X68000 (hoot)", Notes = "hoot x68k" };
            bool opm = Log.Any(x => x.chip == 0), pcm = Log.Any(x => x.chip == 1 && x.reg == 1);
            w.SetClock(VgmWriter.ClockYm2151, OpmClock);
            if (pcm) { w.SetClock(VgmWriter.ClockOkim6258, 8000000); w.SetHeaderByte(0x94, 0x02); }
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x54, (byte)x.reg, (byte)x.val);
                else if (pcm) w.Write(0xB7, (byte)x.reg, (byte)x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (!opm && !pcm) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
