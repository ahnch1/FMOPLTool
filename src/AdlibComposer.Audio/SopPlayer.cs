using System;
using System.Collections.Generic;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// SOP 곡 재생기. 칩 쪽은 Ad262Sop 라이브러리(박진홍, _incoming/AD262SOP/AD262SOP.C)를 그대로 옮겼다:
/// <list type="bullet">
/// <item>트랙 0~8 → 첫 뱅크 채널 0~8, 11~19 → 둘째 뱅크 채널 0~8. 리듬 모드면 6~10 = BD SD TOM CY HH (CY = 채널 8, HH = 채널 7)</item>
/// <item>4-op(Set_4OP_Mode): 채널 모드 1인 트랙 0·1·2·11·12·13은 처음부터 채널 +3과 짝. 짝 트랙(+3)의 악기 · 볼륨은 무시</item>
/// <item>음높이(SetFreq_SOP): 반음당 32단계 F-Number 표, 노트 12 = C0 (60 = 가운데 도), 피치 0~200 (100 = 그대로, 3.125당 1단계 = 100당 1반음)</item>
/// <item>볼륨(SetVoiceVolume_SOP): TL = 63 − ((63 − TL) × vol + 64) >> 7, 출력 오퍼레이터만 (4-op 연결별)</item>
/// <item>팬(SetStereoPan_SOP): 0 → C0 비트 0xA0(B·D = 오른쪽), 1 → 0x30(양쪽), 2 → 0x50(A·C = 왼쪽)</item>
/// <item>드럼(NoteOn_SOP): BD만 자기 음높이, TOM은 음이 바뀌면 TOM = 음 · SD = 음 + 7 (피치 100), SD · CY · HH는 음높이를 바꾸지 않음. 드럼 피치 이벤트는 BD만</item>
/// </list>
/// 드라이버와 다르게 한 것 (재생 루프는 소스에 없음):
/// <list type="bullet">
/// <item>앞 노트가 끝나기 전에 같은 트랙의 새 노트가 오면 키오프 후 다시 키온 (드라이버만으로는 이어 붙어 드럼이 다시 울리지 않는다. 실제 곡의 BD에 이런 겹침이 많다)</item>
/// <item>리듬 모드가 아닐 때 트랙 9 · 10의 악기/팬은 무시 (드라이버는 채널 0 오퍼레이터와 채널 7 · 8의 C0를 덮어쓴다)</item>
/// <item>마스터 볼륨(제어 트랙): 트랙 볼륨 × 마스터 / 127. 볼륨 이벤트 전 기본값 127</item>
/// </list>
/// <see cref="Sequencer"/>처럼 칩 샘플 1개마다 Step()을 부른다.
/// </summary>
public sealed class SopPlayer
{
    private const int Tracks = SopSong.MaxTracks;
    private const int BD = 6, SD = 7, TOM = 8, CYMB = 9, HIHAT = 10;
    private const int TomPitch = 36, TomToSd = 7;

    /// <summary>AD262SOP.C fNumTbl (PURE_HTS 아님): C부터 반음당 32단계 × 12. 음수는 다음 블록의 0x200대.</summary>
    private static readonly int[] FNumTbl =
    {
        690, 691, 692, 694, 695, 696, 697, 699, 700, 701, 702, 704,
        705, 706, 707, 709, 710, 711, 713, 714, 715, 716, 718, 719,
        720, 722, 723, 724, 726, 727, 728, 729, 731, 732, 733, 735,
        736, 737, 739, 740, 741, 743, 744, 745, 747, 748, 749, 751,
        752, 754, 755, 756, 758, 759, 760, 762, 763, 765, 766, 767,
        769, 770, 771, 773, 774, 776, 777, 778, 780, 781, 783, 784,
        785, 787, 788, 790, 791, 793, 794, 795, 797, 798, 800, 801,
        803, 804, 806, 807, 809, 810, 811, 813, 814, 816, 817, 819,
        820, 822, 823, 825, 826, 828, 829, 831, 832, 834, 835, 837,
        838, 840, 841, 843, 844, 846, 847, 849, 850, 852, 854, 855,
        857, 858, 860, 861, 863, 864, 866, 867, 869, 871, 872, 874,
        875, 877, 879, 880, 882, 883, 885, 886, 888, 890, 891, 893,
        895, 896, 898, 899, 901, 903, 904, 906, 908, 909, 911, 912,
        914, 916, 917, 919, 921, 922, 924, 926, 927, 929, 931, 932,
        934, 936, 937, 939, 941, 943, 944, 946, 948, 949, 951, 953,
        955, 956, 958, 960, 961, 963, 965, 967, 968, 970, 972, 974,
        975, 977, 979, 981, 983, 984, 986, 988, 990, 991, 993, 995,
        997, 999, 1000, 1002, 1004, 1006, 1008, 1010, 1011, 1013, 1015, 1017,
        1019, 1021, 1022, -512, -511, -510, -509, -508, -507, -506, -505, -504,
        -504, -503, -502, -501, -500, -499, -498, -497, -496, -495, -494, -493,
        -492, -491, -490, -489, -488, -487, -486, -485, -484, -483, -482, -481,
        -480, -479, -479, -478, -477, -476, -475, -474, -473, -472, -471, -470,
        -469, -468, -467, -466, -465, -464, -463, -462, -460, -459, -458, -457,
        -456, -455, -454, -453, -452, -451, -450, -449, -448, -447, -446, -445,
        -444, -443, -442, -441, -440, -439, -438, -437, -436, -434, -433, -432,
        -431, -430, -429, -428, -427, -426, -425, -424, -423, -422, -420, -419,
        -418, -417, -416, -415, -414, -413, -412, -411, -409, -408, -407, -406,
        -405, -404, -403, -402, -401, -399, -398, -397, -396, -395, -394, -393,
        -391, -390, -389, -388, -387, -386, -385, -383, -382, -381, -380, -379,
        -378, -376, -375, -374, -373, -372, -371, -369, -368, -367, -366, -365,
        -363, -362, -361, -360, -359, -357, -356, -355, -354, -353, -351, -350,
        -349, -348, -347, -345, -344, -343, -342, -340, -339, -338, -337, -335,
    };

    /// <summary>오퍼레이터 오프셋 (AD262SOP.C SlotX): 0~19 = 멜로디, 20~39 = 리듬 모드.</summary>
    private static readonly int[] SlotX =
    {
        0, 1, 2, 8, 9, 10, 16, 17, 18, 0, 0, 0, 1, 2, 8, 9, 10, 16, 17, 18,
        0, 1, 2, 8, 9, 10, 16, 20, 18, 21, 17, 0, 1, 2, 8, 9, 10, 16, 17, 18,
    };

    private enum Kind { NoteOff = 0, Control = 1, Note = 2 }

    private struct Ev
    {
        public int Tick;
        public Kind Kind;
        public int Track;
        public SopEventType Type;
        public int Value;
        public int Serial;
    }

    private readonly Opl3Emulator chip;
    private readonly List<Ev> events = new List<Ev>();
    private SopSong song;
    private int next;
    private double tick;
    private double ticksPerSample;
    private int tempo = 120;
    private int endTick;
    private int tailSamples;
    private bool playing, ending;
    private PlaybackOptions options = new PlaybackOptions();
    private int masterVolume = 127;
    private bool percussion;
    private int bdReg;

    // 트랙(드라이버 voice) 상태
    private readonly SopInstrument[] inst = new SopInstrument[Tracks];
    private readonly byte[][] instData = new byte[Tracks][];
    private readonly int[] volume = new int[Tracks];
    private readonly int[] pitchBend = new int[Tracks];
    private readonly int[] stereo = new int[Tracks];
    private readonly int[] voiceNote = new int[Tracks];
    private readonly bool[] keyOn = new bool[Tracks];
    private readonly int[] playing_note = new int[Tracks];
    private readonly int[] serial = new int[Tracks];
    private readonly bool[] op4 = new bool[Tracks];
    private readonly VoiceState[] states = new VoiceState[Tracks];
    /// <summary>B0 레지스터 사본 (드라이버 ymbuf): 키오프할 때 블록 · F-Number 위 비트를 남긴다. 0~10 = 첫 뱅크 B0~BA, 11~19 = 둘째 뱅크.</summary>
    private readonly int[] b0 = new int[Tracks];
    /// <summary>C0 레지스터 사본 (드라이버 ymbuf 0x10~): [뱅크 × 9 + 채널].</summary>
    private readonly int[] c0 = new int[18];

    public SopPlayer(Opl3Emulator chip) { this.chip = chip; }

    public bool IsPlaying { get { return playing; } }
    /// <summary>시음 중 (곡 재생이 아님): <see cref="JamOn"/>.</summary>
    public bool IsJam { get; private set; }

    /// <summary>
    /// 시음: 악기 하나를 트랙 0(4-op면 채널 0+3 짝)에 실어 음 하나를 친다. 끝나지 않고 <see cref="JamOff"/>까지 울린다.
    /// Instrument Maker / Composer에서 4-op 악기를 OPL3로 들을 때 쓴다.
    /// </summary>
    public void JamOn(SopInstrument ins, int note)
    {
        bool four = ins.Is4Op;
        if (!IsJam || song == null || song.Tracks[0].ChannelMode != (four ? 1 : 2))
        {
            song = new SopSong { BasicTempo = 120, TickBeat = 1, Tracks = new[] { new SopTrack { ChannelMode = four ? 1 : 2 } } };
            options = new PlaybackOptions();
            events.Clear();
            ResetChip();
        }
        IsJam = true;
        if (keyOn[0]) KeyOffRegister(0);
        keyOn[0] = false;
        SetVoiceTimbre(0, ins);
        next = 0; tick = 0; ticksPerSample = 0; endTick = int.MaxValue; ending = false; tailSamples = 0;
        playing = true;
        NoteOn(0, note, 0);
    }

    /// <summary>시음 중 악기 값이 바뀌었을 때 (누르고 있는 음에 바로 반영).</summary>
    public void JamUpdate(SopInstrument ins)
    {
        if (!IsJam || song == null || ins.Is4Op != op4[0]) return;
        SetVoiceTimbre(0, ins);
    }

    public void JamOff() { if (IsJam) KeyOff(0); }
    public double PositionTick { get { return tick; } }
    public PlaybackOptions Options { get { return options; } }
    public SopSong Song { get { return song; } }
    /// <summary>지금 템포 (BPM, 속도 배수 제외).</summary>
    public int Tempo { get { return tempo; } }

    public void CopyStates(VoiceState[] dst) { Array.Copy(states, dst, Math.Min(dst.Length, states.Length)); }

    public void Start(SopSong s, int startTick, PlaybackOptions opts = null)
    {
        IsJam = false;
        song = s;
        options = opts ?? new PlaybackOptions();
        BuildEvents();
        endTick = s.LengthTicks;
        ending = false;
        tailSamples = 0;
        SeekTo(Math.Max(0, startTick));
        playing = true;
    }

    public void Stop()
    {
        playing = false;
        IsJam = false;
        AllNotesOff();
    }

    public void ApplySpeed() { SetTempo(tempo); }

    public void ApplyMute(int track)
    {
        if (track < 0 || track >= Tracks || song == null) return;
        if (options.VoiceMute[track]) KeyOff(track);
    }

    // ------------------------------------------------------------ 이벤트 목록

    private void BuildEvents()
    {
        events.Clear();
        int seq = 0;
        foreach (var e in song.Control.Events)
            if (e.Type == SopEventType.Tempo || e.Type == SopEventType.MasterVolume)
                events.Add(new Ev { Tick = e.Tick, Kind = Kind.Control, Track = -1, Type = e.Type, Value = e.Value, Serial = seq++ });
        for (int t = 0; t < song.TrackCount && t < Tracks; t++)
        {
            foreach (var e in song.Tracks[t].Events)
            {
                if (e.Type == SopEventType.Note)
                {
                    int id = seq++;
                    events.Add(new Ev { Tick = e.Tick, Kind = Kind.Note, Track = t, Type = e.Type, Value = e.Value, Serial = id });
                    if (e.Duration > 0) events.Add(new Ev { Tick = e.Tick + e.Duration, Kind = Kind.NoteOff, Track = t, Serial = id });
                }
                else if (e.Type != SopEventType.Special)
                    events.Add(new Ev { Tick = e.Tick, Kind = Kind.Control, Track = t, Type = e.Type, Value = e.Value, Serial = seq++ });
            }
        }
        // 같은 틱: 키오프 → 제어(템포 · 악기 · 볼륨 …) → 노트, 그 안에서는 파일 순서
        events.Sort((a, b) =>
        {
            int c = a.Tick.CompareTo(b.Tick);
            if (c != 0) return c;
            c = ((int)a.Kind).CompareTo((int)b.Kind);
            if (c != 0) return c;
            return a.Serial.CompareTo(b.Serial);
        });
    }

    // ------------------------------------------------------------ 초기화 / 이동

    /// <summary>SoundWarmInit + Set_4OP_Mode + SetMode_SOP.</summary>
    private void ResetChip()
    {
        chip.Reset();
        chip.Write(0x105, 0x01);   // YMF262 모드
        chip.Write(0x08, 0x00);
        chip.Write(0x01, 0x20);
        masterVolume = 127;
        int mask = 0;
        for (int t = 0; t < Tracks; t++)
        {
            inst[t] = null; instData[t] = null;
            volume[t] = 127; pitchBend[t] = 100; stereo[t] = 0x30; voiceNote[t] = 60; keyOn[t] = false; b0[t] = 0;
            playing_note[t] = -1; serial[t] = -1;
            op4[t] = t < song.TrackCount && song.Tracks[t].ChannelMode == 1 && (t <= 2 || (t >= 11 && t <= 13));
            if (op4[t]) mask |= t <= 10 ? 1 << t : 1 << (t - 11 + 3);
            states[t] = new VoiceState { Note = -1, Volume = 1f, NoteOnCount = states[t].NoteOnCount };
        }
        chip.Write(0x104, mask);
        for (int c = 0; c < 18; c++) c0[c] = 0;
        percussion = song.Percussive;
        if (percussion)
        {
            voiceNote[TOM] = TomPitch; SetFreq(TOM, TomPitch, 100, false);
            voiceNote[SD] = TomPitch + TomToSd; SetFreq(SD, TomPitch + TomToSd, 100, false);
        }
        bdReg = percussion ? 0x20 : 0;
        chip.Write(0xBD, bdReg);
    }

    private void SeekTo(int target)
    {
        ResetChip();
        tempo = song.BasicTempo;
        next = 0;
        while (next < events.Count && events[next].Tick < target)
        {
            var e = events[next++];
            if (e.Kind == Kind.Control) ApplyControl(e);
        }
        tick = target;
        SetTempo(tempo);
    }

    private void SetTempo(int bpm)
    {
        tempo = bpm <= 0 ? song.BasicTempo : bpm;
        double speed = options.Speed < 0.05 ? 0.05 : options.Speed;
        ticksPerSample = tempo * song.TickBeat / 60.0 * speed / chip.NativeRate;
    }

    // ------------------------------------------------------------ 진행

    public bool Step()
    {
        if (!playing) return false;
        while (next < events.Count && events[next].Tick <= tick)
        {
            var e = events[next++];
            switch (e.Kind)
            {
                case Kind.NoteOff:
                    if (serial[e.Track] == e.Serial) { KeyOff(e.Track); serial[e.Track] = -1; }
                    break;
                case Kind.Control: ApplyControl(e); break;
                case Kind.Note: NoteOn(e.Track, e.Value, e.Serial); break;
            }
        }
        tick += ticksPerSample;

        if (!ending && tick >= endTick)
        {
            ending = true;
            AllNotesOff();
        }
        if (ending && ++tailSamples > chip.NativeRate)
        {
            playing = false;
            return false;
        }
        return true;
    }

    private void ApplyControl(Ev e)
    {
        int t = e.Track;
        if (t >= 0 && !HasVoice(t)) return;
        switch (e.Type)
        {
            case SopEventType.Tempo: SetTempo(e.Value); break;
            case SopEventType.MasterVolume:
                masterVolume = Math.Min(127, e.Value);
                for (int i = 0; i < Tracks; i++) SetVoiceVolume(i);
                break;
            case SopEventType.Volume:
                if (t < 0) break;
                volume[t] = Math.Min(127, e.Value);
                SetVoiceVolume(t);
                break;
            case SopEventType.Pitch:
                if (t < 0) break;
                SetVoicePitch(t, e.Value);
                break;
            case SopEventType.Pan:
                if (t < 0 || e.Value > 2) break;
                SetStereoPan(t, e.Value);
                break;
            case SopEventType.Instrument:
                if (t < 0 || e.Value >= song.Instruments.Count) break;
                var ins = song.Instruments[e.Value];
                if (ins.IsEmpty) break;   // 빈 악기 칸 (종류 11 · 12)
                SetVoiceTimbre(t, ins);
                states[t].Instrument = ins.Name;
                states[t].InstrumentFound = true;
                break;
        }
    }

    // ------------------------------------------------------------ 드라이버 (AD262SOP.C)

    private bool IsDrum(int t) { return percussion && t >= BD && t <= HIHAT; }

    /// <summary>소리를 낼 수 있는 트랙: 4-op 짝(+3)이 아니고, 리듬 모드가 아니면 9 · 10 제외.</summary>
    private bool HasVoice(int t)
    {
        if (t > 2 && op4[t - 3]) return false;
        if (!percussion && (t == CYMB || t == HIHAT)) return false;
        return true;
    }

    /// <summary>트랙 → (뱅크 0x000 / 0x100, 채널 번호 0~8). 9 · 10은 리듬 모드의 CY(채널 8) · HH(채널 7).</summary>
    private static void Port(int t, out int bank, out int ch)
    {
        if (t < 9) { bank = 0; ch = t; }
        else if (t < 11) { bank = 0; ch = 17 - t; }
        else { bank = 0x100; ch = t - 11; }
    }

    private int Slot(int t) { return SlotX[percussion ? t + 20 : t]; }

    /// <summary>SEND_INS: 20/40/60/80, E0(& 7).</summary>
    private void SendIns(int bank, int baseAddr, byte[] d, int i)
    {
        for (int k = 0; k < 4; k++) { chip.Write(bank | baseAddr, d[i + k]); baseAddr += 0x20; }
        chip.Write(bank | (baseAddr + 0x40), d[i + 4] & 7);
    }

    private void SetVoiceTimbre(int t, SopInstrument ins)
    {
        int bank, ch;
        Port(t, out bank, out ch);
        // 4-op 트랙에 2-op 악기면 오퍼레이터 3·4 자리는 0 (드라이버는 23바이트 버퍼를 그대로 보낸다)
        var d = new byte[22];
        Array.Copy(ins.Data, d, Math.Min(22, ins.Data.Length));
        inst[t] = ins;
        instData[t] = d;
        int slot = Slot(t);
        WriteC0(bank, ch, 0);
        SendIns(bank, 0x20 + slot, d, 0);
        if (!(IsDrum(t) && t > BD)) SendIns(bank, 0x23 + slot, d, 6);
        if (op4[t])
        {
            WriteC0(bank, ch + 3, 0);
            SendIns(bank, 0x28 + slot, d, 11);
            SendIns(bank, 0x2B + slot, d, 17);
            WriteC0(bank, ch + 3, (d[16] & 0x0F) | stereo[t]);
        }
        SetVoiceVolume(t);
        WriteC0(bank, ch, (d[5] & 0x0F) | stereo[t]);
    }

    /// <summary>VolumeTable: (진폭 × vol + 64) >> 7.</summary>
    private static int Scaled(int reg40, int vol)
    {
        int amp = 63 - (reg40 & 0x3F);
        return (reg40 & 0xC0) | (63 - ((amp * vol + 64) >> 7));
    }

    private void SetVoiceVolume(int t)
    {
        var d = instData[t];
        if (d == null || !HasVoice(t)) return;
        int vol = volume[t] * masterVolume / 127;
        states[t].Volume = vol / 127f;
        int bank, ch;
        Port(t, out bank, out ch);
        int slot = Slot(t);
        int car = bank | (0x43 + slot), mod = bank | (0x40 + slot);
        if (IsDrum(t) && t > BD) { chip.Write(mod, Scaled(d[1], vol)); return; }   // 단일 오퍼레이터 드럼
        bool cnt1 = (d[5] & 1) != 0, cnt2 = (d[16] & 1) != 0;
        if (op4[t])
        {
            int mod2 = bank | (0x48 + slot), car2 = bank | (0x4B + slot);
            chip.Write(car2, Scaled(d[18], vol));                                   // 오퍼레이터 4는 항상 출력
            if (cnt1) chip.Write(mod, Scaled(d[1], vol));                           // 1 + …
            if (cnt1 && cnt2) chip.Write(mod2, Scaled(d[12], vol));                 // 1 + (2→3) + 4
            if (!cnt1 && cnt2) chip.Write(car, Scaled(d[7], vol));                  // (1→2) + (3→4)
            return;
        }
        if (cnt1) chip.Write(mod, Scaled(d[1], vol));
        chip.Write(car, Scaled(d[7], vol));
    }

    private void SetStereoPan(int t, int value)
    {
        int[] pan = { 0xA0, 0x30, 0x50 };
        stereo[t] = pan[value];
        int bank, ch;
        Port(t, out bank, out ch);
        int i = (bank != 0 ? 9 : 0) + ch;
        if (op4[t]) WriteC0(bank, ch + 3, (c0[i + 3] & 0x0F) | stereo[t]);
        WriteC0(bank, ch, (c0[i] & 0x0F) | stereo[t]);
    }

    private void WriteC0(int bank, int ch, int value)
    {
        c0[(bank != 0 ? 9 : 0) + ch] = value;
        chip.Write(bank | (0xC0 + ch), value);
    }

    /// <summary>SetFreq_SOP: 반음당 32단계, 피치 100 = 그대로, 3.125 = 1단계.</summary>
    private void SetFreq(int t, int note, int pitch, bool key)
    {
        int temp = (int)((pitch - 100) / 3.125) + ((note - 12) << 5);
        if (temp < 0) temp = 0;
        else if (temp >= (96 << 5) - 1) temp = (96 << 5) - 1;
        int fN = FNumTbl[((temp >> 5) % 12 << 5) + (temp & 31)];
        int div = (temp >> 5) / 12 - 1;
        if (fN < 0) div++;
        if (div < 0) { div++; fN >>= 1; }
        int bank = t < 11 ? 0 : 0x100, ch = t < 11 ? t : t - 11;
        chip.Write(bank | (0xA0 + ch), fN & 0xFF);
        int v = (((fN >> 8) & 3) | (div << 2) | (key ? 0x20 : 0)) & 0xFF;
        b0[t] = v;
        chip.Write(bank | (0xB0 + ch), v);
    }

    private int Transposed(int t, int n)
    {
        return n + options.VoiceTranspose[t] + (IsDrum(t) ? 0 : options.Transpose);
    }

    private void SetVoicePitch(int t, int pitch)
    {
        if (pitch < 0 || pitch > 200) return;
        pitchBend[t] = pitch;
        if (IsDrum(t) && t != BD) return;
        SetFreq(t, voiceNote[t], pitch, keyOn[t]);
    }

    private void NoteOn(int t, int n, int id)
    {
        serial[t] = id;
        playing_note[t] = n;
        states[t].Note = n;
        states[t].NoteOnCount++;
        if (options.VoiceMute[t] || !HasVoice(t)) { KeyOff(t); playing_note[t] = n; states[t].Note = n; return; }
        int nn = Transposed(t, n);
        if (IsDrum(t))
        {
            int bit = 0x10 >> (t - BD);
            if (t == BD) { voiceNote[BD] = nn; SetFreq(BD, nn, pitchBend[BD], false); }
            else if (t == TOM && voiceNote[TOM] != nn)
            {
                voiceNote[TOM] = nn; voiceNote[SD] = nn + TomToSd;
                SetFreq(TOM, nn, 100, false);
                SetFreq(SD, nn + TomToSd, 100, false);
            }
            if ((bdReg & bit) != 0) chip.Write(0xBD, bdReg & ~bit);   // 겹친 노트: 다시 울린다 (드라이버와 다름)
            bdReg |= bit;
            chip.Write(0xBD, bdReg);
            return;
        }
        if (keyOn[t]) KeyOffRegister(t);   // 겹친 노트: 다시 울린다 (드라이버와 다름)
        voiceNote[t] = nn;
        keyOn[t] = true;
        SetFreq(t, nn, pitchBend[t], true);
    }

    private void KeyOff(int t)
    {
        playing_note[t] = -1;
        states[t].Note = -1;
        if (IsDrum(t))
        {
            bdReg &= ~(0x10 >> (t - BD));
            chip.Write(0xBD, bdReg);
            return;
        }
        if (!keyOn[t]) return;
        keyOn[t] = false;
        KeyOffRegister(t);
    }

    private void KeyOffRegister(int t)
    {
        b0[t] &= 0xDF;
        chip.Write(t < 11 ? 0xB0 + t : 0x100 | (0xB0 + t - 11), b0[t]);
    }

    private void AllNotesOff()
    {
        if (song == null) return;
        for (int t = 0; t < Tracks; t++) { serial[t] = -1; KeyOff(t); }
    }
}
