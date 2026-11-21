using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Player;

namespace AdlibComposer.SelfTest;

/// <summary>
/// 안드로이드 Jukebox가 쓰는 플랫폼 없는 재생 묶음 (AdlibComposer.Player): 곡 열기 · 재생 · 일시정지 · 이동 · 끝 알림, 폴더 · zip 목록.
/// 장치 없이 엔진을 직접 렌더링해 본다.
/// </summary>
public static class PlayerTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }

        string samples = SopTests.FindDir("samples");
        if (samples == null || !File.Exists(Path.Combine(samples, "demo.rol"))) { Console.WriteLine("SKIP  player (samples/demo.rol not found)"); return; }

        string root = Path.Combine(Path.GetTempPath(), "FMOPLTool-playertest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            // 같은 이름 BNK는 소문자/대문자를 섞어 둔다 (안드로이드는 대소문자를 가리는 파일 시스템)
            File.Copy(Path.Combine(samples, "demo.rol"), Path.Combine(root, "demo.rol"));
            File.Copy(Path.Combine(samples, "demo.bnk"), Path.Combine(root, "DEMO.BNK"));
            // 같은 이름 가사 (대문자 확장자)
            File.WriteAllBytes(Path.Combine(root, "demo.ISS"), JukeboxTests.BuildIss(true, new[] { "첫 줄 가사", "둘째 줄" },
                new System.Collections.Generic.List<int[]> { new[] { 0, 0, 0, 2 }, new[] { 60, 0, 3, 2 }, new[] { 120, 1, 0, 4 } }));
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            Directory.CreateDirectory(Path.Combine(root, ".hidden"));
            File.WriteAllText(Path.Combine(root, "readme.txt"), "x");
            string zip = Path.Combine(root, "pack.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                z.CreateEntryFromFile(Path.Combine(samples, "demo.rol"), "songs/demo.rol");
                z.CreateEntryFromFile(Path.Combine(samples, "demo.bnk"), "songs/demo.bnk");
            }

            // ---- 폴더 목록
            var list = SongFolder.List(root, true);
            check("Player list: up · folder · zip · song order",
                list.Count == 4 && list[0].Kind == EntryKind.Up && list[1].Kind == EntryKind.Folder && list[1].Name == "sub"
                && list[2].Kind == EntryKind.Zip && list[3].Kind == EntryKind.Song && list[3].Name == "demo.rol",
                string.Join(", ", list));
            check("Player list: playlist = songs only", SongFolder.Playlist(list).SequenceEqual(new[] { Path.Combine(root, "demo.rol") }));
            check("Player list: IsSong", SongFolder.IsSong("a.VGZ") && SongFolder.IsSong("x.hoot::3") && !SongFolder.IsSong("a.txt"));

            string unz = SongFolder.OpenZip(zip);
            var inner = SongFolder.List(Path.Combine(unz, "songs"), true);
            check("Player zip: extracted folder lists song", inner.Any(e => e.Kind == EntryKind.Song && e.Name == "demo.rol"), string.Join(", ", inner));

            // ---- 재생
            var engine = new AudioEngine();
            var session = new JukeboxSession(engine, new PlayerSettings());
            check("Player: empty state", session.State == PlayState.Empty && !session.HasSong);
            bool bad = session.Load(Path.Combine(root, "readme.txt"), out string err);
            check("Player: unreadable file fails, state unchanged", !bad && err != null && session.State == PlayState.Empty, err ?? "");

            bool ok = session.Load(Path.Combine(root, "demo.rol"), out err);
            check("Player: load ROL", ok && session.Kind == SongKind.Adlib && session.State == PlayState.Stopped, err ?? "");
            check("Player: same-name bank found case-insensitively", session.Info.Contains("DEMO.BNK") && session.Info.Contains("같은 이름"), session.Info);
            double len = session.LengthSeconds;
            check("Player: length > 1s", len > 1, len.ToString("0.00"));

            session.Play();
            var buf = new short[4410 * 2];
            engine.RenderStereo(buf, 4410);
            engine.RenderStereo(buf, 4410);
            int peak = buf.Max(s => Math.Abs((int)s));
            check("Player: playing renders sound", session.State == PlayState.Playing && peak > 100, "peak=" + peak);
            double p1 = session.PositionSeconds;
            check("Player: position advances (~0.2s)", p1 > 0.1 && p1 < 0.4, p1.ToString("0.000"));

            session.Pause();
            engine.RenderStereo(buf, 4410);
            double p2 = session.PositionSeconds;
            check("Player: pause keeps position", session.State == PlayState.Paused && Math.Abs(p2 - p1) < 0.05, p1.ToString("0.000") + " → " + p2.ToString("0.000"));

            session.Seek(1.0);
            check("Player: seek while paused", Math.Abs(session.PositionSeconds - 1.0) < 0.05, session.PositionSeconds.ToString("0.000"));
            session.Play();
            engine.RenderStereo(buf, 4410);
            check("Player: resume from seek", session.PositionSeconds > 1.05 && session.PositionSeconds < 1.3, session.PositionSeconds.ToString("0.000"));

            session.Seek(len - 0.2);
            check("Player: not ended yet", !session.Update());
            for (int i = 0; i < 40 && engine.IsPlaying; i++) engine.RenderStereo(buf, 4410);
            check("Player: end detected once", session.Update() && session.State == PlayState.Stopped && !session.Update());
            check("Player: stopped position = 0", session.PositionSeconds == 0, session.PositionSeconds.ToString("0.000"));

            session.Play();
            session.Stop();
            check("Player: stop", session.State == PlayState.Stopped && !engine.IsPlaying);

            // ---- 채널 믹서 · 조절 (안드로이드 가로 화면)
            var chs = session.Channels;
            check("Player channels: ROL voices with labels", chs.Count >= 9 && chs[0].Label == "1" && chs.Any(c => c.Used), string.Join(" ", chs.Select(c => c.Label + (c.Used ? "*" : ""))));
            check("Player channels: first instrument known and found in bank", chs.Any(c => c.Used && c.Instrument != null && session.InstrumentFound(c.Instrument)),
                string.Join(", ", chs.Select(c => c.Instrument ?? "-")));
            check("Player bpm: ROL tempo > 0", session.Bpm > 0, session.Bpm.ToString("0.0"));
            session.SetKey(30);
            check("Player key: clamped to +12", session.Options.Transpose == JukeboxSession.MaxKey);
            session.SetVoicePitch(0, -40);
            check("Player pitch: clamped to -24", session.Options.VoiceTranspose[0] == -JukeboxSession.MaxVoicePitch);
            var vsTest = new VoiceState { Note = 60 };
            check("Player sounding note: note + key + voice pitch", session.SoundingNote(0, vsTest) == 60 + 12 - 24, session.SoundingNote(0, vsTest).ToString());
            check("Player sounding note: drum ignores key", session.SoundingNote(0, new VoiceState { Note = 36, Drum = true }) == 36);
            session.Solo(1);
            check("Player solo: only voice 2 on", !session.Options.VoiceMute[1] && Enumerable.Range(0, chs.Count).Where(v => v != 1).All(v => session.Options.VoiceMute[v]));
            session.Solo(1);
            check("Player solo again: all on", Enumerable.Range(0, chs.Count).All(v => !session.Options.VoiceMute[v]));
            session.SetSpeed(1.5);
            session.SetMute(3, true);
            session.ResetAdjustments();
            check("Player reset: speed · key · mute · pitch back", session.Options.Speed == 1.0 && session.Options.Transpose == 0 && !session.Options.VoiceMute[3] && session.Options.VoiceTranspose[0] == 0);

            session.Seek(1.0);
            session.SeekBy(5);
            check("Player seek +5s", Math.Abs(session.PositionSeconds - Math.Min(6.0, len)) < 0.05, session.PositionSeconds.ToString("0.000"));
            session.SeekBy(-10);
            check("Player seek -10s stops at 0", session.PositionSeconds < 0.01, session.PositionSeconds.ToString("0.000"));

            var states = new VoiceState[PlaybackOptions.Voices];
            check("Player voice states: none while stopped", !session.GetVoiceStates(states));
            session.Play();
            bool any = false;
            for (int i = 0; i < 20 && !any; i++)
            {
                engine.RenderStereo(buf, 4410);
                any = session.GetVoiceStates(states) && states.Take(chs.Count).Any(s => s.Note >= 0);
            }
            check("Player voice states: a note sounds while playing", any);
            int mutedPeak;
            for (int v = 0; v < chs.Count; v++) session.SetMute(v, true);
            for (int i = 0; i < 8; i++) engine.RenderStereo(buf, 4410);   // 끄기 전 음의 릴리스가 잦아들게 (0.8초)
            mutedPeak = buf.Max(s => Math.Abs((int)s));
            check("Player mute all: silent", mutedPeak < 200, "peak=" + mutedPeak);
            session.Stop();
            session.ResetAdjustments();

            // ---- 가사 · 소리 고르기
            check("Player lyrics: same-name ISS found", session.Lyrics != null && session.Lyrics.Lines.Count == 2 && session.LyricsText != null && session.LyricsText.Contains(".ISS"),
                session.LyricsText ?? "(없음)");
            var ls = session.LyricStateNow();
            check("Player lyrics: state at start = first line", ls != null && ls.Line == 0, ls == null ? "null" : ls.Line + " next " + ls.NextLine);
            check("Player sound: default OPL2", !session.ChipSynth && session.SoundText == "OPL2", session.SoundText);
            session.RawTone = true;
            check("Player sound: raw tone → engine ToneStandard off", !engine.ToneStandard);
            session.RawTone = false;
            session.VintageSpeaker = true;
            check("Player sound: vintage speaker → engine", engine.VintageSpeaker);
            session.VintageSpeaker = false;
            session.SetSound(ChipSound.Nes, false);
            check("Player sound: 8-bit label", session.ChipSynth && session.SoundText == ChipMidiSynth.Label(ChipSound.Nes), session.SoundText);
            session.Seek(0);
            session.Play();
            int chipPeak = 0;
            bool leadShown = false;
            for (int i = 0; i < 10; i++)
            {
                engine.RenderStereo(buf, 4410);
                chipPeak = Math.Max(chipPeak, buf.Max(s => Math.Abs((int)s)));
                if (session.GetVoiceStates(states) && session.InstrumentOf(0, states[0], true) == chs[0].Instrument) leadShown = true;
            }
            check("Player sound: ROL through 8-bit synth renders sound", chipPeak > 100, "peak=" + chipPeak);
            check("Player sound: 8-bit mixer shows song instrument name", leadShown, chs[0].Instrument ?? "-");
            session.SetSound(null, false);
            engine.RenderStereo(buf, 4410);
            check("Player sound: back to OPL while playing", session.State == PlayState.Playing && !session.ChipSynth && session.SoundText == "OPL2");
            session.Stop();

            // zip에서 푼 곡: 같은 폴더 뱅크
            ok = session.Load(Path.Combine(unz, "songs", "demo.rol"), out err);
            check("Player: load song from unzipped folder", ok && session.Info.Contains("demo.bnk"), err ?? session.Info);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }
}
