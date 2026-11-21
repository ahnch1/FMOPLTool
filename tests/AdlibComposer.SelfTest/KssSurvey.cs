using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>진단: --kssurvey 폴더 [zip 수] [초] — zip 안 .kss 곡마다 렌더해 PSG 볼륨 · OPLL 키온 · SCC 볼륨 쓰기를 센다 (hoot kss.cpp와 맞춘 BIOS · PSG 되읽기 전후 비교용).</summary>
public static class KssSurvey
{
    public static int Run(string folder, int count, double seconds)
    {
        var zips = Directory.GetFiles(folder, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(count).ToList();
        int songs = 0, sound = 0;
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception) { continue; }
            foreach (var kss in Directory.GetFiles(dir, "*.kss", SearchOption.AllDirectories))
            {
                byte[] bytes = File.ReadAllBytes(kss);
                var tracks = Kss.Tracks(kss).Select(t => t.Song).Distinct().Take(3).ToList();
                if (tracks.Count == 0) tracks.Add(0);
                foreach (int song in tracks)
                {
                    songs++;
                    string note;
                    bool ok;
                    try
                    {
                        var v = Kss.Render(bytes, song, seconds, 0);
                        var (psg, opll, scc) = Count(v);
                        ok = psg + opll + scc > 0;
                        note = "psg " + psg + " opll " + opll + " scc " + scc;
                    }
                    catch (Exception ex) { ok = false; note = ex.GetType().Name; }
                    if (ok) sound++;
                    Console.WriteLine((ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + Path.GetFileName(kss) + " #" + song + " | " + note);
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine("kss: " + sound + " / " + songs + " songs make sound (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        return 0;
    }

    private static (int psg, int opll, int scc) Count(VgmSong v)
    {
        int psg = 0, opll = 0, scc = 0;
        var d = v.Data;
        int p = v.DataOffset;
        int end = v.DataEnd > 0 ? Math.Min(v.DataEnd, d.Length) : d.Length;
        while (p < end)
        {
            int c = d[p];
            if (c == 0x66) break;
            int len = VgmFormat.CommandLength(d, p);
            if (len <= 0) break;
            if (c == 0xA0 && p + 2 < end && (d[p + 1] & 0x7F) >= 8 && (d[p + 1] & 0x7F) <= 10 && (d[p + 2] & 0x1F) != 0) psg++;
            else if (c == 0x51 && p + 2 < end && d[p + 1] >= 0x20 && d[p + 1] <= 0x28 && (d[p + 2] & 0x10) != 0) opll++;
            else if (c == 0xD2 && p + 3 < end && d[p + 1] == 2 && (d[p + 3] & 0x0F) != 0) scc++;
            p += len;
        }
        return (psg, opll, scc);
    }
}
