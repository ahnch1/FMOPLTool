using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>QSound (HLE) 칩 · VGM, hoot 캡콤 CPS-2 (HootCps2): 실제 zip, 진단 조사.</summary>
public static class HootCps2Tests
{
    public static void Run(Action<string, bool, string> check)
    {
        // QSound: 채널 0 = 뱅크 0 (채널 15의 뱅크 레지스터) · 주소 0 · 속도 1000h · 끝 100h · 루프 100h · 볼륨 2000h → 소리 · 되풀이
        var q = new QSound(QSound.StandardClock, 44100);
        var rom = new byte[0x200];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)(sbyte)(i % 32 < 16 ? 100 : -100);
        q.LoadRom(rom, 0);
        q.Write(0, 15 * 8 + 0, 0);   // 채널 0의 뱅크
        q.Write(0, 0x80, 0x20);      // 가운데 팬
        q.Write(0, 1, 0); q.Write(0, 2, 0x1000); q.Write(0, 4, 0x100); q.Write(0, 5, 0x100); q.Write(0, 6, 0x2000);
        int peak = 0;
        for (int i = 0; i < 44100; i++) { q.Generate(out int l, out _); peak = Math.Max(peak, Math.Abs(l)); }
        check("QSound HLE: looping channel sounds after 1 s", peak > 5000 && q.ChannelActive(0), "peak " + peak);
        q.Write(0, 6, 0);
        check("QSound HLE: volume 0 keys off", !q.ChannelActive(0), "");
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockQsound, 4000000);
        w.Write(0xC4, 0x20, 0x00, 0x06); w.Wait(10);
        var v = VgmFormat.Read(w.ToArray());
        check("VGM QSound (B4h) read as playable", v.QsoundClock == 4000000 && v.IsPlayable && v.ChipSummary.Contains("QSound") && !v.UnsupportedChips.Contains("QSound"), v.ChipSummary);
        check("hoot cps2: playable type", HootArchive.Playable("capcom", "cps2") && HootArchive.Playable("capcom", "cps1"), "");
        check("hoot cps1qs · zn: playable type, board", HootArchive.Playable("capcom", "cps1qs") && HootArchive.Playable("capcom", "zn")
            && HootCps2.BoardOf("cps1qs") == HootCps2.Board.Cps1QSound && HootCps2.BoardOf("ZN") == HootCps2.Board.Zn && !HootCps2.Handles("capcom", "cps1"), "");

        // Z80 OpcodeRead: 명령 바이트만 다른 표에서 (자료 = 3E 3E → LD A,3Eh가 아니라, 명령 표의 3Eh 뒤 자료 바이트 = 피연산자)
        var zm = new HootCps2.Machine(new byte[0x10000], new byte[0], HootCps2.Board.Zn, new byte[] { 0x3E, 0x00, 0x76 });
        zm.Rom[0] = 0x00; zm.Rom[1] = 0x5A; zm.Rom[2] = 0x00;
        zm.Cpu.Iff1 = false;
        zm.Cpu.Step(); zm.Cpu.Step();
        check("Z80 OpcodeRead: opcodes from decrypted table, operands from data", zm.Cpu.A == 0x5A && zm.Cpu.Halted, "A " + zm.Cpu.A.ToString("X2"));

        // Kabuki: 열쇠마다 F3 ED 56 31을 거꾸로 만든 ROM → 그 열쇠를 찾고 풀면 F3 ED 56 31
        bool allKeys = true;
        foreach (var k in HootCps2.Kabuki.Keys)
        {
            int[] want = { 0xF3, 0xED, 0x56, 0x31 };
            var enc = new byte[0x100];
            for (int a = 0; a < 4; a++)
                for (int b = 0; b < 256; b++)
                    if (HootCps2.Kabuki.Byte(b, k, (a + k.Addr) & 0xFFFF) == want[a]) { enc[a] = (byte)b; break; }
            var found = HootCps2.Kabuki.Find(enc);
            var ops = HootCps2.Kabuki.Decode(enc, k, 4);
            allKeys &= found.HasValue && found.Value.Name == k.Name && ops[0] == 0xF3 && ops[1] == 0xED && ops[2] == 0x56 && ops[3] == 0x31;
        }
        check("Kabuki: each key found from its encrypted DI · IM 1 · LD SP start", allKeys, "");

        string root = SopTests.FindDir("_incoming");
        string folder = root == null ? null : Path.Combine(root, "Hootarchive", "roms");
        if (folder == null || !Directory.Exists(folder)) { Console.WriteLine("SKIP  hoot cps2 real files (_incoming/Hootarchive/roms not found)"); return; }
        foreach (var arc in new[] { "ssf2t", "mshvsf", "gigawing", "wof", "dino", "sfex", "tgmj" })
        {
            string zip = Path.Combine(folder, arc + ".zip");
            if (!File.Exists(zip)) { Console.WriteLine("SKIP  hoot cps2 " + arc + " (zip not found)"); continue; }
            string dir = ZipFolder.Extract(zip);
            var hoot = Directory.GetFiles(dir, "*" + HootArchive.Extension).Select(HootArchive.Load).FirstOrDefault(x => HootCps2.Handles(x.Driver, x.Type));
            if (hoot == null) { check("hoot cps2 " + arc + ": .hoot entry", false, "none"); continue; }
            var r = (sound: false, note: "");
            int code = 0;
            foreach (var t in hoot.Titles.Take(6)) { code = t.Code; r = Measure(hoot, dir, code, 8, 0); if (r.sound) break; }
            check("hoot cps2 " + arc + ": song 0x" + code.ToString("X2") + " makes sound in 8 s", r.sound, r.note);
        }
    }

    public static (bool sound, string note) Measure(HootArchive.Entry e, string dir, int code, double seconds, int layout)
    {
        var m = HootCps2.Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; }, layout);
        m.Play(code);
        m.RunSeconds(seconds);
        int keyOn = m.Log.Count(x => x.reg < 0x80 && (x.reg & 7) == 6 && x.val != 0);
        bool sound = keyOn >= 8;
        return (sound, m.Kind + (m.KabukiKey.Length > 0 ? " kabuki " + m.KabukiKey : "") + ", key-on " + keyOn + ", writes " + m.Log.Count + ", irq " + m.Irqs + " taken " + m.Taken
            + (m.Kind == HootCps2.Board.Zn ? " nmi " + m.Nmis + " latch " + m.LatchReads : "") + ", PC " + m.Cpu.PC.ToString("X4")
            + (m.Unsupported.Count > 0 ? ", unsupported " + string.Join(" ", m.Unsupported) : "")
            + (m.Missing.Count > 0 ? ", missing " + string.Join(" ", m.Missing.Take(3)) : ""));
    }

    /// <summary>조사할 형식 (환경 변수 HOOT_TYPE=cps2,zn … 로 좁힘).</summary>
    private static string[] Types
    {
        get
        {
            string env = Environment.GetEnvironmentVariable("HOOT_TYPE");
            return string.IsNullOrWhiteSpace(env) ? new[] { "cps2", "cps1qs", "zn" } : env.Split(',').Select(s => s.Trim()).ToArray();
        }
    }

    /// <summary>진단: --hootcps2survey roms폴더 [zip 수] [초] [모양 0|1] — cps2 · cps1qs · zn (HOOT_TYPE으로 좁힘).</summary>
    public static int Survey(string folder, int count, double seconds, int layout)
    {
        string list = HootArchive.FindListDir(Path.Combine(folder, "x.zip"));
        var idx = list != null ? HootArchive.Index(list) : new Dictionary<string, List<string>>();
        var zips = Directory.GetFiles(folder, "*.zip")
            .Where(z => idx.TryGetValue(Path.GetFileNameWithoutExtension(z), out var xs) && xs.Any(x => Types.Any(t => x.Contains("type=\"" + t + "\""))))
            .Where(z => Environment.GetEnvironmentVariable("HOOT_ONLY") == null || Environment.GetEnvironmentVariable("HOOT_ONLY").Split(',').Contains(Path.GetFileNameWithoutExtension(z), StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(count).ToList();
        int total = 0, sound = 0;
        var sw = Stopwatch.StartNew();
        foreach (var zip in zips)
        {
            string dir;
            try { dir = ZipFolder.Extract(zip); } catch (Exception ex) { Console.WriteLine("ZIP?  " + Path.GetFileName(zip) + ": " + ex.Message); continue; }
            foreach (var h in Directory.GetFiles(dir, "*" + HootArchive.Extension))
            {
                var e = HootArchive.Load(h);
                if (!HootCps2.Handles(e.Driver, e.Type) || !Types.Contains(e.Type.Trim().ToLowerInvariant()) || e.Titles.Count == 0) continue;
                total++;
                (bool ok, string note) r = (false, "");
                int code = 0;
                if (Environment.GetEnvironmentVariable("HOOT_SONGS") is string ns && int.TryParse(ns, out int nSongs))
                {
                    // 곡마다: 곡 코드가 정말 곡을 고르는지 (곡마다 쓰기 수 · 첫 키온 채널 값이 달라야 한다)
                    foreach (var t in e.Titles.Take(nSongs))
                    {
                        var s = Measure(e, dir, t.Code, seconds, layout);
                        Console.WriteLine("  0x" + t.Code.ToString("X3") + " " + (s.sound ? "OK  " : "MUTE") + " " + t.Title + " | " + s.note);
                    }
                }
                try { foreach (var t in e.Titles.Take(6)) { code = t.Code; r = Measure(e, dir, t.Code, seconds, layout); if (r.ok) break; } }
                catch (Exception ex) { r = (false, "exception " + ex.GetType().Name + ": " + ex.Message); }
                if (r.ok) sound++;
                Console.WriteLine((r.ok ? "OK    " : "MUTE  ") + Path.GetFileName(zip) + " | " + e.Name + " | 0x" + code.ToString("X2") + " | " + r.note);
            }
        }
        Console.WriteLine();
        Console.WriteLine(string.Join("+", Types) + ": " + sound + " / " + total + " (" + sw.Elapsed.TotalSeconds.ToString("0") + " s)");
        return 0;
    }
}
