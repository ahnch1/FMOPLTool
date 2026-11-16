using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Dos;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>
/// hoot PC-98generic (pc98vx · pc98, DOS 없는 PC-98): code를 주소에 놓고 bootcs:bootip에서 시작, 재생 = 요인 0 → bgm을 dataaddr에 → 요인 1,
/// I/O 7D0h~7D7h (dataaddr 설정 · 얻기 · bgm 넣기 · strlist), addressing 1 (세그먼트:오프셋), MPU는 MIDI 판에만.
/// </summary>
public static class HootGenericTests
{
    private static byte[] B(params int[] b) { return b.Select(x => (byte)x).ToArray(); }

    /// <summary>
    /// 시험 스텁 (0060:0000): hoot 인터럽트 끄기 → 스택 · INT 7Fh ← 처리기 → 켜기 → STI · HLT 고리.
    /// 처리기: 요인 1이면 2000:0000의 (레지스터, 값) 짝을 FFh까지 OPN에 쓴다 (bgm = OPN 쓰기 목록).
    /// </summary>
    private static byte[] Stub()
    {
        var main = B(0xFA, 0xBA, 0xE8, 0x07, 0xB0, 0x80, 0xEE, 0x31, 0xC0, 0x8E, 0xD8, 0x8E, 0xD0, 0xBC, 0x00, 0x7C,
            0xC7, 0x06, 0xFC, 0x01, 0x40, 0x00, 0x8C, 0x0E, 0xFE, 0x01, 0xB0, 0x81, 0xEE, 0xFB, 0xF4, 0xEB, 0xFD);
        var handler = B(0x50, 0x52, 0x1E, 0x56, 0xBA, 0xE0, 0x07, 0xEC, 0x3C, 0x01, 0x75, 0x17,
            0xB8, 0x00, 0x20, 0x8E, 0xD8, 0x31, 0xF6,
            0xAC, 0x3C, 0xFF, 0x74, 0x0B, 0xBA, 0x88, 0x01, 0xEE, 0xAC, 0xBA, 0x8A, 0x01, 0xEE, 0xEB, 0xF0,
            0x5E, 0x1F, 0x5A, 0x58, 0xCF);
        var b = new byte[0x40 + handler.Length];
        Array.Copy(main, b, main.Length);
        Array.Copy(handler, 0, b, 0x40, handler.Length);
        return b;
    }

    /// <summary>bgm: 채널 1 음색 (알고리즘 7) + 블록 · F-number + 키온, FFh로 끝.</summary>
    private static byte[] Bgm(int fnumLow)
    {
        int[] t =
        {
            0xB0, 0x07, 0x30, 0x01, 0x34, 0x01, 0x38, 0x01, 0x3C, 0x01, 0x40, 0x10, 0x44, 0x10, 0x48, 0x10, 0x4C, 0x10,
            0x50, 0x1F, 0x54, 0x1F, 0x58, 0x1F, 0x5C, 0x1F, 0x80, 0x0F, 0x84, 0x0F, 0x88, 0x0F, 0x8C, 0x0F,
            0xA4, 0x22, 0xA0, fnumLow, 0x28, 0xF0, 0xFF,
        };
        return t.Select(x => (byte)x).ToArray();
    }

    private static string Game(string name, string driver, string options, string roms, string titles)
    {
        return "\t<game>\n\t\t<name>" + name + "</name>\n\t\t<driver type=\"opn\">" + driver + "</driver>\n\t\t<options>\n" + options + "\t\t</options>\n" +
            "\t\t<romlist archive=\"gen_98\">\n" + roms + "\t\t</romlist>\n\t\t<titlelist>\n" + titles + "\t\t</titlelist>\n\t</game>\n";
    }

    private static string Opt(string n, string v) { return "\t\t\t<option name=\"" + n + "\" value=\"" + v + "\"/>\n"; }

    public static void Run(Action<string, bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "fmopl_hootgen_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string oldCache = ZipFolder.CacheBase;
        try
        {
            ZipFolder.CacheBase = Path.Combine(root, "cache");
            Directory.CreateDirectory(Path.Combine(root, "xml_new"));
            Directory.CreateDirectory(Path.Combine(root, "pc98"));
            string zip = Path.Combine(root, "pc98", "gen_98.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                void Add(string n, byte[] d) { using (var s = z.CreateEntry(n).Open()) s.Write(d, 0, d.Length); }
                Add("STUB.BIN", Stub());
                // 처리기 앞에 NOP 하나, INT 7Fh 주소 칸은 0 (binary 패치로 0x41을 넣는다)
                var s2 = Stub().ToList(); s2.Insert(0x40, 0x90); s2[0x14] = 0; s2[0x15] = 0;
                Add("STUB2.BIN", s2.ToArray());
                Add("BGM01", Bgm(0x6A));
                Add("BGM02", Bgm(0x40));
            }
            string common = Opt("bootcs", "0x0060") + Opt("bootip", "0x0000") + Opt("funcvect", "0x7f") + Opt("filesize", "0x100") + Opt("datamax", "2") + Opt("clockmul", "8");
            string bgms = "\t\t\t<rom type=\"bgm\" offset=\"1\">BGM01</rom>\n\t\t\t<rom type=\"bgm\" offset=\"2\">BGM02</rom>\n\t\t\t<rom type=\"strlist\" offset=\"3\">HELLO</rom>\n";
            string titles = "\t\t\t<title code=\"0x00\">Stop</title>\n\t\t\t<title code=\"0x01\">Tone A</title>\n\t\t\t<title code=\"0x010002\">Tone B</title>\n";
            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<gamelist>\n" +
                Game("[PC-9801] Generic Test (YM2203)", "pc98vx", common + Opt("adressing", "0") + Opt("dataaddr", "0x20000"),
                    "\t\t\t<rom type=\"code\" offset=\"0x00600\">STUB.BIN</rom>\n" + bgms, titles) +
                // 옛 방식 pc98 + addressing 1 (세그먼트 << 16 | 오프셋), 처리기 주소를 binary 패치로 (STUB2: 0 → 0x41)
                Game("[PC-9801] Generic Test Real (YM2203)", "pc98", common + Opt("addressing", "1") + Opt("dataaddr", "0x20000000"),
                    "\t\t\t<rom type=\"code\" offset=\"0x00600000\">STUB2.BIN</rom>\n\t\t\t<rom type=\"binary\" offset=\"0x00600014\">41 00</rom>\n" + bgms, titles) +
                "</gamelist>\n";
            File.WriteAllText(Path.Combine(root, "xml_new", "gen.xml"), xml, new UTF8Encoding(true));

            check("hoot PC-98generic: pc98vx · pc98 FM entries are playable, also as MIDI versions",
                HootArchive.Playable("pc98vx", "opn") && HootArchive.Playable("pc98", "86") && HootArchive.Playable("pc98vx", "beep") && !HootArchive.Playable("pc98vx", "amd98") && HootArchive.MidiPlayable("pc98vx"), "");
            string dir = ZipFolder.Extract(zip);
            var hoots = Directory.GetFiles(dir, "*" + HootArchive.Extension).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            check("hoot PC-98generic: unzipping writes a .hoot for each pc98vx / pc98 entry", hoots.Length == 2, string.Join(", ", hoots.Select(Path.GetFileName)));
            if (hoots.Length != 2) return;
            foreach (var hp in hoots)
            {
                var e = HootArchive.Load(hp);
                bool old = e.Driver == "pc98";
                var m = HootPc98.BootDir(dir, e, out _);
                string tag = old ? "pc98 (addressing 1, binary patch)" : "pc98vx";
                check("hoot PC-98generic " + tag + ": stub boots from bootcs:bootip, hooks INT 7Fh and turns the hoot interrupt on (no DOS)",
                    m.HootArmed && m.PeekWord(0, 0x7F * 4 + 2) == 0x0060 && m.PeekWord(0, 0x7F * 4) == (old ? 0x41 : 0x40) && m.Generic != null && !m.MpuPresent && m.HootDelivered == 0,
                    "armed " + m.HootArmed + ", vec " + m.PeekWord(0, 0x7F * 4 + 2).ToString("X4") + ":" + m.PeekWord(0, 0x7F * 4).ToString("X4"));
                m.Play(0x010002);
                m.Run((long)(m.CpuHz * 0.05));
                var keyOn = m.Log.Where(x => x.kind == 0 && x.reg == 0x28 && x.val == 0xF0).ToList();
                var fnum = m.Log.LastOrDefault(x => x.kind == 0 && x.reg == 0xA0);
                check("hoot PC-98generic " + tag + ": play = cause 0, then bgm (code low 16 bits = 2) loaded at dataaddr, then cause 1 → driver plays it",
                    m.HootDelivered == 2 && m.Generic.Loads == 1 && keyOn.Count == 1 && fnum.val == 0x40 && m.PeekByte(0x2000, 0) == 0xB0,
                    "hoot " + m.HootDelivered + ", loads " + m.Generic.Loads + ", key-ons " + keyOn.Count + ", fnum " + fnum.val.ToString("X2") + ", missing " + string.Join("/", m.MissingFiles) + ", " + m.Describe());
                m.Play(0x05);   // bgm 5 없음: 요인 0만
                m.Run((long)(m.CpuHz * 0.05));
                check("hoot PC-98generic " + tag + ": a code with no bgm only raises cause 0 (nothing loaded)", m.HootDelivered == 3 && m.Generic.Loads == 1, "hoot " + m.HootDelivered + ", loads " + m.Generic.Loads);
                if (old) continue;
                // I/O 7D0h: 10h dataaddr 설정 · 11h 얻기 · 20h bgm 넣기 · 30h 넣기 (인자 블록) · 18h strlist
                void Fn(int f, int p1, int p2, int p3) { m.Out(0x7D2, (byte)p1); m.Out(0x7D3, (byte)(p1 >> 8)); m.Out(0x7D4, (byte)p2); m.Out(0x7D5, (byte)(p2 >> 8)); m.Out(0x7D6, (byte)p3); m.Out(0x7D7, (byte)(p3 >> 8)); m.Out(0x7D0, (byte)f); }
                int P(int i) { return m.In(0x7D2 + i * 2) | (m.In(0x7D3 + i * 2) << 8); }
                Fn(0x10, 0, 0x0100, 0x3000);
                int s10 = m.In(0x7D0);
                Fn(0x11, 0, 0, 0);
                int got = X86Linear(P(2), P(1));
                Fn(0x20, 1, 0x0010, 0x4000);
                int s20 = m.In(0x7D0);
                Fn(0x20, 9, 0x0010, 0x4000);
                int s20bad = m.In(0x7D0);
                // 30h: 인자 블록 5000:0000 = 넣을 곳 5000:0100, 파일 위치 2, 크기 4
                int[] blk = { 0x00, 0x01, 0x00, 0x50, 2, 0, 0, 0, 4, 0, 0, 0 };
                for (int i = 0; i < blk.Length; i++) m.Mem[0x50000 + i] = (byte)blk[i];
                Fn(0x30, 2, 0x0000, 0x5000);
                Fn(0x18, 3, 0x0200, 0x5000);
                string str = Encoding.ASCII.GetString(m.Mem, 0x50200, 6);
                check("hoot PC-98generic I/O 7D0h: set / get dataaddr (10h · 11h), load bgm by number (20h, FFh when missing), load part (30h), strlist (18h)",
                    s10 == 0 && got == 0x30100 && m.Generic.Addr[0] == 0x30100 && s20 == 0 && m.Mem[0x40010] == 0xB0 && s20bad == 0xFF &&
                    m.Mem[0x50100] == 0x30 && m.Mem[0x50103] == 0x01 && m.Mem[0x50104] == 0 && str == "HELLO\0",
                    "status " + s10 + "/" + s20 + "/" + s20bad + ", got " + got.ToString("X5") + ", part " + m.Mem[0x50100].ToString("X2") + ", str " + str.TrimEnd('\0'));
            }
            // Jukebox 길 (스스로 도는 칩)
            var v = ChipSongs.Load(hoots[0] + "::2");
            var p = new VgmPlayer();
            p.Start(v, 0, null);
            double sum = 0;
            for (int i = 0; i < 44100; i++) { p.Step(); p.Generate(out double l, out double r); sum += l * l; }
            double rms = Math.Sqrt(sum / 44100);
            check("hoot PC-98generic: jukebox path plays the entry (self-running PC-98 chip, no DOS label)",
                rms > 300 && v.ChipSummary.Contains("PC-98") && !v.ChipSummary.Contains("DOS") && v.Title == "Tone B", "rms " + rms.ToString("0") + ", " + v.ChipSummary + ", " + v.Title);
            // 제목 없이 range만 있는 항목 = range가 곡 목록
            var re = HootArchive.Parse("<game><name>R</name><driver type=\"opn\">pc98vx</driver><titlelist><range min=\"0x01\" max=\"0x0a\" extcode=\"1\">BGM %02x</range></titlelist></game>");
            check("hoot list: a titlelist with only a range becomes the song list (BGM %02x)", re.Titles.Count == 10 && re.Titles[9].Code == 10 && re.Titles[9].Title == "BGM 0a", string.Join(", ", re.Titles.Take(3)));
        }
        finally
        {
            ZipFolder.CacheBase = oldCache;
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
        Real(check);
    }

    private static int X86Linear(int seg, int off) { return AdlibComposer.Audio.Cpu.X86.Linear(seg, off); }

    /// <summary>실제 hoot pc98vx · pc98 항목: 첫 두 곡이 소리 나고 서로 다르다.</summary>
    private static void Real(Action<string, bool, string> check)
    {
        string inc = SopTests.FindDir("_incoming");
        string pc98 = inc == null ? null : Path.Combine(inc, "Hootarchive", "pc98");
        if (pc98 == null || !Directory.Exists(pc98) || HootArchive.FindListDir(Path.Combine(pc98, "x.zip")) == null) { Console.WriteLine("SKIP  real hoot pc98vx (Hootarchive/pc98 · xml_new not found)"); return; }
        string root = Path.Combine(Path.GetTempPath(), "fmopl_hootgenr_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string oldCache = ZipFolder.CacheBase;
        try
        {
            ZipFolder.CacheBase = root;
            foreach (var (zipName, driver, type, label) in new[] { ("dodoki05_98", "pc98vx", "opn", "D.O. DokiDoki Disk 5 (DOFMD, YM2203)"), ("lmspb_98", "pc98vx", "opna", "Lord Monarch (YM2608, 7D0h 10h)"), ("lodrun98", "pc98", "opn", "Lode Runner (pc98, auto EOI)") })
            {
                string zip = Path.Combine(pc98, zipName + ".zip");
                if (!File.Exists(zip)) { Console.WriteLine("SKIP  real hoot " + zipName + " (zip not found)"); continue; }
                string dir = ZipFolder.Extract(zip);
                string hp = Directory.GetFiles(dir, "*" + HootArchive.Extension).FirstOrDefault(h => { var e = HootArchive.Load(h); return e.Driver == driver && e.Type == type && !HootPc98.IsMidiEntry(e); });
                if (hp == null) { check("Real hoot " + driver + ": " + label + " has a .hoot entry", false, "none"); continue; }
                var entry = HootArchive.Load(hp);
                var music = ChipSongs.Tracks(hp).Where((t, i) => entry.Roms.Any(r => r.Type == "bgm" && r.Offset == (entry.Titles[i].Code & 0xFFFF))).Take(2).ToList();
                var sw = Stopwatch.StartNew();
                double[] rms = new double[2];
                for (int k = 0; k < Math.Min(2, music.Count); k++)
                {
                    var v = ChipSongs.Load(music[k].path);
                    var p = new VgmPlayer();
                    p.Start(v, 0, null);
                    double sum = 0; int n = 44100 * 4;
                    for (int i = 0; i < n; i++) { p.Step(); p.Generate(out double l, out double r); sum += l * l; }
                    rms[k] = Math.Sqrt(sum / n);
                }
                double speed = 8 / sw.Elapsed.TotalSeconds;
                check("Real hoot " + driver + ": " + label + " — two songs play (" + string.Join(" · ", music.Select(t => t.title)) + ")",
                    music.Count == 2 && rms[0] > 200 && rms[1] > 200 && Math.Abs(rms[0] - rms[1]) > 1 && speed > 1.5,
                    "rms " + rms[0].ToString("0") + " / " + rms[1].ToString("0") + ", " + speed.ToString("0.0") + "x realtime");
            }
        }
        finally
        {
            ZipFolder.CacheBase = oldCache;
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }
}
