using System;
using System.IO;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// AdPlug ChscPlayer (hsc.cpp, HSC-Tracker, Simon Peter) — LGPL 2.1을 옮긴 것 (이 파일도 LGPL 2.1). 18.2Hz 고정.
/// 이것을 물려받는 형식: HSP (HSC Packed, RLE) · MTK (MPU-401 Trakker, LZ 압축). (.hsc 자체는 예전부터 <see cref="AdlibStreamFormats.LoadHsc"/>.)
/// </summary>
public abstract class AdPlugHscPlayer : OplPlayer
{
    private static readonly int[] NoteTable = { 363, 385, 408, 432, 458, 485, 514, 544, 577, 611, 647, 686 };
    private static readonly int[] OpTable = { 0x00, 0x01, 0x02, 0x08, 0x09, 0x0a, 0x10, 0x11, 0x12 };

    protected readonly byte[,] instr = new byte[128, 12];
    protected readonly byte[] song = new byte[0x80];
    /// <summary>패턴 50개 × (64줄 × 9채널) × (음, 효과).</summary>
    protected readonly byte[] patterns = new byte[50 * 64 * 9 * 2];
    protected int mtkmode;

    private readonly byte[] chInst = new byte[9];
    private readonly sbyte[] chSlide = new sbyte[9];
    private readonly ushort[] chFreq = new ushort[9];
    /// <summary>instr[n][k] (C 메모리 배치 그대로: 악기 128개 뒤에 곡 순서표, 그 뒤 패턴 — 번호가 128 이상이어도 같은 곳을 읽는다).</summary>
    private byte I(int n, int k)
    {
        int idx = n * 12 + k;
        if (idx < 128 * 12) return instr[idx / 12, idx % 12];
        idx -= 128 * 12;
        if (idx < song.Length) return song[idx];
        idx -= song.Length;
        return idx < patterns.Length ? patterns[idx] : (byte)0;
    }

    private byte pattpos, songpos, pattbreak, songend, mode6, bd, fadein;
    private uint speed, del;
    private readonly byte[] adlFreq = new byte[9];

    public override float Refresh { get { return 18.2f; } }

    public override void Load(byte[] data)
    {
        Parse(data);
        Rewind(0);
    }

    protected abstract void Parse(byte[] data);

    /// <summary>악기 고침 (HSC 공통): 2 · 3바이트 bit6 → bit7 뒤집기, 11 = 슬라이드 (위 4비트).</summary>
    protected void FixInstruments()
    {
        for (int i = 0; i < 128; i++)
        {
            instr[i, 2] ^= (byte)((instr[i, 2] & 0x40) << 1);
            instr[i, 3] ^= (byte)((instr[i, 3] & 0x40) << 1);
            instr[i, 11] >>= 4;
        }
    }

    public override void Rewind(int subsong)
    {
        pattpos = 0; songpos = 0; pattbreak = 0; speed = 2;
        del = 1; songend = 0; mode6 = 0; bd = 0; fadein = 0;
        Opl.Init();
        Opl.Write(1, 32); Opl.Write(8, 128); Opl.Write(0xbd, 0);
        for (int i = 0; i < 9; i++) SetInstr(i, i);
    }

    public override bool Update()
    {
        del--;
        if (del != 0) return songend == 0;
        if (fadein != 0) fadein--;
        byte pattnr = song[songpos];
        if (pattnr >= 0xb2)
        {
            songend = 1; songpos = 0; pattnr = song[songpos];
        }
        else if ((pattnr & 128) != 0 && pattnr <= 0xb1)
        {
            songpos = (byte)(song[songpos] & 127);
            pattpos = 0;
            pattnr = song[songpos];
            songend = 1;
        }

        if (pattnr < 50)
        {
            int pattoff = pattpos * 9;
            for (int chan = 0; chan < 9; chan++)
            {
                int at = (pattnr * 64 * 9 + pattoff) * 2;
                byte note = patterns[at], effect = patterns[at + 1];
                pattoff++;
                if ((note & 128) != 0) { SetInstr(chan, effect); continue; }
                byte effOp = (byte)(effect & 0x0f);
                byte inst = chInst[chan];
                if (note != 0) chSlide[chan] = 0;
                switch (effect & 0xf0)
                {
                    case 0:
                        switch (effOp)
                        {
                            case 1: pattbreak++; break;
                            case 3: fadein = 31; break;
                            case 5: mode6 = 1; break;
                            case 6: mode6 = 0; break;
                        }
                        break;
                    case 0x20:
                    case 0x10:
                        if ((effect & 0x10) != 0) { chFreq[chan] = (ushort)(chFreq[chan] + effOp); chSlide[chan] = (sbyte)(chSlide[chan] + effOp); }
                        else { chFreq[chan] = (ushort)(chFreq[chan] - effOp); chSlide[chan] = (sbyte)(chSlide[chan] - effOp); }
                        if (note == 0) SetFreq(chan, chFreq[chan]);
                        break;
                    case 0x50: break;
                    case 0x60: Opl.Write(0xc0 + chan, (I(chInst[chan], 8) & 1) + (effOp << 1)); break;
                    case 0xa0:
                    {
                        byte vol = (byte)(effOp << 2);
                        Opl.Write(0x43 + OpTable[chan], vol | (I(chInst[chan], 2) & ~63));
                        break;
                    }
                    case 0xb0:
                    {
                        byte vol = (byte)(effOp << 2);
                        if ((I(inst, 8) & 1) != 0) Opl.Write(0x40 + OpTable[chan], vol | (I(chInst[chan], 3) & ~63));
                        else Opl.Write(0x40 + OpTable[chan], vol | (I(inst, 3) & ~63));
                        break;
                    }
                    case 0xc0:
                    {
                        byte db = (byte)(effOp << 2);
                        Opl.Write(0x43 + OpTable[chan], db | (I(chInst[chan], 2) & ~63));
                        if ((I(inst, 8) & 1) != 0) Opl.Write(0x40 + OpTable[chan], db | (I(chInst[chan], 3) & ~63));
                        break;
                    }
                    case 0xd0: pattbreak++; songpos = effOp; songend = 1; break;
                    case 0xf0: speed = effOp; del = ++speed; break;
                }
                if (fadein != 0) SetVolume(chan, fadein * 2, fadein * 2);
                if (note == 0) continue;
                note--;
                if (note == 0x7f - 1 || ((note / 12) & ~7) != 0)
                {
                    adlFreq[chan] = (byte)(adlFreq[chan] & ~32);
                    Opl.Write(0xb0 + chan, adlFreq[chan]);
                    continue;
                }
                if (mtkmode != 0) note--;
                byte okt = (byte)(((note / 12) & 7) << 2);
                ushort fnr = (ushort)(NoteTable[note % 12] + I(inst, 11) + chSlide[chan]);
                chFreq[chan] = fnr;
                adlFreq[chan] = mode6 == 0 || chan < 6 ? (byte)(okt | 32) : okt;
                Opl.Write(0xb0 + chan, 0);
                SetFreq(chan, fnr);
                if (mode6 != 0)
                {
                    switch (chan)
                    {
                        case 6: Opl.Write(0xbd, bd & ~16); bd |= 48; break;
                        case 7: Opl.Write(0xbd, bd & ~1); bd |= 33; break;
                        case 8: Opl.Write(0xbd, bd & ~2); bd |= 34; break;
                    }
                    Opl.Write(0xbd, bd);
                }
            }
        }

        del = speed;
        if (pattbreak != 0)
        {
            pattpos = 0; pattbreak = 0;
            songpos = (byte)((songpos + 1) % 50);
            if (songpos == 0) songend = 1;
        }
        else
        {
            pattpos = (byte)((pattpos + 1) & 63);
            if (pattpos == 0)
            {
                songpos = (byte)((songpos + 1) % 50);
                if (songpos == 0) songend = 1;
            }
        }
        return songend == 0;
    }

    private void SetFreq(int chan, ushort freq)
    {
        adlFreq[chan] = (byte)((adlFreq[chan] & ~3) | (freq >> 8));
        Opl.Write(0xa0 + chan, freq & 0xff);
        Opl.Write(0xb0 + chan, adlFreq[chan]);
    }

    private void SetVolume(int chan, int volc, int volm)
    {
        int ins = chInst[chan];
        int op = OpTable[chan];
        Opl.Write(0x43 + op, volc | (I(ins, 2) & ~63));
        if ((I(ins, 8) & 1) != 0) Opl.Write(0x40 + op, volm | (I(ins, 3) & ~63));
        else Opl.Write(0x40 + op, I(ins, 3));
    }

    private void SetInstr(int chan, int insnr)
    {
        insnr &= 0xFF;
        int op = OpTable[chan];
        chInst[chan] = (byte)insnr;
        Opl.Write(0xb0 + chan, 0);
        Opl.Write(0xc0 + chan, I(insnr, 8));
        Opl.Write(0x23 + op, I(insnr, 0));
        Opl.Write(0x20 + op, I(insnr, 1));
        Opl.Write(0x63 + op, I(insnr, 4));
        Opl.Write(0x60 + op, I(insnr, 5));
        Opl.Write(0x83 + op, I(insnr, 6));
        Opl.Write(0x80 + op, I(insnr, 7));
        Opl.Write(0xe3 + op, I(insnr, 9));
        Opl.Write(0xe0 + op, I(insnr, 10));
        SetVolume(chan, I(insnr, 2) & 63, I(insnr, 3) & 63);
    }
}

/// <summary>HSP (HSC Packed: 원래 크기 2바이트 + (횟수, 값) RLE). AdPlug hsp.cpp (LGPL 2.1).</summary>
public sealed class HspPlayer : AdPlugHscPlayer
{
    public override string TypeName { get { return "HSC Packed"; } }

    protected override void Parse(byte[] data)
    {
        if (data.Length < 2) throw new InvalidDataException("HSP 파일이 너무 짧습니다.");
        long orgsize = data[0] | (data[1] << 8);
        // AdPlug: 크기 2바이트 뒤부터 파일 크기만큼 읽는다 → 끝 2바이트는 파일 끝 너머 (FFh)
        var cmp = new byte[data.Length];
        for (int i = 0; i < cmp.Length; i++) cmp[i] = i + 2 < data.Length ? data[i + 2] : (byte)0xFF;
        if (orgsize > 59187) throw new InvalidDataException("HSP 원래 크기가 너무 큽니다.");
        var org = new byte[orgsize];
        long j = 0;
        for (long i = 0; i < cmp.Length; j += B(cmp, i), i += 2)
        {
            if (j >= orgsize) break;
            long n = j + B(cmp, i) < orgsize ? B(cmp, i) : orgsize - j - 1;
            for (long k = 0; k < n; k++) org[j + k] = B(cmp, i + 1);
        }
        if (j < orgsize) orgsize = j;
        if (orgsize < 128 * 12 + 51) throw new InvalidDataException("HSP 풀린 크기가 모자랍니다.");
        for (int i = 0; i < 128 * 12; i++) instr[i / 12, i % 12] = org[i];
        FixInstruments();
        Array.Copy(org, 128 * 12, song, 0, 51);
        Array.Copy(org, 128 * 12 + 51, patterns, 0, Math.Min(patterns.Length, orgsize - 128 * 12 - 51));
    }

    /// <summary>C의 cmp[i]는 파일 크기만큼 할당 · 그 밖은 정의되지 않음 → 0.</summary>
    private static byte B(byte[] b, long i) { return i >= 0 && i < b.Length ? b[i] : (byte)0; }
}

/// <summary>MTK (MPU-401 Trakker): "mpu401tr\x92kk\xeer@data" + CRC · 크기 + LZ 압축 → HSC 자료 (곡 · 작곡 · 악기 이름 포함). AdPlug mtk.cpp (LGPL 2.1).</summary>
public sealed class MtkPlayer : AdPlugHscPlayer
{
    private string title = "", composer = "";
    public MtkPlayer() { mtkmode = 1; }
    public override string TypeName { get { return "MPU-401 Trakker"; } }
    public override string Title { get { return title; } }
    public override string Author { get { return composer; } }

    protected override void Parse(byte[] data)
    {
        var f = new AdPlugReader(data);
        string id = Encoding.Latin1.GetString(f.Bytes(18));
        f.Int(2);
        int size = (int)f.Int(2);
        const int dataSize = 34 * 2 + 34 * 0x80 + 0x80 * 12 + 0x80 + 1;   // mtkdata
        if (id != "mpu401tr\x92kk\xeer@data" || size < dataSize) throw new InvalidDataException("MTK 파일이 아닙니다.");
        var org = new byte[size];
        int ctrlbits = 0, ctrlmask = 0, cnt;
        for (int orgptr = 0; orgptr < size; orgptr += cnt)
        {
            if (f.Error) throw new InvalidDataException("MTK 자료가 잘렸습니다.");
            ctrlmask >>= 1;
            if (ctrlmask == 0) { ctrlbits = (int)f.Int(2); ctrlmask = 0x8000; }
            if ((ctrlbits & ctrlmask) == 0) { org[orgptr] = (byte)f.Byte(); cnt = 1; continue; }
            int cmd = f.Byte();
            cnt = (cmd & 0x0f) + 3;
            int offs;
            switch (cmd >> 4)
            {
                case 0:
                case 1:
                {
                    if (cmd >> 4 == 1) cnt += (f.Byte() << 4) + 16;
                    if (orgptr + cnt > size) throw new InvalidDataException("MTK 압축이 깨졌습니다.");
                    byte v = (byte)f.Byte();
                    for (int i = 0; i < cnt; i++) org[orgptr + i] = v;
                    break;
                }
                case 2:
                    offs = cnt + (f.Byte() << 4);
                    cnt = f.Byte() + 16;
                    Copy(org, orgptr, offs, cnt, size);
                    break;
                default:
                    offs = cnt + (f.Byte() << 4);
                    cnt = cmd >> 4;
                    Copy(org, orgptr, offs, cnt, size);
                    break;
            }
        }
        if (f.Error || !f.AtEof) throw new InvalidDataException("MTK 자료 길이가 맞지 않습니다.");
        title = Str(org, 1, 33); composer = Str(org, 35, 33);
        int insAt = 34 * 2 + 34 * 0x80;
        for (int i = 0; i < 128 * 12; i++) instr[i / 12, i % 12] = org[insAt + i];
        FixInstruments();
        Array.Copy(org, insAt + 128 * 12, song, 0, 0x80);
        Array.Copy(org, dataSize, patterns, 0, Math.Min(patterns.Length, size - dataSize));
    }

    private static void Copy(byte[] org, int orgptr, int offs, int cnt, int size)
    {
        if (orgptr + cnt > size || offs > orgptr) throw new InvalidDataException("MTK 압축이 깨졌습니다.");
        for (int i = 0; i < cnt; i++) org[orgptr + i] = org[orgptr - offs + i];
    }

    private static string Str(byte[] b, int at, int n)
    {
        int len = 0;
        while (len < n && at + len < b.Length && b[at + len] != 0) len++;
        return KoreanText.ShiftJis.GetString(b, at, len).Trim();
    }
}
