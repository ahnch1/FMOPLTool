using System;

namespace AdlibComposer.Audio.Windows;

/// <summary>Windows 구현을 <see cref="AudioPlatform"/>에 끼운다: waveOut 출력, midiOut, Media Foundation 디코더.</summary>
public static class WindowsAudio
{
    public static void Register()
    {
        AudioPlatform.OutputFactory = (rate, frames, channels, fill) => new WaveOutPlayer(rate, frames, channels, fill);
        AudioPlatform.MidiOutFactory = () => new WinMidiOut();
        if (OperatingSystem.IsWindows()) AudioPlatform.CompressedDecoder = MediaFoundationDecoder.ReadMono;
    }
}
