using System;
using System.IO;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>데모 곡(리듬 모드: 멜로디 + BD/SD/HH)을 WAV로 렌더링한다. 사용: dotnet run --project tests/AdlibComposer.SelfTest -- --wav demo.wav [--rol demo.rol] [--bnk demo.bnk]</summary>
public static class DemoRender
{
    public static Song BuildDemoSong()
    {
        var song = new Song { Title = "demo", TickBeat = 48, BeatsPerMeasure = 4, BasicTempo = 120f, IsMelodic = false };
        song.Voices[0].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "LEAD" });
        song.Voices[1].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "BASS" });
        song.Voices[6].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "KICK" });
        song.Voices[7].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "SNARE" });
        song.Voices[10].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = "HAT" });

        int[] melody = { 60, 64, 67, 72, 71, 67, 64, 62, 60, 64, 67, 76, 74, 71, 67, 64 };   // 2마디, 8분음표 x 16
        for (int i = 0; i < melody.Length; i++)
            song.Voices[0].Notes.Add(new Note { StartTick = i * 24, Duration = 22, Pitch = melody[i] });

        int[] bass = { 48, 48, 55, 55, 48, 48, 53, 55 };
        for (int i = 0; i < bass.Length; i++)
            song.Voices[1].Notes.Add(new Note { StartTick = i * 48, Duration = 44, Pitch = bass[i] });

        for (int beat = 0; beat < 8; beat++)
        {
            if (beat % 2 == 0) song.Voices[6].Notes.Add(new Note { StartTick = beat * 48, Duration = 12, Pitch = 36 });
            else song.Voices[7].Notes.Add(new Note { StartTick = beat * 48, Duration = 12, Pitch = 36 });
        }
        for (int i = 0; i < 16; i++)
            song.Voices[10].Notes.Add(new Note { StartTick = i * 24, Duration = 6, Pitch = 36 });
        return song;
    }

    public static InstrumentBank BuildDemoBank()
    {
        var bank = new InstrumentBank();

        var lead = AdlibInstrument.CreateDefault("LEAD");
        lead.Modulator.Multiple = 2; lead.Modulator.TotalLevel = 27; lead.Modulator.Feedback = 4;
        lead.Carrier.Eg = 1; lead.Carrier.Sustain = 3; lead.Carrier.Decay = 2; lead.Carrier.ReleaseRate = 8;
        bank.Add(lead);

        var bass = AdlibInstrument.CreateDefault("BASS");
        bass.Modulator.Multiple = 1; bass.Modulator.TotalLevel = 33; bass.Modulator.Feedback = 2; bass.Modulator.Decay = 5;
        bass.Carrier.Eg = 1; bass.Carrier.Sustain = 4; bass.Carrier.ReleaseRate = 8;
        bank.Add(bass);

        var kick = AdlibInstrument.CreateDefault("KICK");   // 베이스 드럼: 두 오퍼레이터 모두 사용
        kick.Modulator.Multiple = 1; kick.Modulator.Feedback = 6; kick.Modulator.Attack = 15; kick.Modulator.Decay = 8; kick.Modulator.Sustain = 15; kick.Modulator.TotalLevel = 13;
        kick.Carrier.Multiple = 0; kick.Carrier.Attack = 15; kick.Carrier.Decay = 7; kick.Carrier.Sustain = 15; kick.Carrier.ReleaseRate = 7; kick.Carrier.TotalLevel = 0;
        bank.Add(kick);

        var snare = AdlibInstrument.CreateDefault("SNARE"); // 스네어: 모듈레이터 필드만 사용
        snare.Modulator.Multiple = 1; snare.Modulator.Attack = 15; snare.Modulator.Decay = 7; snare.Modulator.Sustain = 15; snare.Modulator.ReleaseRate = 7; snare.Modulator.TotalLevel = 0; snare.Modulator.Feedback = 0;
        bank.Add(snare);

        var hat = AdlibInstrument.CreateDefault("HAT");     // 하이햇: 모듈레이터 필드만 사용
        hat.Modulator.Multiple = 1; hat.Modulator.Attack = 15; hat.Modulator.Decay = 10; hat.Modulator.Sustain = 15; hat.Modulator.ReleaseRate = 10; hat.Modulator.TotalLevel = 7;
        bank.Add(hat);
        return bank;
    }

    public static short[] Render(Song song, InstrumentBank bank, double seconds)
    {
        var engine = new AudioEngine();
        engine.Play(song, bank);
        int total = (int)(seconds * AudioEngine.OutputRate);
        var all = new short[total];
        var chunk = new short[1024];
        for (int off = 0; off < total; off += chunk.Length)
        {
            int n = Math.Min(chunk.Length, total - off);
            engine.Render(chunk, n);
            Array.Copy(chunk, 0, all, off, n);
        }
        return all;
    }

    public static void WriteWav(string path, short[] samples, int rate)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            int bytes = samples.Length * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (short s in samples) w.Write(s);
        }
    }

    public static int Run(string[] args)
    {
        string wav = null, rol = null, bnk = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--wav") wav = args[i + 1];
            if (args[i] == "--rol") rol = args[i + 1];
            if (args[i] == "--bnk") bnk = args[i + 1];
        }
        var song = BuildDemoSong();
        var bank = BuildDemoBank();
        if (rol != null) using (var fs = File.Create(rol)) new RolFormat().Write(song, fs);
        if (bnk != null) using (var fs = File.Create(bnk)) new BnkFormat().Write(bank, fs);
        if (wav != null)
        {
            var pcm = Render(song, bank, 5.0);
            WriteWav(wav, pcm, AudioEngine.OutputRate);
            int peak = 0; double sum = 0;
            foreach (short s in pcm) { peak = Math.Max(peak, Math.Abs((int)s)); sum += (double)s * s; }
            Console.WriteLine("rendered " + wav + "  peak=" + peak + "  rms=" + Math.Sqrt(sum / pcm.Length).ToString("0"));
        }
        return 0;
    }
}
