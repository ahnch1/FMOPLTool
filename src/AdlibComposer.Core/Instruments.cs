using System;
using System.Collections.Generic;

namespace AdlibComposer.Core;

/// <summary>Instrument Maker 화면의 편집 항목 (화면 순서 그대로).</summary>
public enum OperatorParameter
{
    AttackRate, DecayRate, SustainLevel, ReleaseRate,
    SustainingSound, EnvelopeScaling, FrequencyMultiplier, ModulationFeedback,
    PitchVibrato, OutputLevel, LevelScaling, AmplitudeVibrato, WaveForm
}

public sealed class ParameterInfo
{
    public OperatorParameter Id { get; }
    public string Label { get; }
    public int Min { get; }
    public int Max { get; }
    public bool IsToggle { get; }
    public bool ModulatorOnly { get; }

    private ParameterInfo(OperatorParameter id, string label, int min, int max, bool toggle = false, bool modOnly = false)
    {
        Id = id; Label = label; Min = min; Max = max; IsToggle = toggle; ModulatorOnly = modOnly;
    }

    public static readonly IReadOnlyList<ParameterInfo> All = new ParameterInfo[]
    {
        new ParameterInfo(OperatorParameter.AttackRate, "Attack Rate", 0, 15),
        new ParameterInfo(OperatorParameter.DecayRate, "Decay Rate", 0, 15),
        new ParameterInfo(OperatorParameter.SustainLevel, "Sustain Level", 0, 15),
        new ParameterInfo(OperatorParameter.ReleaseRate, "Release Rate", 0, 15),
        new ParameterInfo(OperatorParameter.SustainingSound, "Sustaining Sound", 0, 1, true),
        new ParameterInfo(OperatorParameter.EnvelopeScaling, "Envelope Scaling", 0, 1, true),
        new ParameterInfo(OperatorParameter.FrequencyMultiplier, "Frequency Multiplier", 0, 15),
        new ParameterInfo(OperatorParameter.ModulationFeedback, "Modulation Feedback", 0, 7, false, true),
        new ParameterInfo(OperatorParameter.PitchVibrato, "Pitch Vibrato", 0, 1, true),
        new ParameterInfo(OperatorParameter.OutputLevel, "Output Level", 0, 63),
        new ParameterInfo(OperatorParameter.LevelScaling, "Level Scaling", 0, 3),
        new ParameterInfo(OperatorParameter.AmplitudeVibrato, "Amplitude Vibrato", 0, 1, true),
        new ParameterInfo(OperatorParameter.WaveForm, "Wave Form", 0, 3),
    };

    public static ParameterInfo Get(OperatorParameter id)
    {
        foreach (var p in All) if (p.Id == id) return p;
        throw new ArgumentOutOfRangeException("id");
    }
}

/// <summary>오퍼레이터 1개 (BNK의 OPLREGS 13필드 + 웨이브셀렉트). 값은 파일에 저장되는 값 그대로.</summary>
public sealed class OplOperator
{
    public int Ksl { get; set; }
    public int Multiple { get; set; } = 1;
    public int Feedback { get; set; }
    public int Attack { get; set; } = 15;
    public int Sustain { get; set; }
    public int Eg { get; set; }
    public int Decay { get; set; }
    public int ReleaseRate { get; set; } = 4;
    /// <summary>BNK/INS의 totalLevel 필드 = OPL TL 레지스터 값 그대로 (0 = 가장 큼, 63 = 무음).
    /// 실제 BNK에서 캐리어가 거의 모두 0인 것으로 확인. 화면의 "Output Level"은 63 - TotalLevel.</summary>
    public int TotalLevel { get; set; } = 0;
    public int Am { get; set; }
    public int Vib { get; set; }
    public int Ksr { get; set; }
    /// <summary>BNK/INS의 connector 필드 (0이면 OPL additive 비트=1). 모듈레이터 쪽 값만 유효.</summary>
    public int Con { get; set; } = 1;
    public int WaveSelect { get; set; }

    public int Get(OperatorParameter p)
    {
        switch (p)
        {
            case OperatorParameter.AttackRate: return Attack;
            case OperatorParameter.DecayRate: return Decay;
            case OperatorParameter.SustainLevel: return Sustain;
            case OperatorParameter.ReleaseRate: return ReleaseRate;
            case OperatorParameter.SustainingSound: return Eg;
            case OperatorParameter.EnvelopeScaling: return Ksr;
            case OperatorParameter.FrequencyMultiplier: return Multiple;
            case OperatorParameter.ModulationFeedback: return Feedback;
            case OperatorParameter.PitchVibrato: return Vib;
            case OperatorParameter.OutputLevel: return 63 - (TotalLevel & 0x3F);
            case OperatorParameter.LevelScaling: return Ksl;
            case OperatorParameter.AmplitudeVibrato: return Am;
            case OperatorParameter.WaveForm: return WaveSelect;
        }
        return 0;
    }

    public void Set(OperatorParameter p, int value)
    {
        var info = ParameterInfo.Get(p);
        int v = Calc.Clamp(value, info.Min, info.Max);
        switch (p)
        {
            case OperatorParameter.AttackRate: Attack = v; break;
            case OperatorParameter.DecayRate: Decay = v; break;
            case OperatorParameter.SustainLevel: Sustain = v; break;
            case OperatorParameter.ReleaseRate: ReleaseRate = v; break;
            case OperatorParameter.SustainingSound: Eg = v; break;
            case OperatorParameter.EnvelopeScaling: Ksr = v; break;
            case OperatorParameter.FrequencyMultiplier: Multiple = v; break;
            case OperatorParameter.ModulationFeedback: Feedback = v; break;
            case OperatorParameter.PitchVibrato: Vib = v; break;
            case OperatorParameter.OutputLevel: TotalLevel = 63 - v; break;
            case OperatorParameter.LevelScaling: Ksl = v; break;
            case OperatorParameter.AmplitudeVibrato: Am = v; break;
            case OperatorParameter.WaveForm: WaveSelect = v; break;
        }
    }

    public OplOperator Clone() { return (OplOperator)MemberwiseClone(); }
}

/// <summary>2-오퍼레이터 FM 악기 1개.</summary>
public sealed class AdlibInstrument
{
    public const int MaxNameLength = 8;

    public string Name { get; set; } = "NEW";
    /// <summary>BNK iPercussive (재생에는 쓰이지 않음, 보존용).</summary>
    public bool Percussive { get; set; }
    /// <summary>BNK iVoiceNum (퍼커션 전용, 보존용).</summary>
    public int VoiceNum { get; set; }
    /// <summary>BNK 이름 목록의 flags != 0.</summary>
    public bool IsUsed { get; set; } = true;
    public OplOperator Modulator { get; set; } = new OplOperator();
    public OplOperator Carrier { get; set; } = new OplOperator();
    /// <summary>INS 파일의 알 수 없는 앞 2바이트 / 뒤 22바이트 (읽은 값 보존).</summary>
    public int InsUnknown { get; set; }
    public byte[] InsTail { get; set; }

    /// <summary>connector 필드가 0이면 OPL additive(두 오퍼레이터 합산).</summary>
    public bool Additive { get { return Modulator.Con == 0; } }

    public OplOperator GetOperator(bool carrier) { return carrier ? Carrier : Modulator; }

    public static string NormalizeName(string name)
    {
        if (name == null) name = "";
        name = name.Trim();
        if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength);
        return name;
    }

    public AdlibInstrument Clone()
    {
        var c = (AdlibInstrument)MemberwiseClone();
        c.Modulator = Modulator.Clone();
        c.Carrier = Carrier.Clone();
        c.InsTail = InsTail == null ? null : (byte[])InsTail.Clone();
        return c;
    }

    /// <summary>들을 수 있는 무난한 기본 악기 (피아노 비슷한 감쇠음).</summary>
    public static AdlibInstrument CreateDefault(string name)
    {
        var ins = new AdlibInstrument { Name = NormalizeName(name) };
        ins.Modulator = new OplOperator
        {
            Multiple = 1, Attack = 15, Decay = 4, Sustain = 7, ReleaseRate = 5, TotalLevel = 23,
            Feedback = 3, Con = 1
        };
        ins.Carrier = new OplOperator
        {
            Multiple = 1, Attack = 15, Decay = 3, Sustain = 6, ReleaseRate = 5, TotalLevel = 0, Con = 1
        };
        return ins;
    }
}

/// <summary>악기 모음 (BNK 1개에 해당).</summary>
public sealed class InstrumentBank
{
    public int VersionMajor { get; set; } = 1;
    public int VersionMinor { get; set; } = 0;
    public string FilePath { get; set; }
    public List<AdlibInstrument> Instruments { get; } = new List<AdlibInstrument>();

    /// <summary>HMI 엔진용 "Version 0.0" 변형 여부.</summary>
    public bool IsHmiVariant { get { return VersionMajor == 0 && VersionMinor == 0; } }

    public int UsedCount
    {
        get { int n = 0; foreach (var i in Instruments) if (i.IsUsed) n++; return n; }
    }

    public AdlibInstrument Find(string name)
    {
        if (name == null) return null;
        foreach (var i in Instruments)
            if (i.IsUsed && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    public string UniqueName(string baseName)
    {
        baseName = AdlibInstrument.NormalizeName(baseName);
        if (baseName.Length == 0) baseName = "NEW";
        if (Find(baseName) == null) return baseName;
        for (int n = 2; n < 1000; n++)
        {
            string suffix = n.ToString();
            string stem = baseName.Length + suffix.Length > AdlibInstrument.MaxNameLength
                ? baseName.Substring(0, AdlibInstrument.MaxNameLength - suffix.Length) : baseName;
            string cand = stem + suffix;
            if (Find(cand) == null) return cand;
        }
        return baseName;
    }

    public AdlibInstrument Add(AdlibInstrument ins)
    {
        ins.Name = UniqueName(ins.Name);
        ins.IsUsed = true;
        Instruments.Add(ins);
        SortByName();
        return ins;
    }

    public void Remove(AdlibInstrument ins) { Instruments.Remove(ins); }

    /// <summary>BNK는 이름 알파벳순 정렬이 필요하다.</summary>
    public void SortByName()
    {
        Instruments.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static InstrumentBank CreateNew()
    {
        var b = new InstrumentBank();
        b.Add(AdlibInstrument.CreateDefault("PIANO"));
        return b;
    }
}
