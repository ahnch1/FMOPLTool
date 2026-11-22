using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AdlibComposer.Formats;

// AdPlug의 작은 재생기들 (LGPL 2.1, 각 원본 파일 이름은 클래스 설명에) — 옮긴 이 파일도 LGPL 2.1.

/// <summary>SNG (SNGPlay, "ObsM": 레지스터 · 값 쌍 흐름, 70Hz). AdPlug sng.cpp.</summary>
public sealed class SngPlayer : OplPlayer
{
    private ushort length, start, loop;
    private byte delay;
    private bool compressed;
    private byte[] val = new byte[0], reg = new byte[0];
    private byte del;
    private ushort pos;
    private bool songend;
    public override string TypeName { get { return "SNG File Format"; } }
    public override float Refresh { get { return 70.0f; } }

    public override void Load(byte[] data)
    {
        var f = new AdPlugReader(data);
        string id = Encoding.ASCII.GetString(f.Bytes(4));
        length = (ushort)f.Int(2); start = (ushort)f.Int(2); loop = (ushort)f.Int(2); delay = (byte)f.Byte();
        compressed = f.Byte() != 0;
        if (id != "ObsM") throw new InvalidDataException("SNG (SNGPlay) 파일이 아닙니다 (ObsM이 없음).");
        length /= 2; start /= 2; loop /= 2;
        if (length == 0) throw new InvalidDataException("SNG 자료가 없습니다.");
        val = new byte[length]; reg = new byte[length];
        for (int i = 0; i < length; i++) { val[i] = (byte)f.Byte(); reg[i] = (byte)f.Byte(); }
        Rewind(0);
    }

    private byte R(int i) { return i < reg.Length ? reg[i] : (byte)0; }
    private byte V(int i) { return i < val.Length ? val[i] : (byte)0; }

    public override bool Update()
    {
        if (compressed && del != 0) { del--; return !songend; }
        int guard = 0;
        while (R(pos) != 0 && ++guard < 1000000)
        {
            Opl.Write(R(pos), V(pos));
            pos++;
            if (pos >= length) { songend = true; pos = loop; }
        }
        if (!compressed) Opl.Write(R(pos), V(pos));
        if (V(pos) != 0) del = (byte)(V(pos) - 1);
        pos++;
        if (pos >= length) { songend = true; pos = loop; }
        return !songend;
    }

    public override void Rewind(int subsong)
    {
        pos = start; del = delay; songend = false;
        Opl.Init(); Opl.Write(1, 32);
    }
}

/// <summary>GOT (God of Thunder Music: (시간, 레지스터, 값) 3바이트씩, 120Hz · 메뉴 곡만 140Hz). AdPlug got.cpp.</summary>
public sealed class GotPlayer : OplPlayer
{
    private byte[] time = new byte[0], reg = new byte[0], val = new byte[0];
    private int size, pos;
    private ushort del;
    private float rate = 120, timer;
    private bool songend;
    public override string TypeName { get { return "God of Thunder Music"; } }
    public override float Refresh { get { return timer; } }

    /// <summary>AdPlug 데이터베이스 열쇠 (CRC16 · CRC32, 파일 끝 다음 FFh 한 바이트까지 — binio eof 방식 그대로).</summary>
    public static (int crc16, uint crc32) Key(byte[] d)
    {
        int crc16 = 0;
        uint crc32 = 0xffffffff;
        for (int i = 0; i <= d.Length; i++)
        {
            int b = i < d.Length ? d[i] : 0xFF;
            for (int j = 0; j < 8; j++)
            {
                crc16 = ((crc16 ^ b) & 1) != 0 ? (crc16 >> 1) ^ 0xa001 : crc16 >> 1;
                crc32 = ((crc32 ^ (uint)b) & 1) != 0 ? (crc32 >> 1) ^ 0xedb88320 : crc32 >> 1;
                b >>= 1;
            }
        }
        return (crc16 & 0xffff, ~crc32);
    }

    public override void Load(byte[] data)
    {
        if (data.Length % 3 != 0 || data.Length < 9 || (data[0] | (data[1] << 8)) != 1 || BitConverter.ToUInt32(data, data.Length - 4) != 0)
            throw new InvalidDataException("GOT (God of Thunder) 곡이 아닙니다.");
        var k = Key(data);
        rate = k.crc16 == 0xB627 && k.crc32 == 0x72036C41 ? 140.0f : 120.0f;
        size = data.Length / 3 - 1;
        time = new byte[size]; reg = new byte[size]; val = new byte[size];
        for (int i = 0; i < size; i++) { time[i] = data[2 + i * 3]; reg[i] = data[3 + i * 3]; val[i] = data[4 + i * 3]; }
        Rewind(0);
    }

    public override bool Update()
    {
        do
        {
            del = time[pos];
            Opl.Write(reg[pos], val[pos]);
            pos++;
        } while (del == 0 && pos < size);
        if (pos >= size) { pos = 0; songend = true; }
        else timer = rate / (float)del;
        return !songend;
    }

    public override void Rewind(int subsong)
    {
        pos = 0; del = 0; timer = rate; songend = false;
        Opl.Init(); Opl.Write(1, 32);
    }
}

/// <summary>MKJ (MK-Jamz). AdPlug mkj.cpp. 100Hz.</summary>
public sealed class MkjPlayer : OplPlayer
{
    private static readonly int[] OpTable = { 0x00, 0x01, 0x02, 0x08, 0x09, 0x0a, 0x10, 0x11, 0x12 };
    private short maxchannel, maxnotes;
    private short[] songbuf = new short[0];
    private readonly short[,] inst = new short[9, 8];
    private sealed class Ch { public short defined, songptr, octave, waveform, pstat, speed; }
    private readonly Ch[] channel = Enumerable.Range(0, 9).Select(_ => new Ch()).ToArray();
    private bool songend;
    public override string TypeName { get { return "MK-Jamz"; } }
    public override float Refresh { get { return 100.0f; } }

    public override void Load(byte[] data)
    {
        var f = new AdPlugReader(data);
        if (Encoding.ASCII.GetString(f.Bytes(6)) != "MKJamz") throw new InvalidDataException("MKJ 파일이 아닙니다 (MKJamz가 없음).");
        float ver = f.Float();
        if (ver > 1.12) throw new InvalidDataException("MKJ 판이 너무 새것입니다: " + ver);
        maxchannel = (short)f.Int(2);
        if (maxchannel > 9 || maxchannel < 0) throw new InvalidDataException("MKJ 채널 수가 이상합니다.");
        for (int i = 0; i < maxchannel; i++) for (int j = 0; j < 8; j++) inst[i, j] = (short)f.Int(2);
        maxnotes = (short)f.Int(2);
        if (maxnotes < 1 || maxnotes > 0x7fff / (maxchannel + 1) || maxnotes - 1 + 3 * maxchannel > (maxchannel + 1) * maxnotes)
            throw new InvalidDataException("MKJ 음 수가 이상합니다.");
        songbuf = new short[(maxchannel + 1) * maxnotes];
        for (int i = 0; i < maxchannel; i++) channel[i].defined = (short)f.Int(2);
        for (int i = 0; i < songbuf.Length; i++) songbuf[i] = (short)f.Int(2);
        if (f.Error) throw new InvalidDataException("MKJ 파일이 잘렸습니다.");
        Rewind(0);
    }

    private short S(int i) { return i >= 0 && i < songbuf.Length ? songbuf[i] : (short)251; }

    public override bool Update()
    {
        for (int c = 0; c < maxchannel; c++)
        {
            var ch = channel[c];
            if (ch.defined == 0) continue;
            if (ch.pstat != 0) { ch.pstat--; continue; }
            Opl.Write(0xb0 + c, 0);
            int guard = 0;
            do
            {
                if (++guard > 100000) { songend = true; return false; }
                short note = S(ch.songptr);
                if (ch.songptr - c > maxchannel && note != 0 && note < 250) ch.pstat = ch.speed;
                bool bad = false;
                switch (note)
                {
                    case 68: Opl.Write(0xa0 + c, 0x81); Opl.Write(0xb0 + c, 0x21 + 4 * ch.octave); break;
                    case 69: Opl.Write(0xa0 + c, 0xb0); Opl.Write(0xb0 + c, 0x21 + 4 * ch.octave); break;
                    case 70: Opl.Write(0xa0 + c, 0xca); Opl.Write(0xb0 + c, 0x21 + 4 * ch.octave); break;
                    case 71: Opl.Write(0xa0 + c, 0x2); Opl.Write(0xb0 + c, 0x22 + 4 * ch.octave); break;
                    case 65: Opl.Write(0xa0 + c, 0x41); Opl.Write(0xb0 + c, 0x22 + 4 * ch.octave); break;
                    case 66: Opl.Write(0xa0 + c, 0x87); Opl.Write(0xb0 + c, 0x22 + 4 * ch.octave); break;
                    case 67: Opl.Write(0xa0 + c, 0xae); Opl.Write(0xb0 + c, 0x22 + 4 * ch.octave); break;
                    case 17: Opl.Write(0xa0 + c, 0x6b); Opl.Write(0xb0 + c, 0x21 + 4 * ch.octave); break;
                    case 18: Opl.Write(0xa0 + c, 0x98); Opl.Write(0xb0 + c, 0x21 + 4 * ch.octave); break;
                    case 20: Opl.Write(0xa0 + c, 0xe5); Opl.Write(0xb0 + c, 0x21 + 4 * ch.octave); break;
                    case 21: Opl.Write(0xa0 + c, 0x20); Opl.Write(0xb0 + c, 0x22 + 4 * ch.octave); break;
                    case 15: Opl.Write(0xa0 + c, 0x63); Opl.Write(0xb0 + c, 0x22 + 4 * ch.octave); break;
                    case 255:
                        ch.songptr = (short)(ch.songptr + maxchannel);
                        if (S(ch.songptr) < 0) { bad = true; break; }
                        ch.pstat = S(ch.songptr);
                        break;
                    case 254:
                        ch.songptr = (short)(ch.songptr + maxchannel);
                        if ((S(ch.songptr) | 7) != 7) { bad = true; break; }
                        ch.octave = S(ch.songptr);
                        break;
                    case 253:
                        ch.songptr = (short)(ch.songptr + maxchannel);
                        if (S(ch.songptr) < 0) { bad = true; break; }
                        ch.speed = S(ch.songptr);
                        break;
                    case 252:
                        ch.songptr = (short)(ch.songptr + maxchannel);
                        if (((S(ch.songptr) - 300) | 0xff) != 0xff) { bad = true; break; }
                        ch.waveform = (short)(S(ch.songptr) - 300);
                        if (c > 2) Opl.Write(0xe0 + c + (c + 6), ch.waveform);
                        else Opl.Write(0xe0 + c, ch.waveform);
                        break;
                    case 251: bad = true; break;
                }
                if (bad)
                {
                    for (int i = 0; i < maxchannel; i++) channel[i].songptr = (short)i;
                    songend = true;
                    return false;
                }
                if (ch.songptr - c < maxnotes) ch.songptr = (short)(ch.songptr + maxchannel);
                else ch.songptr = (short)c;
            } while (ch.pstat == 0);
        }
        return !songend;
    }

    public override void Rewind(int subsong)
    {
        Opl.Init(); Opl.Write(1, 32);
        for (int i = 0; i < maxchannel; i++)
        {
            var ch = channel[i];
            ch.pstat = 0; ch.speed = 0; ch.waveform = 0; ch.songptr = (short)i; ch.octave = 4;
            Opl.Write(0x20 + OpTable[i], inst[i, 4]);
            Opl.Write(0x23 + OpTable[i], inst[i, 0]);
            Opl.Write(0x40 + OpTable[i], inst[i, 5]);
            Opl.Write(0x43 + OpTable[i], inst[i, 1]);
            Opl.Write(0x60 + OpTable[i], inst[i, 6]);
            Opl.Write(0x63 + OpTable[i], inst[i, 2]);
            Opl.Write(0x80 + OpTable[i], inst[i, 7]);
            Opl.Write(0x83 + OpTable[i], inst[i, 3]);
        }
        songend = false;
    }
}

/// <summary>BAM (Bob's Adlib Music, "CBMF"). AdPlug bam.cpp. 25Hz.</summary>
public sealed class BamPlayer : OplPlayer
{
    private static readonly int[] OpTable = { 0x00, 0x01, 0x02, 0x08, 0x09, 0x0a, 0x10, 0x11, 0x12 };
    private static readonly int[] Freq = {172,182,193,205,217,230,243,258,274,
        290,307,326,345,365,387,410,435,460,489,517,547,580,614,651,1369,1389,1411,
        1434,1459,1484,1513,1541,1571,1604,1638,1675,2393,2413,2435,2458,2483,2508,
        2537,2565,2595,2628,2662,2699,3417,3437,3459,3482,3507,3532,3561,3589,3619,
        3652,3686,3723,4441,4461,4483,4506,4531,4556,4585,4613,4643,4676,4710,4747,
        5465,5485,5507,5530,5555,5580,5609,5637,5667,5700,5734,5771,6489,6509,6531,
        6554,6579,6604,6633,6661,6691,6724,6758,6795,7513,7533,7555,7578,7603,7628,
        7657,7685,7715,7748,7782,7819,7858,7898,7942,7988,8037,8089,8143,8191,8191,
        8191,8191,8191,8191,8191,8191,8191,8191,8191,8191};
    private byte[] song = new byte[0];
    private byte del;
    private long pos, size, gosub;
    private bool songend, chorus;
    private readonly long[] lTarget = new long[16];
    private readonly bool[] lDefined = new bool[16];
    private readonly byte[] lCount = new byte[16];
    public override string TypeName { get { return "Bob's Adlib Music"; } }
    public override float Refresh { get { return 25.0f; } }

    public override void Load(byte[] data)
    {
        if (data.Length < 4 || Encoding.ASCII.GetString(data, 0, 4) != "CBMF") throw new InvalidDataException("BAM 파일이 아닙니다 (CBMF가 없음).");
        size = data.Length - 4;
        song = new byte[size];
        Array.Copy(data, 4, song, 0, size);
        Rewind(0);
    }

    private byte S(long i) { return i >= 0 && i < song.Length ? song[i] : (byte)0; }
    private int F(int i) { return i < Freq.Length ? Freq[i] : 8191; }

    public override bool Update()
    {
        if (del != 0) { del--; return !songend; }
        if (pos >= size) { pos = 0; songend = true; }
        int guard = 0;
        while (S(pos) < 128)
        {
            if (++guard > 100000) { songend = true; return false; }
            int cmd = S(pos) & 240, c = S(pos) & 15;
            switch (cmd)
            {
                case 0: pos = 0; songend = true; break;
                case 16:
                    if (c < 9)
                    {
                        Opl.Write(0xa0 + c, F(S(++pos)) & 255);
                        Opl.Write(0xb0 + c, (F(S(pos)) >> 8) + 32);
                    }
                    else pos++;
                    pos++;
                    break;
                case 32: if (c < 9) Opl.Write(0xb0 + c, 0); pos++; break;
                case 48:
                    if (c < 9)
                    {
                        Opl.Write(0x20 + OpTable[c], S(pos + 1)); Opl.Write(0x23 + OpTable[c], S(pos + 2));
                        Opl.Write(0x40 + OpTable[c], S(pos + 3)); Opl.Write(0x43 + OpTable[c], S(pos + 4));
                        Opl.Write(0x60 + OpTable[c], S(pos + 5)); Opl.Write(0x63 + OpTable[c], S(pos + 6));
                        Opl.Write(0x80 + OpTable[c], S(pos + 7)); Opl.Write(0x83 + OpTable[c], S(pos + 8));
                        Opl.Write(0xe0 + OpTable[c], S(pos + 9)); Opl.Write(0xe3 + OpTable[c], S(pos + 10));
                        Opl.Write(0xc0 + c, S(pos + 11));
                    }
                    pos += 12;
                    break;
                case 80: lTarget[c] = ++pos; lDefined[c] = true; break;
                case 96:
                    if (lDefined[c])
                    {
                        int p = S(pos + 1);
                        if (p == 254) { pos = lTarget[c]; songend = true; break; }
                        if (p == 254 || p == 255)
                        {
                            if (!chorus) { chorus = true; gosub = pos + 2; pos = lTarget[c]; break; }
                            pos += 2;
                            break;
                        }
                        if (p == 0) { pos += 2; break; }
                        if (lCount[c] == 0) { lCount[c] = 255; pos += 2; break; }
                        if (lCount[c] < 255) lCount[c]--;
                        else lCount[c] = (byte)(p - 1);
                        pos = lTarget[c];
                    }
                    break;
                case 112:
                    if (chorus) { pos = gosub; chorus = false; } else pos++;
                    break;
                default: pos++; break;
            }
        }
        if (S(pos) >= 128) { del = (byte)(S(pos) - 127); pos++; }
        return !songend;
    }

    public override void Rewind(int subsong)
    {
        pos = 0; songend = false; del = 0; gosub = 0; chorus = false;
        Array.Clear(lTarget, 0, 16); Array.Clear(lDefined, 0, 16);
        lDefined[0] = true;
        for (int i = 0; i < 16; i++) lCount[i] = 255;
        Opl.Init(); Opl.Write(1, 32);
    }
}

/// <summary>JBM (JBM Adlib Music, Dennis Lindroos가 처음부터 쓴 재생기 — 원본 주석은 GPL이라 적혀 있음). AdPlug jbm.cpp.</summary>
public sealed class JbmPlayer : OplPlayer
{
    private static readonly int[] NoteTable = {
        0x0158, 0x016d, 0x0183, 0x019a, 0x01b2, 0x01cc, 0x01e7, 0x0204, 0x0223, 0x0244, 0x0266, 0x028b, 0x0558, 0x056d, 0x0583, 0x059a,
        0x05b2, 0x05cc, 0x05e7, 0x0604, 0x0623, 0x0644, 0x0666, 0x068b, 0x0958, 0x096d, 0x0983, 0x099a, 0x09b2, 0x09cc, 0x09e7, 0x0a04,
        0x0a23, 0x0a44, 0x0a66, 0x0a8b, 0x0d58, 0x0d6d, 0x0d83, 0x0d9a, 0x0db2, 0x0dcc, 0x0de7, 0x0e04, 0x0e23, 0x0e44, 0x0e66, 0x0e8b,
        0x1158, 0x116d, 0x1183, 0x119a, 0x11b2, 0x11cc, 0x11e7, 0x1204, 0x1223, 0x1244, 0x1266, 0x128b, 0x1558, 0x156d, 0x1583, 0x159a,
        0x15b2, 0x15cc, 0x15e7, 0x1604, 0x1623, 0x1644, 0x1666, 0x168b, 0x1958, 0x196d, 0x1983, 0x199a, 0x19b2, 0x19cc, 0x19e7, 0x1a04,
        0x1a23, 0x1a44, 0x1a66, 0x1a8b, 0x1d58, 0x1d6d, 0x1d83, 0x1d9a, 0x1db2, 0x1dcc, 0x1de7, 0x1e04, 0x1e23, 0x1e44, 0x1e66, 0x1e8b };
    private static readonly int[] PercmxTab = { 0x14, 0x12, 0x15, 0x11 };
    private static readonly int[] PerchnTab = { 6, 7, 8, 8, 7 };
    private static readonly int[] PercMaskOff = { 0xef, 0xf7, 0xfb, 0xfd, 0xfe };
    private static readonly int[] PercMaskOn = { 0x10, 0x08, 0x04, 0x02, 0x01 };
    private static readonly int[] OpTable = { 0x00, 0x01, 0x02, 0x08, 0x09, 0x0a, 0x10, 0x11, 0x12 };

    private byte[] m = new byte[0];
    private float timer;
    private ushort flags, voicemask, seqtable, seqcount, instable, inscount;
    private ushort[] sequences = new ushort[0];
    private byte bdreg;
    private sealed class Voice { public ushort trkpos, trkstart, seqpos; public byte seqno, note; public short vol, delay, instr; public readonly byte[] frq = new byte[2]; }
    private readonly Voice[] voice = Enumerable.Range(0, 11).Select(_ => new Voice()).ToArray();
    public override string TypeName { get { return "JBM Adlib Music"; } }
    public override float Refresh { get { return timer; } }

    private byte M(int i) { return i >= 0 && i < m.Length ? m[i] : (byte)0; }
    private ushort W(int i) { return (ushort)((M(i + 1) << 8) | M(i)); }

    public override void Load(byte[] data)
    {
        if (data.Length == 0) throw new InvalidDataException("JBM 파일이 비었습니다.");
        m = data;
        if (W(0) != 0x0002) throw new InvalidDataException("JBM 파일이 아닙니다 (첫 낱말 0002h가 아님).");
        int t = W(2);
        timer = (float)(1193810.0 / (t != 0 ? t : 0xffff));
        seqtable = W(4); instable = W(6); flags = W(8);
        inscount = (ushort)((data.Length - instable) >> 4);
        seqcount = 0xffff;
        for (int i = 0; i < 11; i++)
        {
            voice[i].trkpos = voice[i].trkstart = W(10 + (i << 1));
            if (voice[i].trkpos != 0 && voice[i].trkpos < seqcount) seqcount = voice[i].trkpos;
        }
        seqcount = (ushort)((seqcount - seqtable) >> 1);
        sequences = new ushort[seqcount];
        for (int i = 0; i < seqcount; i++) sequences[i] = W(seqtable + (i << 1));
        Rewind(0);
    }

    private ushort Seq(int n) { return n < sequences.Length ? sequences[n] : (ushort)0; }

    public override bool Update()
    {
        for (int c = 0; c < 11; c++)
        {
            var v = voice[c];
            if (v.trkpos == 0) continue;
            if (--v.delay != 0) continue;
            if ((v.note & 0x7f) != 0) NoteOnOff(c, false);
            short spos = (short)v.seqpos;
            int guard = 0;
            while (v.delay == 0)
            {
                if (++guard > 100000) return false;
                switch (M(spos))
                {
                    case 0xFD:
                        v.instr = M(spos + 1);
                        SetInstrument(c);
                        spos += 2;
                        break;
                    case 0xFF:
                        v.seqno = M(++v.trkpos);
                        if (v.seqno == 0xff)
                        {
                            v.trkpos = v.trkstart;
                            v.seqno = M(v.trkpos);
                            voicemask = (ushort)(voicemask & ~(1 << c));
                        }
                        spos = (short)(v.seqpos = Seq(v.seqno));
                        break;
                    default:
                        if ((M(spos) & 127) > 95) return false;
                        v.note = M(spos);
                        v.vol = M(spos + 1);
                        v.delay = (short)((M(spos + 2) + (M(spos + 3) << 8)) + 1);
                        int frq = NoteTable[v.note & 127];
                        v.frq[0] = (byte)frq;
                        v.frq[1] = (byte)(frq >> 8);
                        spos += 4;
                        break;
                }
            }
            v.seqpos = (ushort)spos;
            if ((flags & 1) != 0 && c > 6) Opl.Write(0x40 + PercmxTab[c - 7], v.vol ^ 0x3f);
            else if (c < 9) Opl.Write(0x43 + OpTable[c], v.vol ^ 0x3f);
            NoteOnOff(c, (v.note & 0x80) == 0);
        }
        return voicemask != 0;
    }

    public override void Rewind(int subsong)
    {
        voicemask = 0;
        for (int c = 0; c < 11; c++)
        {
            var v = voice[c];
            v.trkpos = v.trkstart;
            if (v.trkpos == 0) continue;
            voicemask |= (ushort)(1 << c);
            v.seqno = M(v.trkpos);
            v.seqpos = Seq(v.seqno);
            v.note = 0;
            v.delay = 1;
        }
        Opl.Init();
        Opl.Write(0x01, 32);
        bdreg = (byte)(0xC0 | (flags & 1) << 5);
        Opl.Write(0xbd, bdreg);
    }

    private void NoteOnOff(int ch, bool state)
    {
        var v = voice[ch];
        if ((flags & 1) != 0 && ch > 5)
        {
            Opl.Write(0xa0 + PerchnTab[ch - 6], v.frq[0]);
            Opl.Write(0xb0 + PerchnTab[ch - 6], v.frq[1]);
            Opl.Write(0xbd, state ? bdreg | PercMaskOn[ch - 6] : bdreg & PercMaskOff[ch - 6]);
        }
        else
        {
            Opl.Write(0xa0 + ch, v.frq[0]);
            Opl.Write(0xb0 + ch, state ? v.frq[1] | 0x20 : v.frq[1] & 0x1f);
        }
    }

    private void SetInstrument(int ch)
    {
        var v = voice[ch];
        short i = (short)(instable + (v.instr << 4));
        if (v.instr >= inscount) return;
        if (((flags & 1) & (ch > 6 ? 1 : 0)) != 0)
        {
            Opl.Write(0x20 + PercmxTab[ch - 7], M(i + 0));
            Opl.Write(0x40 + PercmxTab[ch - 7], M(i + 1) ^ 0x3f);
            Opl.Write(0x60 + PercmxTab[ch - 7], M(i + 2));
            Opl.Write(0x80 + PercmxTab[ch - 7], M(i + 3));
            Opl.Write(0xc0 + PerchnTab[ch - 6], M(i + 8) & 15);
            return;
        }
        if (ch >= 9) return;
        Opl.Write(0x20 + OpTable[ch], M(i + 0));
        Opl.Write(0x40 + OpTable[ch], M(i + 1) ^ 0x3f);
        Opl.Write(0x60 + OpTable[ch], M(i + 2));
        Opl.Write(0x80 + OpTable[ch], M(i + 3));
        Opl.Write(0x23 + OpTable[ch], M(i + 4));
        Opl.Write(0x43 + OpTable[ch], M(i + 5) ^ 0x3f);
        Opl.Write(0x63 + OpTable[ch], M(i + 6));
        Opl.Write(0x83 + OpTable[ch], M(i + 7));
        Opl.Write(0xe0 + OpTable[ch], (M(i + 8) >> 4) & 3);
        Opl.Write(0xe3 + OpTable[ch], (M(i + 8) >> 6) & 3);
        Opl.Write(0xc0 + ch, M(i + 8) & 15);
    }
}

/// <summary>KSM (Ken Silverman's Music, 240Hz). 악기는 같은 폴더의 insts.dat (<see cref="Insts"/>). AdPlug ksm.cpp.</summary>
public sealed class KsmPlayer : OplPlayer
{
    private static readonly int[] OpTable = { 0x00, 0x01, 0x02, 0x08, 0x09, 0x0a, 0x10, 0x11, 0x12 };
    private static readonly uint[] AdlibFreq = {
        0,
        2390,2411,2434,2456,2480,2506,2533,2562,2592,2625,2659,2695,
        3414,3435,3458,3480,3504,3530,3557,3586,3616,3649,3683,3719,
        4438,4459,4482,4504,4528,4554,4581,4610,4640,4673,4707,4743,
        5462,5483,5506,5528,5552,5578,5605,5634,5664,5697,5731,5767,
        6486,6507,6530,6552,6576,6602,6629,6658,6688,6721,6755,6791,
        7510 };

    /// <summary>insts.dat 내용 (256 × (이름 20 + 11 + 2)). 없으면 Load가 실패.</summary>
    public byte[] Insts;

    private ulong count, countstop;
    private readonly ulong[] chanage = new ulong[18];
    private ulong[] note = new ulong[1];
    private ushort numnotes;
    private uint nownote, numchans, drumstat;
    private readonly byte[] trinst = new byte[16], trquant = new byte[16], trchan = new byte[16], trvol = new byte[16], chanfreq = new byte[18], chantrack = new byte[18];
    private readonly byte[,] inst = new byte[256, 11];
    private bool songend;
    public override string TypeName { get { return "Ken Silverman's Music Format"; } }
    public override float Refresh { get { return 240.0f; } }

    public static byte[] FindInsts(string ksmPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(ksmPath)) ?? "";
        var f = Directory.Exists(dir) ? Directory.GetFiles(dir).FirstOrDefault(x => string.Equals(Path.GetFileName(x), "insts.dat", StringComparison.OrdinalIgnoreCase)) : null;
        return f != null ? File.ReadAllBytes(f) : null;
    }

    public override void Load(byte[] data)
    {
        if (Insts == null) throw new FileNotFoundException("KSM은 같은 폴더의 insts.dat (Ken Silverman 악기)가 필요합니다.");
        var fi = new AdPlugReader(Insts);
        for (int i = 0; i < 256; i++) { fi.Ignore(20); for (int j = 0; j < 11; j++) inst[i, j] = (byte)fi.Byte(); fi.Ignore(2); }
        var f = new AdPlugReader(data);
        for (int i = 0; i < 16; i++) trinst[i] = (byte)f.Byte();
        for (int i = 0; i < 16; i++) trquant[i] = (byte)f.Byte();
        for (int i = 0; i < 16; i++) trchan[i] = (byte)f.Byte();
        f.Ignore(16);
        for (int i = 0; i < 16; i++) trvol[i] = (byte)f.Byte();
        numnotes = (ushort)f.Int(2);
        note = new ulong[Math.Max(1, (int)numnotes)];
        for (int i = 0; i < numnotes; i++) note[i] = (ulong)f.Int(4);
        if (trchan[11] == 0) { drumstat = 0; numchans = 9; }
        else { drumstat = 32; numchans = 6; }
        Rewind(0);
    }

    private static int Quanter(byte q) { return 240 / (q != 0 ? q : 240); }

    public override bool Update()
    {
        count++;
        if (count >= countstop)
        {
            var buf = new List<(int reg, int val)>();
            int guard = 0;
            while (count >= countstop && ++guard < 100000)
            {
                ulong templong = note[nownote];
                int track = (int)((templong >> 8) & 15);
                if ((templong & 192) == 0)
                {
                    uint i = 0;
                    while (i < numchans && (chanfreq[i] != (templong & 63) || chantrack[i] != ((templong >> 8) & 15))) i++;
                    if (i < numchans)
                    {
                        buf.Add(((int)(0xb0 + i) & 0xFF, (int)((AdlibFreq[templong & 63] >> 8) & 223)));
                        chanfreq[i] = 0;
                        chanage[i] = 0;
                    }
                }
                else
                {
                    int volevel = trvol[track];
                    if ((templong & 192) == 128) { volevel -= 4; if (volevel < 0) volevel = 0; }
                    if ((templong & 192) == 192) { volevel += 4; if (volevel > 63) volevel = 63; }
                    if (track < 11)
                    {
                        ulong temp = 0;
                        uint i = numchans;
                        for (uint j = 0; j < numchans; j++)
                            if (countstop - chanage[j] >= temp && chantrack[j] == track) { temp = countstop - chanage[j]; i = j; }
                        if (i < numchans)
                        {
                            buf.Add(((int)(0xb0 + i) & 0xFF, 0));
                            int volval = (inst[trinst[track], 1] & 192) + (volevel ^ 63);
                            buf.Add(((0x40 + OpTable[i] + 3) & 0xFF, volval & 0xFF));
                            buf.Add(((int)(0xa0 + i) & 0xFF, (int)(AdlibFreq[templong & 63] & 255)));
                            buf.Add(((int)(0xb0 + i) & 0xFF, (int)((AdlibFreq[templong & 63] >> 8) | 32) & 0xFF));
                            chanfreq[i] = (byte)(templong & 63);
                            chanage[i] = countstop;
                        }
                    }
                    else if ((drumstat & 32) > 0)
                    {
                        int freq = (int)AdlibFreq[templong & 63], drumnum, chan;
                        switch (track)
                        {
                            case 11: drumnum = 16; chan = 6; freq -= 2048; break;
                            case 12: drumnum = 8; chan = 7; freq -= 2048; break;
                            case 13: drumnum = 4; chan = 8; break;
                            case 14: drumnum = 2; chan = 8; break;
                            case 15: drumnum = 1; chan = 7; freq -= 2048; break;
                            default: drumnum = 0; chan = 0; break;
                        }
                        buf.Add(((0xa0 + chan) & 0xFF, freq & 255));
                        buf.Add(((0xb0 + chan) & 0xFF, (freq >> 8) & 223));
                        buf.Add((0xbd, (int)(drumstat & (uint)(255 - drumnum)) & 0xFF));
                        drumstat |= (uint)drumnum;
                        if (track == 11 || track == 12 || track == 14)
                        {
                            int volval = (inst[trinst[track], 1] & 192) + (volevel ^ 63);
                            buf.Add(((0x40 + OpTable[chan] + 3) & 0xFF, volval & 0xFF));
                        }
                        else
                        {
                            int volval = (inst[trinst[track], 6] & 192) + (volevel ^ 63);
                            buf.Add(((0x40 + OpTable[chan]) & 0xFF, volval & 0xFF));
                        }
                        buf.Add((0xbd, (int)drumstat & 0xFF));
                    }
                }
                nownote++;
                if (nownote >= numnotes) { nownote = 0; songend = true; }
                templong = note[nownote];
                if (nownote == 0) count = (templong >> 12) - 1;
                int quanter = Quanter(trquant[(templong >> 8) & 15]);
                countstop = (((templong >> 12) + (ulong)(quanter >> 1)) / (ulong)quanter) * (ulong)quanter;
            }
            foreach (var (r, v) in buf) Opl.Write(r, v);
        }
        return !songend;
    }

    public override void Rewind(int subsong)
    {
        var ib = new byte[11];
        songend = false;
        Opl.Init(); Opl.Write(1, 32); Opl.Write(4, 0); Opl.Write(8, 0); Opl.Write(0xbd, (int)drumstat);
        if (trchan[11] == 1)
        {
            for (int i = 0; i < 11; i++) ib[i] = inst[trinst[11], i];
            ib[1] = (byte)((ib[1] & 192) | (trvol[11] ^ 63));
            SetInst(6, ib);
            for (int i = 0; i < 5; i++) ib[i] = inst[trinst[12], i];
            for (int i = 5; i < 11; i++) ib[i] = inst[trinst[15], i];
            ib[1] = (byte)((ib[1] & 192) | (trvol[12] ^ 63));
            ib[6] = (byte)((ib[6] & 192) | (trvol[15] ^ 63));
            SetInst(7, ib);
            for (int i = 0; i < 5; i++) ib[i] = inst[trinst[14], i];
            for (int i = 5; i < 11; i++) ib[i] = inst[trinst[13], i];
            ib[1] = (byte)((ib[1] & 192) | (trvol[14] ^ 63));
            ib[6] = (byte)((ib[6] & 192) | (trvol[13] ^ 63));
            SetInst(8, ib);
        }
        for (int i = 0; i < numchans; i++) { chantrack[i] = 0; chanage[i] = 0; }
        uint jj = 0;
        for (int i = 0; i < 16; i++)
            if (trchan[i] > 0 && jj < numchans)
            {
                int k = trchan[i];
                while (jj < numchans && k > 0) { chantrack[jj] = (byte)i; k--; jj++; }
            }
        for (int i = 0; i < numchans; i++)
        {
            for (int j = 0; j < 11; j++) ib[j] = inst[trinst[chantrack[i]], j];
            ib[1] = (byte)((ib[1] & 192) | (63 - trvol[chantrack[i]]));
            SetInst(i, ib);
            chanfreq[i] = 0;
        }
        ulong templong = note[0];
        count = (templong >> 12) - 1;
        countstop = (templong >> 12) - 1;
        nownote = 0;
    }

    private void SetInst(int chan, byte[] v)
    {
        Opl.Write(0xa0 + chan, 0);
        Opl.Write(0xb0 + chan, 0);
        Opl.Write(0xc0 + chan, v[10]);
        int offs = OpTable[chan];
        Opl.Write(0x20 + offs, v[5]); Opl.Write(0x40 + offs, v[6]); Opl.Write(0x60 + offs, v[7]); Opl.Write(0x80 + offs, v[8]); Opl.Write(0xe0 + offs, v[9]);
        offs += 3;
        Opl.Write(0x20 + offs, v[0]); Opl.Write(0x40 + offs, v[1]); Opl.Write(0x60 + offs, v[2]); Opl.Write(0x80 + offs, v[3]); Opl.Write(0xe0 + offs, v[4]);
    }
}

/// <summary>AdPlug에서 옮긴 작은 형식들 확장자 → 재생기 (ChipSongs가 쓰는 곳).</summary>
public static class AdPlugFormats
{
    private static readonly (string ext, Func<OplPlayer> make, string name)[] table =
    {
        (".hsp", () => new HspPlayer(), "HSP"),
        (".sng", () => new SngPlayer(), "SNG"),
        (".mad", () => new MadPlayer(), "MAD"),
        (".got", () => new GotPlayer(), "GOT"),
        (".mtk", () => new MtkPlayer(), "MTK"),
        (".mkj", () => new MkjPlayer(), "MKJ"),
        (".bam", () => new BamPlayer(), "BAM"),
        (".amd", () => new AmdPlayer(), "AMD"),
        (".sa2", () => new Sa2Player(), "SA2"),
        (".jbm", () => new JbmPlayer(), "JBM"),
        (".dtm", () => new DtmPlayer(), "DTM"),
        (".ksm", () => new KsmPlayer(), "KSM"),
    };

    public static readonly string[] Extensions = table.Select(t => t.ext).ToArray();

    public static OplPlayer Create(string path)
    {
        string ext = Path.GetExtension(path ?? "");
        foreach (var t in table)
            if (string.Equals(t.ext, ext, StringComparison.OrdinalIgnoreCase))
            {
                var p = t.make();
                if (p is KsmPlayer k) k.Insts = KsmPlayer.FindInsts(path);
                return p;
            }
        throw new InvalidDataException("AdPlug 형식이 아닙니다: " + ext);
    }

    public static VgmSong Load(string path)
    {
        var p = Create(path);
        string name = table.First(t => string.Equals(t.ext, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)).name;
        var v = AdPlugHost.Render(p, File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path), name, "PC (" + p.TypeName + ")");
        v.FilePath = path;
        v.SourceFormat = name + " (" + p.TypeName + ")" + v.SourceFormat.Substring(name.Length);
        return v;
    }
}
