using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Core;

namespace AdlibComposer.Formats;

/// <summary>General MIDI 이름표와, FM으로 GM을 연주할 악기 뱅크.</summary>
public static class GeneralMidi
{
    public const int DrumChannel = 9;
    public const int FirstDrum = 35, LastDrum = 81;

    public static readonly string[] ProgramNames =
    {
        "Acoustic Grand Piano", "Bright Acoustic Piano", "Electric Grand Piano", "Honky-tonk Piano", "Electric Piano 1", "Electric Piano 2", "Harpsichord", "Clavinet",
        "Celesta", "Glockenspiel", "Music Box", "Vibraphone", "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
        "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ", "Reed Organ", "Accordion", "Harmonica", "Tango Accordion",
        "Nylon Guitar", "Steel Guitar", "Jazz Guitar", "Clean Guitar", "Muted Guitar", "Overdriven Guitar", "Distortion Guitar", "Guitar Harmonics",
        "Acoustic Bass", "Finger Bass", "Pick Bass", "Fretless Bass", "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
        "Violin", "Viola", "Cello", "Contrabass", "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani",
        "String Ensemble 1", "String Ensemble 2", "Synth Strings 1", "Synth Strings 2", "Choir Aahs", "Voice Oohs", "Synth Voice", "Orchestra Hit",
        "Trumpet", "Trombone", "Tuba", "Muted Trumpet", "French Horn", "Brass Section", "Synth Brass 1", "Synth Brass 2",
        "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax", "Oboe", "English Horn", "Bassoon", "Clarinet",
        "Piccolo", "Flute", "Recorder", "Pan Flute", "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
        "Square Lead", "Sawtooth Lead", "Calliope Lead", "Chiff Lead", "Charang Lead", "Voice Lead", "Fifths Lead", "Bass + Lead",
        "New Age Pad", "Warm Pad", "Polysynth Pad", "Choir Pad", "Bowed Pad", "Metallic Pad", "Halo Pad", "Sweep Pad",
        "Rain FX", "Soundtrack FX", "Crystal FX", "Atmosphere FX", "Brightness FX", "Goblins FX", "Echoes FX", "Sci-fi FX",
        "Sitar", "Banjo", "Shamisen", "Koto", "Kalimba", "Bagpipe", "Fiddle", "Shanai",
        "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock", "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
        "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet", "Telephone Ring", "Helicopter", "Applause", "Gunshot",
    };

    public static readonly string[] DrumNames =
    {
        "Acoustic Bass Drum", "Bass Drum 1", "Side Stick", "Acoustic Snare", "Hand Clap", "Electric Snare", "Low Floor Tom", "Closed Hi-hat",
        "High Floor Tom", "Pedal Hi-hat", "Low Tom", "Open Hi-hat", "Low-Mid Tom", "Hi-Mid Tom", "Crash Cymbal 1", "High Tom",
        "Ride Cymbal 1", "Chinese Cymbal", "Ride Bell", "Tambourine", "Splash Cymbal", "Cowbell", "Crash Cymbal 2", "Vibraslap",
        "Ride Cymbal 2", "Hi Bongo", "Low Bongo", "Mute Hi Conga", "Open Hi Conga", "Low Conga", "High Timbale", "Low Timbale",
        "High Agogo", "Low Agogo", "Cabasa", "Maracas", "Short Whistle", "Long Whistle", "Short Guiro", "Long Guiro",
        "Claves", "Hi Wood Block", "Low Wood Block", "Mute Cuica", "Open Cuica", "Mute Triangle", "Open Triangle",
    };

    /// <summary>
    /// MT-32 기본 음색 번호(0~127) → 가장 비슷한 GM 프로그램. MT-32 에뮬레이터 없이 MT-32용 곡을 GM 소리(FM / Windows)로 낼 때 쓴다.
    /// (MT-32 순서: 피아노 · 오르간 · 키보드 → 신스 → 현 → 기타 · 베이스 → 관악 → 타악 · 효과)
    /// </summary>
    public static readonly byte[] Mt32ToGm =
    {
        0, 1, 0, 2, 4, 4, 5, 3, 16, 17, 18, 16, 16, 19, 20, 21,
        6, 6, 6, 7, 7, 7, 8, 112, 62, 62, 63, 63, 38, 38, 39, 39,
        88, 95, 52, 98, 97, 99, 14, 54, 102, 96, 53, 102, 81, 100, 14, 80,
        48, 48, 49, 45, 41, 40, 42, 42, 43, 46, 45, 24, 25, 28, 27, 104,
        32, 32, 34, 33, 36, 37, 35, 35, 79, 73, 72, 72, 74, 75, 64, 65,
        66, 67, 71, 71, 68, 69, 70, 22, 56, 59, 57, 57, 60, 60, 58, 61,
        61, 11, 11, 98, 14, 9, 14, 13, 12, 107, 107, 77, 78, 78, 76, 76,
        47, 117, 127, 118, 118, 116, 115, 119, 115, 112, 55, 124, 123, 0, 14, 117,
    };

    public static string DrumName(int note)
    {
        return note >= FirstDrum && note <= LastDrum ? DrumNames[note - FirstDrum] : "Drum " + note;
    }
}

/// <summary>FM으로 GM 음 하나를 낼 악기: 2-op 보이스 1개 또는 2개 (DMX "double voice").</summary>
public sealed class GmPatch
{
    public string Name = "";
    /// <summary>보이스 1 · 2 (2는 없을 수 있음).</summary>
    public AdlibInstrument Voice1, Voice2;
    /// <summary>보이스별 음 옮김 (반음).</summary>
    public int Offset1, Offset2;
    /// <summary>두 번째 보이스의 미세 음높이 (DMX finetune: 128 = 그대로, 1단위 = 1/64 반음쯤).</summary>
    public int FineTune = 128;
    /// <summary>≥ 0이면 음 번호와 상관없이 이 음 (드럼).</summary>
    public int FixedNote = -1;
}

/// <summary>
/// GM FM 뱅크: 멜로디 128 + 드럼(35~81).
/// <list type="bullet">
/// <item><see cref="LoadOp2"/>: DMX GENMIDI.OP2 (Doom 등, "#OPL_II#" + 175 × 36바이트 + 이름 175 × 32바이트)</item>
/// <item><see cref="FromBank"/>: GM 번호마다 BNK 악기 이름을 찾아 대략 맞춘다 (STANDARD.BNK). GENMIDI.OP2가 없을 때</item>
/// </list>
/// </summary>
public sealed class GmBank
{
    public string Source { get; private set; } = "";
    public GmPatch[] Melodic { get; } = new GmPatch[128];
    /// <summary>[음 번호 0~127], 35~81만 채운다.</summary>
    public GmPatch[] Drums { get; } = new GmPatch[128];

    public static bool IsOp2(byte[] d) { return d.Length >= 8 + 175 * 36 && Encoding.ASCII.GetString(d, 0, 8) == "#OPL_II#"; }

    public static GmBank LoadOp2(string path)
    {
        var d = File.ReadAllBytes(path);
        if (!IsOp2(d)) throw new InvalidDataException("GENMIDI.OP2 형식이 아닙니다 (#OPL_II# 없음).");
        var b = new GmBank { Source = Path.GetFileName(path) };
        for (int i = 0; i < 175; i++)
        {
            int at = 8 + i * 36;
            int flags = d[at] | (d[at + 1] << 8);
            string name = 8 + 175 * 36 + i * 32 + 32 <= d.Length ? MidiFiles.Text(d, 8 + 175 * 36 + i * 32, 32) : "";
            var v1 = Op2Voice(d, at + 4, name, out int o1);
            var p = new GmPatch
            {
                Name = name,
                FineTune = d[at + 2],
                FixedNote = (flags & 1) != 0 ? d[at + 3] : -1,
                Voice1 = v1,
                Offset1 = o1,
            };
            if ((flags & 4) != 0) { p.Voice2 = Op2Voice(d, at + 20, name, out int p2); p.Offset2 = p2; }
            if (i < 128) b.Melodic[i] = p;
            else b.Drums[GeneralMidi.FirstDrum + i - 128] = p;
        }
        return b;
    }

    /// <summary>DMX 보이스 16바이트: 모듈레이터(20 · 60 · 80 · E0 · KSL · TL) · C0 · 캐리어(같은 6) · ? · 음 옮김(int16).</summary>
    private static AdlibInstrument Op2Voice(byte[] d, int at, string name, out int offset)
    {
        OplOperator Op(int i)
        {
            var o = OplOperator.FromRegisters(new byte[] { d[i], (byte)((d[i + 4] & 0xC0) | (d[i + 5] & 0x3F)), d[i + 1], d[i + 2], d[i + 3] }, 0);
            return o;
        }
        var ins = new AdlibInstrument { Name = name, Modulator = Op(at), Carrier = Op(at + 7) };
        int c0 = d[at + 6];
        ins.Modulator.Feedback = (c0 >> 1) & 7;
        ins.Modulator.Con = (c0 & 1) != 0 ? 0 : 1;   // AdlibInstrument: Con 0 = 가산 (레지스터 비트와 반대)
        ins.Carrier.Con = ins.Modulator.Con;
        offset = (short)(d[at + 14] | (d[at + 15] << 8));
        return ins;
    }

    /// <summary>GM 번호별 BNK 악기 이름 후보 (앞에서부터 찾는다). STANDARD.BNK 기준.</summary>
    private static readonly string[][] BankNames = BuildNames();

    private static string[][] BuildNames()
    {
        string[] fam(params string[] n) { return n; }
        var t = new string[128][];
        void set(int from, int to, string[] names) { for (int i = from; i <= to; i++) t[i] = names; }
        set(0, 3, fam("PIANO1", "PIANO", "GRDPIANO"));
        set(4, 5, fam("ELPIANO1", "EPIANO1A", "ELPIANO2"));
        set(6, 6, fam("HARPSI1", "HARPSI2"));
        set(7, 7, fam("ELCLAV1", "CLAVECIN"));
        set(8, 8, fam("CELESTA", "CELESTA1"));
        set(9, 10, fam("BELLS", "BELL", "CHIME"));
        set(11, 11, fam("VIBRA1", "VIBE2"));
        set(12, 12, fam("MARIMBA", "MARIMBA1"));
        set(13, 13, fam("XYLO1", "XYLOPONE"));
        set(14, 14, fam("CHIME", "BELLS"));
        set(15, 15, fam("HARPSI3", "BANJO1"));
        set(16, 18, fam("ORGAN1", "ELORGAN1", "ORGAN2"));
        set(19, 19, fam("ORGAN3", "ORGAN1"));
        set(20, 20, fam("ORGAN4", "ORGAN1"));
        set(21, 21, fam("ACCORDN", "ACCORDN1"));
        set(22, 22, fam("HARMON1", "HARMON2"));
        set(23, 23, fam("ACCORDN2", "ACCORDN"));
        set(24, 25, fam("GUITAR1", "ACGUIT1", "STEELGT"));
        set(26, 26, fam("JAZZGUIT", "JAZGUIT1"));
        set(27, 28, fam("ELGUIT1", "CLEARGT", "MUTE-GT1"));
        set(29, 29, fam("FUZGUIT1", "OV-DRIVE", "DISTGT"));
        set(30, 30, fam("DISTGT", "HEAVYMT1", "FUZGUIT2"));
        set(31, 31, fam("ELGUIT2", "GUITAR2"));
        set(32, 32, fam("BASS1", "BASS"));
        set(33, 34, fam("ELBASS1", "BASS2", "BASS1"));
        set(35, 35, fam("JAZZBASS", "BASS1"));
        set(36, 37, fam("BASS-POP", "POPBASS1", "ELBASS2"));
        set(38, 39, fam("SYNBASS1", "BASSSYN1", "SYNBASS2"));
        set(40, 40, fam("VIOLIN1", "VIOLIN"));
        set(41, 41, fam("ALTOVIOL", "VIOLIN2"));
        set(42, 42, fam("CELLO", "CELLO1"));
        set(43, 43, fam("CONTRAB", "BASSVIOL"));
        set(44, 44, fam("STRINGS1", "STRING1"));
        set(45, 45, fam("VLNPIZZ1", "GTRPIZZ1", "HARP"));
        set(46, 46, fam("HARP", "HARP1"));
        set(47, 47, fam("TIMPANI", "KETTLE1"));
        set(48, 49, fam("STRINGS1", "STRING1", "STRINGS2"));
        set(50, 51, fam("SYNVIO1", "STRING2", "STRINGS2"));
        set(52, 53, fam("VOICE", "VOCAL1", "CHMMM1"));
        set(54, 54, fam("VOXSYNTH", "VOICE2"));
        set(55, 55, fam("ORCHHIT1", "ORCH001"));
        set(56, 56, fam("TRUMPET1", "TRUMPET"));
        set(57, 57, fam("TROMBONE", "TROMB1"));
        set(58, 58, fam("TUBA1", "TUBA2"));
        set(59, 59, fam("TRUMPET2", "TRUMPET1"));
        set(60, 60, fam("FRHORN1", "HORN1"));
        set(61, 61, fam("BRASS1", "BRASS2"));
        set(62, 63, fam("SFTBRSS1", "FMBRSS1", "BRASS2"));
        set(64, 64, fam("SAX-SOP1", "SAX1"));
        set(65, 65, fam("ALTOSAX1", "SAX1"));
        set(66, 66, fam("TENSAX", "SAX2"));
        set(67, 67, fam("SAX3", "TENSAX2"));
        set(68, 68, fam("OBOE", "OBOE1"));
        set(69, 69, fam("OBOE2", "OBOE"));
        set(70, 70, fam("BASSOON1", "BASSOON2"));
        set(71, 71, fam("CLARINET", "CLARINE1"));
        set(72, 72, fam("PICCOLO", "PICCOLO1"));
        set(73, 73, fam("FLUTE1", "FLUTE"));
        set(74, 74, fam("FLUTE2", "FLUTE1"));
        set(75, 75, fam("PANFLUTE", "FLUTE3"));
        set(76, 77, fam("FLUTE3", "SFTFL2", "FLUTE1"));
        set(78, 78, fam("WHISTLE", "WHISTLE2"));
        set(79, 79, fam("OCARINA", "FMOCARIN"));
        set(80, 87, fam("SYNTH1", "LEAD1", "SYN1"));
        set(88, 95, fam("SYNTH2", "STRINGS2", "SYN2"));
        set(96, 103, fam("SYNTH3", "SPACE1", "SYN3"));
        set(104, 104, fam("SITAR1", "SITAR2"));
        set(105, 105, fam("BANJO1", "BANJO2"));
        set(106, 106, fam("KOTO1", "BANJO1"));
        set(107, 107, fam("KOTO1", "HARP"));
        set(108, 108, fam("MARIMBA", "XYLO1"));
        set(109, 109, fam("BAGPIPE", "BAGPIPE1"));
        set(110, 110, fam("VIOLIN2", "VIOLIN1"));
        set(111, 111, fam("OBOE3", "OBOE"));
        set(112, 112, fam("BELL", "BELLS"));
        set(113, 113, fam("AGOGO-L", "COWBELL1"));
        set(114, 114, fam("PANBELL1", "MARIMBA"));
        set(115, 115, fam("WOOD1", "CASTANET"));
        set(116, 117, fam("TOM1", "TOM2"));
        set(118, 118, fam("SYN_DRUM", "TOM1"));
        set(119, 119, fam("CYMBAL1", "CYMBAL"));
        set(120, 127, fam("NOISE1", "SEAWAVE", "HELICPTR"));
        return t;
    }

    /// <summary>GM 드럼 → (BNK 이름 후보, 칠 음).</summary>
    private static (string[] names, int note) DrumFor(int n)
    {
        switch (n)
        {
            case 35: case 36: return (new[] { "BDRUM1", "BDRUM", "BASSDRUM" }, 36);
            case 37: return (new[] { "RIMSHOT", "SNARE1" }, 60);
            case 38: case 40: return (new[] { "SNARE1", "SNARE" }, 60);
            case 39: return (new[] { "CLAP1", "CLAPPING", "SNARE1" }, 60);
            case 41: case 43: case 45: case 47: case 48: case 50:
                return (new[] { "TOM1", "TOM2" }, 41 + (n - 41) * 2);
            case 42: case 44: return (new[] { "HIHAT1", "HIHAT" }, 72);
            case 46: return (new[] { "OPENHAT", "OPENHI1", "HIHAT1" }, 72);
            case 49: case 52: case 55: case 57: return (new[] { "CYMBAL1", "CYMBAL" }, 72);
            case 51: case 53: case 59: return (new[] { "CYMRIDE", "CYMBAL2", "CYMBAL1" }, 76);
            case 54: return (new[] { "KABASA", "HIHAT1" }, 72);
            case 56: return (new[] { "COWBELL1", "AGOGO-L" }, 72);
            case 60: case 61: return (new[] { "BONGO-HI", "BONGO-LO", "TOM1" }, n == 60 ? 72 : 67);
            case 62: case 63: case 64: return (new[] { "CONGA-HI", "CONGA-LO", "TOM1" }, 70 - (n - 62) * 3);
            case 65: case 66: return (new[] { "TIMBLE", "TOM1" }, n == 65 ? 67 : 62);
            case 67: case 68: return (new[] { "AGOGO-L", "COWBELL1" }, n == 67 ? 79 : 74);
            case 69: case 70: return (new[] { "KABASA", "HIHAT1" }, 76);
            case 71: case 72: return (new[] { "WHISTLE", "WHISTLE2" }, 84);
            case 75: case 76: case 77: return (new[] { "WOOD1", "CASTANET" }, 72 + (77 - n) * 2);
            case 80: case 81: return (new[] { "TRIANGLE", "BELL" }, 84);
            default: return (new[] { "HIHAT1", "SNARE1" }, 72);
        }
    }

    /// <summary>BNK(예: STANDARD.BNK)에서 GM 번호마다 비슷한 이름의 악기를 골라 만든다. 없으면 첫 악기 / 기본 악기.</summary>
    public static GmBank FromBank(InstrumentBank bank)
    {
        var b = new GmBank { Source = bank != null && bank.FilePath != null ? Path.GetFileName(bank.FilePath) + " (이름으로 맞춤)" : "기본 악기" };
        AdlibInstrument fallback = bank?.Instruments.FirstOrDefault(i => i.IsUsed && !i.Percussive) ?? AdlibInstrument.CreateDefault("piano1");
        AdlibInstrument find(string[] names)
        {
            if (bank != null)
                foreach (var n in names) { var i = bank.Find(n); if (i != null && i.IsUsed) return i; }
            return null;
        }
        for (int p = 0; p < 128; p++)
        {
            var ins = find(BankNames[p]) ?? fallback;
            b.Melodic[p] = new GmPatch { Name = ins.Name, Voice1 = ins };
        }
        for (int n = GeneralMidi.FirstDrum; n <= GeneralMidi.LastDrum; n++)
        {
            var (names, note) = DrumFor(n);
            var ins = find(names) ?? fallback;
            b.Drums[n] = new GmPatch { Name = ins.Name, Voice1 = ins, FixedNote = note };
        }
        return b;
    }
}
