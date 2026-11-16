using System;
using System.Collections.Generic;
using System.Linq;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// 칩 기록 곡(VGM · NSF · KSS · HES · GBS · S98 · SPC · hoot …) → 작업 곡(ROL처럼 보이스 · 노트): Composer · Note · Orgel Note에서 보고 고치게.
/// <para>방법: <see cref="VgmPlayer"/>로 곡을 소리 없이 끝까지 돌리며 한 틱(기본 120 BPM · 박당 48틱 ≈ 10.4ms)마다 채널별 지금 음(Jukebox 건반 표시와 같은 것)을 읽는다.
/// 음이 바뀌거나 다시 쳐지면(노트온 수가 늘면) 새 노트, 소리가 없어지면 노트 끝. 2틱보다 짧은 조각(비브라토 · 슬라이드 · 아르페지오의 잔 조각)은 앞 노트에 붙인다.</para>
/// <para>음높이가 있는 채널만 (노이즈 · PCM · 드럼은 뺌). 노트가 있는 채널이 9개 이하면 멜로딕 9보이스 곡, 더 많으면 SOP 20보이스 곡 (많으면 노트가 많은 20개).
/// 음 세기 · 음색은 옮기지 않는다 (악기 = 보이스 기본 악기). 템포 변화는 모르므로 고정 템포로 (Note의 양자화로 정리).</para>
/// </summary>
public static class VgmToSong
{
    /// <summary>바꾸는 최대 길이 (초): 루프 곡은 한 번 더 돈 길이까지, 그래도 이것까지.</summary>
    public const double MaxSeconds = 600;

    public sealed class Result
    {
        public Song Song;
        /// <summary>노트가 나온 채널 수 (곡에 실은 것보다 많을 수 있다).</summary>
        public int ChannelsWithNotes;
        /// <summary>곡에 실은 채널 (보이스 순서대로): 칩 채널 이름.</summary>
        public readonly List<string> Used = new List<string>();
        public int Notes;
        public double Seconds;
    }

    /// <param name="newSong">빈 곡을 만드는 함수 (보이스 기본 악기가 든 것). null이면 악기 없는 빈 곡.</param>
    /// <param name="progress">진행 0~1 (돌린 시간 / 바꿀 길이), 0.1초 분량마다쯤. 다른 스레드에서 불릴 수 있다.</param>
    /// <param name="cancel">true를 돌려주면 그만두고 null을 돌려준다.</param>
    public static Result Convert(VgmSong v, Func<Song> newSong = null, double maxSeconds = MaxSeconds, int tickBeat = 48, double bpm = 120,
        Action<double> progress = null, Func<bool> cancel = null)
    {
        var song = newSong != null ? newSong() : new Song();
        song.TickBeat = tickBeat;
        song.BasicTempo = (float)bpm;
        song.TempoEvents.Clear();
        double ticksPerSecond = bpm / 60.0 * tickBeat;
        double seconds = Math.Min(maxSeconds, VgmPlayer.PlaySeconds(v));

        var player = new VgmPlayer();
        player.Start(v, 0, null);
        int channels = Math.Min(player.Channels.Count, PlaybackOptions.Voices);
        var states = new VoiceState[PlaybackOptions.Voices];
        var notes = new List<Note>[channels];
        var open = new Note[channels];
        var lastCount = new int[channels];
        for (int c = 0; c < channels; c++) notes[c] = new List<Note>();

        int totalTicks = (int)Math.Ceiling(seconds * ticksPerSecond);
        double samplesPerTick = VgmPlayer.Rate / ticksPerSecond, due = 0;
        int reportEvery = Math.Max(1, (int)(ticksPerSecond / 10));
        for (int tick = 0; tick <= totalTicks; tick++)
        {
            if (tick % reportEvery == 0)
            {
                if (cancel != null && cancel()) { player.Stop(); return null; }
                progress?.Invoke(totalTicks > 0 ? tick / (double)totalTicks : 1);
            }
            due += samplesPerTick;
            while (due >= 1) { due -= 1; player.Step(); player.Generate(out _, out _); }
            bool alive = player.IsPlaying && tick < totalTicks;
            player.CopyStates(states);
            for (int c = 0; c < channels; c++)
            {
                int n = alive ? states[c].Note : -1;
                bool retrigger = states[c].NoteOnCount != lastCount[c];
                lastCount[c] = states[c].NoteOnCount;
                var cur = open[c];
                if (cur != null && (n != cur.Pitch || retrigger || n < 0))
                {
                    cur.Duration = tick - cur.StartTick;
                    open[c] = null;
                }
                if (n >= 0 && open[c] == null)
                {
                    open[c] = new Note { StartTick = tick, Pitch = n };
                    notes[c].Add(open[c]);
                }
            }
            if (!alive) break;
        }
        player.Stop();
        progress?.Invoke(1);
        for (int c = 0; c < channels; c++)
        {
            if (open[c] != null && open[c].Duration <= 0) open[c].Duration = Math.Max(1, totalTicks - open[c].StartTick);
            notes[c] = Clean(notes[c]);
        }

        // 노트가 있는 채널: 9개 이하면 멜로딕 9, 넘으면 SOP 20 (노트가 많은 것)
        var withNotes = Enumerable.Range(0, channels).Where(c => notes[c].Count > 0).ToList();
        var r = new Result { Song = song, ChannelsWithNotes = withNotes.Count, Seconds = seconds };
        bool sop = withNotes.Count > Song.MelodicVoices;
        int room = sop ? Song.TotalVoices : Song.MelodicVoices;
        var pick = withNotes.OrderByDescending(c => notes[c].Count).Take(room).OrderBy(c => c).ToList();
        song.IsMelodic = true;
        song.IsSop = sop;
        foreach (var vt in song.Voices) vt.Notes.Clear();
        for (int i = 0; i < pick.Count; i++)
        {
            int c = pick[i];
            foreach (var n in notes[c])
            {
                int p = n.Pitch;
                while (p < Song.MinNote) p += 12;
                while (p > Song.MaxNote) p -= 12;
                song.Voices[i].Notes.Add(new Note { StartTick = n.StartTick, Duration = n.Duration, Pitch = p });
            }
            r.Used.Add(player.Channels[c].Label);
            r.Notes += notes[c].Count;
        }
        return r;
    }

    /// <summary>2틱보다 짧은 조각은 바로 앞 노트(붙어 있으면)에 붙이고, 그래도 짧은 것은 1틱 노트로 남긴다 (스타카토 · 짧은 타격).</summary>
    private static List<Note> Clean(List<Note> l)
    {
        var o = new List<Note>(l.Count);
        foreach (var n in l)
        {
            if (n.Duration <= 0) n.Duration = 1;
            var prev = o.Count > 0 ? o[o.Count - 1] : null;
            if (n.Duration < 2 && prev != null && prev.EndTick == n.StartTick) { prev.Duration += n.Duration; continue; }
            if (prev != null && prev.Duration < 2 && prev.EndTick == n.StartTick && prev.Pitch != n.Pitch && o.Count > 1 && o[o.Count - 2].EndTick == prev.StartTick)
            {
                // 앞 조각이 짧았으면 그 앞 노트에 붙인다 (음이 미끄러지는 사이)
                o[o.Count - 2].Duration += prev.Duration;
                o.RemoveAt(o.Count - 1);
            }
            o.Add(n);
        }
        return o;
    }
}
