using System;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// 곡을 VGM으로 내보내기: 재생기를 소리 없이 끝까지 돌리며 OPL 레지스터 쓰기를 시간과 함께 기록한다.
/// ROL · IMS = YM3812 (OPL2), SOP · MIDI(FM) = YMF262 (OPL3). 다른 VGM 플레이어에서 그대로 재생된다.
/// </summary>
public static class VgmExport
{
    /// <summary>OPL2 클럭 (AdLib = 3.579545MHz), OPL3 = 그 4배.</summary>
    public const double Opl2Clock = 3579545, Opl3Clock = 14318180;
    /// <summary>끝나지 않는 곡을 막는 최대 길이 (초).</summary>
    public const double MaxSeconds = 60 * 60;

    /// <summary>ROL / IMS 곡 (Song). SOP로 만든 곡(IsSop)이면 OPL3 SOP로.</summary>
    public static VgmWriter FromSong(Song song, InstrumentBank bank)
    {
        if (song.IsSop) return FromSop(SopConvert.FromSong(song, bank != null ? (Func<string, AdlibInstrument>)bank.Find : n => null), song.Title);
        var chip = new OplChip(false);
        var w = NewWriter(false, song.Title);
        Hook(chip, w, false);
        var synth = new OplSynth(chip);
        var seq = new Sequencer(synth);
        seq.Start(song, name => bank != null ? bank.Find(name) : null, 0, -1, -1);
        Run(chip, w, () => { seq.Step(); return seq.IsPlaying; });
        return w;
    }

    /// <summary>SOP 곡 (OPL3).</summary>
    public static VgmWriter FromSop(SopSong sop, string title = null)
    {
        var chip = new OplChip(true);
        var w = NewWriter(true, title ?? sop.Title);
        Hook(chip, w, true);
        var player = new SopPlayer(chip);
        player.Start(sop, 0);
        Run(chip, w, () => { player.Step(); return player.IsPlaying; });
        return w;
    }

    /// <summary>MIDI 곡을 OPL3 FM(GM 뱅크)으로.</summary>
    public static VgmWriter FromMidi(MidiSong midi, GmBank bank, string title = null)
    {
        var chip = new OplChip(true);
        var w = NewWriter(true, title ?? "");
        Hook(chip, w, true);
        var player = new MidiPlayer(chip) { Bank = bank, Synth = MidiSynth.Fm, Module = MidiModule.Gm };
        try
        {
            player.Start(midi, 0);
            Run(chip, w, () => { player.Step(); return player.IsPlaying; });
        }
        finally { player.Dispose(); }
        return w;
    }

    private static VgmWriter NewWriter(bool opl3, string title)
    {
        var w = new VgmWriter { Title = title ?? "", System = opl3 ? "PC (OPL3)" : "PC (AdLib)", Notes = "FMOPLTools" };
        if (opl3) w.SetClock(VgmWriter.ClockYmf262, Opl3Clock);
        else w.SetClock(VgmWriter.ClockYm3812, Opl2Clock);
        return w;
    }

    // 기다림은 칩 샘플(≈49716Hz) 단위로 모았다가 다음 쓰기 직전에 44100Hz로 바꿔 쓴다
    [ThreadStatic] private static long pendingChip;
    [ThreadStatic] private static double chipToVgm;
    [ThreadStatic] private static double carry;

    private static void Hook(OplChip chip, VgmWriter w, bool opl3)
    {
        pendingChip = 0; carry = 0;
        chipToVgm = (double)VgmSong.Rate / chip.NativeRate;
        chip.WriteHook = (reg, v) =>
        {
            FlushWait(w);
            if (opl3) w.Write((byte)(reg >= 0x100 ? 0x5F : 0x5E), reg & 0xFF, v);
            else w.Write(0x5A, reg & 0xFF, v);
        };
    }

    private static void FlushWait(VgmWriter w)
    {
        if (pendingChip == 0) return;
        double exact = pendingChip * chipToVgm + carry;
        long n = (long)Math.Floor(exact);
        carry = exact - n;
        pendingChip = 0;
        w.Wait(n);
    }

    private static void Run(OplChip chip, VgmWriter w, Func<bool> step)
    {
        long max = (long)(MaxSeconds * chip.NativeRate);
        for (long i = 0; i < max; i++)
        {
            if (!step()) break;
            pendingChip++;
        }
        FlushWait(w);
        chip.WriteHook = null;
    }
}
