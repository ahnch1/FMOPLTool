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
        // 웨이브폼은 OPL3(4-op · SOP) 0~7까지 받는다. OPL2 재생은 아래 2비트만 쓴다
        int v = Calc.Clamp(value, info.Min, p == OperatorParameter.WaveForm ? 7 : info.Max);
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

    /// <summary>OPL 레지스터 5개 (20 · 40 · 60 · 80 · E0)로. E0은 OPL3 웨이브폼 0~7.</summary>
    public void ToRegisters(byte[] d, int i)
    {
        d[i] = (byte)((Am != 0 ? 0x80 : 0) | (Vib != 0 ? 0x40 : 0) | (Eg != 0 ? 0x20 : 0) | (Ksr != 0 ? 0x10 : 0) | (Multiple & 0x0F));
        d[i + 1] = (byte)(((Ksl & 3) << 6) | (TotalLevel & 0x3F));
        d[i + 2] = (byte)(((Attack & 0x0F) << 4) | (Decay & 0x0F));
        d[i + 3] = (byte)(((Sustain & 0x0F) << 4) | (ReleaseRate & 0x0F));
        d[i + 4] = (byte)(WaveSelect & 7);
    }

    /// <summary>OPL 레지스터 5개 (20 · 40 · 60 · 80 · E0)에서. Feedback · Con은 C0에서 따로 채운다.</summary>
    public static OplOperator FromRegisters(byte[] d, int i)
    {
        return new OplOperator
        {
            Am = (d[i] >> 7) & 1, Vib = (d[i] >> 6) & 1, Eg = (d[i] >> 5) & 1, Ksr = (d[i] >> 4) & 1, Multiple = d[i] & 0x0F,
            Ksl = d[i + 1] >> 6, TotalLevel = d[i + 1] & 0x3F,
            Attack = d[i + 2] >> 4, Decay = d[i + 2] & 0x0F,
            Sustain = d[i + 3] >> 4, ReleaseRate = d[i + 3] & 0x0F,
            WaveSelect = d[i + 4] & 7,
        };
    }
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
    /// <summary>
    /// SOP 4-op 악기의 오퍼레이터 3·4 (SOP 데이터 [11..21] 그대로 11바이트: 20/40/60/80/E0, C0, 20/40/60/80/E0). 2-op 악기는 null.
    /// 오퍼레이터 1·2는 Modulator/Carrier에 들어 있다. BNK/INS에는 저장되지 않는다.
    /// </summary>
    public byte[] Sop4OpPair { get; set; }
    /// <summary>SOP 악기의 긴 이름 (19바이트, 보존용).</summary>
    public string LongName { get; set; }

    public bool Is4Op { get { return Sop4OpPair != null && Sop4OpPair.Length >= 11; } }

    /// <summary>4-op 둘째 짝 (오퍼레이터 3 = 모듈레이터 자리, 4 = 캐리어 자리)을 오퍼레이터로 푼다. Feedback · Con은 짝의 C0에서 (Con 0 = 가산).</summary>
    public void GetPairOperators(out OplOperator op3, out OplOperator op4)
    {
        var d = Sop4OpPair ?? new byte[11];
        op3 = OplOperator.FromRegisters(d, 0);
        op4 = OplOperator.FromRegisters(d, 6);
        op3.Feedback = (d[5] >> 1) & 7;
        op3.Con = op4.Con = (d[5] & 1) != 0 ? 0 : 1;
    }

    /// <summary>4-op 둘째 짝을 레지스터 값으로 되돌려 쓴다.</summary>
    public void SetPairOperators(OplOperator op3, OplOperator op4)
    {
        var d = new byte[11];
        op3.ToRegisters(d, 0);
        d[5] = (byte)(((op3.Feedback & 7) << 1) | (op3.Con == 0 ? 1 : 0));
        op4.ToRegisters(d, 6);
        Sop4OpPair = d;
    }

    /// <summary>4-op 연결 이름 (OPL3 CNT 비트 두 개: 첫 짝 = Additive, 둘째 짝 = 짝 C0 비트 0).</summary>
    public string FourOpAlgorithm
    {
        get
        {
            if (!Is4Op) return Additive ? "1 + 2" : "1 → 2";
            bool c2 = (Sop4OpPair[5] & 1) != 0;
            if (!Additive && !c2) return "1→2→3→4";
            if (Additive && !c2) return "1 + 2→3→4";
            if (!Additive && c2) return "1→2 + 3→4";
            return "1 + 2→3 + 4";
        }
    }

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
        c.Sop4OpPair = Sop4OpPair == null ? null : (byte[])Sop4OpPair.Clone();
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
