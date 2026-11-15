using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// 레지스터를 직접 쓰는 AdLib 곡 형식을 OPL2 VGM으로 바꿔 읽는다 (재생은 VGM 재생기):
/// <list type="bullet">
/// <item>IMF (id Software: Commander Keen · Wolfenstein 3D · Duke Nukem II) — (레지스터, 값, 기다림) 기록 그대로. .imf = 560Hz, .wlf = 700Hz.</item>
/// <item>HSC (HSC-Tracker, Hannes Seifert) — 9채널 패턴 트래커. 재생 규칙은 AdPlug의 hsc.cpp를 따른다 (18.2Hz).</item>
/// <item>DRO (DOSBox Raw OPL, 판 0.1 · 2.0) · RAW (Rdos Raw OPL Capture) — 게임이 OPL에 쓴 것을 그대로 기록. OPL2 · OPL3 · 듀얼 OPL2.</item>
/// <item>CMF (Creative Music File, 사운드 블래스터) — 악기 뱅크 + MIDI 이벤트. SBFMDRV처럼 MIDI 채널 n = OPL 채널 n, 리듬 모드는 채널 12~16 = BD SD TOM CY HH.</item>
/// </list>
/// </summary>
public static class AdlibStreamFormats
{
    public static readonly string[] ImfExtensions = { ".imf", ".wlf" };
    public static readonly string[] HscExtensions = { ".hsc" };
    public static readonly string[] CmfExtensions = { ".cmf" };
    public const double Opl2Clock = 3579545;
    /// <summary>끝나지 않는 곡을 막는 최대 길이 (초).</summary>
    public const double MaxSeconds = 20 * 60;

    /// <summary>OPL2 VGM 만들기 도우미: 시간(초)을 받아 기다림을 44100Hz로 바꿔 쓴다.</summary>
    private sealed class Out
    {
        public readonly VgmWriter W = new VgmWriter { System = "PC (AdLib)" };
        private double now;     // 초
        private long written;   // 샘플
        public Out() { W.SetClock(VgmWriter.ClockYm3812, Opl2Clock); }
        public void Reg(int r, int v) { Flush(); W.Write(0x5A, r & 0xFF, v & 0xFF); }
        public void Advance(double sec) { now += sec; }
        public double Now { get { return now; } }
        public void Flush()
        {
            long target = (long)Math.Round(now * VgmSong.Rate);
            if (target > written) { W.Wait(target - written); written = target; }
        }
        public VgmSong Finish(string title, string source, double fade = 0)
        {
            Flush();
            W.Title = title ?? "";
            var s = VgmFormat.Read(W.ToArray());
            s.SourceFormat = source;
            s.FadeSeconds = fade;
            return s;
        }
    }

    // ================================================================ IMF

    public static VgmSong LoadImf(string path)
    {
        var d = File.ReadAllBytes(path);
        bool wlf = string.Equals(Path.GetExtension(path), ".wlf", StringComparison.OrdinalIgnoreCase);
        var s = ReadImf(d, wlf ? 700 : 560, Path.GetFileNameWithoutExtension(path));
        s.FilePath = path;
        return s;
    }

    /// <summary>
    /// 종류 0 = 머리말 없이 기록만, 종류 1 = 첫 2바이트가 기록 길이. 기록 뒤에 0x1A + 제목 · 작곡가 · 메모(0으로 끝)가 붙기도 한다.
    /// </summary>
    public static VgmSong ReadImf(byte[] d, int hz, string fallbackTitle = "")
    {
        if (d.Length < 4) throw new InvalidDataException("IMF 파일이 너무 짧습니다.");
        int start = 0, end = d.Length;
        int len = d[0] | (d[1] << 8);
        if (len > 0 && len % 4 == 0 && len + 2 <= d.Length) { start = 2; end = 2 + len; }
        string title = fallbackTitle, author = "", notes = "";
        if (end < d.Length && d[end] == 0x1A)
        {
            var parts = Encoding.ASCII.GetString(d, end + 1, d.Length - end - 1).Split('\0');
            if (parts.Length > 0 && parts[0].Trim().Length > 0) title = parts[0].Trim();
            if (parts.Length > 1) author = parts[1].Trim();
            if (parts.Length > 2) notes = parts[2].Trim();
        }
        var o = new Out();
        o.W.Author = author; o.W.Notes = notes;
        for (int p = start; p + 4 <= end; p += 4)
        {
            o.Reg(d[p], d[p + 1]);
            o.Advance((d[p + 2] | (d[p + 3] << 8)) / (double)hz);
            if (o.Now > MaxSeconds) break;
        }
        o.Advance(0.5);
        return o.Finish(title, "IMF " + hz + "Hz" + (start == 2 ? " (종류 1)" : " (종류 0)"));
    }

    // ================================================================ DRO · RAW (OPL 기록)

    public static readonly string[] DroExtensions = { ".dro" };
    public static readonly string[] RawExtensions = { ".raw" };
    public const double Opl3Clock = 14318180;

    /// <summary>OPL 쓰기 기록 (시간 · 칩 0/1 · 레지스터 · 값). 다 모은 뒤 OPL2 / OPL3 / 듀얼 OPL2를 정해 VGM으로.</summary>
    private sealed class OplLog
    {
        public readonly List<(double t, int chip, int reg, int val)> E = new List<(double, int, int, int)>();
        public double Now;
        public int Chip;
        public void Write(int reg, int val) { E.Add((Now, Chip, reg & 0xFF, val & 0xFF)); }
    }

    /// <summary>하드웨어 종류 (DRO 판 2 값으로 맞춤).</summary>
    private enum OplHw { Unknown = -1, Opl2 = 0, DualOpl2 = 1, Opl3 = 2 }

    /// <summary>
    /// 기록 → VGM. 둘째 칩을 안 쓰면 OPL2 (YM3812). OPL3이면 칩 0/1 = 레지스터 뱅크 0/1 그대로 (YMF262).
    /// 듀얼 OPL2(사운드 블래스터 프로 1)는 OPL3 하나로: 칩 0 = 왼쪽, 칩 1 = 오른쪽 (C0~C8의 좌우 비트를 바꿔 쓰고, NEW = 1로 좌우를 살린다).
    /// </summary>
    private static VgmSong OplLogToVgm(OplLog log, OplHw hw, string title, string source, string system)
    {
        bool chip1 = log.E.Any(e => e.chip == 1);
        bool opl3 = hw == OplHw.Opl3 || log.E.Any(e => e.chip == 1 && e.reg == 0x05 && (e.val & 1) != 0);
        bool dual = chip1 && !opl3;
        bool two = chip1 || opl3;
        var w = new VgmWriter { System = system + " (" + (opl3 ? "OPL3" : dual ? "듀얼 OPL2" : "OPL2") + ")", Title = title ?? "" };
        if (two) w.SetClock(VgmWriter.ClockYmf262, Opl3Clock); else w.SetClock(VgmWriter.ClockYm3812, Opl2Clock);
        if (dual) w.Write(0x5F, 0x05, 0x01);
        long written = 0;
        foreach (var (t, chip, reg, val) in log.E)
        {
            if (t > MaxSeconds) break;
            long target = (long)Math.Round(t * VgmSong.Rate);
            if (target > written) { w.Wait(target - written); written = target; }
            if (!two) { w.Write(0x5A, reg, val); continue; }
            int v = val;
            if (dual)
            {
                if (chip == 1 && reg == 0x05) continue;
                if (reg >= 0xC0 && reg <= 0xC8) v = (v & 0x0F) | (chip == 0 ? 0x10 : 0x20);
            }
            w.Write((byte)(chip == 0 ? 0x5E : 0x5F), reg, v);
        }
        long end = (long)Math.Round((Math.Min(log.Now, MaxSeconds) + 0.5) * VgmSong.Rate);
        if (end > written) w.Wait(end - written);
        var s = VgmFormat.Read(w.ToArray());
        s.SourceFormat = source + (opl3 ? " · OPL3" : dual ? " · 듀얼 OPL2 → OPL3 좌우" : " · OPL2");
        return s;
    }

    public static VgmSong LoadDro(string path)
    {
        var s = ReadDro(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
        s.FilePath = path;
        return s;
    }

    /// <summary>
    /// DOSBox Raw OPL (.dro, "DBRAWOPL"). 시간 단위 = 1ms.
    /// <list type="bullet">
    /// <item>판 0.1 (DOSBox 0.61~0.72): 0x08 판 0x00010000, 0x0C 길이(ms), 0x10 길이(바이트), 0x14 하드웨어 (0 OPL2 · 1 OPL3 · 2 듀얼 OPL2, 1바이트 또는 4바이트).
    /// 명령 0 = 기다림 1+n ms, 1 = 기다림 1+nn ms(16비트), 2 / 3 = 칩 0 / 1, 4 = 다음 두 바이트가 레지스터 · 값 (레지스터 0~4용), 그 밖 = 레지스터 · 값.</item>
    /// <item>판 2.0 (DOSBox 0.73~): 0x0C 쌍 개수, 0x14 하드웨어 (0 OPL2 · 1 듀얼 OPL2 · 2 OPL3), 0x15 형식 0, 0x16 압축 0,
    /// 0x17 짧은 기다림 코드, 0x18 긴 기다림 코드, 0x19 코드 표 길이, 0x1A 코드 표 (코드 → 레지스터). 이어서 (코드, 값) 쌍: 코드 bit7 = 칩 1.</item>
    /// </list>
    /// AdPlug dro.cpp · dro2.cpp와 같은 해석.
    /// </summary>
    public static VgmSong ReadDro(byte[] d, string title = "")
    {
        if (d.Length < 0x18 || Encoding.ASCII.GetString(d, 0, 8) != "DBRAWOPL") throw new InvalidDataException("DRO 파일이 아닙니다 (머리말 DBRAWOPL이 없음).");
        uint ver = VgmFormat.U32(d, 8);
        var log = new OplLog();
        OplHw hw;
        string source;
        if (ver == 0x10000)
        {
            int lenBytes = (int)Math.Min(VgmFormat.U32(d, 0x10), int.MaxValue);
            int t = d[0x14];
            hw = t == 0 ? OplHw.Opl2 : t == 1 ? OplHw.Opl3 : t == 2 ? OplHw.DualOpl2 : OplHw.Unknown;
            int p = 0x15;
            // 처음에는 하드웨어 종류가 1바이트였다가 판 번호를 바꾸지 않고 4바이트가 되었다: 뒤 3바이트 중 0이 있으면 4바이트
            if (d.Length >= 0x18 && (d[0x15] == 0 || d[0x16] == 0 || d[0x17] == 0)) p = 0x18;
            int end = (int)Math.Min(d.Length, (long)p + lenBytes);
            while (p < end)
            {
                int c = d[p++];
                switch (c)
                {
                    case 0: if (p < end) log.Now += (1 + d[p++]) / 1000.0; break;
                    case 1:
                        if (p + 1 < end) { log.Now += (1 + (d[p] | (d[p + 1] << 8))) / 1000.0; p += 2; }
                        else p = end;
                        break;
                    case 2: log.Chip = 0; break;
                    case 3: log.Chip = 1; break;
                    case 4:
                        if (p + 1 < end) { log.Write(d[p], d[p + 1]); p += 2; }
                        else p = end;
                        break;
                    default:
                        if (p < end) log.Write(c, d[p++]);
                        break;
                }
                if (log.Now > MaxSeconds) break;
            }
            source = "DRO 0.1";
        }
        else if (ver == 2)
        {
            if (d.Length < 0x1A) throw new InvalidDataException("DRO 2.0 머리말이 짧습니다.");
            long pairs = VgmFormat.U32(d, 0x0C);
            int t = d[0x14];
            hw = t == 0 ? OplHw.Opl2 : t == 1 ? OplHw.DualOpl2 : t == 2 ? OplHw.Opl3 : OplHw.Unknown;
            if (d[0x15] != 0 || d[0x16] != 0) throw new InvalidDataException("DRO 2.0: 이 형식 · 압축은 모릅니다 (형식 " + d[0x15] + ", 압축 " + d[0x16] + ").");
            int shortCode = d[0x17], longCode = d[0x18], n = d[0x19];
            if (0x1A + n > d.Length) throw new InvalidDataException("DRO 2.0 코드 표가 잘렸습니다.");
            var map = new byte[n];
            Array.Copy(d, 0x1A, map, 0, n);
            int p = 0x1A + n;
            long end = Math.Min(d.Length, p + pairs * 2);
            while (p + 1 < end)
            {
                int code = d[p], val = d[p + 1];
                p += 2;
                if (code == shortCode) log.Now += (val + 1) / 1000.0;
                else if (code == longCode) log.Now += ((val + 1) << 8) / 1000.0;
                else
                {
                    log.Chip = (code & 0x80) != 0 ? 1 : 0;
                    int idx = code & 0x7F;
                    if (idx < n) log.Write(map[idx], val);
                }
                if (log.Now > MaxSeconds) break;
            }
            source = "DRO 2.0";
        }
        else throw new InvalidDataException("DRO 판을 모릅니다 (0x" + ver.ToString("X8") + ").");
        return OplLogToVgm(log, hw, title, source, "DOSBox");
    }

    public static VgmSong LoadRaw(string path)
    {
        var s = ReadRaw(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
        s.FilePath = path;
        return s;
    }

    /// <summary>
    /// Rdos Raw OPL Capture (.raw, "RAWADATA"): 0x08 클럭 값(16비트) → 틱 = 1193180Hz / 클럭. 이어서 (값, 명령) 2바이트씩:
    /// 명령 0 = 이 틱을 끝내고 값만큼 틱 기다림 (0 = 256), 명령 2 = 값 0이면 다음 2바이트가 새 클럭 · 값 1/2면 칩 0/1,
    /// 명령 0xFF 값 0xFF = 끝, 그 밖 = OPL 레지스터(명령)에 값. AdPlug raw.cpp와 같은 해석.
    /// </summary>
    public static VgmSong ReadRaw(byte[] d, string title = "")
    {
        if (d.Length < 10 || Encoding.ASCII.GetString(d, 0, 8) != "RAWADATA") throw new InvalidDataException("RAW 파일이 아닙니다 (머리말 RAWADATA가 없음).");
        int speed = d[8] | (d[9] << 8);
        var log = new OplLog();
        int n = (d.Length - 10) / 2;
        for (int i = 0; i < n; i++)
        {
            int param = d[10 + 2 * i], cmd = d[11 + 2 * i];
            double tick = (speed == 0 ? 0xFFFF : speed) / 1193180.0;
            if (cmd == 0) log.Now += tick * (param == 0 ? 256 : param);
            else if (cmd == 2)
            {
                if (param == 0)
                {
                    if (++i >= n) break;
                    speed = d[10 + 2 * i] | (d[11 + 2 * i] << 8);
                }
                else log.Chip = param == 2 ? 1 : 0;
            }
            else if (cmd == 0xFF) { if (param == 0xFF) break; }
            else log.Write(cmd, param);
            if (log.Now > MaxSeconds) break;
        }
        return OplLogToVgm(log, OplHw.Unknown, title, "RAW (Rdos)", "PC");
    }

    // ================================================================ HSC

    private static readonly int[] HscNotes = { 0x16B, 0x181, 0x198, 0x1B0, 0x1CA, 0x1E5, 0x202, 0x220, 0x241, 0x263, 0x287, 0x2AE };
    private static readonly int[] OpTable = { 0x00, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x10, 0x11, 0x12 };

    public static VgmSong LoadHsc(string path)
    {
        var s = ReadHsc(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
        s.FilePath = path;
        return s;
    }

    public static VgmSong ReadHsc(byte[] d, string title = "")
    {
        const int InsBytes = 128 * 12, OrderAt = InsBytes, PatAt = InsBytes + 51, PatSize = 64 * 9 * 2;
        if (d.Length < PatAt + PatSize) throw new InvalidDataException("HSC 파일이 너무 짧습니다.");
        int patterns = Math.Min(50, (d.Length - PatAt) / PatSize);
        var ins = new byte[128, 12];
        for (int i = 0; i < 128; i++)
        {
            for (int k = 0; k < 12; k++) ins[i, k] = d[i * 12 + k];
            ins[i, 2] ^= (byte)((ins[i, 2] & 0x40) << 1);
            ins[i, 3] ^= (byte)((ins[i, 3] & 0x40) << 1);
            ins[i, 11] >>= 4;
        }
        var order = new int[51];
        for (int i = 0; i < 51; i++)
        {
            int v = d[OrderAt + i];
            order[i] = (v & 0x7F) > 0x31 || (v & 0x7F) >= patterns ? 0xFF : v;
        }

        var o = new Out();
        int[] chIns = new int[9], chFreq = new int[9], chSlide = new int[9], adlFreq = new int[9];
        int songPos = 0, pattPos = 0, pattBreak = 0, speed = 2, del = 1, fadein = 0, bd = 0;
        bool mode6 = false, songEnd = false;

        void SetVolume(int ch, int volc, int volm)
        {
            int i = chIns[ch], op = OpTable[ch];
            o.Reg(0x43 + op, volc | (ins[i, 2] & ~63));
            if ((ins[i, 8] & 1) != 0) o.Reg(0x40 + op, volm | (ins[i, 3] & ~63));
            else o.Reg(0x40 + op, ins[i, 3]);
        }
        void SetInstr(int ch, int i)
        {
            i &= 127;
            int op = OpTable[ch];
            chIns[ch] = i;
            o.Reg(0xB0 + ch, 0);
            o.Reg(0xC0 + ch, ins[i, 8]);
            o.Reg(0x23 + op, ins[i, 0]); o.Reg(0x20 + op, ins[i, 1]);
            o.Reg(0x63 + op, ins[i, 4]); o.Reg(0x60 + op, ins[i, 5]);
            o.Reg(0x83 + op, ins[i, 6]); o.Reg(0x80 + op, ins[i, 7]);
            o.Reg(0xE3 + op, ins[i, 9]); o.Reg(0xE0 + op, ins[i, 10]);
            SetVolume(ch, ins[i, 2] & ~0xC0, ins[i, 3] & ~0xC0);
        }
        void SetFreq(int ch, int f)
        {
            adlFreq[ch] = (adlFreq[ch] & ~3) | ((f >> 8) & 3);
            o.Reg(0xA0 + ch, f & 0xFF);
            o.Reg(0xB0 + ch, adlFreq[ch]);
        }

        o.Reg(0x01, 32); o.Reg(0x08, 128); o.Reg(0xBD, 0);
        for (int c = 0; c < 9; c++) SetInstr(c, c);

        const double Tick = 1 / 18.2;
        while (!songEnd && o.Now < MaxSeconds)
        {
            o.Advance(Tick);
            if (--del > 0) continue;
            if (fadein > 0) fadein--;
            int pattnr = order[songPos];
            if (pattnr >= 0xB2) { songEnd = true; break; }
            if ((pattnr & 128) != 0) { songPos = pattnr & 127; pattPos = 0; pattnr = order[songPos]; songEnd = true; break; }
            int off = PatAt + pattnr * PatSize + pattPos * 9 * 2;
            for (int ch = 0; ch < 9; ch++)
            {
                int note = off + ch * 2 + 1 < d.Length ? d[off + ch * 2] : 0;
                int effect = off + ch * 2 + 1 < d.Length ? d[off + ch * 2 + 1] : 0;
                if ((note & 128) != 0) { SetInstr(ch, effect); continue; }
                int effOp = effect & 0x0F, inst = chIns[ch];
                if (note != 0) chSlide[ch] = 0;
                switch (effect & 0xF0)
                {
                    case 0x00:
                        switch (effOp) { case 1: pattBreak++; break; case 3: fadein = 31; break; case 5: mode6 = true; break; case 6: mode6 = false; break; }
                        break;
                    case 0x10: case 0x20:
                        if ((effect & 0x10) != 0) { chFreq[ch] += effOp; chSlide[ch] += effOp; }
                        else { chFreq[ch] -= effOp; chSlide[ch] -= effOp; }
                        if (note == 0) SetFreq(ch, chFreq[ch]);
                        break;
                    case 0x60: o.Reg(0xC0 + ch, (ins[inst, 8] & 1) + (effOp << 1)); break;
                    case 0xA0: o.Reg(0x43 + OpTable[ch], (effOp << 2) | (ins[inst, 2] & ~63)); break;
                    case 0xB0: o.Reg(0x40 + OpTable[ch], (effOp << 2) | (ins[inst, 3] & ~63)); break;
                    case 0xC0:
                        o.Reg(0x43 + OpTable[ch], (effOp << 2) | (ins[inst, 2] & ~63));
                        if ((ins[inst, 8] & 1) != 0) o.Reg(0x40 + OpTable[ch], (effOp << 2) | (ins[inst, 3] & ~63));
                        break;
                    case 0xD0: pattBreak++; songPos = effOp; songEnd = true; break;
                    case 0xF0: speed = effOp; del = ++speed; break;
                }
                if (fadein > 0) SetVolume(ch, fadein * 2, fadein * 2);
                if (note == 0) continue;
                note--;
                if (note == 0x7E || ((note / 12) & ~7) != 0) { adlFreq[ch] &= ~32; o.Reg(0xB0 + ch, adlFreq[ch]); continue; }
                int okt = ((note / 12) & 7) << 2;
                int fnr = HscNotes[note % 12] + ins[inst, 11] + chSlide[ch];
                chFreq[ch] = fnr;
                adlFreq[ch] = !mode6 || ch < 6 ? okt | 32 : okt;
                o.Reg(0xB0 + ch, 0);
                SetFreq(ch, fnr);
                if (mode6)
                {
                    switch (ch)
                    {
                        case 6: o.Reg(0xBD, bd & ~16); bd |= 48; break;
                        case 7: o.Reg(0xBD, bd & ~1); bd |= 33; break;
                        case 8: o.Reg(0xBD, bd & ~2); bd |= 34; break;
                    }
                    o.Reg(0xBD, bd);
                }
            }
            del = speed;
            if (pattBreak > 0)
            {
                pattPos = 0; pattBreak = 0;
                songPos = (songPos + 1) % 50;
                if (songPos == 0) songEnd = true;
            }
            else
            {
                pattPos = (pattPos + 1) & 63;
                if (pattPos == 0) { songPos = (songPos + 1) % 50; if (songPos == 0) songEnd = true; }
            }
        }
        for (int c = 0; c < 9; c++) o.Reg(0xB0 + c, adlFreq[c] & ~32);
        o.Advance(1.0);
        return o.Finish(title, "HSC-Tracker");
    }

    // ================================================================ CMF

    public static VgmSong LoadCmf(string path)
    {
        var s = ReadCmf(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
        s.FilePath = path;
        return s;
    }

    // OPL 채널의 오퍼레이터 (모듈레이터) 위치
    private static readonly int[] ModOp = { 0x00, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x10, 0x11, 0x12 };
    // F-number (블록 4 기준 C ~ B, 49716Hz)
    private static readonly int[] FnumTab = { 0x157, 0x16B, 0x181, 0x198, 0x1B0, 0x1CA, 0x1E5, 0x202, 0x220, 0x241, 0x263, 0x287 };

    private static int U16(byte[] d, int o) { return o + 1 < d.Length ? d[o] | (d[o + 1] << 8) : 0; }

    private static string CString(byte[] d, int at)
    {
        if (at <= 0 || at >= d.Length) return "";
        int e = at;
        while (e < d.Length && d[e] != 0) e++;
        return Encoding.ASCII.GetString(d, at, e - at).Trim();
    }

    /// <summary>
    /// 머리말: "CTMF", 판, 악기 위치, 음악 위치, 4분음표당 틱, 초당 틱, 제목 · 작곡가 · 메모 위치, 채널 사용 16, 악기 수, 기본 템포.
    /// 악기는 16바이트 (SBI 순서: 20 23 40 43 60 63 80 83 E0 E3 C0).
    /// </summary>
    public static VgmSong ReadCmf(byte[] d, string fallbackTitle = "")
    {
        if (d.Length < 0x28 || Encoding.ASCII.GetString(d, 0, 4) != "CTMF") throw new InvalidDataException("CMF 파일이 아닙니다 (머리말 CTMF가 없음).");
        int version = U16(d, 4);
        int insAt = U16(d, 6), musicAt = U16(d, 8), ticksPerSec = Math.Max(1, U16(d, 0x0C));
        string title = CString(d, U16(d, 0x0E)), author = CString(d, U16(d, 0x10)), notes = CString(d, U16(d, 0x12));
        int insCount = version >= 0x0101 ? U16(d, 0x24) : d[0x24];
        var bank = new byte[Math.Max(1, insCount), 16];
        for (int i = 0; i < insCount; i++)
            for (int k = 0; k < 16; k++) bank[i, k] = insAt + i * 16 + k < d.Length ? d[insAt + i * 16 + k] : (byte)0;

        var o = new Out();
        o.W.Author = author; o.W.Notes = notes;
        var program = new int[16];
        var chNote = new int[16];
        var loaded = new int[9];
        var drumLoaded = new int[5];
        for (int i = 0; i < 9; i++) loaded[i] = -1;
        for (int i = 0; i < 5; i++) drumLoaded[i] = -1;
        bool rhythm = false;
        int bd = 0, transpose = 0;
        var b0 = new int[9];
        for (int i = 0; i < 16; i++) chNote[i] = -1;

        // 리듬 채널 (MIDI 12~16 = 0부터 11~15): BD SD TOM CY HH → OPL 슬롯 · BD 비트
        int[] drumBit = { 0x10, 0x08, 0x04, 0x02, 0x01 };
        int[] drumOp = { -1, 0x14, 0x12, 0x15, 0x11 };   // SD = 채널 8 캐리어, TOM = 채널 9 모듈레이터, CY = 채널 9 캐리어, HH = 채널 8 모듈레이터

        void LoadIns(int ch, int insNo)
        {
            if (insCount == 0) return;
            if (insNo < 0 || insNo >= insCount) insNo = 0;
            int m = ModOp[ch];
            o.Reg(0x20 + m, bank[insNo, 0]); o.Reg(0x23 + m, bank[insNo, 1]);
            o.Reg(0x40 + m, bank[insNo, 2]); o.Reg(0x43 + m, bank[insNo, 3]);
            o.Reg(0x60 + m, bank[insNo, 4]); o.Reg(0x63 + m, bank[insNo, 5]);
            o.Reg(0x80 + m, bank[insNo, 6]); o.Reg(0x83 + m, bank[insNo, 7]);
            o.Reg(0xE0 + m, bank[insNo, 8]); o.Reg(0xE3 + m, bank[insNo, 9]);
            o.Reg(0xC0 + ch, bank[insNo, 10]);
        }

        // 슬롯 하나짜리 드럼: 악기의 모듈레이터 칸 값을 그 슬롯에
        void LoadDrum(int op, int insNo)
        {
            if (insCount == 0) return;
            if (insNo < 0 || insNo >= insCount) insNo = 0;
            o.Reg(0x20 + op, bank[insNo, 0]); o.Reg(0x40 + op, bank[insNo, 2]);
            o.Reg(0x60 + op, bank[insNo, 4]); o.Reg(0x80 + op, bank[insNo, 6]);
            o.Reg(0xE0 + op, bank[insNo, 8]);
        }

        void Freq(int ch, int note, bool key)
        {
            note += transpose;
            int nn = Math.Max(0, Math.Min(127, note));
            int block = nn / 12 - 1;                    // MIDI 60 (C4) = 블록 4, F-number 0x157 ≈ 261Hz
            int fnum = FnumTab[nn % 12];
            while (block < 0) { fnum >>= 1; block++; }
            while (block > 7) { fnum = Math.Min(1023, fnum << 1); block--; }
            o.Reg(0xA0 + ch, fnum & 0xFF);
            b0[ch] = (key ? 0x20 : 0) | (block << 2) | (fnum >> 8);
            o.Reg(0xB0 + ch, b0[ch]);
        }

        void NoteOff(int c)
        {
            if (rhythm && c >= 11 && c <= 15) { bd &= ~drumBit[c - 11]; o.Reg(0xBD, bd); return; }
            if (c > 8 || (rhythm && c > 5)) return;
            o.Reg(0xB0 + c, b0[c] & ~0x20);
            b0[c] &= ~0x20;
        }

        void NoteOn(int c, int note, int vel)
        {
            if (vel == 0) { if (chNote[c] == note) { NoteOff(c); chNote[c] = -1; } return; }
            if (rhythm && c >= 11 && c <= 15)
            {
                int k = c - 11;
                if (drumLoaded[k] != program[c])
                {
                    if (k == 0) LoadIns(6, program[c]); else LoadDrum(drumOp[k], program[c]);
                    drumLoaded[k] = program[c];
                }
                if (k == 0) Freq(6, note, false);
                else if (k == 2) Freq(8, note, false);
                else if (k == 1) Freq(7, note, false);
                bd &= ~drumBit[k]; o.Reg(0xBD, bd);
                bd |= drumBit[k]; o.Reg(0xBD, bd);
                return;
            }
            if (c > 8 || (rhythm && c > 5)) return;
            if (loaded[c] != program[c]) { LoadIns(c, program[c]); loaded[c] = program[c]; }
            o.Reg(0xB0 + c, b0[c] & ~0x20);
            Freq(c, note, true);
            chNote[c] = note;
        }

        o.Reg(0x01, 0x20); o.Reg(0x08, 0x00); o.Reg(0xBD, 0x00);
        int p = musicAt, running = 0;
        double secPerTick = 1.0 / ticksPerSec;
        while (p < d.Length && o.Now < MaxSeconds)
        {
            long delta = 0; int b;
            do { if (p >= d.Length) { b = 0; break; } b = d[p++]; delta = (delta << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);
            o.Advance(delta * secPerTick);
            if (p >= d.Length) break;
            int st = d[p];
            if (st >= 0x80) { p++; if (st < 0xF0) running = st; }
            else st = running;
            if (st == 0xFF)
            {
                int type = d[p++];
                long len = 0;
                do { b = p < d.Length ? d[p++] : 0; len = (len << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);
                if (type == 0x2F) break;
                p += (int)len;
                continue;
            }
            if (st == 0xF0 || st == 0xF7)
            {
                long len = 0;
                do { b = p < d.Length ? d[p++] : 0; len = (len << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);
                p += (int)len;
                continue;
            }
            int c = st & 15, kind = st & 0xF0;
            int d1 = p < d.Length ? d[p++] : 0;
            int d2 = kind == 0xC0 || kind == 0xD0 ? 0 : p < d.Length ? d[p++] : 0;
            switch (kind)
            {
                case 0x80: if (chNote[c] == d1 || (rhythm && c >= 11)) { NoteOff(c); chNote[c] = -1; } break;
                case 0x90: NoteOn(c, d1, d2); break;
                case 0xC0: program[c] = d1; break;
                case 0xB0:
                    if (d1 == 0x67) { rhythm = d2 != 0; bd = rhythm ? 0x20 : 0; o.Reg(0xBD, bd); }
                    else if (d1 == 0x68) transpose = d2;
                    else if (d1 == 0x69) transpose = -d2;
                    break;
            }
        }
        for (int ch = 0; ch < 9; ch++) o.Reg(0xB0 + ch, b0[ch] & ~0x20);
        o.Advance(1.0);
        return o.Finish(title.Length > 0 ? title : fallbackTitle, "CMF v" + (version >> 8) + "." + (version & 0xFF));
    }
}
