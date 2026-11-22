using System;
using System.IO;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// RAD (Reality AdLib Tracker) v1.0 · v2.1 재생기. AdPlug <c>rad2.cpp</c> (LGPL 2.1)를 옮긴 것 — 그 파일은 Shayde/Reality의 공개 RAD2 재생기 (public domain)를
/// v1도 읽게 고친 것이다. 이 파일도 AdPlug의 LGPL 2.1을 따른다.
/// <para>머리말 "RAD by REALiTY!!", 0x10 판 (10h · 21h), 0x11 깃발 (속도 · BPM 있음 · 느린 타이머 · 설명), 악기 (v2: 이름 · 알고리즘 7가지 (4-op 포함) · 패닝 · 디튠 · 리프),
/// 순서표 (80h 이상 = 점프), 트랙 (v2 = 번호 + 크기, v1 = 32개 위치), 리프 (v2). 효과: 1 · 2 포르타멘토, 3 톤 슬라이드, 5 톤 + 볼륨, A 볼륨 슬라이드, C 볼륨, D 줄 점프, F 속도,
/// I · M · R · T · U · V (리프 · 배수 · 전조 · 피드백 · 오퍼레이터 볼륨).</para>
/// <para>Update 주기 = 50Hz (v2는 BPM × 2 / 5, 느린 타이머 18.2Hz). 순서표의 같은 자리를 다시 만나면 곡 끝 (되풀이 시작). OPL3 레지스터 공간 그대로 (v1도 OPL3 모드로 씀, AdPlug와 같이).</para>
/// </summary>
public sealed class RadPlayer : OplPlayer
{
    public static readonly string[] Extensions = { ".rad" };

    public static VgmSong Load(string path)
    {
        var p = new RadPlayer();
        var v = AdPlugHost.Render(p, File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path), "RAD", "PC (Reality AdLib Tracker)");
        v.FilePath = path;
        v.SourceFormat = "RAD v" + (p.version >= 2 ? "2.1" : "1.0") + " · " + p.Hertz.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "Hz" + v.SourceFormat.Substring(3);
        return v;
    }

    // ------------------------------------------------------------ 상수

    private const int kTracks = 100, kChannels = 9, kTrackLines = 64, kRiffTracks = 10, kInstruments = 127;
    private const int cmPortamentoUp = 0x1, cmPortamentoDwn = 0x2, cmToneSlide = 0x3, cmToneVolSlide = 0x5, cmVolSlide = 0xA, cmSetVol = 0xC,
        cmJumpToLine = 0xD, cmSetSpeed = 0xF, cmIgnore = 'I' - 55, cmMultiplier = 'M' - 55, cmRiff = 'R' - 55, cmTranspose = 'T' - 55, cmFeedback = 'U' - 55, cmVolume = 'V' - 55;
    private enum Src { None, Riff, IRiff }
    private const int fKeyOn = 1, fKeyOff = 2, fKeyedOn = 4;

    private static readonly int[] NoteSize = { 0, 2, 1, 3, 1, 3, 2, 4 };
    private static readonly int[] ChanOffsets3 = { 0, 1, 2, 0x100, 0x101, 0x102, 6, 7, 8 };
    private static readonly int[] Chn2Offsets3 = { 3, 4, 5, 0x103, 0x104, 0x105, 0x106, 0x107, 0x108 };
    private static readonly int[] NoteFreq = { 0x16b, 0x181, 0x198, 0x1b0, 0x1ca, 0x1e5, 0x202, 0x220, 0x241, 0x263, 0x287, 0x2ae };
    private static readonly int[,] OpOffsets2 = { { 0x003, 0x000 }, { 0x004, 0x001 }, { 0x005, 0x002 }, { 0x00B, 0x008 }, { 0x00C, 0x009 }, { 0x00D, 0x00A }, { 0x013, 0x010 }, { 0x014, 0x011 }, { 0x015, 0x012 } };
    private static readonly int[,] OpOffsets3 =
    {
        { 0x00B, 0x008, 0x003, 0x000 }, { 0x00C, 0x009, 0x004, 0x001 }, { 0x00D, 0x00A, 0x005, 0x002 },
        { 0x10B, 0x108, 0x103, 0x100 }, { 0x10C, 0x109, 0x104, 0x101 }, { 0x10D, 0x10A, 0x105, 0x102 },
        { 0x113, 0x110, 0x013, 0x010 }, { 0x114, 0x111, 0x014, 0x011 }, { 0x115, 0x112, 0x015, 0x012 },
    };
    private static readonly bool[,] AlgCarriers =
    {
        { true, false, false, false }, { true, true, false, false }, { true, false, false, false }, { true, false, false, true },
        { true, false, true, false }, { true, false, true, true }, { true, true, true, true },
    };
    private static readonly byte[] BlankOp = { 0, 0x3F, 0, 0xF0, 0 };

    // ------------------------------------------------------------ 상태

    private sealed class Instrument
    {
        public readonly byte[] Feedback = new byte[2], Panning = new byte[2];
        public byte Algorithm, Detune, Volume, RiffSpeed;
        public int Riff = -1;
        public readonly byte[][] Operators = { new byte[5], new byte[5], new byte[5], new byte[5] };
        public string Name = "";
    }

    private sealed class Effects
    {
        public sbyte PortSlide, VolSlide;
        public int ToneSlideFreq;
        public byte ToneSlideOct, ToneSlideSpeed;
        public sbyte ToneSlideDir;
    }

    private sealed class Riff
    {
        public readonly Effects FX = new Effects();
        public int Track = -1, TrackStart = -1;
        public byte Line, Speed, SpeedCnt;
        public sbyte TransposeOctave, TransposeNote;
        public byte LastInstrument;
    }

    private sealed class Channel
    {
        public byte LastInstrument;
        public Instrument Instrument;
        public byte Volume, DetuneA, DetuneB, KeyFlags;
        public int CurrFreq;
        public sbyte CurrOctave;
        public readonly Effects FX = new Effects();
        public readonly Riff Riff = new Riff(), IRiff = new Riff();
    }

    private byte[] d = new byte[1];
    private int version;
    private bool useOpl3;
    private string description = "";
    private readonly Instrument[] instruments = new Instrument[kInstruments];
    private int numInstruments;
    private readonly Channel[] channels = new Channel[kChannels];
    private uint playTime;
    private readonly uint[] orderMap = new uint[4];
    private bool repeating;
    public float Hertz = 50;
    private int orderList;
    private readonly int[] tracks = new int[kTracks];
    private int numTracks;
    private readonly int[,] riffs = new int[kRiffTracks, kChannels];
    private int track = -1;
    private bool initialised;
    private byte speed, orderListSize, speedCnt, order, line, masterVol;
    private sbyte entrances, lineJump;
    private readonly byte[] opl3Regs = new byte[512];

    private sbyte noteNum, octaveNum;
    private byte instNum, effectNum, param;

    public override float Refresh { get { return Hertz; } }
    public override string Description { get { return description; } }
    public override string TypeName { get { return "Reality ADlib Tracker (version " + version + ")"; } }
    public int Version { get { return version; } }

    private byte B(int i) { return i >= 0 && i < d.Length ? d[i] : (byte)0; }

    private void SetOPL3(int reg, int val)
    {
        opl3Regs[reg] = (byte)val;
        AdPlugHost.Write3(Opl, reg, val & 0xFF);
    }

    private byte GetOPL3(int reg) { return opl3Regs[reg]; }

    // ------------------------------------------------------------ 불러오기

    public override void Load(byte[] data)
    {
        if (data.Length < 18 || Encoding.ASCII.GetString(data, 0, 16) != "RAD by REALiTY!!") throw new InvalidDataException("RAD 파일이 아닙니다 (머리말 RAD by REALiTY!!가 없음).");
        // 옛 곡 중 마지막 음이 잘린 것이 있어 빈 바이트 하나를 덧붙인다 (AdPlug와 같이)
        d = new byte[data.Length + 1];
        Array.Copy(data, d, data.Length);
        Init();
        if (Hertz <= 0) throw new InvalidDataException("RAD 판이 1.0 · 2.1이 아닙니다: 0x" + B(0x10).ToString("X2"));
        var sb = new StringBuilder();
        if (descAt >= 0)
            for (int s = descAt; s < d.Length && d[s] != 0; s++)
            {
                byte c = d[s];
                if (c >= 0x20) sb.Append((char)c);
                else if (c == 0x01) sb.Append('\n');
                else sb.Append(' ', c);
            }
        description = sb.ToString().Trim();
    }

    private int descAt = -1;

    private void Init()
    {
        initialised = false;
        int ver = B(0x10);
        if (ver != 0x10 && ver != 0x21) { Hertz = -1; return; }
        version = ver >> 4;
        useOpl3 = version >= 2;
        for (int i = 0; i < kTracks; i++) tracks[i] = -1;
        for (int i = 0; i < kRiffTracks; i++) for (int j = 0; j < kChannels; j++) riffs[i, j] = -1;
        for (int i = 0; i < kChannels; i++) channels[i] = new Channel();

        int s = 0x11;
        byte flags = B(s++);
        speed = (byte)(flags & 0x1F);
        Hertz = 50.0f;
        if (version >= 2 && (flags & 0x20) != 0)
        {
            Hertz = (float)(B(s) | (B(s + 1) << 8)) * 2 / 5;
            s += 2;
        }
        if ((flags & 0x40) != 0) Hertz = 18.2f;
        descAt = -1;
        if (version >= 2 || (flags & 0x80) != 0)
        {
            descAt = s;
            while (B(s) != 0 && s < d.Length) s++;
            s++;
        }

        for (int i = 0; i < kInstruments; i++) instruments[i] = new Instrument();
        numInstruments = 0;
        while (s < d.Length)
        {
            byte instNo = B(s++);
            if (instNo == 0) break;
            if (instNo > numInstruments) numInstruments = instNo;
            var inst = instruments[Math.Min(instNo - 1, kInstruments - 1)];
            if (version >= 2)
            {
                int nameLen = B(s++);
                inst.Name = Encoding.ASCII.GetString(d, Math.Min(s, d.Length), Math.Max(0, Math.Min(nameLen, d.Length - s)));
                s += nameLen;
                byte alg = B(s++);
                inst.Algorithm = (byte)(alg & 7);
                inst.Panning[0] = (byte)((alg >> 3) & 3);
                inst.Panning[1] = (byte)((alg >> 5) & 3);
                if (inst.Algorithm < 7)
                {
                    byte b = B(s++);
                    inst.Feedback[0] = (byte)(b & 15);
                    inst.Feedback[1] = (byte)(b >> 4);
                    b = B(s++);
                    inst.Detune = (byte)(b >> 4);
                    inst.RiffSpeed = (byte)(b & 15);
                    inst.Volume = B(s++);
                    for (int i = 0; i < 4; i++) for (int j = 0; j < 5; j++) inst.Operators[i][j] = B(s++);
                }
                else s += 6;   // MIDI 악기는 건너뜀
                if ((alg & 0x80) != 0)
                {
                    int size = B(s) | (B(s + 1) << 8);
                    s += 2;
                    inst.Riff = s;
                    s += size;
                }
                else inst.Riff = -1;
            }
            else
            {
                inst.Name = "";
                inst.Algorithm = (byte)(B(s + 8) & 1);
                inst.Panning[0] = inst.Panning[1] = 0;
                inst.Feedback[0] = (byte)((B(s + 8) >> 1) & 7);
                inst.Feedback[1] = 0;
                inst.Detune = 0; inst.RiffSpeed = 0; inst.Volume = 64;
                for (int k = 0; k < 4; k++) for (int j = 0; j < 5; j++) inst.Operators[k][j] = 0;
                inst.Operators[0][0] = B(s); inst.Operators[1][0] = B(s + 1);
                inst.Operators[0][1] = B(s + 2); inst.Operators[1][1] = B(s + 3);
                inst.Operators[0][2] = B(s + 4); inst.Operators[1][2] = B(s + 5);
                inst.Operators[0][3] = B(s + 6); inst.Operators[1][3] = B(s + 7);
                inst.Operators[0][4] = B(s + 9); inst.Operators[1][4] = B(s + 10);
                inst.Riff = -1;
                s += 11;
            }
        }

        orderListSize = B(s++);
        orderList = s;
        s += orderListSize;

        numTracks = 0;
        if (version >= 2)
        {
            while (s < d.Length)
            {
                byte trackNo = B(s++);
                if (trackNo >= kTracks) break;
                if (trackNo + 1 > numTracks) numTracks = trackNo + 1;
                int size = B(s) | (B(s + 1) << 8);
                s += 2;
                tracks[trackNo] = s;
                s += size;
            }
        }
        else
        {
            for (int i = 0; i < 32; i++)
            {
                int pos = B(s) | (B(s + 1) << 8);
                s += 2;
                if (pos != 0) { numTracks = i + 1; tracks[i] = pos; }
            }
        }

        if (version >= 2)
        {
            while (s < d.Length)
            {
                byte riffId = B(s++);
                int riffNum = riffId >> 4, chanNum = riffId & 15;
                if (riffNum >= kRiffTracks || chanNum > kChannels) break;
                int size = B(s) | (B(s + 1) << 8);
                s += 2;
                if (chanNum >= 1) riffs[riffNum, chanNum - 1] = s;
                s += size;
            }
        }

        for (int i = 0; i < 512; i++) opl3Regs[i] = 255;
        Stop();
        initialised = true;
    }

    public override void Rewind(int subsong) { Stop(); }

    private void Stop()
    {
        for (int reg = 0x20; reg < 0xF6; reg++)
        {
            int val = (reg >= 0x60 && reg < 0xA0) ? 0xFF : 0;
            SetOPL3(reg, val);
            SetOPL3(reg + 0x100, val);
        }
        SetOPL3(1, 0x20);
        SetOPL3(8, 0);
        SetOPL3(0xbd, 0);
        SetOPL3(0x104, 0);
        SetOPL3(0x105, 1);

        playTime = 0;
        repeating = false;
        for (int i = 0; i < 4; i++) orderMap[i] = 0;

        speedCnt = 1;
        order = 0;
        track = GetTrack();
        line = 0;
        entrances = 0;
        masterVol = 64;
        foreach (var chan in channels)
        {
            chan.LastInstrument = 0;
            chan.Instrument = null;
            chan.Volume = 0;
            chan.DetuneA = 0;
            chan.DetuneB = 0;
            chan.KeyFlags = 0;
            chan.Riff.SpeedCnt = 0;
            chan.IRiff.SpeedCnt = 0;
        }
    }

    /// <summary>AdPlug: update = !RADPlayer.Update() (되풀이를 만나면 false).</summary>
    public override bool Update() { return !UpdateRad(); }

    private bool UpdateRad()
    {
        if (!initialised) return false;
        for (int i = 0; i < kChannels; i++)
        {
            var chan = channels[i];
            TickRiff(i, chan.IRiff, false);
            TickRiff(i, chan.Riff, true);
        }
        PlayLine();
        for (int i = 0; i < kChannels; i++)
        {
            var chan = channels[i];
            ContinueFX(i, chan.IRiff.FX);
            ContinueFX(i, chan.Riff.FX);
            ContinueFX(i, chan.FX);
        }
        playTime++;
        return repeating;
    }

    // ------------------------------------------------------------ 음 풀기 · 트랙

    private bool UnpackNote(ref int s, ref byte lastInstrument)
    {
        byte chanid = B(s++);
        instNum = 0; effectNum = 0; param = 0;
        byte note = 0;
        if (version >= 2)
        {
            if ((chanid & 0x40) != 0)
            {
                byte n = B(s++);
                note = (byte)(n & 0x7F);
                if ((n & 0x80) != 0) instNum = lastInstrument;
            }
            if ((chanid & 0x20) != 0)
            {
                instNum = B(s++);
                lastInstrument = instNum;
            }
            if ((chanid & 0x10) != 0)
            {
                effectNum = B(s++);
                param = B(s++);
            }
        }
        else
        {
            byte n = B(s++);
            note = (byte)(n & 0x7f);
            if ((n & 0x80) != 0) instNum = 16;
            n = B(s++);
            instNum |= (byte)(n >> 4);
            if (instNum != 0) lastInstrument = instNum;
            effectNum = (byte)(n & 0xf);
            if (effectNum != 0) param = B(s++);
        }
        noteNum = (sbyte)(note & 15);
        octaveNum = (sbyte)(note >> 4);
        return (chanid & 0x80) != 0;
    }

    private int GetTrack()
    {
        if (order >= orderListSize) order = 0;
        int trackNo = B(orderList + order);
        if ((trackNo & 0x80) != 0)
        {
            order = (byte)(trackNo & 0x7F);
            trackNo = B(orderList + order) & 0x7F;
        }
        if (order < 128)
        {
            int idx = order >> 5;
            uint bit = 1u << (order & 31);
            if ((orderMap[idx] & bit) != 0) repeating = true;
            else orderMap[idx] |= bit;
        }
        return trackNo < kTracks ? tracks[trackNo] : -1;
    }

    private int SkipToLine(int trk, int lineNum, bool chanRiff)
    {
        if (trk < 0) return -1;
        while (trk < d.Length)
        {
            byte lineid = B(trk);
            if ((lineid & 0x7F) >= lineNum) return trk;
            if ((lineid & 0x80) != 0) break;
            trk++;
            byte chanid;
            do
            {
                chanid = B(trk++);
                if (version >= 2) trk += NoteSize[(chanid >> 4) & 7];
                else if ((B(trk + 1) & 0xf) != 0) trk += 3;
                else trk += 2;
            } while ((chanid & 0x80) == 0 && !chanRiff && trk < d.Length);
        }
        return -1;
    }

    private void PlayLine()
    {
        speedCnt--;
        if (speedCnt > 0) return;
        speedCnt = speed;
        for (int i = 0; i < kChannels; i++) ResetFX(channels[i].FX);
        lineJump = -1;

        int trk = track;
        if (trk >= 0 && (B(trk) & 0x7F) <= line)
        {
            byte lineid = B(trk++);
            bool last;
            int guard = 0;
            do
            {
                int channum = B(trk) & 15;
                if (channum >= kChannels) channum = kChannels - 1;
                var chan = channels[channum];
                last = UnpackNote(ref trk, ref chan.LastInstrument);
                PlayNote(channum, noteNum, octaveNum, instNum, effectNum, param);
            } while (!last && ++guard < 64 && trk < d.Length);
            if ((lineid & 0x80) != 0) trk = -1;
            track = trk;
        }

        line++;
        if (line >= kTrackLines || lineJump >= 0)
        {
            line = lineJump >= 0 ? (byte)lineJump : (byte)0;
            order++;
            track = GetTrack();
            if (line > 0) track = SkipToLine(track, line, false);
        }
    }

    // ------------------------------------------------------------ 음 · 효과

    private void PlayNote(int channum, sbyte notenum, sbyte octave, int instnum, int cmd = 0, int prm = 0, Src src = Src.None, int op = 0)
    {
        var chan = channels[channum];
        if (entrances >= 8) return;
        entrances++;

        var fx = src == Src.Riff ? chan.Riff.FX : src == Src.IRiff ? chan.IRiff.FX : chan.FX;
        bool transposing = false;

        if (cmd == cmToneSlide)
        {
            if (notenum > 0 && notenum <= 12)
            {
                fx.ToneSlideOct = (byte)octave;
                fx.ToneSlideFreq = NoteFreq[notenum - 1];
            }
            ToneSlide(channum, fx, prm);
            entrances--;
            return;
        }

        if (instnum > 0)
        {
            var oldinst = chan.Instrument;
            var inst = instruments[Math.Min(instnum - 1, kInstruments - 1)];
            chan.Instrument = inst;
            if (inst.Algorithm < 7)
            {
                LoadInstrumentOPL3(channum);
                chan.KeyFlags |= fKeyOff | fKeyOn;
                ResetFX(chan.IRiff.FX);
                if (src != Src.IRiff || inst != oldinst)
                {
                    if (inst.Riff >= 0 && inst.RiffSpeed > 0)
                    {
                        chan.IRiff.Track = chan.IRiff.TrackStart = inst.Riff;
                        chan.IRiff.Line = 0;
                        chan.IRiff.Speed = inst.RiffSpeed;
                        chan.IRiff.LastInstrument = 0;
                        if (notenum >= 1 && notenum <= 12)
                        {
                            chan.IRiff.TransposeOctave = octave;
                            chan.IRiff.TransposeNote = notenum;
                            transposing = true;
                        }
                        else { chan.IRiff.TransposeOctave = 3; chan.IRiff.TransposeNote = 12; }
                        chan.IRiff.SpeedCnt = 1;
                        TickRiff(channum, chan.IRiff, false);
                    }
                    else chan.IRiff.SpeedCnt = 0;
                }
            }
            else chan.Instrument = null;
        }

        if (cmd == cmRiff || cmd == cmTranspose)
        {
            ResetFX(chan.Riff.FX);
            int p0 = prm / 10, p1 = prm % 10;
            chan.Riff.Track = p1 > 0 && p0 < kRiffTracks ? riffs[p0, p1 - 1] : -1;
            if (chan.Riff.Track >= 0)
            {
                chan.Riff.TrackStart = chan.Riff.Track;
                chan.Riff.Line = 0;
                chan.Riff.Speed = speed;
                chan.Riff.LastInstrument = 0;
                if (cmd == cmTranspose && notenum >= 1 && notenum <= 12)
                {
                    chan.Riff.TransposeOctave = octave;
                    chan.Riff.TransposeNote = notenum;
                    transposing = true;
                }
                else { chan.Riff.TransposeOctave = 3; chan.Riff.TransposeNote = 12; }
                chan.Riff.SpeedCnt = 1;
                TickRiff(channum, chan.Riff, true);
            }
            else chan.Riff.SpeedCnt = 0;
        }

        if (!transposing && notenum > 0)
        {
            if (notenum == 15) chan.KeyFlags |= fKeyOff;
            if (chan.Instrument == null || chan.Instrument.Algorithm < 7) PlayNoteOPL3(channum, octave, notenum);
        }

        switch (cmd)
        {
            case cmSetVol: SetVolume(channum, (byte)prm); break;
            case cmSetSpeed:
                if (src == Src.None) { speed = (byte)prm; speedCnt = (byte)prm; }
                else if (src == Src.Riff) { chan.Riff.Speed = (byte)prm; chan.Riff.SpeedCnt = (byte)prm; }
                else { chan.IRiff.Speed = (byte)prm; chan.IRiff.SpeedCnt = (byte)prm; }
                break;
            case cmPortamentoUp: fx.PortSlide = (sbyte)prm; break;
            case cmPortamentoDwn: fx.PortSlide = (sbyte)-(sbyte)prm; break;
            case cmToneVolSlide:
            case cmVolSlide:
            {
                sbyte val = (sbyte)prm;
                if (val >= 50) val = (sbyte)-(val - 50);
                fx.VolSlide = val;
                if (cmd == cmToneVolSlide) ToneSlide(channum, fx, prm);
                break;
            }
            case cmJumpToLine:
                if (prm >= kTrackLines) break;
                if (src == Src.None) lineJump = (sbyte)prm;
                break;
            case cmMultiplier: if (src == Src.IRiff) LoadInstMultiplierOPL3(channum, op, (byte)prm); break;
            case cmVolume: if (src == Src.IRiff) LoadInstVolumeOPL3(channum, op, (byte)prm); break;
            case cmFeedback:
                if (src == Src.IRiff) LoadInstFeedbackOPL3(channum, prm / 10, (byte)(prm % 10));
                break;
        }
        entrances--;
    }

    /// <summary>원본의 toneslide: 라벨 (속도 바꾸고 방향 정하기).</summary>
    private void ToneSlide(int channum, Effects fx, int prm)
    {
        byte sp = (byte)prm;
        if (sp != 0) fx.ToneSlideSpeed = sp;
        GetSlideDir(channum, fx);
    }

    private void LoadInstrumentOPL3(int channum)
    {
        var chan = channels[channum];
        var inst = chan.Instrument;
        if (inst == null) return;
        int alg = inst.Algorithm;
        chan.Volume = inst.Volume;
        chan.DetuneA = (byte)((inst.Detune + 1) >> 1);
        chan.DetuneB = (byte)(inst.Detune >> 1);

        if (useOpl3 && channum < 6)
        {
            int mask = 1 << channum;
            SetOPL3(0x104, (GetOPL3(0x104) & ~mask) | (alg == 2 || alg == 3 ? mask : 0));
        }
        if (useOpl3)
        {
            SetOPL3(0xC0 + ChanOffsets3[channum], ((inst.Panning[1] ^ 3) << 4) | inst.Feedback[1] << 1 | (alg == 3 || alg == 5 || alg == 6 ? 1 : 0));
            SetOPL3(0xC0 + Chn2Offsets3[channum], ((inst.Panning[0] ^ 3) << 4) | inst.Feedback[0] << 1 | (alg == 1 || alg == 6 ? 1 : 0));
        }
        else SetOPL3(0xC0 + channum, ((inst.Panning[0] ^ 3) << 4) | inst.Feedback[0] << 1 | (alg == 1 ? 1 : 0));

        for (int i = 0; i < (useOpl3 ? 4 : 2); i++)
        {
            var op = (alg < 2 && i >= 2) ? BlankOp : inst.Operators[i];
            int reg = useOpl3 ? OpOffsets3[channum, i] : OpOffsets2[channum, i];
            int vol = ~op[1] & 0x3F;
            if (AlgCarriers[alg, i])
            {
                vol = (vol * inst.Volume / 64) & 0xFFFF;
                vol = (vol * masterVol / 64) & 0xFFFF;
            }
            SetOPL3(reg + 0x20, op[0]);
            SetOPL3(reg + 0x40, (op[1] & 0xC0) | ((vol ^ 0x3F) & 0x3F));
            SetOPL3(reg + 0x60, op[2]);
            SetOPL3(reg + 0x80, op[3]);
            SetOPL3(reg + 0xE0, op[4]);
        }
    }

    private void PlayNoteOPL3(int channum, sbyte octave, sbyte note)
    {
        var chan = channels[channum];
        int o1, o2;
        if (useOpl3) { o1 = ChanOffsets3[channum]; o2 = Chn2Offsets3[channum]; }
        else { o1 = 0; o2 = channum; }

        if ((chan.KeyFlags & fKeyOff) != 0)
        {
            chan.KeyFlags = (byte)(chan.KeyFlags & ~(fKeyOff | fKeyedOn));
            if (useOpl3) SetOPL3(0xB0 + o1, GetOPL3(0xB0 + o1) & ~0x20);
            SetOPL3(0xB0 + o2, GetOPL3(0xB0 + o2) & ~0x20);
        }
        if (note > 12) return;

        bool op4 = useOpl3 && chan.Instrument != null && chan.Instrument.Algorithm >= 2;
        int freq = NoteFreq[note - 1];
        int frq2 = freq;
        chan.CurrFreq = freq;
        chan.CurrOctave = octave;
        freq = (freq + chan.DetuneA) & 0xFFFF;
        frq2 = (frq2 - chan.DetuneB) & 0xFFFF;

        if (op4) SetOPL3(0xA0 + o1, frq2 & 0xFF);
        SetOPL3(0xA0 + o2, freq & 0xFF);
        if ((chan.KeyFlags & fKeyOn) != 0) chan.KeyFlags = (byte)((chan.KeyFlags & ~fKeyOn) | fKeyedOn);
        int keyed = (chan.KeyFlags & fKeyedOn) != 0 ? 0x20 : 0;
        if (op4) SetOPL3(0xB0 + o1, ((frq2 >> 8) | (octave << 2) | keyed) & 0xFF);
        else if (useOpl3) SetOPL3(0xB0 + o1, 0);
        SetOPL3(0xB0 + o2, ((freq >> 8) | (octave << 2) | keyed) & 0xFF);
    }

    private static void ResetFX(Effects fx) { fx.PortSlide = 0; fx.VolSlide = 0; fx.ToneSlideDir = 0; }

    private void TickRiff(int channum, Riff riff, bool chanRiff)
    {
        byte lineid;
        if (riff.SpeedCnt == 0) { ResetFX(riff.FX); return; }
        riff.SpeedCnt--;
        if (riff.SpeedCnt > 0) return;
        riff.SpeedCnt = riff.Speed;

        byte ln = riff.Line++;
        if (riff.Line >= kTrackLines) riff.SpeedCnt = 0;
        ResetFX(riff.FX);

        int trk = riff.Track;
        if (trk >= 0 && (B(trk) & 0x7F) == ln)
        {
            lineid = B(trk++);
            if (chanRiff)
            {
                UnpackNote(ref trk, ref riff.LastInstrument);
                Transpose(riff.TransposeNote, riff.TransposeOctave);
                PlayNote(channum, noteNum, octaveNum, instNum, effectNum, param, Src.Riff);
            }
            else
            {
                bool last;
                int guard = 0;
                do
                {
                    int col = B(trk) & 15;
                    last = UnpackNote(ref trk, ref riff.LastInstrument);
                    if (effectNum != cmIgnore) Transpose(riff.TransposeNote, riff.TransposeOctave);
                    PlayNote(channum, noteNum, octaveNum, instNum, effectNum, param, Src.IRiff, col > 0 ? (col - 1) & 3 : 0);
                } while (!last && ++guard < 64 && trk < d.Length);
            }
            if ((lineid & 0x80) != 0) trk = -1;
            riff.Track = trk;
        }

        if (trk < 0 || (B(trk++) & 0x7F) != riff.Line) return;
        byte dummy = 0;
        UnpackNote(ref trk, ref dummy);
        if (effectNum == cmJumpToLine && param < kTrackLines)
        {
            riff.Line = param;
            riff.Track = SkipToLine(riff.TrackStart, param, chanRiff);
        }
    }

    private void ContinueFX(int channum, Effects fx)
    {
        var chan = channels[channum];
        if (fx.PortSlide != 0) Portamento(channum, fx, fx.PortSlide, false);
        if (fx.VolSlide != 0)
        {
            sbyte vol = (sbyte)chan.Volume;
            vol = (sbyte)(vol - fx.VolSlide);
            if (vol < 0) vol = 0;
            SetVolume(channum, (byte)vol);
        }
        if (fx.ToneSlideDir != 0) Portamento(channum, fx, fx.ToneSlideDir, true);
    }

    private void SetVolume(int channum, byte vol)
    {
        var chan = channels[channum];
        if (vol > 64) vol = 64;
        chan.Volume = vol;
        vol = (byte)(vol * masterVol / 64);
        var inst = chan.Instrument;
        if (inst == null) return;
        int alg = inst.Algorithm;
        for (int i = 0; i < 4; i++)
        {
            var op = inst.Operators[i];
            if (!AlgCarriers[alg, i]) continue;
            byte opvol = (byte)(((op[1] & 63) ^ 63) * vol / 64);
            int reg = 0x40 + (useOpl3 ? OpOffsets3[channum, i] : OpOffsets2[channum, i]);
            SetOPL3(reg, (GetOPL3(reg) & 0xC0) | (opvol ^ 0x3F));
        }
    }

    private void GetSlideDir(int channum, Effects fx)
    {
        var chan = channels[channum];
        sbyte sp = (sbyte)fx.ToneSlideSpeed;
        if (sp > 0)
        {
            byte oct = fx.ToneSlideOct;
            int freq = fx.ToneSlideFreq;
            int oldfreq = chan.CurrFreq;
            byte oldoct = (byte)chan.CurrOctave;
            if (oldoct > oct) sp = (sbyte)-sp;
            else if (oldoct == oct)
            {
                if (oldfreq > freq) sp = (sbyte)-sp;
                else if (oldfreq == freq) sp = 0;
            }
        }
        fx.ToneSlideDir = sp;
    }

    private void LoadInstMultiplierOPL3(int channum, int op, byte mult)
    {
        int reg = 0x20 + OpOffsets3[channum, op];
        SetOPL3(reg, (GetOPL3(reg) & 0xF0) | (mult & 15));
    }

    private void LoadInstVolumeOPL3(int channum, int op, byte vol)
    {
        int reg = 0x40 + OpOffsets3[channum, op];
        SetOPL3(reg, (GetOPL3(reg) & 0xC0) | ((vol & 0x3F) ^ 0x3F));
    }

    private void LoadInstFeedbackOPL3(int channum, int which, byte fb)
    {
        if (which == 0) { int reg = 0xC0 + Chn2Offsets3[channum]; SetOPL3(reg, (GetOPL3(reg) & 0x31) | ((fb & 7) << 1)); }
        else if (which == 1) { int reg = 0xC0 + ChanOffsets3[channum]; SetOPL3(reg, (GetOPL3(reg) & 0x31) | ((fb & 7) << 1)); }
    }

    private void Portamento(int channum, Effects fx, sbyte amount, bool toneslide)
    {
        var chan = channels[channum];
        int freq = chan.CurrFreq;
        byte oct = (byte)chan.CurrOctave;
        freq = (freq + amount) & 0xFFFF;
        if (freq < 0x156)
        {
            if (oct > 0) { oct--; freq += 0x2AE - 0x156; }
            else freq = 0x156;
        }
        else if (freq > 0x2AE)
        {
            if (oct < 7) { oct++; freq -= 0x2AE - 0x156; }
            else freq = 0x2AE;
        }
        if (toneslide)
        {
            if (amount >= 0)
            {
                if (oct > fx.ToneSlideOct || (oct == fx.ToneSlideOct && freq >= fx.ToneSlideFreq)) { freq = fx.ToneSlideFreq; oct = fx.ToneSlideOct; }
            }
            else
            {
                if (oct < fx.ToneSlideOct || (oct == fx.ToneSlideOct && freq <= fx.ToneSlideFreq)) { freq = fx.ToneSlideFreq; oct = fx.ToneSlideOct; }
            }
        }
        chan.CurrFreq = freq;
        chan.CurrOctave = (sbyte)oct;
        int frq2 = (freq - chan.DetuneB) & 0xFFFF;
        freq = (freq + chan.DetuneA) & 0xFFFF;
        int co = useOpl3 ? Chn2Offsets3[channum] : channum;
        SetOPL3(0xA0 + co, freq & 0xFF);
        SetOPL3(0xB0 + co, ((freq >> 8 & 3) | oct << 2 | (GetOPL3(0xB0 + co) & 0xE0)) & 0xFF);
        if (useOpl3)
        {
            co = ChanOffsets3[channum];
            SetOPL3(0xA0 + co, frq2 & 0xFF);
            SetOPL3(0xB0 + co, ((frq2 >> 8 & 3) | oct << 2 | (GetOPL3(0xB0 + co) & 0xE0)) & 0xFF);
        }
    }

    private void Transpose(sbyte note, sbyte octave)
    {
        if (noteNum >= 1 && noteNum <= 12)
        {
            int toct = octave - 3;
            if (toct != 0)
            {
                int o = octaveNum + toct;
                octaveNum = (sbyte)(o < 0 ? 0 : o > 7 ? 7 : o);
            }
            int tnot = note - 12;
            if (tnot != 0)
            {
                int n = noteNum + tnot;
                if (n < 1)
                {
                    n += 12;
                    if (octaveNum > 0) octaveNum--;
                    else n = 1;
                }
                noteNum = (sbyte)n;
            }
        }
    }
}
