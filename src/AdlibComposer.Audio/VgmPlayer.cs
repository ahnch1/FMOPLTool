using System;
using System.Collections.Generic;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// VGM 재생기: 기록된 레지스터 쓰기를 시간에 맞춰 칩에 보낸다. PSG(<see cref="PsgEmulator"/>) · SCC(<see cref="SccEmulator"/>) ·
/// OPL(<see cref="OplChip"/>, OPL2 또는 OPL3)을 함께 돌려 섞는다 (44100Hz, VGM의 시간 단위 그대로).
/// <para>Jukebox 조절: 속도 = 기다림을 빨리/느리게 (칩 소리는 그대로), 키 · 채널 음높이 = 칩에 보내는 분주 · F-number를 바꿔 쓴다,
/// 채널 끄기 = PSG · SCC는 에뮬레이터의 Mute, OPL은 키온 비트(리듬 모드는 BD의 드럼 비트)를 지운다.</para>
/// <para>되돌아가는 곡(루프)은 한 번 더 돌고 마지막 몇 초를 줄여 끝낸다. 위치 단위 = ms. 처음이 아닌 곳에서 시작하면
/// 그곳까지 명령만 빨리 실행한다 (소리는 만들지 않음).</para>
/// <para>둘째 칩(듀얼 칩) 명령과 그 밖의 칩 명령은 건너뛴다.</para>
/// </summary>
public sealed class VgmPlayer
{
    public const int Rate = VgmSong.Rate;
    /// <summary>루프 곡 끝에서 줄여 가는 시간 (초).</summary>
    public const double FadeSeconds = 4.0;

    public enum ChipKind { Psg, Scc, Fm }

    /// <summary>믹서 한 줄 = 칩의 채널 하나.</summary>
    public struct Channel
    {
        public ChipKind Chip;
        public int Index;
        public string Label;
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
        return l;
    }

    /// <summary>곡 끝까지 명령만 돌려 소리를 낸 적 있는 채널을 찾는다 (믹서 '사용' 표시).</summary>
    public static bool[] ScanUsed(VgmSong s)
    {
        var p = new VgmPlayer();
        p.Start(s, PlaySeconds(s) * 1000.0 + 1, null);
        var used = new bool[PlaybackOptions.Voices];
        Array.Copy(p.everSounded, used, used.Length);
        p.Stop();
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

        // 칩이 여럿이면 각각 줄여 합이 넘치지 않게
        int chips = (psg != null ? 1 : 0) + (scc != null ? 1 : 0) + (opl != null ? 1 : 0);
        double share = chips <= 1 ? 1.0 : chips == 2 ? 0.7 : 0.58;
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
        loopsLeft = s.LoopOffset > 0 ? 1 : 0;
        playing = pos < s.DataEnd;
        SyncAdjustments();

        // 시작 위치까지 명령만 빨리
        double target = Math.Max(0, startMs) * Rate / 1000.0;
        while (playing && due <= target) ExecuteOne();
        clock = Math.Min(target, playLength);
        if (clock >= playLength) playing = false;
    }

    public void Stop() { playing = false; }

    // ================================================================ 한 샘플

    /// <summary>44100Hz 한 칸: 이 시간까지 할 명령을 실행한다.</summary>
    public void Step()
    {
        if (!playing) return;
        if (--syncCountdown <= 0) { syncCountdown = 64; SyncAdjustments(); }
        clock += Math.Max(0.05, Options.Speed);
        while (playing && due <= clock) ExecuteOne();
        if (clock >= playLength) playing = false;
    }

    /// <summary>칩 소리를 섞어 스테레오 샘플 하나 (16bit 범위 안팎).</summary>
    public void Generate(out double left, out double right)
    {
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
        double fade = 1.0;
        if (song != null && song.LoopOffset > 0)
        {
            double fadeLen = Math.Min(FadeSeconds * Rate, song.LoopSamples);
            double left0 = playLength - clock;
            if (left0 < fadeLen) fade = Math.Max(0, left0 / fadeLen);
        }
        left = (mono + l) * fade;
        right = (mono + r) * fade;
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
            default:
                if (c >= 0x70 && c <= 0x7F) due += (c & 15) + 1;
                else if (c >= 0x80 && c <= 0x8F) due += c & 15;   // YM2612 DAC + 기다림: 기다림만
                break;
        }
        pos += len;
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
            default: return opl3 ? "OPL3 FM" : "FM";
        }
    }

    private static double Ratio(int semis) { return Math.Pow(2, semis / 12.0); }
    private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
}
