using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>hoot MSX 범용 드라이버 (HootMsx): 만든 시작 코드로 규약(포트 02h~07h · 곡 데이터 올리기 · H.TIMI) 시험, 실제 zip 몇 개, 진단 조사.</summary>
public static class HootMsxTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Synthetic(check);
        Real(check);
    }

    /// <summary>
    /// 시작 코드 (crescent_msx의 patch와 같은 모양): H.TIMI = 드라이버, 명령을 기다려 곡 번호를 03h에 써서 데이터를 올리고 재생.
    /// 드라이버 (0x4000) = 곡 데이터 첫 바이트를 PSG 채널 A 볼륨(R8)로, 둘째 바이트를 톤(R0)으로 프레임마다 쓴다.
    /// </summary>
    private static void Synthetic(Action<string, bool, string> check)
    {
        byte[] stub =
        {
            0xF3, 0xED, 0x56, 0x31, 0x80, 0xF3,             // 0400 DI · IM 1 · LD SP,F380
            0x3E, 0xC3, 0x32, 0x9F, 0xFD,                   // 0406 LD A,C3 · LD (FD9F),A
            0x21, 0x00, 0x40, 0x22, 0xA0, 0xFD,             // 040B LD HL,4000 · LD (FDA0),HL
            0xFB,                                           // 0411 EI
            0xDB, 0x02, 0xB7, 0x28, 0xFB,                   // 0412 wait: IN A,(02) · OR A · JR Z,wait
            0xFE, 0x01, 0x20, 0x06,                         // 0417 CP 1 · JR NZ,stop
            0xDB, 0x03, 0xD3, 0x03,                         // 041B IN A,(03) · OUT (03),A
            0x18, 0xF1,                                     // 041F JR wait
            0xAF, 0xD3, 0x02, 0x18, 0xED,                   // 0421 stop: XOR A · OUT (02),A · JR wait
        };
        byte[] driver =
        {
            0x3E, 0x08, 0xD3, 0xA0, 0x3A, 0x00, 0xC0, 0xD3, 0xA1,   // R8 = (C000)
            0x3E, 0x00, 0xD3, 0xA0, 0x3A, 0x01, 0xC0, 0xD3, 0xA1,   // R0 = (C001)
            0x3E, 0x07, 0xD3, 0xA0, 0x3E, 0x3E, 0xD3, 0xA1,         // R7 = 3E (톤 A만)
            0xC9,
        };
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["patch"] = stub, ["DRV.BIN"] = driver, ["S1.BIN"] = new byte[] { 0x0C, 0x40 }, ["S2.BIN"] = new byte[] { 0x0A, 0x80 },
        };
        string xml = "<game><name>[MSX] Test</name><driver type=\"generic\">msx</driver><options>"
            + "<option name=\"mdata_addr\" value=\"0xc000\"/><option name=\"mdata_size\" value=\"0x0100\"/><option name=\"mfile_size\" value=\"0x0100\"/><option name=\"init_pc\" value=\"0x0400\"/>"
            + "</options><romlist archive=\"test_msx\"><rom type=\"code\" offset=\"0x0400\">patch</rom><rom type=\"code\" offset=\"0x4000\">DRV.BIN</rom>"
            + "<rom type=\"bgm\" offset=\"0x00\">S1.BIN</rom><rom type=\"bgm\" offset=\"0x01\">S2.BIN</rom></romlist>"
            + "<titlelist><title code=\"0x00\">one</title><title code=\"0x01\">two</title></titlelist></game>";
        var e = HootArchive.Parse(xml);
        check("hoot msx generic: playable", HootArchive.Playable(e.Driver, e.Type), e.Driver + "/" + e.Type);

        var m = HootMsx.Boot(e, n => files.TryGetValue(n, out var d) ? d : null);
        check("hoot msx generic: boot reaches the command wait loop", m.Waiting && m.Missing.Count == 0, "PC " + m.Cpu.PC.ToString("X4") + ", missing " + m.Missing.Count);
        m.Play(1);
        m.RunSeconds(0.5);
        check("hoot msx generic: OUT 03h loads bgm n at mdata_addr", m.Loads == 1 && m.Ram[0xC000] == 0x0A && m.Ram[0xC001] == 0x80, "loads " + m.Loads + ", C000 = " + m.Ram[0xC000].ToString("X2"));
        int vol = m.Log.Count(x => x.cmd[0] == 0xA0 && x.cmd[1] == 8 && x.cmd[2] == 0x0A);
        check("hoot msx generic: H.TIMI driver runs every frame (~30 in 0.5 s)", vol >= 25 && vol <= 35, vol + " volume writes");
        var v = m.ToVgm(1, 0);
        check("hoot msx generic: VGM with PSG", v.HasPsg && v.IsPlayable, v.ChipSummary);
        // hoot kss.cpp · ssAY8910: BIOS 영역 = RET, PSG 읽기 = 레지스터 값 (R14 = FFh), 인터럽트가 JIFFY를 올림
        m.Out(0xA0, 7); m.Out(0xA1, 0xB8);
        int r7 = m.In(0xA2);
        m.Out(0xA0, 14);
        int r14 = m.In(0xA2);
        check("hoot msx generic: unused BIOS entries are RET (00D8h GTTRIG · 013Eh RDVDP · 0141h), VDP port bytes 0006h/0007h = 98h",
            m.Ram[0x00D5] == 0xAF && m.Ram[0x0138] == 0xDB && m.Ram[0x0180] == 0xC9 && m.Ram[0x0006] == 0x98 && m.Ram[0x0007] == 0x98, m.Ram[0x0180].ToString("X2"));
        check("hoot msx generic: PSG read-back returns the register (R7 = B8h), R14 = FFh (no joystick)", r7 == 0xB8 && r14 == 0xFF, r7.ToString("X2") + " " + r14.ToString("X2"));
        int jiffy = m.Ram[0xFC9E] | m.Ram[0xFC9F] << 8;
        check("hoot msx generic: interrupt handler counts JIFFY (FC9Eh)", jiffy >= 25, jiffy.ToString());
    }

    private static void Real(Action<string, bool, string> check)
    {
        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "msx");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot msx generic real files (_incoming/Hootarchive/msx not found)"); return; }
        foreach (var (arc, what) in new[] { ("crescent_msx", "Alice Soft PSG"), ("kubikiri_msx", "OPLL"), ("dante2_msx", "PSG"), ("sgolveli_msx", "BIOS call before load"), ("herzog_msx", "GTTRIG"), ("ic_msx", "system timer E6h") })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot msx " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).FirstOrDefault(h => { var x = HootArchive.Load(h); return x.Driver == "msx" && x.Type == "generic"; });
            if (hoot == null) { check("hoot msx " + arc + ": .hoot entry", false, "none"); continue; }
            var e = HootArchive.Load(hoot);
            var r = Measure(e, dir, e.Titles[0].Code, 10);
            check("hoot msx " + arc + " (" + what + "): first song makes sound in 10 s", r.sound, r.note);
        }
        // SCC+ 카트리지 (sccp)는 본체 RAM과 따로: kgc3 grascc = GRA.BIN (본체 4000h) + GRASCC.BIN (카트리지, A8h D7h로 고름)
        string zipK = Path.Combine(folder, "kgc3_msx.zip");
        if (File.Exists(zipK))
        {
            string dirK = ZipFolder.Extract(zipK);
            var eK = Directory.GetFiles(dirK, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => x.Driver == "msx" && x.Roms.Any(rm => rm.Type == "sccp") && x.Roms.Any(rm => rm.Type == "code" && rm.Offset == 0x4000));
            if (eK == null) check("hoot msx kgc3: SCC+ cartridge entry", false, "none");
            else
            {
                var rK = Measure(eK, dirK, eK.Titles[0].Code, 10);
                check("hoot msx kgc3 (Nemesis SCC+): sccp cartridge RAM is separate from main RAM 4000h (slot via A8h) → SCC+ sound", rK.sound && !rK.note.Contains("scc 0 "), rK.note);
            }
        }
        // fmbios: archive="winsltn_msx,fmpac_msx" — FMPAC.ROM은 같이 쓰는 zip (fmpac_msx.zip)에서
        string zipF = Path.Combine(folder, "winsltn_msx.zip");
        if (File.Exists(zipF) && File.Exists(Path.Combine(folder, "fmpac_msx.zip")))
        {
            string dirF = ZipFolder.Extract(zipF);
            var eF = Directory.GetFiles(dirF, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => x.Roms.Any(rm => rm.Type == "fmbios"));
            if (eF == null) check("hoot msx winsltn: fmbios entry (archive \"winsltn_msx,fmpac_msx\")", false, "none");
            else
            {
                var rF = Measure(eF, dirF, eF.Titles[0].Code, 10);
                check("hoot msx winsltn (OPLL, fmbios from fmpac_msx.zip): first song makes sound", rF.sound && !rF.note.Contains("opll 0 ") && !rF.note.Contains("missing"), rF.note);
            }
        }
        // FM-PAC 롬: 실기 부팅 때처럼 SLTWRK 작업 영역 · 슬롯 0 확장 (Ryu Kyu = INIOPL 전 MSTOP, Yoshida Konzern = 슬롯 0에서 찾으면 "없음")
        foreach (var (arc, pick) in new[] { ("ryukyu_msx", "(OPLL)"), ("yosikon_msx", "") })
        {
            string zf = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zf) || !File.Exists(Path.Combine(folder, "fmpac_msx.zip"))) continue;
            string df = ZipFolder.Extract(zf);
            var ef = Directory.GetFiles(df, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => x.Driver == "msx" && x.Name.Contains(pick));
            if (ef == null) { check("hoot msx " + arc + ": entry", false, "none"); continue; }
            var rf = Measure(ef, df, ef.Titles[0].Code, 10);
            check("hoot msx " + arc + " (FM-PAC BIOS work area seeded like the ROM's boot init): first song makes sound", rf.sound, rf.note);
        }
        // hoot ds4.cpp (Legacy of the Wizard 롬): 뱅크 7000h · 7800h, IPL, 포트 00h · 01h · 02h
        string zip4 = Path.Combine(folder, "slayer42.zip");
        if (!File.Exists(zip4)) { Console.WriteLine("SKIP  hoot msx slayer42 (zip not found)"); return; }
        string dir4 = ZipFolder.Extract(zip4);
        var e4 = Directory.GetFiles(dir4, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => x.Driver == "msx" && x.Type == "ds4");
        if (e4 == null) { check("hoot msx slayer42: ds4 .hoot entry", false, "none"); return; }
        var silent = new List<string>();
        string firstNote = "";
        foreach (var (code, _) in e4.Titles) { var mr = Measure(e4, dir4, code, 6); if (!mr.sound) { silent.Add(code.ToString("X2")); if (firstNote.Length == 0) firstNote = " (" + mr.cause + ": " + mr.note + ")"; } }
        int ok = e4.Titles.Count - silent.Count;
        check("hoot msx slayer42 (ds4, hoot ds4.cpp): songs make sound", HootArchive.Playable(e4.Driver, e4.Type) && ok >= e4.Titles.Count * 0.8,
            ok + " / " + e4.Titles.Count + (silent.Count > 0 ? ", silent " + string.Join(" ", silent) + firstNote : ""));
    }

    /// <summary>항목 · 곡을 seconds초 돌려 소리가 나는지 (PSG 볼륨 · OPLL 키온 · SCC 볼륨 · MSX-AUDIO 키온).</summary>
    public static (bool sound, string cause, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds)
    {
        var m = HootMsx.Boot(e, HootArchive.Files(e, dir));
        bool waiting = m.Waiting;
        m.Play(code);
        m.RunSeconds(seconds);
        int psg = m.Log.Count(x => x.cmd[0] == 0xA0 && x.cmd[1] >= 8 && x.cmd[1] <= 10 && (x.cmd[2] & 0x1F) != 0);
        int opll = m.Log.Count(x => x.cmd[0] == 0x51 && x.cmd[1] >= 0x20 && x.cmd[1] <= 0x28 && (x.cmd[2] & 0x10) != 0);
        int scc = m.Log.Count(x => x.cmd[0] == 0xD2 && x.cmd[1] == 2 && (x.cmd[3] & 0x0F) != 0);
        int au = m.Log.Count(x => x.cmd[0] == 0x5C && x.cmd[1] >= 0xB0 && x.cmd[1] <= 0xB8 && (x.cmd[2] & 0x20) != 0);
        bool sound = psg + opll + scc + au > 0;
        string cause = sound ? "sound" : m.Missing.Count > 0 ? "missing-file" : !waiting ? "boot-not-waiting" : m.Log.Count == 0 ? "no-chip-writes" : "writes-no-keyon";
        string note = "psg " + psg + " opll " + opll + " scc " + scc + " audio " + au + ", writes " + m.Log.Count + ", loads " + m.Loads + ", PC " + m.Cpu.PC.ToString("X4")
            + (sound ? "" : ", regs " + string.Join(" ", m.Log.GroupBy(x => x.cmd[0].ToString("X2") + ":" + x.cmd[1].ToString("X2")).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key + "x" + g.Count() + "=" + g.Last().cmd[g.Last().cmd.Length - 1].ToString("X2"))))
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : "") + (m.Unsupported.Count > 0 ? ", rom types " + string.Join(" ", m.Unsupported) : "");
        return (sound, cause, note);
    }

    /// <summary>진단: --hootmsxsurvey 폴더 [zip 수] [초] — msx generic 항목마다 첫 곡이 소리를 내는지, 안 나면 까닭별로.</summary>
    public static int Survey(string folder, int count, double seconds)
    {
        var zips = Directory.GetFiles(folder, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var rnd = new Random(88);
        zips = zips.OrderBy(_ => rnd.Next()).Take(count).ToList();
        int total = 0, sound = 0;
        var causes = new SortedDictionary<string, int>();
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception ex) { Console.WriteLine("ZIP?  " + Path.GetFileName(zip) + ": " + ex.Message); continue; }
            foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
            {
                var e = HootArchive.Load(h);
                if (!e.Driver.Equals("msx", StringComparison.OrdinalIgnoreCase) || !e.Type.Equals("generic", StringComparison.OrdinalIgnoreCase) || e.Titles.Count == 0) continue;
                total++;
                (bool ok, string cause, string note) r;
                try
                {
                    r = Measure(e, dir, e.Titles[0].Code, seconds);
                    // 첫 곡이 조용하면 (효과음 · 정지 곡 등) 다음 곡 몇 개도 본다
                    for (int k = 1; !r.ok && k < Math.Min(6, e.Titles.Count); k++)
                    {
                        var r2 = Measure(e, dir, e.Titles[k].Code, seconds);
                        if (r2.sound) r = (true, "sound-later", "title " + (k + 1) + ": " + r2.note);
                    }
                }
                catch (Exception ex) { r = (false, "exception", ex.GetType().Name + ": " + ex.Message); }
                if (r.ok) sound++;
                causes.TryGetValue(r.cause, out int c); causes[r.cause] = c + 1;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Name + " | " + r.cause + " | " + r.note);
            }
        }
        Console.WriteLine();
        Console.WriteLine("msx generic: " + sound + " / " + total + " make sound (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        foreach (var kv in causes) Console.WriteLine("  " + kv.Key + ": " + kv.Value);
        return 0;
    }
}
