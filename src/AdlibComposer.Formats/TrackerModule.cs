using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>Scream Tracker 모듈 종류: STM(Scream Tracker 2, 4채널) / S3M(Scream Tracker 3, 32채널 + AdLib).</summary>
public enum TrackerKind { Stm, S3m }

/// <summary>
/// 패턴 칸 1개. 값은 파일 그대로:
/// Note = 옥타브 &lt;&lt; 4 | 반음 (255 = 비어 있음, 254 = ^^ 노트 오프/컷),
/// Instrument = 1부터 (0 = 없음), Volume = 0~64 (255 = 없음), Command = 1(A) ~ 26(Z) (0 = 없음), Info = 인포바이트.
/// STM의 옥타브는 ST2 기준 (가운데 C = C-2), S3M은 ST3 기준 (가운데 C = C-4).
/// </summary>
public struct TrackerCell
{
    public const byte NoNote = 255, NoteOff = 254, NoVolume = 255;
    public byte Note, Instrument, Volume, Command, Info;

    public static readonly TrackerCell Empty = new TrackerCell { Note = NoNote, Volume = NoVolume };

    public bool IsEmpty { get { return Note == NoNote && Instrument == 0 && Volume == NoVolume && Command == 0 && Info == 0; } }
    public bool HasNote { get { return Note < NoteOff; } }
    public int Octave { get { return Note >> 4; } }
    public int Semitone { get { return Note & 15; } }

    public static byte MakeNote(int octave, int semitone) { return (byte)((octave << 4) | semitone); }
}

/// <summary>패턴: 64줄 × 채널 수.</summary>
public sealed class TrackerPattern
{
    public const int Rows = 64;
    public readonly int Channels;
    public readonly TrackerCell[] Cells;

    public TrackerPattern(int channels)
    {
        Channels = channels;
        Cells = new TrackerCell[Rows * channels];
        for (int i = 0; i < Cells.Length; i++) Cells[i] = TrackerCell.Empty;
    }

    public TrackerCell this[int row, int ch]
    {
        get { return Cells[row * Channels + ch]; }
        set { Cells[row * Channels + ch] = value; }
    }

    public TrackerPattern Clone()
    {
        var p = new TrackerPattern(Channels);
        Array.Copy(Cells, p.Cells, Cells.Length);
        return p;
    }

    public bool IsEmpty { get { foreach (var c in Cells) if (!c.IsEmpty) return false; return true; } }
}

/// <summary>악기(샘플 또는 AdLib FM). 샘플 데이터는 16bit 부호 있는 값으로 통일해 둔다 (8bit 샘플은 &lt;&lt; 8).</summary>
public sealed class TrackerSample
{
    public const int TypeEmpty = 0, TypeSample = 1, TypeAdlibMelody = 2;

    public int Type;
    public string Name = "";
    public string FileName = "";
    public short[] Data = new short[0];
    public bool Is16Bit;
    public bool Loop;
    public int LoopStart, LoopEnd;
    public int Volume = 64;
    /// <summary>C-4(S3M) / C-2(STM) 음에서의 재생 주파수 (Hz). 기본 8363 (STM 8448).</summary>
    public int C2Spd = 8363;
    /// <summary>AdLib 악기 D00~D0B (ST3 형식): 모듈레이터/캐리어 20 · 40 · 60 · 80 · E0, C0(피드백·연결), 미사용.</summary>
    public byte[] Adlib = new byte[12];

    public int Length { get { return Data.Length; } }
    public bool IsAdlib { get { return Type >= TypeAdlibMelody; } }
    public bool IsSample { get { return Type == TypeSample && Data.Length > 0; } }
    public bool IsEmpty { get { return Type == TypeEmpty || (Type == TypeSample && Data.Length == 0); } }

    public TrackerSample Clone()
    {
        var s = (TrackerSample)MemberwiseClone();
        s.Data = (short[])Data.Clone();
        s.Adlib = (byte[])Adlib.Clone();
        return s;
    }
}

/// <summary>Scream Tracker 2/3 곡 (STM / S3M 공용 모델).</summary>
public sealed class TrackerModule
{
    public const int MaxChannels = 32;
    public const byte OrderSkip = 254, OrderEnd = 255;

    public TrackerKind Kind = TrackerKind.S3m;
    public string Title = "";
    public string FilePath;
    /// <summary>처음 속도 (줄당 틱, 명령 A).</summary>
    public int Speed = 6;
    /// <summary>처음 템포 (BPM, 명령 T). STM은 ST2 템포 바이트 (윗 4비트 = 속도, 아랫 4비트 = 미세 템포).</summary>
    public int Tempo = 125;
    public int GlobalVolume = 64;
    /// <summary>S3M 마스터 볼륨 (0~127, 사운드블라스터 증폭).</summary>
    public int MasterVolume = 48;
    public bool Stereo = true;
    /// <summary>S3M 플래그 (+8 0볼륨 최적화, +16 아미가 한계, +64 빠른 볼륨 슬라이드).</summary>
    public int Flags;
    /// <summary>만든 트래커/버전 (S3M Cwt/v, 예: 0x1320 = ST3.20).</summary>
    public int TrackerVersion = 0x1320;
    /// <summary>채널 설정 32개: 0~7 = L1~L8, 8~15 = R1~R8, 16~24 = A1~A9 (AdLib 멜로디), 25~29 = AdLib 드럼, +128 = 끔, 255 = 없음.</summary>
    public byte[] ChannelSettings = new byte[MaxChannels];
    /// <summary>채널 기본 팬 0~15 (S3M).</summary>
    public byte[] Pan = new byte[MaxChannels];
    public List<byte> Orders = new List<byte>();
    public List<TrackerSample> Samples = new List<TrackerSample>();
    public List<TrackerPattern> Patterns = new List<TrackerPattern>();

    /// <summary>패턴 칸 수 (STM 4, S3M 32).</summary>
    public int Channels { get { return Kind == TrackerKind.Stm ? 4 : MaxChannels; } }

    public bool IsChannelUsed(int ch)
    {
        if (Kind == TrackerKind.Stm) return ch < 4;
        return ch < MaxChannels && ChannelSettings[ch] < 128;
    }

    public bool IsAdlibChannel(int ch) { return Kind == TrackerKind.S3m && ch < MaxChannels && ChannelSettings[ch] >= 16 && ChannelSettings[ch] < 32; }

    /// <summary>채널 이름: STM 1~4, S3M L1~R8 / A1~A9 / 꺼짐 --.</summary>
    public string ChannelLabel(int ch)
    {
        if (Kind == TrackerKind.Stm) return (ch + 1).ToString();
        int s = ChannelSettings[ch];
        if (s == 255) return "--";
        s &= 127;
        if (s < 8) return "L" + (s + 1);
        if (s < 16) return "R" + (s - 7);
        if (s < 25) return "A" + (s - 15);
        if (s < 30) return new[] { "AB", "AS", "AT", "AC", "AH" }[s - 25];
        return "--";
    }

    /// <summary>쓰는 채널 수 (마지막으로 켜진 채널 + 1).</summary>
    public int UsedChannelCount
    {
        get
        {
            int n = 0;
            for (int c = 0; c < Channels; c++) if (IsChannelUsed(c) || ChannelSettings[c] != 255 && Kind == TrackerKind.S3m) n = c + 1;
            return Kind == TrackerKind.Stm ? 4 : Math.Max(1, n);
        }
    }

    public TrackerPattern NewPattern() { return new TrackerPattern(Channels); }

    /// <summary>빈 새 곡. S3M: 8채널 L1 R1 L2 R2 …, 속도 6, 템포 125. STM: 4채널, 템포 0x60.</summary>
    public static TrackerModule CreateNew(TrackerKind kind)
    {
        var m = new TrackerModule { Kind = kind, Title = "untitled" };
        for (int c = 0; c < MaxChannels; c++) { m.ChannelSettings[c] = 255; m.Pan[c] = 7; }
        if (kind == TrackerKind.S3m)
        {
            for (int c = 0; c < 8; c++) { m.ChannelSettings[c] = (byte)(c % 2 == 0 ? c / 2 : 8 + c / 2); m.Pan[c] = (byte)(c % 2 == 0 ? 3 : 12); }
            m.Speed = 6; m.Tempo = 125; m.MasterVolume = 48; m.Stereo = true;
        }
        else
        {
            for (int c = 0; c < 4; c++) m.ChannelSettings[c] = (byte)c;
            m.Speed = 6; m.Tempo = 0x60; m.Stereo = false;
        }
        m.Patterns.Add(m.NewPattern());
        m.Orders.Add(0);
        return m;
    }

    /// <summary>재생할 주문 목록 (254 건너뜀 제외, 255에서 끝).</summary>
    public List<int> PlayOrders()
    {
        var l = new List<int>();
        foreach (var o in Orders)
        {
            if (o == OrderEnd) break;
            if (o == OrderSkip) continue;
            l.Add(o);
        }
        return l;
    }

    /// <summary>STM ↔ S3M 바꾸기 (저장 형식이 다를 때). 잃는 것은 경고 목록으로 돌려준다.</summary>
    public TrackerModule ConvertTo(TrackerKind kind, List<string> warnings)
    {
        if (kind == Kind) return this;
        var m = CreateNew(kind);
        m.Title = Title; m.GlobalVolume = GlobalVolume; m.FilePath = null;
        m.Patterns.Clear();
        m.Orders = new List<byte>(Orders);
        foreach (var s in Samples) m.Samples.Add(s.Clone());
        int octShift = kind == TrackerKind.S3m ? 2 : -2;   // ST2 가운데 C = C-2, ST3 = C-4
        if (kind == TrackerKind.S3m)
        {
            m.Speed = Math.Max(1, Tempo >> 4);
            m.Tempo = 125;
            // 4채널 → L1 R1 R2 L2 (아미가 배치와 비슷하게)
            byte[] layout = { 0, 8, 9, 1 };
            for (int c = 0; c < 4; c++) { m.ChannelSettings[c] = layout[c]; m.Pan[c] = (byte)(layout[c] < 8 ? 3 : 12); }
            for (int c = 4; c < MaxChannels; c++) m.ChannelSettings[c] = 255;
        }
        else
        {
            m.Tempo = Math.Max(1, Math.Min(15, Speed)) << 4;
            if (Tempo != 125) warnings.Add("템포 " + Tempo + " BPM은 STM에 없어 속도만 옮겼습니다");
            int adlib = 0;
            foreach (var s in m.Samples) if (s.IsAdlib) { adlib++; s.Type = TrackerSample.TypeEmpty; }
            if (adlib > 0) warnings.Add("AdLib 악기 " + adlib + "개는 STM에 없어 뺐습니다");
            if (m.Samples.Count > 31) { warnings.Add("악기 32번부터는 STM에 없어 뺐습니다"); m.Samples.RemoveRange(31, m.Samples.Count - 31); }
            if (m.Patterns.Count > 63) warnings.Add("패턴 64번부터는 STM에 넣을 수 없습니다");
        }
        int droppedCh = 0, droppedFx = 0;
        foreach (var p in Patterns)
        {
            var np = m.NewPattern();
            for (int r = 0; r < TrackerPattern.Rows; r++)
            {
                int dst = 0;
                for (int c = 0; c < Channels; c++)
                {
                    var cell = p[r, c];
                    if (kind == TrackerKind.Stm)
                    {
                        if (!IsChannelUsed(c)) continue;
                        if (dst >= 4) { if (!cell.IsEmpty) droppedCh++; continue; }
                    }
                    else dst = c;
                    if (cell.HasNote) cell.Note = TrackerCell.MakeNote(Math.Max(0, Math.Min(kind == TrackerKind.Stm ? 4 : 7, cell.Octave + octShift)), cell.Semitone);
                    if (kind == TrackerKind.Stm && cell.Command > 10) { droppedFx++; cell.Command = 0; cell.Info = 0; }
                    if (kind == TrackerKind.Stm && cell.Instrument > 31) cell.Instrument = 0;
                    if (dst < np.Channels) np[r, dst] = cell;
                    dst++;
                }
            }
            m.Patterns.Add(np);
        }
        if (droppedCh > 0) warnings.Add("5번째 채널부터의 칸 " + droppedCh + "개는 STM(4채널)에 없어 뺐습니다");
        if (droppedFx > 0) warnings.Add("STM에 없는 명령(K~Z) " + droppedFx + "개를 뺐습니다");
        return m;
    }

    internal static string Ascii(byte[] d, int off, int len)
    {
        int n = 0;
        while (n < len && off + n < d.Length && d[off + n] != 0) n++;
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) { byte b = d[off + i]; sb.Append(b >= 32 && b < 127 ? (char)b : b >= 128 ? KoreanOrCp437(b) : ' '); }
        return sb.ToString().TrimEnd();
    }

    private static char KoreanOrCp437(byte b) { return (char)b < (char)0xA0 ? '?' : (char)(0x00A0 + (b - 0xA0)); }

    internal static void PutAscii(byte[] d, int off, int len, string s)
    {
        s = s ?? "";
        for (int i = 0; i < len; i++) d[off + i] = 0;
        for (int i = 0; i < Math.Min(len - 1, s.Length); i++) { char c = s[i]; d[off + i] = (byte)(c < 256 ? c : '?'); }
    }
}

/// <summary>Scream Tracker 2 모듈 (.STM). st2play(viiri/st2play) 로더와 같은 해석.</summary>
public static class StmFormat
{
    public const int HeaderSize = 48, InstrumentSize = 32, PatternSize = 1024;

    public static TrackerModule Load(string path)
    {
        var m = Read(File.ReadAllBytes(path));
        m.FilePath = path;
        return m;
    }

    public static TrackerModule Read(byte[] d)
    {
        // st2play처럼 종류(1 · 2)와 버전(2.xx)만 본다. 0x1A 자리가 다른 파일도 있다 (Putup 10/11.stm: 0x02).
        if (d.Length < HeaderSize + 31 * InstrumentSize + 64 || (d[29] != 1 && d[29] != 2) || d[30] != 2 || d[31] > 21)
            throw new InvalidDataException("STM 파일이 아닙니다.");
        int version = d[30] * 100 + d[31];
        var m = TrackerModule.CreateNew(TrackerKind.Stm);
        m.Patterns.Clear(); m.Orders.Clear();
        m.Title = TrackerModule.Ascii(d, 0, 20);
        int tempo = d[32];
        if (version < 221) tempo = (tempo / 10 << 4) + tempo % 10;
        m.Tempo = tempo == 0 ? 0x60 : tempo;
        m.Speed = Math.Max(1, m.Tempo >> 4);
        int nPat = d[33];
        m.GlobalVolume = version > 210 ? Math.Min(64, (int)d[34]) : 64;
        var offsets = new int[31];
        for (int i = 0; i < 31; i++)
        {
            int o = HeaderSize + i * InstrumentSize;
            var s = new TrackerSample { Type = TrackerSample.TypeSample, FileName = TrackerModule.Ascii(d, o, 12) };
            s.Name = s.FileName;
            offsets[i] = BitConverter.ToUInt16(d, o + 14) << 4;
            int length = BitConverter.ToUInt16(d, o + 16);
            int ls = BitConverter.ToUInt16(d, o + 18), le = BitConverter.ToUInt16(d, o + 20);
            s.Volume = Math.Min(64, (int)d[o + 22]);
            s.C2Spd = BitConverter.ToUInt16(d, o + 24);
            if (s.C2Spd == 0) s.C2Spd = 8448;
            if (le == 0) le = 0xFFFF;
            if (le != 0xFFFF && le > length) le = length;   // st2play: amegas.stm
            s.Loop = le != 0xFFFF && le > ls;
            s.LoopStart = s.Loop ? ls : 0;
            s.LoopEnd = s.Loop ? le : length;
            s.Data = new short[length];
            m.Samples.Add(s);
        }
        int ordLen = version == 200 ? 64 : 128;
        int p = HeaderSize + 31 * InstrumentSize;
        for (int i = 0; i < ordLen && p + i < d.Length; i++)
        {
            byte o = d[p + i];
            if (o >= 99) { m.Orders.Add(TrackerModule.OrderEnd); break; }
            m.Orders.Add(o);
        }
        if (m.Orders.Count == 0 || m.Orders[m.Orders.Count - 1] != TrackerModule.OrderEnd) m.Orders.Add(TrackerModule.OrderEnd);
        p += ordLen;
        for (int pi = 0; pi < nPat; pi++)
        {
            var pat = m.NewPattern();
            for (int k = 0; k < 256 && p < d.Length; k++)
            {
                int row = k / 4, ch = k % 4;
                byte code = d[p];
                TrackerCell cell;
                if (code == 0xFB) { cell = new TrackerCell { Note = 0, Volume = 0 }; p++; }       // st2play: 모두 0인 칸
                else if (code == 0xFC) { cell = TrackerCell.Empty; p++; }
                else if (code == 0xFD) { cell = TrackerCell.Empty; cell.Note = TrackerCell.NoteOff; p++; }
                else
                {
                    if (p + 4 > d.Length) break;
                    byte b1 = d[p + 1], b2 = d[p + 2], b3 = d[p + 3];
                    int vol = (b1 & 7) | ((b2 >> 1) & 0x78);
                    int cmd = b2 & 0x0F;
                    if (version < 221 && cmd == 1) b3 = (byte)((b3 / 10 << 4) + b3 % 10);
                    cell = new TrackerCell
                    {
                        Note = code, Instrument = (byte)(b1 >> 3), Volume = (byte)(vol > 64 ? TrackerCell.NoVolume : vol),
                        Command = (byte)cmd, Info = b3,
                    };
                    p += 4;
                }
                pat[row, ch] = cell;
            }
            m.Patterns.Add(pat);
        }
        if (d[29] == 2)
            for (int i = 0; i < 31; i++)
            {
                var s = m.Samples[i];
                if (s.Volume == 0 || s.Data.Length == 0) { if (s.Data.Length == 0) s.Type = TrackerSample.TypeEmpty; continue; }
                int o = offsets[i];
                for (int k = 0; k < s.Data.Length; k++) s.Data[k] = o + k < d.Length ? (short)((sbyte)d[o + k] << 8) : (short)0;
            }
        foreach (var s in m.Samples) if (s.Data.Length == 0) s.Type = TrackerSample.TypeEmpty;
        return m;
    }

    public static byte[] Write(TrackerModule m)
    {
        if (m.Kind != TrackerKind.Stm) throw new InvalidOperationException("STM으로 저장하려면 먼저 ConvertTo(Stm)");
        var ms = new MemoryStream();
        var head = new byte[HeaderSize];
        TrackerModule.PutAscii(head, 0, 20, m.Title);
        Encoding.ASCII.GetBytes("!Scream!").CopyTo(head, 20);
        head[28] = 0x1A; head[29] = 2; head[30] = 2; head[31] = 21;
        head[32] = (byte)m.Tempo;
        int nPat = Math.Min(63, m.Patterns.Count);
        head[33] = (byte)nPat;
        head[34] = (byte)Math.Min(64, m.GlobalVolume);
        ms.Write(head, 0, head.Length);

        // 샘플 위치: 헤더 + 악기 + 주문 + 패턴 뒤, 16바이트 정렬
        int pos = HeaderSize + 31 * InstrumentSize + 128 + nPat * PatternSize;
        var insts = new byte[31 * InstrumentSize];
        var offs = new int[31];
        for (int i = 0; i < 31; i++)
        {
            var s = i < m.Samples.Count ? m.Samples[i] : null;
            int o = i * InstrumentSize;
            int len = s != null && s.IsSample ? Math.Min(65535, s.Length) : 0;
            TrackerModule.PutAscii(insts, o, 12, s != null ? (s.FileName.Length > 0 ? s.FileName : s.Name) : "");
            if (len > 0) { pos = (pos + 15) & ~15; offs[i] = pos; pos += len; }
            BitConverter.GetBytes((ushort)(offs[i] >> 4)).CopyTo(insts, o + 14);
            BitConverter.GetBytes((ushort)len).CopyTo(insts, o + 16);
            bool loop = s != null && s.Loop && s.LoopEnd > s.LoopStart && len > 0;
            BitConverter.GetBytes((ushort)(loop ? s.LoopStart : 0)).CopyTo(insts, o + 18);
            BitConverter.GetBytes((ushort)(loop ? Math.Min(len, s.LoopEnd) : 0xFFFF)).CopyTo(insts, o + 20);
            insts[o + 22] = (byte)(s != null && len > 0 ? Math.Min(64, s.Volume) : 0);
            BitConverter.GetBytes((ushort)(s != null && s.C2Spd > 0 ? Math.Min(65535, s.C2Spd) : 8448)).CopyTo(insts, o + 24);
            BitConverter.GetBytes((ushort)((len + 15) >> 4)).CopyTo(insts, o + 30);
        }
        ms.Write(insts, 0, insts.Length);
        var ord = new byte[128];
        for (int i = 0; i < 128; i++) ord[i] = 99;
        int oi = 0;
        foreach (var o in m.Orders)
        {
            if (o == TrackerModule.OrderEnd || oi >= 127) break;
            if (o == TrackerModule.OrderSkip || o >= nPat) continue;
            ord[oi++] = o;
        }
        ms.Write(ord, 0, 128);
        for (int pi = 0; pi < nPat; pi++)
        {
            var pat = m.Patterns[pi];
            var b = new byte[PatternSize];
            for (int r = 0; r < 64; r++)
                for (int c = 0; c < 4; c++)
                {
                    var cell = pat[r, c];
                    int k = (r * 4 + c) * 4;
                    int vol = cell.Volume == TrackerCell.NoVolume ? 65 : Math.Min(64, (int)cell.Volume);
                    b[k] = cell.Note;
                    b[k + 1] = (byte)(((cell.Instrument & 31) << 3) | (vol & 7));
                    b[k + 2] = (byte)(((vol & 0x78) << 1) | (cell.Command & 0x0F));
                    b[k + 3] = cell.Info;
                }
            ms.Write(b, 0, b.Length);
        }
        for (int i = 0; i < 31; i++)
        {
            if (offs[i] == 0) continue;
            while (ms.Length < offs[i]) ms.WriteByte(0);
            var s = m.Samples[i];
            int len = Math.Min(65535, s.Length);
            for (int k = 0; k < len; k++) ms.WriteByte((byte)(sbyte)(s.Data[k] >> 8));
        }
        return ms.ToArray();
    }
}

/// <summary>Scream Tracker 3 모듈 (.S3M). TECH.DOC(ST3.20) 형식: 헤더 · 악기(샘플/AdLib) · 압축 패턴 · 샘플(부호 없는 8/16bit).</summary>
public static class S3mFormat
{
    public static TrackerModule Load(string path)
    {
        var m = Read(File.ReadAllBytes(path));
        m.FilePath = path;
        return m;
    }

    public static TrackerModule Read(byte[] d)
    {
        if (d.Length < 0x60 || d[0x2C] != 'S' || d[0x2D] != 'C' || d[0x2E] != 'R' || d[0x2F] != 'M')
            throw new InvalidDataException("S3M 파일이 아닙니다 (SCRM 서명 없음).");
        var m = new TrackerModule { Kind = TrackerKind.S3m };
        m.Title = TrackerModule.Ascii(d, 0, 28);
        int ordNum = U16(d, 0x20), insNum = U16(d, 0x22), patNum = U16(d, 0x24);
        m.Flags = U16(d, 0x26);
        m.TrackerVersion = U16(d, 0x28);
        int ffi = U16(d, 0x2A);
        m.GlobalVolume = Math.Min(64, (int)d[0x30]);
        m.Speed = d[0x31] == 0 || d[0x31] == 255 ? 6 : d[0x31];
        m.Tempo = d[0x32] < 33 ? 125 : d[0x32];
        m.MasterVolume = d[0x33] & 127;
        m.Stereo = (d[0x33] & 128) != 0;
        bool hasPan = d[0x35] == 252;
        Array.Copy(d, 0x40, m.ChannelSettings, 0, 32);
        int p = 0x60;
        for (int i = 0; i < ordNum && p + i < d.Length; i++) m.Orders.Add(d[p + i]);
        p += ordNum;
        var insPtr = new int[insNum];
        for (int i = 0; i < insNum; i++) insPtr[i] = U16(d, p + i * 2) * 16;
        p += insNum * 2;
        var patPtr = new int[patNum];
        for (int i = 0; i < patNum; i++) patPtr[i] = U16(d, p + i * 2) * 16;
        p += patNum * 2;
        for (int c = 0; c < 32; c++)
        {
            int set = m.ChannelSettings[c] & 127;
            int def = !m.Stereo ? 7 : set < 8 ? 3 : set < 16 ? 12 : 7;
            m.Pan[c] = (byte)def;
            if (hasPan && p + c < d.Length && (d[p + c] & 0x20) != 0) m.Pan[c] = (byte)(d[p + c] & 15);
        }
        bool signed = ffi == 1;
        for (int i = 0; i < insNum; i++)
        {
            int o = insPtr[i];
            var s = new TrackerSample();
            if (o == 0 || o + 0x50 > d.Length) { m.Samples.Add(s); continue; }
            s.Type = d[o];
            s.FileName = TrackerModule.Ascii(d, o + 1, 12);
            s.Name = TrackerModule.Ascii(d, o + 0x30, 28);
            s.C2Spd = (int)U32(d, o + 0x20) & 0xFFFF;
            if (s.C2Spd == 0) s.C2Spd = 8363;
            if (s.Type == TrackerSample.TypeSample)
            {
                int memseg = (d[o + 0x0D] << 16) | U16(d, o + 0x0E);
                int len = (int)Math.Min(U32(d, o + 0x10), 1 << 20);
                s.LoopStart = (int)U32(d, o + 0x14);
                s.LoopEnd = (int)U32(d, o + 0x18);
                s.Volume = Math.Min(64, (int)d[o + 0x1C]);
                int flags = d[o + 0x1F];
                s.Loop = (flags & 1) != 0;
                s.Is16Bit = (flags & 4) != 0;
                bool stereo = (flags & 2) != 0;
                int dataOff = memseg * 16;
                int bytes = s.Is16Bit ? 2 : 1;
                if (dataOff + len * bytes > d.Length) len = Math.Max(0, (d.Length - dataOff) / bytes);
                s.Data = new short[Math.Max(0, len)];
                for (int k = 0; k < s.Data.Length; k++)
                {
                    int v;
                    if (s.Is16Bit) { v = U16(d, dataOff + k * 2); if (!signed) v -= 32768; else v = (short)v; }
                    else { v = d[dataOff + k]; v = signed ? (sbyte)v << 8 : (v - 128) << 8; }
                    if (stereo && dataOff + (len + k) * bytes < d.Length)   // 스테레오 샘플은 왼쪽 + 오른쪽 평균
                    {
                        int w;
                        if (s.Is16Bit) { w = U16(d, dataOff + (len + k) * 2); w = signed ? (short)w : w - 32768; }
                        else { w = d[dataOff + len + k]; w = signed ? (sbyte)w << 8 : (w - 128) << 8; }
                        v = (v + w) / 2;
                    }
                    s.Data[k] = (short)v;
                }
                if (s.LoopEnd > s.Data.Length) s.LoopEnd = s.Data.Length;
                if (s.LoopStart >= s.LoopEnd) s.Loop = false;
                if (s.Data.Length == 0) s.Type = TrackerSample.TypeEmpty;
            }
            else if (s.Type >= TrackerSample.TypeAdlibMelody)
            {
                Array.Copy(d, o + 0x10, s.Adlib, 0, 12);
                s.Volume = Math.Min(64, (int)d[o + 0x1C]);
            }
            m.Samples.Add(s);
        }
        for (int i = 0; i < patNum; i++)
        {
            var pat = m.NewPattern();
            int o = patPtr[i];
            if (o > 0 && o + 2 <= d.Length)
            {
                int end = Math.Min(d.Length, o + 2 + U16(d, o));
                int q = o + 2, row = 0;
                while (row < 64 && q < end)
                {
                    byte what = d[q++];
                    if (what == 0) { row++; continue; }
                    int ch = what & 31;
                    var cell = pat[row, ch];
                    if ((what & 32) != 0 && q + 2 <= end) { cell.Note = d[q]; cell.Instrument = d[q + 1]; q += 2; }
                    if ((what & 64) != 0 && q + 1 <= end) { cell.Volume = d[q]; q += 1; }
                    if ((what & 128) != 0 && q + 2 <= end) { cell.Command = d[q]; cell.Info = d[q + 1]; q += 2; }
                    if (cell.Volume > 64 && cell.Volume != TrackerCell.NoVolume) cell.Volume = 64;
                    pat[row, ch] = cell;
                }
            }
            m.Patterns.Add(pat);
        }
        return m;
    }

    public static byte[] Write(TrackerModule m)
    {
        if (m.Kind != TrackerKind.S3m) throw new InvalidOperationException("S3M으로 저장하려면 먼저 ConvertTo(S3m)");
        var orders = new List<byte>(m.Orders);
        if (orders.Count == 0 || orders[orders.Count - 1] != TrackerModule.OrderEnd) orders.Add(TrackerModule.OrderEnd);
        if (orders.Count % 2 == 1) orders.Add(TrackerModule.OrderEnd);
        int insNum = m.Samples.Count, patNum = m.Patterns.Count;
        var buf = new List<byte>();
        var head = new byte[0x60];
        TrackerModule.PutAscii(head, 0, 28, m.Title);
        head[0x1C] = 0x1A; head[0x1D] = 16;
        Put16(head, 0x20, orders.Count); Put16(head, 0x22, insNum); Put16(head, 0x24, patNum);
        Put16(head, 0x26, m.Flags & ~128); Put16(head, 0x28, m.TrackerVersion == 0 ? 0x1320 : m.TrackerVersion); Put16(head, 0x2A, 2);
        head[0x2C] = (byte)'S'; head[0x2D] = (byte)'C'; head[0x2E] = (byte)'R'; head[0x2F] = (byte)'M';
        head[0x30] = (byte)Math.Min(64, m.GlobalVolume);
        head[0x31] = (byte)Math.Max(1, Math.Min(255, m.Speed));
        head[0x32] = (byte)Math.Max(33, Math.Min(255, m.Tempo));
        head[0x33] = (byte)((m.MasterVolume & 127) | (m.Stereo ? 128 : 0));
        head[0x34] = 16; head[0x35] = 252;
        Array.Copy(m.ChannelSettings, 0, head, 0x40, 32);
        buf.AddRange(head);
        buf.AddRange(orders);
        int insPtrAt = buf.Count;
        buf.AddRange(new byte[insNum * 2]);
        int patPtrAt = buf.Count;
        buf.AddRange(new byte[patNum * 2]);
        for (int c = 0; c < 32; c++) buf.Add((byte)(0x20 | (m.Pan[c] & 15)));

        var insAt = new int[insNum];
        for (int i = 0; i < insNum; i++) { Align(buf); insAt[i] = buf.Count; buf.AddRange(new byte[0x50]); }
        for (int i = 0; i < patNum; i++)
        {
            Align(buf);
            Put16(buf, patPtrAt + i * 2, buf.Count / 16);
            var packed = Pack(m.Patterns[i]);
            buf.Add((byte)(packed.Count + 2)); buf.Add((byte)((packed.Count + 2) >> 8));
            buf.AddRange(packed);
        }
        for (int i = 0; i < insNum; i++)
        {
            var s = m.Samples[i];
            var ins = new byte[0x50];
            ins[0] = (byte)(s.IsEmpty ? 0 : s.Type);
            TrackerModule.PutAscii(ins, 1, 13, s.FileName);
            TrackerModule.PutAscii(ins, 0x30, 28, s.Name);
            Put32(ins, 0x20, s.C2Spd);
            if (s.IsAdlib)
            {
                Array.Copy(s.Adlib, 0, ins, 0x10, 12);
                ins[0x1C] = (byte)Math.Min(64, s.Volume);
                ins[0x4C] = (byte)'S'; ins[0x4D] = (byte)'C'; ins[0x4E] = (byte)'R'; ins[0x4F] = (byte)'I';
            }
            else
            {
                if (s.IsSample)
                {
                    Align(buf);
                    int seg = buf.Count / 16;
                    ins[0x0D] = (byte)(seg >> 16); ins[0x0E] = (byte)seg; ins[0x0F] = (byte)(seg >> 8);
                    foreach (var v in s.Data)
                    {
                        if (s.Is16Bit) { int u = v + 32768; buf.Add((byte)u); buf.Add((byte)(u >> 8)); }
                        else buf.Add((byte)((v >> 8) + 128));
                    }
                }
                Put32(ins, 0x10, s.IsSample ? s.Length : 0);
                Put32(ins, 0x14, s.Loop ? s.LoopStart : 0);
                Put32(ins, 0x18, s.Loop ? s.LoopEnd : 0);
                ins[0x1C] = (byte)Math.Min(64, s.Volume);
                ins[0x1F] = (byte)((s.Loop ? 1 : 0) | (s.Is16Bit ? 4 : 0));
                ins[0x4C] = (byte)'S'; ins[0x4D] = (byte)'C'; ins[0x4E] = (byte)'R'; ins[0x4F] = (byte)'S';
            }
            for (int k = 0; k < 0x50; k++) buf[insAt[i] + k] = ins[k];
            Put16(buf, insPtrAt + i * 2, insAt[i] / 16);
        }
        return buf.ToArray();
    }

    private static List<byte> Pack(TrackerPattern p)
    {
        var l = new List<byte>();
        for (int r = 0; r < 64; r++)
        {
            for (int c = 0; c < p.Channels; c++)
            {
                var cell = p[r, c];
                if (cell.IsEmpty) continue;
                int what = c & 31;
                if (cell.Note != TrackerCell.NoNote || cell.Instrument != 0) what |= 32;
                if (cell.Volume != TrackerCell.NoVolume) what |= 64;
                if (cell.Command != 0 || cell.Info != 0) what |= 128;
                l.Add((byte)what);
                if ((what & 32) != 0) { l.Add(cell.Note); l.Add(cell.Instrument); }
                if ((what & 64) != 0) l.Add(cell.Volume);
                if ((what & 128) != 0) { l.Add(cell.Command); l.Add(cell.Info); }
            }
            l.Add(0);
        }
        return l;
    }

    private static void Align(List<byte> b) { while (b.Count % 16 != 0) b.Add(0); }
    private static int U16(byte[] d, int o) { return o + 1 < d.Length ? d[o] | (d[o + 1] << 8) : 0; }
    private static uint U32(byte[] d, int o) { return o + 3 < d.Length ? (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)) : 0; }
    private static void Put16(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
    private static void Put16(List<byte> d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
    private static void Put32(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }
}

/// <summary>확장자로 STM / S3M 고르기.</summary>
public static class TrackerFiles
{
    public static readonly string[] Extensions = { ".s3m", ".stm" };

    public static bool IsTracker(string path)
    {
        string e = Path.GetExtension(path).ToLowerInvariant();
        return e == ".s3m" || e == ".stm";
    }

    public static TrackerModule Load(string path)
    {
        return string.Equals(Path.GetExtension(path), ".stm", StringComparison.OrdinalIgnoreCase) ? StmFormat.Load(path) : S3mFormat.Load(path);
    }

    /// <summary>확장자에 맞는 형식으로 저장 (필요하면 바꾼다). 바꾸면서 잃은 것은 warnings.</summary>
    public static TrackerModule Save(TrackerModule m, string path, List<string> warnings)
    {
        bool stm = string.Equals(Path.GetExtension(path), ".stm", StringComparison.OrdinalIgnoreCase);
        var target = m.ConvertTo(stm ? TrackerKind.Stm : TrackerKind.S3m, warnings);
        File.WriteAllBytes(path, stm ? StmFormat.Write(target) : S3mFormat.Write(target));
        target.FilePath = path;
        return target;
    }
}
