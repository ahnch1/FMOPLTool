using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// SAP (Slight Atari Player, 아타리 8비트 음악): 글 머리말 + 아타리 실행 파일 블록. 6502로 재생 루틴을 돌려
/// POKEY 쓰기($D200~$D20F, STEREO면 둘째 칩 $D210~$D21F)를 VGM(<see cref="VgmSong"/>, POKEY 0xBB)으로 모은다.
/// <para>머리말 (CR LF 줄, $FF $FF 앞까지): AUTHOR · NAME · DATE "…", SONGS n · DEFSONG n (0부터), TYPE B · C · D · S, INIT · MUSIC · PLAYER 주소 (16진 4자리),
/// FASTPLAY n (재생 간격 = 주사선 n개, 기본 PAL 312 · NTSC 262), STEREO, NTSC, TIME mm:ss.xxx [LOOP] (곡마다 차례로).</para>
/// <para>몸통 = 블록 (시작 · 끝 주소 + 바이트, 앞에 $FF $FF가 다시 올 수 있음) → 64KB RAM. 돌리는 방법 (참고: modizer asap.c · libGME Sap_Emu):
/// B = A = 곡으로 INIT 부르고, 간격마다 PLAYER를 JSR. C = PLAYER+3을 A = $70 · X/Y = MUSIC, 다음 A = 0 · X = 곡으로 부르고 간격마다 PLAYER+6.
/// D = INIT을 A = 곡으로 띄워 두고 (돌아오지 않아도 됨) 간격마다 인터럽트처럼 PLAYER (레지스터를 쌓고 RTI로 돌아감). S = INIT을 띄워 두고 간격마다 $45를 줄여 0이면 $B07B를 늘린다.</para>
/// <para>하드웨어 읽기: VCOUNT($D40B) · NMIST($D40F) · PAL($D014) · CONSOL($D01F) · RANDOM($D20A) · IRQST($D20E). 주사선 = 114사이클, 화면 = PAL 312 · NTSC 262줄.
/// CPU = NMOS 6502 (10진 모드 켬), 클럭 PAL 1.773447MHz · NTSC 1.789772MHz = POKEY 클럭. 곡 길이 = TIME, 없으면 2분.</para>
/// </summary>
public static class Sap
{
    public static readonly string[] Extensions = { ".sap" };
    public const double DefaultSeconds = 120, DefaultFade = 4;
    public const double PalClock = 1773447, NtscClock = 1789772;
    public const int LineCycles = 114;

    public sealed class Header
    {
        public string Author = "", Name = "", Date = "";
        public int Songs = 1, DefSong;
        public char Type = 'B';
        public int Init = -1, Music = -1, Player = -1;
        public int FastPlay = -1;
        public bool Stereo, Ntsc;
        public readonly List<double> Times = new List<double>();
        public readonly List<bool> Loops = new List<bool>();
        /// <summary>몸통 시작 ($FF $FF 다음).</summary>
        public int BodyAt;
        public int Lines { get { return Ntsc ? 262 : 312; } }
        public int PlayLines { get { return FastPlay > 0 ? FastPlay : Lines; } }
        public double Clock { get { return Ntsc ? NtscClock : PalClock; } }
    }

    public sealed class Track
    {
        public int Song;
        public string Title = "";
        public double Seconds = DefaultSeconds, Fade = DefaultFade;
        public bool Loop;
    }

    public static Header ReadHeader(byte[] d)
    {
        if (d.Length < 7 || Encoding.ASCII.GetString(d, 0, 5) != "SAP\r\n") throw new InvalidDataException("SAP 파일이 아닙니다 (머리말 SAP가 없음).");
        var h = new Header();
        int p = 5;
        while (p + 1 < d.Length && !(d[p] == 0xFF && d[p + 1] == 0xFF))
        {
            int end = p;
            while (end < d.Length && d[end] != 0x0D && d[end] != 0x0A) end++;
            string line = KoreanText.Decode(d, p, end - p).Trim();
            p = end;
            while (p < d.Length && (d[p] == 0x0D || d[p] == 0x0A)) p++;
            if (line.Length == 0) continue;
            int sp = line.IndexOf(' ');
            string tag = (sp < 0 ? line : line.Substring(0, sp)).ToUpperInvariant();
            string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            switch (tag)
            {
                case "AUTHOR": h.Author = Quoted(arg); break;
                case "NAME": h.Name = Quoted(arg); break;
                case "DATE": h.Date = Quoted(arg); break;
                case "SONGS": if (int.TryParse(arg, out int n) && n > 0) h.Songs = Math.Min(n, 32); break;
                case "DEFSONG": if (int.TryParse(arg, out int ds) && ds >= 0) h.DefSong = ds; break;
                case "TYPE": if (arg.Length > 0) h.Type = char.ToUpperInvariant(arg[0]); break;
                case "INIT": h.Init = Hex(arg); break;
                case "MUSIC": h.Music = Hex(arg); break;
                case "PLAYER": h.Player = Hex(arg); break;
                case "FASTPLAY": if (int.TryParse(arg, out int fp) && fp > 0) h.FastPlay = fp; break;
                case "STEREO": h.Stereo = true; break;
                case "NTSC": h.Ntsc = true; break;
                case "TIME":
                {
                    bool loop = arg.ToUpperInvariant().Contains("LOOP");
                    string t = arg.Split(' ')[0];
                    h.Times.Add(ParseTime(t));
                    h.Loops.Add(loop);
                    break;
                }
            }
        }
        if (p + 1 >= d.Length) throw new InvalidDataException("SAP 몸통이 없습니다 ($FF $FF가 없음).");
        h.BodyAt = p + 2;
        if ("BCDS".IndexOf(h.Type) < 0) throw new InvalidDataException("SAP TYPE " + h.Type + "는 아직 재생하지 않습니다 (B · C · D · S).");
        if (h.Type == 'B' && (h.Init < 0 || h.Player < 0)) throw new InvalidDataException("SAP TYPE B에 INIT · PLAYER가 없습니다.");
        if (h.Type == 'C' && (h.Music < 0 || h.Player < 0)) throw new InvalidDataException("SAP TYPE C에 MUSIC · PLAYER가 없습니다.");
        if ((h.Type == 'D' || h.Type == 'S') && h.Init < 0) throw new InvalidDataException("SAP TYPE " + h.Type + "에 INIT이 없습니다.");
        return h;
    }

    private static string Quoted(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);
        return s == "<?>" ? "" : s.Trim();
    }

    private static int Hex(string s)
    {
        return int.TryParse(s.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) && v >= 0 && v <= 0xFFFF ? v : -1;
    }

    /// <summary>"mm:ss.xxx" / "ss.xxx" → 초 (못 읽으면 0).</summary>
    public static double ParseTime(string t)
    {
        double sec = 0;
        foreach (var part in t.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return 0;
            sec = sec * 60 + v;
        }
        return sec;
    }

    // ------------------------------------------------------------ 곡 목록 · 열기

    public static List<Track> Tracks(Header h, string fallbackName)
    {
        var list = new List<Track>();
        string name = h.Name.Length > 0 ? h.Name : fallbackName;
        for (int s = 0; s < h.Songs; s++)
        {
            var t = new Track { Song = s, Title = h.Songs > 1 ? name + " #" + (s + 1) : name };
            if (s < h.Times.Count && h.Times[s] > 0)
            {
                t.Loop = h.Loops[s];
                t.Seconds = h.Times[s];
                // LOOP가 없으면 곡이 거기서 끝난다 (페이드 짧게), 있으면 되풀이 앞에서 줄여 끝냄
                t.Fade = t.Loop ? DefaultFade : 0.5;
                if (t.Loop) t.Seconds += DefaultFade;
            }
            list.Add(t);
        }
        return list;
    }

    public static List<Track> Tracks(string path)
    {
        try { return Tracks(ReadHeader(File.ReadAllBytes(path)), Path.GetFileNameWithoutExtension(path)); }
        catch (Exception) { return new List<Track> { new Track() }; }
    }

    /// <summary>"파일.sap" 또는 "파일.sap::n" (n = 곡 목록 안 번호, 0부터).</summary>
    public static VgmSong Load(string path)
    {
        path = NezM3u.SplitTrack(path, out int track);
        var bytes = File.ReadAllBytes(path);
        var h = ReadHeader(bytes);
        var tracks = Tracks(h, Path.GetFileNameWithoutExtension(path));
        var t = tracks[Math.Max(0, Math.Min(track, tracks.Count - 1))];
        var v = Render(bytes, t.Song, t.Seconds, t.Fade);
        v.FilePath = path;
        v.TitleEn = t.Title;
        v.GameEn = h.Name;
        v.AuthorEn = h.Author;
        v.ReleaseDate = h.Date;
        v.SourceFormat = "SAP " + h.Type + (h.Stereo ? " 스테레오" : "") + (h.Ntsc ? " NTSC" : "") + " · 곡 " + (t.Song + 1) + " (" + (track + 1) + "/" + tracks.Count + ")";
        return v;
    }

    // ------------------------------------------------------------ 돌리기

    /// <summary>쉬는 곳: JMP 자기 자신 (B · C 루틴은 여기로 돌아온다).</summary>
    private const int Idle = 0xD3F0;
    /// <summary>D형 인터럽트 틀 (PHP PHA TXA PHA TYA PHA JSR player PLA TAY PLA TAX PLA RTI).</summary>
    private const int Stub = 0xD300;

    private sealed class Machine : ICpu6502Bus
    {
        public readonly byte[] Ram = new byte[0x10000];
        private readonly byte[] stub = new byte[0x100];
        public Cpu6502 Cpu;
        public double Clock;
        public bool Stereo, Ntsc;
        public long FrameStart;
        public int FrameCycles;
        public readonly List<(double t, byte[] cmd)> Log = new List<(double, byte[])>();
        public bool UsedPokey;
        private int rnd = 0x1FFFF;

        public Machine(int player)
        {
            stub[0xF0] = 0x4C; stub[0xF1] = Idle & 0xFF; stub[0xF2] = Idle >> 8;   // JMP Idle
            byte[] irq = { 0x08, 0x48, 0x8A, 0x48, 0x98, 0x48, 0x20, (byte)player, (byte)(player >> 8), 0x68, 0xA8, 0x68, 0xAA, 0x68, 0x40 };
            irq.CopyTo(stub, 0);
        }

        private int FrameCycle { get { return (int)(Cpu.Cycles - FrameStart); } }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if ((a & 0xFF00) == 0xD300) return stub[a & 0xFF];
            if ((a & 0xFF00) == 0xD200)
            {
                switch (a & 0x0F)
                {
                    case 0x0A:   // RANDOM (poly17 흉내)
                        for (int i = 0; i < 8; i++) { int bit = ((rnd >> 16) ^ (rnd >> 11)) & 1; rnd = ((rnd << 1) | bit) & 0x1FFFF; }
                        return (byte)rnd;
                    case 0x0E: return 0xFF;   // IRQST: 걸린 인터럽트 없음
                    case 0x0C: case 0x0F: return 0xFF;
                }
                return Ram[a];
            }
            if ((a & 0xFF1F) == 0xD014) return (byte)(Ntsc ? 0x0F : 0x01);   // GTIA PAL
            if ((a & 0xFF1F) == 0xD01F) return 0x07;                          // CONSOL: 키 안 누름
            if ((a & 0xFF0F) == 0xD40B) { int c = FrameCycle; return (byte)(c >= FrameCycles ? 0 : c / (LineCycles * 2)); }   // VCOUNT
            if ((a & 0xFF0F) == 0xD40F) return (byte)(FrameCycle < FrameCycles - 248 * LineCycles / 10 ? 0x1F : 0x5F);   // NMIST (수직 귀선 무렵)
            return Ram[a];
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if ((a & 0xFF00) == 0xD300) return;
            Ram[a] = v;
            if ((a & 0xFF00) == 0xD200)
            {
                int reg = a & 0x1F;
                bool second = Stereo && reg >= 0x10;
                reg &= 0x0F;
                if (reg > 0x09 && reg != 0x0F) return;   // 쓰기 레지스터만 (AUDF/AUDC 1~4 · AUDCTL · STIMER · SKCTL)
                Log.Add((Cpu.Cycles * VgmSong.Rate / Clock, new byte[] { 0xBB, (byte)(reg | (second ? 0x80 : 0)), v }));
                if (reg < 8 && (reg & 1) == 1 && (v & 0x0F) != 0) UsedPokey = true;
                if (reg < 8 && (reg & 1) == 0 && v != 0) UsedPokey = true;
            }
        }
    }

    /// <summary>
    /// 블록을 RAM에 싣는다 (시작 · 끝 주소, 블록 사이에 $FF $FF가 다시 올 수 있음).
    /// </summary>
    private static void LoadBlocks(byte[] d, int p, byte[] ram)
    {
        while (p + 4 <= d.Length)
        {
            if (d[p] == 0xFF && d[p + 1] == 0xFF) { p += 2; continue; }
            int start = d[p] | (d[p + 1] << 8), end = d[p + 2] | (d[p + 3] << 8);
            p += 4;
            if (end < start) break;
            int len = Math.Min(end - start + 1, d.Length - p);
            if (len <= 0) break;
            Array.Copy(d, p, ram, start, len);
            p += len;
        }
    }

    /// <summary>곡 하나(song = 0부터)를 seconds초 돌려 VGM으로.</summary>
    public static VgmSong Render(byte[] file, int song, double seconds, double fade = DefaultFade)
    {
        var h = ReadHeader(file);
        var m = new Machine(h.Player < 0 ? 0 : h.Player);
        LoadBlocks(file, h.BodyAt, m.Ram);
        var cpu = new Cpu6502(m, Cpu6502.Variant.Nes2A03) { DecimalMode = true };
        m.Cpu = cpu;
        m.Clock = h.Clock;
        m.Stereo = h.Stereo;
        m.Ntsc = h.Ntsc;
        m.FrameCycles = h.Lines * LineCycles;

        // POKEY 처음 상태: SKCTL = 3 (돌아감), 음량 0
        for (int c = 0; c < (h.Stereo ? 2 : 1); c++)
        {
            int b = 0xD200 + c * 0x10;
            for (int r = 0; r < 9; r++) m.Write(b + r, 0);
            m.Write(b + 0x0F, 3);
        }

        long limit = (long)(m.Clock * 2);
        switch (h.Type)
        {
            case 'B':
                Call(cpu, m, h.Init, song, 0, 0, limit);
                break;
            case 'C':
                Call(cpu, m, h.Player + 3, 0x70, h.Music & 0xFF, h.Music >> 8, limit);
                Call(cpu, m, h.Player + 3, 0x00, song, 0, limit);
                break;
            case 'D':
            case 'S':
                // INIT을 띄워 둔다: 돌아오면 쉬는 곳에서 돈다
                cpu.S = 0xFF;
                int ret = Idle - 1;
                cpu.Push(ret >> 8); cpu.Push(ret & 0xFF);
                cpu.A = song; cpu.X = 0; cpu.Y = 0;
                cpu.PC = h.Init;
                break;
        }

        long playPeriod = (long)h.PlayLines * LineCycles;
        long totalCycles = (long)(seconds * m.Clock);
        long nextPlay = cpu.Cycles;
        long frameEnd = cpu.Cycles;
        bool inIrq = false;
        int irqPc = 0, irqS = 0;
        while (cpu.Cycles < totalCycles && !cpu.Jammed)
        {
            // 다음 사건까지 CPU를 돌림 (화면 시작 · 재생 차례)
            if (cpu.Cycles >= frameEnd) { m.FrameStart = frameEnd; frameEnd += m.FrameCycles; }
            if (cpu.Cycles >= nextPlay)
            {
                nextPlay += playPeriod;
                switch (h.Type)
                {
                    case 'B':
                    case 'C':
                        if (cpu.PC >= Idle && cpu.PC < Idle + 3)
                        {
                            int r = Idle - 1;
                            cpu.S = 0xFF;
                            cpu.Push(r >> 8); cpu.Push(r & 0xFF);
                            cpu.PC = h.Type == 'B' ? h.Player : h.Player + 6;
                        }
                        break;
                    case 'D':
                        // 앞 차례 PLAYER가 아직 안 끝났으면 건너뜀 (겹쳐 부르지 않는다)
                        if (h.Player >= 0 && !inIrq)
                        {
                            irqPc = cpu.PC; irqS = cpu.S; inIrq = true;
                            cpu.Push(cpu.PC >> 8); cpu.Push(cpu.PC & 0xFF);
                            cpu.PC = Stub;
                        }
                        break;
                    case 'S':
                    {
                        int i = (m.Ram[0x45] - 1) & 0xFF;
                        m.Ram[0x45] = (byte)i;
                        if (i == 0) m.Ram[0xB07B] = (byte)(m.Ram[0xB07B] + 1);
                        break;
                    }
                }
            }
            long until = Math.Min(Math.Min(nextPlay, frameEnd), totalCycles);
            if (cpu.PC >= Idle && cpu.PC < Idle + 3) { cpu.Cycles = Math.Max(cpu.Cycles, until); continue; }   // 쉬는 중: 건너뜀
            while (cpu.Cycles < until && !cpu.Jammed)
            {
                cpu.Step();
                if (inIrq && cpu.PC == irqPc && cpu.S == irqS) inIrq = false;   // RTI로 돌아옴
            }
        }

        var w = new VgmWriter { System = "Atari 8-bit (SAP)", Notes = "SAP", Title = h.Name, Author = h.Author, Date = h.Date };
        w.SetClock(VgmWriter.ClockPokey, m.Clock, h.Stereo ? 0x40000000u : 0);
        double total = seconds * VgmSong.Rate;
        long at = 0;
        foreach (var (time, cmd) in m.Log)
        {
            long ts = (long)Math.Min(total, Math.Max(0, time));
            if (ts > at) { w.Wait(ts - at); at = ts; }
            w.Write(cmd);
        }
        if ((long)total > at) w.Wait((long)total - at);
        var v = VgmFormat.Read(w.ToArray());
        v.PokeyStereo = h.Stereo;
        v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
        if (!m.UsedPokey) v.UnsupportedChips.Add("(칩 쓰기 없음)");
        if (cpu.UnknownOps > 0 || cpu.Jammed) v.UnsupportedChips.Add("(6502 알 수 없는 명령 " + cpu.UnknownOps + (cpu.Jammed ? ", 멈춤" : "") + ")");
        return v;
    }

    /// <summary>addr를 불러 쉬는 곳으로 돌아오거나 한도까지 돌린다.</summary>
    private static void Call(Cpu6502 cpu, Machine m, int addr, int a, int x, int y, long maxCycles)
    {
        cpu.A = a & 0xFF; cpu.X = x & 0xFF; cpu.Y = y & 0xFF;
        cpu.S = 0xFF;
        int ret = Idle - 1;
        cpu.Push(ret >> 8); cpu.Push(ret & 0xFF);
        cpu.PC = addr;
        long end = cpu.Cycles + maxCycles;
        while (cpu.Cycles < end && !(cpu.PC >= Idle && cpu.PC < Idle + 3) && !cpu.Jammed)
        {
            if (cpu.Cycles - m.FrameStart >= m.FrameCycles) m.FrameStart += m.FrameCycles;
            cpu.Step();
        }
    }
}
