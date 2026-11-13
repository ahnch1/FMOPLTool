using System;
using System.Collections.Generic;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// Scream Tracker 2 / 3 재생기 (44.1kHz 스테레오).
/// <list type="bullet">
/// <item>STM: st2play(viiri/st2play)를 옮긴 것 — 4채널, 템포 바이트(윗 4비트 = 줄당 틱, 아래 = 미세), 주기 표 · FXMULT 10 · LFO 표 그대로, 명령 A~J</item>
/// <item>S3M: ST3 규칙 — 주기 = 8363 × 16 × (표 >> 옥타브) / C4Spd, 주파수 = 14317056 / 주기, 틱 = 2.5 / 템포 초.
///   명령 A B C D E F G H I J K L O Q R S(1 3 4 8 B C D E) T U V X, 볼륨 칸, 채널 팬, AdLib 멜로디 채널(OPL2)</item>
/// </list>
/// 샘플은 선형 보간으로 섞는다. 재생 위치는 지난 시간(ms)과 주문/줄로 알려 준다.
/// </summary>
public sealed class TrackerPlayer
{
    public const int Rate = 44100;
    private const int Rows = TrackerPattern.Rows;

    // ST3 주기 표 (옥타브 0) · ST2 주기 표 (st2play)
    private static readonly int[] St3Period = { 1712, 1616, 1524, 1440, 1356, 1280, 1208, 1140, 1076, 1016, 960, 907 };
    private static readonly int[] St2PeriodBase = { 17080, 16012, 15184, 14236, 13664, 12808, 12008, 11388, 10676, 10248, 9608, 9108 };
    private static readonly int[] St2Tempo = { 140, 50, 25, 15, 10, 7, 6, 4, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1 };
    private static readonly int[] Lfo = {
        0, 24, 49, 74, 97, 120, 141, 161, 180, 197, 212, 224, 235, 244, 250, 253,
        255, 253, 250, 244, 235, 224, 212, 197, 180, 161, 141, 120, 97, 74, 49, 24,
        0, -24, -49, -74, -97, -120, -141, -161, -180, -197, -212, -224, -235, -244, -250, -253,
        -255, -253, -250, -244, -235, -224, -212, -197, -180, -161, -141, -120, -97, -74, -49, -24, 0 };
    private const double St2BaseFreq = 36072500.0;
    private const double St3Clock = 14317056.0;
    private static readonly int[] RetrigAdd = { 0, -1, -2, -4, -8, -16, 0, 0, 0, 1, 2, 4, 8, 16, 0, 0 };

    private sealed class Chan
    {
        public int Index;
        public TrackerSample Sample;
        public int SampleNo;
        public double Pos, Step;
        public bool Active;
        public int Period, TargetPeriod, BasePeriod;
        public int Volume;
        public int OutVolume;       // 이번 틱의 볼륨 (트레몰로 · 트레머 반영)
        public int Pan = 8;          // 0~15
        public int Note = -1;
        public TrackerCell Cell;
        public int Cmd, Info;
        public int LastInfo, PortaSpeed, VibInfo;
        public int VibPos, TremPos, VibWave, TremWave;
        public int TremorCount; public bool TremorOn = true;
        public int RetrigCount;
        public int LoopRow, LoopCount;
        public int DelayTicks = -1; public TrackerCell DelayedCell;
        public int CutTick = -1;
        public bool Adlib;
        public int AdlibCh = -1;
        public bool KeyOn;
        // ST2
        public int St2VibCurrent, St2TremorCounter, St2TremorState = 1, St2VolInitial;
        public int St2LoopStart = 0xFFFF, St2LoopEnd;
        // 표시
        public int NoteOnCount;
        public int ShownNote = -1;
    }

    private TrackerModule mod;
    private readonly Chan[] ch = new Chan[TrackerModule.MaxChannels];
    private readonly OplChip opl = new OplChip(false);
    private bool hasAdlib;
    private double oplPos, oplPrev, oplCur;
    private readonly int[] oplFnum = new int[9], oplBlock = new int[9];

    private List<int> orders;
    private int orderIndex, row, tick, speed, tempo, globalVolume;
    private int nextOrder = -1, breakRow = -1, patternDelay, patternDelayCount;
    private double samplesPerTick, tickCounter;
    private bool playing, ending, stopAtEnd = true;
    private int shownRow;
    private int singlePatternIndex = -1;
    private int tail;
    private long elapsedSamples;
    private readonly HashSet<long> visited = new HashSet<long>();
    private PlaybackOptions options = new PlaybackOptions();
    // ST2 (st2play) 상태
    private int st2FramesPerTick, st2Frame, st2TicksPerRow, st2Tick, st2Tempo;
    private bool st2ChangePattern;
    // 시음 (편집기에서 건반으로)
    private bool jam;

    public bool IsPlaying { get { return playing || jam; } }
    public bool IsSongPlaying { get { return playing; } }
    public TrackerModule Module { get { return mod; } }
    public PlaybackOptions Options { get { return options; } }
    public int CurrentOrder { get { return orders != null && orderIndex < orders.Count ? orderIndex : 0; } }
    public int CurrentPattern { get { return orders != null && orderIndex < orders.Count ? orders[orderIndex] : 0; } }
    public int CurrentRow { get { return shownRow; } }
    public int Speed { get { return mod != null && mod.Kind == TrackerKind.Stm ? st2TicksPerRow : speed; } }
    public int Tempo { get { return tempo; } }
    /// <summary>재생 위치 = 지난 시간 (ms, 속도 배수 반영 전 곡 시간).</summary>
    public double PositionMs { get { return elapsedSamples * 1000.0 / Rate; } }

    public TrackerPlayer()
    {
        for (int i = 0; i < ch.Length; i++) ch[i] = new Chan { Index = i };
    }

    // ================================================================ 시작 / 정지

    /// <summary>곡 재생. startOrder부터, singlePattern ≥ 0이면 그 패턴만 되풀이. loop = 끝에서 처음으로 (편집기).</summary>
    public void Start(TrackerModule m, int startOrder = 0, int startRow = 0, int singlePattern = -1, bool loop = false, PlaybackOptions opts = null, double skipMs = 0)
    {
        mod = m;
        options = opts ?? new PlaybackOptions();
        orders = m.PlayOrders();
        if (orders.Count == 0) orders.Add(0);
        singlePatternIndex = singlePattern;
        stopAtEnd = !loop;
        jam = false;
        Reset();
        orderIndex = Math.Max(0, Math.Min(orders.Count - 1, startOrder));
        if (singlePattern >= 0) { orders = new List<int> { singlePattern }; orderIndex = 0; }
        row = Math.Max(0, Math.Min(Rows - 1, startRow));
        playing = true;
        if (mod.Kind == TrackerKind.Stm) St2Start(); else S3mStart();
        if (skipMs > 0) FastForward(skipMs);
    }

    public void Stop()
    {
        playing = false;
        jam = false;
        foreach (var c in ch) { c.Active = false; AdlibOff(c); c.ShownNote = -1; }
    }

    private void Reset()
    {
        foreach (var c in ch)
        {
            int i = c.Index;
            ch[i] = new Chan { Index = i, NoteOnCount = c.NoteOnCount };
            ch[i].Pan = mod.Kind == TrackerKind.Stm ? 8 : mod.Stereo ? mod.Pan[i] & 15 : 8;
            ch[i].Adlib = mod.IsAdlibChannel(i);
            if (ch[i].Adlib) { int s = mod.ChannelSettings[i] & 127; ch[i].AdlibCh = s >= 16 && s < 25 ? s - 16 : -1; }
        }
        hasAdlib = false;
        for (int i = 0; i < mod.Channels; i++) if (ch[i].Adlib && mod.IsChannelUsed(i)) hasAdlib = true;
        opl.Reset();
        opl.Write(0x01, 0x20);
        oplPos = 0; oplPrev = oplCur = 0;
        speed = Math.Max(1, mod.Speed);
        tempo = Math.Max(32, mod.Tempo);
        globalVolume = mod.GlobalVolume;
        tick = 0; tickCounter = 0;
        nextOrder = -1; breakRow = -1; patternDelay = 0; patternDelayCount = 0;
        elapsedSamples = 0;
        ending = false; tail = 0;
        visited.Clear();
    }

    /// <summary>편집기 시음: 노래 없이 한 채널로 음을 낸다. 곡 재생 중이 아닐 때만.</summary>
    public void Jam(TrackerModule m, int sampleNo, int note, int channel)
    {
        if (playing) return;
        if (!ReferenceEquals(mod, m) || !jam)
        {
            mod = m;
            options = new PlaybackOptions();
            orders = new List<int> { 0 };
            Reset();
            jam = true;
        }
        channel = Math.Max(0, Math.Min(mod.Channels - 1, channel));
        var c = ch[channel];
        var cell = TrackerCell.Empty;
        cell.Note = (byte)note;
        cell.Instrument = (byte)sampleNo;
        if (mod.Kind == TrackerKind.Stm) { c.Cmd = 0; St2Trigger(c, cell); }
        else
        {
            var s = SampleOf(sampleNo);
            c.Adlib = s != null && s.IsAdlib;
            if (c.Adlib && c.AdlibCh < 0) c.AdlibCh = 0;
            c.Cmd = 0;
            S3mTrigger(c, cell, 0);
            c.OutVolume = c.Volume;
        }
    }

    public void JamOff(int channel)
    {
        if (!jam || channel < 0 || channel >= ch.Length) return;
        var c = ch[channel];
        c.Active = false;
        AdlibOff(c);
    }

    private void FastForward(double ms)
    {
        long target = (long)(ms * Rate / 1000.0);
        while (playing && !ending && elapsedSamples < target) FastTick();
        foreach (var c in ch) { c.Active = false; AdlibOff(c); }   // 울리던 음은 끊고 다음 노트부터
    }

    /// <summary>곡 길이 (초): 처음부터 끝(또는 다시 돌아오는 곳)까지 빨리 돌려 잰다. 최대 30분.</summary>
    public static double MeasureSeconds(TrackerModule m)
    {
        var p = new TrackerPlayer();
        p.Start(m);
        long limit = (long)Rate * 60 * 30;
        while (p.playing && !p.ending && p.elapsedSamples < limit) p.FastTick();
        return p.elapsedSamples / (double)Rate;
    }

    // ================================================================ 샘플 단위

    /// <summary>스테레오 샘플 1개 (±1 근처 값을 16bit 크기로 돌려준다).</summary>
    public void Generate(out double left, out double right)
    {
        left = right = 0;
        if (mod == null || (!playing && !jam)) { if (hasAdlib) { double a = Opl(); left = right = a; } return; }
        if (playing) SequencerStep();
        double l = 0, r = 0;
        int n = mod.Channels;
        bool mono = mod.Kind == TrackerKind.Stm || !mod.Stereo;
        for (int i = 0; i < n; i++)
        {
            var c = ch[i];
            if (!c.Active || c.Adlib || c.Sample == null || options.VoiceMute[i]) continue;
            double v = MixSample(c);
            if (v == 0) continue;
            double vol = c.OutVolume * globalVolume / (64.0 * 64.0);
            if (mono) { l += v * vol; r += v * vol; continue; }
            double pan = c.Pan / 15.0;
            l += v * vol * Math.Cos(pan * Math.PI / 2) * 1.41;
            r += v * vol * Math.Sin(pan * Math.PI / 2) * 1.41;
        }
        double gain = mod.Kind == TrackerKind.Stm ? 0.4 : 0.7 * Math.Max(16, mod.MasterVolume) / 48.0;
        left = l * gain * 32768 * 0.5;
        right = r * gain * 32768 * 0.5;
        if (hasAdlib) { double a = Opl(); left += a; right += a; }
    }

    private double MixSample(Chan c)
    {
        var s = c.Sample;
        var d = s.Data;
        if (mod.Kind == TrackerKind.Stm)
        {
            // st2play: 끝(루프 끝)에 닿으면 루프 시작으로, 루프가 없으면 멈춤
            if ((int)c.Pos >= c.St2LoopEnd)
            {
                if (c.St2LoopStart != 0xFFFF) c.Pos = c.St2LoopStart + (c.Pos - Math.Floor(c.Pos));
                else { c.Active = false; return 0; }
            }
        }
        int ip = (int)c.Pos;
        if (ip >= d.Length) { c.Active = false; return 0; }
        double frac = c.Pos - ip;
        int next = ip + 1;
        int end = mod.Kind == TrackerKind.Stm ? c.St2LoopEnd : s.Loop ? s.LoopEnd : d.Length;
        if (next >= end) next = mod.Kind == TrackerKind.Stm ? (c.St2LoopStart != 0xFFFF ? c.St2LoopStart : ip) : s.Loop ? s.LoopStart : ip;
        if (next >= d.Length) next = ip;
        double v = (d[ip] + (d[next] - d[ip]) * frac) / 32768.0;
        c.Pos += c.Step;
        if (mod.Kind == TrackerKind.S3m)
        {
            if (s.Loop && s.LoopEnd > s.LoopStart)
            {
                while (c.Pos >= s.LoopEnd) c.Pos -= s.LoopEnd - s.LoopStart;
            }
            else if (c.Pos >= d.Length) c.Active = false;
        }
        return v;
    }

    private double Opl()
    {
        // OPL2(49716Hz) → 44100Hz 선형 보간
        oplPos += 49716.0 / Rate;
        while (oplPos >= 1.0) { oplPos -= 1.0; oplPrev = oplCur; oplCur = opl.Generate(); }
        return (oplPrev + (oplCur - oplPrev) * oplPos) * 0.9;
    }

    /// <summary>샘플 1개만큼 시간을 보내고 틱 경계면 틱 처리.</summary>
    private void SequencerStep()
    {
        elapsedSamples++;
        if (mod.Kind == TrackerKind.Stm)
        {
            if (st2Frame <= 1) { st2Frame = st2FramesPerTick; St2Tick(); }
            else st2Frame--;
        }
        else
        {
            tickCounter += 1.0;
            if (tickCounter >= samplesPerTick) { tickCounter -= samplesPerTick; S3mTick(); }
        }
        if (ending && ++tail > Rate) playing = false;
    }

    /// <summary>틱 하나를 한 번에 처리하고 그 길이만큼 시간을 보낸다 (섞지 않음: 길이 재기 · 앞으로 감기).</summary>
    private void FastTick()
    {
        if (mod.Kind == TrackerKind.Stm)
        {
            St2Tick();
            elapsedSamples += st2FramesPerTick;
        }
        else
        {
            S3mTick();
            elapsedSamples += (long)Math.Max(1, Math.Round(samplesPerTick));
            tickCounter = 0;
        }
    }

    private double SpeedFactor { get { return options.Speed < 0.05 ? 0.05 : options.Speed; } }

    // ================================================================ ST2 (st2play)

    private void St2Start()
    {
        st2Tempo = mod.Tempo;
        globalVolume = mod.GlobalVolume;
        St2SetTempo(st2Tempo);
        st2Frame = 1;   // 첫 샘플에서 첫 틱
        st2Tick = 0;
        st2ChangePattern = false;
        foreach (var c in ch) { c.St2LoopStart = 0xFFFF; c.St2LoopEnd = 0; }
        visited.Add((long)orderIndex << 8);
    }

    private void St2SetTempo(int t)
    {
        st2Tempo = t;
        st2TicksPerRow = t >> 4;
        double div = 49 - ((St2Tempo[Math.Min(17, st2TicksPerRow)] * (t & 0x0F)) >> 4);
        st2FramesPerTick = Math.Max(1, (int)(Rate / Math.Max(1.0, div) / SpeedFactor));
        tempo = (int)Math.Round(div * 2.5);   // 표시용 (대략 BPM)
    }

    public void ApplySpeed()
    {
        if (mod == null) return;
        if (mod.Kind == TrackerKind.Stm) St2SetTempo(st2Tempo); else SetS3mTempo(tempo);
    }

    private int St2Period(int note)
    {
        int oct = note >> 4, semi = note & 15;
        if (semi >= 12) return 0;
        return St2PeriodBase[semi] >> oct;
    }

    private void St2UpdateFreq(Chan c)
    {
        c.Step = c.Period >= 551 ? St2BaseFreq / c.Period / Rate : 0;
        if (c.Step == 0) c.Active = false;
    }

    /// <summary>st2play change_pattern: 다음 주문의 0번 줄로. 끝(99)이면 곡 끝 (편집기에서는 처음으로).</summary>
    private void St2Goto(int ord)
    {
        if (singlePatternIndex >= 0) { orderIndex = 0; row = 0; return; }
        if (ord >= orders.Count)
        {
            if (stopAtEnd) { ending = true; return; }
            ord = 0;
        }
        if (stopAtEnd && !visited.Add((long)ord << 8)) { ending = true; return; }   // 되돌아가는 곡: 한 바퀴만
        orderIndex = ord;
        row = 0;
    }

    private void St2Tick()
    {
        if (ending) { foreach (var c in ch) c.OutVolume = 0; return; }
        if (st2Tick != 0)
        {
            st2Tick--;
            for (int i = 0; i < 4; i++) St2CmdTick(ch[i]);
        }
        else
        {
            if (st2ChangePattern)
            {
                st2ChangePattern = false;
                int nx = nextOrder >= 0 ? nextOrder : orderIndex + 1;
                nextOrder = -1;
                St2Goto(nx);
                if (ending) return;
            }
            var pat = PatternAt(orderIndex);
            shownRow = row;
            for (int i = 0; i < 4; i++)
            {
                var c = ch[i];
                var cell = pat != null && row < Rows ? pat[row, i] : TrackerCell.Empty;
                c.Cell = cell;
                c.Cmd = cell.Command; c.Info = cell.Info;
                St2Trigger(c, cell);
                if (c.Cmd == 9) St2CmdTick(c);
            }
            row++;
            if (row >= Rows) st2ChangePattern = true;
            st2Tick = st2TicksPerRow != 0 ? st2TicksPerRow - 1 : 0;
        }
        foreach (var c in ch) c.OutVolume = c.Volume;
    }

    private void St2Trigger(Chan c, TrackerCell cell)
    {
        int vol = cell.Volume == TrackerCell.NoVolume ? 65 : cell.Volume;
        if (vol != 65) { c.Volume = vol; c.St2VolInitial = vol; }
        if (c.Cmd == 7)   // 톤 포르타멘토
        {
            if (cell.Note != TrackerCell.NoNote && cell.Note != TrackerCell.NoteOff) c.TargetPeriod = St2Period(cell.Note);
            return;
        }
        if (cell.Instrument != 0)
        {
            var s = SampleOf(cell.Instrument);
            c.Sample = s; c.SampleNo = cell.Instrument;
            if (vol == 65 && s != null) { c.Volume = s.Volume; c.St2VolInitial = c.Volume; }
            if (s != null)
            {
                if (s.Loop) { c.St2LoopEnd = s.LoopEnd; c.St2LoopStart = s.LoopStart; }
                else { c.St2LoopEnd = s.Length; c.St2LoopStart = 0xFFFF; }
            }
        }
        if (cell.Note != TrackerCell.NoNote)
        {
            c.Pos = 0;
            if (cell.Note == TrackerCell.NoteOff) { c.St2LoopEnd = 0; c.St2LoopStart = 0xFFFF; c.Active = false; c.ShownNote = -1; }
            else if (c.Sample != null && c.Sample.IsSample && !options.VoiceMute[c.Index])
            {
                int note = TransposedNote(c.Index, cell.Note, 4);
                int c2 = c.Sample.C2Spd > 0 ? c.Sample.C2Spd : 8448;
                c.Period = St2Period(note) * 8448 / c2;
                c.TargetPeriod = c.Period;
                c.Active = true;
                c.NoteOnCount++;
                c.ShownNote = cell.Note;
                St2UpdateFreq(c);
            }
        }
        // 줄 명령
        switch (c.Cmd)
        {
            case 1: if (c.Info != 0) St2SetTempo(c.Info); break;
            case 2: nextOrder = c.Info; break;
            case 3: st2ChangePattern = true; break;
        }
    }

    private void St2CmdTick(Chan c)
    {
        const int FxMult = 10;
        switch (c.Cmd)
        {
            case 5: c.Period += FxMult * c.Info; St2UpdateFreq(c); break;
            case 6: c.Period -= FxMult * c.Info; St2UpdateFreq(c); break;
            case 4:
                if ((c.Info & 0x0F) != 0) { c.Volume -= c.Info & 0x0F; if (c.Volume < 0) c.Volume = 0; }
                else { c.Volume += c.Info >> 4; if (c.Volume > 64) c.Volume = 64; }
                break;
            case 9:
                if (c.St2TremorCounter == 0)
                {
                    if (c.St2TremorState == 1) { c.St2TremorState = 0; c.Volume = 0; c.St2TremorCounter = c.Info & 0x0F; }
                    else { c.St2TremorState = 1; c.Volume = c.St2VolInitial; c.St2TremorCounter = c.Info >> 4; }
                }
                else c.St2TremorCounter--;
                break;
            default:
                c.St2TremorCounter = 0; c.St2TremorState = 1;
                if (c.Cmd == 7)
                {
                    if (c.Period != c.TargetPeriod)
                    {
                        if (c.Period > c.TargetPeriod) { c.Period -= FxMult * c.Info; if (c.Period < c.TargetPeriod) c.Period = c.TargetPeriod; }
                        else { c.Period += FxMult * c.Info; if (c.Period > c.TargetPeriod) c.Period = c.TargetPeriod; }
                        St2UpdateFreq(c);
                    }
                }
                else if (c.Cmd == 8)
                {
                    c.Period = FxMult * ((Lfo[c.St2VibCurrent >> 1] * (c.Info & 0x0F)) >> 6) + c.TargetPeriod;
                    St2UpdateFreq(c);
                    c.St2VibCurrent = (c.St2VibCurrent + ((c.Info >> 4) << 1)) & 0x7E;
                }
                else c.St2VibCurrent = 0;
                break;
        }
    }

    // ================================================================ ST3

    private void S3mStart()
    {
        SetS3mTempo(tempo);
        tickCounter = samplesPerTick;   // 첫 샘플에서 바로 첫 줄
        tick = 0;
        rowPending = true;
        visited.Clear();
    }

    private bool rowPending;

    private void SetS3mTempo(int t)
    {
        tempo = Math.Max(32, t);
        samplesPerTick = Rate * 2.5 / tempo / SpeedFactor;
    }

    private TrackerPattern PatternAt(int ord)
    {
        if (orders == null || ord < 0 || ord >= orders.Count) return null;
        int p = orders[ord];
        return p < mod.Patterns.Count ? mod.Patterns[p] : null;
    }

    private TrackerSample SampleOf(int no)
    {
        return no > 0 && no <= mod.Samples.Count ? mod.Samples[no - 1] : null;
    }

    private void S3mTick()
    {
        if (ending) return;
        if (rowPending) { S3mRow(); rowPending = false; }
        else
        {
            for (int i = 0; i < mod.Channels; i++) if (mod.IsChannelUsed(i)) S3mEffectTick(ch[i]);
        }
        tick++;
        if (tick >= speed * (patternDelayCount + 1))
        {
            tick = 0;
            patternDelayCount = 0;
            NextRow();
            rowPending = true;
        }
    }

    private void NextRow()
    {
        if (patternDelay > 0) { patternDelayCount = 0; }
        if (nextOrder >= 0 || breakRow >= 0)
        {
            int ord = nextOrder >= 0 ? nextOrder : orderIndex + 1;
            int r = breakRow >= 0 ? breakRow : 0;
            nextOrder = -1; breakRow = -1;
            GoTo(ord, r);
            return;
        }
        row++;
        if (row >= Rows) GoTo(orderIndex + 1, 0);
    }

    private void GoTo(int ord, int r)
    {
        if (singlePatternIndex >= 0) { orderIndex = 0; row = r; return; }
        if (ord >= orders.Count)
        {
            if (stopAtEnd) { ending = true; return; }
            ord = 0;
        }
        long key = ((long)ord << 8) | (uint)r;
        if (stopAtEnd && !visited.Add(key)) { ending = true; return; }   // 되돌아가는 곡: 한 바퀴만
        if (ord != orderIndex) foreach (var c in ch) { c.LoopRow = 0; c.LoopCount = 0; }   // 패턴 루프(SBx)로 돌아올 때는 횟수를 남긴다
        orderIndex = ord;
        row = Math.Min(Rows - 1, r);
    }

    private void S3mRow()
    {
        if (row == 0 && singlePatternIndex < 0) visited.Add((long)orderIndex << 8);
        shownRow = row;
        var pat = PatternAt(orderIndex);
        patternDelay = 0;
        for (int i = 0; i < mod.Channels; i++)
        {
            if (!mod.IsChannelUsed(i)) continue;
            var c = ch[i];
            var cell = pat != null ? pat[row, i] : TrackerCell.Empty;
            c.Cell = cell;
            c.Cmd = cell.Command;
            c.Info = cell.Info;
            // 효과 기억: 0이면 앞 값 (G · H/U는 따로)
            if (c.Cmd != 0)
            {
                if (c.Info != 0) { if (c.Cmd != 7 && c.Cmd != 8 && c.Cmd != 21) c.LastInfo = c.Info; }
                else if (c.Cmd != 7 && c.Cmd != 8 && c.Cmd != 21 && c.Cmd != 1 && c.Cmd != 2 && c.Cmd != 3 && c.Cmd != 20) c.Info = c.LastInfo;
            }
            c.CutTick = -1;
            c.DelayTicks = -1;
            if (c.Cmd == 19 && (c.Info >> 4) == 0xD && (c.Info & 15) > 0) { c.DelayTicks = c.Info & 15; c.DelayedCell = cell; }
            else S3mTrigger(c, cell, 0);
            S3mRowEffect(c);
        }
        if (patternDelay > 0) patternDelayCount = patternDelay;
    }

    private int St3PeriodFor(int note, int c2spd)
    {
        int oct = note >> 4, semi = note & 15;
        if (semi >= 12) semi = 11;
        if (c2spd <= 0) c2spd = 8363;
        return (int)(8363L * 16 * (St3Period[semi] >> oct) / c2spd);
    }

    private int TransposedNote(int channel, int note, int maxOct)
    {
        bool drum = false;
        int t = options.VoiceTranspose[Math.Min(channel, options.VoiceTranspose.Length - 1)] + (drum ? 0 : options.Transpose);
        if (t == 0) return note;
        int n = (note >> 4) * 12 + (note & 15) + t;
        n = Math.Max(0, Math.Min((maxOct + 1) * 12 - 1, n));
        return ((n / 12) << 4) | (n % 12);
    }

    private void S3mTrigger(Chan c, TrackerCell cell, int atTick)
    {
        bool porta = c.Cmd == 7 || c.Cmd == 12;
        if (cell.Instrument != 0)
        {
            var s = SampleOf(cell.Instrument);
            if (s != null && !s.IsEmpty)
            {
                c.SampleNo = cell.Instrument;
                if (!porta || c.Sample == null) c.Sample = s;
                c.Volume = s.Volume;
                if (c.Adlib && s.IsAdlib) AdlibLoad(c, s);
            }
        }
        if (cell.Note == TrackerCell.NoteOff)
        {
            c.Active = false;
            AdlibOff(c);
            c.ShownNote = -1;
        }
        else if (cell.Note != TrackerCell.NoNote && c.Sample != null)
        {
            int note = TransposedNote(c.Index, cell.Note, 7);
            int period = St3PeriodFor(note, c.Sample.C2Spd);
            if (porta && (c.Active || c.KeyOn)) c.TargetPeriod = period;
            else if (!options.VoiceMute[c.Index])
            {
                c.Period = c.TargetPeriod = c.BasePeriod = period;
                c.Note = note;
                c.VibPos = 0; c.TremPos = 0;
                c.RetrigCount = 0;
                c.TremorCount = 0; c.TremorOn = true;
                c.NoteOnCount++;
                c.ShownNote = cell.Note;
                if (c.Adlib)
                {
                    if (c.Sample.IsAdlib) { AdlibOff(c); c.KeyOn = true; AdlibFreq(c); }
                }
                else if (c.Sample.IsSample)
                {
                    c.Pos = 0;
                    if (c.Cmd == 15)   // Oxx 오프셋
                    {
                        int off = c.Info * 256;
                        var s = c.Sample;
                        if (s.Loop && off >= s.LoopEnd && s.LoopEnd > s.LoopStart) off = s.LoopStart + (off - s.LoopStart) % (s.LoopEnd - s.LoopStart);
                        c.Pos = off < s.Length ? off : s.Length;
                    }
                    c.Active = true;
                    UpdateStep(c);
                }
            }
        }
        if (cell.Volume != TrackerCell.NoVolume) c.Volume = Math.Min(64, (int)cell.Volume);
        c.OutVolume = c.Volume;
        if (c.Adlib) AdlibVolume(c);
    }

    private void UpdateStep(Chan c)
    {
        if (c.Period <= 0) { c.Step = 0; return; }
        c.Step = St3Clock / c.Period / Rate;
    }

    private bool FastSlides { get { return mod.TrackerVersion == 0x1300 || (mod.Flags & 64) != 0; } }

    private void S3mRowEffect(Chan c)
    {
        int x = c.Info >> 4, y = c.Info & 15;
        switch (c.Cmd)
        {
            case 1: if (c.Info > 0) speed = c.Info; break;                     // A
            case 2: nextOrder = c.Info; if (breakRow < 0) breakRow = 0; break;   // B
            case 3: breakRow = x * 10 + y; if (breakRow > 63) breakRow = 0; if (nextOrder < 0) nextOrder = orderIndex + 1; break;   // C (10진)
            case 4: VolumeSlide(c, true); break;                                // D (미세)
            case 5: if (x == 0xF) SlidePeriod(c, y * 4); else if (x == 0xE) SlidePeriod(c, y); break;      // E 미세
            case 6: if (x == 0xF) SlidePeriod(c, -y * 4); else if (x == 0xE) SlidePeriod(c, -y); break;    // F 미세
            case 7: if (c.Info != 0) c.PortaSpeed = c.Info; break;              // G
            case 8: case 21: if (x != 0) c.VibInfo = (c.VibInfo & 0x0F) | (x << 4); if (y != 0) c.VibInfo = (c.VibInfo & 0xF0) | y; Vibrato(c, c.Cmd == 21); break;
            case 9: Tremor(c); break;                                           // I
            case 10: c.Period = c.BasePeriod; UpdateStep(c); break;             // J (첫 틱 = 원음)
            case 11: if (FastSlides) VolumeSlide(c, false); break;              // K
            case 12: if (FastSlides) VolumeSlide(c, false); break;              // L
            case 17: break;                                                     // Q: 틱마다
            case 18: break;                                                     // R
            case 19:                                                            // S
                switch (x)
                {
                    case 3: c.VibWave = y & 3; break;
                    case 4: c.TremWave = y & 3; break;
                    case 8: c.Pan = y; break;
                    case 0xB:
                        if (y == 0) c.LoopRow = row;
                        else
                        {
                            if (c.LoopCount == 0) c.LoopCount = y;
                            else c.LoopCount--;
                            if (c.LoopCount > 0) { breakRow = c.LoopRow; nextOrder = orderIndex; visited.Remove(((long)orderIndex << 8) | (uint)c.LoopRow); }
                        }
                        break;
                    case 0xC: c.CutTick = y; break;
                    case 0xE: patternDelay = y; break;
                }
                break;
            case 20: if (c.Info >= 0x20) SetS3mTempo(c.Info); break;           // T
            case 22: globalVolume = Math.Min(64, c.Info); break;               // V
            case 24: c.Pan = Math.Min(15, c.Info * 15 / 0x80); break;          // X (0~80)
        }
        if (FastSlides && c.Cmd == 4) VolumeSlide(c, false);
        if (c.Cmd != 8 && c.Cmd != 21 && c.Cmd != 11 && c.Cmd != 10 && c.Period != c.BasePeriod && c.Cmd != 5 && c.Cmd != 6 && c.Cmd != 7 && c.Cmd != 12)
        { c.Period = c.BasePeriod; UpdateStep(c); }
    }

    private void S3mEffectTick(Chan c)
    {
        int x = c.Info >> 4, y = c.Info & 15;
        if (c.DelayTicks >= 0 && tick % speed == c.DelayTicks) { S3mTrigger(c, c.DelayedCell, tick); c.DelayTicks = -1; }
        if (c.CutTick >= 0 && tick % speed == c.CutTick) { c.Volume = 0; c.OutVolume = 0; c.CutTick = -1; if (c.Adlib) AdlibOff(c); }
        c.OutVolume = c.Volume;
        switch (c.Cmd)
        {
            case 4: VolumeSlide(c, false); break;
            case 5: if (c.Info < 0xE0) SlidePeriod(c, c.Info * 4); break;
            case 6: if (c.Info < 0xE0) SlidePeriod(c, -c.Info * 4); break;
            case 7: TonePorta(c); break;
            case 8: Vibrato(c, false); break;
            case 21: Vibrato(c, true); break;
            case 9: Tremor(c); break;
            case 10:
            {
                int k = tick % 3;
                int add = k == 1 ? x : k == 2 ? y : 0;
                c.Period = (int)(c.BasePeriod / Math.Pow(2, add / 12.0));
                UpdateStep(c);
                if (c.Adlib) AdlibFreq(c);
                break;
            }
            case 11: Vibrato(c, false); VolumeSlide(c, false); break;
            case 12: TonePorta(c); VolumeSlide(c, false); break;
            case 17:
                if (y > 0 && ++c.RetrigCount >= y)
                {
                    c.RetrigCount = 0;
                    c.Pos = 0; if (c.Sample != null && c.Sample.IsSample) c.Active = true;
                    int v = c.Volume;
                    if (x == 6) v = v * 2 / 3; else if (x == 7) v /= 2; else if (x == 0xE) v = v * 3 / 2; else if (x == 0xF) v *= 2; else v += RetrigAdd[x];
                    c.Volume = Math.Max(0, Math.Min(64, v));
                    c.OutVolume = c.Volume;
                }
                break;
            case 18:
            {
                int d = (Wave(c.TremWave, c.TremPos) * y) >> 6;
                c.OutVolume = Math.Max(0, Math.Min(64, c.Volume + d));
                c.TremPos = (c.TremPos + x) & 63;
                break;
            }
        }
        if (c.Adlib) AdlibVolume(c);
    }

    private void VolumeSlide(Chan c, bool firstTick)
    {
        int x = c.Info >> 4, y = c.Info & 15;
        if (firstTick)
        {
            if (y == 0xF && x != 0) c.Volume += x;          // DxF 미세 올림
            else if (x == 0xF && y != 0) c.Volume -= y;     // DFy 미세 내림
        }
        else
        {
            if (y == 0xF && x != 0) { }
            else if (x == 0xF && y != 0) { }
            else if (y == 0) c.Volume += x;
            else if (x == 0) c.Volume -= y;
        }
        c.Volume = Math.Max(0, Math.Min(64, c.Volume));
        c.OutVolume = c.Volume;
    }

    private void SlidePeriod(Chan c, int delta)
    {
        c.Period += delta;
        if (c.Period < 64) c.Period = 64;
        if (c.Period > 32000) c.Period = 32000;
        c.BasePeriod = c.Period;
        UpdateStep(c);
        if (c.Adlib) AdlibFreq(c);
    }

    private void TonePorta(Chan c)
    {
        int sp = c.PortaSpeed * 4;
        if (c.Period < c.TargetPeriod) { c.Period = Math.Min(c.TargetPeriod, c.Period + sp); }
        else if (c.Period > c.TargetPeriod) { c.Period = Math.Max(c.TargetPeriod, c.Period - sp); }
        c.BasePeriod = c.Period;
        UpdateStep(c);
        if (c.Adlib) AdlibFreq(c);
    }

    private void Vibrato(Chan c, bool fine)
    {
        int sp = c.VibInfo >> 4, depth = c.VibInfo & 15;
        int d = (Wave(c.VibWave, c.VibPos) * depth) >> 7;
        if (!fine) d *= 4;
        c.Period = c.BasePeriod + d;
        UpdateStep(c);
        if (c.Adlib) AdlibFreq(c);
        c.VibPos = (c.VibPos + sp) & 63;
    }

    private void Tremor(Chan c)
    {
        int on = (c.Info >> 4) + 1, off = (c.Info & 15) + 1;
        c.TremorCount++;
        if (c.TremorOn && c.TremorCount >= on) { c.TremorOn = false; c.TremorCount = 0; }
        else if (!c.TremorOn && c.TremorCount >= off) { c.TremorOn = true; c.TremorCount = 0; }
        c.OutVolume = c.TremorOn ? c.Volume : 0;
    }

    private static readonly Random rnd = new Random(1);

    private static int Wave(int wave, int pos)
    {
        switch (wave)
        {
            case 1: return 255 - pos * 8;               // 내려가는 톱니
            case 2: return pos < 32 ? 255 : -255;       // 사각
            case 3: return rnd.Next(-255, 256);         // 무작위
            default: return Lfo[pos & 63];
        }
    }

    // ================================================================ AdLib (S3M A1~A9 → OPL2 채널 0~8)

    private static readonly int[] ModOff = { 0, 1, 2, 8, 9, 10, 16, 17, 18 };
    private static readonly int[] CarOff = { 3, 4, 5, 11, 12, 13, 19, 20, 21 };

    private void AdlibLoad(Chan c, TrackerSample s)
    {
        if (c.AdlibCh < 0) return;
        int k = c.AdlibCh;
        var a = s.Adlib;
        opl.Write(0x20 + ModOff[k], a[0]); opl.Write(0x20 + CarOff[k], a[1]);
        opl.Write(0x40 + ModOff[k], a[2]); opl.Write(0x40 + CarOff[k], a[3]);
        opl.Write(0x60 + ModOff[k], a[4]); opl.Write(0x60 + CarOff[k], a[5]);
        opl.Write(0x80 + ModOff[k], a[6]); opl.Write(0x80 + CarOff[k], a[7]);
        opl.Write(0xE0 + ModOff[k], a[8] & 3); opl.Write(0xE0 + CarOff[k], a[9] & 3);
        opl.Write(0xC0 + k, a[10] & 0x0F);
    }

    private void AdlibVolume(Chan c)
    {
        if (c.AdlibCh < 0 || c.Sample == null || !c.Sample.IsAdlib) return;
        int k = c.AdlibCh;
        var a = c.Sample.Adlib;
        int vol = options.VoiceMute[c.Index] ? 0 : c.OutVolume * globalVolume / 64;
        int car = a[3] & 0x3F;
        opl.Write(0x40 + CarOff[k], (a[3] & 0xC0) | (63 - (63 - car) * vol / 64));
        if ((a[10] & 1) != 0)
        {
            int mod = a[2] & 0x3F;
            opl.Write(0x40 + ModOff[k], (a[2] & 0xC0) | (63 - (63 - mod) * vol / 64));
        }
    }

    private void AdlibFreq(Chan c)
    {
        if (c.AdlibCh < 0 || c.Period <= 0) return;
        int k = c.AdlibCh;
        // ST3: 주기 → "샘플 주파수"를 C4Spd 기준 비율로 → 실제 음높이 (C-4 = 가운데 도)
        double hz = St3Clock / c.Period / 8363.0 * 261.6256;
        int block, fnum = 1023;
        for (block = 0; block < 8; block++)
        {
            fnum = (int)Math.Round(hz * (1 << (20 - block)) / 49716.0);
            if (fnum <= 1023) break;
        }
        if (block > 7) block = 7;
        fnum = Math.Max(0, Math.Min(1023, fnum));
        oplFnum[k] = fnum; oplBlock[k] = block;
        opl.Write(0xA0 + k, fnum & 0xFF);
        opl.Write(0xB0 + k, (c.KeyOn ? 0x20 : 0) | (block << 2) | (fnum >> 8));
    }

    private void AdlibOff(Chan c)
    {
        if (!c.Adlib || c.AdlibCh < 0) { c.KeyOn = false; return; }
        int k = c.AdlibCh;
        c.KeyOn = false;
        opl.Write(0xB0 + k, (oplBlock[k] << 2) | (oplFnum[k] >> 8));
    }

    // ================================================================ 상태 (Jukebox · 편집기)

    public void CopyStates(VoiceState[] dst)
    {
        if (mod == null) return;
        for (int i = 0; i < dst.Length && i < ch.Length; i++)
        {
            var c = ch[i];
            bool sounding = c.Adlib ? c.KeyOn : c.Active;
            dst[i] = new VoiceState
            {
                Note = sounding && c.ShownNote >= 0 ? NoteNumber(c.ShownNote) : -1,
                Volume = c.OutVolume / 64f,
                NoteOnCount = c.NoteOnCount,
                Instrument = c.SampleNo > 0 && c.SampleNo <= mod.Samples.Count ? SampleLabel(mod.Samples[c.SampleNo - 1]) : null,
                InstrumentFound = true,
            };
        }
    }

    private static string SampleLabel(TrackerSample s)
    {
        string n = (s.Name ?? "").Trim();
        return n.Length > 0 ? n : (s.FileName ?? "").Trim();
    }

    /// <summary>칸 음 → MIDI 번호 (표시용). S3M C-4 = 60, STM C-2 = 60.</summary>
    private int NoteNumber(int note)
    {
        int n = (note >> 4) * 12 + (note & 15);
        return mod.Kind == TrackerKind.Stm ? n + 36 : n + 12;
    }

    public void ApplyMute(int channel)
    {
        if (channel < 0 || channel >= ch.Length) return;
        if (options.VoiceMute[channel]) { ch[channel].Active = false; AdlibOff(ch[channel]); }
    }
}
