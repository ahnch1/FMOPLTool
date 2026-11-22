using System;
using System.IO;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// D00 (EdLib packed, Jens-Christian Huus / Vibrants) 재생기, 판 0 ~ 4 (+ 옛 곡에 새 머리말을 씌운 것). AdPlug <c>d00.cpp</c> (LGPL 2.1, Simon Peter)를 옮긴 것 — 이 파일도 LGPL 2.1.
/// <para>새 머리말 (판 2~4): "JCH" 26h 02h 66h, 종류 · 판 · 속도 (Hz) · 부곡 수 · 사운드카드, 제목 · 작곡 (32바이트씩), 위치 tpoin · seqptr · instptr · infoptr · spfxptr.
/// 옛 머리말 (판 0 · 1): 판 · 속도 · 부곡 수 · 위치들 (판 0 = 70Hz). 채널마다 순서표 (FFFEh 끝 · FFFFh 점프 · 9000h~ 속도 · 8000h~ 전조) → 패턴 (음 · 쉼 · 이음 · 효과:
/// 6 끊기 · 7 비브라토 · 9 볼륨 · B SpFX · C 악기 · D/E 슬라이드), 레벨펄스 (판 1 · 2) · SpFX (판 4), 판 4 하드 리스타트.</para>
/// <para>9채널이 모두 끝나거나 점프를 만나면 곡 끝. update 주기 = 머리말 속도 (Hz). 부곡은 "파일::n".</para>
/// </summary>
public sealed class D00Player : OplPlayer
{
    public static readonly string[] Extensions = { ".d00" };

    private static readonly int[] NoteTable = { 340, 363, 385, 408, 432, 458, 485, 514, 544, 577, 611, 647 };
    private static readonly int[] OpTable = { 0x00, 0x01, 0x02, 0x08, 0x09, 0x0a, 0x10, 0x11, 0x12 };
    private const int InstSize = 16, SpfxSize = 8, LevpulsSize = 4;

    public static VgmSong Load(string path)
    {
        int sub = 0;
        int sep = path.LastIndexOf("::", StringComparison.Ordinal);
        if (sep > 1 && int.TryParse(path.Substring(sep + 2), out int n)) { sub = n; path = path.Substring(0, sep); }
        var p = new D00Player { startSub = sub };
        var v = AdPlugHost.Render(p, File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path), "D00", "PC (EdLib D00)");
        v.FilePath = path;
        v.SourceFormat = "D00 v" + p.version + " · " + p.Refresh.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "Hz" + (p.Subsongs > 1 ? " · 부곡 " + (sub + 1) + "/" + p.Subsongs : "") + v.SourceFormat.Substring(3);
        return v;
    }

    /// <summary>부곡 제목 목록 (부곡이 하나면 1개).</summary>
    public static System.Collections.Generic.List<string> Tracks(string path)
    {
        var l = new System.Collections.Generic.List<string>();
        try
        {
            var p = new D00Player();
            p.Parse(File.ReadAllBytes(path));
            string name = p.Title.Length > 0 ? p.Title : Path.GetFileNameWithoutExtension(path);
            int n = Math.Max(1, (int)p.Subsongs);
            for (int i = 0; i < n; i++) l.Add(n > 1 ? name + " #" + (i + 1) : name);
        }
        catch (Exception) { l.Add(Path.GetFileNameWithoutExtension(path)); }
        return l;
    }

    // ------------------------------------------------------------ 상태

    private sealed class Chan
    {
        public int order = -1;
        public ushort ordpos, pattpos, del, speed, rhcnt, key, freq, inst, spfx, ispfx, irhcnt;
        public short transpose, slide, slideval, vibspeed;
        public byte seqend, vol, vibdepth, fxdel, modvol, cvol, levpuls, frameskip, nextnote, note, ilevpuls, trigger, fxflag;
    }

    private readonly Chan[] channel = new Chan[9];
    private byte[] fd = new byte[1];
    private long filesize;
    private bool ver1;
    private byte songend, version, cursubsong;
    private int startSub;
    private int instAt, seqAt, infoAt, spfxAt = -1, levpulsAt = -1;
    private string title = "", author = "", info = "";

    public override string Title { get { return title; } }
    public override string Author { get { return author; } }
    public override string Description { get { return info; } }
    public override string TypeName { get { return "EdLib packed (version " + version + ")"; } }
    public override float Refresh { get { return ver1 ? B(1) : B(8); } }
    public int Subsongs { get { return ver1 ? B(2) : B(9); } }

    private byte B(long i) { return i >= 0 && i < fd.Length ? fd[i] : (byte)0; }
    private ushort W(long i) { return (ushort)(B(i) | (B(i + 1) << 8)); }
    private bool IndexOk(long ptr, long idx, int size) { return ptr >= 0 && ptr + (idx + 1) * size <= filesize; }

    // ------------------------------------------------------------ 불러오기

    public override void Load(byte[] data)
    {
        Parse(data);
        Rewind(startSub);
    }

    private void Parse(byte[] data)
    {
        int headerstart = 0;
        bool id = data.Length >= 11 && data[0] == 'J' && data[1] == 'C' && data[2] == 'H' && data[3] == 0x26 && data[4] == 0x02 && data[5] == 0x66;
        if (id && (data[7] & 0x80) != 0)
        {
            // 옛 곡에 새 머리말을 씌운 것: 옛 머리말은 0x6B부터
            if (data.Length < 0x6B + 15 || data[0x6B] > 1 || data[0x6B + 2] == 0) throw new InvalidDataException("D00 파일이 아닙니다 (옛 머리말이 이상함).");
            ver1 = true;
            headerstart = 0x6b;
        }
        else if (!id || data[6] != 0 || data[9] == 0 || data[10] != 0 || data[7] < 2 || data[7] > 4)
        {
            if (data.Length < 15 || data[0] > 1 || data[2] == 0) throw new InvalidDataException("D00 파일이 아닙니다.");
            ver1 = true;
        }
        else ver1 = false;

        // AdPlug와 같이: headerstart부터 읽되 크기는 파일 전체 (모자란 곳은 0), 끝에 0 하나
        filesize = data.Length;
        fd = new byte[filesize + 1];
        Array.Copy(data, headerstart, fd, 0, data.Length - headerstart);

        if (!ver1)
        {
            if (filesize < 119 || filesize < W(113) || filesize < W(111) || filesize < W(109)) throw new InvalidDataException("D00 머리말이 파일 밖을 가리킵니다.");
            version = B(7);
            infoAt = W(113); instAt = W(111); seqAt = W(109);
            title = Text(11); author = Text(43);
        }
        else
        {
            if (filesize < 15 || filesize <= W(9) || filesize <= W(7) || filesize <= W(5)) throw new InvalidDataException("D00 머리말이 파일 밖을 가리킵니다.");
            version = B(0);
            infoAt = W(9); instAt = W(7); seqAt = W(5);
        }
        switch (version)
        {
            case 0: levpulsAt = -1; spfxAt = -1; fd[1] = 70; break;   // 판 0 = 70Hz
            case 1: if (filesize <= W(11)) throw new InvalidDataException("D00 레벨펄스 위치가 파일 밖입니다."); levpulsAt = W(11); spfxAt = -1; break;
            case 2: if (filesize <= W(115)) throw new InvalidDataException("D00 레벨펄스 위치가 파일 밖입니다."); levpulsAt = W(115); spfxAt = -1; break;
            case 3: spfxAt = -1; levpulsAt = -1; break;
            case 4: if (filesize <= W(115)) throw new InvalidDataException("D00 SpFX 위치가 파일 밖입니다."); spfxAt = W(115); levpulsAt = -1; break;
        }
        // 설명 글 (FFh FFh 앞까지, 끝 공백 · FFh 지움)
        int end = infoAt;
        while (end < fd.Length && fd[end] != 0 && !(fd[end] == 0xFF && end + 1 < fd.Length && fd[end + 1] == 0xFF)) end++;
        info = end > infoAt ? KoreanText.ShiftJis.GetString(fd, infoAt, end - infoAt).Replace("\r", "").Trim() : "";
    }

    private string Text(int at)
    {
        int n = 0;
        while (n < 31 && fd[at + n] != 0) n++;
        return KoreanText.ShiftJis.GetString(fd, at, n).TrimEnd(' ');
    }

    public override void Rewind(int subsong)
    {
        if (subsong < 0) subsong = cursubsong;
        long tpoin = ver1 ? W(3) : W(107);
        long dataofs = subsong * 32L + tpoin;
        var tp = new byte[32];
        if (subsong < Subsongs && dataofs + 32 <= filesize) Array.Copy(fd, dataofs, tp, 0, 32);
        for (int i = 0; i < 9; i++)
        {
            var c = channel[i] = new Chan();
            int ptr = tp[i * 2] | (tp[i * 2 + 1] << 8);
            if (ptr != 0 && ptr + 4 <= filesize) { c.speed = W(ptr); c.order = ptr + 2; }
            else { c.speed = 0; c.order = -1; }
            c.ispfx = 0xffff; c.spfx = 0xffff;
            c.ilevpuls = 0xff; c.levpuls = 0xff;
            c.cvol = (byte)(tp[18 + i] & 0x7f);
            c.vol = c.cvol;
        }
        songend = 0;
        Opl.Init();
        Opl.Write(1, 32);
        cursubsong = (byte)(subsong > 0xff ? 0xff : subsong);
    }

    // ------------------------------------------------------------ 재생

    private long SpfxRec(int idx) { return spfxAt + (long)idx * SpfxSize; }
    private long LevRec(int idx) { return levpulsAt + (long)idx * LevpulsSize; }
    private long InstRec(int idx) { return instAt + (long)idx * InstSize; }

    public override bool Update()
    {
        byte trackend = 0;

        for (int c = 0; c < 9; c++)
        {
            var ch = channel[c];
            ch.slideval = (short)(ch.slideval + ch.slide);
            SetFreq(c);
            Vibrato(c);

            if (ch.spfx != 0xffff)
            {
                bool stop = false;
                if (ch.fxdel != 0) ch.fxdel--;
                else
                {
                    ch.spfx = W(SpfxRec(ch.spfx) + 6);
                    if (ch.spfx == 0xffff || !IndexOk(spfxAt, ch.spfx, SpfxSize)) { ch.spfx = 0xffff; stop = true; }
                    else
                    {
                        long r = SpfxRec(ch.spfx);
                        ch.fxdel = B(r + 5);
                        ch.inst = (ushort)(W(r) & 0xfff);
                        if (B(r + 3) != 0xff) ch.modvol = B(r + 3);
                        SetInst(c);
                        byte note = (W(r) & 0x8000) != 0 ? B(r + 2) : (byte)((sbyte)B(r + 2) + ch.note);
                        ch.freq = (ushort)(NoteTable[note % 12] + ((note / 12) << 10));
                        SetFreq(c);
                    }
                }
                if (!stop)
                {
                    ch.modvol = (byte)((ch.modvol + (sbyte)B(SpfxRec(ch.spfx) + 4)) & 63);
                    SetVolume(c);
                }
            }

            if (ch.levpuls != 0xff)
            {
                if (ch.frameskip != 0) ch.frameskip--;
                else if (IndexOk(instAt, ch.inst, InstSize))
                {
                    ch.frameskip = B(InstRec(ch.inst) + 12);
                    if (ch.fxdel != 0) ch.fxdel--;
                    else if (IndexOk(levpulsAt, ch.levpuls, LevpulsSize))
                    {
                        ch.levpuls = (byte)(B(LevRec(ch.levpuls) + 3) - 1);
                        ch.fxdel = B(LevRec(ch.levpuls) + 2);
                        if (B(LevRec(ch.levpuls)) != 0xff) ch.modvol = B(LevRec(ch.levpuls));
                    }
                    ch.modvol = (byte)((ch.modvol + (sbyte)B(LevRec(ch.levpuls) + 1)) & 63);
                    SetVolume(c);
                }
            }
        }

        for (int c = 0; c < 9; c++)
        {
            var ch = channel[c];
            if (version < 3 ? ch.del != 0 : ch.del <= 0x7f)
            {
                if (version == 4 && ch.del == B(InstRec(ch.inst) + 12) && ch.nextnote != 0)
                    Opl.Write(0x83 + OpTable[c], B(InstRec(ch.inst) + 13));
                if (version < 3) ch.del--;
                else if (ch.speed != 0) ch.del = (ushort)(ch.del + ch.speed);
                else { ch.seqend = 1; continue; }
                continue;
            }
            if (ch.speed != 0)
            {
                if (version < 3) ch.del = ch.speed;
                else { ch.del &= 0x7f; ch.del = (ushort)(ch.del + ch.speed); }
            }
            else { ch.seqend = 1; continue; }
            if (ch.rhcnt != 0) { ch.rhcnt--; continue; }

            int guard = 0;
            long patt = -1;
            bool done = false;
            // readorder
            while (!done)
            {
                if (++guard > 4096) { ch.seqend = 1; break; }
                if (!IndexOk(ch.order, ch.ordpos, 2)) { ch.seqend = 1; break; }
                ushort ord = W(ch.order + ch.ordpos * 2L);
                if (ord == 0xfffe) { ch.seqend = 1; break; }
                if (ord == 0xffff)
                {
                    ch.seqend = 1;
                    if (!IndexOk(ch.order, ch.ordpos + 1, 2)) break;
                    ch.ordpos = W(ch.order + (ch.ordpos + 1) * 2L);
                    continue;
                }
                if (ord >= 0x9000)
                {
                    ch.speed = (ushort)(ord & 0xff);
                    ord = ch.ordpos > 0 ? W(ch.order + (ch.ordpos - 1) * 2L) : (ushort)0;
                    ch.ordpos++;
                }
                else if (ord >= 0x8000)
                {
                    ch.transpose = (short)(ord & 0xff);
                    if ((ord & 0x100) != 0) ch.transpose = (short)-ch.transpose;
                    if (!IndexOk(ch.order, ch.ordpos + 1, 2)) { ch.seqend = 1; break; }
                    ord = W(ch.order + (++ch.ordpos) * 2L);
                }
                if (!IndexOk(seqAt, ord, 2) || W(seqAt + ord * 2L) + 2L > filesize) { ch.seqend = 1; break; }
                patt = W(seqAt + ord * 2L);

                ch.fxflag = 0;
                // readseq
                bool nextOrder = false;
                while (true)
                {
                    if (++guard > 4096) { ch.seqend = 1; done = true; break; }
                    if (version == 0) ch.rhcnt = ch.irhcnt;
                    ushort pattpos = IndexOk(patt, ch.pattpos, 2) ? W(patt + ch.pattpos * 2L) : (ushort)0xffff;
                    if (pattpos == 0xffff)
                    {
                        ch.pattpos = 0;
                        ch.ordpos++;
                        nextOrder = true;
                        break;
                    }
                    byte cnt = (byte)(pattpos >> 8);
                    byte note = (byte)(pattpos & 0xff);
                    int fx = pattpos >> 12;
                    ushort fxop = (ushort)(pattpos & 0x0fff);
                    ch.pattpos++;
                    pattpos = IndexOk(patt, ch.pattpos, 2) ? W(patt + ch.pattpos * 2L) : (ushort)0;
                    ch.nextnote = (byte)(pattpos & 0x7f);
                    if (version != 0 ? cnt < 0x40 : fx == 0)
                    {
                        NoteEvent(c, cnt, note);
                        done = true;
                        break;
                    }
                    // 효과 (뒤에 음이 온다)
                    switch (fx)
                    {
                        case 6:
                        {
                            ushort buf = ch.inst;
                            ch.inst = 0;
                            PlayNote(c);
                            ch.inst = buf;
                            ch.rhcnt = fxop;
                            done = true;
                            break;
                        }
                        case 7:
                            ch.vibspeed = (short)(fxop & 0xff);
                            ch.vibdepth = (byte)(fxop >> 8);
                            ch.trigger = (byte)(fxop >> 9);
                            ch.fxflag |= 1;
                            break;
                        case 8: if (version == 0) ch.irhcnt = fxop; break;
                        case 9:
                            ch.vol = (byte)(fxop & 63);
                            if (ch.vol + ch.cvol < 63) ch.vol = (byte)(ch.vol + ch.cvol);
                            else ch.vol = 63;
                            SetVolume(c);
                            break;
                        case 0xb: if (version == 4) ch.ispfx = fxop; break;
                        case 0xc:
                            ch.ispfx = 0xffff;
                            ch.spfx = 0xffff;
                            ch.inst = fxop;
                            if (!IndexOk(instAt, fxop, InstSize))
                            {
                                ch.modvol = 0;
                                ch.levpuls = ch.ilevpuls = 0xff;
                                break;
                            }
                            ch.modvol = (byte)(B(InstRec(fxop) + 7) & 63);
                            if (version < 3 && version != 0 && B(InstRec(fxop) + 11) != 0) ch.ilevpuls = (byte)(B(InstRec(fxop) + 11) - 1);
                            else { ch.ilevpuls = 0xff; ch.levpuls = 0xff; }
                            break;
                        case 0xd: ch.slide = (short)fxop; ch.fxflag |= 2; break;
                        case 0xe: ch.slide = (short)-fxop; ch.fxflag |= 2; break;
                    }
                    if (done) break;
                }
                if (!nextOrder) done = true;
            }
        }

        for (int c = 0; c < 9; c++) if (channel[c].seqend != 0) trackend++;
        if (trackend == 9) songend = 1;
        return songend == 0;
    }

    private void NoteEvent(int c, byte cnt, byte note)
    {
        var ch = channel[c];
        switch (note)
        {
            case 0:
            case 0x80:
                if (note == 0 || version != 0) { ch.key = 0; SetFreq(c); }
                if (version != 0) ch.rhcnt = cnt;
                ch.nextnote = 0;
                return;
            case 0x7e:
                if (version != 0) ch.rhcnt = cnt;
                ch.nextnote = 0;
                return;
        }
        if ((ch.fxflag & 1) == 0) ch.vibdepth = 0;
        if ((ch.fxflag & 2) == 0) ch.slideval = ch.slide = 0;

        if (version != 0)
        {
            if (note > 0x80) note -= 0x80;
            else note = (byte)(note + ch.transpose);
            ch.note = note;

            if (ch.ispfx != 0xffff && cnt < 0x20 && IndexOk(spfxAt, ch.ispfx, SpfxSize))
            {
                ch.spfx = ch.ispfx;
                long r = SpfxRec(ch.spfx);
                if ((W(r) & 0x8000) != 0) note = B(r + 2);
                else note = (byte)(note + (sbyte)B(r + 2));
                ch.inst = (ushort)(W(r) & 0xfff);
                ch.fxdel = B(r + 5);
                if (B(r + 3) != 0xff) ch.modvol = B(r + 3);
                else ch.modvol = (byte)(B(InstRec(ch.inst) + 7) & 63);
            }

            if (ch.ilevpuls != 0xff && cnt < 0x20 && IndexOk(levpulsAt, ch.ilevpuls, LevpulsSize) && IndexOk(instAt, ch.inst, InstSize))
            {
                ch.levpuls = ch.ilevpuls;
                ch.fxdel = B(LevRec(ch.levpuls) + 2);
                ch.frameskip = B(InstRec(ch.inst) + 12);
                if (B(LevRec(ch.levpuls)) != 0xff) ch.modvol = B(LevRec(ch.levpuls));
                else ch.modvol = (byte)(B(InstRec(ch.inst) + 7) & 63);
            }

            ch.freq = (ushort)(NoteTable[note % 12] + ((note / 12) << 10));
            if (cnt < 0x20) PlayNote(c);
            else { SetFreq(c); cnt -= 0x20; }
            ch.rhcnt = cnt;
        }
        else
        {
            if (cnt < 2) note = (byte)(note + ch.transpose);
            ch.note = note;
            ch.freq = (ushort)(NoteTable[note % 12] + ((note / 12) << 10));
            if (cnt == 1) SetFreq(c);
            else PlayNote(c);
        }
    }

    private void SetVolume(int chan)
    {
        int op = OpTable[chan];
        int insnr = channel[chan].inst;
        if (!IndexOk(instAt, insnr, InstSize)) return;
        long r = InstRec(insnr);
        int d2 = B(r + 2), d7 = B(r + 7), d10 = B(r + 10);
        int vol = channel[chan].vol;
        Opl.Write(0x43 + op, (int)(63 - ((63 - (d2 & 63)) / 63.0) * (63 - vol)) + (d2 & 192));
        if ((d10 & 1) != 0) Opl.Write(0x40 + op, (int)(63 - ((63 - channel[chan].modvol) / 63.0) * (63 - vol)) + (d7 & 192));
        else Opl.Write(0x40 + op, channel[chan].modvol + (d7 & 192));
    }

    private void SetFreq(int chan)
    {
        int freq = channel[chan].freq;
        if (version == 4 && IndexOk(instAt, channel[chan].inst, InstSize)) freq += B(InstRec(channel[chan].inst) + 11);
        freq = (freq + channel[chan].slideval) & 0xFFFF;
        Opl.Write(0xa0 + chan, freq & 255);
        if (channel[chan].key != 0) Opl.Write(0xb0 + chan, ((freq >> 8) & 31) | 32);
        else Opl.Write(0xb0 + chan, (freq >> 8) & 31);
    }

    private void SetInst(int chan)
    {
        int op = OpTable[chan];
        int insnr = channel[chan].inst;
        if (!IndexOk(instAt, insnr, InstSize)) return;
        long r = InstRec(insnr);
        Opl.Write(0x63 + op, B(r + 0));
        Opl.Write(0x83 + op, B(r + 1));
        Opl.Write(0x23 + op, B(r + 3));
        Opl.Write(0xe3 + op, B(r + 4));
        Opl.Write(0x60 + op, B(r + 5));
        Opl.Write(0x80 + op, B(r + 6));
        Opl.Write(0x20 + op, B(r + 8));
        Opl.Write(0xe0 + op, B(r + 9));
        if (version != 0) Opl.Write(0xc0 + chan, B(r + 10));
        else Opl.Write(0xc0 + chan, (B(r + 10) << 1) + (B(r + 11) & 1));
    }

    private void PlayNote(int chan)
    {
        Opl.Write(0xb0 + chan, 0);
        SetInst(chan);
        channel[chan].key = 1;
        SetFreq(chan);
        SetVolume(chan);
    }

    private void Vibrato(int chan)
    {
        var ch = channel[chan];
        if (ch.vibdepth == 0) return;
        if (ch.trigger != 0) ch.trigger--;
        else
        {
            ch.trigger = ch.vibdepth;
            ch.vibspeed = (short)-ch.vibspeed;
        }
        ch.freq = (ushort)(ch.freq + ch.vibspeed);
        SetFreq(chan);
    }
}
