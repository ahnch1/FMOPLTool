using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>칩 기록 곡 → 작업 곡 (VgmToSong): ROL → VGM → 곡으로 되돌려 노트가 맞는지, 실제 NSF가 노트를 내는지.</summary>
public static class VgmToSongTests
{
    public static void Run(Action<string, bool, string> check)
    {
        RoundTrip(check);
        RealNsf(check);
    }

    private static void RoundTrip(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("samples");
        if (dir == null || !File.Exists(Path.Combine(dir, "demo.rol"))) { Console.WriteLine("SKIP  VgmToSong round trip (samples/demo.rol not found)"); return; }
        Song song; InstrumentBank bank;
        using (var fs = File.OpenRead(Path.Combine(dir, "demo.rol"))) song = new RolFormat().Read(fs);
        using (var fs = File.OpenRead(Path.Combine(dir, "demo.bnk"))) bank = new BnkFormat().Read(fs);
        var vgm = VgmFormat.Read(VgmExport.FromSong(song, bank).ToArray());

        var r = VgmToSong.Convert(vgm);
        var back = r.Song;
        check("VgmToSong: melodic 9-voice song from OPL2", !back.IsSop && back.IsMelodic && r.Notes > 0, r.ChannelsWithNotes + " channels, " + r.Notes + " notes, " + string.Join(" ", r.Used));

        // 원래 노트 (melodic 보이스)의 시작 시각 · 음높이가 바꾼 곡의 어느 보이스에 ±30ms · 같은 음으로 있는가
        var srcMap = new TempoMap(song);
        double tps = back.BasicTempo / 60.0 * back.TickBeat;
        int total = 0, found = 0;
        for (int v = 0; v < song.VoiceCount; v++)
        {
            if (song.IsDrumVoice(v)) continue;
            foreach (var n in song.Voices[v].Notes)
            {
                if (n.Pitch <= 0) continue;   // ROL 쉼표
                double t = srcMap.SecondsAt(n.StartTick);
                if (t > vgm.LengthSeconds - 1.5) continue;
                total++;
                int tick = (int)Math.Round(t * tps);
                if (back.Voices.Take(back.VoiceCount).Any(vt => vt.Notes.Any(m => m.Pitch == n.Pitch && Math.Abs(m.StartTick - tick) <= 3))) found++;
            }
        }
        double rate = total > 0 ? found / (double)total : 0;
        check("VgmToSong: original note onsets found (pitch, ±3 ticks)", total > 20 && rate >= 0.85, found + " / " + total + " = " + rate.ToString("P0"));
    }

    private static void RealNsf(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("_incoming");
        string nsf = dir == null ? null : Path.Combine(dir, "VGM", "Castlevania.nsf");
        if (nsf == null || !File.Exists(nsf)) { Console.WriteLine("SKIP  VgmToSong NSF (_incoming/VGM/Castlevania.nsf not found)"); return; }
        var v = ChipSongs.Load(nsf + "::0");
        var sw = Stopwatch.StartNew();
        var r = VgmToSong.Convert(v, null, 60);
        sw.Stop();
        var s = r.Song;
        int voices = s.Voices.Take(s.VoiceCount).Count(t => t.Notes.Count > 0);
        bool sane = s.Voices.SelectMany(t => t.Notes).All(n => n.Pitch >= Song.MinNote && n.Pitch <= Song.MaxNote && n.Duration > 0);
        check("VgmToSong: Castlevania NSF track 1 (60 s) → notes on the pulse / triangle channels", voices >= 2 && r.Notes > 50 && sane,
            r.Notes + " notes, voices " + voices + " (" + string.Join(" ", r.Used) + "), " + sw.ElapsedMilliseconds + " ms");
    }
}
