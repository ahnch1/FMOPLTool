using System;
using System.Collections.Generic;

namespace AdlibComposer.Formats;

/// <summary>곡이 어떤 규격으로 만들어졌는지 (곡 안의 리셋 SysEx로 판단).</summary>
public enum MidiStandard
{
    /// <summary>리셋 SysEx 없음 (보통 GM으로 듣는다).</summary>
    None,
    Gm,
    Gm2,
    /// <summary>Roland GS (GS 리셋 또는 GS 블록 SysEx).</summary>
    Gs,
    /// <summary>Yamaha XG (XG System On).</summary>
    Xg,
    /// <summary>Roland MT-32 / LA 계열 SysEx (모델 0x16).</summary>
    Mt32,
}

/// <summary>SysEx 메시지 종류 (재생기가 알아듣는 것).</summary>
public enum SysExKind
{
    Other,
    /// <summary>GM System On: F0 7E dev 09 01 F7.</summary>
    GmOn,
    /// <summary>GM System Off: F0 7E dev 09 02 F7.</summary>
    GmOff,
    /// <summary>GM2 System On: F0 7E dev 09 03 F7.</summary>
    Gm2On,
    /// <summary>GS Reset: F0 41 dev 42 12 40 00 7F 00 41 F7.</summary>
    GsReset,
    /// <summary>GS "Use for rhythm part": F0 41 dev 42 12 40 1x 15 vv sum F7 (vv 0 = 멜로디, 1 · 2 = 드럼 맵).</summary>
    GsRhythmPart,
    /// <summary>XG System On: F0 43 1n 4C 00 00 7E 00 F7.</summary>
    XgOn,
}

/// <summary>
/// GM · GM2 · GS · XG 리셋과 GS 드럼 파트 SysEx 알아보기, 리셋 메시지, 곡 규격 판단, GS 드럼 세트 이름.
/// SysEx 바이트 배열은 F0으로 시작하고 F7로 끝난다 (<see cref="MidiEvent.SysEx"/>).
/// </summary>
public static class MidiSysEx
{
    public static readonly byte[] GmOn = { 0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7 };
    public static readonly byte[] Gm2On = { 0xF0, 0x7E, 0x7F, 0x09, 0x03, 0xF7 };
    public static readonly byte[] GsReset = { 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 };
    public static readonly byte[] XgOn = { 0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7 };

    /// <summary>XG 드럼 뱅크 MSB (127 = 드럼, 126 = SFX 드럼).</summary>
    public const int XgDrumBank = 127, XgSfxBank = 126;
    /// <summary>GM2 드럼 뱅크 MSB.</summary>
    public const int Gm2DrumBank = 120;

    /// <summary>
    /// 메시지 종류. GsRhythmPart면 channel = MIDI 채널(0~15), value = 0(멜로디) · 1 · 2(드럼 맵). 다른 종류면 둘 다 -1.
    /// </summary>
    public static SysExKind Classify(byte[] sx, out int channel, out int value)
    {
        channel = value = -1;
        if (sx == null || sx.Length < 5 || sx[0] != 0xF0) return SysExKind.Other;
        // 유니버설 비실시간: F0 7E dev 09 nn
        if (sx[1] == 0x7E && sx.Length >= 6 && sx[3] == 0x09)
        {
            switch (sx[4])
            {
                case 0x01: return SysExKind.GmOn;
                case 0x02: return SysExKind.GmOff;
                case 0x03: return SysExKind.Gm2On;
            }
            return SysExKind.Other;
        }
        // Roland GS: F0 41 dev 42 12 aa aa aa dd … sum F7
        if (sx[1] == 0x41 && sx.Length >= 11 && sx[3] == 0x42 && sx[4] == 0x12)
        {
            int a1 = sx[5], a2 = sx[6], a3 = sx[7];
            if (a1 == 0x40 && a2 == 0x00 && a3 == 0x7F && sx[8] == 0x00) return SysExKind.GsReset;
            if (a1 == 0x40 && (a2 & 0xF0) == 0x10 && a3 == 0x15)
            {
                channel = GsBlockToChannel(a2 & 0x0F);
                value = sx[8];
                return SysExKind.GsRhythmPart;
            }
            return SysExKind.Other;
        }
        // Yamaha XG: F0 43 1n 4C 00 00 7E 00 F7
        if (sx[1] == 0x43 && sx.Length >= 8 && (sx[2] & 0xF0) == 0x10 && sx[3] == 0x4C && sx[4] == 0x00 && sx[5] == 0x00 && sx[6] == 0x7E)
            return SysExKind.XgOn;
        return SysExKind.Other;
    }

    public static SysExKind Classify(byte[] sx) { return Classify(sx, out _, out _); }

    /// <summary>GS 파트 블록 번호(주소 40 1x의 x) → MIDI 채널: 0 = 파트 10 (채널 9), 1~9 = 채널 0~8, A~F = 채널 10~15.</summary>
    public static int GsBlockToChannel(int block)
    {
        block &= 0x0F;
        if (block == 0) return 9;
        return block <= 9 ? block - 1 : block;
    }

    public static int ChannelToGsBlock(int channel)
    {
        if (channel == 9) return 0;
        return channel < 9 ? channel + 1 : channel;
    }

    /// <summary>GS DT1 메시지 (주소 3바이트 + 값 1바이트, 체크섬 계산).</summary>
    public static byte[] GsData(int a1, int a2, int a3, int value)
    {
        int sum = (128 - ((a1 + a2 + a3 + value) & 0x7F)) & 0x7F;
        return new byte[] { 0xF0, 0x41, 0x10, 0x42, 0x12, (byte)a1, (byte)a2, (byte)a3, (byte)value, (byte)sum, 0xF7 };
    }

    /// <summary>Roland 체크섬이 맞는가 (주소 · 값의 합 + 체크섬 ≡ 0 mod 128).</summary>
    public static bool RolandChecksumOk(byte[] sx)
    {
        if (sx == null || sx.Length < 8 || sx[1] != 0x41 || sx[sx.Length - 1] != 0xF7) return false;
        int sum = 0;
        for (int i = 5; i < sx.Length - 1; i++) sum += sx[i];
        return (sum & 0x7F) == 0;
    }

    /// <summary>
    /// 곡의 규격: XG On → XG, GS 리셋 · GS 블록 → GS, GM2 On → GM2, MT-32 SysEx → MT-32, GM On → GM, 없으면 None.
    /// 여러 개면 위 순서가 앞선다 (GS 곡도 처음에 GM On을 함께 보내는 일이 많다).
    /// </summary>
    public static MidiStandard Detect(IEnumerable<MidiEvent> events)
    {
        bool gm = false, gm2 = false, gs = false, xg = false, mt32 = false;
        foreach (var e in events)
        {
            if (!e.IsSysEx || e.SysEx == null) continue;
            var sx = e.SysEx;
            switch (Classify(sx))
            {
                case SysExKind.GmOn: gm = true; break;
                case SysExKind.Gm2On: gm2 = true; break;
                case SysExKind.GsReset: case SysExKind.GsRhythmPart: gs = true; break;
                case SysExKind.XgOn: xg = true; break;
                default:
                    if (sx.Length > 4 && sx[1] == 0x41 && sx[3] == 0x42) gs = true;          // 다른 GS 블록 (리버브 · 파트 설정)
                    else if (sx.Length > 4 && sx[1] == 0x41 && sx[3] == 0x16) mt32 = true;   // MT-32 / CM-32L
                    else if (sx.Length > 4 && sx[1] == 0x43 && sx[3] == 0x4C) xg = true;     // XG 파라미터
                    break;
            }
        }
        if (xg) return MidiStandard.Xg;
        if (gs) return MidiStandard.Gs;
        if (gm2) return MidiStandard.Gm2;
        if (mt32) return MidiStandard.Mt32;
        if (gm) return MidiStandard.Gm;
        return MidiStandard.None;
    }

    public static string Name(MidiStandard s)
    {
        switch (s)
        {
            case MidiStandard.Gm: return "GM";
            case MidiStandard.Gm2: return "GM2";
            case MidiStandard.Gs: return "GS";
            case MidiStandard.Xg: return "XG";
            case MidiStandard.Mt32: return "MT-32";
        }
        return "";
    }

    /// <summary>GS 드럼 세트 이름 (드럼 채널의 프로그램 번호, 0부터).</summary>
    public static string GsDrumKitName(int program)
    {
        switch (program & 127)
        {
            case 0: return "Standard";
            case 8: return "Room";
            case 16: return "Power";
            case 24: return "Electronic";
            case 25: return "TR-808";
            case 32: return "Jazz";
            case 40: return "Brush";
            case 48: return "Orchestra";
            case 56: return "SFX";
            case 127: return "CM-64/32L";
        }
        return "Kit " + ((program & 127) + 1);
    }
}
