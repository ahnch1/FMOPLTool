using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>PC/AT 게임 음악 형식: XMI (+ AIL 음색 묶음) · MDI · Ad Lib MUS + SND.</summary>
public static class PcFormatTests
{
    private static readonly byte[] Ins28 =
    {
        1, 1, 4, 15, 2, 1, 4, 5, 10, 0, 0, 0, 0,     // 모듈레이터: KSL MULT FB AR SL EG DR RR TL AM VIB KSR CON
        0, 2, 0, 14, 3, 1, 6, 7, 0, 0, 0, 0, 0,      // 캐리어
        1, 0,                                         // 파형
    };

    public static void Run(Action<string, bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "fmopl_pcfmt_test");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Xmi(check, dir);
            Mdi(check, dir);
            Mus(check, dir);
            Real(check, dir);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    // ------------------------------------------------------------ XMI

    private static byte[] Chunk(string id, byte[] body)
    {
        var l = new List<byte>(Encoding.ASCII.GetBytes(id));
        l.Add((byte)(body.Length >> 24)); l.Add((byte)(body.Length >> 16)); l.Add((byte)(body.Length >> 8)); l.Add((byte)body.Length);
        l.AddRange(body);
        if ((body.Length & 1) != 0) l.Add(0);
        return l.ToArray();
    }

    private static byte[] Concat(params byte[][] parts) { return parts.SelectMany(x => x).ToArray(); }

    private static byte[] XmiSeq(byte[] evnt) { return Chunk("FORM", Concat(Encoding.ASCII.GetBytes("XMID"), Chunk("EVNT", evnt))); }

    private static void Xmi(Action<string, bool, string> check, string dir)
    {
        // 곡 1: 프로그램 5, 노트 60 길이 60틱 (= 0.5초), 120틱 기다림, 볼륨, AIL 반복 컨트롤(116, 빼야 함), 끝
        byte[] e1 = { 0xC0, 0x05, 0x90, 0x3C, 0x64, 0x3C, 0x78, 0xB0, 0x07, 0x64, 0xB0, 0x74, 0x02, 0xFF, 0x2F, 0x00 };
        // 곡 2: 노트 64, 길이 240틱 (VLQ 0x81 0x70), 기다림 바이트 둘 (0x7F + 0x01 = 128)
        byte[] e2 = { 0x91, 0x40, 0x50, 0x81, 0x70, 0x7F, 0x01, 0xB1, 0x0A, 0x40, 0xFF, 0x2F, 0x00 };
        var info = Chunk("INFO", new byte[] { 2, 0 });
        var xmi = Concat(Chunk("FORM", Concat(Encoding.ASCII.GetBytes("XDIR"), info)), Chunk("CAT ", Concat(Encoding.ASCII.GetBytes("XMID"), XmiSeq(e1), XmiSeq(e2))));
        check("XMI: FORM XDIR + CAT XMID, 2 sequences", XmiFormat.IsXmi(xmi) && XmiFormat.SequenceCount(xmi) == 2, XmiFormat.SequenceCount(xmi).ToString());
        var s = XmiFormat.Read(xmi, 0);
        var on = s.Events.First(x => x.IsNoteOn);
        var off = s.Events.First(x => x.IsNoteOff);
        check("XMI: note-on duration → note-off, 120 Hz timing, AIL controllers dropped",
            on.Data1 == 60 && off.Tick == 60 && s.Division == 60 && Math.Abs(s.TickToSeconds(120) - 1.0) < 1e-6 && s.Events.Any(x => x.Kind == 0xB0 && x.Data1 == 7) && !s.Events.Any(x => x.Kind == 0xB0 && x.Data1 == 116) && s.FirstProgram[0] == 5,
            "off @" + off.Tick + ", events " + s.Events.Count);
        var s2 = XmiFormat.Read(xmi, 1);
        var cc = s2.Events.First(x => x.Kind == 0xB0);
        check("XMI: second sequence, summed delay bytes, VLQ duration", s2.Events.First(x => x.IsNoteOn).Data1 == 64 && s2.Events.First(x => x.IsNoteOff).Tick == 240 && cc.Tick == 128, cc.Tick + " / " + s2.Events.First(x => x.IsNoteOff).Tick);

        // AIL 음색 묶음 (.AD): 프로그램 5 (뱅크 0), 드럼 36 (뱅크 127)
        var ad = new List<byte>();
        ad.AddRange(new byte[] { 5, 0, 18, 0, 0, 0 });
        ad.AddRange(new byte[] { 36, 127, 32, 0, 0, 0 });
        ad.AddRange(new byte[] { 0xFF, 0xFF });
        while (ad.Count < 18) ad.Add(0);
        ad.AddRange(new byte[] { 14, 0, 0, 0x21, 0x0A, 0xF2, 0x34, 0x01, 0x0E, 0x21, 0x00, 0xF4, 0x56, 0x00 });
        ad.AddRange(new byte[] { 14, 0, 0xFC, 0x01, 0x00, 0xF8, 0x88, 0x00, 0x01, 0x01, 0x00, 0xF9, 0x99, 0x00 });
        var gm = AilTimbres.Read(ad.ToArray(), "T.AD");
        var p5 = gm.Melodic[5]?.Voice1;
        check("AIL timbres (.AD): melodic + drum, register bytes → instrument",
            p5 != null && p5.Modulator.Multiple == 1 && p5.Modulator.Eg == 1 && p5.Modulator.TotalLevel == 10 && p5.Modulator.Attack == 15 && p5.Modulator.Feedback == 7 && !p5.Additive && gm.Drums[36] != null && gm.Drums[36].Offset1 == -4,
            p5 == null ? "null" : "mult " + p5.Modulator.Multiple + " tl " + p5.Modulator.TotalLevel + " fb " + p5.Modulator.Feedback);
        File.WriteAllBytes(Path.Combine(dir, "SONG.XMI"), xmi);
        File.WriteAllBytes(Path.Combine(dir, "SAMPLE.AD"), ad.ToArray());
        var loaded = MidiFiles.Load(Path.Combine(dir, "SONG.XMI") + "::1");
        check("XMI load: 'file::1' = second sequence, AIL bank from folder attached", MidiFiles.IsMidi("a.XMI::3") && loaded.Events.First(x => x.IsNoteOn).Data1 == 64 && loaded.CustomBank != null && loaded.CustomBank.Melodic[5] != null && MidiFiles.SequenceCount(Path.Combine(dir, "SONG.XMI")) == 2,
            loaded.FormatName + " / " + loaded.CustomBank?.Source);
        check("XMI in file list: multi-sequence file expands into tracks", ChipSongs.HasTracks(Path.Combine(dir, "SONG.XMI")) && ChipSongs.Tracks(Path.Combine(dir, "SONG.XMI")).Count == 2, "");
    }

    // ------------------------------------------------------------ MDI

    private static byte[] AdlibMeta(int kind, params byte[] data)
    {
        var l = new List<byte> { 0x00, 0xFF, 0x7F, (byte)(5 + data.Length), 0x00, 0x00, 0x3F, (byte)(kind >> 8), (byte)kind };
        l.AddRange(data);
        return l.ToArray();
    }

    private static byte[] MakeMdi()
    {
        var trk = new List<byte>();
        trk.AddRange(new byte[] { 0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20 });       // 템포 500000
        trk.AddRange(AdlibMeta(2, 1));                                                  // 타악기 모드
        trk.AddRange(AdlibMeta(1, Concat(new byte[] { 0 }, Ins28)));                    // 채널 0 악기
        var drumIns = (byte[])Ins28.Clone(); drumIns[8] = 20;
        trk.AddRange(AdlibMeta(1, Concat(new byte[] { 11 }, drumIns)));                 // 채널 11 (BD) 악기
        trk.AddRange(new byte[] { 0x00, 0x90, 0x3C, 0x60, 0x00, 0x9B, 0x30, 0x70 });   // 노트: 채널 0 · 채널 11
        trk.AddRange(new byte[] { 0x60, 0x80, 0x3C, 0x00, 0x00, 0x8B, 0x30, 0x00 });
        trk.AddRange(new byte[] { 0x00, 0xFF, 0x2F, 0x00 });
        var l = new List<byte>(Encoding.ASCII.GetBytes("MThd"));
        l.AddRange(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 0, 96 });
        l.AddRange(Encoding.ASCII.GetBytes("MTrk"));
        l.AddRange(new byte[] { 0, 0, (byte)(trk.Count >> 8), (byte)trk.Count });
        l.AddRange(trk);
        return l.ToArray();
    }

    private static void Mdi(Action<string, bool, string> check, string dir)
    {
        var d = MakeMdi();
        var s = MdiFormat.Read(d);
        var ins = s.CustomBank?.Melodic[0]?.Voice1;
        check("MDI: AdLib instrument meta → song FM bank + program change", MdiFormat.IsMdi(d) && ins != null && ins.Modulator.Feedback == 4 && ins.Modulator.TotalLevel == 10 && ins.Carrier.Multiple == 2 && ins.Modulator.WaveSelect == 1
            && s.Events.Any(e => e.Kind == 0xC0 && e.Channel == 0 && e.Data1 == 0), ins == null ? "no bank" : "fb " + ins.Modulator.Feedback + " tl " + ins.Modulator.TotalLevel);
        check("MDI: percussive mode → channel 11 notes become GM drum 36 with its timbre", s.Events.Any(e => e.IsNoteOn && e.Channel == 9 && e.Data1 == 36) && s.CustomBank.Drums[36]?.Voice1.Modulator.TotalLevel == 20 && !s.Events.Any(e => e.Channel == 11), "");
        // 곡 악기만으로 소리 (설정 GM 뱅크 없이)
        using (var eng = new AudioEngine { MidiBank = null, MidiSynth = MidiSynth.Fm })
        {
            eng.PlayMidi(s);
            var buf = new short[44100 / 2];
            eng.Render(buf, buf.Length);
            double r = Math.Sqrt(buf.Select(x => (double)x * x).Average());
            var bare = MidiFileFormat.Read(d);
            eng.PlayMidi(bare);
            eng.Render(buf, buf.Length);
            double r0 = Math.Sqrt(buf.Select(x => (double)x * x).Average());
            check("MDI: FM plays the song's own instruments (silent without them)", r > 300 && r0 < 10, r.ToString("0") + " vs " + r0.ToString("0"));
        }
    }

    // ------------------------------------------------------------ MUS + SND

    private static byte[] MakeMus()
    {
        var h = new byte[ImsFormat.HeaderSize];
        h[0] = 1; h[1] = 0; h[36] = 240; h[37] = 4;
        byte[] ev = { 0x00, 0xC0, 0x01, 0x00, 0x90, 0x3C, 0x7F, 0xF8, 0x00, 0x80, 0x3C, 0x00, 0x00, 0xFC };
        BitConverter.GetBytes(240).CopyTo(h, 38);
        BitConverter.GetBytes(ev.Length).CopyTo(h, 42);
        h[58] = 0; h[59] = 1; h[60] = 120;
        return Concat(h, ev);
    }

    private static byte[] MakeSnd(bool named)
    {
        int n = 2;
        var l = new List<byte> { 1, 0, (byte)n, 0 };
        int at = named ? 6 + n * 9 : 6;
        l.Add((byte)at); l.Add((byte)(at >> 8));
        if (named) foreach (var nm in new[] { "piano1", "flute1" }) { var b = new byte[9]; Encoding.ASCII.GetBytes(nm).CopyTo(b, 0); l.AddRange(b); }
        for (int i = 0; i < n; i++)
            foreach (var v in Ins28) { int x = v + (i == 1 && v == 10 ? 5 : 0); l.Add((byte)x); l.Add(0); }
        return l.ToArray();
    }

    private static void Mus(Action<string, bool, string> check, string dir)
    {
        var mus = MakeMus();
        var song = new AdlibMusFormat().Read(new MemoryStream(mus));
        var v0 = song.Voices[0];
        check("Ad Lib MUS: header check, notes, instrument by number (INS1)", AdlibMus.IsAdlibMus(mus) && v0.Notes.Count == 1 && v0.Notes[0].Pitch == 60 && v0.Timbres.Count == 1 && v0.Timbres[0].InstrumentName == "INS1",
            v0.Notes.Count + " notes, " + (v0.Timbres.Count > 0 ? v0.Timbres[0].InstrumentName : "-"));
        bool rejected = false;
        try { new AdlibMusFormat().Read(new MemoryStream(new byte[] { 0x23, 0x04, 0x40, 0x00, 0x41, 0x65 }.Concat(new byte[100]).ToArray())); } catch (InvalidDataException) { rejected = true; }
        check("Ad Lib MUS: other games' .MUS rejected", rejected, "");
        foreach (bool named in new[] { true, false })
        {
            var bank = new SndBankFormat().Read(new MemoryStream(MakeSnd(named)));
            var i1 = bank.Find("INS1");
            check("SND timbre bank (" + (named ? "with names" : "no names") + "): 16-bit params, INS0 / INS1", bank.Instruments.Count == 2 && i1 != null && i1.Modulator.TotalLevel == 15 && i1.Carrier.Multiple == 2,
                bank.Instruments.Count + " / " + i1?.Modulator.TotalLevel);
        }
        var b2 = new SndBankFormat().Read(new MemoryStream(MakeSnd(true)));
        using (var eng = new AudioEngine())
        {
            eng.Play(song, b2);
            var buf = new short[44100 / 2];
            eng.Render(buf, buf.Length);
            double r = Math.Sqrt(buf.Select(x => (double)x * x).Average());
            check("Ad Lib MUS + SND plays", r > 300, r.ToString("0"));
        }
    }

    // ------------------------------------------------------------ 실제 파일 (Hootarchive/pc의 zip)

    private static string Extract(string zip, string dir, Func<string, bool> want)
    {
        string to = Path.Combine(dir, Path.GetFileNameWithoutExtension(zip));
        Directory.CreateDirectory(to);
        using (var z = ZipFile.OpenRead(zip))
            foreach (var e in z.Entries.Where(e => e.Name.Length > 0 && want(e.Name)))
                e.ExtractToFile(Path.Combine(to, e.Name), true);
        return to;
    }

    private static double RenderMidi(MidiSong m, GmBank gm, double sec)
    {
        using (var eng = new AudioEngine { MidiBank = gm, MidiSynth = MidiSynth.Fm })
        {
            eng.PlayMidi(m, m.SecondsToTick(Math.Min(10, m.LengthSeconds / 3)));
            var buf = new short[(int)(44100 * sec)];
            eng.Render(buf, buf.Length);
            return Math.Sqrt(buf.Select(x => (double)x * x).Average());
        }
    }

    private static void Real(Action<string, bool, string> check, string dir)
    {
        string inc = SopTests.FindDir("_incoming");
        string pc = inc == null ? null : Path.Combine(inc, "Hootarchive", "pc");
        if (pc == null || !Directory.Exists(pc)) { Console.WriteLine("SKIP  real PC/AT formats (_incoming/Hootarchive/pc not found)"); return; }
        string res = SopTests.FindDir(Path.Combine("src", "AdlibComposer.App", "Defaults", "resource"));
        GmBank gm = res != null && File.Exists(Path.Combine(res, "GENMIDI.OP2")) ? GmBank.LoadOp2(Path.Combine(res, "GENMIDI.OP2")) : null;

        string Zip(string name) { var f = Path.Combine(pc, name); return File.Exists(f) ? f : null; }
        var xz = Zip("7cogold_at.zip");
        if (xz != null)
        {
            string d = Extract(xz, dir, n => n.EndsWith(".XMI", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".AD", StringComparison.OrdinalIgnoreCase));
            var x = Directory.GetFiles(d, "*.XMI").OrderBy(f => f).First();
            var m = MidiFiles.Load(x);
            double r = RenderMidi(m, gm, 3);
            check("Real XMI 7cogold/" + Path.GetFileName(x) + ": notes, AIL bank (SAMPLE.AD), FM sound", m.Events.Count(e => e.IsNoteOn) > 20 && m.CustomBank != null && r > 200,
                m.Events.Count(e => e.IsNoteOn) + " notes, " + m.LengthSeconds.ToString("0") + " s, bank " + m.CustomBank?.Source + ", rms " + r.ToString("0"));
        }
        var mz = Zip("1000miglia_at.zip");
        if (mz != null)
        {
            string d = Extract(mz, dir, n => n.EndsWith(".mdi", StringComparison.OrdinalIgnoreCase));
            var m = MidiFiles.Load(Path.Combine(d, "01.mdi"));
            double r = RenderMidi(m, gm, 3);
            int patches = m.CustomBank == null ? 0 : m.CustomBank.Melodic.Count(p => p != null);
            check("Real MDI 1000miglia/01.mdi: AdLib instruments from the song, FM sound", patches >= 3 && m.Events.Count(e => e.IsNoteOn) > 20 && r > 200,
                patches + " instruments, " + m.Events.Count(e => e.IsNoteOn) + " notes, " + m.LengthSeconds.ToString("0") + " s, rms " + r.ToString("0"));
        }
        foreach (var name in new[] { "ccmaster3_at.zip", "ddribble_at.zip" })
        {
            var z = Zip(name);
            if (z == null) continue;
            string d = Extract(z, dir, n => n.EndsWith(".MUS", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".SND", StringComparison.OrdinalIgnoreCase));
            var musFile = Directory.GetFiles(d, "*.MUS").Where(f => AdlibMus.IsAdlibMus(File.ReadAllBytes(f))).OrderByDescending(f => new FileInfo(f).Length).First();
            Song song;
            using (var fs = File.OpenRead(musFile)) song = new AdlibMusFormat().Read(fs);
            string sndFile = Directory.GetFiles(d, Path.GetFileNameWithoutExtension(musFile) + ".SND").FirstOrDefault() ?? Directory.GetFiles(d, "*.SND").First();
            InstrumentBank bank;
            using (var fs = File.OpenRead(sndFile)) bank = new SndBankFormat().Read(fs);
            var used = song.Voices.SelectMany(v => v.Timbres).Select(t => t.InstrumentName).Distinct().ToList();
            int found = used.Count(n => bank.Find(n) != null);
            double r;
            using (var eng = new AudioEngine())
            {
                eng.Play(song, bank);
                var buf = new short[44100 * 3];
                eng.Render(buf, buf.Length);
                r = Math.Sqrt(buf.Select(x => (double)x * x).Average());
            }
            check("Real MUS " + name + "/" + Path.GetFileName(musFile) + " + " + Path.GetFileName(sndFile) + ": instruments found, sound",
                song.Voices.Sum(v => v.Notes.Count) > 10 && found == used.Count && r > 200,
                song.Voices.Sum(v => v.Notes.Count) + " notes, instruments " + found + "/" + used.Count + ", rms " + r.ToString("0"));
        }
    }
}
