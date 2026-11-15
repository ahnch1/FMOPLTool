using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Dos;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot pc98dos 항목 → 곡: zip을 풀면 .hoot 항목 파일, 곡 목록 = titlelist, 재생 = PC-98 기계를 스스로 돌리는 칩.</summary>
public static class HootPc98Tests
{
    private static byte[] B(params int[] b) { return b.Select(x => (byte)x).ToArray(); }

    /// <summary>
    /// 시험 드라이버 (COM, 상주): INT 7Fh ← 처리기, INT 21h 31h로 상주. 처리기: 7E0h = 0 (재생)이면 표를 OPN에 써서 채널 1 키온 (알고리즘 7, A4 근처).
    /// </summary>
    private static byte[] TestDriver()
    {
        var b = new byte[0xC0];
        var main = B(0xB8, 0x7F, 0x25, 0xBA, 0x40, 0x01, 0xCD, 0x21, 0xBA, 0x20, 0x00, 0xB8, 0x00, 0x31, 0xCD, 0x21);
        Array.Copy(main, 0, b, 0, main.Length);
        int[] table =
        {
            0x28, 0x00, 0xB0, 0x07,
            0x30, 0x01, 0x34, 0x01, 0x38, 0x01, 0x3C, 0x01,
            0x40, 0x10, 0x44, 0x10, 0x48, 0x10, 0x4C, 0x10,
            0x50, 0x1F, 0x54, 0x1F, 0x58, 0x1F, 0x5C, 0x1F,
            0x60, 0x00, 0x64, 0x00, 0x68, 0x00, 0x6C, 0x00,
            0x70, 0x00, 0x74, 0x00, 0x78, 0x00, 0x7C, 0x00,
            0x80, 0x0F, 0x84, 0x0F, 0x88, 0x0F, 0x8C, 0x0F,
            0xA4, 0x22, 0xA0, 0x6A, 0x28, 0xF0,
        };
        int n = table.Length / 2;
        var h = B(0x60, 0x1E, 0x0E, 0x1F, 0xBA, 0xE0, 0x07, 0xEC, 0x3C, 0x00, 0x75, 0x12,
            0xBE, 0x80, 0x01, 0xB9, n, 0x00,
            0xAC, 0xBA, 0x88, 0x01, 0xEE, 0xAC, 0xBA, 0x8A, 0x01, 0xEE, 0xE2, 0xF4,
            0x1F, 0x61, 0xCF);
        Array.Copy(h, 0, b, 0x40, h.Length);
        for (int i = 0; i < table.Length; i++) b[0x80 + i] = (byte)table[i];
        return b;
    }

    private static string Game(string name, string driver, string type, string archive, string roms, string titles)
    {
        return "\t<game>\n\t\t<name>" + name + "</name>\n\t\t<driver type=\"" + type + "\">" + driver + "</driver>\n\t\t<options>\n\t\t\t<option name=\"clockmul\" value=\"8\"/>\n\t\t</options>\n" +
            "\t\t<romlist archive=\"" + archive + "\">\n" + roms + "\t\t</romlist>\n\t\t<titlelist>\n" + titles + "\t\t</titlelist>\n\t</game>\n";
    }

    private static double Rms(VgmSong v, double seconds, out VgmPlayer p)
    {
        p = new VgmPlayer();
        p.Start(v, 0, null);
        double sum = 0; int n = (int)(seconds * 44100);
        for (int i = 0; i < n; i++) { p.Step(); p.Generate(out double l, out double r); sum += l * l; }
        return Math.Sqrt(sum / n);
    }

    public static void Run(Action<string, bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "fmopl_hoot98_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string oldCache = ZipFolder.CacheBase;
        try
        {
            ZipFolder.CacheBase = Path.Combine(root, "cache");
            Directory.CreateDirectory(Path.Combine(root, "xml_new"));
            Directory.CreateDirectory(Path.Combine(root, "pc98"));
            string zip = Path.Combine(root, "pc98", "tone_98.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var s = z.CreateEntry("DRV.COM").Open()) { var d = TestDriver(); s.Write(d, 0, d.Length); }
                using (var s = z.CreateEntry("SONG.DAT").Open()) { var d = Encoding.ASCII.GetBytes("song"); s.Write(d, 0, d.Length); }
            }
            string roms = "\t\t\t<rom type=\"file\" offset=\"0x10\">SONG.DAT</rom>\n\t\t\t<rom type=\"shell\" offset=\"0\">DRV</rom>\n";
            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<gamelist>\n" +
                Game("[PC-9801] Tone Test (YM2203)", "pc98dos", "opn", "tone_98", roms, "\t\t\t<title code=\"0x10\">Tone A</title>\n\t\t\t<title code=\"0x11\">Tone B</title>\n\t\t\t<range min=\"0x0800\" max=\"0x0801\" extcode=\"0\">SE %02X</range>\n") +
                Game("[PC-9801] Tone Test (MIDI)", "pc98dos", "mpu", "tone_98", roms, "\t\t\t<title code=\"0x10\">Tone A</title>\n") +
                Game("[PC-9801] Tone Test (GS)", "pc98dos", "beep", "tone_98", roms, "\t\t\t<title code=\"0x10\">Tone A</title>\n").Replace("<options>", "<options>\n\t\t\t<option name=\"midiout\" value=\"0\"/>") +
                Game("[X68000] Tone Test", "x68k", "mdx", "tone_98", roms, "\t\t\t<title code=\"0x10\">Tone A</title>\n") +
                "</gamelist>\n";
            File.WriteAllText(Path.Combine(root, "xml_new", "tone.xml"), xml, new UTF8Encoding(true));

            check("hoot archive: listing dir found next to the zip's folder (xml_new)", string.Equals(HootArchive.FindListDir(zip), Path.Combine(root, "xml_new"), StringComparison.OrdinalIgnoreCase), HootArchive.FindListDir(zip) ?? "null");
            string dir = ZipFolder.Extract(zip);
            var hoots = Directory.GetFiles(dir, "*" + HootArchive.Extension);
            check("hoot archive: unzipping writes one .hoot per playable pc98dos entry (not MIDI / midiout / other machines)",
                hoots.Length == 1 && Path.GetFileName(hoots[0]) == "Tone Test (YM2203).hoot", string.Join(", ", hoots.Select(Path.GetFileName)));
            if (hoots.Length == 0) return;
            string hp = hoots[0];
            var tracks = ChipSongs.Tracks(hp);
            check("hoot archive: .hoot lists titlelist songs (range = sound effects left out), shown as a multi-song file",
                ChipSongs.HasTracks(hp) && tracks.Count == 2 && tracks[0].title == "Tone A" && tracks[1].path == hp + "::1", string.Join(" | ", tracks.Select(t => t.path.Substring(hp.Length) + "=" + t.title)));
            // 두 번째 열기: 다시 쓰지 않는다 (표시 파일)
            File.Delete(hp);
            ZipFolder.Extract(zip);
            check("hoot archive: entries written once per unzip (marker)", !File.Exists(hp), "");
            File.WriteAllText(hp, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + xml.Substring(xml.IndexOf("\t<game>"), xml.IndexOf("</game>") + 7 - xml.IndexOf("\t<game>")), new UTF8Encoding(true));

            var v = ChipSongs.Load(hp + "::1");
            double rms = Rms(v, 1, out var p);
            check("hoot pc98dos song: resident test driver plays on hoot interrupt (self-running PC-98 chip, YM2203 tone)",
                rms > 300 && v.ChipSummary.Contains("PC-98") && v.Title == "Tone B" && Math.Abs(v.LengthSeconds - (HootPc98.DefaultSeconds + HootPc98.DefaultFade)) < 0.1,
                "rms " + rms.ToString("0") + ", " + v.ChipSummary + ", " + v.Title + ", " + v.LengthSeconds.ToString("0") + " s");
            var e = HootArchive.Load(hp);
            var m = HootPc98.Boot(DosFileSystem.FromDirectory(dir), e, out var shells);
            m.Play(0x11);
            m.Run((long)(m.CpuHz * 0.1));
            int keyOns = m.Log.Count(x => x.kind == 0 && x.reg == 0x28 && x.val == 0xF0);
            check("hoot pc98dos boot: file roms bound to handles, resident shell → idle loop keeps taking hoot interrupts",
                shells.Count == 1 && shells[0] == DosMachine.ShellResult.Resident && keyOns == 1 && m.HandleOpen(0x10), shells.FirstOrDefault() + ", key-ons " + keyOns);
            var used = VgmPlayer.ScanUsed(v);
            var chip = new Pc98Chip(() => HootPc98.Boot(DosFileSystem.FromDirectory(dir), e), Pc98Machine.Board.Opn, 0x10);
            chip.Skip(0.5);
            check("hoot pc98dos: mixer shows every channel without booting (UI thread), chip itself reports FM 1 as used after running",
                used.Take(VgmPlayer.ChannelsOf(v).Count).All(u => u) && chip.VoicesUsed == 1 && chip.ChannelCount == 7 && chip.ChannelLabel(6) == "BEEP", "voices " + chip.VoicesUsed + ", " + chip.ChannelCount + " ch");
        }
        finally
        {
            ZipFolder.CacheBase = oldCache;
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
        Real(check);
    }

    private static void Real(Action<string, bool, string> check)
    {
        string inc = SopTests.FindDir("_incoming");
        string zip = inc == null ? null : Path.Combine(inc, "Hootarchive", "pc98", "yumenosi_98.zip");
        if (zip == null || !File.Exists(zip) || HootArchive.FindListDir(zip) == null) { Console.WriteLine("SKIP  real hoot pc98dos (yumenosi_98.zip / hoot lists not found)"); return; }
        string root = Path.Combine(Path.GetTempPath(), "fmopl_hoot98r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string oldCache = ZipFolder.CacheBase;
        try
        {
            ZipFolder.CacheBase = root;
            var sw = Stopwatch.StartNew();
            string dir = ZipFolder.Extract(zip);
            long ms = sw.ElapsedMilliseconds;
            var hoots = Directory.GetFiles(dir, "*" + HootArchive.Extension).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string opn = hoots.FirstOrDefault(h => h.Contains("YM2203")), opna = hoots.FirstOrDefault(h => h.Contains("YM2608"));
            check("Real hoot archive: yumenosi_98.zip → .hoot entries for YM2203 and YM2608 (index of hoot lists)",
                opn != null && opna != null && hoots.Length == 2, hoots.Length + " entries, " + ms + " ms incl. list index: " + string.Join(", ", hoots.Select(Path.GetFileName)));
            if (opn == null || opna == null) return;
            foreach (var h in new[] { opn, opna })
            {
                var tracks = ChipSongs.Tracks(h);
                int daydream = tracks.FindIndex(t => t.title == "Daydream");
                sw.Restart();
                var v = ChipSongs.Load(tracks[Math.Max(0, daydream)].path);
                double rms = Rms(v, 6, out _);
                double speed = 6 / sw.Elapsed.TotalSeconds;
                check("Real hoot pc98dos: 夢の精 " + (h == opn ? "PMD (YM2203)" : "PMDB2 (YM2608)") + " 'Daydream' plays in the jukebox path",
                    tracks.Count == 15 && daydream >= 0 && rms > 300 && speed > 1.5, tracks.Count + " songs, rms " + rms.ToString("0") + ", " + speed.ToString("0.0") + "x realtime");
            }
        }
        finally
        {
            ZipFolder.CacheBase = oldCache;
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// 진단: --hoot98survey 폴더 [zip 수] [초] — 폴더의 zip을 무작위로 골라 풀고 (.hoot 항목), 항목마다 첫 곡을 돌려 소리(키온 · BEEP 켬)가 나는지 형식별로 센다.
    /// </summary>
    public static int Survey(string folder, int count, double seconds)
    {
        var zips = Directory.GetFiles(folder, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var rnd = new Random(98);
        zips = zips.OrderBy(_ => rnd.Next()).Take(count).ToList();
        var stats = new System.Collections.Generic.SortedDictionary<string, (int total, int sound, int halted)>();
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception ex) { Console.WriteLine("ZIP?  " + Path.GetFileName(zip) + ": " + ex.Message); continue; }
            foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
            {
                var e = HootArchive.Load(h);
                string key = e.Type.ToLowerInvariant();
                stats.TryGetValue(key, out var st);
                st.total++;
                string note;
                try
                {
                    HootPc98.ToVgm(h, 0, seconds, out var m);
                    int keyOns = m.Log.Count(x => x.kind == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0), beeps = m.Log.Count(x => x.kind == 2 && x.val != 0);
                    bool sound = keyOns > 0 || beeps > 0;
                    if (sound) st.sound++;
                    if (m.Cpu.UnknownOps > 0) st.halted++;
                    note = (sound ? "SOUND " : "quiet ") + "key-ons " + keyOns + ", beeps " + beeps + (m.Cpu.UnknownOps > 0 ? ", unknown op" : "") +
                        (m.UnknownPorts.Count > 0 ? ", ports " + string.Join(" ", m.UnknownPorts.Take(6).Select(p => (p > 0xFFFF ? "w" : "r") + (p & 0xFFFF).ToString("X")) ) : "");
                }
                catch (Exception ex) { note = "ERROR " + ex.Message; }
                stats[key] = st;
                Console.WriteLine(key.PadRight(5) + " " + Path.GetFileName(zip).PadRight(22) + " " + note);
            }
        }
        Console.WriteLine();
        foreach (var kv in stats) Console.WriteLine(kv.Key + ": sound " + kv.Value.sound + "/" + kv.Value.total + (kv.Value.halted > 0 ? ", unknown op " + kv.Value.halted : ""));
        Console.WriteLine(zips.Count + " zips, " + sw.Elapsed.TotalSeconds.ToString("0") + " s");
        return 0;
    }
}
