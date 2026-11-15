using System;
using System.Collections.Generic;

namespace AdlibComposer.Audio.Dos;

/// <summary>
/// Roland MPU-401 (PC/AT 330h/331h · PC-98 MPU-PC98 E0D0h/E0D2h): 데이터 포트 · 상태 / 명령 포트, UART 모드와 지능 모드(재생 시퀀서).
/// <para>상태: bit6 (DRR) = 0 → 쓸 수 있음, bit7 (DSR) = 0 → 읽을 데이터 있음. 모든 명령에 ACK FEh (UART 모드에서는 FFh만).
/// 3Fh = UART 모드: 데이터 포트 바이트가 그대로 MIDI.</para>
/// <para>지능 모드: Ex 명령 (E0h 템포 · E7h 클럭-투-호스트 간격 · ECh 쓰는 트랙 …) = 다음 데이터 바이트가 값,
/// D0h~D7h (Want to Send Data) = 다음 메시지 하나를 바로 MIDI로 (DFh = SysEx), C2h~C8h = 타임베이스 48~192,
/// 재생 시작 (명령 bit2~3 = 10) → 쓰는 트랙마다 F0h+n으로 데이터를 달라고 하고, 호스트가 [타이밍 바이트, 메시지]를 주면 그만큼 클럭 뒤에 낸다
/// (F8h = 240클럭 넘김, F9h = 마디 끝, FCh = 트랙 끝 → 모두 끝나면 FCh). 8Eh/8Fh 지휘 트랙 끔/켬 (F9h로 요청, 템포 명령), 94h/95h 클럭-투-호스트 끔/켬 (FDh).
/// 읽을 데이터가 있으면 <see cref="IrqPending"/> (기계가 인터럽트를 올린다).</para>
/// <para>내부 클럭 = 템포(BPM) × 타임베이스 / 60 (초당), 기본 100 BPM · 120.</para>
/// </summary>
public sealed class Mpu401
{
    private readonly Queue<int> rx = new Queue<int>();
    private bool uart;

    /// <summary>MIDI 바이트 하나가 나감.</summary>
    public Action<int> MidiOut;
    public bool Uart { get { return uart; } }
    public int BytesOut { get; private set; }
    /// <summary>진단: 받은 명령 (순서대로, 256개까지).</summary>
    public readonly List<int> Commands = new List<int>();

    // 지능 모드
    private int tempo = 100, timebase = 120, activeTracks, clocksPerHost = 240, hostCount;
    private bool playing, clockToHost, conductor;
    private int paramCommand = -1;   // 값을 기다리는 Ex 명령
    private int sendLeft = -1;       // Want to Send Data: 남은 바이트 (−2 = SysEx 중)
    private int sendRunning;

    private sealed class Track
    {
        public bool Active, Waiting, Ended;
        public int Wait;                 // 낼 때까지 남은 클럭
        public readonly List<int> Msg = new List<int>();
        public int Running;
        // 받는 중 상태: 0 = 타이밍 바이트, 1 = 메시지 첫 바이트, 2 = 데이터 더
        public int Phase, Need;
        public bool Ready;               // 메시지 다 받음 (Wait 뒤에 냄)
        public bool Mark;                // 낼 메시지 없음 (넘김 · 마디 끝)
    }

    private readonly Track[] tracks = new Track[9];   // 8 = 지휘 트랙
    private int requested = -1;                       // 호스트가 지금 답하는 트랙

    public Mpu401() { for (int i = 0; i < tracks.Length; i++) tracks[i] = new Track(); }

    /// <summary>읽을 데이터가 있음 = 인터럽트 요청.</summary>
    public bool IrqPending { get { return rx.Count > 0; } }
    /// <summary>시퀀서 클럭이 필요함 (재생 중 · 클럭-투-호스트).</summary>
    public bool Running { get { return !uart && (playing || clockToHost); } }
    /// <summary>내부 클럭 1틱의 길이 (초).</summary>
    public double ClockSeconds { get { return 60.0 / Math.Max(8, tempo) / Math.Max(24, timebase); } }

    public int ReadData() { return rx.Count > 0 ? rx.Dequeue() : 0xFE; }

    public int ReadStatus() { return 0x3F | (rx.Count > 0 ? 0x00 : 0x80); }

    private void Out(int b) { BytesOut++; MidiOut?.Invoke(b & 0xFF); }

    public void WriteCommand(int v)
    {
        if (Commands.Count < 256) Commands.Add(v);
        if (v == 0xFF) { Reset(); rx.Enqueue(0xFE); return; }
        if (uart) return;
        rx.Enqueue(0xFE);
        if (v == 0x3F) { uart = true; return; }
        if (v >= 0xE0 && v <= 0xEF) { paramCommand = v; return; }
        if (v >= 0xD0 && v <= 0xDF) { sendLeft = v == 0xDF ? -2 : 0; sendRunning = 0; return; }
        if (v >= 0xC2 && v <= 0xC8) { timebase = 48 + (v - 0xC2) * 24; return; }
        switch (v)
        {
            case 0x94: clockToHost = false; return;                    // 짝수 = 끔, 홀수 = 켬 (8Eh/8Fh처럼)
            case 0x95: clockToHost = true; hostCount = 0; return;
            case 0x8E: conductor = false; return;
            case 0x8F: conductor = true; return;
            case 0xAC: rx.Enqueue(0x15); return;   // 판
            case 0xAD: rx.Enqueue(0x01); return;   // 개정
        }
        if (v < 0x40)
        {
            int play = (v >> 2) & 3;
            int midi = v & 3;
            if (midi == 2) Out(0xFA); else if (midi == 3) Out(0xFB); else if (midi == 1) Out(0xFC);
            if (play == 2) StartPlay();
            else if (play == 1) { playing = false; requested = -1; }
        }
    }

    private void Reset()
    {
        uart = false; rx.Clear(); playing = false; clockToHost = false; conductor = false;
        tempo = 100; timebase = 120; activeTracks = 0; clocksPerHost = 240; paramCommand = -1; sendLeft = -1; requested = -1;
        for (int i = 0; i < tracks.Length; i++) tracks[i] = new Track();
    }

    private void StartPlay()
    {
        playing = true;
        requested = -1;
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            t.Active = i < 8 ? (activeTracks & (1 << i)) != 0 : conductor;
            t.Ended = false; t.Wait = 0; t.Ready = false; t.Mark = false; t.Msg.Clear(); t.Phase = 0;
            t.Waiting = t.Active;
        }
        RequestNext();
    }

    /// <summary>데이터가 필요한 트랙이 있고 지금 답을 기다리는 것이 없으면 요청 (F0h+n · 지휘 = F9h).</summary>
    private void RequestNext()
    {
        if (requested >= 0 || !playing) return;
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = tracks[i];
            if (t.Active && t.Waiting && !t.Ended)
            {
                requested = i;
                t.Waiting = false; t.Phase = 0; t.Msg.Clear(); t.Ready = false; t.Mark = false;
                rx.Enqueue(i == 8 ? 0xF9 : 0xF0 + i);
                return;
            }
        }
        if (Array.TrueForAll(tracks, t => !t.Active || t.Ended)) { playing = false; rx.Enqueue(0xFC); }   // 모두 끝
    }

    public void WriteData(int v)
    {
        v &= 0xFF;
        if (uart) { Out(v); return; }
        if (paramCommand >= 0)
        {
            switch (paramCommand)
            {
                case 0xE0: tempo = Math.Max(8, v); break;
                case 0xE7: clocksPerHost = Math.Max(1, v); break;
                case 0xEC: activeTracks = v; break;
            }
            paramCommand = -1;
            return;
        }
        if (sendLeft != -1) { WantSend(v); return; }
        if (requested >= 0) { TrackData(tracks[requested], v); return; }
        Out(v);   // 지능 모드인데 기다리는 것이 없음: 그냥 MIDI로
    }

    /// <summary>Want to Send Data: 메시지 하나 (러닝 스테이터스 · SysEx).</summary>
    private void WantSend(int v)
    {
        if (sendLeft == -2) { Out(v); if (v == 0xF7) sendLeft = -1; return; }
        if (v >= 0x80)
        {
            if (v == 0xF0) { Out(v); sendLeft = -2; return; }
            sendRunning = v;
            Out(v);
            sendLeft = DataBytes(v);
            if (sendLeft == 0) sendLeft = -1;
            return;
        }
        if (sendLeft == 0 && sendRunning != 0) { Out(sendRunning); sendLeft = DataBytes(sendRunning); }   // 러닝 스테이터스
        Out(v);
        if (--sendLeft <= 0) sendLeft = -1;
    }

    private static int DataBytes(int status)
    {
        int k = status & 0xF0;
        if (k == 0xC0 || k == 0xD0) return 1;
        if (status >= 0xF0) return status == 0xF2 ? 2 : status == 0xF1 || status == 0xF3 ? 1 : 0;
        return 2;
    }

    /// <summary>트랙 데이터 받기: [타이밍] [메시지 | F8h 넘김 | F9h 마디 끝 | FCh 끝].</summary>
    private void TrackData(Track t, int v)
    {
        int idx = Array.IndexOf(tracks, t);
        if (t.Phase == 0)
        {
            if (v == 0xF8) { t.Wait = 240; t.Mark = true; Accepted(t); return; }   // 넘김: 240클럭 뒤 다시 요청
            t.Wait = v; t.Phase = 1;
            return;
        }
        if (t.Phase == 1)
        {
            if (v == 0xFC) { t.Ended = true; requested = -1; RequestNext(); return; }
            if (v == 0xF8 || v == 0xF9) { t.Mark = true; Accepted(t); return; }
            if (idx == 8)
            {
                // 지휘 트랙: MPU 명령 (E0h 템포 …)
                if (v >= 0xE0 && v <= 0xEF) { t.Msg.Clear(); t.Msg.Add(v); t.Need = 1; t.Phase = 2; return; }
                t.Mark = true; Accepted(t); return;
            }
            t.Msg.Clear();
            if (v >= 0x80) { t.Running = v; t.Msg.Add(v); t.Need = DataBytes(v); }
            else { t.Msg.Add(t.Running); t.Msg.Add(v); t.Need = DataBytes(t.Running) - 1; }
            if (t.Need <= 0) { t.Ready = true; Accepted(t); return; }
            t.Phase = 2;
            return;
        }
        t.Msg.Add(v);
        if (--t.Need <= 0) { t.Ready = true; Accepted(t); }
    }

    /// <summary>한 트랙의 다음 일을 다 받음: 기다림이 0이면 바로 내고 다음 요청.</summary>
    private void Accepted(Track t)
    {
        requested = -1;
        if (t.Wait == 0) Fire(t);
        RequestNext();
    }

    private void Fire(Track t)
    {
        int idx = Array.IndexOf(tracks, t);
        if (t.Ready)
        {
            if (idx == 8) { if (t.Msg.Count >= 2 && t.Msg[0] == 0xE0) tempo = Math.Max(8, t.Msg[1]); }
            else foreach (var b in t.Msg) Out(b);
        }
        t.Ready = false; t.Mark = false; t.Msg.Clear();
        t.Waiting = true;
    }

    /// <summary>내부 클럭 한 틱 (기계가 <see cref="ClockSeconds"/>마다 부른다).</summary>
    public void Clock()
    {
        if (uart) return;
        if (clockToHost && ++hostCount >= clocksPerHost) { hostCount = 0; rx.Enqueue(0xFD); }
        if (!playing) return;
        bool any = false;
        foreach (var t in tracks)
        {
            if (!t.Active || t.Ended || t.Waiting || (!t.Ready && !t.Mark)) continue;
            if (t.Wait > 0 && --t.Wait > 0) continue;
            Fire(t);
            any = true;
        }
        if (any) RequestNext();
    }
}

/// <summary>MIDI 바이트 흐름 → 메시지 (러닝 스테이터스 · SysEx · 실시간 메시지 빼기).</summary>
public sealed class MidiByteParser
{
    private int status, need, have, d1;
    private List<byte> sysex;

    /// <summary>메시지 하나 (상태, 데이터 1, 데이터 2, SysEx 또는 null).</summary>
    public Action<int, int, int, byte[]> Message;

    public void Feed(int b)
    {
        if (b >= 0xF8) return;   // 실시간 (클럭 · 액티브 센싱 …)
        if (b == 0xF0) { sysex = new List<byte> { 0xF0 }; status = 0; return; }
        if (b == 0xF7) { if (sysex != null) { sysex.Add(0xF7); Message?.Invoke(0xF0, 0, 0, sysex.ToArray()); sysex = null; } return; }
        if (b >= 0x80)
        {
            if (sysex != null) sysex = null;   // 끝나지 않은 SysEx 버림
            if (b >= 0xF1) { status = 0; return; }   // 시스템 공통: 무시 (러닝 스테이터스 끊김)
            status = b; have = 0;
            int k = b & 0xF0;
            need = k == 0xC0 || k == 0xD0 ? 1 : 2;
            return;
        }
        if (sysex != null) { if (sysex.Count < 65536) sysex.Add((byte)b); return; }
        if (status == 0) return;
        if (have == 0) { d1 = b; have = 1; if (need == 1) { Message?.Invoke(status, d1, 0, null); have = 0; } return; }
        Message?.Invoke(status, d1, b, null);
        have = 0;
    }
}
