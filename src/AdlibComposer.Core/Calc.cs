namespace AdlibComposer.Core;

/// <summary>net48/net8 공통으로 쓰는 작은 수학 도우미.</summary>
public static class Calc
{
    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}
