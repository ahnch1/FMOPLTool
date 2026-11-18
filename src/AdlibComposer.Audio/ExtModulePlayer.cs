using System;
using System.Collections.Generic;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// XM (FastTracker 2) · IT (Impulse Tracker) 재생기 (44.1kHz 스테레오, 선형 보간, 볼륨 램프).
/// <list type="bullet">
/// <item>악기: 볼륨 · 팬 · 음높이 엔벨로프 (서스테인 · 루프), 키 오프 뒤 페이드아웃, 자동 진동, IT 음 바꾸기 · 기본 팬 · 음높이-팬 분리</item>
/// <item>IT 새 음 처리(NNA: 컷 · 이어서 · 노트 오프 · 페이드)와 중복 검사(DCT/DCA): 앞 음은 배경 보이스로 계속 울린다</item>
/// <item>주파수: 선형(1/64 반음 단위) 또는 아미가 주기, 샘플 루프 · 핑퐁 · IT 서스테인 루프</item>
/// <item>효과: XM 0~F · E · G H K L P R T X, IT A~Y (Z 매크로 · 레조넌스 필터는 내지 않음), 볼륨 칸 전부</item>
/// </list>
/// 재생 위치는 지난 시간(ms). 곡 끝 · 되돌아가는 곳에서 멈춘다 (Jukebox).
/// </summary>
public sealed class ExtModulePlayer
{
    public const int Rate = 44100;
    private const int RampSamples = 64;
    private const double AmigaConst = 14317456.0;   // 1712 × 8363: C-4 = 주기 1712 (×4 해상도)
    private const int MaxVoices = 160;

    private sealed class Voice
    {
        public int Channel;
        public ExtSample Sample;
        public ExtInstrument Ins;
        public int InsNo, SampleNo, Note;
        public double Pos;
        public bool Backward;
        public bool Active;
        public double Step;
        public bool Released, Fading;
        public double Fade = 1;
        public int VolEnvTick, PanEnvTick, PitchEnvTick;
        public bool VolEnvOn = true, PanEnvOn = true, PitchEnvOn = true;
        public int AutoVibPos, AutoVibAmp;
        public bool Background;
        // 배경 보이스: 마지막 값 그대로
        public double Pitch;       // 선형 단위 또는 주기
        public int Volume = 64, ChanVolume = 64, Pan = 32;
        // 램프
        public double CurL, CurR, TgtL, TgtR, StepL, StepR;
        public int RampLeft;
        public bool Stopping;
        public int Nna;
    }

    private sealed class Chan
    {
        public int Index;
        public Voice V;
        public int InsNo, SampleNo, Note = -1, RealNote = -1;
        public ExtSample Sample;
        public int Volume = 64, ChanVolume = 64, Pan = 32;
        public bool Surround;
        /// <summary>미끄럼 · 포르타를 반영한 음높이 (선형: 1/64 반음, 아미가: 주기), 진동 뺀 것.</summary>
        public double Pitch, TargetPitch;
        public double PitchAdd;    // 이번 틱 진동 · 아르페지오
        public int VolAdd;         // 이번 틱 트레몰로
        public int PanAdd;
        public bool TremorMute;
        public ExtCell Cell;
        public int Fx, Param;      // 이번 줄 효과 (XM = 효과 번호 0~35, IT = 1~26) — 없으면 −1
        // 메모리
        public int MemPortaUp, MemPortaDown, MemVolSlide, MemFineUp, MemFineDown, MemXFineUp, MemXFineDown, MemFineVolUp, MemFineVolDown;
        public int MemOffset, MemHighOffset, MemGlobalSlide, MemPanSlide, MemRetrig, MemTremor, MemChanVolSlide, MemTempo, MemArp, MemVolCol;
        public int VibSpeed, VibDepth, VibPos, VibWave, TremSpeed, TremDepth, TremPos, TremWave, PanbSpeed, PanbDepth, PanbPos, PanbWave;
        public int TonePortaSpeed;
        public int TremorCount; public bool TremorOn = true;
        public int RetrigCount;
        public int LoopRow, LoopCount;
        public int DelayTick = -1; public ExtCell Delayed;
        public int CutTick = -1, KeyOffTick = -1;
        public int NnaOverride = -1;
        public bool PortaRow;
        // 표시
        public int NoteOnCount;
        public int ShownNote = -1;
    }

    private ExtModule mod;
    private readonly Chan[] ch = new Chan[ExtModule.MaxChannels];
    private readonly List<Voice> voices = new List<Voice>();
    private List<int> orders;
    private int orderIndex, row, tick, speed, tempo, globalVolume;
    private int nextOrder = -1, breakRow = -1;
    private int patternDelay, patternDelayLeft, extraTicks;
    private bool rowRepeat;
    private double samplesPerTick, tickCounter;
    private bool playing, ending;
    private int tail;
    private long elapsedSamples;
    private readonly HashSet<long> visited = new HashSet<long>();
    private PlaybackOptions options = new PlaybackOptions();
    private int shownRow;
    private static readonly Random rnd = new Random(7);

    public bool IsPlaying { get { return playing; } }
    public ExtModule Module { get { return mod; } }
    public PlaybackOptions Options { get { return options; } }
    public int CurrentOrder { get { return orders != null && orderIndex < orders.Count ? orderIndex : 0; } }
    public int CurrentPattern { get { return orders != null && orderIndex < orders.Count ? orders[orderIndex] : 0; } }
    public int CurrentRow { get { return shownRow; } }
    public int Speed { get { return speed; } }
    public int Tempo { get { return tempo; } }
    public double PositionMs { get { return elapsedSamples * 1000.0 / Rate; } }

    public ExtModulePlayer()
    {
        for (int i = 0; i < ch.Length; i++) ch[i] = new Chan { Index = i };
    }

    // ================================================================ 시작 / 정지

    public void Start(ExtModule m, PlaybackOptions opts = null, double skipMs = 0)
    {
        mod = m;
        options = opts ?? new PlaybackOptions();
        orders = m.PlayOrders();
        BuildOrderMap(m);
        if (orders.Count == 0) { orders.Add(0); if (m.Patterns.Count == 0) m.Patterns.Add(new ExtPattern(64, Math.Max(1, m.Channels))); }
        voices.Clear();
        for (int i = 0; i < ch.Length; i++)
        {
            int n = ch[i].NoteOnCount;
            ch[i] = new Chan { Index = i, NoteOnCount = n, Pan = m.ChannelPan[i], ChanVolume = m.ChannelVolume[i], Surround = m.ChannelSurround[i] };
        }
        speed = Math.Max(1, m.Speed);
        SetTempo(m.Tempo);
        globalVolume = m.GlobalVolume;
        orderIndex = 0; row = 0; tick = 0;
        nextOrder = -1; breakRow = -1; patternDelay = 0; patternDelayLeft = 0; extraTicks = 0; rowRepeat = false;
        elapsedSamples = 0; ending = false; tail = 0;
        visited.Clear();
        visited.Add(0);
        tickCounter = samplesPerTick;   // 첫 샘플에서 첫 줄
        playing = true;
        if (skipMs > 0) FastForward(skipMs);
    }

    /// <summary>원래 주문 번호 → 재생 목록 번호 (건너뜀 254 · 없는 패턴을 뺀 목록): 그 자리부터 처음 나오는 재생할 주문.</summary>
    private int[] orderMap = new int[0];

    private void BuildOrderMap(ExtModule m)
    {
        orderMap = new int[m.Orders.Count + 1];
        int play = 0;
        bool ended = false;
        for (int i = 0; i < m.Orders.Count; i++)
        {
            orderMap[i] = ended ? int.MaxValue : play;
            int o = m.Orders[i];
            if (o == 255) ended = true;
            else if (o != 254 && o < m.Patterns.Count) play++;
        }
        orderMap[m.Orders.Count] = int.MaxValue;
    }

    private int OrderFor(int raw) { return raw >= 0 && raw < orderMap.Length ? orderMap[raw] : int.MaxValue; }

    public void Stop()
    {
        playing = false;
        voices.Clear();
        foreach (var c in ch) { c.V = null; c.ShownNote = -1; }
    }

    private void SetTempo(int t)
    {
        tempo = Math.Max(32, Math.Min(255, t));
        samplesPerTick = Rate * 2.5 / tempo / SpeedFactor;
    }

    public void ApplySpeed() { if (mod != null) SetTempo(tempo); }

    private double SpeedFactor { get { return options.Speed < 0.05 ? 0.05 : options.Speed; } }

    private void FastForward(double ms)
    {
        long target = (long)(ms * Rate / 1000.0);
        while (playing && !ending && elapsedSamples < target) FastTick();
        // 울리던 음은 이어서 (샘플 위치는 처음부터) — 오래 끄는 음은 놓친다
        foreach (var v in voices) { v.CurL = v.TgtL; v.CurR = v.TgtR; v.RampLeft = 0; }
    }

    private void FastTick()
    {
        Tick();
        // 샘플 위치도 틱만큼 (루프 · 끝 처리)
        int n = (int)Math.Max(1, Math.Round(samplesPerTick));
        foreach (var v in voices) if (v.Active) Advance(v, n);
        voices.RemoveAll(v => !v.Active);
        elapsedSamples += n;
        tickCounter = 0;
    }

    /// <summary>곡 길이 (초): 처음부터 끝(또는 되돌아오는 곳)까지 빨리 돌려 잰다. 최대 30분.</summary>
    public static double MeasureSeconds(ExtModule m)
    {
        var p = new ExtModulePlayer();
        p.Start(m);
        long limit = (long)Rate * 60 * 30;
        while (p.playing && !p.ending && p.elapsedSamples < limit) { p.Tick(); p.elapsedSamples += (long)Math.Max(1, Math.Round(p.samplesPerTick)); }
        return p.elapsedSamples / (double)Rate;
    }

    // ================================================================ 샘플 단위

    public void Generate(out double left, out double right)
    {
        left = right = 0;
        if (mod == null || !playing) return;
        elapsedSamples++;
        tickCounter += 1.0;
        if (tickCounter >= samplesPerTick) { tickCounter -= samplesPerTick; Tick(); }
        if (ending && ++tail > Rate / 2) playing = false;
        double l = 0, r = 0;
        for (int i = 0; i < voices.Count; i++)
        {
            var v = voices[i];
            if (!v.Active) continue;
            double s = Mix(v);
            if (v.RampLeft > 0) { v.CurL += v.StepL; v.CurR += v.StepR; if (--v.RampLeft == 0) { v.CurL = v.TgtL; v.CurR = v.TgtR; if (v.Stopping) v.Active = false; } }
            l += s * v.CurL;
            r += s * v.CurR;
        }
        if (voices.Count > 0 && (elapsedSamples & 255) == 0) voices.RemoveAll(v => !v.Active);
        double gain = 32768 * Gain;
        left = l * gain;
        right = r * gain;
    }

    /// <summary>섞는 크기: IT 믹싱 볼륨 · 채널 수에 따라 (많이 울려도 덜 찢어지게).</summary>
    private double Gain
    {
        get
        {
            double mv = mod.IsIt ? Math.Max(16, mod.MixVolume) / 48.0 : 1.0;
            return 0.55 * mv * 2.0 / Math.Sqrt(Math.Max(4, mod.Channels));
        }
    }

    private double Mix(Voice v)
    {
        var s = v.Sample;
        var d = s.Data;
        int n = d.Length;
        if (n == 0) { v.Active = false; return 0; }
        int ip = (int)v.Pos;
        if (ip >= n) ip = n - 1;
        if (ip < 0) ip = 0;
        double frac = v.Pos - Math.Floor(v.Pos);
        int next;
        if (v.Backward) { next = ip - 1; frac = 1 - frac; if (next < 0) next = 0; double a = d[ip], b = d[next]; Advance(v, 1); return (a + (b - a) * (1 - frac)) / 32768.0; }
        next = ip + 1;
        GetLoop(v, out bool loop, out bool ping, out int ls, out int le);
        if (next >= (loop ? le : n)) next = loop ? (ping ? ip : ls) : ip;
        double val = (d[ip] + (d[Math.Min(n - 1, next)] - d[ip]) * frac) / 32768.0;
        Advance(v, 1);
        return val;
    }

    private static void GetLoop(Voice v, out bool loop, out bool ping, out int ls, out int le)
    {
        var s = v.Sample;
        if (s.SustainLoop && !v.Released) { loop = true; ping = s.SustainPingPong; ls = s.SustainStart; le = s.SustainEnd; return; }
        loop = s.Loop; ping = s.PingPong; ls = s.LoopStart; le = s.LoopEnd;
    }

    /// <summary>재생 위치를 n 샘플 진행 (루프 · 핑퐁 · 끝).</summary>
    private static void Advance(Voice v, int n)
    {
        double step = v.Step * n;
        GetLoop(v, out bool loop, out bool ping, out int ls, out int le);
        int len = v.Sample.Data.Length;
        if (v.Backward)
        {
            v.Pos -= step;
            if (v.Pos < ls) { v.Pos = ls + (ls - v.Pos); v.Backward = false; if (v.Pos >= le) v.Pos = ls; }
            return;
        }
        v.Pos += step;
        if (loop && le > ls)
        {
            if (v.Pos >= le)
            {
                if (ping)
                {
                    double over = v.Pos - le;
                    int span = le - ls;
                    over %= 2.0 * span;
                    if (over < span) { v.Pos = le - 1 - over; v.Backward = true; if (v.Pos < ls) v.Pos = ls; }
                    else v.Pos = ls + (over - span);
                }
                else v.Pos = ls + (v.Pos - le) % (le - ls);
            }
        }
        else if (v.Pos >= len) v.Active = false;
    }

    // ================================================================ 틱

    private void Tick()
    {
        if (ending) { foreach (var v in voices) StopVoice(v); return; }
        if (tick == 0 && !rowRepeat) ProcessRow();
        else
        {
            for (int i = 0; i < mod.Channels; i++) if (!mod.ChannelMuted[i]) EffectTick(ch[i]);
        }
        UpdateVoices();
        tick++;
        if (tick >= speed + extraTicks)
        {
            tick = 0;
            if (patternDelayLeft > 0) { patternDelayLeft--; rowRepeat = true; return; }
            rowRepeat = false;
            extraTicks = 0;
            NextRow();
        }
    }

    private ExtPattern CurrentPat { get { return orders != null && orderIndex < orders.Count && orders[orderIndex] < mod.Patterns.Count ? mod.Patterns[orders[orderIndex]] : null; } }

    private void NextRow()
    {
        if (nextOrder >= 0 || breakRow >= 0)
        {
            int ord = nextOrder >= 0 ? nextOrder : orderIndex + 1;
            int r = breakRow >= 0 ? breakRow : 0;
            nextOrder = -1; breakRow = -1;
            GoTo(ord, r);
            return;
        }
        row++;
        var pat = CurrentPat;
        if (pat == null || row >= pat.Rows) GoTo(orderIndex + 1, 0);
        else if (!visited.Add(((long)orderIndex << 16) | (uint)row)) { }   // 같은 줄 두 번 (패턴 루프) — 아래 GoTo에서만 끝을 본다
    }

    private void GoTo(int ord, int r)
    {
        if (ord >= orders.Count) { ending = true; return; }
        var pat = ord < orders.Count && orders[ord] < mod.Patterns.Count ? mod.Patterns[orders[ord]] : null;
        if (pat != null && r >= pat.Rows) r = 0;
        long key = ((long)ord << 16) | (uint)r;
        if (!visited.Add(key)) { ending = true; return; }   // 되돌아가는 곡: 한 바퀴만
        if (ord != orderIndex) foreach (var c in ch) { c.LoopRow = 0; c.LoopCount = 0; }
        orderIndex = ord;
        row = r;
    }

    private void ProcessRow()
    {
        var pat = CurrentPat;
        shownRow = row;
        patternDelay = 0;
        for (int i = 0; i < mod.Channels; i++)
        {
            var c = ch[i];
            var cell = pat != null && i < pat.Channels && row < pat.Rows ? pat[row, i] : default(ExtCell);
            if (mod.ChannelMuted[i]) continue;
            c.Cell = cell;
            c.Fx = cell.Effect == 0 ? -1 : mod.IsIt ? cell.Effect : cell.Effect - 1;
            c.Param = cell.Param;
            c.PitchAdd = 0; c.VolAdd = 0; c.PanAdd = 0;
            c.CutTick = -1; c.KeyOffTick = -1; c.DelayTick = -1;
            if (!mod.IsIt && c.Fx == 0 && c.Param == 0) c.Fx = -1;
            int delay = NoteDelay(c);
            if (delay > 0) { c.DelayTick = delay; c.Delayed = cell; }
            else { Trigger(c, cell); VolumeColumnRow(c, cell); }
            if (mod.IsIt) ItRowEffect(c); else XmRowEffect(c);
        }
        if (patternDelay > 0) patternDelayLeft = patternDelay;
    }

    private int NoteDelay(Chan c)
    {
        if (mod.IsIt) { if (c.Fx == 19 && (c.Param >> 4) == 0xD) return Math.Max(1, c.Param & 15); }
        else if (c.Fx == 0xE && (c.Param >> 4) == 0xD) return c.Param & 15;
        return 0;
    }

    private bool IsTonePorta(Chan c, ExtCell cell)
    {
        if (cell.VolCmd == ExtVolCmd.TonePorta) return true;
        return mod.IsIt ? c.Fx == 7 || c.Fx == 12 : c.Fx == 3 || c.Fx == 5;
    }

    // ================================================================ 음 시작

    private void Trigger(Chan c, ExtCell cell)
    {
        int note = cell.Note, insNo = cell.Instrument;
        bool porta = IsTonePorta(c, cell);
        c.PortaRow = porta;
        if (note == ExtModule.NoteCut) { if (c.V != null) { StopVoice(c.V); c.V = null; } c.ShownNote = -1; return; }
        if (note == ExtModule.NoteOff) { KeyOff(c); if (insNo == 0) return; note = 0; if (!mod.IsIt) return; }
        if (note == ExtModule.NoteFade) { if (c.V != null) c.V.Fading = true; return; }

        if (insNo > 0)
        {
            bool valid = mod.IsIt && !mod.UseInstruments ? mod.SampleAt(insNo) != null : mod.InstrumentAt(insNo) != null;
            if (!valid && !mod.IsIt) { if (c.V != null) { StopVoice(c.V); c.V = null; } c.InsNo = insNo; return; }   // FT2: 없는 악기 = 컷
            c.InsNo = insNo;
        }

        if (note >= 1 && note <= 120)
        {
            int n = note - 1;
            ExtInstrument ins = null;
            int smpNo, realNote = n;
            if (mod.IsIt && !mod.UseInstruments) smpNo = c.InsNo;
            else
            {
                ins = mod.InstrumentAt(c.InsNo);
                if (ins == null) return;
                smpNo = ins.KeySample[n];
                if (mod.IsIt) realNote = ins.KeyNote[n];
            }
            var smp = mod.SampleAt(smpNo);
            realNote = Math.Max(0, Math.Min(119, realNote + Transpose(c.Index)));
            if (porta && c.V != null && c.V.Active && !c.V.Stopping && c.Sample != null)
            {
                c.TargetPitch = PitchOf(realNote, c.Sample);
                c.Note = n; c.RealNote = realNote;
                if (insNo > 0) ResetInstrumentVolume(c, c.Sample, ins);
                return;
            }
            if (smp == null || smp.Length == 0)
            {
                if (c.V != null) { StopVoice(c.V); c.V = null; }
                return;
            }
            NewNoteAction(c, ins, smpNo, realNote);
            c.Sample = smp; c.SampleNo = smpNo;
            c.Note = n; c.RealNote = realNote;
            c.Pitch = c.TargetPitch = PitchOf(realNote, smp);
            if (insNo > 0) ResetInstrumentVolume(c, smp, ins);
            if (mod.IsIt && ins != null && ins.PitchPanSeparation != 0)
                c.Pan = Clamp(c.Pan + (realNote - ins.PitchPanCenter) * ins.PitchPanSeparation / 8, 0, 64);
            var v = new Voice { Channel = c.Index, Sample = smp, Ins = ins, InsNo = c.InsNo, SampleNo = smpNo, Note = realNote, Active = true, Nna = c.NnaOverride >= 0 ? c.NnaOverride : ins != null ? ins.Nna : 0 };
            c.NnaOverride = -1;
            AddVoice(v);
            c.V = v;
            if ((c.VibWave & 4) == 0) c.VibPos = 0;
            if ((c.TremWave & 4) == 0) c.TremPos = 0;
            c.TremorCount = 0; c.TremorOn = true;
            c.RetrigCount = 0;
            c.NoteOnCount++;
            c.ShownNote = realNote;
            // 오프셋 (XM 9xx · IT Oxx)
            ApplyOffset(c);
        }
        else if (insNo > 0)
        {
            // 음 없이 악기만: 볼륨 · 팬 처음 값, 엔벨로프 다시 (FT2), IT는 볼륨만
            var ins = mod.IsIt && !mod.UseInstruments ? null : mod.InstrumentAt(insNo);
            var smp = c.Sample;
            if (!mod.IsIt && ins != null && c.Note >= 0) smp = mod.SampleAt(ins.KeySample[Math.Min(119, c.Note)]) ?? smp;
            if (mod.IsIt && !mod.UseInstruments) smp = mod.SampleAt(insNo) ?? smp;
            if (smp != null) ResetInstrumentVolume(c, smp, ins);
            if (!mod.IsIt && c.V != null && c.V.Active) { RestartEnvelopes(c.V); c.V.Released = false; c.V.Fading = false; c.V.Fade = 1; }
        }
    }

    private void ResetInstrumentVolume(Chan c, ExtSample smp, ExtInstrument ins)
    {
        if (smp == null) return;
        c.Volume = smp.Volume;
        if (mod.IsIt)
        {
            if (ins != null && ins.DefaultPan >= 0) c.Pan = ins.DefaultPan;
            if (smp.Pan >= 0) c.Pan = smp.Pan;
        }
        else c.Pan = smp.Pan >= 0 ? smp.Pan : 32;
        if (c.V != null && c.V.Active && !mod.IsIt) RestartEnvelopes(c.V);
    }

    private static void RestartEnvelopes(Voice v)
    {
        v.VolEnvTick = v.PanEnvTick = v.PitchEnvTick = 0;
        v.AutoVibPos = 0; v.AutoVibAmp = 0;
    }

    private int Transpose(int channel)
    {
        int t = options.Transpose;
        if (channel < options.VoiceTranspose.Length) t += options.VoiceTranspose[channel];
        return t;
    }

    /// <summary>IT: 앞 음을 배경으로 (NNA), 같은 음 · 샘플 · 악기의 배경 음 처리 (DCT/DCA). XM: 앞 음은 짧게 줄이며 끈다.</summary>
    private void NewNoteAction(Chan c, ExtInstrument ins, int smpNo, int note)
    {
        if (mod.IsIt && mod.UseInstruments && ins != null && ins.Dct != 0)
        {
            foreach (var v in voices)
            {
                if (!v.Active || !v.Background || v.Channel != c.Index || v.InsNo != c.InsNo) continue;
                bool dup = ins.Dct == 1 ? v.Note == note : ins.Dct == 2 ? v.SampleNo == smpNo : true;
                if (!dup) continue;
                if (ins.Dca == 0) StopVoice(v);
                else if (ins.Dca == 1) VoiceKeyOff(v);
                else v.Fading = true;
            }
        }
        var old = c.V;
        c.V = null;
        if (old == null || !old.Active) return;
        if (!mod.IsIt || !mod.UseInstruments || old.Nna == 0) { StopVoice(old); return; }
        old.Background = true;
        FreezeVoice(c, old);
        if (old.Nna == 2) VoiceKeyOff(old);
        else if (old.Nna == 3) old.Fading = true;
    }

    /// <summary>배경으로 보낼 때: 채널 값을 보이스에 남긴다.</summary>
    private void FreezeVoice(Chan c, Voice v)
    {
        v.Pitch = c.Pitch;
        v.Volume = c.Volume;
        v.ChanVolume = c.ChanVolume;
        v.Pan = c.Pan;
    }

    private void AddVoice(Voice v)
    {
        voices.Add(v);
        if (voices.Count <= MaxVoices) return;
        // 넘치면 가장 작은 배경 보이스부터 끈다
        Voice quiet = null;
        foreach (var o in voices) if (o.Background && o.Active && !o.Stopping && (quiet == null || o.TgtL + o.TgtR < quiet.TgtL + quiet.TgtR)) quiet = o;
        if (quiet != null) StopVoice(quiet); else voices.RemoveAt(0);
        voices.RemoveAll(o => !o.Active);
    }

    private void StopVoice(Voice v)
    {
        if (!v.Active || v.Stopping) return;
        v.Stopping = true;
        v.TgtL = v.TgtR = 0;
        v.RampLeft = RampSamples;
        v.StepL = -v.CurL / RampSamples;
        v.StepR = -v.CurR / RampSamples;
        if (v.CurL == 0 && v.CurR == 0) v.Active = false;
    }

    private void KeyOff(Chan c)
    {
        if (c.V == null) return;
        VoiceKeyOff(c.V);
        if (!mod.IsIt && (c.V.Ins == null || !c.V.Ins.VolumeEnv.Usable)) { c.Volume = 0; }   // FT2: 엔벨로프 없으면 키 오프 = 볼륨 0
    }

    private void VoiceKeyOff(Voice v)
    {
        v.Released = true;
        var ins = v.Ins;
        if (mod.IsIt)
        {
            if (ins == null) { if (!mod.UseInstruments) StopVoice(v); return; }
            if (!ins.VolumeEnv.Usable || !v.VolEnvOn || ins.VolumeEnv.Loop) v.Fading = true;
        }
        else
        {
            if (ins != null && ins.VolumeEnv.Usable) v.Fading = true;
            else if (v.Background) StopVoice(v);
        }
    }

    // ================================================================ 음높이

    /// <summary>음 번호 → 선형 단위 (음 × 64) 또는 아미가 주기.</summary>
    private double PitchOf(int note, ExtSample s)
    {
        if (mod.LinearSlides) return note * 64.0;
        double hz = s.C5Speed * Math.Pow(2, (note - 60) / 12.0);
        return AmigaConst / Math.Max(1, hz) * 1;
    }

    private double FreqOf(double pitch, double add, ExtSample s)
    {
        if (mod.LinearSlides)
        {
            double p = pitch + add;
            return s.C5Speed * Math.Pow(2, (p - 60 * 64.0) / 768.0);
        }
        double per = Math.Max(1, pitch + add);
        return AmigaConst / per;
    }

    /// <summary>위로 미끄럼 (amount = 선형 단위 = 주기 단위).</summary>
    private void SlideUp(Chan c, double amount)
    {
        if (mod.LinearSlides) c.Pitch = Math.Min(c.Pitch + amount, 119 * 64.0 + 63);
        else c.Pitch = Math.Max(40, c.Pitch - amount);
    }

    private void SlideDown(Chan c, double amount)
    {
        if (mod.LinearSlides) c.Pitch = Math.Max(c.Pitch - amount, 0);
        else c.Pitch = Math.Min(c.Pitch + amount, 120000);
    }

    private void TonePorta(Chan c)
    {
        if (c.V == null || c.TonePortaSpeed == 0) return;
        double sp = c.TonePortaSpeed * 4.0;
        if (c.Pitch < c.TargetPitch) c.Pitch = Math.Min(c.TargetPitch, c.Pitch + sp);
        else if (c.Pitch > c.TargetPitch) c.Pitch = Math.Max(c.TargetPitch, c.Pitch - sp);
    }

    /// <summary>진동 값 (−255~255): 0 사인 1 램프 2 사각 3 무작위. pos 0~63.</summary>
    private static int Wave(int wave, int pos)
    {
        pos &= 63;
        switch (wave & 3)
        {
            case 1: return 255 - pos * 8;
            case 2: return pos < 32 ? 255 : -255;
            case 3: return rnd.Next(-255, 256);
            default: return (int)Math.Round(255 * Math.Sin(pos * Math.PI / 32));
        }
    }

    private void Vibrato(Chan c, int div)
    {
        int d = Wave(c.VibWave, c.VibPos) * c.VibDepth / div;
        // 선형: 위로 = +, 아미가: 위로 = 주기 −
        c.PitchAdd += mod.LinearSlides ? -d : d;
        c.VibPos = (c.VibPos + c.VibSpeed) & 63;
    }

    private void Tremolo(Chan c)
    {
        c.VolAdd = Wave(c.TremWave, c.TremPos) * c.TremDepth / 64;
        c.TremPos = (c.TremPos + c.TremSpeed) & 63;
    }

    private void Arpeggio(Chan c, int param)
    {
        int k = tick % 3;
        int semis = k == 1 ? param >> 4 : k == 2 ? param & 15 : 0;
        if (semis == 0) return;
        if (mod.LinearSlides) c.PitchAdd += semis * 64;
        else c.PitchAdd += (c.Pitch + c.PitchAdd) * (Math.Pow(2, -semis / 12.0) - 1);
    }

    // ================================================================ 볼륨 칸

    private void VolumeColumnRow(Chan c, ExtCell cell)
    {
        int v = cell.VolValue;
        switch (cell.VolCmd)
        {
            case ExtVolCmd.Volume: c.Volume = Math.Min(64, v); break;
            case ExtVolCmd.Panning: c.Pan = Math.Min(64, v); c.Surround = false; break;
            case ExtVolCmd.FineUp:
                if (mod.IsIt) { if (v == 0) v = c.MemVolCol; else c.MemVolCol = v; }
                c.Volume = Math.Min(64, c.Volume + v); break;
            case ExtVolCmd.FineDown:
                if (mod.IsIt) { if (v == 0) v = c.MemVolCol; else c.MemVolCol = v; }
                c.Volume = Math.Max(0, c.Volume - v); break;
            case ExtVolCmd.SlideUp: case ExtVolCmd.SlideDown:
                if (mod.IsIt && v != 0) c.MemVolCol = v; break;
            case ExtVolCmd.TonePorta:
                if (mod.IsIt) { int[] t = { 0, 1, 4, 8, 16, 32, 64, 96, 128, 255 }; if (v > 0) SetTonePortaSpeed(c, t[Math.Min(9, v)]); }
                else if (v > 0) c.TonePortaSpeed = v * 16;
                break;
            case ExtVolCmd.VibratoSpeed: if (v > 0) c.VibSpeed = v; break;
            case ExtVolCmd.VibratoDepth: if (v > 0) c.VibDepth = mod.IsIt ? v * 4 : v; if (mod.IsIt && mod.OldEffects) Vibrato(c, VibDiv); break;
            case ExtVolCmd.PortaUp: case ExtVolCmd.PortaDown:
                if (mod.IsIt && v != 0) c.MemPortaUp = c.MemPortaDown = v * 4;   // E · F 메모리와 같이 (×4)
                break;
        }
    }

    private void VolumeColumnTick(Chan c, ExtCell cell)
    {
        int v = cell.VolValue;
        switch (cell.VolCmd)
        {
            case ExtVolCmd.SlideUp: if (mod.IsIt && v == 0) v = c.MemVolCol; c.Volume = Math.Min(64, c.Volume + v); break;
            case ExtVolCmd.SlideDown: if (mod.IsIt && v == 0) v = c.MemVolCol; c.Volume = Math.Max(0, c.Volume - v); break;
            case ExtVolCmd.TonePorta: TonePorta(c); break;
            case ExtVolCmd.VibratoDepth: Vibrato(c, VibDiv); break;
            case ExtVolCmd.PanSlideLeft: c.Pan = Math.Max(0, c.Pan - Math.Max(1, v / 4)); break;
            case ExtVolCmd.PanSlideRight: c.Pan = Math.Min(64, c.Pan + Math.Max(1, v / 4)); break;
            case ExtVolCmd.PortaUp: SlideUp(c, (v == 0 ? c.MemPortaUp : v * 4) * 4); break;
            case ExtVolCmd.PortaDown: SlideDown(c, (v == 0 ? c.MemPortaDown : v * 4) * 4); break;
        }
    }

    /// <summary>진동 깊이 나눔수: XM 4xy 깊이 y → /32, IT Hxy는 깊이 ×4로 두고 /128 (옛 효과는 두 배 깊게 /64), Uxy는 ×1로 /128.</summary>
    private int VibDiv { get { return !mod.IsIt ? 32 : mod.OldEffects ? 64 : 128; } }

    private void SetTonePortaSpeed(Chan c, int speedVal)
    {
        c.TonePortaSpeed = speedVal;
        if (mod.IsIt && mod.CompatGxx) c.MemPortaUp = c.MemPortaDown = speedVal;
    }

    // ================================================================ XM 효과

    private void XmRowEffect(Chan c)
    {
        int p = c.Param, x = p >> 4, y = p & 15;
        switch (c.Fx)
        {
            case 1: if (p != 0) c.MemPortaUp = p; break;
            case 2: if (p != 0) c.MemPortaDown = p; break;
            case 3: if (p != 0) c.TonePortaSpeed = p; break;
            case 4: if (x != 0) c.VibSpeed = x; if (y != 0) c.VibDepth = y; break;
            case 5: case 6: case 0xA: if (p != 0) c.MemVolSlide = p; break;
            case 7: if (x != 0) c.TremSpeed = x; if (y != 0) c.TremDepth = y; break;
            case 8: c.Pan = p * 64 / 255; break;
            case 9: break;   // 음 시작에서
            case 0xB: nextOrder = OrderFor(p); if (breakRow < 0) breakRow = 0; break;
            case 0xC: c.Volume = Math.Min(64, p); break;
            case 0xD: breakRow = x * 10 + y; if (nextOrder < 0) nextOrder = orderIndex + 1; break;
            case 0xE:
                switch (x)
                {
                    case 1: if (y != 0) c.MemFineUp = y; SlideUp(c, c.MemFineUp * 4); break;
                    case 2: if (y != 0) c.MemFineDown = y; SlideDown(c, c.MemFineDown * 4); break;
                    case 4: c.VibWave = y & 7; break;
                    case 6: PatternLoop(c, y); break;
                    case 7: c.TremWave = y & 7; break;
                    case 8: c.Pan = y * 64 / 15; break;
                    case 0xA: if (y != 0) c.MemFineVolUp = y; c.Volume = Math.Min(64, c.Volume + c.MemFineVolUp); break;
                    case 0xB: if (y != 0) c.MemFineVolDown = y; c.Volume = Math.Max(0, c.Volume - c.MemFineVolDown); break;
                    case 0xC: c.CutTick = y; if (y == 0) c.Volume = 0; break;
                    case 0xE: if (patternDelayLeft == 0) patternDelay = y; break;
                }
                break;
            case 0xF: if (p == 0) break; if (p < 0x20) speed = p; else SetTempo(p); break;
            case 16: globalVolume = Math.Min(128, p * 2); break;
            case 17: if (p != 0) c.MemGlobalSlide = p; break;
            case 20: c.KeyOffTick = p; if (p == 0) KeyOff(c); break;
            case 21: if (c.V != null) { c.V.VolEnvTick = p; c.V.PanEnvTick = p; } break;
            case 25: if (p != 0) c.MemPanSlide = p; break;
            case 27: if (x != 0) c.MemRetrig = (c.MemRetrig & 0x0F) | (x << 4); if (y != 0) c.MemRetrig = (c.MemRetrig & 0xF0) | y; break;
            case 29: if (p != 0) c.MemTremor = p; break;
            case 33:
                if (x == 1) { if (y != 0) c.MemXFineUp = y; SlideUp(c, c.MemXFineUp); }
                else if (x == 2) { if (y != 0) c.MemXFineDown = y; SlideDown(c, c.MemXFineDown); }
                break;
        }
    }

    private void XmEffectTick(Chan c)
    {
        int p = c.Param, x = p >> 4, y = p & 15;
        switch (c.Fx)
        {
            case 0: Arpeggio(c, p); break;
            case 1: SlideUp(c, c.MemPortaUp * 4); break;
            case 2: SlideDown(c, c.MemPortaDown * 4); break;
            case 3: TonePorta(c); break;
            case 4: Vibrato(c, 32); break;
            case 5: TonePorta(c); XmVolSlide(c); break;
            case 6: Vibrato(c, 32); XmVolSlide(c); break;
            case 7: Tremolo(c); break;
            case 0xA: XmVolSlide(c); break;
            case 0xE:
                if (x == 9 && y > 0 && tick % y == 0) Retrigger(c);
                else if (x == 0xC && tick == y) c.Volume = 0;
                break;
            case 17:
            {
                int gx = c.MemGlobalSlide >> 4, gy = c.MemGlobalSlide & 15;
                if (gx != 0) globalVolume = Math.Min(128, globalVolume + gx * 2); else globalVolume = Math.Max(0, globalVolume - gy * 2);
                break;
            }
            case 20: if (tick == c.KeyOffTick) KeyOff(c); break;
            case 25:
            {
                int px = c.MemPanSlide >> 4, py = c.MemPanSlide & 15;
                if (px != 0) c.Pan = Math.Min(64, c.Pan + Math.Max(1, px / 4)); else c.Pan = Math.Max(0, c.Pan - Math.Max(1, py / 4));
                break;
            }
            case 27:
            {
                int ry = c.MemRetrig & 15, rx = c.MemRetrig >> 4;
                if (ry > 0 && ++c.RetrigCount >= ry) { c.RetrigCount = 0; Retrigger(c); c.Volume = RetrigVolume(c.Volume, rx); }
                break;
            }
            case 29:
            {
                int on = (c.MemTremor >> 4) + 1, off = (c.MemTremor & 15) + 1;
                Tremor(c, on, off);
                break;
            }
        }
    }

    private void XmVolSlide(Chan c)
    {
        int x = c.MemVolSlide >> 4, y = c.MemVolSlide & 15;
        if (x != 0) c.Volume = Math.Min(64, c.Volume + x); else c.Volume = Math.Max(0, c.Volume - y);
    }

    // ================================================================ IT 효과

    private void ItRowEffect(Chan c)
    {
        int p = c.Param, x = p >> 4, y = p & 15;
        switch (c.Fx)
        {
            case 1: if (p > 0) speed = p; break;                                              // A
            case 2: nextOrder = OrderFor(p); if (breakRow < 0) breakRow = 0; break;              // B
            case 3: breakRow = p; if (nextOrder < 0) nextOrder = orderIndex + 1; break;          // C (16진)
            case 4: case 11: case 12:                                                            // D K L
                if (p != 0) c.MemVolSlide = p;
                ItFineVolSlide(c, ref c.Volume, c.MemVolSlide, 64);
                break;
            case 5: case 6:                                                                      // E F
                if (p != 0) { c.MemPortaUp = c.MemPortaDown = p; if (mod.CompatGxx) c.TonePortaSpeed = p; }
                ItFinePorta(c, c.Fx == 6);
                break;
            case 7:                                                                              // G
                if (p != 0) SetTonePortaSpeed(c, p);
                break;
            case 8: case 21:                                                                     // H U
                if (x != 0) c.VibSpeed = x;
                if (y != 0) c.VibDepth = c.Fx == 21 ? y : y * 4;
                if (mod.OldEffects) Vibrato(c, VibDiv);
                break;
            case 9: if (p != 0) c.MemTremor = p; break;                                          // I
            case 10: if (p != 0) c.MemArp = p; break;                                            // J
            case 13: c.ChanVolume = Math.Min(64, p); break;                                      // M
            case 14: if (p != 0) c.MemChanVolSlide = p; ItFineVolSlide(c, ref c.ChanVolume, c.MemChanVolSlide, 64); break;   // N
            case 15: break;                                                                      // O (음 시작에서)
            case 16: if (p != 0) c.MemPanSlide = p; ItFinePanSlide(c); break;                    // P
            case 17: if (p != 0) c.MemRetrig = p; break;                                         // Q
            case 18: if (x != 0) c.TremSpeed = x; if (y != 0) c.TremDepth = y; break;            // R
            case 19: ItS(c, x, y); break;                                                        // S
            case 20: if (p != 0) c.MemTempo = p; if (c.MemTempo >= 0x20) SetTempo(c.MemTempo); break;   // T
            case 22: globalVolume = Math.Min(128, p); break;                                     // V
            case 23: if (p != 0) c.MemGlobalSlide = p; ItFineVolSlide(c, ref globalVolume, c.MemGlobalSlide, 128); break;   // W
            case 24: c.Pan = p * 64 / 255; c.Surround = false; break;                            // X
            case 25: if (x != 0) c.PanbSpeed = x; if (y != 0) c.PanbDepth = y; break;            // Y
        }
    }

    private void ItS(Chan c, int x, int y)
    {
        switch (x)
        {
            case 3: c.VibWave = y & 3; break;
            case 4: c.TremWave = y & 3; break;
            case 5: c.PanbWave = y & 3; break;
            case 6: extraTicks += y; break;
            case 7:
                switch (y)
                {
                    case 0: case 1: case 2:
                        foreach (var v in voices)
                            if (v.Background && v.Channel == c.Index && v.Active)
                            {
                                if (y == 0) StopVoice(v); else if (y == 1) VoiceKeyOff(v); else v.Fading = true;
                            }
                        break;
                    case 3: case 4: case 5: case 6: if (c.V != null) c.V.Nna = y - 3; else c.NnaOverride = y - 3; break;
                    case 7: if (c.V != null) c.V.VolEnvOn = false; break;
                    case 8: if (c.V != null) c.V.VolEnvOn = true; break;
                    case 9: if (c.V != null) c.V.PanEnvOn = false; break;
                    case 10: if (c.V != null) c.V.PanEnvOn = true; break;
                    case 11: if (c.V != null) c.V.PitchEnvOn = false; break;
                    case 12: if (c.V != null) c.V.PitchEnvOn = true; break;
                }
                break;
            case 8: c.Pan = y * 64 / 15; c.Surround = false; break;
            case 9: if (y == 1) c.Surround = true; break;
            case 0xA: c.MemHighOffset = y; break;
            case 0xB: PatternLoop(c, y); break;
            case 0xC: c.CutTick = Math.Max(1, y); break;
            case 0xE: if (patternDelayLeft == 0) patternDelay = y; break;
        }
    }

    private void ItEffectTick(Chan c)
    {
        int p = c.Param;
        switch (c.Fx)
        {
            case 4: ItVolSlide(c, ref c.Volume, c.MemVolSlide, 64); break;
            case 5: ItPorta(c, false); break;
            case 6: ItPorta(c, true); break;
            case 7: TonePorta(c); break;
            case 8: case 21: Vibrato(c, VibDiv); break;
            case 9:
            {
                int on = c.MemTremor >> 4, off = c.MemTremor & 15;
                if (mod.OldEffects) { on++; off++; }
                Tremor(c, Math.Max(1, on), Math.Max(1, off));
                break;
            }
            case 10: Arpeggio(c, c.MemArp); break;
            case 11: Vibrato(c, VibDiv); ItVolSlide(c, ref c.Volume, c.MemVolSlide, 64); break;
            case 12: TonePorta(c); ItVolSlide(c, ref c.Volume, c.MemVolSlide, 64); break;
            case 14: ItVolSlide(c, ref c.ChanVolume, c.MemChanVolSlide, 64); break;
            case 16:
            {
                int x = c.MemPanSlide >> 4, y = c.MemPanSlide & 15;
                if (x == 0 && y != 0 && y != 15) c.Pan = Math.Min(64, c.Pan + y);
                else if (y == 0 && x != 0 && x != 15) c.Pan = Math.Max(0, c.Pan - x);
                break;
            }
            case 17:
            {
                int ry = c.MemRetrig & 15, rx = c.MemRetrig >> 4;
                if (ry > 0 && ++c.RetrigCount >= ry) { c.RetrigCount = 0; Retrigger(c); c.Volume = RetrigVolume(c.Volume, rx); }
                break;
            }
            case 18: Tremolo(c); break;
            case 20:
            {
                int t = c.MemTempo;
                if (t < 0x10) SetTempo(tempo - (t & 15)); else if (t < 0x20) SetTempo(tempo + (t & 15));
                break;
            }
            case 23: ItVolSlide(c, ref globalVolume, c.MemGlobalSlide, 128); break;
            case 25:
            {
                int d = Wave(c.PanbWave, c.PanbPos) * c.PanbDepth / 128;
                c.PanAdd = d / 2;
                c.PanbPos = (c.PanbPos + c.PanbSpeed) & 63;
                break;
            }
        }
    }

    /// <summary>IT D · N · W 첫 틱: DxF 미세 올림, DFy 미세 내림.</summary>
    private static void ItFineVolSlide(Chan c, ref int vol, int mem, int max)
    {
        int x = mem >> 4, y = mem & 15;
        if (y == 0xF && x != 0) vol = Math.Min(max, vol + x);
        else if (x == 0xF && y != 0) vol = Math.Max(0, vol - y);
    }

    /// <summary>IT D · N · W 다음 틱들: Dx0 올림, D0y 내림.</summary>
    private static void ItVolSlide(Chan c, ref int vol, int mem, int max)
    {
        int x = mem >> 4, y = mem & 15;
        if (y == 0 && x != 0) vol = Math.Min(max, vol + x);
        else if (x == 0 && y != 0) vol = Math.Max(0, vol - y);
        else if (y == 0xF && x == 0) vol = Math.Max(0, vol - 15);
    }

    private void ItFinePanSlide(Chan c)
    {
        int x = c.MemPanSlide >> 4, y = c.MemPanSlide & 15;
        if (x == 0xF && y != 0) c.Pan = Math.Min(64, c.Pan + y);
        else if (y == 0xF && x != 0) c.Pan = Math.Max(0, c.Pan - x);
    }

    private void ItFinePorta(Chan c, bool up)
    {
        int m = up ? c.MemPortaUp : c.MemPortaDown;
        if (m >= 0xF0) { if (up) SlideUp(c, (m & 15) * 4); else SlideDown(c, (m & 15) * 4); }
        else if (m >= 0xE0) { if (up) SlideUp(c, m & 15); else SlideDown(c, m & 15); }
    }

    private void ItPorta(Chan c, bool up)
    {
        int m = up ? c.MemPortaUp : c.MemPortaDown;
        if (m >= 0xE0) return;
        if (up) SlideUp(c, m * 4); else SlideDown(c, m * 4);
    }

    // ================================================================ 공통 효과

    private void PatternLoop(Chan c, int y)
    {
        if (y == 0) { c.LoopRow = row; return; }
        if (c.LoopCount == 0) c.LoopCount = y;
        else c.LoopCount--;
        if (c.LoopCount > 0)
        {
            breakRow = c.LoopRow; nextOrder = orderIndex;
            // 되돌아가는 줄은 다시 지나가도 곡 끝이 아니다
            var pat = CurrentPat;
            int rows = pat != null ? pat.Rows : 64;
            for (int r = c.LoopRow; r <= Math.Min(rows - 1, row); r++) visited.Remove(((long)orderIndex << 16) | (uint)r);
        }
        else if (mod.IsIt) c.LoopRow = row + 1;
    }

    private void Tremor(Chan c, int on, int off)
    {
        c.TremorCount++;
        if (c.TremorOn && c.TremorCount >= on) { c.TremorOn = false; c.TremorCount = 0; }
        else if (!c.TremorOn && c.TremorCount >= off) { c.TremorOn = true; c.TremorCount = 0; }
        c.TremorMute = !c.TremorOn;
    }

    private static readonly int[] RetrigAdd = { 0, -1, -2, -4, -8, -16, 0, 0, 0, 1, 2, 4, 8, 16, 0, 0 };

    private static int RetrigVolume(int v, int x)
    {
        if (x == 6) v = v * 2 / 3; else if (x == 7) v /= 2; else if (x == 0xE) v = v * 3 / 2; else if (x == 0xF) v *= 2; else v += RetrigAdd[x];
        return Clamp(v, 0, 64);
    }

    private void Retrigger(Chan c)
    {
        var v = c.V;
        if (v == null || c.Sample == null) return;
        if (!v.Active || v.Stopping)
        {
            v = new Voice { Channel = c.Index, Sample = c.Sample, Ins = mod.IsIt && !mod.UseInstruments ? null : mod.InstrumentAt(c.InsNo), InsNo = c.InsNo, SampleNo = c.SampleNo, Note = c.RealNote, Active = true };
            AddVoice(v);
            c.V = v;
        }
        v.Pos = 0; v.Backward = false;
        c.NoteOnCount++;
    }

    private void ApplyOffset(Chan c)
    {
        bool off = mod.IsIt ? c.Fx == 15 : c.Fx == 9;
        if (!off || c.V == null) return;
        if (c.Param != 0) c.MemOffset = c.Param;
        int pos = c.MemOffset * 256 + (mod.IsIt ? c.MemHighOffset * 65536 : 0);
        var s = c.V.Sample;
        if (pos >= s.Length)
        {
            if (!mod.IsIt) { StopVoice(c.V); return; }
            if (s.Loop) pos = s.LoopStart; else pos = s.Length - 1;
        }
        c.V.Pos = Math.Max(0, pos);
    }

    private void EffectTick(Chan c)
    {
        c.PitchAdd = 0; c.VolAdd = 0;
        if (c.DelayTick >= 0 && tick == c.DelayTick)
        {
            Trigger(c, c.Delayed);
            VolumeColumnRow(c, c.Delayed);
            c.DelayTick = -1;
        }
        if (c.CutTick >= 0 && tick == c.CutTick)
        {
            if (mod.IsIt) { if (c.V != null) { StopVoice(c.V); c.V = null; } c.ShownNote = -1; }
            else c.Volume = 0;
            c.CutTick = -1;
        }
        if (c.DelayTick < 0) VolumeColumnTick(c, c.Cell);
        if (mod.IsIt) ItEffectTick(c); else XmEffectTick(c);
    }

    // ================================================================ 보이스 갱신 (틱마다)

    private void UpdateVoices()
    {
        foreach (var v in voices)
        {
            if (!v.Active || v.Stopping) continue;
            Chan c = v.Background ? null : ch[v.Channel];
            if (c != null && !ReferenceEquals(c.V, v)) { v.Background = true; FreezeVoice(c, v); c = null; }
            double pitch = c != null ? c.Pitch : v.Pitch;
            double add = c != null ? c.PitchAdd : 0;
            int vol = c != null ? Clamp(c.Volume + c.VolAdd, 0, 64) : v.Volume;
            if (c != null && c.TremorMute) vol = 0;
            int chanVol = c != null ? c.ChanVolume : v.ChanVolume;
            int pan = c != null ? Clamp(c.Pan + c.PanAdd, 0, 64) : v.Pan;
            bool surround = c != null && c.Surround;
            var ins = v.Ins;
            var s = v.Sample;

            // 엔벨로프
            double envVol = 1;
            if (ins != null && ins.VolumeEnv.Usable && v.VolEnvOn)
            {
                envVol = EnvValue(ins.VolumeEnv, v.VolEnvTick) / 64.0;
                bool end = StepEnvelope(ins.VolumeEnv, ref v.VolEnvTick, v.Released);
                if (end)
                {
                    // 끝이 0이면 더 들리지 않는다: 끈다 (IT는 원래 그렇고, XM도 소리는 같다)
                    if (ins.VolumeEnv.Values[ins.VolumeEnv.Values.Count - 1] == 0) { StopVoice(v); if (c != null) { c.V = null; c.ShownNote = -1; } continue; }
                    if (mod.IsIt && v.Released) v.Fading = true;
                }
            }
            if (ins != null && ins.PanEnv.Usable && v.PanEnvOn)
            {
                int ep = EnvValue(ins.PanEnv, v.PanEnvTick);
                StepEnvelope(ins.PanEnv, ref v.PanEnvTick, v.Released);
                pan = Clamp(pan + ep * (32 - Math.Abs(pan - 32)) / 32, 0, 64);
            }
            if (ins != null && ins.PitchEnv.Usable && !ins.PitchEnv.Filter && v.PitchEnvOn)
            {
                int ep = EnvValue(ins.PitchEnv, v.PitchEnvTick);
                StepEnvelope(ins.PitchEnv, ref v.PitchEnvTick, v.Released);
                if (mod.LinearSlides) add += ep * 32; else add += (pitch + add) * (Math.Pow(2, -ep / 24.0) - 1);
            }
            // 페이드아웃
            if (v.Fading)
            {
                double fo = ins != null ? ins.FadeOut : 0;
                if (mod.IsIt && fo == 0 && ins == null) fo = 1;
                v.Fade -= fo;
                if (v.Fade <= 0) { v.Fade = 0; StopVoice(v); if (c != null) c.V = null; continue; }
            }
            // 자동 진동
            if (s.VibDepth > 0 && s.VibRate > 0)
            {
                int amp;
                if (mod.IsIt)
                {
                    v.AutoVibAmp = Math.Min(s.VibDepth * 256, v.AutoVibAmp + Math.Max(1, s.VibSweep) * 2);
                    amp = v.AutoVibAmp;
                    v.AutoVibPos = (v.AutoVibPos + s.VibRate) & 255;
                }
                else
                {
                    if (s.VibSweep > 0) v.AutoVibAmp = Math.Min(s.VibDepth * 256, v.AutoVibAmp + s.VibDepth * 256 / s.VibSweep);
                    else v.AutoVibAmp = s.VibDepth * 256;
                    amp = v.AutoVibAmp;
                    v.AutoVibPos = (v.AutoVibPos + s.VibRate) & 255;
                }
                int w = Wave(s.VibType == 1 ? 2 : s.VibType == 2 ? 1 : s.VibType, v.AutoVibPos >> 2) / 4;   // ±64
                double d = w * (double)amp / (64.0 * 256.0) * (mod.IsIt ? 0.5 : 1.0);
                if (mod.LinearSlides) add += d; else add -= d;
            }

            double freq = FreqOf(pitch, add, s);
            v.Step = Math.Max(0, freq / Rate);

            // 볼륨
            double fv = vol / 64.0 * envVol * v.Fade * globalVolume / 128.0;
            if (mod.IsIt) fv *= chanVol / 64.0 * s.GlobalVolume / 64.0 * (ins != null ? ins.GlobalVolume / 128.0 : 1.0);
            if (v.Channel < options.VoiceMute.Length && options.VoiceMute[v.Channel]) fv = 0;
            double pl, pr;
            if (!mod.Stereo || surround) { pl = pr = 0.7071; }
            else
            {
                double pp = pan / 64.0;
                pl = Math.Sqrt(1 - pp); pr = Math.Sqrt(pp);
            }
            SetTarget(v, fv * pl, fv * pr);
            if (c != null) { v.Pitch = pitch; v.Volume = vol; v.ChanVolume = chanVol; v.Pan = pan; }
        }
    }

    private static void SetTarget(Voice v, double l, double r)
    {
        v.TgtL = l; v.TgtR = r;
        v.RampLeft = RampSamples;
        v.StepL = (l - v.CurL) / RampSamples;
        v.StepR = (r - v.CurR) / RampSamples;
    }

    private static int EnvValue(ExtEnvelope e, int t)
    {
        var ts = e.Ticks; var vs = e.Values;
        int n = ts.Count;
        if (t <= ts[0]) return vs[0];
        for (int i = 1; i < n; i++)
        {
            if (t <= ts[i])
            {
                int t0 = ts[i - 1], t1 = ts[i];
                if (t1 <= t0) return vs[i];
                return vs[i - 1] + (vs[i] - vs[i - 1]) * (t - t0) / (t1 - t0);
            }
        }
        return vs[n - 1];
    }

    /// <summary>엔벨로프 한 틱 진행 (서스테인 · 루프). 끝을 지났으면 true.</summary>
    private bool StepEnvelope(ExtEnvelope e, ref int t, bool released)
    {
        var ts = e.Ticks;
        int last = ts[ts.Count - 1];
        if (!mod.IsIt)
        {
            // FT2: 서스테인 점에서 멈춤 (키 오프 전), 루프 끝 점에 닿으면 루프 시작으로
            if (e.Sustain && !released && t == ts[e.SustainStart]) return false;
            t++;
            if (e.Loop && t == ts[e.LoopEnd]) t = ts[e.LoopStart];
        }
        else
        {
            // IT: 서스테인 루프(키 오프 전)가 보통 루프보다 먼저
            t++;
            if (e.Sustain && !released) { if (t > ts[e.SustainEnd]) t = ts[e.SustainStart]; }
            else if (e.Loop && t > ts[e.LoopEnd]) t = ts[e.LoopStart];
        }
        if (t > last) { t = last; return true; }
        return false;
    }

    private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

    // ================================================================ 상태 (Jukebox)

    public void CopyStates(VoiceState[] dst)
    {
        if (mod == null) return;
        for (int i = 0; i < dst.Length; i++)
        {
            if (i >= ch.Length) { dst[i] = new VoiceState { Note = -1 }; continue; }
            var c = ch[i];
            var v = c.V;
            bool sounding = v != null && v.Active && !v.Stopping && (v.TgtL + v.TgtR) > 0.002;
            dst[i] = new VoiceState
            {
                Note = sounding && c.ShownNote >= 0 ? NoteNumber(c.ShownNote) : -1,
                Volume = sounding ? (float)Math.Min(1, (v.TgtL + v.TgtR) * 0.9) : 0f,
                NoteOnCount = c.NoteOnCount,
                Instrument = c.InsNo > 0 ? mod.InstrumentName(c.InsNo) : null,
                InstrumentFound = true,
            };
        }
    }

    /// <summary>음 번호 → MIDI 번호 (표시용): IT C-5 · XM C-4 = 60 (샘플 본디 높이).</summary>
    private int NoteNumber(int note) { return Clamp(mod.IsIt ? note : note + 12, 0, 127); }

    public void ApplyMute(int channel)
    {
        if (channel < 0 || channel >= options.VoiceMute.Length || !options.VoiceMute[channel]) return;
        foreach (var v in voices) if (v.Channel == channel) { v.TgtL = v.TgtR = 0; v.CurL = v.CurR = 0; v.RampLeft = 0; }
    }
}
