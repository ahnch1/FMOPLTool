using System;

namespace AdlibComposer.Audio.Dos;

/// <summary>
/// 인터럽트 제어기 8259A 하나 (PC-98 · PC/AT 공통): ICW1~4, OCW2 (EOI · 정한 EOI), OCW3 (IRR / ISR 읽기), 마스크 (IMR), 우선순위 = 낮은 번호.
/// 종 연결 · 벡터 넣기는 기계 쪽에서.
/// </summary>
internal sealed class Pic8259
{
    public int Irr, Isr, Imr = 0xFF, Base;
    public int IcwStep;
    public bool NeedIcw4, ReadIsr, AutoEoi;
    /// <summary>특수 완전 중첩 (ICW4 bit4): 종 연결 입력 (<see cref="Cascade"/>)은 들어가 있어도 다시 받는다 — PC-98 BIOS 주 PIC = ICW4 1Dh.</summary>
    public bool Sfnm;
    /// <summary>주 PIC의 종 연결 입력 (ICW3).</summary>
    public int Cascade;

    public void WriteCmd(int v)
    {
        if ((v & 0x10) != 0) { IcwStep = 1; NeedIcw4 = (v & 1) != 0; Imr = 0; Isr = 0; Irr = 0; return; }   // ICW1
        if ((v & 0x18) == 0)   // OCW2
        {
            int cmd = v >> 5;
            if (cmd == 1) { for (int i = 0; i < 8; i++) if ((Isr & (1 << i)) != 0) { Isr &= ~(1 << i); break; } }   // 정하지 않은 EOI
            else if (cmd == 3) Isr &= ~(1 << (v & 7));   // 정한 EOI
            return;
        }
        if ((v & 0x18) == 0x08) { if ((v & 2) != 0) ReadIsr = (v & 1) != 0; }   // OCW3
    }

    public void WriteData(int v)
    {
        switch (IcwStep)
        {
            case 1: Base = v & 0xF8; IcwStep = 2; return;
            case 2: Cascade = v; IcwStep = NeedIcw4 ? 3 : 0; return;
            case 3: AutoEoi = (v & 2) != 0; Sfnm = (v & 0x10) != 0; IcwStep = 0; return;
        }
        Imr = v;
    }

    public int ReadCmd() { return ReadIsr ? Isr : Irr; }

    /// <summary>받을 IRQ (가장 높은 우선순위, 들어가 있는 것보다 높음), 없으면 −1.</summary>
    public int Pending()
    {
        int req = Irr & ~Imr;
        for (int i = 0; i < 8; i++)
        {
            if ((Isr & (1 << i)) != 0)
            {
                // SFNM: 종 연결 입력은 들어가 있어도 종의 새 요청을 받는다 (처리기가 종에만 EOI하는 드라이버 — Kuni's MDRV)
                if (Sfnm && (Cascade & (1 << i)) != 0 && (req & (1 << i)) != 0) return i;
                return -1;
            }
            if ((req & (1 << i)) != 0) return i;
        }
        return -1;
    }
}
