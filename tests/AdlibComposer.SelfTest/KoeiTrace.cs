using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>
/// 진단: 메가드라이브 카트리지를 아주 작은 MD 버스 (ROM · 작업 RAM · Z80 창 · VDP / 패드 흉내)에서 68000으로 부팅해,
/// 68000이 Z80 RAM에 쓰는 것을 PC와 함께 적는다 (Koei 사운드 명령 · 곡 주소 알아내기). --hootkoeitrace rom.bin [초]
/// </summary>
public sealed class KoeiTrace : IM68kBus
{
    private readonly byte[] rom;
    private readonly byte[] work = new byte[0x10000];
    private readonly HootMegaDrive.Machine z;
    private readonly M68000 cpu;
    private bool busReq, z80Reset = true, vblankFlag;
    public readonly List<(long cyc, uint pc, int addr, int val)> Writes = new List<(long, uint, int, int)>();
    public readonly Dictionary<uint, int> ReadPcs = new Dictionary<uint, int>();

    private KoeiTrace(byte[] rom)
    {
        this.rom = rom;
        z = new HootMegaDrive.Machine(rom);
        cpu = new M68000(this) { AddressErrors = false };
        cpu.Reset();
    }

    public static int Run(string path, double seconds)
    {
        byte[] rom = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? File.ReadAllBytes(Directory.GetFiles(ZipFolder.Extract(path), "*.bin").OrderByDescending(f => new FileInfo(f).Length).First())
            : File.ReadAllBytes(path);
        var t = new KoeiTrace(rom);
        const double clock = 7670453;
        long frame = (long)(clock / 60), next = frame;
        long end = (long)(clock * seconds);
        long zDebt = 0;
        while (t.cpu.Cycles < end)
        {
            long before = t.cpu.Cycles;
            t.cpu.Step();
            if (t.cpu.Cycles >= next) { next += frame; t.vblankFlag = true; t.cpu.IrqLevel = 6; }
            zDebt += t.cpu.Cycles - before;
            if (!t.z80Reset && !t.busReq && zDebt > 200) { t.z.Run(zDebt * 15 / 32); zDebt = 0; }
            else if (zDebt > 200) zDebt = 0;
        }
        Console.WriteLine("68000 PC " + t.cpu.PC.ToString("X6") + ", exceptions " + t.cpu.Exceptions + ", z80 frames " + t.z.Frames + ", chip writes " + t.z.Log.Count
            + ", fm key-on " + t.z.Log.Count(x => x.chip == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0));
        // 대량 복사 (같은 PC에서 수백 번)는 빼고
        var bulk = new HashSet<uint>(t.Writes.GroupBy(w => w.pc).Where(g => g.Count() > 256).Select(g => g.Key));
        Console.WriteLine("bulk-copy PCs: " + string.Join(" ", bulk.Select(p => p.ToString("X6"))));
        foreach (var w in t.Writes.Where(w => !bulk.Contains(w.pc)).Take(400))
            Console.WriteLine((w.cyc / clock).ToString("0.000") + " s  PC " + w.pc.ToString("X6") + "  Z80[" + w.addr.ToString("X4") + "] <- " + w.val.ToString("X2"));
        return 0;
    }

    public byte Read8(uint a)
    {
        a &= 0xFFFFFF;
        if (a < 0x400000) return a < rom.Length ? rom[a] : (byte)0xFF;
        if (a >= 0xE00000) return work[a & 0xFFFF];
        if (a >= 0xA00000 && a < 0xA10000) return z.Read((int)(a & 0x7FFF));
        if (a == 0xA10001) return 0xA0;                          // 판: 해외 NTSC
        if (a >= 0xA10002 && a < 0xA10020) return 0x7F;          // 패드: 아무것도 안 누름
        if (a == 0xA11100) return (byte)(busReq ? 0x00 : 0x01);  // bit0 = 0: 버스 받음
        if (a >= 0xC00000 && a < 0xC00020)
        {
            if ((a & 0x1C) == 4)
            {
                // 상태: FIFO 빔 (bit9), bit3 = 수직 귀선 (읽을 때마다 바뀌게)
                vblankFlag = !vblankFlag;
                return (a & 1) == 0 ? (byte)0x34 : (byte)(0x80 | (vblankFlag ? 0x08 : 0));
            }
            if ((a & 0x1C) == 8) return (byte)(cpu.Cycles >> 6);   // HV 계수기
            return 0;
        }
        return 0xFF;
    }

    public ushort Read16(uint a) { return (ushort)(Read8(a) << 8 | Read8(a + 1)); }

    public void Write8(uint a, byte v)
    {
        a &= 0xFFFFFF;
        if (a >= 0xE00000) { work[a & 0xFFFF] = v; return; }
        if (a >= 0xA00000 && a < 0xA10000)
        {
            int za = (int)(a & 0x7FFF);
            if (za < 0x4000) Writes.Add((cpu.Cycles, cpu.PC, za & 0x1FFF, v));
            z.Write(za, v);
            return;
        }
        if (a == 0xA11100) { busReq = (v & 1) != 0; return; }
        if (a == 0xA11200)
        {
            bool reset = (v & 1) == 0;
            if (z80Reset && !reset) { z.Cpu.PC = 0; z.Cpu.SP = 0x1FFF; z.Cpu.Iff1 = z.Cpu.Iff2 = false; z.Cpu.Halted = false; }
            z80Reset = reset;
        }
    }

    public void Write16(uint a, ushort v)
    {
        a &= 0xFFFFFF;
        if (a == 0xA11100 || a == 0xA11200) { Write8(a, (byte)(v >> 8)); return; }
        Write8(a, (byte)(v >> 8)); Write8(a + 1, (byte)v);
    }

    public int Acknowledge(int level) { cpu.IrqLevel = 0; return -1; }
}
