using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// MOD / NST (ProTracker · NoiseTracker · SoundTracker) 읽기 · 쓰기. 편집 · 재생은 S3M 모델로 바꿔서 한다
/// (OpenMPT가 MOD를 S3M 규칙에 맞추는 것과 같은 방식: 아미가 한계 플래그 켬, 채널 L R R L).
/// <code>
/// 0 제목(20) · 샘플 31개 × 30 (이름 22, 길이(워드, BE), 파인튠(하위 4비트 −8~7), 볼륨, 루프 시작(워드), 루프 길이(워드))
/// 950 곡 길이 · 재시작 · 주문 128 · 1080 서명 ("M.K." "M!K!" "4CHN" "6CHN" "8CHN" "xxCH" "FLT4" "FLT8" "CD81" "OKTA" "N.T.")
/// 1084 패턴 × 64줄 × 채널 × 4바이트 (주기 12비트, 샘플 = 상위 니블 둘, 명령 니블, 인포), 그 뒤 샘플 데이터 (8bit 부호 있음)
/// 서명이 없으면 샘플 15개짜리 옛 SoundTracker (600부터 패턴).
/// </code>
/// 음: 주기 428 = ProTracker C-2 = ST3 C-4 (C2Spd로 재생). 명령은 S3M 명령으로 바꾼다 (Cxx 볼륨 = 볼륨 칸).
/// </summary>
public static class ModFormat
{
    public static readonly string[] Extensions = { ".mod", ".nst" };

    public static bool IsMod(string path)
    {
        string e = Path.GetExtension(path).ToLowerInvariant();
        return e == ".mod" || e == ".nst";
    }

    public static TrackerModule Load(string path)
    {
        var m = Read(File.ReadAllBytes(path));
        m.FilePath = path;
        return m;
    }

    /// <summary>1080의 서명 → 채널 수 (0 = 서명 없음).</summary>
    private static int ChannelsFor(string sig)
    {
        switch (sig)
        {
            case "M.K.": case "M!K!": case "M&K!": case "4CHN": case "FLT4": case "N.T.": return 4;
            case "6CHN": return 6;
            case "8CHN": case "FLT8": case "CD81": case "OKTA": return 8;
        }
        if (sig.Length == 4 && sig.EndsWith("CH") && char.IsDigit(sig[0]) && char.IsDigit(sig[1])) return (sig[0] - '0') * 10 + (sig[1] - '0');
        if (sig.Length == 4 && sig.EndsWith("CHN") && char.IsDigit(sig[0])) return sig[0] - '0';
        return 0;
    }

    public static TrackerModule Read(byte[] d)
    {
        if (d.Length < 600) throw new InvalidDataException("MOD 파일이 너무 작습니다.");
        string sig = d.Length >= 1084 ? Encoding.ASCII.GetString(d, 1080, 4) : "";
        int channels = ChannelsFor(sig);
        int nSamples = channels > 0 ? 31 : 15;
        if (channels == 0) channels = 4;
        if (channels > TrackerModule.MaxChannels) throw new InvalidDataException("MOD 채널 수가 너무 많습니다: " + channels);
        int orderOff = 20 + nSamples * 30;
        int songLen = Math.Max(1, Math.Min(128, (int)d[orderOff]));
        int patOff = orderOff + 2 + 128 + (nSamples == 31 ? 4 : 0);
        int maxPat = 0;
        for (int i = 0; i < 128; i++) maxPat = Math.Max(maxPat, d[orderOff + 2 + i]);
        int nPatterns = maxPat + 1;
        int patBytes = 64 * channels * 4;
        if (nSamples == 15)
        {
            // 옛 SoundTracker: 서명이 없으니 그럴듯한지 따져 본다
            if (patOff + patBytes > d.Length || maxPat > 63) throw new InvalidDataException("MOD 파일이 아닙니다 (서명 없음, 15샘플 형식으로도 맞지 않음).");
        }

        var m = TrackerModule.CreateNew(TrackerKind.S3m);
        m.Patterns.Clear(); m.Orders.Clear();
        m.Title = TrackerModule.Ascii(d, 0, 20);
        m.Flags = 16;            // 아미가 한계
        m.TrackerVersion = 0x1320;
        m.Speed = 6; m.Tempo = 125; m.GlobalVolume = 64; m.MasterVolume = 48; m.Stereo = true;
        for (int c = 0; c < TrackerModule.MaxChannels; c++) { m.ChannelSettings[c] = 255; m.Pan[c] = 7; }
        int left = 0, right = 0;
        for (int c = 0; c < channels; c++)
        {
            bool isLeft = c % 4 == 0 || c % 4 == 3;   // 아미가 L R R L
            m.ChannelSettings[c] = (byte)(isLeft ? (left++ % 8) : 8 + (right++ % 8));
            m.Pan[c] = (byte)(isLeft ? 3 : 12);
        }

        var lengths = new int[nSamples];
        for (int i = 0; i < nSamples; i++)
        {
            int o = 20 + i * 30;
            var s = new TrackerSample { Type = TrackerSample.TypeSample, Name = TrackerModule.Ascii(d, o, 22) };
            lengths[i] = BE16(d, o + 22) * 2;
            int ft = d[o + 24] & 15;
            if (ft > 7) ft -= 16;
            s.C2Spd = (int)Math.Round(8363 * Math.Pow(2, ft / 96.0));
            s.Volume = Math.Min(64, (int)d[o + 25]);
            int rs = BE16(d, o + 26) * 2, rl = BE16(d, o + 28) * 2;
            if (nSamples == 15) rs /= 2;   // SoundTracker: 루프 시작은 바이트
            s.Loop = rl > 2;
            s.LoopStart = rs; s.LoopEnd = rs + rl;
            m.Samples.Add(s);
        }
        for (int i = 0; i < songLen; i++) m.Orders.Add(d[orderOff + 2 + i]);

        int p = patOff;
        for (int pi = 0; pi < nPatterns; pi++)
        {
            var pat = m.NewPattern();
            for (int row = 0; row < 64; row++)
                for (int c = 0; c < channels; c++)
                {
                    int o = p + (row * channels + c) * 4;
                    if (o + 4 > d.Length) continue;
                    pat[row, c] = ToCell(d[o], d[o + 1], d[o + 2], d[o + 3]);
                }
            m.Patterns.Add(pat);
            p += patBytes;
        }

        for (int i = 0; i < nSamples; i++)
        {
            var s = m.Samples[i];
            int len = Math.Max(0, Math.Min(lengths[i], d.Length - p));
            s.Data = new short[len];
            for (int k = 0; k < len; k++) s.Data[k] = (short)((sbyte)d[p + k] << 8);
            p += lengths[i];
            if (s.LoopEnd > s.Data.Length) s.LoopEnd = s.Data.Length;
            if (s.LoopStart >= s.LoopEnd) s.Loop = false;
            if (s.Data.Length == 0) s.Type = TrackerSample.TypeEmpty;
        }
        return m;
    }

    // ------------------------------------------------------------ 음 ↔ 주기

    /// <summary>주기 → ST3 음 (428 = C-4). 0이면 음 없음.</summary>
    public static byte PeriodToNote(int period)
    {
        if (period <= 0) return TrackerCell.NoNote;
        int semis = (int)Math.Round(12 * Math.Log(428.0 / period, 2));
        int n = 4 * 12 + semis;
        n = Math.Max(0, Math.Min(7 * 12 + 11, n));
        return TrackerCell.MakeNote(n / 12, n % 12);
    }

    /// <summary>ProTracker 주기 표 (파인튠 0, C-1 ~ B-3).</summary>
    private static readonly int[] PtPeriods =
    {
        856, 808, 762, 720, 678, 640, 604, 570, 538, 508, 480, 453,
        428, 404, 381, 360, 339, 320, 302, 285, 269, 254, 240, 226,
        214, 202, 190, 180, 170, 160, 151, 143, 135, 127, 120, 113,
    };

    /// <summary>ST3 음 → 주기. ProTracker 음역(C-1 ~ B-3 = ST3 C-3 ~ B-5)은 표 그대로, 밖이면 계산 값.</summary>
    public static int NoteToPeriod(byte note)
    {
        int n = (note >> 4) * 12 + (note & 15);
        int idx = n - 3 * 12;
        if (idx >= 0 && idx < PtPeriods.Length) return PtPeriods[idx];
        return Math.Max(28, Math.Min(4095, (int)Math.Round(428 * Math.Pow(2, -(n - 48) / 12.0))));
    }

    // ------------------------------------------------------------ 칸 ↔ MOD 4바이트

    private static TrackerCell ToCell(byte b0, byte b1, byte b2, byte b3)
    {
        var cell = TrackerCell.Empty;
        int period = ((b0 & 15) << 8) | b1;
        int sample = (b0 & 0xF0) | (b2 >> 4);
        int fx = b2 & 15, x = b3 >> 4, y = b3 & 15, info = b3;
        if (period > 0) cell.Note = PeriodToNote(period);
        cell.Instrument = (byte)sample;
        int cmd = 0, inf = 0;
        switch (fx)
        {
            case 0x0: if (info != 0) { cmd = 'J'; inf = info; } break;
            case 0x1: if (info != 0) { cmd = 'F'; inf = Math.Min(0xDF, info); } break;
            case 0x2: if (info != 0) { cmd = 'E'; inf = Math.Min(0xDF, info); } break;
            case 0x3: cmd = 'G'; inf = info; break;
            case 0x4: cmd = 'H'; inf = info; break;
            case 0x5: cmd = 'L'; inf = VolSlide(x, y); break;
            case 0x6: cmd = 'K'; inf = VolSlide(x, y); break;
            case 0x7: cmd = 'R'; inf = info; break;
            case 0x8: cmd = 'X'; inf = info >> 1; break;
            case 0x9: cmd = 'O'; inf = info; break;
            case 0xA: if (info != 0) { cmd = 'D'; inf = VolSlide(x, y); } break;
            case 0xB: cmd = 'B'; inf = info; break;
            case 0xC: cell.Volume = (byte)Math.Min(64, info); break;
            case 0xD: cmd = 'C'; inf = info; break;   // 둘 다 10진(BCD) 줄 번호
            case 0xE:
                switch (x)
                {
                    case 0x1: if (y != 0) { cmd = 'F'; inf = 0xF0 | y; } break;
                    case 0x2: if (y != 0) { cmd = 'E'; inf = 0xF0 | y; } break;
                    case 0x3: cmd = 'S'; inf = 0x10 | y; break;
                    case 0x4: cmd = 'S'; inf = 0x30 | y; break;
                    case 0x5: cmd = 'S'; inf = 0x20 | y; break;
                    case 0x6: cmd = 'S'; inf = 0xB0 | y; break;
                    case 0x7: cmd = 'S'; inf = 0x40 | y; break;
                    case 0x8: cmd = 'S'; inf = 0x80 | y; break;
                    case 0x9: if (y != 0) { cmd = 'Q'; inf = y; } break;
                    case 0xA: if (y != 0) { cmd = 'D'; inf = (y << 4) | 0x0F; } break;
                    case 0xB: if (y != 0) { cmd = 'D'; inf = 0xF0 | y; } break;
                    case 0xC: cmd = 'S'; inf = 0xC0 | y; break;
                    case 0xD: cmd = 'S'; inf = 0xD0 | y; break;
                    case 0xE: cmd = 'S'; inf = 0xE0 | y; break;
                }
                break;
            case 0xF:
                if (info == 0) break;
                if (info < 0x20) { cmd = 'A'; inf = info; } else { cmd = 'T'; inf = info; }
                break;
        }
        if (cmd != 0) { cell.Command = (byte)(cmd - 'A' + 1); cell.Info = (byte)inf; }
        return cell;
    }

    /// <summary>MOD 볼륨 슬라이드 xy (x 올림이 먼저) → S3M Dxy (x0 올림 / 0y 내림).</summary>
    private static int VolSlide(int x, int y) { return x != 0 ? x << 4 : y; }

    /// <summary>S3M 명령 → MOD 명령 (못 바꾸면 false).</summary>
    private static bool FromCommand(TrackerCell c, out int fx, out int info)
    {
        fx = 0; info = 0;
        int x = c.Info >> 4, y = c.Info & 15;
        switch ((char)('A' + c.Command - 1))
        {
            case 'A': if (c.Info == 0) return false; fx = 0xF; info = Math.Min(0x1F, (int)c.Info); return true;
            case 'B': fx = 0xB; info = c.Info; return true;
            case 'C': fx = 0xD; info = c.Info; return true;
            case 'D':
                if (y == 0xF && x != 0) { fx = 0xE; info = 0xA0 | x; return true; }
                if (x == 0xF && y != 0) { fx = 0xE; info = 0xB0 | y; return true; }
                fx = 0xA; info = c.Info; return true;
            case 'E': if (x == 0xF) { fx = 0xE; info = 0x20 | y; return true; } if (x == 0xE) return false; fx = 0x2; info = c.Info; return true;
            case 'F': if (x == 0xF) { fx = 0xE; info = 0x10 | y; return true; } if (x == 0xE) return false; fx = 0x1; info = c.Info; return true;
            case 'G': fx = 0x3; info = c.Info; return true;
            case 'H': fx = 0x4; info = c.Info; return true;
            case 'J': fx = 0x0; info = c.Info; return true;
            case 'K': fx = 0x6; info = c.Info; return true;
            case 'L': fx = 0x5; info = c.Info; return true;
            case 'O': fx = 0x9; info = c.Info; return true;
            case 'Q': fx = 0xE; info = 0x90 | y; return true;
            case 'R': fx = 0x7; info = c.Info; return true;
            case 'T': if (c.Info < 0x20) return false; fx = 0xF; info = c.Info; return true;
            case 'X': fx = 0x8; info = Math.Min(255, c.Info * 2); return true;
            case 'S':
                switch (x)
                {
                    case 0x1: fx = 0xE; info = 0x30 | y; return true;
                    case 0x2: fx = 0xE; info = 0x50 | y; return true;
                    case 0x3: fx = 0xE; info = 0x40 | y; return true;
                    case 0x4: fx = 0xE; info = 0x70 | y; return true;
                    case 0x8: fx = 0xE; info = 0x80 | y; return true;
                    case 0xB: fx = 0xE; info = 0x60 | y; return true;
                    case 0xC: fx = 0xE; info = 0xC0 | y; return true;
                    case 0xD: fx = 0xE; info = 0xD0 | y; return true;
                    case 0xE: fx = 0xE; info = 0xE0 | y; return true;
                }
                return false;
        }
        return false;
    }

    // ------------------------------------------------------------ 쓰기

    /// <summary>
    /// MOD 쓰기 (S3M 모델에서). 채널 = 쓰는 샘플 채널 수 (짝수, 4 = "M.K.", 6 · 8 = "xCHN", 그 밖 = "xxCH"), 샘플 31개 (8bit),
    /// 패턴 64개까지 "M.K." / 더 많으면 "M!K!". 볼륨 칸은 명령이 없을 때 Cxx로. 바꿀 수 없는 것은 warnings에.
    /// </summary>
    public static byte[] Write(TrackerModule src, List<string> warnings)
    {
        var m = src.Kind == TrackerKind.S3m ? src : src.ConvertTo(TrackerKind.S3m, warnings);
        // 쓰는 샘플 채널 (AdLib · 꺼진 채널 빼고) → MOD 채널
        var chans = new List<int>();
        for (int c = 0; c < m.Channels; c++)
            if (m.IsChannelUsed(c) && !m.IsAdlibChannel(c)) chans.Add(c);
        // 뒤쪽의 비어 있는 채널은 뺀다 (새 S3M은 8채널이 켜져 있지만 4채널만 쓰는 경우가 많다)
        int lastUsed = -1;
        for (int k = 0; k < chans.Count; k++)
            foreach (var pat in m.Patterns)
            {
                bool any = false;
                for (int row = 0; row < TrackerPattern.Rows && !any; row++) any = !pat[row, chans[k]].IsEmpty;
                if (any) { lastUsed = k; break; }
            }
        if (lastUsed + 1 < chans.Count) chans.RemoveRange(Math.Max(lastUsed + 1, Math.Min(4, chans.Count)), chans.Count - Math.Max(lastUsed + 1, Math.Min(4, chans.Count)));
        if (chans.Count == 0) chans.Add(0);
        int adlibCh = 0;
        for (int c = 0; c < m.Channels; c++) if (m.IsChannelUsed(c) && m.IsAdlibChannel(c)) adlibCh++;
        if (adlibCh > 0) warnings.Add("AdLib 채널 " + adlibCh + "개는 MOD에 넣을 수 없어 빠집니다.");
        int channels = Math.Max(4, chans.Count + (chans.Count % 2));
        string sig = channels == 4 ? "M.K." : channels < 10 ? channels + "CHN" : channels + "CH";

        var orders = m.PlayOrders();
        if (orders.Count > 128) { warnings.Add("주문 " + orders.Count + "개 중 128개만 들어갑니다."); orders = orders.GetRange(0, 128); }
        if (orders.Count == 0) orders.Add(0);
        int nPat = 0;
        foreach (int o in orders) nPat = Math.Max(nPat, o + 1);
        nPat = Math.Min(nPat, m.Patterns.Count);
        if (nPat > 64 && channels == 4) sig = "M!K!";
        if (nPat > 100) { warnings.Add("패턴은 100개까지만 들어갑니다."); nPat = 100; }

        int adlibIns = 0, bigIns = 0;
        if (m.Samples.Count > 31) warnings.Add("악기 " + m.Samples.Count + "개 중 31개만 들어갑니다.");
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Fixed(m.Title, 20));
        var sampleData = new List<byte[]>();
        for (int i = 0; i < 31; i++)
        {
            var s = i < m.Samples.Count ? m.Samples[i] : null;
            if (s != null && s.IsAdlib) { adlibIns++; s = null; }
            byte[] data = new byte[0];
            if (s != null && s.IsSample)
            {
                int len = s.Data.Length & ~1;
                if (len > 0x1FFFE) { bigIns++; len = 0x1FFFE; }
                data = new byte[len];
                for (int k = 0; k < len; k++) data[k] = (byte)(sbyte)(s.Data[k] >> 8);
            }
            sampleData.Add(data);
            w.Write(Fixed(s != null ? s.Name : "", 22));
            BE(w, data.Length / 2);
            int ft = 0;
            if (s != null && s.C2Spd > 0) ft = Math.Max(-8, Math.Min(7, (int)Math.Round(96 * Math.Log(s.C2Spd / 8363.0, 2))));
            if (s != null && s.C2Spd > 0 && Math.Abs(96 * Math.Log(s.C2Spd / 8363.0, 2)) > 8.5) warnings.Add("악기 " + (i + 1) + " C4Spd " + s.C2Spd + "는 MOD 파인튠(−8~7) 밖입니다.");
            w.Write((byte)(ft & 15));
            w.Write((byte)(s != null ? Math.Min(64, s.Volume) : 0));
            if (s != null && s.Loop && s.LoopEnd > s.LoopStart && data.Length > 0)
            {
                int ls = Math.Min(s.LoopStart, data.Length) / 2, le = Math.Min(s.LoopEnd, data.Length) / 2;
                BE(w, ls); BE(w, Math.Max(1, le - ls));
            }
            else { BE(w, 0); BE(w, 1); }
        }
        if (adlibIns > 0) warnings.Add("AdLib 악기 " + adlibIns + "개는 MOD에 넣을 수 없어 빈 샘플이 됩니다.");
        if (bigIns > 0) warnings.Add("128KB보다 긴 샘플 " + bigIns + "개는 잘립니다.");
        w.Write((byte)orders.Count);
        w.Write((byte)127);
        for (int i = 0; i < 128; i++) w.Write((byte)(i < orders.Count ? Math.Min(nPat - 1, orders[i]) : 0));
        w.Write(Encoding.ASCII.GetBytes(sig));

        int lostVol = 0, lostFx = 0, lostNote = 0;
        for (int pi = 0; pi < nPat; pi++)
        {
            var pat = m.Patterns[pi];
            for (int row = 0; row < 64; row++)
                for (int c = 0; c < channels; c++)
                {
                    var cell = c < chans.Count ? pat[row, chans[c]] : TrackerCell.Empty;
                    int period = 0;
                    if (cell.HasNote)
                    {
                        period = NoteToPeriod(cell.Note);
                        int n = cell.Octave * 12 + cell.Semitone;
                        if (n < 3 * 12 || n >= 6 * 12) lostNote++;
                    }
                    int ins = Math.Min(31, (int)cell.Instrument);
                    int fx = 0, info = 0;
                    bool hasFx = cell.Command != 0;
                    if (hasFx && !FromCommand(cell, out fx, out info)) { lostFx++; fx = 0; info = 0; hasFx = false; }
                    if (cell.Note == TrackerCell.NoteOff)
                    {
                        // ^^ 노트 오프 → C00 (볼륨 0). 명령이나 볼륨 칸과 겹치면 빠진다
                        if (!hasFx && cell.Volume == TrackerCell.NoVolume) { fx = 0xC; info = 0; hasFx = true; }
                        else lostNote++;
                    }
                    if (cell.Volume != TrackerCell.NoVolume)
                    {
                        if (!hasFx) { fx = 0xC; info = Math.Min(64, (int)cell.Volume); }
                        else lostVol++;
                    }
                    w.Write((byte)((ins & 0xF0) | ((period >> 8) & 15)));
                    w.Write((byte)(period & 0xFF));
                    w.Write((byte)(((ins & 15) << 4) | (fx & 15)));
                    w.Write((byte)info);
                }
        }
        if (lostNote > 0) warnings.Add("ProTracker 음역(ST3 C-3 ~ B-5) 밖의 음이나 노트 오프 " + lostNote + "개 (계산한 주기로 쓰거나 빠짐).");
        if (lostFx > 0) warnings.Add("MOD에 없는 명령 " + lostFx + "개가 빠집니다.");
        if (lostVol > 0) warnings.Add("명령과 겹친 볼륨 칸 " + lostVol + "개가 빠집니다.");
        foreach (var data in sampleData) w.Write(data);
        w.Flush();
        return ms.ToArray();
    }

    private static int BE16(byte[] d, int o) { return o + 1 < d.Length ? (d[o] << 8) | d[o + 1] : 0; }
    private static void BE(BinaryWriter w, int v) { w.Write((byte)((v >> 8) & 0xFF)); w.Write((byte)(v & 0xFF)); }

    private static byte[] Fixed(string s, int n)
    {
        var b = new byte[n];
        var src = Encoding.ASCII.GetBytes(s ?? "");
        Array.Copy(src, b, Math.Min(src.Length, n));
        return b;
    }
}
