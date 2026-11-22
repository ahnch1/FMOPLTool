using System;
using System.IO;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// AdPlug binio 읽기 흉내 (작은 엔디언): 파일 끝을 넘으면 FFh를 돌려주고 오류 깃발 (AdPlug 로더가 끝 넘어 읽은 값까지 쓰므로 그대로).
/// </summary>
internal sealed class AdPlugReader
{
    private readonly byte[] d;
    public int Pos;
    public bool Error;
    public AdPlugReader(byte[] data) { d = data; }
    public int Length { get { return d.Length; } }
    public int Byte()
    {
        if (Pos >= 0 && Pos < d.Length) return d[Pos++];
        Pos++;
        Error = true;
        return 0xFF;
    }
    public long Int(int n)
    {
        long v = 0;
        for (int i = 0; i < n; i++) v |= (long)Byte() << (i * 8);
        return v;
    }
    public byte[] Bytes(int n) { var b = new byte[Math.Max(0, n)]; for (int i = 0; i < b.Length; i++) b[i] = (byte)Byte(); return b; }
    public void Ignore(int n) { for (int i = 0; i < n; i++) Byte(); }
    public bool AtEof { get { return Pos >= d.Length; } }
    public void Seek(int p) { Pos = p; }
    public float Float() { var b = Bytes(4); return BitConverter.ToSingle(b, 0); }
}

/// <summary>
/// AdPlug CmodPlayer (protrack.cpp, "Generic Protracker Player", Simon Peter) — 프로트래커식 형식 공통 재생기, LGPL 2.1을 옮긴 것 (이 파일도 LGPL 2.1).
/// 이것을 물려받는 형식: SA2 (Surprise! Adlib Tracker 2) · AMD (AMUSIC) · DTM (DeFy Adlib Tracker) · MAD (Mlat Adlib Tracker).
/// </summary>
public abstract class AdPlugModPlayer : OplPlayer
{
    protected const int Standard = 0, Decimal = 1, Faust = 2, NoKeyOn = 4, Opl3 = 8, Tremolo = 16, Vibrato = 32, Percussion = 64;
    private static readonly int[] Sa2NoteTable = { 340, 363, 385, 408, 432, 458, 485, 514, 544, 577, 611, 647 };
    private static readonly byte[] VibratoTab = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
    protected static readonly int[] OpTable = { 0x00, 0x01, 0x02, 0x08, 0x09, 0x0a, 0x10, 0x11, 0x12 };
    private const int JumpMarker = 0x80;

    protected sealed class Instrument
    {
        public readonly byte[] data = new byte[11];
        public byte arpstart, arpspeed, arppos, arpspdcnt, misc;
        public sbyte slide;
    }
    protected struct Track { public byte note, command, inst, param2, param1; }
    protected sealed class Channel
    {
        public ushort freq, nextfreq;
        public byte oct, vol1, vol2, inst, fx, info1, info2, key, nextoct, note, portainfo, vibinfo1, vibinfo2, arppos, arpspdcnt;
        public sbyte trigger;
    }

    protected Instrument[] inst;
    protected Track[][] tracks;
    protected byte[] order, arplist, arpcmd;
    protected byte initspeed = 6;
    protected ushort tempo, bpm, nop;
    protected ushort[][] trackord;
    protected long length, restartpos, activechan = 0xffffffff;
    protected int flags = Standard, curchip;
    protected Channel[] channel;

    private byte speed, del, songend, regbd;
    private readonly ushort[] notetable = new ushort[12];
    private long rw, ord, nrows, npats, nchans;

    protected AdPlugModPlayer()
    {
        ReallocOrder(128);
        ReallocPatterns(64, 64, 9);
        ReallocInstruments(250);
        InitNotetable(Sa2NoteTable);
    }

    public override void Load(byte[] data)
    {
        curchip = Opl.Chip;
        Parse(new AdPlugReader(data));
        Rewind(0);
    }

    /// <summary>형식마다 읽기 (실패면 InvalidDataException).</summary>
    private protected abstract void Parse(AdPlugReader f);

    protected void InitTrackord() { for (long i = 0; i < npats * nchans; i++) trackord[i / nchans][i % nchans] = (ushort)(i + 1); }
    protected void InitSpecialArp() { arplist = new byte[256]; arpcmd = new byte[256]; }
    protected void InitNotetable(int[] t) { for (int i = 0; i < 12; i++) notetable[i] = (ushort)t[i]; }
    protected void ReallocOrder(long len) { order = new byte[Math.Max(len, 1) + 256]; }
    protected void ReallocPatterns(long pats, long rows, long chans)
    {
        npats = pats; nrows = rows; nchans = chans;
        tracks = new Track[pats * chans][];
        for (long i = 0; i < pats * chans; i++) tracks[i] = new Track[rows];
        trackord = new ushort[pats][];
        for (long i = 0; i < pats; i++) trackord[i] = new ushort[chans];
        channel = new Channel[chans];
        for (long i = 0; i < chans; i++) channel[i] = new Channel();
    }
    protected void ReallocInstruments(long len) { inst = new Instrument[Math.Max(len, 1) + 256]; for (int i = 0; i < inst.Length; i++) inst[i] = new Instrument(); }

    public override float Refresh { get { return (float)(tempo / 2.5); } }

    public override void Rewind(int subsong)
    {
        songend = del = 0; ord = rw = 0; regbd = 0;
        tempo = bpm; speed = initspeed;
        for (long i = 0; i < nchans; i++) channel[i] = new Channel();
        if (nop == 0) for (long i = 0; i < length; i++) nop = order[i] > nop ? order[i] : nop;
        Opl.Init();
        Opl.Write(1, 32);
        if ((flags & Opl3) != 0) { Opl.SetChip(1); Opl.Write(1, 32); Opl.Write(5, 1); Opl.SetChip(0); }
        if ((flags & Tremolo) != 0) regbd |= 128;
        if ((flags & Vibrato) != 0) regbd |= 64;
        if (regbd != 0) Opl.Write(0xbd, regbd);
    }

    private Instrument Ins(int n) { return inst[n < inst.Length ? n : 0]; }
    private byte Order(long i) { return i >= 0 && i < order.Length ? order[i] : (byte)0; }

    public override bool Update()
    {
        byte pattbreak = 0, donote, pattnr, oplchan, info1, info2, info, patternDelay;
        if (speed == 0) return false;

        for (int chan = 0; chan < nchans; chan++)
        {
            var ch = channel[chan];
            oplchan = SetOplChip(chan);
            if (arplist != null && arpcmd != null && Ins(ch.inst).arpstart != 0)
            {
                if (ch.arpspdcnt != 0) ch.arpspdcnt--;
                else if (arpcmd[ch.arppos] != 255)
                {
                    switch (arpcmd[ch.arppos])
                    {
                        case 252:
                            ch.vol1 = arplist[ch.arppos];
                            if (ch.vol1 > 63) ch.vol1 = 63;
                            ch.vol2 = ch.vol1;
                            SetVolume(chan);
                            break;
                        case 253: ch.key = 0; SetFreq(chan); break;
                        case 254: ch.arppos = arplist[ch.arppos]; break;
                        default:
                            if (arpcmd[ch.arppos] != 0)
                            {
                                if (arpcmd[ch.arppos] / 10 != 0) Opl.Write(0xe3 + OpTable[oplchan], arpcmd[ch.arppos] / 10 - 1);
                                if (arpcmd[ch.arppos] % 10 != 0) Opl.Write(0xe0 + OpTable[oplchan], (arpcmd[ch.arppos] % 10) - 1);
                                if (arpcmd[ch.arppos] < 10) Opl.Write(0xe0 + OpTable[oplchan], arpcmd[ch.arppos] - 1);
                            }
                            break;
                    }
                    if (arpcmd[ch.arppos] != 252)
                    {
                        if (arplist[ch.arppos] <= 96) SetNote(chan, ch.note + arplist[ch.arppos]);
                        if (arplist[ch.arppos] >= 100) SetNote(chan, arplist[ch.arppos] - 100);
                    }
                    else SetNote(chan, ch.note);
                    SetFreq(chan);
                    if (arpcmd[ch.arppos] != 255) ch.arppos++;
                    ch.arpspdcnt = (byte)(Ins(ch.inst).arpspeed - 1);
                }
            }

            info1 = ch.info1; info2 = ch.info2;
            info = (flags & Decimal) != 0 ? (byte)(ch.info1 * 10 + ch.info2) : (byte)((ch.info1 << 4) + ch.info2);
            switch (ch.fx)
            {
                case 0:
                    if (info != 0)
                    {
                        if (ch.trigger < 2) ch.trigger++; else ch.trigger = 0;
                        switch (ch.trigger)
                        {
                            case 0: SetNote(chan, ch.note); break;
                            case 1: SetNote(chan, ch.note + info1); break;
                            case 2: SetNote(chan, ch.note + info2); break;
                        }
                        SetFreq(chan);
                    }
                    break;
                case 1: SlideUp(chan, info); SetFreq(chan); break;
                case 2: SlideDown(chan, info); SetFreq(chan); break;
                case 3: TonePortamento(chan, ch.portainfo); break;
                case 4: VibratoFx(chan, ch.vibinfo1, ch.vibinfo2); break;
                case 5:
                case 6:
                case 10:
                    if (ch.fx == 5) TonePortamento(chan, ch.portainfo);
                    else if (ch.fx == 6) VibratoFx(chan, ch.vibinfo1, ch.vibinfo2);
                    if (del % 4 != 0) break;
                    if (info1 != 0) VolUp(chan, info1); else VolDown(chan, info2);
                    SetVolume(chan);
                    break;
                case 14:
                    if (info1 == 3 && del % (info2 + 1) == 0) PlayNote(chan);
                    break;
                case 16:
                    if (del % 4 != 0) break;
                    if (info1 != 0) VolUpAlt(chan, info1); else VolDownAlt(chan, info2);
                    SetVolume(chan);
                    break;
                case 20:
                    if (info < 50) VolDownAlt(chan, info); else VolUpAlt(chan, info - 50);
                    SetVolume(chan);
                    break;
                case 26:
                    if (info1 != 0) VolUp(chan, info1); else VolDown(chan, info2);
                    SetVolume(chan);
                    break;
                case 28:
                    if (info1 != 0) { SlideUp(chan, 1); ch.info1--; }
                    if (info2 != 0) { SlideDown(chan, 1); ch.info2--; }
                    SetFreq(chan);
                    break;
            }
        }

        if (del != 0) { del--; return songend == 0; }

        if (!ResolveOrder()) return songend == 0;
        pattnr = Order(ord);

        patternDelay = 0;
        long row = rw;
        for (int chan = 0; chan < nchans; chan++)
        {
            var ch = channel[chan];
            oplchan = SetOplChip(chan);
            if (((activechan >> (31 - chan)) & 1) == 0) continue;
            if (pattnr >= trackord.Length) continue;
            int track = trackord[pattnr][chan];
            if (track == 0) continue;
            track--;
            if (track >= tracks.Length || row >= tracks[track].Length) continue;
            var t = tracks[track][row];

            donote = 0;
            if (t.inst != 0)
            {
                ch.inst = (byte)(t.inst - 1);
                if ((flags & Faust) == 0)
                {
                    ch.vol1 = (byte)(63 - (Ins(ch.inst).data[10] & 63));
                    ch.vol2 = (byte)(63 - (Ins(ch.inst).data[9] & 63));
                    SetVolume(chan);
                }
            }
            if (t.note != 0 && t.command != 3)
            {
                ch.note = t.note;
                SetNote(chan, t.note);
                ch.nextfreq = ch.freq;
                ch.nextoct = ch.oct;
                ch.arppos = Ins(ch.inst).arpstart;
                ch.arpspdcnt = 0;
                if (t.note != 127) donote = 1;
            }
            ch.fx = t.command;
            ch.info1 = t.param1;
            ch.info2 = t.param2;
            if (donote != 0) PlayNote(chan);

            info1 = ch.info1; info2 = ch.info2;
            info = (flags & Decimal) != 0 ? (byte)(ch.info1 * 10 + ch.info2) : (byte)((ch.info1 << 4) + ch.info2);
            switch (ch.fx)
            {
                case 3:
                    if (t.note != 0)
                    {
                        if (t.note < 13) ch.nextfreq = notetable[t.note - 1];
                        else if (t.note % 12 > 0) ch.nextfreq = notetable[(t.note % 12) - 1];
                        else ch.nextfreq = notetable[11];
                        ch.nextoct = (byte)((t.note - 1) / 12);
                        if (t.note == 127) { ch.nextfreq = ch.freq; ch.nextoct = ch.oct; }
                    }
                    if (info != 0) ch.portainfo = info;
                    break;
                case 4:
                    if (info != 0) { ch.vibinfo1 = info1; ch.vibinfo2 = info2; }
                    break;
                case 7: tempo = info; break;
                case 8: ch.key = 0; SetFreq(chan); break;
                case 9:
                    if (info1 != 0) ch.vol1 = (byte)(info1 * 7); else ch.vol2 = (byte)(info2 * 7);
                    SetVolume(chan);
                    break;
                case 11:
                    pattbreak = 1; rw = 0;
                    if (info <= ord) songend = 1;
                    ord = info;
                    break;
                case 12:
                    ch.vol1 = info; ch.vol2 = info;
                    if (ch.vol1 > 63) ch.vol1 = 63;
                    if (ch.vol2 > 63) ch.vol2 = 63;
                    SetVolume(chan);
                    break;
                case 13:
                    if (pattbreak == 0) { pattbreak = 1; rw = info < nrows ? info : 0; ord++; }
                    break;
                case 14:
                    switch (info1)
                    {
                        case 0: if (info2 != 0) regbd |= 128; else regbd &= 127; Opl.Write(0xbd, regbd); break;
                        case 1: if (info2 != 0) regbd |= 64; else regbd &= 191; Opl.Write(0xbd, regbd); break;
                        case 4: VolUpAlt(chan, info2); SetVolume(chan); break;
                        case 5: VolDownAlt(chan, info2); SetVolume(chan); break;
                        case 6: SlideUp(chan, info2); SetFreq(chan); break;
                        case 7: SlideDown(chan, info2); SetFreq(chan); break;
                        case 8: patternDelay = (byte)(info2 * speed); break;
                    }
                    break;
                case 15:
                    if (info <= 0x1f) speed = info;
                    if (info >= 0x32) tempo = info;
                    if (info == 0) songend = 1;
                    break;
                case 17:
                    ch.vol1 = info;
                    if (ch.vol1 > 63) ch.vol1 = 63;
                    if ((Ins(ch.inst).data[0] & 1) != 0) { ch.vol2 = info; if (ch.vol2 > 63) ch.vol2 = 63; }
                    SetVolume(chan);
                    break;
                case 18:
                    if (info <= 31 && info > 0) speed = info;
                    if (info > 31 || info == 0) tempo = info;
                    break;
                case 19: speed = (byte)(info != 0 ? info : info + 1); break;
                case 21: ch.vol2 = (byte)(info <= 63 ? info : 63); SetVolume(chan); break;
                case 22: ch.vol1 = (byte)(info <= 63 ? info : 63); SetVolume(chan); break;
                case 23: SlideUp(chan, info); SetFreq(chan); break;
                case 24: SlideDown(chan, info); SetFreq(chan); break;
                case 25:
                    if (info1 != 0x0f) Opl.Write(0xe3 + OpTable[oplchan], info1);
                    if (info2 != 0x0f) Opl.Write(0xe0 + OpTable[oplchan], info2);
                    break;
                case 27:
                    if (info1 != 0) regbd |= 128; else regbd &= 127;
                    if (info2 != 0) regbd |= 64; else regbd &= 191;
                    Opl.Write(0xbd, regbd);
                    break;
                case 29: patternDelay = info; break;
            }
        }

        del = (byte)(speed - 1 + patternDelay);
        if (pattbreak == 0)
        {
            rw++;
            if (rw >= nrows) { rw = 0; ord++; }
        }
        ResolveOrder();
        return songend == 0;
    }

    private byte SetOplChip(int chan)
    {
        int newchip = chan < 9 ? 0 : 1;
        if (newchip != curchip) { Opl.SetChip(newchip); curchip = newchip; }
        return (byte)(chan % 9);
    }

    private bool ResolveOrder()
    {
        if (ord < length)
        {
            int guard = 0;
            while (Order(ord) >= JumpMarker)
            {
                long neword = Order(ord) - JumpMarker;
                if (neword <= ord) songend = 1;
                if (neword == ord || ++guard > 256) return false;
                ord = neword;
            }
        }
        else { songend = 1; ord = restartpos; }
        return true;
    }

    private void SetVolume(int chan)
    {
        byte oplchan = SetOplChip(chan);
        if ((flags & Faust) != 0) { SetVolumeAlt(chan); return; }
        var ch = channel[chan];
        Opl.Write(0x40 + OpTable[oplchan], 63 - ch.vol2 + (Ins(ch.inst).data[9] & 192));
        Opl.Write(0x43 + OpTable[oplchan], 63 - ch.vol1 + (Ins(ch.inst).data[10] & 192));
    }

    private void SetVolumeAlt(int chan)
    {
        byte oplchan = SetOplChip(chan);
        var ch = channel[chan];
        int ivol2 = Ins(ch.inst).data[9] & 63, ivol1 = Ins(ch.inst).data[10] & 63;
        Opl.Write(0x40 + OpTable[oplchan], (((63 - (ch.vol2 & 63)) + ivol2) >> 1) + (Ins(ch.inst).data[9] & 192));
        Opl.Write(0x43 + OpTable[oplchan], (((63 - (ch.vol1 & 63)) + ivol1) >> 1) + (Ins(ch.inst).data[10] & 192));
    }

    private void SetFreq(int chan)
    {
        byte oplchan = SetOplChip(chan);
        var ch = channel[chan];
        Opl.Write(0xa0 + oplchan, ch.freq & 255);
        int v = ((ch.freq & 768) >> 8) + (ch.oct << 2);
        Opl.Write(0xb0 + oplchan, ch.key != 0 ? v | 32 : v);
    }

    private void PlayNote(int chan)
    {
        byte oplchan = SetOplChip(chan);
        int op = OpTable[oplchan];
        var ch = channel[chan];
        var ins = Ins(ch.inst);
        if ((flags & NoKeyOn) == 0) Opl.Write(0xb0 + oplchan, 0);
        Opl.Write(0x20 + op, ins.data[1]);
        Opl.Write(0x23 + op, ins.data[2]);
        Opl.Write(0x60 + op, ins.data[3]);
        Opl.Write(0x63 + op, ins.data[4]);
        Opl.Write(0x80 + op, ins.data[5]);
        Opl.Write(0x83 + op, ins.data[6]);
        Opl.Write(0xe0 + op, ins.data[7]);
        Opl.Write(0xe3 + op, ins.data[8]);
        Opl.Write(0xc0 + oplchan, ins.data[0]);
        Opl.Write(0xbd, ins.misc);
        ch.key = 1;
        SetFreq(chan);
        if ((flags & Faust) != 0) { ch.vol2 = 63; ch.vol1 = 63; }
        SetVolume(chan);
    }

    private void SetNote(int chan, int note)
    {
        var ch = channel[chan];
        if (note == 127) { ch.key = 0; SetFreq(chan); return; }
        if (note > 96) note = 96; else if (note < 1) note = 1;
        ch.freq = notetable[(note - 1) % 12];
        ch.oct = (byte)((note - 1) / 12);
        ch.freq = (ushort)(ch.freq + Ins(ch.inst).slide);
    }

    private void SlideDown(int chan, int amount)
    {
        var ch = channel[chan];
        ch.freq = (ushort)(ch.freq - amount);
        if (ch.freq <= 342)
        {
            if (ch.oct != 0) { ch.oct--; ch.freq = (ushort)(ch.freq << 1); }
            else ch.freq = 342;
        }
    }

    private void SlideUp(int chan, int amount)
    {
        var ch = channel[chan];
        ch.freq = (ushort)(ch.freq + amount);
        if (ch.freq >= 686)
        {
            if (ch.oct < 7) { ch.oct++; ch.freq >>= 1; }
            else ch.freq = 686;
        }
    }

    private void TonePortamento(int chan, byte info)
    {
        var ch = channel[chan];
        if (ch.freq + (ch.oct << 10) < ch.nextfreq + (ch.nextoct << 10))
        {
            SlideUp(chan, info);
            if (ch.freq + (ch.oct << 10) > ch.nextfreq + (ch.nextoct << 10)) { ch.freq = ch.nextfreq; ch.oct = ch.nextoct; }
        }
        if (ch.freq + (ch.oct << 10) > ch.nextfreq + (ch.nextoct << 10))
        {
            SlideDown(chan, info);
            if (ch.freq + (ch.oct << 10) < ch.nextfreq + (ch.nextoct << 10)) { ch.freq = ch.nextfreq; ch.oct = ch.nextoct; }
        }
        SetFreq(chan);
    }

    private void VibratoFx(int chan, byte spd, byte depth)
    {
        var ch = channel[chan];
        if (spd == 0 || depth == 0) return;
        if (depth > 14) depth = 14;
        for (int i = 0; i < spd; i++)
        {
            ch.trigger++;
            while (ch.trigger >= 64) ch.trigger -= 64;
            if (ch.trigger >= 16 && ch.trigger < 48) SlideDown(chan, VibratoTab[ch.trigger - 16] / (16 - depth));
            if (ch.trigger < 16) SlideUp(chan, VibratoTab[ch.trigger + 16] / (16 - depth));
            if (ch.trigger >= 48) SlideUp(chan, VibratoTab[ch.trigger - 48] / (16 - depth));
        }
        SetFreq(chan);
    }

    private void VolUp(int chan, int amount)
    {
        var ch = channel[chan];
        ch.vol1 = (byte)(ch.vol1 + amount < 63 ? ch.vol1 + amount : 63);
        ch.vol2 = (byte)(ch.vol2 + amount < 63 ? ch.vol2 + amount : 63);
    }

    private void VolDown(int chan, int amount)
    {
        var ch = channel[chan];
        ch.vol1 = (byte)(ch.vol1 - amount > 0 ? ch.vol1 - amount : 0);
        ch.vol2 = (byte)(ch.vol2 - amount > 0 ? ch.vol2 - amount : 0);
    }

    private void VolUpAlt(int chan, int amount)
    {
        var ch = channel[chan];
        ch.vol1 = (byte)(ch.vol1 + amount < 63 ? ch.vol1 + amount : 63);
        if ((Ins(ch.inst).data[0] & 1) != 0) ch.vol2 = (byte)(ch.vol2 + amount < 63 ? ch.vol2 + amount : 63);
    }

    private void VolDownAlt(int chan, int amount)
    {
        var ch = channel[chan];
        ch.vol1 = (byte)(ch.vol1 - amount > 0 ? ch.vol1 - amount : 0);
        if ((Ins(ch.inst).data[0] & 1) != 0) ch.vol2 = (byte)(ch.vol2 - amount > 0 ? ch.vol2 - amount : 0);
    }

    protected static string Str(byte[] b, int at, int n)
    {
        int len = 0;
        while (len < n && at + len < b.Length && b[at + len] != 0) len++;
        return KoreanText.ShiftJis.GetString(b, at, len).Trim();
    }
}

/// <summary>SA2 (Surprise! Adlib Tracker 2, 판 1~9). AdPlug sa2.cpp (LGPL 2.1).</summary>
public sealed class Sa2Player : AdPlugModPlayer
{
    private int version;
    private readonly byte[][] instname = new byte[29][];
    public override string TypeName { get { return "Surprise! Adlib Tracker 2 (version " + version + ")"; } }

    private protected override void Parse(AdPlugReader f)
    {
        var id = f.Bytes(4);
        version = f.Byte();
        if (Encoding.ASCII.GetString(id) != "SAdT") throw new InvalidDataException("SA2 파일이 아닙니다 (SAdT가 없음).");
        const int ARP = 1 << 7, V7 = 1 << 6, ACT = 1 << 5, TRK = 1 << 4, ARPI = 1 << 3, OLDBPM = 1 << 2, OLDPAT = 1 << 1, U127 = 1;
        int notedis = 0, t;
        switch (version)
        {
            case 1: notedis = 0x18; t = U127 | OLDPAT | OLDBPM; break;
            case 2: notedis = 0x18; t = OLDPAT | OLDBPM; break;
            case 3: notedis = 0x0c; t = OLDPAT | OLDBPM; break;
            case 4: notedis = 0x0c; t = ARPI | OLDPAT | OLDBPM; break;
            case 5: notedis = 0x0c; t = ARPI | ARP | OLDPAT | OLDBPM; break;
            case 6: t = ARPI | ARP | OLDPAT | OLDBPM; break;
            case 7: t = ARPI | ARP | V7; break;
            case 8: t = ARPI | ARP | TRK; break;
            case 9: t = ARPI | ARP | TRK | ACT; break;
            default: throw new InvalidDataException("SA2 판을 모릅니다: " + version);
        }
        byte[] convfx = { 0, 1, 2, 3, 4, 5, 6, 255, 8, 255, 10, 11, 12, 13, 255, 15 };
        for (int i = 0; i < 31; i++)
        {
            for (int j = 0; j < 11; j++) inst[i].data[j] = (byte)f.Byte();
            if ((t & ARPI) != 0) { inst[i].arpstart = (byte)f.Byte(); inst[i].arpspeed = (byte)f.Byte(); inst[i].arppos = (byte)f.Byte(); inst[i].arpspdcnt = (byte)f.Byte(); }
            inst[i].misc = 0; inst[i].slide = 0;
        }
        for (int i = 0; i < 29; i++) instname[i] = f.Bytes(17);
        f.Ignore(3);
        for (int i = 0; i < 128; i++) order[i] = (byte)f.Byte();
        if ((t & U127) != 0) f.Ignore(127);
        nop = (ushort)f.Int(2); length = f.Byte(); restartpos = f.Byte();
        if (nop < 1 || nop > 64 || length < 1 || length > 128 || restartpos >= length) throw new InvalidDataException("SA2 머리말 값이 이상합니다.");
        for (int i = 0; i < length; i++) if (order[i] >= nop) throw new InvalidDataException("SA2 순서표가 패턴 밖을 가리킵니다.");
        bpm = (ushort)f.Int(2);
        if ((t & OLDBPM) != 0) bpm = (ushort)(bpm * 125 / 50);
        if ((t & ARP) != 0)
        {
            InitSpecialArp();
            for (int i = 0; i < 256; i++) arplist[i] = (byte)f.Byte();
            for (int i = 0; i < 256; i++) arpcmd[i] = (byte)f.Byte();
        }
        for (int i = 0; i < 64; i++)
            for (int j = 0; j < 9; j++)
                trackord[i][j] = (t & TRK) != 0 ? (ushort)f.Byte() : (ushort)(i * 9 + j);
        if ((t & ACT) != 0) activechan = f.Int(2) << 16;

        if ((t & OLDPAT) != 0)
        {
            for (int i = 0; i < 64 * 9 && !f.AtEof; i += 9)
                for (int j = 0; j < 64; j++)
                    for (int k = 0; k < 9; k++)
                    {
                        int buf = f.Byte();
                        tracks[i + k][j].note = (byte)(buf != 0 ? buf + notedis : 0);
                        tracks[i + k][j].inst = (byte)f.Byte();
                        tracks[i + k][j].command = convfx[f.Byte() & 0xf];
                        tracks[i + k][j].param1 = (byte)f.Byte();
                        tracks[i + k][j].param2 = (byte)f.Byte();
                    }
        }
        else if ((t & V7) != 0)
        {
            for (int i = 0; i < 64 * 9 && !f.AtEof; i += 9)
                for (int j = 0; j < 64; j++)
                    for (int k = 0; k < 9; k++) ReadV8(f, ref tracks[i + k][j], convfx);
        }
        else
        {
            for (int i = 0; i < 64 * 9 && !f.AtEof; i++)
                for (int j = 0; j < 64; j++) ReadV8(f, ref tracks[i][j], convfx);
        }
    }

    private static void ReadV8(AdPlugReader f, ref Track tr, byte[] convfx)
    {
        int buf = f.Byte();
        tr.note = (byte)(buf >> 1);
        tr.inst = (byte)((buf & 1) << 4);
        buf = f.Byte();
        tr.inst = (byte)(tr.inst + (buf >> 4));
        tr.command = convfx[buf & 0x0f];
        buf = f.Byte();
        tr.param1 = (byte)(buf >> 4);
        tr.param2 = (byte)(buf & 0x0f);
    }

    /// <summary>악기 이름 칸에 "로 둘러 쓴 곡 제목 (AdPlug gettitle과 같이).</summary>
    public override string Title
    {
        get
        {
            var names = new byte[29][];
            for (int i = 0; i < 29; i++) { names[i] = (byte[])(instname[i] ?? new byte[17]).Clone(); for (int j = 0; j < 17; j++) if (names[i][j] == 0) names[i][j] = (byte)' '; }
            int si = -1, sj = -1;
            for (int i = 0; i < 29 && si < 0; i++) for (int j = 1; j < 17; j++) if (names[i][j] == '"') { si = i; sj = j; break; }
            if (si < 0) return "";
            var buf = new StringBuilder();
            int len = 0, spaces = 0;
            bool first = true;
            for (int i = si; i < 29; i++)
            {
                for (int j = first ? sj + 1 : 1; j < 17; j++)
                {
                    if (names[i][j] == ' ') spaces++; else spaces = 0;
                    if (names[i][j] == '"') len = buf.Length;
                    buf.Append((char)names[i][j]);
                }
                first = false;
                buf.Length -= Math.Min(spaces, buf.Length);
                spaces = 1;
                buf.Append(' ');
            }
            return buf.ToString(0, Math.Min(len, buf.Length)).Trim();
        }
    }
}

/// <summary>AMD (AMUSIC Adlib Tracker, Elyssis). AdPlug amd.cpp (LGPL 2.1).</summary>
public sealed class AmdPlayer : AdPlugModPlayer
{
    private string songname = "", author = "";
    public override string TypeName { get { return "AMUSIC Adlib Tracker"; } }
    public override string Title { get { return songname; } }
    public override string Author { get { return author; } }
    public override float Refresh { get { return tempo != 0 ? tempo : 18.2f; } }

    private protected override void Parse(AdPlugReader f)
    {
        if (f.Length < 1062 + 10) throw new InvalidDataException("AMD 파일이 너무 짧습니다.");
        f.Seek(1062);
        string id = Encoding.Latin1.GetString(f.Bytes(9));
        if (id != "<o\xefQU\xeeRoR" && id != "MaDoKaN96") throw new InvalidDataException("AMD 파일이 아닙니다.");
        int version = f.Byte();
        f.Seek(0);
        var sn = f.Bytes(24); var au = f.Bytes(24);
        songname = Str(sn, 0, 24); author = Str(au, 0, 24);
        for (int i = 0; i < 26; i++)
        {
            f.Ignore(23);
            int[] order11 = { 1, 9, 3, 5, 7, 2, 10, 4, 6, 8, 0 };
            foreach (int k in order11) inst[i].data[k] = (byte)f.Byte();
        }
        length = f.Byte();
        nop = (ushort)(f.Byte() + 1);
        if (length > 128 || nop > 64) throw new InvalidDataException("AMD 머리말 값이 이상합니다.");
        var ord = f.Bytes(128);
        Array.Copy(ord, order, 128);
        for (int i = 0; i < length; i++) if ((order[i] & 0x7f) >= 64) throw new InvalidDataException("AMD 순서표가 이상합니다.");
        f.Ignore(10);
        int maxi = 0;
        if (version == 0x10)
        {
            InitTrackord();
            maxi = nop * 9;
            for (int t = 0; t < maxi && !f.AtEof; t += 9)
                for (int j = 0; j < 64; j++)
                    for (int i = t; i < t + 9; i++) ReadEvent(f, ref tracks[i][j], f.Byte() & 0x7f);
        }
        else
        {
            for (int i = 0; i < nop; i++)
                for (int j = 0; j < 9; j++)
                {
                    trackord[i][j] = (ushort)(f.Int(2) + 1);
                    if (trackord[i][j] > 64 * 9) trackord[i][j] = 0;
                }
            int numtrax = (int)f.Int(2);
            for (int k = 0; k < numtrax; k++)
            {
                int i = Math.Min((int)f.Int(2), 64 * 9 - 1);
                maxi = Math.Max(i + 1, maxi);
                for (int j = 0; j < 64; j++)
                {
                    int buf = f.Byte();
                    if ((buf & 0x80) != 0)
                    {
                        int len = Math.Min(buf & 0x7f, 64 - j);
                        for (int z = 0; z < len; z++) tracks[i][j + z] = default;
                        j += len - 1;
                        continue;
                    }
                    ReadEvent(f, ref tracks[i][j], buf);
                }
            }
        }
        bpm = 50; restartpos = 0; flags = Decimal;
        byte[] convfx = { 0, 1, 2, 9, 17, 11, 13, 18, 3, 14 };
        byte[] convvol = {
            0x00, 0x00, 0x00, 0x01, 0x01, 0x02, 0x02, 0x03, 0x03, 0x04, 0x04, 0x05, 0x05, 0x06, 0x06, 0x07,
            0x07, 0x08, 0x09, 0x09, 0x0a, 0x0a, 0x0b, 0x0c, 0x0c, 0x0d, 0x0e, 0x0e, 0x0f, 0x10, 0x10, 0x11,
            0x12, 0x13, 0x14, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x21,
            0x22, 0x23, 0x25, 0x26, 0x28, 0x29, 0x2b, 0x2d, 0x2e, 0x30, 0x32, 0x35, 0x37, 0x3a, 0x3c, 0x3f };
        for (int i = 0; i < maxi; i++)
            for (int j = 0; j < 64; j++)
            {
                ref var tr = ref tracks[i][j];
                tr.command = tr.command < convfx.Length ? convfx[tr.command] : (byte)0;
                if (tr.command == 14)
                {
                    if (tr.param1 == 2) { tr.command = 10; tr.param1 = tr.param2; tr.param2 = 0; }
                    if (tr.param1 == 3) { tr.command = 10; tr.param1 = 0; }
                }
                if (tr.command == 17)
                {
                    int vol = tr.param1 * 10 + tr.param2;
                    vol = vol < convvol.Length ? convvol[vol] : 63;
                    tr.param1 = (byte)(vol / 10); tr.param2 = (byte)(vol % 10);
                }
            }
    }

    private static void ReadEvent(AdPlugReader f, ref Track tr, int buf)
    {
        tr.param1 = (byte)(buf / 10);
        tr.param2 = (byte)(buf % 10);
        buf = f.Byte();
        tr.command = (byte)(buf & 0x0f);
        tr.inst = (byte)(buf >> 4);
        buf = f.Byte();
        tr.inst = (byte)(tr.inst + ((buf & 1) << 4));
        tr.note = (buf >> 4) != 0 ? (byte)(((buf & 0x0e) >> 1) * 12 + (buf >> 4)) : (byte)0;
    }
}

/// <summary>DTM (DeFy Adlib Tracker). AdPlug dtm.cpp (LGPL 2.1).</summary>
public sealed class DtmPlayer : AdPlugModPlayer
{
    private string title = "", author = "", desc = "";
    public override string TypeName { get { return "DeFy Adlib Tracker"; } }
    public override string Title { get { return title; } }
    public override string Author { get { return author; } }
    public override string Description { get { return desc; } }
    public override float Refresh { get { return 18.2f; } }

    private protected override void Parse(AdPlugReader f)
    {
        byte[] convInst = { 2, 1, 10, 9, 4, 3, 6, 5, 0, 8, 7 };
        int[] convNote = { 0x16B, 0x181, 0x198, 0x1B0, 0x1CA, 0x1E5, 0x202, 0x220, 0x241, 0x263, 0x287, 0x2AE };
        const int N_CHAN = 9, N_ROW = 64, N_ORD = 100, MAX_INST = 128, DESC_COLS = 80, DESC_ROWS = 16;
        var id = f.Bytes(12);
        int version = f.Byte();
        var tb = f.Bytes(20); var ab = f.Bytes(20);
        title = Str(tb, 0, 19); author = Str(ab, 0, 19);
        int numpat = f.Byte();
        int numinst = f.Byte() + 1;
        if (Encoding.ASCII.GetString(id, 0, 9) != "DeFy DTM " || version != 0x10 || numinst > MAX_INST || numinst < N_CHAN || numpat == 0 || f.Error)
            throw new InvalidDataException("DTM 파일이 아닙니다.");
        var sb = new StringBuilder();
        int room = DESC_COLS * DESC_ROWS - 1;
        for (int i = 0; i < DESC_ROWS; i++)
        {
            int len = f.Byte();
            if (len > DESC_COLS) throw new InvalidDataException("DTM 설명 줄이 너무 깁니다.");
            int maxLen = room;
            int discard = len > maxLen ? len - maxLen : 0;
            len -= discard;
            if (len > 0)
            {
                var b = f.Bytes(len);
                for (int j = 0; j < len; j++) sb.Append(b[j] == 0 ? ' ' : (char)b[j]);
                room -= len;
                if (discard > 0) f.Ignore(discard);
            }
            if (len < maxLen) { sb.Append('\n'); room--; }
        }
        desc = sb.ToString().TrimEnd();

        ReallocInstruments(numinst);
        ReallocOrder(N_ORD);
        ReallocPatterns(numpat, N_ROW, N_CHAN);
        InitNotetable(convNote);
        InitTrackord();
        for (int i = 0; i < numinst; i++)
        {
            int nameLen = f.Byte();
            if (nameLen >= 13) throw new InvalidDataException("DTM 악기 이름이 너무 깁니다.");
            if (nameLen > 0) f.Ignore(nameLen);
            var data = f.Bytes(12);
            for (int j = 0; j < 11; j++) inst[i].data[convInst[j]] = data[j];
        }
        var ord = f.Bytes(N_ORD);
        Array.Copy(ord, order, N_ORD);
        nop = (ushort)numpat;
        var pattern = new byte[N_ROW * N_CHAN * 2];
        for (int t = 0, i = 0; i < nop; i++)
        {
            int packed = (int)f.Int(2);
            if (!Unpack(f, packed, pattern)) throw new InvalidDataException("DTM 패턴이 깨졌습니다.");
            for (int j = 0; j < N_CHAN; j++, t++)
                for (int k = 0; k < N_ROW; k++)
                {
                    int b0 = pattern[(k * N_CHAN + j) * 2], b1 = pattern[(k * N_CHAN + j) * 2 + 1];
                    ref var tr = ref tracks[t][k];
                    if (b0 == 0x80) { if (b1 < numinst) tr.inst = (byte)(b1 + 1); continue; }
                    tr.note = (byte)b0;
                    if (b0 != 0 && b0 != 127) tr.note++;
                    int p = b1 & 0x0F;
                    switch (b1 >> 4)
                    {
                        case 0x0: if (p == 1) tr.command = 13; break;
                        case 0x1: tr.command = 28; tr.param1 = (byte)p; break;
                        case 0x2: tr.command = 28; tr.param2 = (byte)p; break;
                        case 0xA:
                        case 0xC: tr.command = 22; tr.param1 = (byte)((0x3F - p) >> 4); tr.param2 = (byte)((0x3F - p) & 0xF); break;
                        case 0xB: tr.command = 21; tr.param1 = (byte)((0x3F - p) >> 4); tr.param2 = (byte)((0x3F - p) & 0xF); break;
                        case 0xF: tr.command = 13; tr.param2 = (byte)p; break;
                    }
                }
        }
        if (f.Error) throw new InvalidDataException("DTM 파일이 잘렸습니다.");
        length = N_ORD;
        for (int i = 0; i < N_ORD; i++)
        {
            if ((order[i] & 0x80) != 0)
            {
                length = i;
                restartpos = order[i] == 0xFF ? 0 : order[i] - 0x80;
                if (restartpos >= i) throw new InvalidDataException("DTM 되돌아갈 자리가 이상합니다.");
                break;
            }
            if (order[i] >= nop) throw new InvalidDataException("DTM 순서표가 패턴 밖을 가리킵니다.");
        }
        initspeed = 2;
    }

    private static bool Unpack(AdPlugReader f, int ilen, byte[] o)
    {
        int op = 0, olen = o.Length;
        while (ilen-- > 0)
        {
            int rep = 1, b = f.Byte();
            if ((b & 0xF0) == 0xD0)
            {
                if (ilen-- == 0) return false;
                rep = b & 0x0F;
                b = f.Byte();
            }
            rep = Math.Min(rep, olen);
            for (int i = 0; i < rep; i++) o[op++] = (byte)b;
            olen -= rep;
        }
        return olen == 0 && !f.Error;
    }

    public override void Rewind(int subsong)
    {
        base.Rewind(subsong);
        for (int i = 0; i < 9; i++)
        {
            channel[i].inst = (byte)i;
            channel[i].vol1 = (byte)(63 - (inst[i].data[10] & 63));
            channel[i].vol2 = (byte)(63 - (inst[i].data[9] & 63));
        }
    }
}

/// <summary>MAD (Mlat Adlib Tracker). AdPlug mad.cpp (LGPL 2.1).</summary>
public sealed class MadPlayer : AdPlugModPlayer
{
    private byte timer;
    public override string TypeName { get { return "Mlat Adlib Tracker"; } }
    public override float Refresh { get { return timer; } }

    private protected override void Parse(AdPlugReader f)
    {
        byte[] convInst = { 2, 1, 10, 9, 4, 3, 6, 5, 8, 7 };
        if (Encoding.ASCII.GetString(f.Bytes(4)) != "MAD+") throw new InvalidDataException("MAD 파일이 아닙니다 (MAD+가 없음).");
        var idata = new byte[9][];
        for (int i = 0; i < 9; i++) { f.Ignore(8); idata[i] = f.Bytes(12); }
        f.Ignore(1);
        length = f.Byte(); nop = (ushort)f.Byte(); timer = (byte)f.Byte();
        ReallocInstruments(9);
        ReallocOrder(length);
        ReallocPatterns(nop, 32, 9);
        InitTrackord();
        for (int i = 0; i < nop; i++)
            for (int k = 0; k < 32; k++)
                for (int j = 0; j < 9; j++)
                {
                    int t = i * 9 + j, ev = f.Byte();
                    if (ev < 0x61) tracks[t][k].note = (byte)ev;
                    if (ev == 0xFF) tracks[t][k].command = 8;
                    if (ev == 0xFE) tracks[t][k].command = 13;
                }
        for (int i = 0; i < length; i++) order[i] = (byte)(f.Byte() - 1);
        for (int i = 0; i < 9; i++) for (int j = 0; j < 10; j++) inst[i].data[convInst[j]] = idata[i][j];
        restartpos = 0;
        initspeed = 1;
    }

    public override void Rewind(int subsong)
    {
        base.Rewind(subsong);
        for (int i = 0; i < 9; i++)
        {
            channel[i].inst = (byte)i;
            channel[i].vol1 = (byte)(63 - (inst[i].data[10] & 63));
            channel[i].vol2 = (byte)(63 - (inst[i].data[9] & 63));
        }
    }
}
