using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// S98 (PC-88 · PC-98 · X68000 사운드 로그, 판 1 · 2 · 3): 칩 레지스터 쓰기와 동기(틱) 기록.
/// 읽으면 같은 내용의 VGM(<see cref="VgmSong"/>)으로 바꿔 VGM 재생기로 낸다 (칩 종류마다 한 개까지, 같은 종류 둘째는 건너뜀).
/// <para>머리말: 0x04 · 0x08 = 틱 길이 (분자 · 분모 ms, 0이면 10 / 1000), 0x10 = 태그 위치, 0x14 = 데이터 위치, 0x18 = 되돌아갈 곳,
/// 0x1C = 장치 수 (판 3, 0이면 YM2608 7.9872MHz 하나), 0x20~ = 장치 (종류 · 클럭 · 팬 · 예약 각 4바이트).</para>
/// <para>명령: 0x00~0x7F = 장치 n × 2 + 포트, 레지스터, 값 / 0xFF = 1틱 / 0xFE = 가변 길이 n → n + 2틱 / 0xFD = 끝 (되돌아갈 곳이 있으면 거기로).</para>
/// </summary>
public static class S98Format
{
    public static readonly string[] Extensions = { ".s98" };

    public static bool IsS98(string path)
    {
        string ext = Path.GetExtension(path ?? "");
        return Extensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
    }

    public enum Device { None = 0, Ym2149 = 1, Ym2203 = 2, Ym2612 = 3, Ym2608 = 4, Ym2151 = 5, Ym2413 = 6, Ym3526 = 7, Ym3812 = 8, Ymf262 = 9, Ay8910 = 15, Sn76489 = 16 }

    public static VgmSong Load(string path)
    {
        var v = Read(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
        v.FilePath = path;
        return v;
    }

    public static VgmSong Read(byte[] d, string fallbackTitle = "")
    {
        if (d.Length < 0x20 || d[0] != (byte)'S' || d[1] != (byte)'9' || d[2] != (byte)'8')
            throw new InvalidDataException("S98 파일이 아닙니다 (머리말 'S98'이 없음).");
        int version = d[3] - '0';
        uint num = U32(d, 0x04), den = U32(d, 0x08);
        if (num == 0) num = 10;
        if (den == 0) den = 1000;
        double tickSamples = VgmSong.Rate * (double)num / den;
        int tagAt = (int)U32(d, 0x10), dataAt = (int)U32(d, 0x14), loopAt = (int)U32(d, 0x18);
        if (dataAt <= 0 || dataAt >= d.Length) throw new InvalidDataException("S98 데이터 위치가 파일 밖입니다.");

        // 장치
        var devs = new List<(Device type, double clock)>();
        int count = version >= 2 ? (int)U32(d, 0x1C) : 0;
        for (int i = 0; i < count && 0x20 + i * 16 + 8 <= dataAt; i++)
            devs.Add(((Device)U32(d, 0x20 + i * 16), U32(d, 0x20 + i * 16 + 4)));
        if (devs.Count == 0) devs.Add((Device.Ym2608, 7987200));

        var w = new VgmWriter { System = "PC-88 / PC-98 (S98)" };
        var cmd0 = new byte[devs.Count];
        var cmd1 = new byte[devs.Count];
        var used = new HashSet<Device>();
        var skipped = new List<string>();
        for (int i = 0; i < devs.Count; i++)
        {
            var (type, clock) = devs[i];
            var t = type == Device.Ay8910 ? Device.Ym2149 : type;
            if (used.Contains(t)) { skipped.Add(type + " (둘째)"); continue; }
            if (!Map(type, clock, w, out cmd0[i], out cmd1[i])) { skipped.Add(type.ToString()); continue; }
            used.Add(t);
        }

        // 명령 → VGM
        double owed = 0;
        int p = dataAt;
        bool looped = false;
        while (p < d.Length)
        {
            if (loopAt > 0 && p == loopAt && !looped) { Flush(w, ref owed); w.MarkLoop(); looped = true; }
            int c = d[p++];
            if (c == 0xFF) { owed += tickSamples; continue; }
            if (c == 0xFE)
            {
                int n = 0, shift = 0, b;
                do { if (p >= d.Length) { b = 0; break; } b = d[p++]; n |= (b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0 && shift < 35);
                owed += (n + 2) * tickSamples;
                continue;
            }
            if (c == 0xFD) break;
            if (c < 0x80)
            {
                if (p + 2 > d.Length) break;
                int dev = c >> 1, port = c & 1, r = d[p], val = d[p + 1];
                p += 2;
                if (dev >= devs.Count) continue;
                byte cmd = port == 0 ? cmd0[dev] : cmd1[dev];
                if (cmd == 0) continue;
                Flush(w, ref owed);
                if (cmd == 0x50) w.Write(cmd, val);                 // SN76489: 값만
                else if (cmd == 0xA0) w.Write(cmd, r & 0x0F, val);  // AY
                else w.Write(cmd, r, val);
                continue;
            }
            break;   // 모르는 명령
        }
        Flush(w, ref owed);

        string title = fallbackTitle ?? "";
        var tags = ReadTags(d, tagAt, version);
        if (tags.TryGetValue("title", out var tt) && tt.Length > 0) title = tt;
        w.Title = title;
        if (tags.TryGetValue("game", out var g)) w.Game = g;
        if (tags.TryGetValue("artist", out var a)) w.Author = a;
        if (tags.TryGetValue("year", out var y)) w.Date = y;
        if (tags.TryGetValue("s98by", out var by)) w.Ripper = by;
        if (tags.TryGetValue("system", out var sy)) w.System = sy;
        w.Notes = "S98 v" + version + (skipped.Count > 0 ? " · 내지 않는 장치: " + string.Join(", ", skipped) : "");

        var song = VgmFormat.Read(w.ToArray());
        song.SourceFormat = "S98 v" + version;
        foreach (var s in skipped) song.UnsupportedChips.Add(s);
        return song;
    }

    private static void Flush(VgmWriter w, ref double owed)
    {
        long n = (long)Math.Floor(owed);
        if (n <= 0) return;
        owed -= n;
        w.Wait(n);
    }

    /// <summary>S98 장치 → VGM 칩 명령 (포트 0 · 1)과 머리말 클럭.</summary>
    private static bool Map(Device t, double clock, VgmWriter w, out byte c0, out byte c1)
    {
        c0 = c1 = 0;
        switch (t)
        {
            case Device.Ym2149: case Device.Ay8910:
                w.SetClock(VgmWriter.ClockAy8910, clock > 0 ? clock : 1996800);
                w.SetHeaderByte(0x78, (byte)(t == Device.Ym2149 ? 0x10 : 0x00));
                c0 = c1 = 0xA0; return true;
            case Device.Ym2203: w.SetClock(VgmWriter.ClockYm2203, clock > 0 ? clock : 3993600); c0 = c1 = 0x55; return true;
            case Device.Ym2612: w.SetClock(VgmWriter.ClockYm2612, clock > 0 ? clock : 7670453); c0 = 0x52; c1 = 0x53; return true;
            case Device.Ym2608: w.SetClock(VgmWriter.ClockYm2608, clock > 0 ? clock : 7987200); c0 = 0x56; c1 = 0x57; return true;
            case Device.Ym2151: w.SetClock(VgmWriter.ClockYm2151, clock > 0 ? clock : 4000000); c0 = c1 = 0x54; return true;
            case Device.Ym2413: w.SetClock(VgmWriter.ClockYm2413, clock > 0 ? clock : 3579545); c0 = c1 = 0x51; return true;
            case Device.Ym3526: w.SetClock(VgmWriter.ClockYm3526, clock > 0 ? clock : 3579545); c0 = c1 = 0x5B; return true;
            case Device.Ym3812: w.SetClock(VgmWriter.ClockYm3812, clock > 0 ? clock : 3579545); c0 = c1 = 0x5A; return true;
            case Device.Ymf262: w.SetClock(VgmWriter.ClockYmf262, clock > 0 ? clock : 14318180); c0 = 0x5E; c1 = 0x5F; return true;
            case Device.Sn76489: w.SetClock(VgmWriter.ClockSn76489, clock > 0 ? clock : 3579545); c0 = c1 = 0x50; return true;
        }
        return false;
    }

    /// <summary>태그: 판 3 = "[S98]" 뒤 key=value 줄 (BOM이면 UTF-8, 아니면 Shift-JIS), 판 1 · 2 = 제목 문자열 (Shift-JIS, 0으로 끝).</summary>
    private static Dictionary<string, string> ReadTags(byte[] d, int at, int version)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (at <= 0 || at >= d.Length) return tags;
        int end = at;
        while (end < d.Length && d[end] != 0) end++;
        bool v3 = end - at >= 5 && Encoding.ASCII.GetString(d, at, 5) == "[S98]";
        int from = v3 ? at + 5 : at;
        bool utf8 = from + 3 <= end && d[from] == 0xEF && d[from + 1] == 0xBB && d[from + 2] == 0xBF;
        if (utf8) from += 3;
        string text = (utf8 ? Encoding.UTF8 : KoreanText.ShiftJis).GetString(d, from, end - from);
        if (!v3) { tags["title"] = text.Trim(); return tags; }
        foreach (var line in text.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) tags[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim('\r', ' ');
        }
        return tags;
    }

    private static uint U32(byte[] d, int o) { return o + 4 <= d.Length ? (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)) : 0; }
}
