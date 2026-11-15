using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio.Dos;

/// <summary>
/// hoot pc98dos 항목 (<see cref="HootArchive.Extension"/> 파일 = 항목 XML 하나, 같은 폴더 = zip을 푼 내용) → 곡.
/// <para>곡 목록 = titlelist의 &lt;title code&gt; (range = 효과음은 뺌), "파일::n" = n번째 제목.
/// 재생: <see cref="Pc98Chip"/>가 PC-98 기계를 스스로 돌린다 (VGM에는 길이만). 길이를 알 수 없어 <see cref="DefaultSeconds"/> + 페이드.</para>
/// <para>항목 → 기계 (<see cref="Boot"/>): driver type opn = 26K (YM2203) · opna · 86 = 86 보드 (YM2608) · beep = 보드 없음,
/// 옵션 clockmul (기본 8) · funcvect (기본 7Fh), rom 줄을 차례로: file (offset ≥ 0 = 핸들에 묶기) · conin (글자를 핸들에) · device (config.sys device=)
/// · code · binary (파일을 주소에) · string (글자를 주소에) · shell (명령 줄).</para>
/// </summary>
public static class HootPc98
{
    public static readonly string[] Extensions = { HootArchive.Extension };
    public const double DefaultSeconds = 180, DefaultFade = 8;
    /// <summary>셸 한 줄에 줄 CPU 클럭 (끝나거나 상주하지 않으면 그 자리에서 다음으로 = 계속 도는 재생기).</summary>
    public const long ShellCycles = 40_000_000;
    /// <summary>진단: 세우는 동안부터 포트 · 인터럽트 · BIOS / DOS 호출을 센다 (<see cref="Pc98Machine.Trace"/>).</summary>
    public static bool TraceBoot;

    public static List<(int Code, string Title)> Tracks(string path)
    {
        var e = HootArchive.Load(path);
        return e.Titles.ToList();
    }

    public static Pc98Machine.Board BoardOf(HootArchive.Entry e)
    {
        switch (e.Type.ToLowerInvariant())
        {
            case "opn": return Pc98Machine.Board.Opn;
            case "opna": case "86": return Pc98Machine.Board.Opna;
            default: return Pc98Machine.Board.None;
        }
    }

    /// <summary>"항목.hoot::n" → 곡 (길이만 든 VGM + 스스로 도는 PC-98 칩).</summary>
    public static VgmSong Load(string path)
    {
        string file = ChipSongs.FilePart(path);
        NezM3u.SplitTrack(path, out int n);
        var e = HootArchive.Load(file);
        if (!HootArchive.Playable(e.Driver, e.Type)) throw new InvalidDataException("흉내 낼 수 없는 hoot 항목입니다: " + e.Driver + " / " + e.Type);
        if (e.Titles.Count == 0) throw new InvalidDataException("hoot 항목에 곡 목록(titlelist)이 없습니다.");
        n = Math.Max(0, Math.Min(n, e.Titles.Count - 1));
        var (code, title) = e.Titles[n];
        string dir = Path.GetDirectoryName(Path.GetFullPath(file));
        var board = BoardOf(e);
        var w = new VgmWriter { Title = title, Game = e.Name, System = "PC-9801" };
        w.Wait((long)Math.Round((DefaultSeconds + DefaultFade) * VgmSong.Rate));
        var v = VgmFormat.Read(w.ToArray());
        v.FilePath = path;
        v.FadeSeconds = DefaultFade;
        v.SelfRunningName = "PC-98 (" + (board == Pc98Machine.Board.Opn ? "YM2203" : board == Pc98Machine.Board.Opna ? "YM2608" : "BEEP") + ")";
        // 기계 세우기 (드라이버 상주 · 초기화, 0.2~1초)는 미리 뒤에서: 처음 소리 내는 칩이 이것을 받는다 (오디오 스레드에서 기다리지 않게).
        // 그 뒤의 칩 (위치 옮기기 등)은 새로 세운다
        var pre = System.Threading.Tasks.Task.Run(() => BootDir(dir, e, out _));
        int taken = 0;
        v.SelfRunning = () => new Pc98Chip(() => System.Threading.Interlocked.Exchange(ref taken, 1) == 0 ? pre.GetAwaiter().GetResult() : BootDir(dir, e, out _), board, code);
        v.SourceFormat = "hoot pc98dos (" + e.Type + ", 곡 코드 0x" + code.ToString("X") + ", 길이 " + DefaultSeconds + "초 고정)";
        return v;
    }

    /// <summary>항목대로 기계를 세우고 셸을 돌린다 (재생 요청 전까지). 결과 = 셸마다 결과.</summary>
    public static Pc98Machine Boot(DosFileSystem fs, HootArchive.Entry e) { return Boot(fs, e, out _); }

    /// <summary>zip을 푼 폴더의 항목으로 세우기: 파일 = 원래 zip (Shift_JIS 이름), 없는 셸 프로그램 = 같은 폴더의 다른 zip (<see cref="SharedPrograms"/>).</summary>
    public static Pc98Machine BootDir(string dir, HootArchive.Entry e, out List<DosMachine.ShellResult> shells)
    {
        string zip = ZipFolder.SourceZip(dir);
        return Boot(DosFileSystem.ForHootDir(dir), e, out shells, zip != null ? Path.GetDirectoryName(zip) : null, zip);
    }

    /// <summary>끝나지 않은 셸 뒤에 셸이 더 있으면 (드라이버 초기화가 오래 걸림) 이만큼 더 기다린다.</summary>
    public const long MoreShellCycles = 400_000_000;

    public static Pc98Machine Boot(DosFileSystem fs, HootArchive.Entry e, out List<DosMachine.ShellResult> shells, string library = null, string ownZip = null)
    {
        var m = new Pc98Machine(fs, BoardOf(e), Math.Max(1, e.Option("clockmul", 8))) { FuncVector = e.Option("funcvect", 0x7F) & 0xFF, Trace = TraceBoot };
        shells = new List<DosMachine.ShellResult>();
        var sjis = KoreanText.ShiftJis;
        foreach (var (type, off, text) in e.Roms)
        {
            switch (type)
            {
                case "file":
                    if (off >= 0) m.BindFile((int)off, text);
                    break;
                case "conin":
                    m.BindText((int)Math.Max(0, off), sjis.GetBytes(text));
                    break;
                case "device":
                    m.LoadDevice(text);
                    if (m.HostReturned) m.Idle();
                    break;
                case "code": case "binary":
                    if (off >= 0 && fs.TryGet(text, out var d)) Array.Copy(d, 0, m.Mem, (int)off, (int)Math.Min(d.Length, m.Mem.Length - off));
                    break;
                case "string":
                    if (off >= 0) { var b = sjis.GetBytes(text); Array.Copy(b, 0, m.Mem, (int)off, (int)Math.Min(b.Length, m.Mem.Length - off)); }
                    break;
                case "shell":
                    {
                        // 이 zip에 없는 프로그램 (여러 게임이 같이 쓰는 드라이버): 같은 폴더의 다른 zip에서
                        string prog = text.Trim().Split(new[] { ' ', '\t', '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                        if (library != null && prog.Length > 0 && m.ResolveProgram(prog) == null)
                        {
                            var shared = SharedPrograms.Find(library, prog, ownZip);
                            if (shared != null) fs.Add(shared.Value.name, shared.Value.data);
                        }
                        var r = m.Shell(text, ShellCycles);
                        // 뒤에 셸이 더 있는데 아직 도는 중: 상주 · 끝날 때까지 조금 더
                        if (r == DosMachine.ShellResult.Running && e.Roms.Skip(e.Roms.IndexOf((type, off, text)) + 1).Any(x => x.Type == "shell"))
                        {
                            for (long spent = 0; spent < MoreShellCycles && !m.HostReturned; spent += 20_000_000) m.Run(20_000_000);
                            if (m.HostReturned) r = (m.ReturnCode >> 8) == 3 ? DosMachine.ShellResult.Resident : DosMachine.ShellResult.Exited;
                        }
                        shells.Add(r);
                        if (m.HostReturned || r == DosMachine.ShellResult.NotFound || r == DosMachine.ShellResult.NoMemory) m.Idle();
                        break;
                    }
            }
        }
        if (m.HostReturned) m.Idle();
        m.AutoIdle = true;
        return m;
    }

    /// <summary>항목 · 곡 하나를 seconds초 돌려 칩 쓰기 VGM으로 (내보내기 · 시험). 처음 칩 설정(재생 전 쓰기)은 앞에 기다림 없이.</summary>
    public static byte[] ToVgm(string hootPath, int index, double seconds, out Pc98Machine machine)
    {
        var e = HootArchive.Load(hootPath);
        var (code, title) = e.Titles[Math.Max(0, Math.Min(index, e.Titles.Count - 1))];
        machine = BootDir(Path.GetDirectoryName(Path.GetFullPath(hootPath)), e, out _);
        long t0 = machine.Cpu.Cycles;
        machine.Play(code);
        machine.Run((long)(machine.CpuHz * seconds));
        return machine.ToVgm(t0, seconds, title);
    }
}

/// <summary>
/// PC-98 기계를 품은 칩: 샘플마다 기계를 그만큼 돌리고 나온 칩 쓰기를 OPN / OPNA · BEEP에 넣는다.
/// 기계는 처음 소리를 낼 때 세운다 (곡 목록 · 믹서 채널만 볼 때는 세우지 않음).
/// </summary>
public sealed class Pc98Chip : ISoundChip, ISelfRunningChip
{
    private readonly Func<Pc98Machine> boot;
    private readonly int code;
    private readonly OpnChip opn;
    private readonly Beeper beep;
    private Pc98Machine m;
    private double cyclesPerSample, sampleCycle;
    private int logPos, used, sinceScan;
    private bool failed;
    private readonly bool[] muted;

    public Pc98Chip(Func<Pc98Machine> boot, Pc98Machine.Board board, int code)
    {
        this.boot = boot;
        this.code = code;
        if (board == Pc98Machine.Board.Opn) opn = new OpnChip(OpnChip.Variant.Ym2203, Pc98Machine.OpnBaseClock);
        else if (board == Pc98Machine.Board.Opna) opn = new OpnChip(OpnChip.Variant.Ym2608, Pc98Machine.OpnBaseClock * 2);
        NativeRate = opn != null ? opn.NativeRate : 44100;
        beep = new Beeper(Beeper.Pc98Clock, NativeRate);
        muted = new bool[ChannelCount];
    }

    /// <summary>세운 기계 (아직이면 null) — 시험 · 진단용.</summary>
    public Pc98Machine Machine { get { return m; } }
    public string Error { get; private set; } = "";

    public string Name { get { return "PC-98 DOS (" + (opn != null ? opn.Name + " + " : "") + "BEEP)"; } }
    public int NativeRate { get; }
    private int FmCount { get { return opn != null ? opn.ChannelCount : 0; } }
    public int ChannelCount { get { return FmCount + 1; } }
    public string ChannelLabel(int ch) { return ch < FmCount ? opn.ChannelLabel(ch) : "BEEP"; }

    public void Reset() { }
    public void Write(int port, int reg, int value) { }

    private bool Ensure()
    {
        if (m != null) return true;
        if (failed) return false;
        try
        {
            m = boot();
            cyclesPerSample = m.CpuHz / NativeRate;
            logPos = m.Log.Count;
            // 재생 전 쓰기 (드라이버 초기화)는 칩에 바로
            foreach (var e in m.Log) Apply(e);
            m.Log.Clear(); logPos = 0;
            sampleCycle = m.Cpu.Cycles;
            m.Play(code);
            return true;
        }
        catch (Exception ex) { failed = true; Error = ex.Message; return false; }
    }

    private void Apply((long t, int kind, int reg, int val) e)
    {
        switch (e.kind)
        {
            case 0: case 1: opn?.Write(e.kind, e.reg, e.val); break;
            case 2: beep.Write(0, 2, e.val); if (e.val != 0) used |= 1 << FmCount; break;
            case 3: beep.Write(0, 0, e.val & 0xFF); beep.Write(0, 1, (e.val >> 8) & 0xFF); break;
        }
    }

    /// <summary>한 샘플만큼 기계를 돌리고 그때까지의 쓰기를 칩에.</summary>
    private void Advance()
    {
        sampleCycle += cyclesPerSample;
        long now = (long)sampleCycle;
        while (m.Cpu.Cycles < now)
        {
            long before = m.Cpu.Cycles;
            m.Run(Math.Max(1, (long)(cyclesPerSample * 64)));
            if (m.Cpu.Cycles == before) { m.Cpu.Cycles = now; break; }   // 멈춘 기계 (바깥으로 돌아옴 등)
        }
        var log = m.Log;
        while (logPos < log.Count && log[logPos].t <= now) Apply(log[logPos++]);
        if (logPos > 8192) { log.RemoveRange(0, logPos); logPos = 0; }
        if (++sinceScan >= 256 && opn != null)
        {
            sinceScan = 0;
            for (int i = 0; i < FmCount; i++) if (opn.ChannelLevel(i) > 0.01f || opn.ChannelActive(i)) used |= 1 << i;
        }
    }

    public void Generate(out int left, out int right)
    {
        if (!Ensure()) { left = right = 0; return; }
        Advance();
        int l = 0, r = 0;
        if (opn != null) opn.Generate(out l, out r);
        if (!muted[ChannelCount - 1]) { beep.Generate(out int bl, out int br); l += bl / 2; r += br / 2; }
        left = l; right = r;
    }

    public void Skip(double seconds)
    {
        if (!Ensure()) return;
        long n = (long)(seconds * NativeRate);
        for (long i = 0; i < n; i++) Advance();
    }

    public int VoicesUsed { get { return used; } }

    public void SetMute(int ch, bool mute)
    {
        if (ch < 0 || ch >= ChannelCount) return;
        muted[ch] = mute;
        if (ch < FmCount) opn.SetMute(ch, mute);
    }

    public void SetPitch(int ch, double ratio) { if (ch < FmCount) opn.SetPitch(ch, ratio); else beep.SetPitch(0, ratio); }
    public double ChannelFrequency(int ch) { return ch < FmCount ? opn.ChannelFrequency(ch) : beep.ChannelFrequency(0); }
    public float ChannelLevel(int ch) { return ch < FmCount ? opn.ChannelLevel(ch) : beep.ChannelLevel(0); }
    public bool ChannelActive(int ch) { return ch < FmCount ? opn.ChannelActive(ch) : beep.ChannelActive(0); }
}
