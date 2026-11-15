using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>
/// 오디오 엔진 명령 큐: 장치가 열려 있으면 명령은 큐에 들어가 오디오 스레드(여기서는 가짜 장치의 Pump)가 실행하고,
/// UI 스레드는 렌더 락을 기다리지 않는다.
/// </summary>
public static class EngineQueueTests
{
    /// <summary>가짜 출력 장치: 시험이 Pump를 부를 때만 채운다 (PlayedSamples = 모름 → 위치는 스냅샷).</summary>
    private sealed class FakeOutput : IAudioOutput
    {
        public readonly AudioFill Fill;
        public readonly int Frames;
        public FakeOutput(int frames, AudioFill fill) { Frames = frames; Fill = fill; }
        public bool IsOpen { get; private set; }
        public long PlayedSamples { get { return -1; } }
        public void Start() { IsOpen = true; }
        public void Stop() { IsOpen = false; }
        public void Dispose() { IsOpen = false; }
        public void Pump(int frames) { Fill(new short[frames * 2], frames); }
    }

    public static void Run(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("samples");
        if (dir == null || !File.Exists(Path.Combine(dir, "demo.rol"))) { Console.WriteLine("SKIP  engine queue (samples/demo.rol not found)"); return; }
        Song song; InstrumentBank bank;
        using (var fs = File.OpenRead(Path.Combine(dir, "demo.rol"))) song = new RolFormat().Read(fs);
        using (var fs = File.OpenRead(Path.Combine(dir, "demo.bnk"))) bank = new BnkFormat().Read(fs);

        var saved = AudioPlatform.OutputFactory;
        FakeOutput dev = null;
        AudioPlatform.OutputFactory = (rate, frames, ch, fill) => dev = new FakeOutput(frames, fill);
        try
        {
            using (var eng = new AudioEngine())
            {
                eng.OpenDevice();
                var opts = new PlaybackOptions();
                eng.Play(song, bank, 0, -1, -1, -1, opts);
                check("Engine queue: Play returns before audio thread runs, IsPlaying already true", eng.IsPlaying && eng.PositionTick == 0, "");
                eng.SetGain(0.25); eng.SetGain(0.5); eng.SetTranspose(3);
                check("Engine queue: commands wait in queue (options untouched until audio block)", opts.Gain == 1.0 && opts.Transpose == 0, opts.Gain + " " + opts.Transpose);
                dev.Pump(512);
                check("Engine queue: audio block runs commands in order", opts.Gain == 0.5 && opts.Transpose == 3, opts.Gain + " " + opts.Transpose);
                for (int i = 0; i < 40; i++) dev.Pump(512);
                double pos = eng.PositionTick;
                check("Engine queue: position advances from snapshot", eng.IsPlaying && pos > 0, pos.ToString("0.0"));

                eng.Stop();
                check("Engine queue: Stop shows stopped at once", !eng.IsPlaying, "");
                dev.Pump(512);
                check("Engine queue: stays stopped after block", !eng.IsPlaying, "");

                // 재생 → 바로 멈춤 (블록 없이): 마지막 명령이 이긴다
                eng.Play(song, bank, 0, -1, -1, -1, opts);
                eng.Stop();
                dev.Pump(512);
                check("Engine queue: Play then Stop in one block ends stopped", !eng.IsPlaying, "");

                // 값을 돌려받는 호출은 밀린 명령을 먼저 실행
                eng.Play(song, bank, 0, -1, -1, -1, opts);
                eng.SetGain(0.75);
                check("Engine queue: Options query drains queue first", eng.Options == opts && opts.Gain == 0.75, opts.Gain.ToString());

                // 긴 렌더 블록 중에도 UI 명령은 바로 돌아온다 (예전: 렌더 락을 기다림)
                var renderer = new Thread(() => { for (int k = 0; k < 4; k++) dev.Pump(3 * 44100); });
                var blockSw = Stopwatch.StartNew();
                dev.Pump(3 * 44100);
                double blockMs = blockSw.Elapsed.TotalMilliseconds;
                renderer.Start();
                Thread.Sleep(5);
                double worst = 0;
                var sw = new Stopwatch();
                for (int i = 0; i < 200 && renderer.IsAlive; i++)
                {
                    sw.Restart();
                    eng.SetVoiceMute(i % 11, (i & 1) == 0);
                    eng.SetTranspose(i % 5);
                    bool p = eng.IsPlaying;
                    double t = eng.PositionTick;
                    eng.GetVoiceStates(new VoiceState[PlaybackOptions.Voices]);
                    worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                    Thread.Sleep(1);
                }
                renderer.Join();
                check("Engine queue: UI calls don't wait for a render block", blockMs > 10 && worst < blockMs / 4,
                    "worst " + worst.ToString("0.00") + " ms, block " + blockMs.ToString("0") + " ms");
                for (int v = 0; v < 11; v++) eng.SetVoiceMute(v, false);
                dev.Pump(512);
            }
        }
        finally { AudioPlatform.OutputFactory = saved; }

        // 장치 없음: 예전처럼 바로 실행
        using (var eng = new AudioEngine())
        {
            var opts = new PlaybackOptions();
            eng.Play(song, bank, 0, -1, -1, -1, opts);
            eng.SetGain(0.3);
            check("Engine without device: commands run immediately", eng.IsPlaying && opts.Gain == 0.3, "");
        }
    }
}
