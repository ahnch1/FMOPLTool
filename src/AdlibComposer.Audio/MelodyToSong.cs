using System;
using System.Collections.Generic;
using System.Linq;
using AdlibComposer.Core;

namespace AdlibComposer.Audio;

/// <summary>채보한 음(초) → 곡(틱): 템포 · 첫 박 위치로 박에 맞추고 격자에 붙인다. 보이스 1 하나에 싣는다.</summary>
public static class MelodyToSong
{
    /// <param name="song">빈 곡 (악기 · 박당 틱이 정해진 것). 보이스 0의 음을 채운다.</param>
    /// <param name="bpm">템포 (4분음표 = 1박).</param>
    /// <param name="beatOffset">박이 시작되는 시각 (초). 첫 음이 1/4박 넘게 앞이면 한 박씩 당겨서 음이 0틱보다 앞서지 않게.</param>
    /// <param name="gridDivision">격자: 4 = 4분음표, 8 = 8분음표, 16 = 16분음표 (0 = 격자 없음, 틱 그대로).</param>
    /// <param name="transpose">반음 옮김 (보통 0).</param>
    public static Song Build(Song song, IList<MelodyNote> notes, double bpm, double beatOffset, int gridDivision, int transpose = 0)
    {
        bpm = Math.Max(20, Math.Min(400, bpm));
        double beat = 60.0 / bpm;
        // 첫 음이 첫 박보다 (1/4박 넘게) 앞이면 박을 통째로 당긴다 (몇 ms 이른 것은 반올림으로 첫 박에 붙는다)
        if (notes.Count > 0 && notes[0].Start < beatOffset - beat / 4)
            beatOffset -= Math.Ceiling((beatOffset - beat / 4 - notes[0].Start) / beat - 1e-9) * beat;
        int tpb = song.TickBeat;
        int grid = gridDivision > 0 ? Math.Max(1, tpb * 4 / gridDivision) : 1;
        int Quant(double sec)
        {
            double ticks = (sec - beatOffset) / beat * tpb;
            return (int)Math.Round(ticks / grid) * grid;
        }
        song.BasicTempo = (float)Math.Round(bpm);
        var voice = song.Voices[0];
        voice.Notes.Clear();
        int lastEnd = 0;
        foreach (var n in notes.OrderBy(x => x.Start))
        {
            int s = Math.Max(0, Quant(n.Start));
            int e = Quant(n.End);
            if (e <= s) e = s + grid;
            int pitch = Math.Max(Song.MinNote, Math.Min(Song.MaxNote, n.Midi + transpose));
            if (s < lastEnd)
            {
                // 앞 음과 겹침: 같은 자리에서 시작하면 더 긴 쪽을 남기고, 아니면 앞 음을 줄인다
                var prev = voice.Notes[voice.Notes.Count - 1];
                if (s <= prev.StartTick) { if (e - s > prev.Duration) { prev.Duration = e - prev.StartTick; prev.Pitch = pitch; lastEnd = prev.EndTick; } continue; }
                prev.Duration = s - prev.StartTick;
            }
            voice.Notes.Add(new Note { StartTick = s, Duration = e - s, Pitch = pitch });
            lastEnd = e;
        }
        return song;
    }
}
