using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// MDX (X68000 MXDRV 곡) + PDX (ADPCM 샘플): MXDRV처럼 트랙을 돌려 YM2151(OPM, 4MHz) · MSM6258(ADPCM) 쓰기를 VGM(<see cref="VgmSong"/>)으로 바꾼다.
/// <para>파일: 제목 (Shift_JIS, 0x0D 0x0A 0x1A로 끝) → PDX 이름 (0으로 끝) → 여기가 기준: 음색 위치(2바이트 BE) · 트랙 위치들 (FM 8 + ADPCM 1, PCM8이면 16).
/// 음색 27바이트: 번호 · FL/CON · 슬롯 마스크 · DT1/MUL · TL · KS/AR · AME/D1R · DT2/D2R · D1L/RR (연산자 넷씩, 레지스터 순서 M1 M2 C1 C2).</para>
/// <para>명령: 0x00~0x7F 쉼 (n+1 클럭), 0x80~0xDF 음 (다음 바이트 + 1 = 길이, 0x80 = KC 0), 0xFF 템포 (타이머 B), 0xFE OPM 쓰기, 0xFD 음색, 0xFC 팬, 0xFB 음량 (0~15 / 0x80+ = TL),
/// 0xFA · 0xF9 음량 내림 · 올림, 0xF8 q (1~8 / 0x80+ = 끝에서 몇 클럭 앞), 0xF7 다음 음 키오프 없음 (이음), 0xF6 · 0xF5 · 0xF4 되풀이 시작 · 끝 · 빠져나감,
/// 0xF3 디튠 (1/64 반음), 0xF2 포르타멘토 (1/16384 반음 / 클럭, 다음 음에), 0xF1 끝 · 되돌아감, 0xF0 키온 늦춤, 0xEF · 0xEE 동기 보냄 · 기다림,
/// 0xED ADPCM 속도 (0~4 = 3.9 · 5.2 · 7.8 · 10.4 · 15.6kHz) · 노이즈 (FM 8), 0xEC 음높이 LFO, 0xEB 음량 LFO, 0xEA OPM LFO, 0xE9 LFO 늦춤, 0xE8 PCM8.
/// 점프 거리는 명령(3바이트) 끝에서 잰다.</para>
/// <para>소프트웨어 LFO 모양(톱니 · 사각 · 세모)은 MXDRV를 대략 따른다 (음높이 1/16384 반음 · 음량 1/256 TL 단위, 세모 = 주기의 4배가 한 바퀴).
/// ADPCM: 음 번호 = PDX 샘플 (뱅크 = 음색 번호 × 96, 없으면 0), 음량 없음, 끝까지 울림. PCM8(여러 ADPCM 트랙)은 MSM6258 하나로 마지막 음만.</para>
/// <para>길이: 모든 트랙이 끝나거나 두 번 되돌아갈 때까지 (최대 10분), 되돌아가는 곡은 마지막 4초를 줄인다.</para>
/// </summary>
public static class Mdx
{
    public static readonly string[] Extensions = { ".mdx" };
    public const double OpmClock = 4000000, MaxSeconds = 600, FadeSeconds = 4;
    private static readonly int[] VolTable = { 0x2A, 0x28, 0x25, 0x22, 0x20, 0x1D, 0x1A, 0x18, 0x15, 0x12, 0x10, 0x0D, 0x0A, 0x08, 0x05, 0x02 };
    private static readonly int[] NoteCode = { 0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14 };
    /// <summary>ADPCM 속도 0~4: (MSM6258 클럭, 분주 번호).</summary>
    private static readonly (int clock, int div)[] AdpcmRates = { (4000000, 0), (4000000, 1), (4000000, 2), (8000000, 1), (8000000, 2) };
    private static readonly int[] Dividers = { 1024, 768, 512, 512 };

    public sealed class Header
    {
        public string Title = "", PdxName = "";
        public int Base, VoiceOffset;
        public int[] Tracks;
        public readonly Dictionary<int, byte[]> Voices = new Dictionary<int, byte[]>();
        public byte[] Data;
    }

    public static Header ReadHeader(byte[] d)
    {
        int t = -1;
        for (int i = 0; i + 2 < d.Length && i < 1024; i++) if (d[i] == 0x0D && d[i + 1] == 0x0A && d[i + 2] == 0x1A) { t = i; break; }
        if (t < 0) throw new InvalidDataException("MDX 파일이 아닙니다 (제목 끝 0x0D 0x0A 0x1A가 없음).");
        int z = Array.IndexOf(d, (byte)0, t + 3);
        if (z < 0 || z + 4 >= d.Length) throw new InvalidDataException("MDX: PDX 이름 끝이 없습니다.");
        var h = new Header { Data = d, Base = z + 1 };
        h.Title = KoreanText.ShiftJis.GetString(d, 0, t).Trim();
        h.PdxName = KoreanText.ShiftJis.GetString(d, t + 3, z - t - 3).Trim();
        h.VoiceOffset = U16(d, h.Base);
        int first = U16(d, h.Base + 2);
        int n = Math.Max(1, Math.Min(16, (first - 2) / 2));
        h.Tracks = new int[n];
        for (int i = 0; i < n; i++) h.Tracks[i] = h.Base + U16(d, h.Base + 2 + 2 * i);
        for (int p = h.Base + h.VoiceOffset; p + 27 <= d.Length; p += 27)
        {
            var v = new byte[27];
            Array.Copy(d, p, v, 0, 27);
            h.Voices[v[0]] = v;
        }
        return h;
    }

    /// <summary>MDX 옆의 PDX (이름 그대로 · .PDX 붙여서, 대소문자 무시). 없으면 null.</summary>
    public static string FindPdx(string mdxPath, string pdxName)
    {
        if (string.IsNullOrWhiteSpace(pdxName)) return null;
        string dir = Path.GetDirectoryName(Path.GetFullPath(mdxPath)) ?? ".";
        var want = new List<string> { pdxName };
        if (!pdxName.EndsWith(".pdx", StringComparison.OrdinalIgnoreCase)) want.Add(pdxName + ".PDX");
        foreach (var f in Directory.GetFiles(dir))
            foreach (var w in want)
                if (string.Equals(Path.GetFileName(f), Path.GetFileName(w), StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    public static VgmSong Load(string path)
    {
        var d = File.ReadAllBytes(path);
        var h = ReadHeader(d);
        string pdx = FindPdx(path, h.PdxName);
        var v = Render(d, pdx != null ? File.ReadAllBytes(pdx) : null);
        v.FilePath = path;
        if (v.TitleJp.Length == 0 && v.TitleEn.Length == 0) v.TitleEn = Path.GetFileNameWithoutExtension(path);
        v.SourceFormat = "MDX" + (h.PdxName.Length > 0 ? " + " + (pdx != null ? Path.GetFileName(pdx) : h.PdxName + " (없음)") : "") + " · 트랙 " + h.Tracks.Length;
        if (h.PdxName.Length > 0 && pdx == null) v.UnsupportedChips.Add("PDX 없음: " + h.PdxName);
        return v;
    }

    // ------------------------------------------------------------ 돌리기

    private sealed class Lfo
    {
        public int Wave, Period, Change;
        public bool On;
        public int T;
        public int Value()
        {
            int p = Math.Max(1, Period);
            switch (Wave & 3)
            {
                case 0: return Change * (T % p);                                       // 톱니: 0 → 바뀜량 × 주기, 되풀이
                case 1: return (T / p) % 2 == 0 ? 0 : Change;                          // 사각: 0 · 바뀜량 번갈아
                default:
                    {
                        int t = T % (4 * p);                                            // 세모: 0 → +A → −A → 0
                        return t < p ? Change * t : t < 3 * p ? Change * (2 * p - t) : Change * (t - 4 * p);
                    }
            }
        }
    }

    private sealed class Track
    {
        public int Index, Ch, P, Start;
        public bool Adpcm, Ended, Waiting;
        public int Wait, Gate = -1, KeyDelay, KeyDelayLeft = -1;
        public int Voice = -1, Vol = 8, Pan = 3, Q = 8, Detune, Note = -1, AdpcmRate = 4;
        public bool VolDirect, TieNext, Tied, Keyed;
        public int Porta, PortaAcc, PortaNext;
        public int Loops;
        public bool SyncPending;
        public int LfoDelay, LfoTick;
        public readonly Lfo PitchLfo = new Lfo(), AmpLfo = new Lfo();
        public int OpmPmsAms; public bool OpmLfoSync;
        public int LastKc = -1, LastKf = -1, LastAmp = int.MinValue;
        public readonly Dictionary<int, int> Repeat = new Dictionary<int, int>();
    }

    private sealed class Writer
    {
        public readonly List<(double t, byte[] cmd)> Log = new List<(double, byte[])>();
        public double Time;
        public bool UsedAdpcm, UsedOpm;
        public void Opm(int r, int v) { Log.Add((Time, new byte[] { 0x54, (byte)r, (byte)v })); UsedOpm = true; }
        public void Msm(int r, int v) { Log.Add((Time, new byte[] { 0xB7, (byte)r, (byte)v })); }
        public void Raw(params byte[] c) { Log.Add((Time, c)); }
    }

    /// <summary>MDX (+ PDX)를 VGM으로.</summary>
    public static VgmSong Render(byte[] mdx, byte[] pdx, double maxSeconds = MaxSeconds)
    {
        var h = ReadHeader(mdx);
        var d = h.Data;
        var w = new Writer();
        var tracks = new List<Track>();
        for (int i = 0; i < h.Tracks.Length; i++)
            tracks.Add(new Track { Index = i, Ch = i < 8 ? i : -1, Adpcm = i >= 8, P = h.Tracks[i], Start = h.Tracks[i] });
        bool pdxOk = pdx != null && pdx.Length > 0x300;
        int pdxBanks = pdxOk ? Math.Max(1, PdxFirstOffset(pdx) / 0x300) : 0;

        // OPM 처음 상태
        w.Opm(0x01, 0x02); w.Opm(0x01, 0x00); w.Opm(0x0F, 0x00); w.Opm(0x18, 0x00); w.Opm(0x19, 0x00); w.Opm(0x19, 0x80); w.Opm(0x1B, 0x00);
        for (int ch = 0; ch < 8; ch++) { w.Opm(0x08, ch); w.Opm(0x38 + ch, 0); for (int o = 0; o < 4; o++) { w.Opm(0x60 + o * 8 + ch, 0x7F); w.Opm(0xE0 + o * 8 + ch, 0xFF); } }

        int tempo = 200;
        double tickSamples = TickSamples(tempo);
        double limit = maxSeconds * VgmSong.Rate;
        bool anyLoop = false;
        int ticks = 0;
        while (w.Time < limit)
        {
            bool done = true;
            foreach (var t in tracks) if (!t.Ended && t.Loops < 2) { done = false; break; }
            if (done) break;
            foreach (var t in tracks)
            {
                if (t.Ended || t.Loops >= 2) continue;
                int newTempo = Tick(t, h, w, tracks, pdx, pdxBanks);
                if (newTempo >= 0) { tempo = newTempo; tickSamples = TickSamples(tempo); }
                if (t.Loops > 0) anyLoop = true;
            }
            // 이 클럭에 마지막 트랙이 멈췄으면 시간을 더하지 않는다 (곡 길이 = 마지막 소리까지)
            bool stopped = true;
            foreach (var t in tracks) if (!t.Ended && t.Loops < 2) { stopped = false; break; }
            if (stopped) break;
            w.Time += tickSamples;
            ticks++;
        }
        double total = Math.Min(w.Time, limit);

        var vw = new VgmWriter { System = "X68000 (MDX)", Notes = "MXDRV MDX → VGM", Title = h.Title };
        vw.SetClock(VgmWriter.ClockYm2151, OpmClock);
        if (w.UsedAdpcm && pdxOk)
        {
            vw.SetClock(VgmWriter.ClockOkim6258, 8000000);
            vw.SetHeaderByte(0x94, 0x02);
            var blk = new byte[7 + pdx.Length];
            blk[0] = 0x67; blk[1] = 0x66; blk[2] = 0x04;
            BitConverter.GetBytes(pdx.Length).CopyTo(blk, 3);
            Array.Copy(pdx, 0, blk, 7, pdx.Length);
            vw.Write(blk);
            vw.Write(0x90, 0x00, 0x17, 0x00, 0x01);   // 스트림 0 → OKIM6258 데이터
            vw.Write(0x91, 0x00, 0x04, 0x01, 0x00);
        }
        long at = 0;
        foreach (var (time, cmd) in w.Log.OrderBy(e => e.t))   // 정렬은 안정 (같은 시간은 쓴 순서)
        {
            if (cmd[0] == 0xB7 || cmd[0] == 0x92 || cmd[0] == 0x93 || cmd[0] == 0x94) { if (!(w.UsedAdpcm && pdxOk)) continue; }
            long ts = (long)Math.Min(total, Math.Max(0, time));
            if (ts > at) { vw.Wait(ts - at); at = ts; }
            vw.Write(cmd);
        }
        if ((long)total > at) vw.Wait((long)total - at);
        var v = VgmFormat.Read(vw.ToArray());
        v.TitleJp = h.Title;
        v.TitleEn = "";
        if (anyLoop) v.FadeSeconds = Math.Min(FadeSeconds, total / VgmSong.Rate / 2);
        if (w.UsedAdpcm && !pdxOk && h.PdxName.Length == 0) v.UnsupportedChips.Add("(ADPCM 트랙이 있지만 PDX 이름이 없음)");
        return v;
    }

    /// <summary>타이머 B 한 번 = 1024 × (256 − TB) / 4MHz.</summary>
    private static double TickSamples(int tb) { return 1024.0 * (256 - (tb & 0xFF)) / OpmClock * VgmSong.Rate; }

    private static int PdxFirstOffset(byte[] pdx)
    {
        int min = int.MaxValue;
        for (int i = 0; i + 8 <= Math.Min(pdx.Length, 0x300 * 16); i += 8)
        {
            int o = (int)U32(pdx, i), l = (int)U32(pdx, i + 4);
            if (l > 0 && o > 0 && o < min) min = o;
            if (i + 8 >= min) break;
        }
        return min == int.MaxValue ? 0x300 : min;
    }

    /// <summary>트랙 한 클럭. 템포가 바뀌면 그 값, 아니면 −1.</summary>
    private static int Tick(Track t, Header h, Writer w, List<Track> all, byte[] pdx, int pdxBanks)
    {
        int tempo = -1;
        var d = h.Data;
        // 키온 늦춤
        if (t.KeyDelayLeft > 0 && --t.KeyDelayLeft == 0) KeyOn(t, h, w, pdx, pdxBanks);
        if (t.Wait <= 0 && !t.Waiting)
        {
            int guard = 0;
            while (!t.Ended && t.Wait <= 0 && guard++ < 10000)
            {
                if (t.P < 0 || t.P >= d.Length) { t.Ended = true; break; }
                int c = d[t.P];
                if (c < 0x80) { t.Wait = c + 1; t.P++; Rest(t, w); break; }
                if (c < 0xE0)
                {
                    if (t.P + 1 >= d.Length) { t.Ended = true; break; }
                    int len = d[t.P + 1] + 1;
                    t.P += 2;
                    NoteStart(t, c - 0x80, len, h, w, pdx, pdxBanks);
                    break;
                }
                if (!Command(t, c, h, w, all, ref tempo)) break;
            }
        }
        else if (t.Waiting) return tempo;
        // 효과 (포르타멘토 · LFO)
        if (!t.Adpcm && t.Ch >= 0 && t.Note >= 0 && t.Keyed)
        {
            t.PortaAcc += t.Porta;
            t.LfoTick++;
            if (t.LfoTick > t.LfoDelay) { if (t.PitchLfo.On) t.PitchLfo.T++; if (t.AmpLfo.On) t.AmpLfo.T++; }
            WritePitch(t, w, false);
            WriteVolume(t, h, w, false);
        }
        // 길이 · 게이트
        if (t.Wait > 0) t.Wait--;
        if (t.Gate > 0 && --t.Gate == 0 && !t.Tied) KeyOff(t, w);
        return tempo;
    }

    private static void Rest(Track t, Writer w)
    {
        if (t.Keyed) KeyOff(t, w);   // 이음 뒤의 쉼도 끊는다
        t.Gate = -1;
        t.Tied = false; t.TieNext = false;
    }

    private static void NoteStart(Track t, int n, int len, Header h, Writer w, byte[] pdx, int banks)
    {
        bool legato = t.Tied && t.Keyed && !t.Adpcm;
        t.Tied = t.TieNext; t.TieNext = false;
        t.Wait = len;
        t.Gate = t.Q <= 8 ? Math.Max(1, len * t.Q / 8) : Math.Max(1, len - (256 - t.Q));
        t.Note = n;
        t.Porta = t.PortaNext; t.PortaNext = 0; t.PortaAcc = 0;
        if (legato) { WritePitch(t, w, true); return; }
        t.LfoTick = 0; t.PitchLfo.T = 0; t.AmpLfo.T = 0;
        if (t.KeyDelay > 0) { t.KeyDelayLeft = t.KeyDelay; if (t.Keyed) KeyOff(t, w); return; }
        KeyOn(t, h, w, pdx, banks);
    }

    private static void KeyOn(Track t, Header h, Writer w, byte[] pdx, int banks)
    {
        t.KeyDelayLeft = -1;
        if (t.Adpcm) { AdpcmNote(t, w, pdx, banks); return; }
        if (t.Ch < 0) return;
        if (t.Keyed) w.Opm(0x08, t.Ch);
        WritePitch(t, w, true);
        WriteVolume(t, h, w, true);
        if (t.OpmLfoSync) { w.Opm(0x01, 0x02); w.Opm(0x01, 0x00); }
        int mask = t.Voice >= 0 && h.Voices.TryGetValue(t.Voice, out var vo) ? vo[2] & 15 : 15;
        w.Opm(0x08, (mask << 3) | t.Ch);
        t.Keyed = true;
    }

    private static void KeyOff(Track t, Writer w)
    {
        if (!t.Adpcm && t.Ch >= 0 && t.Keyed) w.Opm(0x08, t.Ch);
        t.Keyed = false;
    }

    private static void AdpcmNote(Track t, Writer w, byte[] pdx, int banks)
    {
        if (pdx == null || pdx.Length < 0x300) { w.UsedAdpcm = true; return; }
        int bank = t.Voice > 0 && t.Voice < banks ? t.Voice : 0;
        int idx = bank * 96 + t.Note;
        if ((idx + 1) * 8 > pdx.Length) return;
        int ofs = (int)U32(pdx, idx * 8), len = (int)U32(pdx, idx * 8 + 4);
        if (len <= 0 || ofs < 0 || ofs >= pdx.Length) return;
        len = Math.Min(len, pdx.Length - ofs);
        w.UsedAdpcm = true;
        var (clock, div) = AdpcmRates[Math.Max(0, Math.Min(4, t.AdpcmRate))];
        w.Raw(0x94, 0x00);
        w.Msm(0x00, 0x01);
        for (int i = 0; i < 4; i++) w.Msm(0x08 + i, (clock >> (8 * i)) & 0xFF);
        w.Msm(0x0C, div);
        w.Msm(0x02, t.Pan == 1 ? 1 : t.Pan == 2 ? 2 : t.Pan == 0 ? 3 : 0);
        int bytesPerSec = clock / Dividers[div] / 2;
        w.Raw(0x92, 0x00, (byte)bytesPerSec, (byte)(bytesPerSec >> 8), (byte)(bytesPerSec >> 16), 0);
        w.Msm(0x00, 0x02);
        w.Raw(0x93, 0x00, (byte)ofs, (byte)(ofs >> 8), (byte)(ofs >> 16), (byte)(ofs >> 24), 0x01, (byte)len, (byte)(len >> 8), (byte)(len >> 16), (byte)(len >> 24));
    }

    /// <summary>KC · KF (음 + 디튠 + 포르타멘토 + 음높이 LFO). force가 아니면 바뀐 것만.</summary>
    private static void WritePitch(Track t, Writer w, bool force)
    {
        if (t.Adpcm || t.Ch < 0 || t.Note < 0) return;
        int lfo = t.PitchLfo.On && t.LfoTick > t.LfoDelay ? t.PitchLfo.Value() : 0;
        int p = t.Note * 64 + t.Detune + ((t.PortaAcc + lfo) >> 8);
        p = Math.Max(0, Math.Min(8 * 12 * 64 - 1, p));
        int s = p >> 6;
        int kc = ((s / 12) << 4) | NoteCode[s % 12], kf = (p & 63) << 2;
        if (force || kc != t.LastKc) { w.Opm(0x28 + t.Ch, kc); t.LastKc = kc; }
        if (force || kf != t.LastKf) { w.Opm(0x30 + t.Ch, kf); t.LastKf = kf; }
    }

    /// <summary>캐리어 TL = 음색 TL + 음량 + 음량 LFO.</summary>
    private static void WriteVolume(Track t, Header h, Writer w, bool force)
    {
        if (t.Adpcm || t.Ch < 0 || t.Voice < 0 || !h.Voices.TryGetValue(t.Voice, out var vo)) return;
        int att = t.VolDirect ? t.Vol & 0x7F : VolTable[Math.Max(0, Math.Min(15, t.Vol))];
        if (t.AmpLfo.On && t.LfoTick > t.LfoDelay) att += t.AmpLfo.Value() >> 8;
        if (!force && att == t.LastAmp) return;
        t.LastAmp = att;
        int con = vo[1] & 7;
        for (int o = 0; o < 4; o++)
        {
            if (!IsCarrier(con, o)) continue;
            w.Opm(0x60 + o * 8 + t.Ch, Math.Max(0, Math.Min(127, (vo[7 + o] & 0x7F) + att)));
        }
    }

    /// <summary>연산자 o (레지스터 순서 M1 M2 C1 C2)가 캐리어인가.</summary>
    private static bool IsCarrier(int con, int o)
    {
        if (o == 3) return true;
        if (con == 7) return true;
        if (o == 2) return con >= 4;
        if (o == 1) return con >= 5;
        return false;
    }

    private static void SetVoice(Track t, Header h, Writer w, int n)
    {
        t.Voice = n;
        if (t.Adpcm || t.Ch < 0 || !h.Voices.TryGetValue(n, out var vo)) return;
        int ch = t.Ch;
        w.Opm(0x20 + ch, PanBits(t.Pan) | (vo[1] & 0x3F));
        for (int o = 0; o < 4; o++)
        {
            w.Opm(0x40 + o * 8 + ch, vo[3 + o]);
            w.Opm(0x80 + o * 8 + ch, vo[11 + o]);
            w.Opm(0xA0 + o * 8 + ch, vo[15 + o]);
            w.Opm(0xC0 + o * 8 + ch, vo[19 + o]);
            w.Opm(0xE0 + o * 8 + ch, vo[23 + o]);
        }
        for (int o = 0; o < 4; o++) w.Opm(0x60 + o * 8 + ch, vo[7 + o] & 0x7F);
        t.LastAmp = int.MinValue;
        WriteVolume(t, h, w, true);
    }

    private static int PanBits(int pan) { return ((pan & 1) << 6) | ((pan & 2) << 6); }

    /// <summary>명령 하나 (음 · 쉼 말고). 트랙이 멈추면 false.</summary>
    private static bool Command(Track t, int c, Header h, Writer w, List<Track> all, ref int tempo)
    {
        var d = h.Data;
        int p = t.P;
        int B(int i) { return p + i < d.Length ? d[p + i] : 0; }
        int S16(int i) { return (short)((B(i) << 8) | B(i + 1)); }
        switch (c)
        {
            case 0xFF: tempo = B(1); t.P += 2; return true;
            case 0xFE: if (!t.Adpcm) w.Opm(B(1), B(2)); t.P += 3; return true;
            case 0xFD: SetVoice(t, h, w, B(1)); t.P += 2; return true;
            case 0xFC:
                t.Pan = B(1) & 3; t.P += 2;
                if (!t.Adpcm && t.Ch >= 0 && t.Voice >= 0 && h.Voices.TryGetValue(t.Voice, out var vo)) w.Opm(0x20 + t.Ch, PanBits(t.Pan) | (vo[1] & 0x3F));
                if (t.Adpcm) w.Msm(0x02, t.Pan == 1 ? 1 : t.Pan == 2 ? 2 : t.Pan == 0 ? 3 : 0);
                return true;
            case 0xFB:
                {
                    int v = B(1);
                    t.VolDirect = (v & 0x80) != 0;
                    t.Vol = t.VolDirect ? v & 0x7F : v & 15;
                    t.P += 2; WriteVolume(t, h, w, true); return true;
                }
            case 0xFA: case 0xF9:
                {
                    bool up = c == 0xF9;
                    if (t.VolDirect) t.Vol = Math.Max(0, Math.Min(127, t.Vol + (up ? -1 : 1)));
                    else t.Vol = Math.Max(0, Math.Min(15, t.Vol + (up ? 1 : -1)));
                    t.P += 1; WriteVolume(t, h, w, true); return true;
                }
            case 0xF8: t.Q = B(1); t.P += 2; return true;
            case 0xF7: t.TieNext = true; t.P += 1; return true;
            case 0xF6: t.Repeat[p + 2] = Math.Max(1, B(1)); t.P += 3; return true;
            case 0xF5:
                {
                    int tgt = p + 3 + S16(1);
                    int key = tgt - 1;
                    int left = t.Repeat.TryGetValue(key, out int r) ? r - 1 : 0;
                    t.Repeat[key] = left;
                    t.P = left > 0 ? tgt : p + 3;
                    return true;
                }
            case 0xF4:
                {
                    int tgt = p + 3 + S16(1);           // 짝 F5의 거리 바이트
                    int f5Tgt = tgt + 2 + (short)((B(tgt - p) << 8) | B(tgt - p + 1));
                    int key = f5Tgt - 1;
                    bool last = t.Repeat.TryGetValue(key, out int r) && r <= 1;
                    t.P = last ? tgt + 2 : p + 3;
                    return true;
                }
            case 0xF3: t.Detune = S16(1); t.P += 3; return true;
            case 0xF2: t.PortaNext = S16(1); t.P += 3; return true;
            case 0xF1:
                if (B(1) == 0) { t.Ended = true; Rest(t, w); return false; }
                t.Loops++;
                t.P = p + 3 + S16(1);
                if (t.Loops >= 2) { Rest(t, w); return false; }   // 두 번 되돌아가면 멈춤 (곡 끝)
                return true;
            case 0xF0: t.KeyDelay = B(1); t.P += 2; return true;
            case 0xEF:
                {
                    int n = B(1);
                    if (n >= 0 && n < all.Count) { var o = all[n]; if (o.Waiting) o.Waiting = false; else o.SyncPending = true; }
                    t.P += 2; return true;
                }
            case 0xEE:
                t.P += 1;
                if (t.SyncPending) { t.SyncPending = false; return true; }
                t.Waiting = true; return false;
            case 0xED:
                if (t.Adpcm) t.AdpcmRate = B(1) & 7;
                else if (t.Ch == 7) w.Opm(0x0F, B(1));
                t.P += 2; return true;
            case 0xEC: case 0xEB:
                {
                    var l = c == 0xEC ? t.PitchLfo : t.AmpLfo;
                    int a = B(1);
                    if (a == 0x80) { l.On = false; t.P += 2; return true; }
                    if (a == 0x81) { l.On = true; t.P += 2; return true; }
                    l.Wave = a; l.Period = (B(2) << 8) | B(3); l.Change = S16(4); l.On = true;
                    t.P += 6; return true;
                }
            case 0xEA:
                {
                    int a = B(1);
                    if (a == 0x80) { if (t.Ch >= 0) w.Opm(0x38 + t.Ch, 0); t.P += 2; return true; }
                    if (a == 0x81) { if (t.Ch >= 0) w.Opm(0x38 + t.Ch, t.OpmPmsAms); t.P += 2; return true; }
                    t.OpmLfoSync = (a & 0x40) != 0;
                    w.Opm(0x1B, a & 3); w.Opm(0x18, B(2)); w.Opm(0x19, B(3) | 0x80); w.Opm(0x19, B(4) & 0x7F);
                    t.OpmPmsAms = B(5);
                    if (t.Ch >= 0) w.Opm(0x38 + t.Ch, t.OpmPmsAms);
                    t.P += 6; return true;
                }
            case 0xE9: t.LfoDelay = B(1); t.P += 2; return true;
            case 0xE8: t.P += 1; return true;
            case 0xE7: t.P += B(1) == 0x01 ? 3 : 2; return true;
            default: t.Ended = true; Rest(t, w); return false;
        }
    }

    private static int U16(byte[] d, int o) { return o + 1 < d.Length ? (d[o] << 8) | d[o + 1] : 0; }
    private static uint U32(byte[] d, int o) { return o + 3 < d.Length ? (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]) : 0; }
}
