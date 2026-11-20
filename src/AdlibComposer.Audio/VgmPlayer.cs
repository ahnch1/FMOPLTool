using System;
using System.Collections.Generic;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// VGM 재생기: 기록된 레지스터 쓰기를 시간에 맞춰 칩에 보낸다. PSG(<see cref="PsgEmulator"/>) · SCC(<see cref="SccEmulator"/>) ·
/// OPL(<see cref="OplChip"/>, OPL2 또는 OPL3)을 함께 돌려 섞는다 (44100Hz, VGM의 시간 단위 그대로).
/// <para>Jukebox 조절: 속도 = 기다림을 빨리/느리게 (칩 소리는 그대로), 키 · 채널 음높이 = 칩에 보내는 분주 · F-number를 바꿔 쓴다,
/// 채널 끄기 = PSG · SCC는 에뮬레이터의 Mute, OPL은 키온 비트(리듬 모드는 BD의 드럼 비트)를 지운다.</para>
/// <para>되돌아가는 곡(루프)은 한 번 더 돌고 마지막 몇 초를 줄여 끝낸다. 위치 단위 = ms. 처음이 아닌 곳에서 시작하면
/// 그곳까지 명령만 빨리 실행한다 (소리는 만들지 않음).</para>
/// <para>그 밖의 칩(SN76489 · YM2413 · YM2612 · YM2151 · YM2203 · YM2608 · OKIM6295)은 <see cref="ISoundChip"/> 공통 경로로 낸다:
/// 조옮김 · 끄기는 칩의 SetPitch · SetMute, 표시는 칩에 물어본다.</para>
/// <para>DAC 스트림 (0x90~0x95): 데이터 블록(0x00~0x3F)의 바이트를 정한 속도로 칩 레지스터에 쓴다 (YM2612 DAC · OKIM6258 …).</para>
/// <para>둘째 칩(듀얼 칩) 명령과 모르는 칩 명령은 건너뛴다.</para>
/// </summary>
public sealed class VgmPlayer
{
    public const int Rate = VgmSong.Rate;
    /// <summary>루프 곡 끝에서 줄여 가는 시간 (초).</summary>
    public const double FadeSeconds = 4.0;

    public enum ChipKind { Psg, Scc, Fm, Other }

    /// <summary>믹서 한 줄 = 칩의 채널 하나.</summary>
    public struct Channel
    {
        public ChipKind Chip;
        public int Index;
        public string Label;
        /// <summary>Other: 공통 칩 목록 안 번호.</summary>
        public int Extra;
    }

    /// <summary>공통 경로로 내는 칩 하나 (칩 속도 → 44100 선형 보간).</summary>
    private sealed class ExtraChip
    {
        public ISoundChip Chip;
        public int Base;
        public double Frac, Gain = 1.0;
        public int PL, PR, CL, CR;
    }

    private readonly List<ExtraChip> extras = new List<ExtraChip>();
    private ExtraChip xSn2, xOki2, xUpd, xQs, xSpcm, xOpn2203b, xPokey, xCus30;
    private ExtraChip xSn, xOpll, xOpn2, xOpm, xOpn, xOpna, xOpnb, xOki, xNes, xPce, xNsfx, xMsm, xSaa, xGb;
    /// <summary>YM2612 PCM 데이터 뱅크 (블록 0x00) · 읽는 위치.</summary>
    private byte[] pcmBank = new byte[0];
    private int pcmLen, pcmPos;

    /// <summary>스트림용 데이터 뱅크 (블록 종류 0x00~0x3F): 이어 붙인 바이트 + 블록마다 시작 위치.</summary>
    private sealed class DataBank
    {
        public byte[] Data = new byte[0];
        public int Length;
        public readonly List<int> Starts = new List<int>();
        public void Add(byte[] d, int at, int n)
        {
            if (Length + n > Data.Length) Array.Resize(ref Data, Math.Max(Length + n, Data.Length * 2));
            Array.Copy(d, at, Data, Length, n);
            Starts.Add(Length);
            Length += n;
        }
    }

    /// <summary>DAC 스트림 (VGM 0x90~0x95): 데이터 뱅크의 바이트를 정한 속도로 칩 레지스터에 쓴다.</summary>
    private sealed class DacStream
    {
        public int ChipType = -1, Port, Reg;
        public int BankType = -1, StepSize = 1, StepBase;
        public double Frequency;
        public bool Running, Loop, Reverse;
        public int Start, Pos, Left, Total;
        public double Next;   // 다음 쓰기 시간 (VGM 샘플)
    }

    private readonly Dictionary<int, DataBank> banks = new Dictionary<int, DataBank>();
    private readonly Dictionary<int, DacStream> streams = new Dictionary<int, DacStream>();
    private bool anyStream;

    /// <summary>곡이 쓰는 PSG · SCC · OPL 말고 다른 칩을 만든다 (채널 목록 순서).</summary>
    private static List<ISoundChip> CreateOtherChips(VgmSong s)
    {
        var l = new List<ISoundChip>();
        if (s.SpcImage != null) l.Add(new SnesSpc(s.SpcImage));
        if (s.SelfRunning != null && s.SelfRunning() is ISoundChip own) l.Add(own);
        if (s.Sn76489Clock > 0) l.Add(new Sn76489(s.Sn76489Clock, Rate, s.SnFeedback, s.SnWidth));
        if (s.Sn76489Clock > 0 && s.Sn76489Clock2 > 0) l.Add(new Sn76489(s.Sn76489Clock2, Rate, s.SnFeedback, s.SnWidth));
        if (s.Ym2413Clock > 0) l.Add(new Ym2413(s.Ym2413Clock, s.Vrc7));
        if (s.Ym2612Clock > 0) l.Add(new OpnChip(OpnChip.Variant.Ym2612, s.Ym2612Clock));
        if (s.Ym2151Clock > 0) l.Add(new Ym2151(s.Ym2151Clock));
        if (s.Ym2203Clock > 0) l.Add(new OpnChip(OpnChip.Variant.Ym2203, s.Ym2203Clock));
        if (s.Ym2203Clock > 0 && s.Ym2203Clock2 > 0) l.Add(new OpnChip(OpnChip.Variant.Ym2203, s.Ym2203Clock2));   // 둘째 YM2203 (명령 A5h)
        if (s.Ym2608Clock > 0) l.Add(new OpnChip(OpnChip.Variant.Ym2608, s.Ym2608Clock));
        if (s.Ym2610Clock > 0) l.Add(new OpnChip(s.Ym2610B ? OpnChip.Variant.Ym2610B : OpnChip.Variant.Ym2610, s.Ym2610Clock));
        if (s.Okim6295Clock > 0) l.Add(new Okim6295(s.Okim6295Clock, s.OkiPin7, Rate));
        if (s.Okim6295Clock > 0 && s.Okim6295Clock2 > 0) l.Add(new Okim6295(s.Okim6295Clock2, s.OkiPin7, Rate));
        if (s.Upd7759Clock > 0) l.Add(new Upd7759(s.Upd7759Clock, Rate));
        if (s.QsoundClock > 0) l.Add(new QSound(s.QsoundClock, Rate));
        if (s.SegaPcmClock > 0) l.Add(new SegaPcm(s.SegaPcmClock, s.SegaPcmInterface, Rate));
        if (s.NesApuClock > 0) l.Add(new NesApu(s.NesApuClock, Rate, s.NesFds));
        if ((s.NsfChips & (NsfExpansion.Vrc6 | NsfExpansion.N163 | NsfExpansion.Mmc5)) != 0) l.Add(new NsfExpansion(s.NsfChips, s.NesApuClock > 0 ? s.NesApuClock : NesApu.NtscClock, Rate));
        if (s.Huc6280Clock > 0) l.Add(new Huc6280Psg(s.Huc6280Clock, Rate));
        if (s.Okim6258Clock > 0) l.Add(new Msm6258(s.Okim6258Clock, s.Okim6258Flags, Rate));
        if (s.Saa1099Clock > 0) l.Add(new Saa1099(s.Saa1099Clock, Rate));
        if (s.PokeyClock > 0) l.Add(new Pokey(s.PokeyClock, Rate));
        if (s.Cus30Clock > 0) l.Add(new NamcoCus30(s.Cus30Clock, Rate));
        if (s.GbDmgClock > 0) l.Add(new GbApu(s.GbDmgClock, Rate));
        return l;
    }

    private VgmSong song;
    private PsgEmulator psg;
    private SccEmulator scc;
    private OplChip opl;
    private bool opl3, y8950;
    private double fmClock;
    private readonly List<Channel> channels = new List<Channel>();
    private int psgBase = -1, sccBase = -1, fmBase = -1;

    // 칩에 쓴 원래 값 (조옮김 · 끄기 전)
    private readonly byte[] psgReg = new byte[16];
    private readonly int[] sccFreq = new int[SccEmulator.Channels];
    private readonly int[] sccVol = new int[SccEmulator.Channels];
    private int sccEnable;
    private readonly byte[] oplReg = new byte[512];

    // 시간
    private int pos;
    private double clock;     // 지나간 시간 (VGM 샘플, 루프 포함)
    private long due;         // 다음 명령을 실행할 시간
    private long playLength;
    private int loopsLeft;
    private bool playing;
    private int syncCountdown;

    // OPL 리샘플 (칩 고유 속도 → 44100)
    private double oplFrac;
    private int oplPrevL, oplPrevR, oplCurL, oplCurR;
    private double psgGain, sccGain, fmGain;

    // 채널 상태 (Jukebox 표시)
    private readonly int[] appliedSemis = new int[PlaybackOptions.Voices];
    private readonly bool[] appliedMute = new bool[PlaybackOptions.Voices];
    private readonly int[] note = new int[PlaybackOptions.Voices];
    private readonly float[] volume = new float[PlaybackOptions.Voices];
    private readonly int[] noteOns = new int[PlaybackOptions.Voices];
    private readonly bool[] everSounded = new bool[PlaybackOptions.Voices];

    public PlaybackOptions Options { get; private set; } = new PlaybackOptions();
    public VgmSong Song { get { return song; } }
    public bool IsPlaying { get { return playing; } }
    public double PositionMs { get { return clock * 1000.0 / Rate; } }
    public IReadOnlyList<Channel> Channels { get { return channels; } }

    /// <summary>재생 길이 (샘플): 루프가 있으면 한 번 더.</summary>
    public static long PlayLength(VgmSong s) { return s.TotalSamples + (s.LoopOffset > 0 ? s.LoopSamples : 0); }
    public static double PlaySeconds(VgmSong s) { return PlayLength(s) / (double)Rate; }

    /// <summary>곡이 쓰는 칩으로 만든 채널 목록 (PSG 3 → SCC 5 → FM 9 또는 18).</summary>
    public static List<Channel> ChannelsOf(VgmSong s)
    {
        var l = new List<Channel>();
        if (s.HasPsg) for (int i = 0; i < PsgEmulator.Channels; i++) l.Add(new Channel { Chip = ChipKind.Psg, Index = i, Label = ((char)('A' + i)).ToString() });
        if (s.HasScc) for (int i = 0; i < SccEmulator.Channels; i++) l.Add(new Channel { Chip = ChipKind.Scc, Index = i, Label = "S" + (i + 1) });
        if (s.HasOpl)
        {
            int n = s.IsOpl3 ? 18 : 9;
            for (int i = 0; i < n; i++) l.Add(new Channel { Chip = ChipKind.Fm, Index = i, Label = "F" + (i + 1) });
        }
        var others = CreateOtherChips(s);
        for (int k = 0; k < others.Count; k++)
            for (int i = 0; i < others[k].ChannelCount && l.Count < PlaybackOptions.Voices; i++)
                l.Add(new Channel { Chip = ChipKind.Other, Index = i, Extra = k, Label = others[k].ChannelLabel(i) });
        return l;
    }

    /// <summary>곡 끝까지 명령만 돌려 소리를 낸 적 있는 채널을 찾는다 (믹서 '사용' 표시).</summary>
    public static bool[] ScanUsed(VgmSong s)
    {
        if (s.SpcImage != null) return ScanSelfRunning(s);
        if (s.SelfRunning != null)
        {
            // 기종 흉내 (PC-98 DOS 등): 기계를 세우고 돌려 보는 데 몇 초 걸려 다 쓴다고 본다
            var all = new bool[PlaybackOptions.Voices];
            int n = ChannelsOf(s).Count;
            for (int i = 0; i < n && i < all.Length; i++) all[i] = true;
            return all;
        }
        var p = new VgmPlayer();
        p.Start(s, PlaySeconds(s) * 1000.0 + 1, null);
        var used = new bool[PlaybackOptions.Voices];
        Array.Copy(p.everSounded, used, used.Length);
        p.Stop();
        return used;
    }

    /// <summary>스스로 도는 칩(SPC): 곡 전체를 돌리면 오래 걸려 (실시간의 8배쯤) 앞 8초만 돌려 소리 낸 보이스를 본다.</summary>
    private static bool[] ScanSelfRunning(VgmSong s)
    {
        var used = new bool[PlaybackOptions.Voices];
        var chips = CreateOtherChips(s);
        int at = 0;
        foreach (var c in chips)
        {
            if (c is ISelfRunningChip sr)
            {
                sr.Skip(Math.Min(8, PlaySeconds(s)));
                for (int i = 0; i < c.ChannelCount && at + i < used.Length; i++) used[at + i] = (sr.VoicesUsed & (1 << i)) != 0;
            }
            at += c.ChannelCount;
        }
        return used;
    }

    // ================================================================ 시작 / 멈춤

    /// <param name="startMs">시작 위치 (ms).</param>
    public void Start(VgmSong s, double startMs, PlaybackOptions options)
    {
        song = s;
        Options = options ?? new PlaybackOptions();
        channels.Clear();
        channels.AddRange(ChannelsOf(s));
        psgBase = sccBase = fmBase = -1;
        for (int i = channels.Count - 1; i >= 0; i--)
        {
            if (channels[i].Chip == ChipKind.Psg) psgBase = i;
            else if (channels[i].Chip == ChipKind.Scc) sccBase = i;
            else fmBase = i;
        }

        psg = null; scc = null; opl = null;
        if (s.HasPsg)
        {
            double c = s.AyClock;
            if (s.AyIsYm && (s.AyFlags & 0x10) != 0) c /= 2;   // YM2149 핀 26 = 클럭 반
            psg = new PsgEmulator(c, Rate, s.AyIsYm);
        }
        if (s.HasScc) scc = new SccEmulator(s.SccClock, Rate, s.SccPlus);
        opl3 = s.IsOpl3;
        y8950 = !opl3 && s.Ym3812Clock <= 0 && s.Y8950Clock > 0;
        if (s.HasOpl)
        {
            opl = new OplChip(opl3);
            opl.Reset();
            fmClock = opl3 ? s.Ymf262Clock / 4 : s.Ym3812Clock > 0 ? s.Ym3812Clock : s.Y8950Clock > 0 ? s.Y8950Clock : s.Ym3526Clock;   // OPL3 = 14.318MHz (OPL2의 4배)
        }

        extras.Clear();
        xSn2 = null; xOki2 = null; xUpd = null; xQs = null; xSpcm = null; xOpn2203b = null; xPokey = null; xCus30 = null;
        xSn = xOpll = xOpn2 = xOpm = xOpn = xOpna = xOpnb = xOki = xNes = xPce = xNsfx = xMsm = xSaa = xGb = null;
        foreach (var chip in CreateOtherChips(s))
        {
            var x = new ExtraChip { Chip = chip, Base = -1 };
            for (int i = 0; i < channels.Count; i++) if (channels[i].Chip == ChipKind.Other && channels[i].Extra == extras.Count) { x.Base = i; break; }
            extras.Add(x);
            if (chip is Sn76489) { if (xSn == null) xSn = x; else xSn2 = x; }
            else if (chip is Ym2413) xOpll = x;
            else if (chip is Ym2151) xOpm = x;
            else if (chip is Okim6295) { if (xOki == null) xOki = x; else xOki2 = x; }
            else if (chip is Upd7759) xUpd = x;
            else if (chip is QSound) xQs = x;
            else if (chip is SegaPcm) xSpcm = x;
            else if (chip is NesApu) xNes = x;
            else if (chip is Huc6280Psg) xPce = x;
            else if (chip is NsfExpansion) xNsfx = x;
            else if (chip is Msm6258) xMsm = x;
            else if (chip is Saa1099) xSaa = x;
            else if (chip is Pokey) xPokey = x;
            else if (chip is NamcoCus30) xCus30 = x;
            else if (chip is GbApu) xGb = x;
            else if (chip is OpnChip o) { if (o.Type == OpnChip.Variant.Ym2612) xOpn2 = x; else if (o.Type == OpnChip.Variant.Ym2203) { if (xOpn == null) xOpn = x; else xOpn2203b = x; } else if (o.Type == OpnChip.Variant.Ym2608) xOpna = x; else xOpnb = x; }
        }
        pcmLen = 0; pcmPos = 0;
        banks.Clear(); streams.Clear(); anyStream = false;

        // 칩이 여럿이면 각각 줄여 합이 넘치지 않게
        int chips = (psg != null ? 1 : 0) + (scc != null ? 1 : 0) + (opl != null ? 1 : 0) + extras.Count;
        double share = chips <= 1 ? 1.0 : chips == 2 ? 0.7 : chips == 3 ? 0.58 : 0.5;
        foreach (var x in extras) x.Gain = share;
        psgGain = 0.75 * share;
        sccGain = 0.75 * share;
        fmGain = 1.0 * share;

        Array.Clear(psgReg, 0, psgReg.Length);
        psgReg[7] = 0x3F;
        Array.Clear(sccFreq, 0, sccFreq.Length);
        Array.Clear(sccVol, 0, sccVol.Length);
        sccEnable = 0;
        Array.Clear(oplReg, 0, oplReg.Length);
        for (int v = 0; v < note.Length; v++) { note[v] = -1; volume[v] = 0; appliedSemis[v] = 0; appliedMute[v] = false; everSounded[v] = false; }
        oplFrac = 0; oplPrevL = oplPrevR = oplCurL = oplCurR = 0;

        pos = s.DataOffset;
        clock = 0;
        due = 0;
        playLength = PlayLength(s);
        outLevel = 1;
        loopsLeft = s.LoopOffset > 0 ? 1 : 0;
        playing = pos < s.DataEnd;
        SyncAdjustments();

        // 시작 위치까지 명령만 빨리 (스스로 도는 칩은 그만큼 앞으로)
        double target = Math.Max(0, startMs) * Rate / 1000.0;
        while (playing && due <= target) { ExecuteOne(); if (anyStream) RunStreams(Math.Min(due, target)); }
        clock = Math.Min(target, playLength);
        if (clock > 0) foreach (var x in extras) if (x.Chip is ISelfRunningChip sr) sr.Skip(clock / Rate);
        if (clock >= playLength) playing = false;
    }

    /// <summary>
    /// 멈춤: 명령을 그만 실행하고 소리도 5ms에 걸쳐 줄여 끈다. 칩은 마지막에 받은 음을 계속 내므로
    /// (NES 사각파의 길이 정지 · PSG 고정 볼륨 · OPL 키온 등) 출력을 끄지 않으면 멈춘 뒤에도 그 음이 남는다.
    /// </summary>
    public void Stop() { playing = false; }

    /// <summary>출력 크기 (재생 중 1, 멈추면 0으로 짧게 줄인다: 딱 소리 없이).</summary>
    private double outLevel = 1;

    // ================================================================ 한 샘플

    /// <summary>44100Hz 한 칸: 이 시간까지 할 명령을 실행한다.</summary>
    public void Step()
    {
        if (!playing) return;
        if (--syncCountdown <= 0) { syncCountdown = 64; SyncAdjustments(); foreach (var x in extras) UpdateOther(x); }
        clock += Math.Max(0.05, Options.Speed);
        while (playing && due <= clock) { ExecuteOne(); if (anyStream) RunStreams(Math.Min(due, clock)); }
        if (anyStream) RunStreams(clock);
        if (clock >= playLength) playing = false;
    }

    /// <summary>칩 소리를 섞어 스테레오 샘플 하나 (16bit 범위 안팎).</summary>
    public void Generate(out double left, out double right)
    {
        // 멈추면 5ms(220샘플)에 걸쳐 0으로 곧게 줄인다 (딱 소리 없이, 끝은 정확히 0)
        outLevel = playing ? Math.Min(1.0, outLevel + 1.0 / 220) : Math.Max(0.0, outLevel - 1.0 / 220);
        if (!playing && outLevel <= 0) { left = right = 0; return; }   // 멈춘 뒤: 칩을 돌리지 않는다
        double mono = 0, l = 0, r = 0;
        if (psg != null) mono += psg.Generate() * psgGain;
        if (scc != null) mono += scc.Generate() * sccGain;
        if (opl != null)
        {
            oplFrac += opl.NativeRate / (double)Rate;
            while (oplFrac >= 1.0)
            {
                oplFrac -= 1.0;
                oplPrevL = oplCurL; oplPrevR = oplCurR;
                opl.GenerateStereo(out oplCurL, out oplCurR);
            }
            l = (oplPrevL + (oplCurL - oplPrevL) * oplFrac) * fmGain;
            r = (oplPrevR + (oplCurR - oplPrevR) * oplFrac) * fmGain;
        }
        foreach (var x in extras)
        {
            int nr = x.Chip.NativeRate;
            if (nr == Rate) { x.Chip.Generate(out int a, out int b); l += a * x.Gain; r += b * x.Gain; continue; }
            x.Frac += nr / (double)Rate;
            while (x.Frac >= 1.0)
            {
                x.Frac -= 1.0;
                x.PL = x.CL; x.PR = x.CR;
                x.Chip.Generate(out x.CL, out x.CR);
            }
            l += (x.PL + (x.CL - x.PL) * x.Frac) * x.Gain;
            r += (x.PR + (x.CR - x.PR) * x.Frac) * x.Gain;
        }
        double fade = 1.0;
        if (song != null && (song.LoopOffset > 0 || song.FadeSeconds > 0))
        {
            double fadeLen = song.LoopOffset > 0 ? Math.Min(FadeSeconds * Rate, song.LoopSamples) : song.FadeSeconds * Rate;
            double left0 = playLength - clock;
            if (left0 < fadeLen) fade = Math.Max(0, left0 / fadeLen);
        }
        left = (mono + l) * fade * outLevel;
        right = (mono + r) * fade * outLevel;
    }

    // ================================================================ 명령

    private void ExecuteOne()
    {
        var d = song.Data;
        if (pos >= song.DataEnd) { EndOfData(); return; }
        int c = d[pos];
        int len = VgmFormat.CommandLength(d, pos);
        if (len <= 0 || pos + len > d.Length) { playing = false; return; }
        switch (c)
        {
            case 0x61: due += d[pos + 1] | (d[pos + 2] << 8); break;
            case 0x62: due += 735; break;
            case 0x63: due += 882; break;
            case 0x66: EndOfData(); return;
            case 0xA0:
                if (psg != null && (d[pos + 1] & 0x80) == 0) WritePsg(d[pos + 1] & 0x0F, d[pos + 2]);
                break;
            case 0xD2:
                if (scc != null && (d[pos + 1] & 0x80) == 0) WriteScc(d[pos + 1], d[pos + 2], d[pos + 3]);
                break;
            case 0x5A:   // YM3812
                if (opl != null && song.Ym3812Clock > 0 && !opl3) WriteOpl(d[pos + 1], d[pos + 2]);
                break;
            case 0x5B:   // YM3526
                if (opl != null && song.Ym3812Clock <= 0 && song.Y8950Clock <= 0 && !opl3) WriteOpl(d[pos + 1], d[pos + 2]);
                break;
            case 0x5C:   // Y8950: FM 레지스터만 (0x07 · 0x09~0x1F = ADPCM · 키보드 · I/O)
                if (opl != null && y8950)
                {
                    int r = d[pos + 1];
                    if (r < 0x07 || r == 0x08 || r >= 0x20) WriteOpl(r, d[pos + 2]);
                }
                break;
            case 0x5E: if (opl != null && opl3) WriteOpl(d[pos + 1], d[pos + 2]); break;
            case 0x5F: if (opl != null && opl3) WriteOpl(0x100 | d[pos + 1], d[pos + 2]); break;
            case 0x50: Other(xSn, 0, 0, d[pos + 1]); break;
            case 0x4F: Other(xSn, 1, 0, d[pos + 1]); break;
            case 0x30: Other(xSn2, 0, 0, d[pos + 1]); break;   // 둘째 SN76489
            case 0x3F: Other(xSn2, 1, 0, d[pos + 1]); break;
            case 0x51: Other(xOpll, 0, d[pos + 1], d[pos + 2]); break;
            case 0x52: Other(xOpn2, 0, d[pos + 1], d[pos + 2]); break;
            case 0x53: Other(xOpn2, 1, d[pos + 1], d[pos + 2]); break;
            case 0x54: Other(xOpm, 0, d[pos + 1], d[pos + 2]); break;
            case 0x55: Other(xOpn, 0, d[pos + 1], d[pos + 2]); break;
            case 0xA5: Other(xOpn2203b, 0, d[pos + 1], d[pos + 2]); break;   // 둘째 YM2203
            case 0x56: Other(xOpna, 0, d[pos + 1], d[pos + 2]); break;
            case 0x57: Other(xOpna, 1, d[pos + 1], d[pos + 2]); break;
            case 0x58: Other(xOpnb, 0, d[pos + 1], d[pos + 2]); break;
            case 0x59: Other(xOpnb, 1, d[pos + 1], d[pos + 2]); break;
            case 0xB6: Other(xUpd, 0, d[pos + 1] & 0x7F, d[pos + 2]); break;   // uPD7759
            case 0xC0: Other(xSpcm, 0, d[pos + 1] | (d[pos + 2] << 8), d[pos + 3]); break;   // SegaPCM aaaa dd
            case 0xC4: Other(xQs, 0, d[pos + 3], (d[pos + 1] << 8) | d[pos + 2]); break;   // QSound mm ll rr
            case 0xB8: if ((d[pos + 1] & 0x80) == 0) Other(xOki, 0, d[pos + 1], d[pos + 2]); else Other(xOki2, 0, d[pos + 1] & 0x7F, d[pos + 2]); break;
            case 0xB4: if ((d[pos + 1] & 0x80) == 0) Other(xNes, 0, d[pos + 1], d[pos + 2]); break;
            case 0xB9: if ((d[pos + 1] & 0x80) == 0) Other(xPce, 0, d[pos + 1], d[pos + 2]); break;
            case 0xB7: if ((d[pos + 1] & 0x80) == 0) Other(xMsm, 0, d[pos + 1], d[pos + 2]); break;
            case 0xBD: if ((d[pos + 1] & 0x80) == 0) Other(xSaa, 0, d[pos + 1], d[pos + 2]); break;
            case 0xBB: if ((d[pos + 1] & 0x80) == 0) Other(xPokey, 0, d[pos + 1], d[pos + 2]); break;   // POKEY
            case 0xB3: if ((d[pos + 1] & 0x80) == 0) Other(xGb, 0, d[pos + 1], d[pos + 2]); break;
            case 0x90: case 0x91: case 0x92: case 0x93: case 0x94: case 0x95: StreamCommand(d, pos); break;
            case 0xD8: if (song.Cus30Clock > 0) Other(xCus30, 0, (d[pos + 1] << 8) | d[pos + 2], d[pos + 3]); break;   // 남코 CUS30 (이 프로그램 안에서만)
            case 0xD7: if (song.NsfChips != 0) Other(xNsfx, d[pos + 1], d[pos + 2], d[pos + 3]); break;   // NSF 확장 음원 (이 프로그램 안에서만)
            case 0x67: DataBlock(d, pos); break;
            case 0xE0: pcmPos = (int)VgmFormat.U32(d, pos + 1); break;
            default:
                if (c >= 0x70 && c <= 0x7F) due += (c & 15) + 1;
                else if (c >= 0x80 && c <= 0x8F)
                {
                    // YM2612 DAC: 뱅크에서 한 바이트를 0x2A에 쓰고 기다림
                    if (xOpn2 != null && pcmPos < pcmLen) Other(xOpn2, 0, 0x2A, pcmBank[pcmPos]);
                    pcmPos++;
                    due += c & 15;
                }
                break;
        }
        pos += len;
    }

    // ================================================================ 공통 칩

    private void Other(ExtraChip x, int port, int r, int v)
    {
        if (x == null) return;
        x.Chip.Write(port, r, v);
        UpdateOther(x);
    }

    /// <summary>데이터 블록 (0x67 0x66 종류 크기 …): YM2612 PCM · YM2608 ADPCM · OKIM6295 ROM · NES APU 메모리(0xC2).</summary>
    private void DataBlock(byte[] d, int p)
    {
        int type = d[p + 2];
        int size = (int)(VgmFormat.U32(d, p + 3) & 0x7FFFFFFF);
        bool second = (VgmFormat.U32(d, p + 3) & 0x80000000) != 0;   // 둘째 칩의 ROM
        int at = p + 7;
        if (at + size > d.Length) return;
        if (type < 0x40)
        {
            // 압축하지 않은 스트림 데이터 (0x00 YM2612 · 0x04 OKIM6258 …): 스트림이 블록 번호로 찾는다
            if (!banks.TryGetValue(type, out var bank)) banks[type] = bank = new DataBank();
            bank.Add(d, at, size);
            if (type == 0x00)
            {
                if (pcmLen + size > pcmBank.Length) Array.Resize(ref pcmBank, Math.Max(pcmLen + size, pcmBank.Length * 2));
                Array.Copy(d, at, pcmBank, pcmLen, size);
                pcmLen += size;
            }
            return;
        }
        if (type == 0xC2 && size > 2 && xNes != null)
        {
            // NES APU 메모리 (DPCM 샘플): 시작 주소 2바이트 + 바이트
            ((NesApu)xNes.Chip).LoadRam(d[at] | (d[at + 1] << 8), d, at + 2, size - 2);
            return;
        }
        if ((type == 0x81 || type == 0x82 || type == 0x83 || type == 0x8B || type == 0x8F || type == 0x80) && size > 8)
        {
            int start = (int)VgmFormat.U32(d, at + 4);
            var data = new byte[size - 8];
            Array.Copy(d, at + 8, data, 0, data.Length);
            if (type == 0x81 && xOpna != null) ((OpnChip)xOpna.Chip).LoadAdpcm(data, start);
            if (type == 0x82 && xOpnb != null) ((OpnChip)xOpnb.Chip).LoadAdpcmA(data, start);   // YM2610 ADPCM-A ROM
            if (type == 0x83 && xOpnb != null) ((OpnChip)xOpnb.Chip).LoadAdpcm(data, start);    // YM2610 ADPCM-B (DELTA-T) ROM
            if (type == 0x8B && !second && xOki != null) ((Okim6295)xOki.Chip).LoadRom(data, start);
            if (type == 0x8B && second && xOki2 != null) ((Okim6295)xOki2.Chip).LoadRom(data, start);
            if (type == 0x8F && xQs != null) ((QSound)xQs.Chip).LoadRom(data, start);
            if (type == 0x80 && xSpcm != null) ((SegaPcm)xSpcm.Chip).LoadRom(data, start);
        }
    }

    // ================================================================ DAC 스트림 (0x90~0x95)

    private DacStream StreamOf(int id)
    {
        if (!streams.TryGetValue(id, out var st)) streams[id] = st = new DacStream();
        return st;
    }

    private void StreamCommand(byte[] d, int p)
    {
        if (d[p] == 0x94 && d[p + 1] == 0xFF) { foreach (var x in streams.Values) x.Running = false; return; }
        var st = StreamOf(d[p + 1]);
        switch (d[p])
        {
            case 0x90: st.ChipType = d[p + 2]; st.Port = d[p + 3]; st.Reg = d[p + 4]; break;
            case 0x91: st.BankType = d[p + 2]; st.StepSize = Math.Max(1, (int)d[p + 3]); st.StepBase = d[p + 4]; break;
            case 0x92: st.Frequency = VgmFormat.U32(d, p + 2); break;
            case 0x93:
                {
                    uint ofs = VgmFormat.U32(d, p + 2);
                    int mode = d[p + 6];
                    uint len = VgmFormat.U32(d, p + 7);
                    st.Loop = (mode & 0x80) != 0;
                    st.Reverse = (mode & 0x10) != 0;
                    if ((mode & 3) == 0)
                    {
                        // 길이는 그대로 두고 읽는 위치만 바꾼다
                        if (ofs != 0xFFFFFFFF) { st.Start = (int)ofs; st.Pos = st.Start + (st.Reverse ? -st.StepBase : st.StepBase); }
                        break;
                    }
                    if (ofs != 0xFFFFFFFF) st.Start = (int)ofs;
                    int count = (mode & 3) == 1 ? (int)Math.Min(len, int.MaxValue)
                              : (mode & 3) == 2 ? (int)Math.Min(len / 1000.0 * st.Frequency, int.MaxValue)
                              : int.MaxValue;   // 3 = 데이터 끝까지
                    StreamBegin(st, count);
                    break;
                }
            case 0x94: st.Running = false; break;
            case 0x95:
                {
                    int block = d[p + 2] | (d[p + 3] << 8);
                    int flags = d[p + 4];
                    if (st.BankType < 0 || !banks.TryGetValue(st.BankType, out var bank) || block >= bank.Starts.Count) { st.Running = false; break; }
                    int start = bank.Starts[block];
                    int end = block + 1 < bank.Starts.Count ? bank.Starts[block + 1] : bank.Length;
                    st.Start = start;
                    st.Loop = (flags & 1) != 0;
                    st.Reverse = (flags & 0x10) != 0;
                    StreamBegin(st, (end - start) / st.StepSize);
                    break;
                }
        }
    }

    private void StreamBegin(DacStream st, int count)
    {
        st.Pos = st.Start + (st.Reverse ? -st.StepBase : st.StepBase);
        st.Left = st.Total = Math.Max(0, count);
        st.Running = st.Frequency > 0 && count > 0;
        st.Next = due;
        anyStream |= st.Running;
    }

    /// <summary>until(VGM 샘플)까지 할 스트림 쓰기를 한다.</summary>
    private void RunStreams(double until)
    {
        bool any = false;
        foreach (var st in streams.Values)
        {
            if (!st.Running) continue;
            double gap = Rate / st.Frequency;
            while (st.Running && st.Next <= until)
            {
                if (!banks.TryGetValue(st.BankType, out var bank) || st.Pos < 0 || st.Pos >= bank.Length || st.Left <= 0)
                {
                    // 끝: 되풀이면 처음부터 (데이터 끝까지 모드는 그 끝에서 다시)
                    if (st.Loop && st.Total > 0) { st.Pos = st.Start + (st.Reverse ? -st.StepBase : st.StepBase); st.Left = st.Total; if (bank != null && st.Pos >= 0 && st.Pos < bank.Length) continue; }
                    st.Running = false; break;
                }
                StreamWrite(st, bank.Data[st.Pos]);
                st.Pos += st.Reverse ? -st.StepSize : st.StepSize;
                st.Left--;
                st.Next += gap;
            }
            any |= st.Running;
        }
        anyStream = any;
    }

    /// <summary>스트림 바이트 하나를 칩에 (VGM 칩 번호: 0x02 YM2612, 0x03 YM2151, 0x06 YM2203, 0x07 YM2608, 0x08 YM2610, 0x13 GB DMG, 0x17 OKIM6258, 0x18 OKIM6295).</summary>
    private void StreamWrite(DacStream st, int v)
    {
        if ((st.ChipType & 0x80) != 0) return;   // 둘째 칩
        switch (st.ChipType)
        {
            case 0x02: Other(xOpn2, st.Port, st.Reg, v); break;
            case 0x03: Other(xOpm, 0, st.Reg, v); break;
            case 0x06: Other(xOpn, 0, st.Reg, v); break;
            case 0x07: Other(xOpna, st.Port, st.Reg, v); break;
            case 0x08: Other(xOpnb, st.Port, st.Reg, v); break;
            case 0x13: Other(xGb, 0, st.Reg, v); break;
            case 0x17: Other(xMsm, 0, st.Reg, v); break;
            case 0x18: Other(xOki, 0, st.Reg, v); break;
        }
    }

    /// <summary>칩에 물어 채널 표시를 고친다.</summary>
    private void UpdateOther(ExtraChip x)
    {
        if (x.Base < 0) return;
        int n = Math.Min(x.Chip.ChannelCount, channels.Count - x.Base);
        for (int i = 0; i < n; i++)
        {
            int v = x.Base + i;
            double f = x.Chip.ChannelFrequency(i);
            float lv = x.Chip.ChannelLevel(i);
            if (f > 0) SetVoice(v, true, f, lv);
            else
            {
                SetVoice(v, false, 0, lv);
                if (x.Chip.ChannelActive(i)) everSounded[v] = true;
            }
        }
    }

    private void EndOfData()
    {
        if (song.LoopOffset > 0 && loopsLeft > 0)
        {
            loopsLeft--;
            pos = song.LoopOffset;
            return;
        }
        // 루프가 없으면 데이터가 끝난 곳에서 곧 끝낸다 (머리말 길이보다 짧더라도)
        playLength = Math.Min(playLength, Math.Max(due, (long)Math.Ceiling(clock)));
        playing = false;
    }

    // ================================================================ PSG

    private void WritePsg(int r, int v)
    {
        psgReg[r] = (byte)v;
        if (r < 6) { WritePsgPeriod(r >> 1); UpdatePsg(r >> 1); return; }
        psg.Write(r, v);
        if (r == 7) for (int ch = 0; ch < 3; ch++) UpdatePsg(ch);
        else if (r >= 8 && r <= 10) UpdatePsg(r - 8);
    }

    private int PsgPeriod(int ch) { return psgReg[ch * 2] | ((psgReg[ch * 2 + 1] & 0x0F) << 8); }

    private void WritePsgPeriod(int ch)
    {
        int p = PsgPeriod(ch);
        int semis = appliedSemis[psgBase + ch];
        if (semis != 0 && p > 0) p = Clamp((int)Math.Round(p / Ratio(semis)), 1, 4095);
        psg.Write(ch * 2, p & 0xFF);
        psg.Write(ch * 2 + 1, p >> 8);
    }

    private void UpdatePsg(int ch)
    {
        int v = psgBase + ch;
        int a = psgReg[8 + ch] & 0x1F;
        bool tone = (psgReg[7] & (1 << ch)) == 0;
        int p = PsgPeriod(ch);
        bool audible = tone && a != 0 && p > 0;
        float vol = (a & 0x10) != 0 ? 1f : (a & 0x0F) / 15f;
        SetVoice(v, audible, audible ? song.AyClock / (16.0 * p) : 0, vol);
        if (!tone && (psgReg[7] & (8 << ch)) == 0 && a != 0) everSounded[v] = true;   // 노이즈만
    }

    // ================================================================ SCC

    private void WriteScc(int port, int r, int v)
    {
        switch (port & 0x7F)
        {
            case 1:
                {
                    int reg = r & 0x0F;
                    if (reg >= 10) { scc.WritePort(1, r, v); return; }
                    int ch = reg >> 1;
                    sccFreq[ch] = (reg & 1) == 0 ? (sccFreq[ch] & 0xF00) | (v & 0xFF) : (sccFreq[ch] & 0x0FF) | ((v & 0x0F) << 8);
                    WriteSccFreq(ch);
                    UpdateScc(ch);
                    return;
                }
            case 2:
                sccVol[r % SccEmulator.Channels] = v & 0x0F;
                scc.WritePort(2, r, v);
                UpdateScc(r % SccEmulator.Channels);
                return;
            case 3:
                sccEnable = v & 0x1F;
                scc.WritePort(3, r, v);
                for (int ch = 0; ch < SccEmulator.Channels; ch++) UpdateScc(ch);
                return;
            default:
                scc.WritePort(port & 0x7F, r, v);
                return;
        }
    }

    private void WriteSccFreq(int ch)
    {
        int n = sccFreq[ch];
        int semis = appliedSemis[sccBase + ch];
        if (semis != 0 && n > 8) n = Clamp((int)Math.Round((n + 1) / Ratio(semis)) - 1, 9, 4095);
        scc.WritePort(1, ch * 2, n & 0xFF);
        scc.WritePort(1, ch * 2 + 1, n >> 8);
    }

    private void UpdateScc(int ch)
    {
        int n = sccFreq[ch];
        bool audible = (sccEnable & (1 << ch)) != 0 && sccVol[ch] > 0 && n > 8;
        SetVoice(sccBase + ch, audible, audible ? song.SccClock / (32.0 * (n + 1)) : 0, sccVol[ch] / 15f);
    }

    // ================================================================ OPL

    private void WriteOpl(int reg, int v)
    {
        reg &= opl3 ? 0x1FF : 0xFF;
        oplReg[reg] = (byte)v;
        int lo = reg & 0xFF, bank = reg >> 8;
        if ((lo >= 0xA0 && lo <= 0xA8) || (lo >= 0xB0 && lo <= 0xB8))
        {
            int ch = lo & 0x0F;
            WriteOplFreq(bank, ch);
            UpdateFm(bank, ch);
            return;
        }
        if (reg == 0xBD) { WriteOplRhythm(); return; }
        opl.Write(reg, v);
        if (lo >= 0x40 && lo <= 0x55)
        {
            // 볼륨(TL)이 바뀐 채널 표시를 고친다
            for (int ch = 0; ch < 9; ch++) if (CarrierOffset(ch) == lo - 0x40) UpdateFm(bank, ch);
        }
    }

    private void WriteOplFreq(int bank, int ch)
    {
        int b = bank << 8;
        int fnum = oplReg[b + 0xA0 + ch] | ((oplReg[b + 0xB0 + ch] & 3) << 8);
        int block = (oplReg[b + 0xB0 + ch] >> 2) & 7;
        int key = oplReg[b + 0xB0 + ch] & 0x20;
        int v = fmBase + bank * 9 + ch;
        int semis = appliedSemis[v];
        if (semis != 0 && fnum > 0)
        {
            double f = fnum * Ratio(semis);
            while (f >= 1023.5 && block < 7) { f /= 2; block++; }
            while (f < 256 && block > 0) { f *= 2; block--; }
            fnum = Clamp((int)Math.Round(f), 1, 1023);
        }
        if (appliedMute[v]) key = 0;
        opl.Write(b + 0xA0 + ch, fnum & 0xFF);
        opl.Write(b + 0xB0 + ch, key | (block << 2) | (fnum >> 8));
    }

    /// <summary>BD: 리듬 모드면 끈 채널의 드럼 비트를 지운다 (6 = BD, 7 = SD · HH, 8 = TOM · CY).</summary>
    private void WriteOplRhythm()
    {
        int v = oplReg[0xBD];
        if ((v & 0x20) != 0 && fmBase >= 0)
        {
            if (appliedMute[fmBase + 6]) v &= ~0x10;
            if (appliedMute[fmBase + 7]) v &= ~0x09;
            if (appliedMute[fmBase + 8]) v &= ~0x06;
        }
        opl.Write(0xBD, v);
        for (int ch = 6; ch < 9; ch++) UpdateFm(0, ch);
    }

    /// <summary>채널 ch의 캐리어(둘째 오퍼레이터) 레지스터 오프셋.</summary>
    private static int CarrierOffset(int ch) { return (ch / 3) * 8 + ch % 3 + 3; }

    private void UpdateFm(int bank, int ch)
    {
        int b = bank << 8;
        int v = fmBase + bank * 9 + ch;
        int fnum = oplReg[b + 0xA0 + ch] | ((oplReg[b + 0xB0 + ch] & 3) << 8);
        int block = (oplReg[b + 0xB0 + ch] >> 2) & 7;
        bool key = (oplReg[b + 0xB0 + ch] & 0x20) != 0;
        int bd = oplReg[0xBD];
        bool drum = bank == 0 && ch >= 6 && (bd & 0x20) != 0;
        if (drum) key = ch == 6 ? (bd & 0x10) != 0 : ch == 7 ? (bd & 0x09) != 0 : (bd & 0x06) != 0;
        bool audible = key && fnum > 0;
        float vol = 1f - (oplReg[b + 0x40 + CarrierOffset(ch)] & 0x3F) / 63f;
        SetVoice(v, audible, audible ? fnum * (fmClock / 72.0) / Math.Pow(2, 20 - block) : 0, vol);
    }

    // ================================================================ 조절 (키 · 채널 음높이 · 끄기)

    /// <summary>조절값이 바뀐 채널만 칩에 다시 쓴다.</summary>
    private void SyncAdjustments()
    {
        for (int v = 0; v < channels.Count; v++)
        {
            int semis = Options.Transpose + Options.VoiceTranspose[v];
            bool mute = Options.VoiceMute[v];
            if (semis == appliedSemis[v] && mute == appliedMute[v]) continue;
            appliedSemis[v] = semis;
            appliedMute[v] = mute;
            var c = channels[v];
            switch (c.Chip)
            {
                case ChipKind.Psg: psg.Mute[c.Index] = mute; WritePsgPeriod(c.Index); break;
                case ChipKind.Scc: scc.Mute[c.Index] = mute; WriteSccFreq(c.Index); break;
                case ChipKind.Fm:
                    WriteOplFreq(c.Index / 9, c.Index % 9);
                    if (c.Index >= 6 && c.Index < 9) WriteOplRhythm();
                    break;
                case ChipKind.Other:
                    extras[c.Extra].Chip.SetMute(c.Index, mute);
                    extras[c.Extra].Chip.SetPitch(c.Index, Ratio(semis));
                    break;
            }
        }
    }

    // ================================================================ 표시

    private void SetVoice(int v, bool audible, double freq, float vol)
    {
        int n = -1;
        if (audible && freq > 0)
        {
            n = (int)Math.Round(69 + 12 * Math.Log(freq / 440.0, 2));
            if (n < 0 || n > 127) n = -1;
        }
        if (n >= 0 && n != note[v]) noteOns[v]++;
        if (n >= 0) everSounded[v] = true;
        note[v] = n;
        volume[v] = vol;
    }

    /// <summary>채널별 지금 상태 (음 = 조옮김 전).</summary>
    public void CopyStates(VoiceState[] dst)
    {
        for (int v = 0; v < dst.Length; v++)
        {
            if (v >= channels.Count) { dst[v] = new VoiceState { Note = -1 }; continue; }
            var c = channels[v];
            dst[v] = new VoiceState
            {
                Note = playing ? note[v] : -1,
                Volume = volume[v],
                NoteOnCount = noteOns[v],
                Instrument = InstrumentText(c),
                InstrumentFound = true,
            };
        }
    }

    private string InstrumentText(Channel c)
    {
        switch (c.Chip)
        {
            case ChipKind.Psg:
                {
                    int m = psgReg[7];
                    bool tone = (m & (1 << c.Index)) == 0, noise = (m & (8 << c.Index)) == 0;
                    string t = tone && noise ? "톤+노이즈" : tone ? "톤" : noise ? "노이즈" : "PSG";
                    return (psgReg[8 + c.Index] & 0x10) != 0 ? t + " · 엔벨로프" : t;
                }
            case ChipKind.Scc: return song.SccPlus ? "SCC+ 파형" : "SCC 파형";
            case ChipKind.Other:
                {
                    var chip = c.Extra < extras.Count ? extras[c.Extra].Chip : null;
                    if (chip is Ym2413 ll) { string n = ll.InstrumentOf(c.Index); return n.Length > 0 ? "OPLL " + n : "OPLL 리듬"; }
                    return chip != null ? chip.Name : "";
                }
            default: return opl3 ? "OPL3 FM" : "FM";
        }
    }

    private static double Ratio(int semis) { return Math.Pow(2, semis / 12.0); }
    private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
}
