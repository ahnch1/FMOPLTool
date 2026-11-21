using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// KSS (MSX · 세가 8비트 사운드 드라이버 덤프, KSCC / KSSX): Z80 코드 + 데이터. 드라이버를 Z80(<see cref="Z80"/>)으로 돌려
/// 칩 쓰기(PSG · SCC · SCC+ · OPLL · MSX-AUDIO · SN76489)를 시간과 함께 모아 VGM(<see cref="VgmSong"/>)으로 바꾼다.
/// <para>머리말 (0x10바이트): KSCC/KSSX, 0x04 읽어 둘 주소, 0x06 크기, 0x08 init, 0x0A play, 0x0C 첫 뱅크, 0x0D 뱅크 수(bit7 = 8KB 뱅크),
/// 0x0E 확장 머리말 크기(KSSX), 0x0F 장치 (bit0 FM-PAC, bit1 세가 SN76489, bit3 MSX-AUDIO).</para>
/// <para>포트 · 메모리 배치 · 클럭 · 프레임 속도는 시스템 정의 (<see cref="SystemDefinitions"/>, id = msx / sms)에서 온다.</para>
/// <para>돌리는 방법 (libkss · NEZplug와 같은 관례): RAM 64KB, init을 A = 곡 번호로 부르고, 그 뒤 1/60초마다 play를 부른다.
/// 곡 길이는 같은 이름의 .m3u (NEZplug 확장 m3u: <c>파일::KSS,$번호,제목,시간,루프,페이드</c>)에서, 없으면 2분.</para>
/// </summary>
public static class Kss
{
    public static readonly string[] Extensions = { ".kss" };
    public const double DefaultSeconds = 120, DefaultFade = 4;
    public const double CpuClock = 3579545;

    public sealed class Header
    {
        public bool Extended;
        public int LoadAddr, LoadSize, InitAddr, PlayAddr;
        public int FirstBank, BankCount;
        public bool Bank8K;
        public bool FmPac, Sega, MsxAudio, GgStereo;
        public int FirstSong, LastSong = 255;
        public int DataAt;
        public byte[] Data;
    }

    public sealed class Track
    {
        public int Song;
        public string Title = "";
        public double Seconds = DefaultSeconds;
        public double Fade = DefaultFade;
    }

    public static Header ReadHeader(byte[] d)
    {
        if (d.Length < 0x10) throw new InvalidDataException("KSS 파일이 너무 짧습니다.");
        string magic = Encoding.ASCII.GetString(d, 0, 4);
        if (magic != "KSCC" && magic != "KSSX") throw new InvalidDataException("KSS 파일이 아닙니다 (머리말 KSCC / KSSX가 없음).");
        var h = new Header { Data = d, Extended = magic == "KSSX" };
        h.LoadAddr = U16(d, 4); h.LoadSize = U16(d, 6); h.InitAddr = U16(d, 8); h.PlayAddr = U16(d, 0x0A);
        h.FirstBank = d[0x0C]; h.BankCount = d[0x0D] & 0x7F; h.Bank8K = (d[0x0D] & 0x80) != 0;
        int dev = d[0x0F];
        h.FmPac = (dev & 1) != 0; h.Sega = (dev & 2) != 0;
        if (h.Sega) h.GgStereo = (dev & 4) != 0; else h.MsxAudio = (dev & 8) != 0;
        h.DataAt = 0x10;
        if (h.Extended)
        {
            int extra = d[0x0E];
            h.DataAt = 0x10 + extra;
            if (extra >= 0x0C && d.Length >= 0x1C) { h.FirstSong = U16(d, 0x18); h.LastSong = U16(d, 0x1A); }
        }
        return h;
    }

    // ------------------------------------------------------------ m3u

    /// <summary>KSS 옆 같은 이름의 .m3u에서 곡 목록 (<see cref="NezM3u"/>). 없으면 KSSX 머리말의 범위 (KSCC면 곡 0 하나).</summary>
    public static List<Track> Tracks(string kssPath)
    {
        var list = new List<Track>();
        foreach (var e in NezM3u.Read(kssPath, "KSS"))
        {
            var t = new Track { Song = e.Number, Title = e.Title };
            if (e.Seconds > 0) t.Seconds = e.Seconds;
            if (e.Fade >= 0) t.Fade = e.Fade;
            list.Add(t);
        }
        if (list.Count > 0) return list;
        try
        {
            var h = ReadHeader(File.ReadAllBytes(kssPath));
            if (h.Extended && h.LastSong >= h.FirstSong && h.LastSong - h.FirstSong < 256)
                for (int s = h.FirstSong; s <= h.LastSong; s++) list.Add(new Track { Song = s });
        }
        catch (Exception) { }
        if (list.Count == 0) list.Add(new Track { Song = 0 });
        return list;
    }

    // ------------------------------------------------------------ 열기 (ChipSongs)

    /// <summary>"파일.kss" 또는 "파일.kss::n" (n = 곡 목록 안 번호, 0부터).</summary>
    public static VgmSong Load(string path)
    {
        path = NezM3u.SplitTrack(path, out int track);
        var tracks = Tracks(path);
        var t = tracks[Math.Max(0, Math.Min(track, tracks.Count - 1))];
        var v = Render(File.ReadAllBytes(path), t.Song, t.Seconds, t.Fade);
        v.FilePath = path;
        v.TitleEn = t.Title.Length > 0 ? t.Title : Path.GetFileNameWithoutExtension(path) + " #" + t.Song;
        v.SourceFormat = "KSS · 곡 " + t.Song + " (" + (track + 1) + "/" + tracks.Count + ")";
        return v;
    }

    // ------------------------------------------------------------ 돌리기

    private sealed class Machine : IZ80Bus
    {
        public readonly byte[] Ram = new byte[0x10000];
        private readonly Header h;
        public double Now;   // 지금 시간 (44100Hz 샘플)
        public readonly List<(double t, byte[] cmd)> Log = new List<(double, byte[])>();
        public bool UsedPsg, UsedScc, UsedSccPlus, UsedOpll, UsedAudio, UsedSn;
        private int psgAddr, opllAddr, audioAddr;
        private readonly byte[] psgReg = new byte[16];
        public long CpuStart;
        public Z80 Cpu;

        // 시스템 정의에서 온 배치 (msx / sms)
        public readonly SystemDefinition Sys;
        public readonly double Clock;
        private readonly int pAddr, pData, pRead, oAddr, oData, aAddr, aData, bankPort, snData, snData2, snStereo, bankMem, sccBase, sccPlusBase;

        public Machine(Header h)
        {
            this.h = h;
            string id = h.Sega ? "sms" : "msx";
            Sys = SystemDefinitions.Get(id) ?? SystemDefinitions.Parse(SystemDefinitions.BuiltinJson).First(x => x.Id == id);
            Clock = Sys.CpuClock > 0 ? Sys.CpuClock : CpuClock;
            var psg = Sys.Chip("AY-3-8910"); var opll = Sys.Chip("YM2413"); var au = Sys.Chip("Y8950"); var map = Sys.Chip("Mapper"); var sn = Sys.Chip("SN76489");
            pAddr = psg?.Port("addr", -1) ?? -1; pData = psg?.Port("data", -1) ?? -1; pRead = psg?.Port("read", -1) ?? -1;
            oAddr = opll?.Port("addr", -1) ?? -1; oData = opll?.Port("data", -1) ?? -1;
            aAddr = au?.Port("addr", -1) ?? -1; aData = au?.Port("data", -1) ?? -1;
            snData = sn?.Port("data", -1) ?? -1; snData2 = sn?.Port("data2", -1) ?? -1; snStereo = sn?.Port("stereo", -1) ?? -1;
            bankPort = map?.Port("bank", -1) ?? -1; bankMem = map?.Memory ?? -1;
            sccBase = Sys.Chip("SCC")?.Memory ?? -1; sccPlusBase = Sys.Chip("SCC+")?.Memory ?? -1;
        }

        public double ChipClock(string chip, double fallback) { var c = Sys.Chip(chip); return c != null && c.Clock > 0 ? c.Clock : fallback; }

        private void Emit(params byte[] cmd)
        {
            double t = Now + (Cpu.Cycles - CpuStart) * VgmSong.Rate / Clock;
            Log.Add((t, cmd));
        }

        public byte Read(int a) { return Ram[a & 0xFFFF]; }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (!h.Sega)
            {
                if (h.BankCount > 0 && h.Bank8K && (a == 0x9000 || a == 0xB000)) { SelectBank(v, a == 0x9000 ? 0x8000 : 0xA000, 0x2000); return; }
                // SCC (0x9800~) · SCC+ (0xB800~): KSS 플레이어 관례대로 모드 레지스터와 상관없이 칩으로 (드라이버가 바로 쓴다)
                if (sccBase >= 0 && a >= sccBase && a < sccBase + 0x100) { UsedScc = true; Emit(0xD2, SccPort(a - sccBase, out int r), (byte)r, v); Ram[a] = v; return; }
                if (sccPlusBase >= 0 && a >= sccPlusBase && a < sccPlusBase + 0xB0) { UsedSccPlus = true; Emit(0xD2, SccPlusPort(a - sccPlusBase, out int r), (byte)r, v); Ram[a] = v; return; }
            }
            else if (a == bankMem && h.BankCount > 0) { SelectBank(v, 0x8000, 0x4000); Ram[a] = v; return; }
            Ram[a] = v;
        }

        /// <summary>SCC 메모리 (0x9800 기준) → VGM 0xD2 포트 · 레지스터.</summary>
        private static byte SccPort(int off, out int r)
        {
            if (off < 0x80) { r = off; return 0; }                       // 파형
            off &= 0x8F;
            if (off < 0x8A) { r = off - 0x80; return 1; }                 // 주파수
            if (off < 0x8F) { r = off - 0x8A; return 2; }                 // 볼륨
            r = 0; return 3;                                              // 켜기
        }

        private static byte SccPlusPort(int off, out int r)
        {
            if (off < 0xA0) { r = off; return 4; }                        // SCC+ 파형 (5채널)
            off &= 0xAF;
            if (off < 0xAA) { r = off - 0xA0; return 1; }
            if (off < 0xAF) { r = off - 0xAA; return 2; }
            r = 0; return 3;
        }

        public void SelectBank(int bank, int at, int size)
        {
            int n = bank - h.FirstBank;
            if (n < 0 || n >= h.BankCount) return;
            int src = h.DataAt + h.LoadSize + n * size;
            int len = Math.Min(size, h.Data.Length - src);
            if (len > 0) Array.Copy(h.Data, src, Ram, at, len);
        }

        public byte In(int port)
        {
            port &= 0xFF;
            if (!h.Sega && port == pRead) return psgAddr == 14 ? (byte)0xFF : psgReg[psgAddr];   // hoot ssAY8910: 레지스터 값, R14 = 조이스틱 안 눌림
            return 0xFF;
        }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            if (h.Sega)
            {
                if (port == snData || port == snData2) { UsedSn = true; Emit(0x50, v); }
                else if (port == snStereo) { UsedSn = true; Emit(0x4F, v); }
                else if (port == oAddr) opllAddr = v;
                else if (port == oData) { UsedOpll = true; Emit(0x51, (byte)opllAddr, v); }
                else if (port == bankPort && h.BankCount > 0) SelectBank(v, 0x8000, 0x4000);
                return;
            }
            if (port == pAddr) psgAddr = v & 0x0F;
            else if (port == pData) { UsedPsg = true; psgReg[psgAddr] = v; Emit(0xA0, (byte)psgAddr, v); }
            else if (port == oAddr) opllAddr = v;
            else if (port == oData) { UsedOpll = true; Emit(0x51, (byte)opllAddr, v); }
            else if (port == aAddr) audioAddr = v;
            else if (port == aData) { UsedAudio = true; Emit(0x5C, (byte)audioAddr, v); }
            else if (port == bankPort && h.BankCount > 0 && !h.Bank8K) SelectBank(v, 0x8000, 0x4000);
        }
    }

    private const int Trap = 0x0000;

    /// <summary>곡 하나를 seconds초 돌려 VGM으로 (끝 fade초 동안 줄인다).</summary>
    public static VgmSong Render(byte[] file, int song, double seconds, double fade = DefaultFade)
    {
        var h = ReadHeader(file);
        var m = new Machine(h);
        var cpu = new Z80(m);
        m.Cpu = cpu;
        var ram = m.Ram;
        // 작은 BIOS: 0x0000 = 함정 (돌아오면 멈춤), WRTPSG (0x93) · RDPSG (0x96), 인터럽트 0x38 = RET
        if (!h.Sega)
        {
            for (int i = 1; i < 0x4000; i++) ram[i] = 0xC9;   // hoot kss.cpp: BIOS 영역 = RET (0000h는 함정 그대로)
            // WRTPSG (0x93, 3바이트 자리) → 0xA0의 본체로 뜀: OUT (A0),A · PUSH AF · LD A,E · OUT (A1),A · POP AF · RET
            ram[0x93] = 0xC3; ram[0x94] = 0xA0; ram[0x95] = 0x00;
            byte[] rdpsg = { 0xD3, 0xA0, 0xDB, 0xA2, 0xC9 };   // RDPSG (0x96)
            Array.Copy(rdpsg, 0, ram, 0x96, rdpsg.Length);
            byte[] wrt = { 0xD3, 0xA0, 0xF5, 0x7B, 0xD3, 0xA1, 0xF1, 0xC9 };
            Array.Copy(wrt, 0, ram, 0xA0, wrt.Length);
        }
        ram[0x38] = 0xC9;
        int load = Math.Min(h.LoadSize, Math.Max(0, file.Length - h.DataAt));
        if (h.LoadAddr + load > 0x10000) load = 0x10000 - h.LoadAddr;
        Array.Copy(file, h.DataAt, ram, h.LoadAddr, load);
        if (h.BankCount > 0 && h.LoadAddr + load <= 0x8000)
        {
            // 처음 뱅크를 미리 넣어 둔다 (드라이버가 고르기 전에 읽는 곡이 있다)
            if (h.Bank8K) { m.SelectBank(h.FirstBank, 0x8000, 0x2000); m.SelectBank(h.FirstBank + 1, 0xA000, 0x2000); }
            else m.SelectBank(h.FirstBank, 0x8000, 0x4000);
        }

        // init
        Call(cpu, m, h.InitAddr, song, (int)(m.Clock * 2));
        double frame = VgmSong.Rate / (m.Sys.FrameRate > 0 ? m.Sys.FrameRate : 60.0);
        double total = seconds * VgmSong.Rate;
        for (double t = 0; t < total; t += frame)
        {
            m.Now = t;
            Call(cpu, m, h.PlayAddr, -1, (int)(m.Clock / 60 * 4));
        }

        // VGM으로
        var w = new VgmWriter { System = m.Sys.Name + " (KSS)", Notes = "KSS" };
        if (m.UsedPsg) { w.SetClock(VgmWriter.ClockAy8910, m.ChipClock("AY-3-8910", CpuClock / 2)); w.SetHeaderByte(0x78, 0x00); }
        if (m.UsedScc || m.UsedSccPlus) w.SetClock(VgmWriter.ClockK051649, SccEmulator.VgmClock(m.ChipClock(m.UsedSccPlus ? "SCC+" : "SCC", CpuClock)), m.UsedSccPlus ? 0x80000000u : 0);
        if (m.UsedOpll) w.SetClock(VgmWriter.ClockYm2413, m.ChipClock("YM2413", CpuClock));
        if (m.UsedAudio) w.SetClock(VgmWriter.ClockY8950, m.ChipClock("Y8950", CpuClock));
        if (m.UsedSn) w.SetClock(VgmWriter.ClockSn76489, m.ChipClock("SN76489", CpuClock));
        long at = 0;
        foreach (var (time, cmd) in m.Log)
        {
            long ts = (long)Math.Min(total, Math.Max(0, time));
            if (ts > at) { w.Wait(ts - at); at = ts; }
            w.Write(cmd);
        }
        if ((long)total > at) w.Wait((long)total - at);
        var v = VgmFormat.Read(w.ToArray());
        v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
        if (!m.UsedPsg && !m.UsedScc && !m.UsedSccPlus && !m.UsedOpll && !m.UsedAudio && !m.UsedSn) v.UnsupportedChips.Add("(칩 쓰기 없음)");
        return v;
    }

    /// <summary>addr를 부르고 함정(0x0000)으로 돌아오거나 HALT · 한도까지 돌린다. a ≥ 0이면 A = a.</summary>
    private static void Call(Z80 cpu, Machine m, int addr, int a, int maxCycles)
    {
        m.CpuStart = cpu.Cycles;
        cpu.Halted = false;
        if (a >= 0) a &= 0xFF;
        if (a >= 0) cpu.A = a;
        if (cpu.SP < 0x100 || cpu.SP > 0xFFF0 || a >= 0) cpu.SP = 0xF380;
        cpu.SP = (cpu.SP - 2) & 0xFFFF;
        m.Ram[cpu.SP] = Trap & 0xFF; m.Ram[cpu.SP + 1] = (Trap >> 8) & 0xFF;
        cpu.PC = addr;
        byte saved = m.Ram[Trap];
        long end = cpu.Cycles + maxCycles;
        while (cpu.Cycles < end)
        {
            if (cpu.PC == Trap) break;
            cpu.Step();
            if (cpu.Halted) break;
        }
        m.Ram[Trap] = saved;
    }

    private static int U16(byte[] d, int o) { return d[o] | (d[o + 1] << 8); }
}
