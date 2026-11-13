using System.Collections.Generic;
using System.Windows.Forms;

namespace AdlibComposer.App.Controls;

/// <summary>
/// 컴퓨터 키보드 → 건반 배치 (Composer와 Instrument Maker 공통).
/// 아랫줄 Z S X D C V G B H N J M , = 기준 옥타브, 윗줄 Q 2 W 3 E R 5 T 6 Y 7 U I = 한 옥타브 위.
/// </summary>
internal static class ComputerKeyboard
{
    private static readonly Dictionary<Keys, int> map = Build();

    private static Dictionary<Keys, int> Build()
    {
        var m = new Dictionary<Keys, int>();
        Keys[] low = { Keys.Z, Keys.S, Keys.X, Keys.D, Keys.C, Keys.V, Keys.G, Keys.B, Keys.H, Keys.N, Keys.J, Keys.M, Keys.Oemcomma };
        for (int i = 0; i < low.Length; i++) m[low[i]] = i;
        Keys[] high = { Keys.Q, Keys.D2, Keys.W, Keys.D3, Keys.E, Keys.R, Keys.D5, Keys.T, Keys.D6, Keys.Y, Keys.D7, Keys.U, Keys.I };
        for (int i = 0; i < high.Length; i++) m[high[i]] = 12 + i;
        return m;
    }

    /// <summary>키 → 기준 C로부터의 반음 수 (0..24).</summary>
    public static IReadOnlyDictionary<Keys, int> Map { get { return map; } }

    public static bool TryGetOffset(Keys key, out int offset) { return map.TryGetValue(key, out offset); }

    public static string Label(Keys k)
    {
        if (k == Keys.Oemcomma) return ",";
        if (k >= Keys.D0 && k <= Keys.D9) return ((int)k - (int)Keys.D0).ToString();
        return k.ToString();
    }

    /// <summary>키보드가 덮는 범위 (기준 C부터 2옥타브 + 1음).</summary>
    public const int Span = 24;
}

internal static class MenuStripExtensions
{
    /// <summary>메뉴가 열려 있으면 true (열린 메뉴에서는 화살표 키를 메뉴가 쓰도록).</summary>
    public static bool IsDropDownOpen(this MenuStrip menu)
    {
        foreach (ToolStripItem it in menu.Items)
        {
            var mi = it as ToolStripMenuItem;
            if (mi != null && mi.DropDown.Visible) return true;
        }
        return false;
    }
}
