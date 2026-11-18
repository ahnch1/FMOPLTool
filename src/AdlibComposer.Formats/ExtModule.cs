using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// XM (FastTracker 2) · IT (Impulse Tracker) 곡 공용 모델 (재생 전용 — 편집 · 저장은 S3M 모델 <see cref="TrackerModule"/>).
/// 음 번호는 둘 다 0 = C-0 ~ 119 = B-9 (C-5 = 60 이 샘플의 <see cref="ExtSample.C5Speed"/>로 울린다).
/// 효과는 두 형식 그대로 두고 (<see cref="ExtModule.IsIt"/>로 갈라 해석), 볼륨 칸만 공용 종류(<see cref="ExtVolCmd"/>)로 바꿔 둔다.
/// </summary>
public sealed class ExtModule
{
    public const int MaxChannels = 64;
    /// <summary>음 칸 값: 0 = 없음, 1~120 = 음 (값 − 1 = 음 번호), 그 밖은 아래.</summary>
    public const byte NoteNone = 0, NoteFade = 253, NoteCut = 254, NoteOff = 255;

    public bool IsIt;
    public string Title = "";
    public string FilePath;
    public string Tracker = "";
    public string Message = "";
    /// <summary>패턴 채널 수.</summary>
    public int Channels;
    /// <summary>주문 (IT: 254 = 건너뜀, 255 = 끝).</summary>
    public List<int> Orders = new List<int>();
    /// <summary>XM 되돌아갈 주문 (곡 끝에서).</summary>
    public int RestartOrder;
    public int Speed = 6, Tempo = 125;
    /// <summary>전체 볼륨 0~128 (XM 0~64는 ×2).</summary>
    public int GlobalVolume = 128;
    /// <summary>IT 믹싱 볼륨 0~128 (XM은 48 상당).</summary>
    public int MixVolume = 48;
    public bool LinearSlides = true;
    /// <summary>IT: 악기 사용 (끄면 칸의 악기 번호 = 샘플 번호).</summary>
    public bool UseInstruments = true;
    /// <summary>IT: 옛 효과 (진동 · 오프셋 등 ST3식).</summary>
    public bool OldEffects;
    /// <summary>IT: G 메모리를 E · F와 같이 쓴다.</summary>
    public bool CompatGxx;
    public bool Stereo = true;
    /// <summary>채널 처음 팬 0~64 (32 = 가운데), 채널 볼륨 0~64 (IT), 꺼진 채널.</summary>
    public int[] ChannelPan = new int[MaxChannels];
    public int[] ChannelVolume = new int[MaxChannels];
    public bool[] ChannelMuted = new bool[MaxChannels];
    public bool[] ChannelSurround = new bool[MaxChannels];
    public List<ExtPattern> Patterns = new List<ExtPattern>();
    public List<ExtInstrument> Instruments = new List<ExtInstrument>();
    public List<ExtSample> Samples = new List<ExtSample>();

    public string FormatName { get { return IsIt ? "IT · Impulse Tracker" : "XM · FastTracker 2"; } }

    /// <summary>재생할 주문 (IT 254 건너뜀 빼고 255에서 끝, 없는 패턴 번호도 뺌).</summary>
    public List<int> PlayOrders()
    {
        var l = new List<int>();
        foreach (var o in Orders)
        {
            if (o == 255) break;
            if (o == 254) continue;
            if (o < Patterns.Count) l.Add(o);
        }
        return l;
    }

    public ExtSample SampleAt(int no) { return no > 0 && no <= Samples.Count ? Samples[no - 1] : null; }
    public ExtInstrument InstrumentAt(int no) { return no > 0 && no <= Instruments.Count ? Instruments[no - 1] : null; }

    /// <summary>채널 이름 (1부터).</summary>
    public string ChannelLabel(int c) { return (c + 1).ToString(); }

    /// <summary>칸의 악기 번호 → 이름 (IT 샘플 모드면 샘플 이름).</summary>
    public string InstrumentName(int no)
    {
        if (IsIt && !UseInstruments) { var s = SampleAt(no); return s == null ? null : Label(s.Name, s.FileName); }
        var ins = InstrumentAt(no);
        if (ins == null) return null;
        string n = Label(ins.Name, ins.FileName);
        if (n.Length == 0) foreach (var sn in ins.KeySample) { var s = SampleAt(sn); if (s != null && s.Name.Trim().Length > 0) return s.Name.Trim(); }
        return n;
    }

    private static string Label(string a, string b) { a = (a ?? "").Trim(); return a.Length > 0 ? a : (b ?? "").Trim(); }

    // ================================================================ 읽기

    public static readonly string[] Extensions = { ".xm", ".it" };

    public static bool IsExtModule(string path)
    {
        string e = Path.GetExtension(path ?? "").ToLowerInvariant();
        return e == ".xm" || e == ".it";
    }

    public static ExtModule Load(string path)
    {
        var d = ZipPath.ReadAllBytes(path);
        var m = Read(d);
        m.FilePath = path;
        return m;
    }

    /// <summary>곡 제목만 (샘플을 풀지 않는다: 파일 목록의 제목 칸).</summary>
    public static string ReadTitle(string path)
    {
        var d = ZipPath.ReadAllBytes(path);
        if (d.Length >= 30 && d[0] == 'I' && d[1] == 'M' && d[2] == 'P' && d[3] == 'M') return Text(d, 4, 26);
        if (d.Length >= 37 && Encoding.ASCII.GetString(d, 0, 17).Equals("Extended Module: ", StringComparison.OrdinalIgnoreCase)) return Text(d, 17, 20);
        return "";
    }

    /// <summary>머리말 보고 XM / IT를 가른다.</summary>
    public static ExtModule Read(byte[] d)
    {
        if (d.Length >= 4 && d[0] == 'I' && d[1] == 'M' && d[2] == 'P' && d[3] == 'M') return ItReader.Read(d);
        if (d.Length >= 17 && Encoding.ASCII.GetString(d, 0, 17).Equals("Extended Module: ", StringComparison.OrdinalIgnoreCase)) return XmReader.Read(d);
        throw new InvalidDataException("XM · IT 파일이 아닙니다.");
    }

    internal static string Text(byte[] d, int off, int len)
    {
        if (off >= d.Length) return "";
        len = Math.Min(len, d.Length - off);
        int n = 0;
        while (n < len && d[off + n] != 0) n++;
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) { byte b = d[off + i]; sb.Append(b >= 32 && b < 127 ? (char)b : ' '); }
        return sb.ToString().Trim();
    }

    internal static int U16(byte[] d, int o) { return o + 1 < d.Length ? d[o] | (d[o + 1] << 8) : 0; }
    internal static int U32(byte[] d, int o) { return o + 3 < d.Length ? d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24) : 0; }
}

/// <summary>패턴: 줄 수 × 채널 (XM 1~256줄, IT 1~200줄).</summary>
public sealed class ExtPattern
{
    public readonly int Rows, Channels;
    public readonly ExtCell[] Cells;

    public ExtPattern(int rows, int channels)
    {
        Rows = Math.Max(1, rows);
        Channels = Math.Max(1, channels);
        Cells = new ExtCell[Rows * Channels];
    }

    public ExtCell this[int row, int ch]
    {
        get { return Cells[row * Channels + ch]; }
        set { Cells[row * Channels + ch] = value; }
    }
}

/// <summary>볼륨 칸 종류 (XM · IT 공용).</summary>
public enum ExtVolCmd : byte
{
    None, Volume, Panning, SlideUp, SlideDown, FineUp, FineDown, PortaUp, PortaDown, TonePorta, VibratoSpeed, VibratoDepth, PanSlideLeft, PanSlideRight,
}

/// <summary>
/// 패턴 칸. Note: <see cref="ExtModule.NoteNone"/> · 1~120 · Fade · Cut · Off. Instrument 1부터 (0 = 없음).
/// Effect: XM = 1 + 효과 번호 (0~35, 1 = "0xx" 아르페지오 …, 0 = 없음 — 0xx가 00이면 효과 없음), IT = 1(A) ~ 26(Z).
/// VolValue: Volume 0~64 · Panning 0~64 · 미끄럼/포르타 양.
/// </summary>
public struct ExtCell
{
    public byte Note, Instrument, Effect, Param;
    public ExtVolCmd VolCmd;
    public byte VolValue;
}

/// <summary>엔벨로프 (점: 틱, 값). 볼륨 0~64, 팬 −32~32, 음높이 −32~32 (반음 반 단위).</summary>
public sealed class ExtEnvelope
{
    public bool Enabled, Loop, Sustain;
    /// <summary>IT 음높이 엔벨로프가 필터용인가 (필터는 내지 않아 쓰지 않는다).</summary>
    public bool Filter;
    public int LoopStart, LoopEnd, SustainStart, SustainEnd;
    public readonly List<int> Ticks = new List<int>();
    public readonly List<int> Values = new List<int>();

    public bool Usable { get { return Enabled && Ticks.Count > 0; } }
}

/// <summary>악기 (XM 악기 · IT 악기). 음마다 쓸 샘플 (1부터, 0 = 없음)과 IT는 음 바꾸기.</summary>
public sealed class ExtInstrument
{
    public string Name = "", FileName = "";
    /// <summary>음 0~119 → 샘플 번호 (1부터).</summary>
    public readonly int[] KeySample = new int[120];
    /// <summary>음 0~119 → 실제로 낼 음 (IT 음 바꾸기, XM은 그대로).</summary>
    public readonly int[] KeyNote = new int[120];
    public readonly ExtEnvelope VolumeEnv = new ExtEnvelope(), PanEnv = new ExtEnvelope(), PitchEnv = new ExtEnvelope();
    /// <summary>키 오프 뒤 틱마다 줄어드는 몫 (1 = 한 틱에 0까지).</summary>
    public double FadeOut;
    /// <summary>IT: 새 음이 오면 (0 컷, 1 이어서, 2 노트 오프, 3 페이드), 중복 검사(0 없음 1 음 2 샘플 3 악기), 중복 처리(0 컷 1 오프 2 페이드).</summary>
    public int Nna, Dct, Dca;
    /// <summary>IT 악기 전체 볼륨 0~128.</summary>
    public int GlobalVolume = 128;
    /// <summary>IT 기본 팬 0~64 (-1 = 안 씀), 음높이-팬 분리 (−32~32) · 가운데 음.</summary>
    public int DefaultPan = -1, PitchPanSeparation, PitchPanCenter = 60;
    /// <summary>XM 자동 진동 (샘플에 옮겨 쓴다).</summary>
    public int VibType, VibSweep, VibDepth, VibRate;

    public ExtInstrument()
    {
        for (int i = 0; i < 120; i++) KeyNote[i] = i;
    }
}

/// <summary>샘플 (16bit 모노로 통일, 스테레오는 섞음).</summary>
public sealed class ExtSample
{
    public string Name = "", FileName = "";
    public short[] Data = new short[0];
    public bool Loop, PingPong, SustainLoop, SustainPingPong;
    public int LoopStart, LoopEnd, SustainStart, SustainEnd;
    /// <summary>기본 볼륨 0~64, 샘플 전체 볼륨 0~64 (IT), 기본 팬 0~64 (-1 = 안 씀).</summary>
    public int Volume = 64, GlobalVolume = 64, Pan = -1;
    /// <summary>C-5(음 60)에서의 재생 주파수 (Hz).</summary>
    public double C5Speed = 8363 * 2;
    /// <summary>자동 진동: 모양(0 사인 1 램프 2 사각 3 무작위), 스윕(XM) / 속도 비율(IT), 깊이, 속도.</summary>
    public int VibType, VibSweep, VibDepth, VibRate;
    public bool Is16Bit, IsStereo;

    public int Length { get { return Data.Length; } }
}

// ==================================================================== XM

internal static class XmReader
{
    public static ExtModule Read(byte[] d)
    {
        if (d.Length < 80) throw new InvalidDataException("XM 머리말이 잘렸습니다.");
        var m = new ExtModule { IsIt = false };
        m.Title = ExtModule.Text(d, 17, 20);
        m.Tracker = ExtModule.Text(d, 38, 20);
        int version = ExtModule.U16(d, 58);
        int headerSize = ExtModule.U32(d, 60);
        int songLen = ExtModule.U16(d, 64);
        m.RestartOrder = ExtModule.U16(d, 66);
        m.Channels = Math.Max(1, Math.Min(ExtModule.MaxChannels, ExtModule.U16(d, 68)));
        int nPat = ExtModule.U16(d, 70), nIns = ExtModule.U16(d, 72), flags = ExtModule.U16(d, 74);
        m.LinearSlides = (flags & 1) != 0;
        m.Speed = Math.Max(1, ExtModule.U16(d, 76));
        m.Tempo = Math.Max(32, ExtModule.U16(d, 78));
        m.GlobalVolume = 128;
        m.MixVolume = 48;
        if (version < 0x0104) throw new InvalidDataException("XM 1.0" + (version & 0xFF).ToString("x2") + " (옛 판)은 아직 읽지 못합니다.");
        songLen = Math.Min(256, songLen);
        for (int i = 0; i < songLen; i++) m.Orders.Add(d[80 + i]);
        for (int c = 0; c < ExtModule.MaxChannels; c++) { m.ChannelPan[c] = 32; m.ChannelVolume[c] = 64; }

        int p = 60 + headerSize;
        for (int pi = 0; pi < nPat; pi++)
        {
            if (p + 9 > d.Length) { m.Patterns.Add(new ExtPattern(64, m.Channels)); continue; }
            int hlen = ExtModule.U32(d, p);
            int rows = ExtModule.U16(d, p + 5);
            int packed = ExtModule.U16(d, p + 7);
            if (rows <= 0 || rows > 256) rows = 64;
            var pat = new ExtPattern(rows, m.Channels);
            int q = p + hlen, end = Math.Min(d.Length, q + packed);
            if (packed > 0)
            {
                for (int r = 0; r < rows && q < end; r++)
                    for (int c = 0; c < m.Channels && q < end; c++)
                    {
                        byte b = d[q++];
                        int note = 0, ins = 0, vol = 0, fx = 0, prm = 0;
                        if ((b & 0x80) != 0)
                        {
                            if ((b & 1) != 0 && q < end) note = d[q++];
                            if ((b & 2) != 0 && q < end) ins = d[q++];
                            if ((b & 4) != 0 && q < end) vol = d[q++];
                            if ((b & 8) != 0 && q < end) fx = d[q++];
                            if ((b & 16) != 0 && q < end) prm = d[q++];
                        }
                        else
                        {
                            note = b;
                            if (q + 4 <= end) { ins = d[q]; vol = d[q + 1]; fx = d[q + 2]; prm = d[q + 3]; }
                            q += 4;
                        }
                        pat[r, c] = Cell(note, ins, vol, fx, prm);
                    }
            }
            m.Patterns.Add(pat);
            p += hlen + packed;
        }

        for (int ii = 0; ii < nIns; ii++)
        {
            var ins = new ExtInstrument();
            m.Instruments.Add(ins);
            if (p + 29 > d.Length) break;
            int isize = ExtModule.U32(d, p);
            ins.Name = ExtModule.Text(d, p + 4, 22);
            int nSmp = ExtModule.U16(d, p + 27);
            if (isize < 29) isize = 29;
            if (nSmp == 0 || nSmp > 255) { p += isize; continue; }
            int shSize = ExtModule.U32(d, p + 29);
            if (shSize <= 0 || shSize > 4096) shSize = 40;
            int first = m.Samples.Count;
            if (p + 241 <= d.Length)
            {
                // 키맵 n (0~95) = XM 음 n+1 = 음 번호 n
                for (int n = 0; n < 120; n++) ins.KeySample[n] = first + Math.Min(nSmp - 1, (int)d[p + 33 + Math.Min(95, n)]) + 1;
                ReadXmEnvelope(d, p + 129, d[p + 225], d[p + 227], d[p + 228], d[p + 229], d[p + 233], ins.VolumeEnv, false);
                ReadXmEnvelope(d, p + 177, d[p + 226], d[p + 230], d[p + 231], d[p + 232], d[p + 234], ins.PanEnv, true);
                ins.VibType = d[p + 235]; ins.VibSweep = d[p + 236]; ins.VibDepth = d[p + 237]; ins.VibRate = d[p + 238];
                ins.FadeOut = ExtModule.U16(d, p + 239) / 32768.0;
            }
            p += isize;

            var lens = new int[nSmp];
            for (int s = 0; s < nSmp; s++)
            {
                var smp = new ExtSample();
                m.Samples.Add(smp);
                int o = p + s * shSize;
                if (o + 40 > d.Length) continue;
                lens[s] = ExtModule.U32(d, o);
                int ls = ExtModule.U32(d, o + 4), ll = ExtModule.U32(d, o + 8);
                smp.Volume = Math.Min(64, (int)d[o + 12]);
                int fine = (sbyte)d[o + 13];
                int type = d[o + 14];
                smp.Pan = d[o + 15] * 64 / 255;
                int rel = (sbyte)d[o + 16];
                smp.Name = ExtModule.Text(d, o + 18, 22);
                smp.Is16Bit = (type & 16) != 0;
                int loop = type & 3;
                smp.Loop = loop != 0 && ll > 0;
                smp.PingPong = loop == 2;
                int div = smp.Is16Bit ? 2 : 1;
                smp.LoopStart = ls / div; smp.LoopEnd = (ls + ll) / div;
                // C-4 (음 48) = 8363Hz × 2^((rel + fine/128)/12) → C-5는 2배
                smp.C5Speed = 8363.0 * 2 * Math.Pow(2, (rel + fine / 128.0) / 12.0);
                smp.VibType = ins.VibType; smp.VibSweep = ins.VibSweep; smp.VibDepth = ins.VibDepth; smp.VibRate = ins.VibRate;
                if (d[o + 17] == 0xAD) lens[s] = -lens[s];   // ModPlug ADPCM (4비트): 아래에서 따로
            }
            p += nSmp * shSize;
            for (int s = 0; s < nSmp; s++)
            {
                var smp = m.Samples[first + s];
                int len = lens[s];
                if (len < 0)
                {
                    len = -len;
                    int bytes = 16 + (len + 1) / 2;
                    smp.Data = DecodeAdpcm(d, p, len);
                    p += bytes;
                }
                else
                {
                    int avail = Math.Max(0, Math.Min(len, d.Length - p));
                    smp.Data = smp.Is16Bit ? Delta16(d, p, avail / 2) : Delta8(d, p, avail);
                    p += len;
                }
                FixLoop(smp);
            }
        }
        // 인스트루먼트가 없는 칸: 컷
        return m;
    }

    private static void ReadXmEnvelope(byte[] d, int off, int count, int sus, int ls, int le, int type, ExtEnvelope env, bool pan)
    {
        count = Math.Min(12, count);
        for (int i = 0; i < count; i++)
        {
            int t = ExtModule.U16(d, off + i * 4), v = ExtModule.U16(d, off + i * 4 + 2);
            if (i > 0 && t <= env.Ticks[i - 1]) t = env.Ticks[i - 1] + 1;
            env.Ticks.Add(t);
            env.Values.Add(pan ? Math.Max(-32, Math.Min(32, v - 32)) : Math.Min(64, v));
        }
        env.Enabled = (type & 1) != 0 && count > 0;
        env.Sustain = (type & 2) != 0;
        env.Loop = (type & 4) != 0;
        env.SustainStart = env.SustainEnd = Math.Min(Math.Max(0, count - 1), sus);
        env.LoopStart = Math.Min(Math.Max(0, count - 1), ls);
        env.LoopEnd = Math.Min(Math.Max(0, count - 1), le);
    }

    private static ExtCell Cell(int note, int ins, int vol, int fx, int prm)
    {
        var c = new ExtCell { Instrument = (byte)ins };
        if (note == 97) c.Note = ExtModule.NoteOff;
        else if (note >= 1 && note <= 96) c.Note = (byte)note;   // 1 = C-0 → 음 번호 0
        if (vol >= 0x10 && vol <= 0x50) { c.VolCmd = ExtVolCmd.Volume; c.VolValue = (byte)(vol - 0x10); }
        else if (vol >= 0x60)
        {
            int v = vol & 15;
            switch (vol >> 4)
            {
                case 6: c.VolCmd = ExtVolCmd.SlideDown; break;
                case 7: c.VolCmd = ExtVolCmd.SlideUp; break;
                case 8: c.VolCmd = ExtVolCmd.FineDown; break;
                case 9: c.VolCmd = ExtVolCmd.FineUp; break;
                case 0xA: c.VolCmd = ExtVolCmd.VibratoSpeed; break;
                case 0xB: c.VolCmd = ExtVolCmd.VibratoDepth; break;
                case 0xC: c.VolCmd = ExtVolCmd.Panning; v = v * 64 / 15; break;
                case 0xD: c.VolCmd = ExtVolCmd.PanSlideLeft; break;
                case 0xE: c.VolCmd = ExtVolCmd.PanSlideRight; break;
                case 0xF: c.VolCmd = ExtVolCmd.TonePorta; break;
            }
            c.VolValue = (byte)v;
        }
        if (fx != 0 || prm != 0) { c.Effect = (byte)(fx + 1); c.Param = (byte)prm; }
        return c;
    }

    private static short[] Delta8(byte[] d, int p, int n)
    {
        var r = new short[n];
        sbyte acc = 0;
        for (int i = 0; i < n; i++) { acc = (sbyte)(acc + (sbyte)d[p + i]); r[i] = (short)(acc << 8); }
        return r;
    }

    private static short[] Delta16(byte[] d, int p, int n)
    {
        var r = new short[n];
        short acc = 0;
        for (int i = 0; i < n; i++) { acc = (short)(acc + (short)(d[p + i * 2] | (d[p + i * 2 + 1] << 8))); r[i] = acc; }
        return r;
    }

    /// <summary>ModPlug ADPCM: 16바이트 표 + 4비트 차이 (낮은 니블 먼저).</summary>
    private static short[] DecodeAdpcm(byte[] d, int p, int len)
    {
        var r = new short[len];
        if (p + 16 > d.Length) return r;
        sbyte acc = 0;
        for (int i = 0; i < len; i++)
        {
            int o = p + 16 + i / 2;
            if (o >= d.Length) break;
            int nib = (i & 1) == 0 ? d[o] & 15 : d[o] >> 4;
            acc = (sbyte)(acc + (sbyte)d[p + nib]);
            r[i] = (short)(acc << 8);
        }
        return r;
    }

    internal static void FixLoop(ExtSample s)
    {
        int n = s.Data.Length;
        if (s.LoopEnd > n) s.LoopEnd = n;
        if (s.LoopStart < 0 || s.LoopStart >= s.LoopEnd) s.Loop = false;
        if (s.SustainEnd > n) s.SustainEnd = n;
        if (s.SustainStart < 0 || s.SustainStart >= s.SustainEnd) s.SustainLoop = false;
    }
}

// ==================================================================== IT

internal static class ItReader
{
    public static ExtModule Read(byte[] d)
    {
        if (d.Length < 192) throw new InvalidDataException("IT 머리말이 잘렸습니다.");
        var m = new ExtModule { IsIt = true };
        m.Title = ExtModule.Text(d, 4, 26);
        int ordNum = ExtModule.U16(d, 32), insNum = ExtModule.U16(d, 34), smpNum = ExtModule.U16(d, 36), patNum = ExtModule.U16(d, 38);
        int cwt = ExtModule.U16(d, 40), cmwt = ExtModule.U16(d, 42), flags = ExtModule.U16(d, 44), special = ExtModule.U16(d, 46);
        m.Tracker = (cwt >> 12) == 1 ? "Impulse Tracker " + ((cwt >> 8) & 15) + "." + (cwt & 0xFF).ToString("x2") : (cwt >> 12) == 5 ? "OpenMPT" : (cwt >> 12) == 7 ? "Schism Tracker" : "IT 0x" + cwt.ToString("x4");
        m.Stereo = (flags & 1) != 0;
        m.UseInstruments = (flags & 4) != 0;
        m.LinearSlides = (flags & 8) != 0;
        m.OldEffects = (flags & 16) != 0;
        m.CompatGxx = (flags & 32) != 0;
        m.GlobalVolume = Math.Min(128, (int)d[48]);
        m.MixVolume = Math.Min(128, (int)d[49]);
        m.Speed = Math.Max(1, (int)d[50]);
        m.Tempo = Math.Max(32, (int)d[51]);
        for (int c = 0; c < 64; c++)
        {
            int pan = d[64 + c];
            m.ChannelMuted[c] = pan >= 128;
            pan &= 127;
            m.ChannelSurround[c] = pan == 100;
            m.ChannelPan[c] = pan == 100 ? 32 : Math.Min(64, pan);
            m.ChannelVolume[c] = Math.Min(64, (int)d[128 + c]);
        }
        int at = 192;
        for (int i = 0; i < ordNum && at + i < d.Length; i++) m.Orders.Add(d[at + i]);
        at += ordNum;
        var insOff = new int[insNum]; var smpOff = new int[smpNum]; var patOff = new int[patNum];
        for (int i = 0; i < insNum; i++) insOff[i] = ExtModule.U32(d, at + i * 4);
        at += insNum * 4;
        for (int i = 0; i < smpNum; i++) smpOff[i] = ExtModule.U32(d, at + i * 4);
        at += smpNum * 4;
        for (int i = 0; i < patNum; i++) patOff[i] = ExtModule.U32(d, at + i * 4);
        if ((special & 1) != 0)
        {
            int len = ExtModule.U16(d, 54), off = ExtModule.U32(d, 56);
            if (off > 0 && off < d.Length)
            {
                len = Math.Min(len, d.Length - off);
                var sb = new StringBuilder();
                for (int i = 0; i < len; i++) { byte b = d[off + i]; if (b == 0) break; sb.Append(b == 13 ? '\n' : b >= 32 && b < 127 ? (char)b : ' '); }
                m.Message = sb.ToString().TrimEnd();
            }
        }

        for (int i = 0; i < insNum; i++) m.Instruments.Add(ReadInstrument(d, insOff[i], cmwt));
        for (int i = 0; i < smpNum; i++) m.Samples.Add(ReadSample(d, smpOff[i], cwt));
        int maxCh = 1;
        for (int i = 0; i < patNum; i++) m.Patterns.Add(ReadPattern(d, patOff[i], ref maxCh));
        m.Channels = maxCh;
        // 패턴은 64채널로 읽었다: 쓰는 채널 수로 줄여 둔다
        for (int i = 0; i < m.Patterns.Count; i++)
        {
            var p = m.Patterns[i];
            var q = new ExtPattern(p.Rows, maxCh);
            for (int r = 0; r < p.Rows; r++) for (int c = 0; c < maxCh; c++) q[r, c] = p[r, c];
            m.Patterns[i] = q;
        }
        return m;
    }

    private static ExtInstrument ReadInstrument(byte[] d, int o, int cmwt)
    {
        var ins = new ExtInstrument();
        if (o <= 0 || o + 554 > d.Length) return ins;
        ins.FileName = ExtModule.Text(d, o + 4, 12);
        ins.Name = ExtModule.Text(d, o + 32, 26);
        for (int n = 0; n < 120; n++)
        {
            int note = d[o + 64 + n * 2], smp = d[o + 65 + n * 2];
            ins.KeyNote[n] = note < 120 ? note : n;
            ins.KeySample[n] = smp;
        }
        if (cmwt < 0x200)
        {
            // 옛 판 (IT 1.x)
            int flg = d[o + 17];
            ins.FadeOut = ExtModule.U16(d, o + 24) * 2 / 1024.0;
            ins.Nna = d[o + 26]; ins.Dct = d[o + 27] != 0 ? 1 : 0;
            var env = ins.VolumeEnv;
            env.Enabled = (flg & 1) != 0; env.Loop = (flg & 2) != 0; env.Sustain = (flg & 4) != 0;
            env.LoopStart = d[o + 18]; env.LoopEnd = d[o + 19]; env.SustainStart = d[o + 20]; env.SustainEnd = d[o + 21];
            for (int k = 0; k < 25; k++)
            {
                int t = d[o + 0x1F8 + k * 2], v = d[o + 0x1F9 + k * 2];
                if (t == 0xFF) break;
                env.Ticks.Add(t); env.Values.Add(Math.Min(64, v));
            }
            ClampEnv(env);
            return ins;
        }
        ins.Nna = d[o + 17] & 3; ins.Dct = d[o + 18] & 3; ins.Dca = d[o + 19] % 3;
        ins.FadeOut = Math.Min(256, ExtModule.U16(d, o + 20)) / 1024.0;
        ins.PitchPanSeparation = (sbyte)d[o + 22];
        ins.PitchPanCenter = Math.Min(119, (int)d[o + 23]);
        ins.GlobalVolume = Math.Min(128, (int)d[o + 24]);
        ins.DefaultPan = (d[o + 25] & 128) != 0 ? -1 : Math.Min(64, d[o + 25] & 127);
        ReadItEnvelope(d, o + 304, ins.VolumeEnv, 0);
        ReadItEnvelope(d, o + 386, ins.PanEnv, 1);
        ReadItEnvelope(d, o + 468, ins.PitchEnv, 2);
        return ins;
    }

    private static void ReadItEnvelope(byte[] d, int o, ExtEnvelope env, int kind)
    {
        int flg = d[o], num = Math.Min(25, (int)d[o + 1]);
        env.Enabled = (flg & 1) != 0;
        env.Loop = (flg & 2) != 0;
        env.Sustain = (flg & 4) != 0;
        env.Filter = kind == 2 && (flg & 128) != 0;
        env.LoopStart = d[o + 2]; env.LoopEnd = d[o + 3]; env.SustainStart = d[o + 4]; env.SustainEnd = d[o + 5];
        for (int k = 0; k < num; k++)
        {
            int v = (sbyte)d[o + 6 + k * 3], t = ExtModule.U16(d, o + 7 + k * 3);
            if (k > 0 && t < env.Ticks[k - 1]) t = env.Ticks[k - 1];
            env.Ticks.Add(t);
            env.Values.Add(kind == 0 ? Math.Max(0, Math.Min(64, v)) : Math.Max(-32, Math.Min(32, v)));
        }
        ClampEnv(env);
    }

    private static void ClampEnv(ExtEnvelope env)
    {
        int last = Math.Max(0, env.Ticks.Count - 1);
        env.LoopStart = Math.Min(env.LoopStart, last); env.LoopEnd = Math.Max(env.LoopStart, Math.Min(env.LoopEnd, last));
        env.SustainStart = Math.Min(env.SustainStart, last); env.SustainEnd = Math.Max(env.SustainStart, Math.Min(env.SustainEnd, last));
        if (env.Ticks.Count == 0) env.Enabled = false;
    }

    private static ExtSample ReadSample(byte[] d, int o, int cwt)
    {
        var s = new ExtSample();
        if (o <= 0 || o + 80 > d.Length) return s;
        s.FileName = ExtModule.Text(d, o + 4, 12);
        s.GlobalVolume = Math.Min(64, (int)d[o + 17]);
        int flg = d[o + 18];
        s.Volume = Math.Min(64, (int)d[o + 19]);
        s.Name = ExtModule.Text(d, o + 20, 26);
        int cvt = d[o + 46];
        int dfp = d[o + 47];
        s.Pan = (dfp & 128) != 0 ? Math.Min(64, dfp & 127) : -1;
        int len = ExtModule.U32(d, o + 48);
        s.LoopStart = ExtModule.U32(d, o + 52); s.LoopEnd = ExtModule.U32(d, o + 56);
        s.C5Speed = Math.Max(1, ExtModule.U32(d, o + 60));
        s.SustainStart = ExtModule.U32(d, o + 64); s.SustainEnd = ExtModule.U32(d, o + 68);
        int ptr = ExtModule.U32(d, o + 72);
        s.VibRate = d[o + 76]; s.VibDepth = d[o + 77]; s.VibSweep = d[o + 78]; s.VibType = d[o + 79] & 3;   // IT: 속도, 깊이, 비율(스윕), 모양
        s.Is16Bit = (flg & 2) != 0;
        s.IsStereo = (flg & 4) != 0;
        bool compressed = (flg & 8) != 0;
        s.Loop = (flg & 16) != 0;
        s.SustainLoop = (flg & 32) != 0;
        s.PingPong = (flg & 64) != 0;
        s.SustainPingPong = (flg & 128) != 0;
        if ((flg & 1) == 0 || len <= 0 || ptr <= 0 || ptr >= d.Length) { s.Data = new short[0]; return s; }
        len = Math.Min(len, 16 * 1024 * 1024);
        bool signed = (cvt & 1) != 0;
        int channels = s.IsStereo ? 2 : 1;
        var parts = new short[channels][];
        int p = ptr;
        for (int ch = 0; ch < channels; ch++)
        {
            if (compressed)
            {
                bool it215 = (cvt & 4) != 0;
                parts[ch] = s.Is16Bit ? ItCompression.Decompress16(d, ref p, len, it215) : ItCompression.Decompress8(d, ref p, len, it215);
            }
            else
            {
                var r = new short[len];
                int bps = s.Is16Bit ? 2 : 1;
                for (int i = 0; i < len; i++)
                {
                    int q = p + i * bps;
                    if (q + bps > d.Length) break;
                    if (s.Is16Bit)
                    {
                        int v = d[q] | (d[q + 1] << 8);
                        r[i] = signed ? (short)v : (short)(v - 32768);
                    }
                    else r[i] = (short)((signed ? (sbyte)d[q] : d[q] - 128) << 8);
                }
                p += len * bps;
                parts[ch] = r;
            }
        }
        if (channels == 2)
        {
            var mix = new short[len];
            for (int i = 0; i < len; i++) mix[i] = (short)((parts[0][i] + parts[1][i]) >> 1);
            s.Data = mix;
        }
        else s.Data = parts[0];
        XmReader.FixLoop(s);
        return s;
    }

    private static ExtPattern ReadPattern(byte[] d, int o, ref int maxCh)
    {
        if (o <= 0 || o + 8 > d.Length) return new ExtPattern(64, 64);
        int len = ExtModule.U16(d, o), rows = ExtModule.U16(d, o + 2);
        if (rows <= 0 || rows > 256) rows = 64;
        var pat = new ExtPattern(rows, 64);
        int p = o + 8, end = Math.Min(d.Length, p + len);
        var mask = new byte[64];
        var last = new ExtCell[64];
        var lastVol = new byte[64];
        int row = 0;
        while (row < rows && p < end)
        {
            int cv = d[p++];
            if (cv == 0) { row++; continue; }
            int c = (cv - 1) & 63;
            if ((cv & 128) != 0) { if (p >= end) break; mask[c] = d[p++]; }
            int mk = mask[c];
            var cell = new ExtCell();
            bool hasVol = false;
            int vol = 0;
            if ((mk & 1) != 0 && p < end) { int n = d[p++]; last[c].Note = NoteOf(n); cell.Note = last[c].Note; }
            if ((mk & 2) != 0 && p < end) { last[c].Instrument = d[p++]; cell.Instrument = last[c].Instrument; }
            if ((mk & 4) != 0 && p < end) { lastVol[c] = d[p++]; vol = lastVol[c]; hasVol = true; }
            if ((mk & 8) != 0 && p + 1 < end) { last[c].Effect = d[p++]; last[c].Param = d[p++]; cell.Effect = last[c].Effect; cell.Param = last[c].Param; }
            if ((mk & 16) != 0) cell.Note = last[c].Note;
            if ((mk & 32) != 0) cell.Instrument = last[c].Instrument;
            if ((mk & 64) != 0) { vol = lastVol[c]; hasVol = true; }
            if ((mk & 128) != 0) { cell.Effect = last[c].Effect; cell.Param = last[c].Param; }
            if (hasVol) SetVolume(ref cell, vol);
            if (cell.Effect > 26) { cell.Effect = 0; cell.Param = 0; }
            pat[row, c] = cell;
            if (c + 1 > maxCh) maxCh = c + 1;
        }
        return pat;
    }

    private static byte NoteOf(int n)
    {
        if (n < 120) return (byte)(n + 1);
        if (n == 255) return ExtModule.NoteOff;
        if (n == 254) return ExtModule.NoteCut;
        return ExtModule.NoteFade;
    }

    private static void SetVolume(ref ExtCell c, int v)
    {
        if (v <= 64) { c.VolCmd = ExtVolCmd.Volume; c.VolValue = (byte)v; }
        else if (v <= 74) { c.VolCmd = ExtVolCmd.FineUp; c.VolValue = (byte)(v - 65); }
        else if (v <= 84) { c.VolCmd = ExtVolCmd.FineDown; c.VolValue = (byte)(v - 75); }
        else if (v <= 94) { c.VolCmd = ExtVolCmd.SlideUp; c.VolValue = (byte)(v - 85); }
        else if (v <= 104) { c.VolCmd = ExtVolCmd.SlideDown; c.VolValue = (byte)(v - 95); }
        else if (v <= 114) { c.VolCmd = ExtVolCmd.PortaDown; c.VolValue = (byte)(v - 105); }
        else if (v <= 124) { c.VolCmd = ExtVolCmd.PortaUp; c.VolValue = (byte)(v - 115); }
        else if (v >= 128 && v <= 192) { c.VolCmd = ExtVolCmd.Panning; c.VolValue = (byte)(v - 128); }
        else if (v >= 193 && v <= 202) { c.VolCmd = ExtVolCmd.TonePorta; c.VolValue = (byte)(v - 193); }
        else if (v >= 203 && v <= 212) { c.VolCmd = ExtVolCmd.VibratoDepth; c.VolValue = (byte)(v - 203); }
    }
}

/// <summary>IT 2.14 / 2.15 샘플 압축 풀기 (블록마다 16비트 길이 + 비트 열, 8bit는 0x8000 · 16bit는 0x4000 샘플씩).</summary>
internal static class ItCompression
{
    private sealed class Bits
    {
        private readonly byte[] d; private int pos; private readonly int end;
        private uint buf; private int left;
        public Bits(byte[] d, int pos, int end) { this.d = d; this.pos = pos; this.end = end; }
        public uint Read(int n)
        {
            uint ret = 0;
            for (int i = 0; i < n; i++)
            {
                if (left == 0) { buf = pos < end ? d[pos] : 0u; pos++; left = 8; }
                ret >>= 1;
                ret |= (buf & 1) << 31;
                buf >>= 1;
                left--;
            }
            return n == 0 ? 0 : ret >> (32 - n);
        }
    }

    public static short[] Decompress8(byte[] d, ref int p, int len, bool it215)
    {
        var dst = new short[len];
        int at = 0;
        while (at < len && p + 2 <= d.Length)
        {
            int blockBytes = ExtModule.U16(d, p);
            p += 2;
            var bits = new Bits(d, p, Math.Min(d.Length, p + blockBytes));
            p += blockBytes;
            int count = Math.Min(0x8000, len - at);
            int width = 9;
            byte temp = 0, temp2 = 0;
            int pos = 0;
            while (pos < count)
            {
                uint v = bits.Read(width);
                if (width < 7)
                {
                    if (v == 1u << (width - 1))
                    {
                        int nw = (int)bits.Read(3) + 1;
                        width = nw < width ? nw : nw + 1;
                        continue;
                    }
                }
                else if (width < 9)
                {
                    uint hi = (0xFFu >> (9 - width)) + 4, lo = hi - 8;
                    if (v > lo && v <= hi)
                    {
                        int nw = (int)(v - lo);
                        width = nw < width ? nw : nw + 1;
                        continue;
                    }
                }
                else if (width >= 10) { pos++; continue; }   // 깨진 자료
                else if (v >= 256) { width = (int)(v + 1) & 0xFF; continue; }
                int val = (int)v;
                if (width < 8)
                {
                    int shift = 8 - width;
                    val = ((sbyte)(byte)(v << shift)) >> shift;
                }
                temp = (byte)(temp + val);
                temp2 = (byte)(temp2 + temp);
                dst[at + pos] = (short)((sbyte)(it215 ? temp2 : temp) << 8);
                pos++;
            }
            at += count;
        }
        return dst;
    }

    public static short[] Decompress16(byte[] d, ref int p, int len, bool it215)
    {
        var dst = new short[len];
        int at = 0;
        while (at < len && p + 2 <= d.Length)
        {
            int blockBytes = ExtModule.U16(d, p);
            p += 2;
            var bits = new Bits(d, p, Math.Min(d.Length, p + blockBytes));
            p += blockBytes;
            int count = Math.Min(0x4000, len - at);
            int width = 17;
            short temp = 0, temp2 = 0;
            int pos = 0;
            while (pos < count)
            {
                uint v = bits.Read(width);
                if (width < 7)
                {
                    if (v == 1u << (width - 1))
                    {
                        int nw = (int)bits.Read(4) + 1;
                        width = nw < width ? nw : nw + 1;
                        continue;
                    }
                }
                else if (width < 17)
                {
                    uint hi = (0xFFFFu >> (17 - width)) + 8, lo = (hi - 16) & 0xFFFF;
                    if (v > lo && v <= (hi & 0xFFFF))
                    {
                        int nw = (int)(v - lo);
                        width = nw < width ? nw : nw + 1;
                        continue;
                    }
                }
                else if (width >= 18) { pos++; continue; }
                else if (v >= 0x10000) { width = (int)(v + 1) & 0xFF; continue; }
                int val;
                if (width < 16)
                {
                    int shift = 16 - width;
                    val = ((short)(ushort)(v << shift)) >> shift;
                }
                else val = (short)(ushort)v;
                temp = (short)(temp + val);
                temp2 = (short)(temp2 + temp);
                dst[at + pos] = it215 ? temp2 : temp;
                pos++;
            }
            at += count;
        }
        return dst;
    }
}
