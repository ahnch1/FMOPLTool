using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Dos;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>PC/AT 기계 (hoot pcatdos): BIOS 타이머 (INT 08h → 1Ch), AdLib 찾기 (OPL 타이머 상태), SB16 DSP, 스피커, 실제 pcatdos 항목 재생.</summary>
public static class PcAtTests
{
    private static byte[] Com(int[] code, params (int at, int[] data)[] extra)
    {
        int len = code.Length;
        foreach (var (at, d) in extra) len = Math.Max(len, at - 0x100 + d.Length);
        var b = new byte[len];
        for (int i = 0; i < code.Length; i++) b[i] = (byte)code[i];
        foreach (var (at, d) in extra) for (int i = 0; i < d.Length; i++) b[at - 0x100 + i] = (byte)d[i];
        return b;
    }

    public static void Run(Action<string, bool, string> check)
    {
        // INT 1Ch에 매단 처리기 (BIOS INT 08h가 부름): 1초에 18.2번
        var fs = new DosFileSystem();
        fs.Add("T.COM", Com(new[] { 0xB8, 0x1C, 0x25, 0xBA, 0x40, 0x01, 0xCD, 0x21, 0xFB, 0xF4, 0xEB, 0xFD },
            (0x140, new[] { 0x2E, 0xFF, 0x06, 0x60, 0x01, 0xCF })));
        var m = new PcAtMachine(fs, PcAtMachine.Card.AdLib, 4);
        m.Shell("T", 1000);
        int psp = m.LastLoadedPsp;
        m.Run((long)(m.CpuHz * 2));
        int ticks = m.PeekWord(psp, 0x160), bios = m.PeekWord(0x40, 0x6C);
        check("PC/AT BIOS timer: PIT 0 at 18.2 Hz → INT 08h (ticks 0040:006C) → INT 1Ch hook", Math.Abs(ticks - 36.4) <= 1.5 && Math.Abs(bios - ticks) <= 1, ticks + " in 2 s, BIOS " + bios + ", CPU " + (m.CpuHz / 1e6).ToString("0.00") + " MHz");

        // AdLib 찾기: 타이머 지우기 → 상태 0, 타이머 1 = FFh 시작 → 80µs 뒤 상태 C0h
        int Status() { return m.In(0x388) & 0xE0; }
        void W(int r, int v) { m.Out(0x388, (byte)r); m.Out(0x389, (byte)v); }
        W(4, 0x60); W(4, 0x80);
        int s1 = Status();
        W(2, 0xFF); W(4, 0x21);
        m.Run((long)(m.CpuHz * 0.0002));
        int s2 = Status();
        W(4, 0x60); W(4, 0x80);
        check("PC/AT AdLib detection: OPL status 00h after reset, C0h after timer 1 overflow (80 µs)", s1 == 0 && s2 == 0xC0, s1.ToString("X2") + " / " + s2.ToString("X2"));

        // SB16 DSP: 리셋 → AAh, E1h → 4.05
        var sb = new PcAtMachine(new DosFileSystem(), PcAtMachine.Card.SoundBlaster16, 4);
        sb.Out(0x226, 1); sb.Out(0x226, 0);
        bool ready = (sb.In(0x22E) & 0x80) != 0;
        int aa = sb.In(0x22A);
        sb.Out(0x22C, 0xE1);
        int major = sb.In(0x22A), minor = sb.In(0x22A);
        sb.Out(0x222, 0x05); sb.Out(0x223, 0x01);   // OPL3 켜기 (포트 1)
        check("PC/AT Sound Blaster 16: DSP reset → AAh, version 4.05, OPL3 bank-1 writes logged", ready && aa == 0xAA && major == 4 && minor == 5 && sb.Log.Any(x => x.kind == 1 && x.reg == 5 && x.val == 1),
            aa.ToString("X2") + " v" + major + "." + minor.ToString("00"));

        // 스피커: PIT 2 = 1193 (1kHz), 61h bit0 · bit1 켬
        var sp = new PcAtMachine(new DosFileSystem(), PcAtMachine.Card.Beep, 4);
        sp.Out(0x43, 0xB6); sp.Out(0x42, 0xA9); sp.Out(0x42, 0x04); sp.Out(0x61, 0x03);
        check("PC/AT speaker: PIT 2 counter + port 61h → beep log (counter, on)", sp.Log.Any(x => x.kind == 3 && x.val == 0x4A9) && sp.Log.Any(x => x.kind == 2 && x.val == 1), string.Join(" ", sp.Log.Select(x => x.kind + ":" + x.val.ToString("X"))));

        // Game Blaster: 221h 주소 · 220h 데이터 = SAA1099 #1, 223h · 222h = #2 / Tandy: C0h = SN76489, BIOS 모델 바이트 FFh
        var gb = new PcAtMachine(new DosFileSystem(), PcAtMachine.Card.GameBlaster, 4);
        gb.Out(0x221, 0x00); gb.Out(0x220, 0xFF); gb.Out(0x223, 0x1C); gb.Out(0x222, 0x01); gb.Out(0x389, 0x20);
        check("PC/AT Game Blaster: SAA1099 × 2 writes logged (kind 0 · 1), no OPL", gb.Log.Count == 2 && gb.Log[0].kind == 0 && gb.Log[0].reg == 0 && gb.Log[0].val == 0xFF && gb.Log[1].kind == 1 && gb.Log[1].reg == 0x1C,
            string.Join(" ", gb.Log.Select(x => x.kind + ":" + x.reg.ToString("X") + "=" + x.val.ToString("X"))));
        var td = new PcAtMachine(new DosFileSystem(), PcAtMachine.Card.Tandy, 4);
        td.Out(0xC0, 0x90);
        check("PC/AT Tandy: SN76489 at C0h, Tandy BIOS signature", td.Log.Count == 1 && td.Log[0].val == 0x90 && td.PeekByte(0xF000, 0xFFFE) == 0xFF && td.PeekByte(0xF000, 0xC000) == 0x21, "");
        var pair = new AdlibComposer.Audio.Chips.Saa1099Pair();
        pair.Write(1, 0x1C, 1); pair.Write(1, 0x14, 1); pair.Write(1, 0x00, 0xFF); pair.Write(1, 0x08, 0x80); pair.Write(1, 0x10, 4);
        int pk = 0; for (int i = 0; i < 4410; i++) { pair.Generate(out int l, out _); pk = Math.Max(pk, Math.Abs(l)); }
        check("Saa1099Pair: second chip (port 1) sounds, 12 channels", pk > 100 && pair.ChannelCount == 12 && pair.ChannelActive(6), "peak " + pk);

        Midi(check);
        Real(check);
        RealMidi(check);
    }

    /// <summary>
    /// MIDI 판 (8-4): MPU-401 UART · 지능 모드 Want-to-Send, 바이트 → 메시지 (러닝 스테이터스 · SysEx), 합성 드라이버로 hoot pcatdos + midiout 항목 → 살아 있는 MIDI 곡.
    /// </summary>
    private static void Midi(Action<string, bool, string> check)
    {
        var got = new System.Collections.Generic.List<string>();
        var parser = new MidiByteParser { Message = (st, a, b, sx) => got.Add(sx != null ? "SX" + sx.Length : st.ToString("X2") + a.ToString("X2") + b.ToString("X2")) };
        foreach (var x in new[] { 0x90, 0x3C, 0x64, 0x40, 0x00, 0xF8, 0xC1, 0x05, 0xF0, 0x41, 0x10, 0x42, 0xF7, 0xB0, 0x07, 0x7F }) parser.Feed(x);
        check("MIDI byte parser: running status, realtime bytes skipped, program change, SysEx", string.Join(" ", got) == "903C64 904000 C10500 SX5 B0077F", string.Join(" ", got));

        var mpu = new Mpu401();
        var outBytes = new System.Collections.Generic.List<int>();
        mpu.MidiOut = outBytes.Add;
        mpu.WriteCommand(0xFF); int ack1 = mpu.ReadData();
        mpu.WriteCommand(0xD0); mpu.ReadData();
        foreach (var x in new[] { 0x91, 0x40, 0x50, 0x41, 0x00 }) mpu.WriteData(x);   // 지능 모드 Want to Send: 메시지 + 러닝 스테이터스
        mpu.WriteCommand(0x3F); int ack2 = mpu.ReadData();
        mpu.WriteData(0x80); mpu.WriteData(0x40); mpu.WriteData(0x00);
        check("MPU-401: reset / UART ACK FEh, intelligent-mode Want-to-Send message, UART pass-through",
            ack1 == 0xFE && ack2 == 0xFE && mpu.Uart && string.Join(" ", outBytes.Select(x => x.ToString("X2"))) == "91 40 50 41 00 80 40 00", string.Join(" ", outBytes.Select(x => x.ToString("X2"))));

        // 합성 드라이버 (PC/AT COM, 상주): hoot 인터럽트 7Fh → 요인 0이면 MPU UART (3Fh) + 노트 켜기 90 3C 64
        var drv = new byte[0x80];
        var main = new byte[] { 0xB8, 0x7F, 0x25, 0xBA, 0x40, 0x01, 0xCD, 0x21, 0xBA, 0x20, 0x00, 0xB8, 0x00, 0x31, 0xCD, 0x21 };
        Array.Copy(main, drv, main.Length);
        var h = new byte[] { 0x60, 0x1E, 0x0E, 0x1F, 0xBA, 0xE0, 0x07, 0xEC, 0x3C, 0x00, 0x75, 0x13,
            0xBA, 0x31, 0x03, 0xB0, 0x3F, 0xEE, 0xBA, 0x30, 0x03, 0xEC,
            0xB0, 0x90, 0xEE, 0xB0, 0x3C, 0xEE, 0xB0, 0x64, 0xEE, 0x1F, 0x61, 0xCF };
        Array.Copy(h, 0, drv, 0x40, h.Length);
        string root = Path.Combine(Path.GetTempPath(), "fmopl_hootmidi_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string oldCache = ZipFolder.CacheBase;
        try
        {
            ZipFolder.CacheBase = Path.Combine(root, "cache");
            Directory.CreateDirectory(Path.Combine(root, "xml_new"));
            Directory.CreateDirectory(Path.Combine(root, "pc"));
            string zip = Path.Combine(root, "pc", "midi_at.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var st = z.CreateEntry("MDRV.COM").Open()) st.Write(drv, 0, drv.Length);
            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<gamelist>\n\t<game>\n\t\t<name>[PC/AT] Midi Test (MT-32)</name>\n\t\t<driver type=\"beep\">pcatdos</driver>\n" +
                "\t\t<options>\n\t\t\t<option name=\"midiout\" value=\"1\"/>\n\t\t\t<option name=\"midiout_type\" value=\"2\"/>\n\t\t</options>\n" +
                "\t\t<romlist archive=\"midi_at\">\n\t\t\t<rom type=\"shell\" offset=\"0\">MDRV</rom>\n\t\t</romlist>\n" +
                "\t\t<titlelist>\n\t\t\t<title code=\"0x01\">Midi Song</title>\n\t\t</titlelist>\n\t</game>\n</gamelist>\n";
            File.WriteAllText(Path.Combine(root, "xml_new", "test.xml"), xml, new UTF8Encoding(true));
            string dir = ZipFolder.Extract(zip);
            string hp = Directory.GetFiles(dir, "*" + HootArchive.Extension).FirstOrDefault();
            bool isMidi = hp != null && HootSongs.IsMidi(hp + "::0");
            var song = isMidi ? HootSongs.LoadMidi(hp + "::0") : null;
            song?.Extend(HootPc98.MidiTicksPerSecond / 2);
            var note = song?.Events.FirstOrDefault(e => e.IsNoteOn) ?? default;
            check("hoot pcatdos MIDI entry (midiout): .hoot written, live MIDI song (MT-32 by midiout_type 2) gets the driver's MPU note-on near tick 0",
                isMidi && song.Standard == MidiStandard.Mt32 && song.Title == "Midi Song" && note.Data1 == 0x3C && note.Data2 == 0x64 && note.Tick < 50 && song.RenderedTicks >= HootPc98.MidiTicksPerSecond / 2,
                hp == null ? "no .hoot" : (song == null ? "not MIDI" : song.Events.Count + " events, note tick " + note.Tick + ", " + song.FormatName));
            // 재생기가 스스로 채운다
            if (song != null)
            {
                var player = new MidiPlayer(new OplChip(true));
                var fresh = HootSongs.LoadMidi(hp + "::0");
                player.Start(fresh, 0);
                for (int i = 0; i < player.Rate; i++) player.Step();
                check("MIDI player extends a live song as it plays (about 1 s ahead)", fresh.RenderedTicks >= HootPc98.MidiTicksPerSecond && fresh.Events.Any(e => e.IsNoteOn) && player.IsPlaying,
                    "rendered " + fresh.RenderedTicks + " ticks, " + fresh.Events.Count + " events");
            }
        }
        finally
        {
            ZipFolder.CacheBase = oldCache;
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }

    /// <summary>실제 MIDI 판: PC/AT (MPU-401 UART) · PC-98 (M.M.D. = MPU-PC98 지능 모드 · 클럭-투-호스트로 INT 찾기).</summary>
    private static void RealMidi(Action<string, bool, string> check)
    {
        string inc = SopTests.FindDir("_incoming");
        string hoot = inc == null ? null : Path.Combine(inc, "Hootarchive");
        if (hoot == null || !Directory.Exists(Path.Combine(hoot, "xml_new"))) { Console.WriteLine("SKIP  real hoot MIDI (Hootarchive not found)"); return; }
        string root = Path.Combine(Path.GetTempPath(), "fmopl_hootrm_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string oldCache = ZipFolder.CacheBase;
        try
        {
            ZipFolder.CacheBase = root;
            foreach (var (folder, zipName) in new[] { ("pc", "deathbringer_at.zip"), ("pc98", "mime_98.zip") })
            {
                string zip = Path.Combine(hoot, folder, zipName);
                if (!File.Exists(zip)) { Console.WriteLine("SKIP  real hoot MIDI " + zipName); continue; }
                string dir = ZipFolder.Extract(zip);
                string hp = Directory.GetFiles(dir, "*" + HootArchive.Extension).OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault(x => HootSongs.IsMidi(x));
                if (hp == null) { check("Real hoot MIDI " + zipName + ": MIDI entry written", false, "no MIDI .hoot"); continue; }
                var song = HootSongs.LoadMidi(hp + "::0");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                song.Extend(HootPc98.MidiTicksPerSecond * 20);
                int notes = song.Events.Count(e => e.IsNoteOn), programs = song.Events.Count(e => e.Kind == 0xC0);
                check("Real hoot MIDI " + zipName.Replace(".zip", "") + " (" + song.FormatName + ", " + song.Standard + "): notes in the first 20 s (MMD sends a long GS setup first)",
                    notes > 20, notes + " notes, " + programs + " program changes, " + song.Events.Count(e => e.IsSysEx) + " SysEx, " + sw.ElapsedMilliseconds + " ms");
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
        string pc = inc == null ? null : Path.Combine(inc, "Hootarchive", "pc");
        if (pc == null || !Directory.Exists(pc) || HootArchive.FindListDir(Path.Combine(pc, "x.zip")) == null) { Console.WriteLine("SKIP  real hoot pcatdos (Hootarchive/pc not found)"); return; }
        string root = Path.Combine(Path.GetTempPath(), "fmopl_hootat_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string oldCache = ZipFolder.CacheBase;
        try
        {
            ZipFolder.CacheBase = root;
            foreach (var (zipName, type) in new[] { ("7cogold_at.zip", "adlib"), ("", "soundblaster16"), ("sq3_at.zip", "gameblaster"), ("silpheed_at.zip", "tandy"), ("wibarm_at.zip", "tandy") })
            {
                string hp = null;
                var candidates = zipName.Length > 0 ? new[] { Path.Combine(pc, zipName) } : Directory.GetFiles(pc, "*.zip").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var z in candidates)
                {
                    if (!File.Exists(z)) continue;
                    string dir = ZipFolder.Extract(z);
                    hp = Directory.GetFiles(dir, "*" + HootArchive.Extension).FirstOrDefault(h => { var e = HootArchive.Load(h); return e.Driver == "pcatdos" && e.Type == type; });
                    if (hp != null) break;
                }
                if (hp == null) { Console.WriteLine("SKIP  real hoot pcatdos " + type); continue; }
                var tracks = ChipSongs.Tracks(hp);
                var v = ChipSongs.Load(tracks[0].path);
                var p = new VgmPlayer();
                p.Start(v, 0, null);
                double sum = 0; int n = 5 * 44100;
                for (int i = 0; i < n; i++) { p.Step(); p.Generate(out double l, out double r); sum += l * l; }
                double rms = Math.Sqrt(sum / n);
                check("Real hoot pcatdos " + type + ": " + Path.GetFileName(Path.GetDirectoryName(hp)).Split('_')[0] + " '" + tracks[0].title + "' plays through the jukebox path (" + v.ChipSummary + ")",
                    rms > 200 && v.ChipSummary.Contains("PC/AT"), "rms " + rms.ToString("0") + ", " + tracks.Count + " songs");
            }
        }
        finally
        {
            ZipFolder.CacheBase = oldCache;
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }
}
