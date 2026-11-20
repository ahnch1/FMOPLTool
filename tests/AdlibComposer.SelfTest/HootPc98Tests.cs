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
        check("hoot pc9821dos: playable as PC-98 DOS (opn · opna · 86), MIDI out too", HootArchive.Playable("pc9821dos", "opn") && HootArchive.Playable("pc9821dos", "86") && !HootArchive.Playable("pc9821dos", "soundblaster16") && HootArchive.MidiPlayable("pc9821dos"), "");
        var m86 = new Pc98Machine(new DosFileSystem(), Pc98Machine.Board.Opna, 8);
        m86.Out(0xA468, 0xB5);
        check("PC-98 86 board PCM ports: A468h latch (IRQ flag bit4 reads 0), A466h bit0 only", m86.In(0xA468) == 0xA5 && (m86.In(0xA466) & 0xFE) == 0 && HootPc98.Is386(new HootArchive.Entry { Driver = "pc9821dos" }), m86.In(0xA468).ToString("X2"));
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
                Game("[PC-9801] Shared Driver Test (YM2203)", "pc98dos", "opn", "shared_98", roms, "\t\t\t<title code=\"0x10\">Shared</title>\n") +
                Game("[X68000] Tone Test", "x68k", "mdx", "tone_98", roms, "\t\t\t<title code=\"0x10\">Tone A</title>\n") +
                "</gamelist>\n";
            File.WriteAllText(Path.Combine(root, "xml_new", "tone.xml"), xml, new UTF8Encoding(true));

            // 드라이버(DRV.COM)가 없는 zip: 같은 폴더의 다른 zip(tone_98)에서 가져온다
            string sharedZip = Path.Combine(root, "pc98", "shared_98.zip");
            using (var z = ZipFile.Open(sharedZip, ZipArchiveMode.Create))
            using (var s = z.CreateEntry("SONG.DAT").Open()) { var d = Encoding.ASCII.GetBytes("song"); s.Write(d, 0, d.Length); }
            check("hoot archive: listing dir found next to the zip's folder (xml_new)", string.Equals(HootArchive.FindListDir(zip), Path.Combine(root, "xml_new"), StringComparison.OrdinalIgnoreCase), HootArchive.FindListDir(zip) ?? "null");
            string dir = ZipFolder.Extract(zip);
            var hoots = Directory.GetFiles(dir, "*" + HootArchive.Extension).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            check("hoot archive: unzipping writes one .hoot per playable entry (pc98dos FM, pc98dos MIDI version; not mpu type / other machines)",
                hoots.Select(Path.GetFileName).SequenceEqual(new[] { "Tone Test (GS).hoot", "Tone Test (YM2203).hoot" }), string.Join(", ", hoots.Select(Path.GetFileName)));
            string hp = hoots.FirstOrDefault(x => x.EndsWith("(YM2203).hoot"));
            if (hp == null) return;
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
            // hoot 트리에서 고른 항목: 같은 내용이면 있던 파일, 다르면 (2)
            string game = xml.Substring(xml.IndexOf("	<game>"), xml.IndexOf("</game>") + 7 - xml.IndexOf("	<game>"));
            string w1 = HootArchive.WriteEntry(dir, game, "tone_98"), w2 = HootArchive.WriteEntry(dir, game.Replace("Tone B", "Tone C"), "tone_98");
            check("hoot tree entry: WriteEntry reuses the same entry's .hoot, numbers a different one with the same name",
                w1 == hp && Path.GetFileName(w2) == "Tone Test (YM2203) (2).hoot" && ChipSongs.Tracks(w2)[1].title == "Tone C", Path.GetFileName(w1) + ", " + Path.GetFileName(w2));
            File.Delete(w2);
            var e = HootArchive.Load(hp);
            var m = HootPc98.BootDir(dir, e, out var shells);
            m.Play(0x11);
            m.Run((long)(m.CpuHz * 0.1));
            int keyOns = m.Log.Count(x => x.kind == 0 && x.reg == 0x28 && x.val == 0xF0);
            check("hoot pc98dos boot: file roms bound to handles, resident shell → idle loop keeps taking hoot interrupts",
                shells.Count == 1 && shells[0] == DosMachine.ShellResult.Resident && keyOns == 1 && m.HandleOpen(0x10), shells.FirstOrDefault() + ", key-ons " + keyOns);
            string sdir = ZipFolder.Extract(sharedZip);
            var sh = Directory.GetFiles(sdir, "*" + HootArchive.Extension).FirstOrDefault();
            var sm = sh == null ? null : HootPc98.BootDir(sdir, HootArchive.Load(sh), out var sshells);
            if (sm != null) { sm.Play(0x10); sm.Run((long)(sm.CpuHz * 0.1)); }
            check("hoot pc98dos: a shell program missing from the zip is taken from another zip in the same folder (shared drivers)",
                sm != null && sm.Log.Any(x => x.kind == 0 && x.reg == 0x28 && x.val == 0xF0), sh == null ? "no .hoot" : sm.Log.Count + " writes");
            var used = VgmPlayer.ScanUsed(v);
            var chip = new Pc98Chip(() => HootPc98.BootDir(dir, e, out _), Pc98Machine.Board.Opn, 0x10);
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
        RealFiles(check);
    }

    /// <summary>
    /// 이미 읽는 파일을 hoot 곡 코드로 (8-1): snes spc · msx kss · nes nsf · gb gbs 항목마다 아카이브 zip 하나를 풀어 .hoot이 생기고,
    /// 곡 목록 = titlelist, 첫 두 곡이 소리 나고 서로 다르며 제목 = hoot 목록의 것.
    /// </summary>
    private static void RealFiles(Action<string, bool, string> check)
    {
        string inc = SopTests.FindDir("_incoming");
        string hootDir = inc == null ? null : Path.Combine(inc, "Hootarchive");
        string listDir = hootDir == null ? null : Path.Combine(hootDir, "xml_new");
        if (listDir == null || !Directory.Exists(listDir)) { Console.WriteLine("SKIP  real hoot SPC / KSS / NSF / GBS (Hootarchive/xml_new not found)"); return; }
        var idx = HootArchive.Index(listDir);
        string root = Path.Combine(Path.GetTempPath(), "fmopl_hootf_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string oldCache = ZipFolder.CacheBase;
        try
        {
            ZipFolder.CacheBase = root;
            foreach (var (folder, driver, type) in new[] { ("snes", "snes", "spc"), ("msx", "msx", "kss"), ("nes", "nes", "nsf"), ("gb", "gb", "gbs") })
            {
                string d = Path.Combine(hootDir, folder);
                if (!Directory.Exists(d)) { Console.WriteLine("SKIP  real hoot " + type + " (" + folder + " folder not found)"); continue; }
                string zip = null, hp = null;
                foreach (var z in Directory.GetFiles(d, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (!idx.TryGetValue(Path.GetFileNameWithoutExtension(z), out var gl)) continue;
                    if (!gl.Any(g => g.Contains("type=\"" + type + "\">" + driver))) continue;
                    if (new FileInfo(z).Length > 3_000_000) continue;
                    string dir = ZipFolder.Extract(z);
                    hp = Directory.GetFiles(dir, "*" + HootArchive.Extension).FirstOrDefault(h => { var e = HootArchive.Load(h); return e.Driver == driver && e.Type == type && e.Titles.Count >= 2; });
                    if (hp != null) { zip = z; break; }
                }
                if (hp == null) { Console.WriteLine("SKIP  real hoot " + type + " (no entry with 2+ songs)"); continue; }
                var entry = HootArchive.Load(hp);
                var tracks = ChipSongs.Tracks(hp);
                var sw = Stopwatch.StartNew();
                var v0 = ChipSongs.Load(tracks[0].path);
                var v1 = ChipSongs.Load(tracks[1].path);
                double r0 = Rms(v0, 3, out _), r1 = Rms(v1, 3, out _);
                bool differ = Math.Abs(r0 - r1) > 1 || !v0.Data.Skip(v0.DataOffset).Take(4000).SequenceEqual(v1.Data.Skip(v1.DataOffset).Take(4000));
                check("Real hoot " + driver + " / " + type + ": " + Path.GetFileName(zip) + " → .hoot, titlelist songs play by hoot code (" + entry.Titles[0].Title + " · " + entry.Titles[1].Title + ")",
                    tracks.Count == entry.Titles.Count && r0 > 100 && r1 > 100 && differ && v0.Title == entry.Titles[0].Title && v1.Title == entry.Titles[1].Title && v0.SourceFormat.StartsWith("hoot"),
                    tracks.Count + " songs, rms " + r0.ToString("0") + " / " + r1.ToString("0") + ", " + sw.ElapsedMilliseconds + " ms, " + v1.SourceFormat);
            }
        }
        finally
        {
            ZipFolder.CacheBase = oldCache;
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
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
            check("Real hoot archive: yumenosi_98.zip → .hoot entries for YM2203 · YM2608 · GS · Vermouth (MIDI versions)",
                opn != null && opna != null && hoots.Length == 4 && hoots.Count(HootSongs.IsMidi) == 2, hoots.Length + " entries, " + ms + " ms incl. list index: " + string.Join(", ", hoots.Select(Path.GetFileName)));
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
        HootPc98.Force386 = Environment.GetEnvironmentVariable("HOOT_386") != null;
        var zips = Directory.GetFiles(folder, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var rnd = new Random(98);
        zips = zips.OrderBy(_ => rnd.Next()).Take(count).ToList();
        var stats = new System.Collections.Generic.SortedDictionary<string, (int total, int sound, int halted)>();
        var causes = new System.Collections.Generic.SortedDictionary<string, int>();
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception ex) { Console.WriteLine("ZIP?  " + Path.GetFileName(zip) + ": " + ex.Message); continue; }
            foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
            {
                var e = HootArchive.Load(h);
                if (!e.Driver.Equals("pc98dos", StringComparison.OrdinalIgnoreCase) && !e.Driver.Equals("pc9821dos", StringComparison.OrdinalIgnoreCase) && !HootPc98.IsGeneric(e)) continue;
                if (HootPc98.IsMidiEntry(e)) continue;
                string key = (HootPc98.IsGeneric(e) ? e.Driver.ToLowerInvariant() + " " : "") + e.Type.ToLowerInvariant();
                stats.TryGetValue(key, out var st);
                st.total++;
                string note;
                try
                {
                    var m = HootPc98.BootDir(dir, e, out var shells);
                    int vec = m.FuncVector;
                    bool hooked = m.PeekWord(0, vec * 4 + 2) != DosMachine.StubSeg;
                    int logBoot = m.Log.Count;
                    m.Play(FirstCode(e));
                    m.Run((long)(m.CpuHz * seconds));
                    int keyOns = m.Log.Count(x => x.kind == 0 && ((x.reg == 0x28 && (x.val & 0xF0) != 0) || (x.reg == 0x10 && (x.val & 0xBF) != 0 && (x.val & 0x80) == 0) || (x.reg >= 8 && x.reg <= 10 && (x.val & 0x1F) != 0))), beeps = m.Log.Count(x => x.kind == 2 && x.val != 0);
                    int fmWrites = m.Log.Count(x => x.kind <= 1);
                    bool sound = keyOns > 0 || beeps > 0;
                    if (sound) st.sound++;
                    if (m.Cpu.UnknownOps > 0) st.halted++;
                    string cause = sound ? "sound"
                        : m.Cpu.UnknownOps > 0 ? "unknown-op"
                        : shells.Any(r => r == DosMachine.ShellResult.NotFound) ? "shell-not-found"
                        : shells.Any(r => r == DosMachine.ShellResult.NoMemory) ? "no-memory"
                        : !hooked ? "vector-not-hooked"
                        : m.HootDelivered == 0 ? (m.HootInterruptEnabled ? "hoot-not-delivered(IF)" : "hoot-disabled")
                        : m.UnknownFunctions.Count > 0 ? "unknown-dos"
                        : fmWrites == 0 && m.Log.Count == 0 ? "no-chip-writes"
                        : "writes-no-keyon";
                    causes.TryGetValue(key + " " + cause, out int cn); causes[key + " " + cause] = cn + 1;
                    note = cause.PadRight(22) + " key-ons " + keyOns + ", beeps " + beeps + ", fm " + fmWrites + " (boot " + logBoot + "), hoot " + m.HootDelivered + (m.HootInterruptEnabled ? "" : " off") +
                        ", vec " + vec.ToString("X2") + (hooked ? "" : " stub") + ", shells " + string.Join("/", shells.Select(r => r.ToString().Substring(0, 3))) +
                        (m.UnknownFunctions.Count > 0 ? ", dos " + string.Join(" ", m.UnknownFunctions.Keys.Take(5).Select(k => k.ToString("X")) ) : "") +
                        (m.Cpu.UnknownOps > 0 ? ", op@" + m.Cpu.CS.ToString("X4") + ":" + m.Cpu.IP.ToString("X4") + " " + string.Join(" ", Enumerable.Range(-2, 8).Select(i => m.PeekByte(m.Cpu.CS, (m.Cpu.IP + i) & 0xFFFF).ToString("X2"))) : "") +
                        (m.UnknownPorts.Count > 0 ? ", ports " + string.Join(" ", m.UnknownPorts.Take(8).Select(p => (p > 0xFFFF ? "w" : "r") + (p & 0xFFFF).ToString("X")) ) : "") +
                        " | " + m.ConsoleText.Replace("\r", " ").Replace("\n", " ").Trim().Split(' ').Where(x => x.Length > 0).TakeLast(12).Aggregate("", (a, b) => a + " " + b);
                }
                catch (Exception ex) { note = "ERROR " + ex.Message; }
                stats[key] = st;
                Console.WriteLine(key.PadRight(5) + " " + Path.GetFileName(zip).PadRight(22) + " " + note);
            }
        }
        Console.WriteLine();
        foreach (var kv in stats) Console.WriteLine(kv.Key + ": sound " + kv.Value.sound + "/" + kv.Value.total + (kv.Value.halted > 0 ? ", unknown op " + kv.Value.halted : ""));
        foreach (var kv in causes) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        Console.WriteLine(zips.Count + " zips, " + sw.Elapsed.TotalSeconds.ToString("0") + " s");
        return 0;
    }

    /// <summary>진단에서 돌릴 곡: 첫 제목, PC-98generic은 bgm이 있는 첫 제목 (곡 코드 0 = "음악 멈춤"인 항목이 많다).</summary>
    private static int FirstCode(HootArchive.Entry e)
    {
        if (e.Titles.Count == 0) return 0;
        if (int.TryParse(Environment.GetEnvironmentVariable("HOOT_TITLE"), out int ti)) return e.Titles[Math.Min(Math.Max(0, ti), e.Titles.Count - 1)].Code;   // 진단: 몇째 곡
        if (HootPc98.IsGeneric(e))
            foreach (var t in e.Titles)
                if (e.Roms.Any(r => (r.Type == "bgm" || r.Type == "bgm2") && r.Offset == (t.Code & 0xFFFF))) return t.Code;
        var music = e.Titles.FirstOrDefault(t => t.Title.IndexOf("stop", StringComparison.OrdinalIgnoreCase) < 0 && t.Title.IndexOf("停止", StringComparison.Ordinal) < 0 && !t.Title.EndsWith(": INIT", StringComparison.Ordinal));
        return music.Title != null ? music.Code : e.Titles[0].Code;
    }

    /// <summary>진단: --hoot98trace zip [항목 번호] [초] — 항목을 세우고 첫 곡을 재생하며 1초마다 기계 상태 · 인터럽트 · 포트를 찍는다.</summary>
    public static int TraceEntry(string zip, int which, double seconds)
    {
        string dir = ZipFolder.Extract(zip);
        var hoots = Directory.GetFiles(dir, "*" + HootArchive.Extension).Where(h => { var he = HootArchive.Load(h); return he.Driver == "pc98dos" || he.Driver == "pc9821dos" || HootPc98.IsGeneric(he); }).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (hoots.Count == 0) { Console.WriteLine("no pc98dos entry"); return 1; }
        var e = HootArchive.Load(hoots[Math.Min(which, hoots.Count - 1)]);
        Console.WriteLine(e.Name + "  " + e.Type + "  " + string.Join(", ", e.Options.Select(o => o.Key + "=" + o.Value)));
        foreach (var r in e.Roms.Where(r => r.Type != "file")) Console.WriteLine("  rom " + r.Type + " " + r.Offset + " " + r.Text);
        HootPc98.TraceBoot = true;
        var steps = new System.Collections.Generic.List<string>();
        bool armed = false;
        if (Environment.GetEnvironmentVariable("HOOT_STEP") is string segText)
        {
            int seg = Convert.ToInt32(segText, 16);
            Pc98Machine.DebugStep = mm =>
            {
                if (mm.Cpu.CS != seg || steps.Count > 3000000) return;
                if (!armed && mm.Mpu.Commands.Contains(0xB9)) armed = true;
                if (!armed) return;
                steps.Add(mm.Cpu.IP.ToString("X4") + ":" + string.Join("", Enumerable.Range(0, 6).Select(i => mm.PeekByte(seg, mm.Cpu.IP + i).ToString("X2"))) + " ax" + mm.Cpu.AX.ToString("X4"));
            };
        }
        HootPc98.Force386 = Environment.GetEnvironmentVariable("HOOT_386") != null;
        var m = HootPc98.BootDir(dir, e, out var shells);
        HootPc98.TraceBoot = false;
        Pc98Machine.DebugStep = null;
        if (steps.Count > 0) { var seen = new System.Collections.Generic.HashSet<string>(); foreach (var st in steps) if (seen.Add(st.Substring(0, 4))) Console.WriteLine("  step " + st); }
        Console.WriteLine("shells " + string.Join("/", shells) + " | " + m.Describe());
        Console.WriteLine("boot calls " + string.Join(" ", m.TraceVectors.OrderBy(k => k.Key).Select(k => (k.Key > 0xFFFF ? "int" + ((k.Key >> 8) & 0xFF).ToString("X2") + "/" + (k.Key & 0xFF).ToString("X2") : "irq" + k.Key.ToString("X2")) + ":" + k.Value)));
        Console.WriteLine("boot io " + string.Join(" ", m.TraceIo.Take(260)));
        m.TraceIo.Clear();
        Console.WriteLine("boot ports " + string.Join(" ", m.TracePorts.OrderByDescending(k => k.Value).Take(24).Select(k => (k.Key > 0xFFFF ? "w" : "r") + (k.Key & 0xFFFF).ToString("X") + ":" + k.Value)));
        m.TraceVectors.Clear(); m.TracePorts.Clear();
        Console.WriteLine("console: " + m.ConsoleText.Replace("\r", " ").Replace("\n", " | "));
        m.Trace = true;
        int code = FirstCode(e);
        m.Play(code);
        int log0 = m.Log.Count;
        for (int s = 0; s < seconds; s++)
        {
            m.Run((long)m.CpuHz);
            int keyOns = m.Log.Skip(log0).Count(x => x.kind == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0);
            Console.WriteLine((s + 1) + "s: key-ons " + keyOns + ", writes " + (m.Log.Count - log0) + ", hoot " + m.HootDelivered + (m.HootPending ? " pending" : "") + " | " + m.Describe());
            Console.WriteLine("   vectors " + string.Join(" ", m.TraceVectors.OrderBy(k => k.Key).Select(k => (k.Key > 0xFFFF ? "bios" + ((k.Key >> 8) & 0xFF).ToString("X2") + "/" + (k.Key & 0xFF).ToString("X2") : k.Key.ToString("X2")) + ":" + k.Value)) +
                " | ports " + string.Join(" ", m.TracePorts.OrderByDescending(k => k.Value).Take(14).Select(k => (k.Key > 0xFFFF ? "w" : "r") + (k.Key & 0xFFFF).ToString("X") + ":" + k.Value)) +
                (m.UnknownFunctions.Count > 0 ? " | dos " + string.Join(" ", m.UnknownFunctions.Select(k => k.Key.ToString("X") + ":" + k.Value)) : ""));
            m.TraceVectors.Clear(); m.TracePorts.Clear();
        }
        if (Environment.GetEnvironmentVariable("HOOT_MIDI") != null)
            Console.WriteLine("midi " + string.Join(" ", m.Log.Skip(log0).Where(x => x.kind == 4).Skip(int.Parse(Environment.GetEnvironmentVariable("HOOT_MIDI"))).Take(160).Select(x => x.val.ToString("X2"))));
        // 멈춰 도는 곳: CPU 위치를 여러 번 보아 가장 많은 곳 둘레의 바이트
        var at = new System.Collections.Generic.Dictionary<long, int>();
        for (int i = 0; i < 2000; i++) { m.Run(997); if (m.Cpu.CS != DosMachine.StubSeg) { long k = ((long)m.Cpu.CS << 16) | (uint)m.Cpu.IP; at[k] = at.TryGetValue(k, out int c) ? c + 1 : 1; } }
        foreach (var kv in at.OrderByDescending(x => x.Value).Take(3))
        {
            int cs = (int)(kv.Key >> 16), ip = (int)(kv.Key & 0xFFFF);
            Console.WriteLine("hot " + cs.ToString("X4") + ":" + ip.ToString("X4") + " x" + kv.Value + "  " +
                string.Join(" ", Enumerable.Range(-24, 48).Select(d => m.PeekByte(cs, (ip + d) & 0xFFFF).ToString("X2") + (d == -1 ? " |" : ""))));
        }
        if (m.Cpu.UnknownOps > 0)
        {
            // 모르는 명령에 닿기 전 64명령: 바뀐 흐름 (점프 · 호출)만
            Console.WriteLine("unknown op " + m.Cpu.LastUnknown.ToString("X") + " at " + m.Cpu.CS.ToString("X4") + ":" + m.Cpu.IP.ToString("X4") + ", last jumps (from: bytes → to):");
            foreach (var (fc, fi, tc, ti) in m.Cpu.RecentJumps())
                Console.WriteLine("  " + fc.ToString("X4") + ":" + fi.ToString("X4") + " " + string.Join(" ", Enumerable.Range(0, 6).Select(d => m.PeekByte(fc, (fi + d) & 0xFFFF).ToString("X2"))) + "  → " + tc.ToString("X4") + ":" + ti.ToString("X4"));
        }
        Console.WriteLine("missing files: " + string.Join(", ", m.MissingFiles) + " | console: " + m.ConsoleText.Replace("\r", " ").Replace("\n", " | "));
        Console.WriteLine("MPU commands: " + string.Join(" ", m.Mpu.Commands.Select(c => c.ToString("X2"))) + " | uart " + m.Mpu.Uart + ", bytes " + m.Mpu.BytesOut);
        Console.WriteLine("vec18 = " + m.PeekWord(0, 0x62).ToString("X4") + ":" + m.PeekWord(0, 0x60).ToString("X4") + ", regs AX " + m.Cpu.AX.ToString("X4"));
        return 0;
    }

    /// <summary>
    /// 진단: --hootatsurvey 폴더 [zip 수] [초] — PC/AT DOS (pcatdos) 항목마다 첫 곡을 돌려 OPL 키온 · 스피커가 나는지, 셸 결과 · 모르는 포트 · 못 연 파일.
    /// </summary>
    public static int SurveyAt(string folder, int count, double seconds)
    {
        var zips = Directory.GetFiles(folder, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var rnd = new Random(98);
        zips = zips.OrderBy(_ => rnd.Next()).Take(count).ToList();
        var stats = new System.Collections.Generic.SortedDictionary<string, (int total, int sound)>();
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception ex) { Console.WriteLine("ZIP?  " + Path.GetFileName(zip) + ": " + ex.Message); continue; }
            foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
            {
                var e = HootArchive.Load(h);
                if (!HootPc98.IsPcAt(e)) continue;
                string key = e.Type.ToLowerInvariant();
                stats.TryGetValue(key, out var st);
                st.total++;
                string note;
                try
                {
                    var m = (PcAtMachine)HootPc98.BootDirAny(dir, e, out var shells);
                    int boot = m.Log.Count;
                    int ti = int.TryParse(Environment.GetEnvironmentVariable("HOOT_TITLE"), out int tv) ? Math.Min(tv, Math.Max(0, e.Titles.Count - 1)) : 0;   // 진단: 몇째 곡
                    m.Play(e.Titles.Count > 0 ? e.Titles[ti].Code : 0);
                    m.Run((long)(m.CpuHz * seconds));
                    int keyOns = m.Sound == PcAtMachine.Card.GameBlaster ? m.Log.Count(x => x.kind <= 1 && x.reg <= 5 && x.val != 0)   // SAA1099 음량
                        : m.Sound == PcAtMachine.Card.Tandy ? m.Log.Count(x => x.kind == 0 && (x.val & 0x90) == 0x90 && (x.val & 15) != 15)   // SN76489 감쇠 < 15
                        : m.Log.Count(x => x.kind <= 1 && ((x.reg >= 0xB0 && x.reg <= 0xB8 && (x.val & 0x20) != 0) || (x.reg == 0xBD && (x.val & 0x20) != 0 && (x.val & 0x1F) != 0)));
                    int beeps = m.Log.Count(x => x.kind == 2 && x.val != 0);
                    bool sound = keyOns > 0 || beeps > 0;
                    if (sound) st.sound++;
                    note = (sound ? "SOUND " : "quiet ") + "key-ons " + keyOns + ", beeps " + beeps + ", writes " + m.Log.Count + " (boot " + boot + "), hoot " + m.HootDelivered + (m.HootInterruptEnabled ? "" : " off") +
                        ", vec " + m.FuncVector.ToString("X2") + (m.PeekWord(0, m.FuncVector * 4 + 2) == DosMachine.StubSeg ? " stub" : "") + ", shells " + string.Join("/", shells.Select(r => r.ToString().Substring(0, 3))) +
                        (m.Cpu.UnknownOps > 0 ? ", op@" + m.Cpu.CS.ToString("X4") + ":" + m.Cpu.IP.ToString("X4") + " " + m.PeekByte(m.Cpu.CS, m.Cpu.IP).ToString("X2") : "") +
                        (m.UnknownFunctions.Count > 0 ? ", dos " + string.Join(" ", m.UnknownFunctions.Keys.Select(k => k.ToString("X"))) : "") +
                        (m.MissingFiles.Count > 0 ? ", missing " + string.Join(" ", m.MissingFiles.Distinct().Take(3)) : "") +
                        (m.UnknownPorts.Count > 0 ? ", ports " + string.Join(" ", m.UnknownPorts.Take(8).Select(p => (p > 0xFFFF ? "w" : "r") + (p & 0xFFFF).ToString("X"))) : "") +
                        " | " + m.ConsoleText.Replace("\r", " ").Replace("\n", " ").Trim().Split(' ').Where(x => x.Length > 0).TakeLast(10).Aggregate("", (a, b) => a + " " + b);
                }
                catch (Exception ex) { note = "ERROR " + ex.GetType().Name + " " + ex.Message; }
                stats[key] = st;
                Console.WriteLine(key.PadRight(6) + " " + Path.GetFileName(zip).PadRight(22) + " " + note);
            }
        }
        Console.WriteLine();
        foreach (var kv in stats) Console.WriteLine(kv.Key + ": sound " + kv.Value.sound + "/" + kv.Value.total);
        Console.WriteLine(zips.Count + " zips, " + sw.Elapsed.TotalSeconds.ToString("0") + " s");
        return 0;
    }

    /// <summary>
    /// 진단: --hootmidisurvey 폴더 [zip 수] [초] — MIDI 판 (pc98dos · pcatdos + midiout) 항목마다 첫 곡을 돌려 MPU로 나간 노트 수 · 바이트 · 모르는 포트.
    /// </summary>
    public static int SurveyMidi(string folder, int count, double seconds)
    {
        var zips = Directory.GetFiles(folder, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var rnd = new Random(98);
        zips = zips.OrderBy(_ => rnd.Next()).Take(count).ToList();
        int total = 0, sound = 0;
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception) { continue; }
            foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
            {
                var e = HootArchive.Load(h);
                if (!HootPc98.IsMidiEntry(e)) continue;
                total++;
                string note;
                try
                {
                    var m = HootPc98.BootDirAny(dir, e, out var shells);
                    var hm = (IHootMachine)m;
                    int bootBytes = hm.ChipLog.Count(x => x.kind == 4);
                    hm.Play(FirstCode(e));
                    m.Run((long)(hm.ClockHz * seconds));
                    int noteOns = 0, bytes = 0;
                    var parser = new MidiByteParser { Message = (st, a, b, sx) => { if ((st & 0xF0) == 0x90 && b > 0) noteOns++; } };
                    foreach (var x in hm.ChipLog) if (x.kind == 4) { bytes++; parser.Feed(x.val); }
                    bool ok = noteOns > 0;
                    if (ok) sound++;
                    var ports = m is Pc98Machine pm ? pm.UnknownPorts : ((PcAtMachine)m).UnknownPorts;
                    note = (ok ? "SOUND " : "quiet ") + "notes " + noteOns + ", bytes " + bytes + " (boot " + bootBytes + "), type " + e.Option("midiout_type", 0) + ", shells " + string.Join("/", shells.Select(r => r.ToString().Substring(0, 3))) +
                        (m.Cpu.UnknownOps > 0 ? ", unknown op" : "") + (m.MissingFiles.Count > 0 ? ", missing " + string.Join(" ", m.MissingFiles.Distinct().Take(3)) : "") +
                        (ports.Count > 0 ? ", ports " + string.Join(" ", ports.Take(8).Select(q => (q > 0xFFFF ? "w" : "r") + (q & 0xFFFF).ToString("X"))) : "");
                }
                catch (Exception ex) { note = "ERROR " + ex.GetType().Name + " " + ex.Message; }
                Console.WriteLine((e.Driver + "/" + e.Type).PadRight(16) + " " + Path.GetFileName(zip).PadRight(22) + " " + note);
            }
        }
        Console.WriteLine();
        Console.WriteLine("midi entries: sound " + sound + "/" + total + ", " + zips.Count + " zips, " + sw.Elapsed.TotalSeconds.ToString("0") + " s");
        return 0;
    }

    /// <summary>진단: --hoottree xml폴더 — hoot 트리(제작사 → 장비 → 게임 → 곡)를 만들 자료를 읽는 시간과 단계별 수.</summary>
    public static int TreeStats(string dir)
    {
        var sw = Stopwatch.StartNew();
        var games = HootLists.ReadFolder(dir);
        long ms = sw.ElapsedMilliseconds;
        var machines = games.GroupBy(g => g.Platform.Length > 0 ? g.Platform : g.AliasType.Length > 0 ? g.AliasType : "(기계 모름)").OrderByDescending(x => x.Count()).ToList();
        Console.WriteLine("games " + games.Count + " in " + ms + " ms, makers " + games.Select(g => g.File).Distinct().Count() + ", songs " + games.Sum(g => g.Titles.Count(t => !t.Code.StartsWith("range:"))));
        Console.WriteLine("machines " + machines.Count + ": " + string.Join(" | ", machines.Take(40).Select(m => m.Key + " " + m.Count())));
        return 0;
    }
}
