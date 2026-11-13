/* Nuked OPL3 — C# port
 * Copyright (C) 2013-2020 Nuke.YKT
 * C# port for AdlibComposer (FMOPLTool).
 *
 * This file is part of Nuked OPL3.
 *
 * Nuked OPL3 is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as
 * published by the Free Software Foundation, either version 2.1
 * of the License, or (at your option) any later version.
 *
 * Nuked OPL3 is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with Nuked OPL3. If not, see <https://www.gnu.org/licenses/>.
 *
 *  Nuked OPL3 emulator.
 *  Thanks:
 *      MAME Development Team(Jarek Burczynski, Tatsuyuki Satoh):
 *          Feedback and Rhythm part calculation information.
 *      forums.submarine.org.uk(carbon14, opl3):
 *          Tremolo and phase generator calculation information.
 *      OPLx decapsulated(Matthew Gambrell, Olli Niemitalo):
 *          OPL2 ROMs.
 *      siliconpr0n.org(John McMaster, digshadow):
 *          YMF262 and VRC VII decaps and die shots.
 *
 * version: 1.8 (https://github.com/nukeykt/Nuked-OPL3)
 *
 * Port notes:
 *  - C 포인터(slot->mod, channel->out[], slot->trem)는 신호 배열 sig[]의 번호로 바꿨다.
 *    sig[0] = zeromod, 슬롯 n의 out = sig[1 + 2n], fbmod = sig[2 + 2n].
 *  - OPL_ENABLE_STEREOEXT = 0, OPL_QUIRK_CHANNELSAMPLEDELAY = 1 (원본 기본값).
 *  - 쓰기 버퍼(OPL3_WriteRegBuffered)와 내장 리샘플러는 빼고, WriteReg와 칩 본래 주기(49716Hz) 출력만.
 *  - FeedbackScale: 되먹임 세기 배율 (1 = 원본 그대로). 원본에는 없는 확장.
 */
using System;

namespace AdlibComposer.Nuked;

/// <summary>Nuked OPL3 (YMF262) 사이클 정밀 에뮬레이터의 C# 포팅. NEW 비트(0x105)가 0이면 OPL2(YM3812)처럼 동작한다.</summary>
public sealed class NukedOpl3
{
    /// <summary>칩 본래 샘플레이트 (14.31818MHz / 288).</summary>
    public const int NativeRate = 49716;

    private const int Ch2Op = 0, Ch4Op = 1, Ch4Op2 = 2, ChDrum = 3;
    private const byte EgkNorm = 0x01, EgkDrum = 0x02;
    private const byte EgAttack = 0, EgDecay = 1, EgSustain = 2, EgRelease = 3;
    private const int Zero = 0;

    private static readonly ushort[] logsinrom = {
        0x859, 0x6c3, 0x607, 0x58b, 0x52e, 0x4e4, 0x4a6, 0x471, 0x443, 0x41a, 0x3f5, 0x3d3, 0x3b5, 0x398, 0x37e, 0x365,
        0x34e, 0x339, 0x324, 0x311, 0x2ff, 0x2ed, 0x2dc, 0x2cd, 0x2bd, 0x2af, 0x2a0, 0x293, 0x286, 0x279, 0x26d, 0x261,
        0x256, 0x24b, 0x240, 0x236, 0x22c, 0x222, 0x218, 0x20f, 0x206, 0x1fd, 0x1f5, 0x1ec, 0x1e4, 0x1dc, 0x1d4, 0x1cd,
        0x1c5, 0x1be, 0x1b7, 0x1b0, 0x1a9, 0x1a2, 0x19b, 0x195, 0x18f, 0x188, 0x182, 0x17c, 0x177, 0x171, 0x16b, 0x166,
        0x160, 0x15b, 0x155, 0x150, 0x14b, 0x146, 0x141, 0x13c, 0x137, 0x133, 0x12e, 0x129, 0x125, 0x121, 0x11c, 0x118,
        0x114, 0x10f, 0x10b, 0x107, 0x103, 0x0ff, 0x0fb, 0x0f8, 0x0f4, 0x0f0, 0x0ec, 0x0e9, 0x0e5, 0x0e2, 0x0de, 0x0db,
        0x0d7, 0x0d4, 0x0d1, 0x0cd, 0x0ca, 0x0c7, 0x0c4, 0x0c1, 0x0be, 0x0bb, 0x0b8, 0x0b5, 0x0b2, 0x0af, 0x0ac, 0x0a9,
        0x0a7, 0x0a4, 0x0a1, 0x09f, 0x09c, 0x099, 0x097, 0x094, 0x092, 0x08f, 0x08d, 0x08a, 0x088, 0x086, 0x083, 0x081,
        0x07f, 0x07d, 0x07a, 0x078, 0x076, 0x074, 0x072, 0x070, 0x06e, 0x06c, 0x06a, 0x068, 0x066, 0x064, 0x062, 0x060,
        0x05e, 0x05c, 0x05b, 0x059, 0x057, 0x055, 0x053, 0x052, 0x050, 0x04e, 0x04d, 0x04b, 0x04a, 0x048, 0x046, 0x045,
        0x043, 0x042, 0x040, 0x03f, 0x03e, 0x03c, 0x03b, 0x039, 0x038, 0x037, 0x035, 0x034, 0x033, 0x031, 0x030, 0x02f,
        0x02e, 0x02d, 0x02b, 0x02a, 0x029, 0x028, 0x027, 0x026, 0x025, 0x024, 0x023, 0x022, 0x021, 0x020, 0x01f, 0x01e,
        0x01d, 0x01c, 0x01b, 0x01a, 0x019, 0x018, 0x017, 0x017, 0x016, 0x015, 0x014, 0x014, 0x013, 0x012, 0x011, 0x011,
        0x010, 0x00f, 0x00f, 0x00e, 0x00d, 0x00d, 0x00c, 0x00c, 0x00b, 0x00a, 0x00a, 0x009, 0x009, 0x008, 0x008, 0x007,
        0x007, 0x007, 0x006, 0x006, 0x005, 0x005, 0x005, 0x004, 0x004, 0x004, 0x003, 0x003, 0x003, 0x002, 0x002, 0x002,
        0x002, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x001, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000, 0x000,
    };

    private static readonly ushort[] exprom = {
        0x7fa, 0x7f5, 0x7ef, 0x7ea, 0x7e4, 0x7df, 0x7da, 0x7d4, 0x7cf, 0x7c9, 0x7c4, 0x7bf, 0x7b9, 0x7b4, 0x7ae, 0x7a9,
        0x7a4, 0x79f, 0x799, 0x794, 0x78f, 0x78a, 0x784, 0x77f, 0x77a, 0x775, 0x770, 0x76a, 0x765, 0x760, 0x75b, 0x756,
        0x751, 0x74c, 0x747, 0x742, 0x73d, 0x738, 0x733, 0x72e, 0x729, 0x724, 0x71f, 0x71a, 0x715, 0x710, 0x70b, 0x706,
        0x702, 0x6fd, 0x6f8, 0x6f3, 0x6ee, 0x6e9, 0x6e5, 0x6e0, 0x6db, 0x6d6, 0x6d2, 0x6cd, 0x6c8, 0x6c4, 0x6bf, 0x6ba,
        0x6b5, 0x6b1, 0x6ac, 0x6a8, 0x6a3, 0x69e, 0x69a, 0x695, 0x691, 0x68c, 0x688, 0x683, 0x67f, 0x67a, 0x676, 0x671,
        0x66d, 0x668, 0x664, 0x65f, 0x65b, 0x657, 0x652, 0x64e, 0x649, 0x645, 0x641, 0x63c, 0x638, 0x634, 0x630, 0x62b,
        0x627, 0x623, 0x61e, 0x61a, 0x616, 0x612, 0x60e, 0x609, 0x605, 0x601, 0x5fd, 0x5f9, 0x5f5, 0x5f0, 0x5ec, 0x5e8,
        0x5e4, 0x5e0, 0x5dc, 0x5d8, 0x5d4, 0x5d0, 0x5cc, 0x5c8, 0x5c4, 0x5c0, 0x5bc, 0x5b8, 0x5b4, 0x5b0, 0x5ac, 0x5a8,
        0x5a4, 0x5a0, 0x59c, 0x599, 0x595, 0x591, 0x58d, 0x589, 0x585, 0x581, 0x57e, 0x57a, 0x576, 0x572, 0x56f, 0x56b,
        0x567, 0x563, 0x560, 0x55c, 0x558, 0x554, 0x551, 0x54d, 0x549, 0x546, 0x542, 0x53e, 0x53b, 0x537, 0x534, 0x530,
        0x52c, 0x529, 0x525, 0x522, 0x51e, 0x51b, 0x517, 0x514, 0x510, 0x50c, 0x509, 0x506, 0x502, 0x4ff, 0x4fb, 0x4f8,
        0x4f4, 0x4f1, 0x4ed, 0x4ea, 0x4e7, 0x4e3, 0x4e0, 0x4dc, 0x4d9, 0x4d6, 0x4d2, 0x4cf, 0x4cc, 0x4c8, 0x4c5, 0x4c2,
        0x4be, 0x4bb, 0x4b8, 0x4b5, 0x4b1, 0x4ae, 0x4ab, 0x4a8, 0x4a4, 0x4a1, 0x49e, 0x49b, 0x498, 0x494, 0x491, 0x48e,
        0x48b, 0x488, 0x485, 0x482, 0x47e, 0x47b, 0x478, 0x475, 0x472, 0x46f, 0x46c, 0x469, 0x466, 0x463, 0x460, 0x45d,
        0x45a, 0x457, 0x454, 0x451, 0x44e, 0x44b, 0x448, 0x445, 0x442, 0x43f, 0x43c, 0x439, 0x436, 0x433, 0x430, 0x42d,
        0x42a, 0x428, 0x425, 0x422, 0x41f, 0x41c, 0x419, 0x416, 0x414, 0x411, 0x40e, 0x40b, 0x408, 0x406, 0x403, 0x400,
    };

    /// <summary>freq mult × 2: 1/2, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 10, 12, 12, 15, 15.</summary>
    private static readonly byte[] mt = { 1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30 };
    private static readonly byte[] kslrom = { 0, 32, 40, 45, 48, 51, 53, 55, 56, 58, 59, 60, 61, 62, 63, 64 };
    private static readonly byte[] kslshift = { 8, 1, 2, 0 };
    private static readonly byte[,] eg_incstep = { { 0, 0, 0, 0 }, { 1, 0, 0, 0 }, { 1, 0, 1, 0 }, { 1, 1, 1, 0 } };
    private static readonly sbyte[] ad_slot = {
        0, 1, 2, 3, 4, 5, -1, -1, 6, 7, 8, 9, 10, 11, -1, -1,
        12, 13, 14, 15, 16, 17, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
    };
    private static readonly byte[] ch_slot = { 0, 1, 2, 6, 7, 8, 12, 13, 14, 18, 19, 20, 24, 25, 26, 30, 31, 32 };

    private sealed class Slot
    {
        public Channel channel;
        public int outIdx, fbIdx;   // sig[] 번호 (이 슬롯의 out / fbmod)
        public int mod;             // sig[] 번호 (모듈레이션 입력)
        public short prout;
        public ushort eg_rout, eg_out;
        public byte eg_gen, eg_ksl;
        public bool tremOn;
        public byte reg_vib, reg_type, reg_ksr, reg_mult, reg_ksl, reg_tl, reg_ar, reg_dr, reg_sl, reg_rr, reg_wf;
        public byte key;
        public bool pg_reset;
        public uint pg_phase;
        public ushort pg_phase_out;
        public byte slot_num;
    }

    private sealed class Channel
    {
        public readonly Slot[] slotz = new Slot[2];
        public Channel pair;
        public readonly int[] outs = new int[4];   // sig[] 번호
        public int chtype;
        public ushort f_num, f_num_reg;
        public byte block, block_reg, fb, con, alg, ksv;
        public bool cha, chb, chc, chd;
        public byte ch_num;
    }

    private readonly short[] sig = new short[1 + 36 * 2];
    private readonly Channel[] channel = new Channel[18];
    private readonly Slot[] slot = new Slot[36];
    private ushort timer;
    private ulong eg_timer;
    private byte eg_timerrem, eg_state, eg_add, eg_timer_lo;
    private byte newm, nts, rhy, vibpos, vibshift, tremolo, tremolopos, tremoloshift;
    private uint noise;
    private readonly int[] mixbuff = new int[4];
    private byte rm_hh_bit2, rm_hh_bit3, rm_hh_bit7, rm_hh_bit8, rm_tc_bit3, rm_tc_bit5;

    /// <summary>되먹임 세기 배율 (1 = 원본 그대로). 원본에는 없는 확장.</summary>
    public double FeedbackScale { get; set; } = 1.0;

    public NukedOpl3()
    {
        for (int i = 0; i < 36; i++) slot[i] = new Slot();
        for (int i = 0; i < 18; i++) channel[i] = new Channel();
        Reset();
    }

    // ------------------------------------------------------------ envelope

    private static short CalcExp(uint level)
    {
        if (level > 0x1fff) level = 0x1fff;
        return (short)((exprom[level & 0xff] << 1) >> (int)(level >> 8));
    }

    private static short Neg(short v, bool neg) { return neg ? (short)~v : v; }

    private static short EnvelopeSin(int wf, int phase, int envelope)
    {
        phase &= 0x3ff;
        uint env = (uint)(envelope << 3);
        uint o;
        switch (wf)
        {
            case 0:
                o = (phase & 0x100) != 0 ? logsinrom[(phase & 0xff) ^ 0xff] : logsinrom[phase & 0xff];
                return Neg(CalcExp(o + env), (phase & 0x200) != 0);
            case 1:
                if ((phase & 0x200) != 0) o = 0x1000;
                else if ((phase & 0x100) != 0) o = logsinrom[(phase & 0xff) ^ 0xff];
                else o = logsinrom[phase & 0xff];
                return CalcExp(o + env);
            case 2:
                o = (phase & 0x100) != 0 ? logsinrom[(phase & 0xff) ^ 0xff] : logsinrom[phase & 0xff];
                return CalcExp(o + env);
            case 3:
                o = (phase & 0x100) != 0 ? 0x1000u : logsinrom[phase & 0xff];
                return CalcExp(o + env);
            case 4:
                if ((phase & 0x200) != 0) o = 0x1000;
                else if ((phase & 0x80) != 0) o = logsinrom[((phase ^ 0xff) << 1) & 0xff];
                else o = logsinrom[(phase << 1) & 0xff];
                return Neg(CalcExp(o + env), (phase & 0x300) == 0x100);
            case 5:
                if ((phase & 0x200) != 0) o = 0x1000;
                else if ((phase & 0x80) != 0) o = logsinrom[((phase ^ 0xff) << 1) & 0xff];
                else o = logsinrom[(phase << 1) & 0xff];
                return CalcExp(o + env);
            case 6:
                return Neg(CalcExp(env), (phase & 0x200) != 0);
            default:
            {
                bool neg = (phase & 0x200) != 0;
                if (neg) phase = (phase & 0x1ff) ^ 0x1ff;
                o = (uint)(phase << 3);
                return Neg(CalcExp(o + env), neg);
            }
        }
    }

    private static void EnvelopeUpdateKSL(Slot s)
    {
        int ksl = (kslrom[s.channel.f_num >> 6] << 2) - ((0x08 - s.channel.block) << 5);
        if (ksl < 0) ksl = 0;
        s.eg_ksl = (byte)ksl;
    }

    private void EnvelopeCalc(Slot s)
    {
        int reg_rate = 0;
        bool reset = false;
        s.eg_out = (ushort)(s.eg_rout + (s.reg_tl << 2) + (s.eg_ksl >> kslshift[s.reg_ksl]) + (s.tremOn ? tremolo : 0));
        if (s.key != 0 && s.eg_gen == EgRelease)
        {
            reset = true;
            reg_rate = s.reg_ar;
        }
        else
        {
            switch (s.eg_gen)
            {
                case EgAttack: reg_rate = s.reg_ar; break;
                case EgDecay: reg_rate = s.reg_dr; break;
                case EgSustain: if (s.reg_type == 0) reg_rate = s.reg_rr; break;
                case EgRelease: reg_rate = s.reg_rr; break;
            }
        }
        s.pg_reset = reset;
        int ks = s.channel.ksv >> ((s.reg_ksr ^ 1) << 1);
        bool nonzero = reg_rate != 0;
        int rate = ks + (reg_rate << 2);
        int rate_hi = rate >> 2;
        int rate_lo = rate & 0x03;
        if ((rate_hi & 0x10) != 0) rate_hi = 0x0f;
        int eg_shift = rate_hi + eg_add;
        int shift = 0;
        if (nonzero)
        {
            if (rate_hi < 12)
            {
                if (eg_state != 0)
                {
                    switch (eg_shift)
                    {
                        case 12: shift = 1; break;
                        case 13: shift = (rate_lo >> 1) & 0x01; break;
                        case 14: shift = rate_lo & 0x01; break;
                    }
                }
            }
            else
            {
                shift = (rate_hi & 0x03) + eg_incstep[rate_lo, eg_timer_lo];
                if ((shift & 0x04) != 0) shift = 0x03;
                if (shift == 0) shift = eg_state;
            }
        }
        int eg_rout = s.eg_rout;
        int eg_inc = 0;
        bool eg_off = false;
        if (reset && rate_hi == 0x0f) eg_rout = 0x00;   // Instant attack
        if ((s.eg_rout & 0x1f8) == 0x1f8) eg_off = true; // Envelope off
        if (s.eg_gen != EgAttack && !reset && eg_off) eg_rout = 0x1ff;
        switch (s.eg_gen)
        {
            case EgAttack:
                if (s.eg_rout == 0) s.eg_gen = EgDecay;
                else if (s.key != 0 && shift > 0 && rate_hi != 0x0f) eg_inc = (short)(~s.eg_rout >> (4 - shift));
                break;
            case EgDecay:
                if ((s.eg_rout >> 4) == s.reg_sl) s.eg_gen = EgSustain;
                else if (!eg_off && !reset && shift > 0) eg_inc = 1 << (shift - 1);
                break;
            case EgSustain:
            case EgRelease:
                if (!eg_off && !reset && shift > 0) eg_inc = 1 << (shift - 1);
                break;
        }
        s.eg_rout = (ushort)((eg_rout + eg_inc) & 0x1ff);
        if (reset) s.eg_gen = EgAttack;   // Key off
        if (s.key == 0) s.eg_gen = EgRelease;
    }

    private static void KeyOn(Slot s, byte type) { s.key |= type; }
    private static void KeyOff(Slot s, byte type) { s.key &= (byte)~type; }

    // ------------------------------------------------------------ phase generator

    private void PhaseGenerate(Slot s)
    {
        int f_num = s.channel.f_num;
        if (s.reg_vib != 0)
        {
            int range = (f_num >> 7) & 7;
            if ((vibpos & 3) == 0) range = 0;
            else if ((vibpos & 1) != 0) range >>= 1;
            range >>= vibshift;
            if ((vibpos & 4) != 0) range = -range;
            f_num = (ushort)(f_num + range);
        }
        uint basefreq = ((uint)f_num << s.channel.block) >> 1;
        ushort phase = (ushort)(s.pg_phase >> 9);
        if (s.pg_reset) s.pg_phase = 0;
        s.pg_phase += (basefreq * mt[s.reg_mult]) >> 1;
        // Rhythm mode
        uint n = noise;
        s.pg_phase_out = phase;
        if (s.slot_num == 13) // hh
        {
            rm_hh_bit2 = (byte)((phase >> 2) & 1);
            rm_hh_bit3 = (byte)((phase >> 3) & 1);
            rm_hh_bit7 = (byte)((phase >> 7) & 1);
            rm_hh_bit8 = (byte)((phase >> 8) & 1);
        }
        if (s.slot_num == 17 && (rhy & 0x20) != 0) // tc
        {
            rm_tc_bit3 = (byte)((phase >> 3) & 1);
            rm_tc_bit5 = (byte)((phase >> 5) & 1);
        }
        if ((rhy & 0x20) != 0)
        {
            int rm_xor = (rm_hh_bit2 ^ rm_hh_bit7) | (rm_hh_bit3 ^ rm_tc_bit5) | (rm_tc_bit3 ^ rm_tc_bit5);
            switch (s.slot_num)
            {
                case 13: // hh
                    s.pg_phase_out = (ushort)(rm_xor << 9);
                    if ((rm_xor ^ (int)(n & 1)) != 0) s.pg_phase_out |= 0xd0;
                    else s.pg_phase_out |= 0x34;
                    break;
                case 16: // sd
                    s.pg_phase_out = (ushort)((rm_hh_bit8 << 9) | ((rm_hh_bit8 ^ (int)(n & 1)) << 8));
                    break;
                case 17: // tc
                    s.pg_phase_out = (ushort)((rm_xor << 9) | 0x80);
                    break;
            }
        }
        uint n_bit = ((n >> 14) ^ n) & 0x01;
        noise = (n >> 1) | (n_bit << 22);
    }

    // ------------------------------------------------------------ slot

    private void SlotWrite20(Slot s, int data)
    {
        s.tremOn = ((data >> 7) & 0x01) != 0;
        s.reg_vib = (byte)((data >> 6) & 0x01);
        s.reg_type = (byte)((data >> 5) & 0x01);
        s.reg_ksr = (byte)((data >> 4) & 0x01);
        s.reg_mult = (byte)(data & 0x0f);
    }

    private static void SlotWrite40(Slot s, int data)
    {
        s.reg_ksl = (byte)((data >> 6) & 0x03);
        s.reg_tl = (byte)(data & 0x3f);
        EnvelopeUpdateKSL(s);
    }

    private static void SlotWrite60(Slot s, int data)
    {
        s.reg_ar = (byte)((data >> 4) & 0x0f);
        s.reg_dr = (byte)(data & 0x0f);
    }

    private static void SlotWrite80(Slot s, int data)
    {
        s.reg_sl = (byte)((data >> 4) & 0x0f);
        if (s.reg_sl == 0x0f) s.reg_sl = 0x1f;
        s.reg_rr = (byte)(data & 0x0f);
    }

    private void SlotWriteE0(Slot s, int data)
    {
        s.reg_wf = (byte)(data & 0x07);
        if (newm == 0) s.reg_wf &= 0x03;
    }

    private void SlotGenerate(Slot s)
    {
        sig[s.outIdx] = EnvelopeSin(s.reg_wf, s.pg_phase_out + sig[s.mod], s.eg_out);
    }

    private void SlotCalcFB(Slot s)
    {
        short o = sig[s.outIdx];
        if (s.channel.fb != 0)
        {
            int fb = (s.prout + o) >> (0x09 - s.channel.fb);
            sig[s.fbIdx] = FeedbackScale == 1.0 ? (short)fb : (short)(fb * FeedbackScale);
        }
        else sig[s.fbIdx] = 0;
        s.prout = o;
    }

    // ------------------------------------------------------------ channel

    private void ChannelUpdateRhythm(int data)
    {
        rhy = (byte)(data & 0x3f);
        if ((rhy & 0x20) != 0)
        {
            Channel c6 = channel[6], c7 = channel[7], c8 = channel[8];
            c6.outs[0] = c6.slotz[1].outIdx; c6.outs[1] = c6.slotz[1].outIdx; c6.outs[2] = Zero; c6.outs[3] = Zero;
            c7.outs[0] = c7.slotz[0].outIdx; c7.outs[1] = c7.slotz[0].outIdx; c7.outs[2] = c7.slotz[1].outIdx; c7.outs[3] = c7.slotz[1].outIdx;
            c8.outs[0] = c8.slotz[0].outIdx; c8.outs[1] = c8.slotz[0].outIdx; c8.outs[2] = c8.slotz[1].outIdx; c8.outs[3] = c8.slotz[1].outIdx;
            for (int ch = 6; ch < 9; ch++) channel[ch].chtype = ChDrum;
            ChannelSetupAlg(c6);
            ChannelSetupAlg(c7);
            ChannelSetupAlg(c8);
            if ((rhy & 0x01) != 0) KeyOn(c7.slotz[0], EgkDrum); else KeyOff(c7.slotz[0], EgkDrum);   // hh
            if ((rhy & 0x02) != 0) KeyOn(c8.slotz[1], EgkDrum); else KeyOff(c8.slotz[1], EgkDrum);   // tc
            if ((rhy & 0x04) != 0) KeyOn(c8.slotz[0], EgkDrum); else KeyOff(c8.slotz[0], EgkDrum);   // tom
            if ((rhy & 0x08) != 0) KeyOn(c7.slotz[1], EgkDrum); else KeyOff(c7.slotz[1], EgkDrum);   // sd
            if ((rhy & 0x10) != 0) { KeyOn(c6.slotz[0], EgkDrum); KeyOn(c6.slotz[1], EgkDrum); }     // bd
            else { KeyOff(c6.slotz[0], EgkDrum); KeyOff(c6.slotz[1], EgkDrum); }
        }
        else
        {
            for (int ch = 6; ch < 9; ch++)
            {
                channel[ch].chtype = Ch2Op;
                ChannelSetupAlg(channel[ch]);
                KeyOff(channel[ch].slotz[0], EgkDrum);
                KeyOff(channel[ch].slotz[1], EgkDrum);
            }
        }
    }

    private void ChannelUpdateFrequency(Channel c)
    {
        c.ksv = (byte)((c.block << 1) | ((c.f_num >> (0x09 - nts)) & 0x01));
        EnvelopeUpdateKSL(c.slotz[0]);
        EnvelopeUpdateKSL(c.slotz[1]);
    }

    private void ChannelRestoreFrequency(Channel c)
    {
        c.f_num = c.f_num_reg;
        c.block = c.block_reg;
        ChannelUpdateFrequency(c);
    }

    private void ChannelSync4Op(Channel c)
    {
        c.pair.f_num = c.f_num;
        c.pair.block = c.block;
        ChannelUpdateFrequency(c.pair);
    }

    private void ChannelWriteA0(Channel c, int data)
    {
        c.f_num_reg = (ushort)((c.f_num_reg & 0x300) | data);
        if (c.chtype == Ch4Op2) return;
        ChannelRestoreFrequency(c);
        if (c.chtype == Ch4Op) ChannelSync4Op(c);
    }

    private void ChannelWriteB0(Channel c, int data)
    {
        c.f_num_reg = (ushort)((c.f_num_reg & 0xff) | ((data & 0x03) << 8));
        c.block_reg = (byte)((data >> 2) & 0x07);
        if (c.chtype == Ch4Op2) return;
        ChannelRestoreFrequency(c);
        if (c.chtype == Ch4Op) ChannelSync4Op(c);
    }

    private static void ChannelSetupAlg(Channel c)
    {
        if (c.chtype == ChDrum)
        {
            if (c.ch_num == 7 || c.ch_num == 8)
            {
                c.slotz[0].mod = Zero;
                c.slotz[1].mod = Zero;
                return;
            }
            if ((c.alg & 0x01) == 0)
            {
                c.slotz[0].mod = c.slotz[0].fbIdx;
                c.slotz[1].mod = c.slotz[0].outIdx;
            }
            else
            {
                c.slotz[0].mod = c.slotz[0].fbIdx;
                c.slotz[1].mod = Zero;
            }
            return;
        }
        if ((c.alg & 0x08) != 0) return;
        if ((c.alg & 0x04) != 0)
        {
            Channel p = c.pair;
            p.outs[0] = p.outs[1] = p.outs[2] = p.outs[3] = Zero;
            switch (c.alg & 0x03)
            {
                case 0x00:
                    p.slotz[0].mod = p.slotz[0].fbIdx;
                    p.slotz[1].mod = p.slotz[0].outIdx;
                    c.slotz[0].mod = p.slotz[1].outIdx;
                    c.slotz[1].mod = c.slotz[0].outIdx;
                    c.outs[0] = c.slotz[1].outIdx; c.outs[1] = Zero; c.outs[2] = Zero; c.outs[3] = Zero;
                    break;
                case 0x01:
                    p.slotz[0].mod = p.slotz[0].fbIdx;
                    p.slotz[1].mod = p.slotz[0].outIdx;
                    c.slotz[0].mod = Zero;
                    c.slotz[1].mod = c.slotz[0].outIdx;
                    c.outs[0] = p.slotz[1].outIdx; c.outs[1] = c.slotz[1].outIdx; c.outs[2] = Zero; c.outs[3] = Zero;
                    break;
                case 0x02:
                    p.slotz[0].mod = p.slotz[0].fbIdx;
                    p.slotz[1].mod = Zero;
                    c.slotz[0].mod = p.slotz[1].outIdx;
                    c.slotz[1].mod = c.slotz[0].outIdx;
                    c.outs[0] = p.slotz[0].outIdx; c.outs[1] = c.slotz[1].outIdx; c.outs[2] = Zero; c.outs[3] = Zero;
                    break;
                case 0x03:
                    p.slotz[0].mod = p.slotz[0].fbIdx;
                    p.slotz[1].mod = Zero;
                    c.slotz[0].mod = p.slotz[1].outIdx;
                    c.slotz[1].mod = Zero;
                    c.outs[0] = p.slotz[0].outIdx; c.outs[1] = c.slotz[0].outIdx; c.outs[2] = c.slotz[1].outIdx; c.outs[3] = Zero;
                    break;
            }
        }
        else
        {
            if ((c.alg & 0x01) == 0)
            {
                c.slotz[0].mod = c.slotz[0].fbIdx;
                c.slotz[1].mod = c.slotz[0].outIdx;
                c.outs[0] = c.slotz[1].outIdx; c.outs[1] = Zero; c.outs[2] = Zero; c.outs[3] = Zero;
            }
            else
            {
                c.slotz[0].mod = c.slotz[0].fbIdx;
                c.slotz[1].mod = Zero;
                c.outs[0] = c.slotz[0].outIdx; c.outs[1] = c.slotz[1].outIdx; c.outs[2] = Zero; c.outs[3] = Zero;
            }
        }
    }

    private static void ChannelUpdateAlg(Channel c)
    {
        c.alg = c.con;
        if (c.chtype == Ch4Op)
        {
            c.pair.alg = (byte)(0x04 | (c.con << 1) | c.pair.con);
            c.alg = 0x08;
            ChannelSetupAlg(c.pair);
        }
        else if (c.chtype == Ch4Op2)
        {
            c.alg = (byte)(0x04 | (c.pair.con << 1) | c.con);
            c.pair.alg = 0x08;
            ChannelSetupAlg(c);
        }
        else ChannelSetupAlg(c);
    }

    private void ChannelWriteC0(Channel c, int data)
    {
        c.fb = (byte)((data & 0x0e) >> 1);
        c.con = (byte)(data & 0x01);
        ChannelUpdateAlg(c);
        if (newm != 0)
        {
            c.cha = ((data >> 4) & 0x01) != 0;
            c.chb = ((data >> 5) & 0x01) != 0;
            c.chc = ((data >> 6) & 0x01) != 0;
            c.chd = ((data >> 7) & 0x01) != 0;
        }
        else
        {
            c.cha = c.chb = true;
            c.chc = c.chd = false;
        }
    }

    private static void ChannelKeyOn(Channel c)
    {
        if (c.chtype == Ch4Op)
        {
            KeyOn(c.slotz[0], EgkNorm); KeyOn(c.slotz[1], EgkNorm);
            KeyOn(c.pair.slotz[0], EgkNorm); KeyOn(c.pair.slotz[1], EgkNorm);
        }
        else if (c.chtype == Ch2Op || c.chtype == ChDrum)
        {
            KeyOn(c.slotz[0], EgkNorm); KeyOn(c.slotz[1], EgkNorm);
        }
    }

    private static void ChannelKeyOff(Channel c)
    {
        if (c.chtype == Ch4Op)
        {
            KeyOff(c.slotz[0], EgkNorm); KeyOff(c.slotz[1], EgkNorm);
            KeyOff(c.pair.slotz[0], EgkNorm); KeyOff(c.pair.slotz[1], EgkNorm);
        }
        else if (c.chtype == Ch2Op || c.chtype == ChDrum)
        {
            KeyOff(c.slotz[0], EgkNorm); KeyOff(c.slotz[1], EgkNorm);
        }
    }

    private void ChannelSet4Op(int data)
    {
        for (int bit = 0; bit < 6; bit++)
        {
            int chnum = bit;
            if (bit >= 3) chnum += 9 - 3;
            if (((data >> bit) & 0x01) != 0)
            {
                channel[chnum].chtype = Ch4Op;
                channel[chnum + 3].chtype = Ch4Op2;
                ChannelSync4Op(channel[chnum]);
                ChannelUpdateAlg(channel[chnum]);
            }
            else
            {
                channel[chnum].chtype = Ch2Op;
                channel[chnum + 3].chtype = Ch2Op;
                ChannelRestoreFrequency(channel[chnum + 3]);
                ChannelUpdateAlg(channel[chnum]);
                ChannelUpdateAlg(channel[chnum + 3]);
            }
        }
    }

    private static short ClipSample(int sample)
    {
        if (sample > 32767) sample = 32767;
        else if (sample < -32768) sample = -32768;
        return (short)sample;
    }

    private void ProcessSlot(Slot s)
    {
        SlotCalcFB(s);
        EnvelopeCalc(s);
        PhaseGenerate(s);
        SlotGenerate(s);
    }

    private void Mix(bool right, out int a, out int b)
    {
        a = b = 0;
        for (int ii = 0; ii < 18; ii++)
        {
            var c = channel[ii];
            short accm = (short)(sig[c.outs[0]] + sig[c.outs[1]] + sig[c.outs[2]] + sig[c.outs[3]]);
            if (right ? c.chb : c.cha) a += accm;
            if (right ? c.chd : c.chc) b += accm;
        }
    }

    // ------------------------------------------------------------ public

    /// <summary>칩 한 샘플 (49716Hz) 4채널 출력: [0] = A(왼쪽), [1] = B(오른쪽), [2] = C, [3] = D.</summary>
    public void Generate4Ch(short[] buf4)
    {
        buf4[1] = ClipSample(mixbuff[1]);
        buf4[3] = ClipSample(mixbuff[3]);

        for (int ii = 0; ii < 15; ii++) ProcessSlot(slot[ii]);
        Mix(false, out mixbuff[0], out mixbuff[2]);
        for (int ii = 15; ii < 18; ii++) ProcessSlot(slot[ii]);

        buf4[0] = ClipSample(mixbuff[0]);
        buf4[2] = ClipSample(mixbuff[2]);

        for (int ii = 18; ii < 33; ii++) ProcessSlot(slot[ii]);
        Mix(true, out mixbuff[1], out mixbuff[3]);
        for (int ii = 33; ii < 36; ii++) ProcessSlot(slot[ii]);

        if ((timer & 0x3f) == 0x3f) tremolopos = (byte)((tremolopos + 1) % 210);
        if (tremolopos < 105) tremolo = (byte)(tremolopos >> tremoloshift);
        else tremolo = (byte)((210 - tremolopos) >> tremoloshift);

        if ((timer & 0x3ff) == 0x3ff) vibpos = (byte)((vibpos + 1) & 7);

        timer++;

        if (eg_state != 0)
        {
            int shift = 0;
            while (shift < 13 && ((eg_timer >> shift) & 1) == 0) shift++;
            eg_add = shift > 12 ? (byte)0 : (byte)(shift + 1);
            eg_timer_lo = (byte)(eg_timer & 0x3u);
        }

        if (eg_timerrem != 0 || eg_state != 0)
        {
            if (eg_timer == 0xfffffffffUL) { eg_timer = 0; eg_timerrem = 1; }
            else { eg_timer++; eg_timerrem = 0; }
        }

        eg_state ^= 1;
    }

    private readonly short[] tmp4 = new short[4];

    /// <summary>칩 한 샘플 (49716Hz) 왼쪽 / 오른쪽 (출력 A / B).</summary>
    public void Generate(out short left, out short right)
    {
        Generate4Ch(tmp4);
        left = tmp4[0];
        right = tmp4[1];
    }

    public void Reset()
    {
        Array.Clear(sig, 0, sig.Length);
        Array.Clear(mixbuff, 0, mixbuff.Length);
        timer = 0; eg_timer = 0; eg_timerrem = eg_state = eg_add = eg_timer_lo = 0;
        newm = nts = rhy = vibpos = tremolo = tremolopos = 0;
        rm_hh_bit2 = rm_hh_bit3 = rm_hh_bit7 = rm_hh_bit8 = rm_tc_bit3 = rm_tc_bit5 = 0;
        for (int n = 0; n < 36; n++)
        {
            var s = slot[n];
            s.channel = null; s.prout = 0; s.eg_ksl = 0; s.tremOn = false;
            s.reg_vib = s.reg_type = s.reg_ksr = s.reg_mult = s.reg_ksl = s.reg_tl = s.reg_ar = s.reg_dr = s.reg_sl = s.reg_rr = s.reg_wf = 0;
            s.key = 0; s.pg_reset = false; s.pg_phase = 0; s.pg_phase_out = 0;
            s.outIdx = 1 + 2 * n;
            s.fbIdx = 2 + 2 * n;
            s.mod = Zero;
            s.eg_rout = 0x1ff;
            s.eg_out = 0x1ff;
            s.eg_gen = EgRelease;
            s.slot_num = (byte)n;
        }
        for (int n = 0; n < 18; n++)
        {
            var c = channel[n];
            int ls = ch_slot[n];
            c.slotz[0] = slot[ls];
            c.slotz[1] = slot[ls + 3];
            slot[ls].channel = c;
            slot[ls + 3].channel = c;
            if (n % 9 < 3) c.pair = channel[n + 3];
            else if (n % 9 < 6) c.pair = channel[n - 3];
            else c.pair = null;
            c.outs[0] = c.outs[1] = c.outs[2] = c.outs[3] = Zero;
            c.chtype = Ch2Op;
            c.f_num = c.f_num_reg = 0; c.block = c.block_reg = 0; c.fb = c.con = c.alg = c.ksv = 0;
            c.cha = c.chb = true;
            c.chc = c.chd = false;
            c.ch_num = (byte)n;
            ChannelSetupAlg(c);
        }
        noise = 1;
        tremoloshift = 4;
        vibshift = 1;
    }

    /// <summary>레지스터 쓰기. reg = 0x000~0x0FF (첫 뱅크) / 0x100~0x1FF (둘째 뱅크).</summary>
    public void WriteReg(int reg, int v)
    {
        v &= 0xff;
        int high = (reg >> 8) & 0x01;
        int regm = reg & 0xff;
        switch (regm & 0xf0)
        {
            case 0x00:
                if (high != 0)
                {
                    switch (regm & 0x0f)
                    {
                        case 0x04: ChannelSet4Op(v); break;
                        case 0x05: newm = (byte)(v & 0x01); break;
                    }
                }
                else if ((regm & 0x0f) == 0x08) nts = (byte)((v >> 6) & 0x01);
                break;
            case 0x20:
            case 0x30:
                if (ad_slot[regm & 0x1f] >= 0) SlotWrite20(slot[18 * high + ad_slot[regm & 0x1f]], v);
                break;
            case 0x40:
            case 0x50:
                if (ad_slot[regm & 0x1f] >= 0) SlotWrite40(slot[18 * high + ad_slot[regm & 0x1f]], v);
                break;
            case 0x60:
            case 0x70:
                if (ad_slot[regm & 0x1f] >= 0) SlotWrite60(slot[18 * high + ad_slot[regm & 0x1f]], v);
                break;
            case 0x80:
            case 0x90:
                if (ad_slot[regm & 0x1f] >= 0) SlotWrite80(slot[18 * high + ad_slot[regm & 0x1f]], v);
                break;
            case 0xe0:
            case 0xf0:
                if (ad_slot[regm & 0x1f] >= 0) SlotWriteE0(slot[18 * high + ad_slot[regm & 0x1f]], v);
                break;
            case 0xa0:
                if ((regm & 0x0f) < 9) ChannelWriteA0(channel[9 * high + (regm & 0x0f)], v);
                break;
            case 0xb0:
                if (regm == 0xbd && high == 0)
                {
                    tremoloshift = (byte)((((v >> 7) ^ 1) << 1) + 2);
                    vibshift = (byte)(((v >> 6) & 0x01) ^ 1);
                    ChannelUpdateRhythm(v);
                }
                else if ((regm & 0x0f) < 9)
                {
                    var c = channel[9 * high + (regm & 0x0f)];
                    ChannelWriteB0(c, v);
                    if ((v & 0x20) != 0) ChannelKeyOn(c);
                    else ChannelKeyOff(c);
                }
                break;
            case 0xc0:
                if ((regm & 0x0f) < 9) ChannelWriteC0(channel[9 * high + (regm & 0x0f)], v);
                break;
        }
    }
}
