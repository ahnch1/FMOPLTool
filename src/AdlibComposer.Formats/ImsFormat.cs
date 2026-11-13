using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdlibComposer.Core;

namespace AdlibComposer.Formats;

/// <summary>
/// IMS 곡 (.IMS, IMPlay). AdLib MUS 포맷과 같은 구조에 악기 이름표가 붙은 형태. 악기 데이터는 같은 이름의 BNK를 쓴다.
/// <code>
/// [헤더 70B] [이벤트 dataSize B] [UINT16 0x7777] [UINT16 nInst] [char[9] × nInst]
/// 헤더: u8 verMajor, u8 verMinor, u32 tuneId, char[30] tuneName(조합형), u8 tickBeat, u8 beatMeasure,
///       u32 totalTick, u32 dataSize, u32 nrCommand(F8 포함), u8[8], u8 soundMode(0 멜로딕/1 리듬),
///       u8 pitchBRange, u16 basicTempo, u8[8]
/// 이벤트: [F8 × n (각 240틱)] [지연 u8] [상태(생략 시 running status)] [데이터]
///   9n note vel  : vel &gt; 0 노트 온 (vel = 보이스 볼륨 0~127), vel = 0 노트 오프
///   8n note vel  : vel &gt; 0 이면 이음 노트 온 (앞 노트가 여기서 끝남, IMS 특성) / vel = 0 노트 오프
///   An vol       : 볼륨          Cn inst : 악기 (이름표 인덱스)
///   En lsb msb   : 피치벤드 14bit, 0x2000 = 1.0 (ROL 피치 값 × 0x2000)
///   F0 7F 00 int frac F7 : 템포 배수 = int + frac/128        FC : 곡 끝
/// </code>
/// 실제 파일(WZ-GAME1.IMS ↔ WZ-GAME1.ROL)과 비교해 노트·악기·피치·볼륨이 같은 곡으로 변환되는 것을 확인했다.
/// </summary>
public sealed class ImsFormat : ISongFormat
{
    public const int HeaderSize = 70;
    public const int NameMagic = 0x7777;
    public const int OverflowTicks = 240;
    private const int NameLength = 9;

    public string Id { get { return "ims"; } }
    public string DisplayName { get { return "IMPlay song"; } }
    public string[] Extensions { get { return new[] { ".ims" }; } }
    public bool CanRead { get { return true; } }
    public bool CanWrite { get { return true; } }

    private static Encoding TitleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1361);      // 조합형 한글
        }
        catch (Exception) { return Encoding.GetEncoding(28591); }
    }

    // ================================================================ 읽기

    private sealed class ChannelState
    {
        public Note Current;
        public int Volume = -1;
    }

    public Song Read(Stream stream)
    {
        var r = new BinaryReader(stream, Encoding.ASCII, true);
        long baseOffset = stream.Position;
        byte[] header = r.ReadBytes(HeaderSize);
        if (header.Length < HeaderSize) throw new InvalidDataException("IMS 헤더가 잘렸습니다.");

        var song = new Song();
        song.VersionMajor = header[0];
        song.VersionMinor = header[1];
        song.Title = DecodeTitle(header, 6, 30);
        int tickBeat = Math.Max(1, (int)header[36]);
        song.BeatsPerMeasure = Math.Max(1, (int)header[37]);
        long dataSize = BitConverter.ToUInt32(header, 42);
        // totalTick: IMPLAY는 이 틱에서 곡을 끝낸다 (FC보다 먼저 오면). 뒤에 악기 바꾸기만 있는 무음 꼬리를 잘라 낸다.
        long totalTick = BitConverter.ToInt32(header, 38);
        song.IsMelodic = header[58] == 0;
        song.PitchBendRange = Math.Max(1, (int)header[59]);
        song.BasicTempo = BitConverter.ToUInt16(header, 60);
        if (song.BasicTempo <= 0) song.BasicTempo = 120f;

        long avail = stream.Length - stream.Position;
        if (dataSize <= 0 || dataSize > avail) dataSize = avail;
        byte[] data = r.ReadBytes((int)dataSize);

        // 이벤트 뒤의 악기 이름표
        var names = new List<string>();
        if (stream.Length - stream.Position >= 4 && r.ReadUInt16() == NameMagic)
        {
            int count = r.ReadUInt16();
            for (int i = 0; i < count && stream.Length - stream.Position >= NameLength; i++)
                names.Add(BinaryUtil.ReadFixedString(r, NameLength));
        }

        var channels = new ChannelState[Song.MaxVoices];
        for (int i = 0; i < channels.Length; i++) channels[i] = new ChannelState();

        int pos = 0, status = 0;
        long tick = 0;
        while (pos < data.Length)
        {
            while (pos < data.Length && data[pos] == 0xF8) { tick += OverflowTicks; pos++; }
            if (pos >= data.Length || data[pos] == 0xFC) break;
            tick += data[pos++];
            if (totalTick > 0 && tick > totalTick) break;   // totalTick과 같은 틱의 이벤트는 읽는다 (다시 저장해도 같게)
            if (pos >= data.Length) break;
            if ((data[pos] & 0x80) != 0) status = data[pos++];
            if (status == 0xFC) break;

            int hi = status & 0xF0, ch = status & 0x0F;
            int t = (int)Math.Min(tick, int.MaxValue);
            bool valid = ch < Song.MaxVoices;
            var cs = valid ? channels[ch] : null;
            VoiceTrack voice = valid ? song.Voices[ch] : null;

            if (hi == 0x80 || hi == 0x90)
            {
                if (pos + 2 > data.Length) break;
                int note = data[pos], vel = data[pos + 1];
                pos += 2;
                if (!valid) continue;
                if (vel == 0)
                {
                    if (cs.Current != null && cs.Current.Pitch == note) EndNote(voice, cs, t);
                    continue;
                }
                EndNote(voice, cs, t);
                if (vel != cs.Volume)
                {
                    cs.Volume = vel;
                    voice.Volumes.Add(new VolumeEvent { Tick = t, Volume = vel / 127f });
                }
                cs.Current = new Note { StartTick = t, Pitch = Calc.Clamp(note, Song.MinNote, Song.MaxNote) };
            }
            else if (hi == 0xA0 || hi == 0xC0 || hi == 0xD0)
            {
                if (pos + 1 > data.Length) break;
                int v = data[pos++];
                if (!valid) continue;
                if (hi == 0xA0)
                {
                    cs.Volume = v;
                    voice.Volumes.Add(new VolumeEvent { Tick = t, Volume = v / 127f });
                }
                else if (hi == 0xC0)
                {
                    string name = v < names.Count ? names[v] : "INS" + v;
                    voice.Timbres.Add(new TimbreEvent { Tick = t, InstrumentName = name, Unknown = v });
                }
            }
            else if (hi == 0xB0 || hi == 0xE0)
            {
                if (pos + 2 > data.Length) break;
                int lsb = data[pos], msb = data[pos + 1];
                pos += 2;
                if (valid && hi == 0xE0)
                    voice.Pitches.Add(new PitchEvent { Tick = t, Pitch = ((msb & 0x7F) << 7 | (lsb & 0x7F)) / 8192f });
            }
            else if (status == 0xF0)
            {
                int start = pos;
                while (pos < data.Length && data[pos] != 0xF7) pos++;
                if (pos - start >= 4 && data[start] == 0x7F && data[start + 1] == 0x00)
                    song.TempoEvents.Add(new TempoEvent { Tick = t, Multiplier = data[start + 2] + data[start + 3] / 128f });
                pos++;
            }
            else break;   // 알 수 없는 상태 바이트: 여기서 멈춘다
        }

        if (totalTick > 0 && tick > totalTick) tick = totalTick;
        int end = (int)Math.Min(tick, int.MaxValue);
        for (int v = 0; v < Song.MaxVoices; v++)
        {
            EndNote(song.Voices[v], channels[v], end);
            var vt = song.Voices[v];
            if (vt.Notes.Count > 0 || vt.Timbres.Count > 0) vt.DeclaredTicks = end;
        }
        song.TickBeat = tickBeat;
        song.FileTickBeat = tickBeat;
        ReduceTicks(song);
        return song;
    }

    private static void EndNote(VoiceTrack voice, ChannelState cs, int tick)
    {
        if (cs.Current == null) return;
        cs.Current.Duration = tick - cs.Current.StartTick;
        if (cs.Current.Duration > 0) voice.Notes.Add(cs.Current);
        cs.Current = null;
    }

    private static string DecodeTitle(byte[] b, int offset, int length)
    {
        int n = 0;
        while (n < length && b[offset + n] != 0) n++;
        return KoreanText.Decode(b, offset, n).Trim();
    }

    private static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }

    /// <summary>모든 틱의 최대공약수로 나눠 틱 해상도를 줄인다 (IMS 240틱/박 → ROL처럼 작은 값, ROL 16bit 틱 범위 확보).</summary>
    private static void ReduceTicks(Song song)
    {
        int g = song.TickBeat;
        foreach (var t in song.TempoEvents) g = Gcd(g, t.Tick);
        foreach (var v in song.Voices)
        {
            g = Gcd(g, v.DeclaredTicks);
            foreach (var n in v.Notes) { g = Gcd(g, n.StartTick); g = Gcd(g, n.Duration); }
            foreach (var e in v.Timbres) g = Gcd(g, e.Tick);
            foreach (var e in v.Volumes) g = Gcd(g, e.Tick);
            foreach (var e in v.Pitches) g = Gcd(g, e.Tick);
        }
        if (g <= 1) return;
        song.TickBeat /= g;
        foreach (var t in song.TempoEvents) t.Tick /= g;
        foreach (var v in song.Voices)
        {
            v.DeclaredTicks /= g;
            foreach (var n in v.Notes) { n.StartTick /= g; n.Duration /= g; }
            foreach (var e in v.Timbres) e.Tick /= g;
            foreach (var e in v.Volumes) e.Tick /= g;
            foreach (var e in v.Pitches) e.Tick /= g;
        }
    }

    // ================================================================ 쓰기

    private struct Cmd
    {
        public long Tick;
        public int Order;      // 같은 틱에서의 순서: 템포 < 악기 < 피치 < 볼륨 < 노트오프 < 노트온
        public int Voice;
        public byte[] Bytes;   // 상태 바이트 포함
    }

    public void Write(Song song, Stream stream)
    {
        // 틱 배율: 가능하면 원본처럼 240틱/박
        int scale = 1;
        int tb = Math.Max(1, song.TickBeat);
        if (tb < 240 && 240 % tb == 0) scale = 240 / tb;
        int outTickBeat = tb * scale;
        if (outTickBeat > 255) throw new InvalidDataException("IMS는 박당 틱이 255 이하여야 합니다 (현재 " + outTickBeat + ").");

        var names = new List<string>();
        var nameIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cmds = new List<Cmd>();

        foreach (var t in song.TempoEvents)
        {
            double m = Calc.Clamp(t.Multiplier, 0.0, 255.99);
            int whole = (int)Math.Floor(m);
            int frac = Calc.Clamp((int)Math.Round((m - whole) * 128), 0, 127);
            cmds.Add(new Cmd { Tick = (long)t.Tick * scale, Order = 0, Voice = -1, Bytes = new byte[] { 0xF0, 0x7F, 0x00, (byte)whole, (byte)frac, 0xF7 } });
        }

        for (int v = 0; v < song.VoiceCount; v++)
        {
            var vt = song.Voices[v];
            foreach (var e in vt.Timbres)
            {
                string name = AdlibInstrument.NormalizeName(e.InstrumentName);
                int idx;
                if (!nameIndex.TryGetValue(name, out idx)) { idx = names.Count; names.Add(name); nameIndex[name] = idx; }
                cmds.Add(new Cmd { Tick = (long)e.Tick * scale, Order = 1, Voice = v, Bytes = new byte[] { (byte)(0xC0 | v), (byte)Math.Min(idx, 127) } });
            }
            foreach (var e in vt.Pitches)
            {
                int pb = Calc.Clamp((int)Math.Round(e.Pitch * 8192.0), 0, 0x3FFF);
                cmds.Add(new Cmd { Tick = (long)e.Tick * scale, Order = 2, Voice = v, Bytes = new byte[] { (byte)(0xE0 | v), (byte)(pb & 0x7F), (byte)(pb >> 7) } });
            }

            // 볼륨: 노트 온의 vel로 표현되므로, 노트 시작과 겹치지 않는 변경만 An 이벤트로 쓴다
            var vols = new List<VolumeEvent>(vt.Volumes);
            vols.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            var notes = new List<Note>(vt.Notes);
            notes.Sort((a, b) => a.StartTick.CompareTo(b.StartTick));
            var noteStarts = new HashSet<int>();
            foreach (var n in notes) noteStarts.Add(n.StartTick);
            foreach (var e in vols)
                if (!noteStarts.Contains(e.Tick))
                    cmds.Add(new Cmd { Tick = (long)e.Tick * scale, Order = 3, Voice = v, Bytes = new byte[] { (byte)(0xA0 | v), VolByte(e.Volume) } });

            int vi = 0;
            float vol = 1f;
            Note prev = null;
            foreach (var n in notes)
            {
                if (n.Duration <= 0) continue;
                if (prev != null && n.StartTick < prev.EndTick) continue;   // 겹침은 무시 (한 보이스 = 한 음)
                while (vi < vols.Count && vols[vi].Tick <= n.StartTick) vol = vols[vi++].Volume;
                byte vel = Math.Max((byte)1, VolByte(vol));
                bool legato = prev != null && prev.EndTick == n.StartTick;
                if (prev != null && !legato)
                    cmds.Add(new Cmd { Tick = (long)prev.EndTick * scale, Order = 4, Voice = v, Bytes = new byte[] { (byte)(0x90 | v), (byte)prev.Pitch, 0 } });
                cmds.Add(new Cmd { Tick = (long)n.StartTick * scale, Order = 5, Voice = v, Bytes = new byte[] { (byte)((legato ? 0x80 : 0x90) | v), (byte)n.Pitch, vel } });
                prev = n;
            }
            if (prev != null)
                cmds.Add(new Cmd { Tick = (long)prev.EndTick * scale, Order = 4, Voice = v, Bytes = new byte[] { (byte)(0x90 | v), (byte)prev.Pitch, 0 } });
        }

        cmds.Sort((a, b) =>
        {
            int c = a.Tick.CompareTo(b.Tick);
            if (c != 0) return c;
            c = a.Order.CompareTo(b.Order);
            return c != 0 ? c : a.Voice.CompareTo(b.Voice);
        });

        long endTick = (long)song.LengthTicks * scale;
        var ev = new MemoryStream();
        long last = 0, nrCommand = 0;
        int running = -1;
        foreach (var c in cmds)
        {
            nrCommand += WriteDelay(ev, c.Tick - last);
            last = c.Tick;
            int st = c.Bytes[0];
            int from = (st == running && st < 0xF0) ? 1 : 0;   // running status
            ev.Write(c.Bytes, from, c.Bytes.Length - from);
            running = st < 0xF0 ? st : -1;
            nrCommand++;
        }
        if (endTick < last) endTick = last;
        nrCommand += WriteDelay(ev, endTick - last);
        ev.WriteByte(0xFC);
        nrCommand++;

        var w = new BinaryWriter(stream, Encoding.ASCII, true);
        w.Write((byte)Calc.Clamp(song.VersionMajor == 0 ? 1 : song.VersionMajor, 0, 255));
        w.Write((byte)Calc.Clamp(song.VersionMajor == 0 ? 0 : song.VersionMinor, 0, 255));
        w.Write((uint)0);
        byte[] title = new byte[30];
        byte[] tb2 = TitleEncoding().GetBytes(song.Title ?? "");
        Array.Copy(tb2, title, Math.Min(tb2.Length, 29));
        w.Write(title);
        w.Write((byte)outTickBeat);
        w.Write((byte)Calc.Clamp(song.BeatsPerMeasure, 1, 255));
        w.Write((uint)endTick);
        w.Write((uint)ev.Length);
        w.Write((uint)nrCommand);
        w.Write(new byte[8]);
        w.Write((byte)(song.IsMelodic ? 0 : 1));
        w.Write((byte)Calc.Clamp(song.PitchBendRange, 1, 12));
        w.Write((ushort)Calc.Clamp((int)Math.Round(song.BasicTempo), 1, 65535));
        w.Write(new byte[8]);
        w.Write(ev.ToArray());
        w.Write((ushort)NameMagic);
        w.Write((ushort)names.Count);
        foreach (var n in names) BinaryUtil.WriteFixedString(w, n, NameLength);
        w.Flush();
    }

    private static byte VolByte(float v) { return (byte)Calc.Clamp((int)Math.Round(v * 127), 0, 127); }

    /// <summary>지연을 F8(240틱) 반복 + 나머지 1바이트로 쓴다. 쓴 F8 개수(= nrCommand에 포함)를 돌려준다.</summary>
    private static int WriteDelay(Stream s, long delay)
    {
        int overflow = 0;
        while (delay >= OverflowTicks) { s.WriteByte(0xF8); delay -= OverflowTicks; overflow++; }
        s.WriteByte((byte)delay);
        return overflow;
    }
}
