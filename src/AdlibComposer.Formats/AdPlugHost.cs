using System;
using System.Globalization;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// AdPlug 방식 OPL 재생기 틀: 재생기는 레지스터 쓰기 (<see cref="IOplOut.Write"/>, 칩 = OPL3 뱅크 0 / 1)를 하고, 불러온 뒤 1/<see cref="OplPlayer.Refresh"/>초마다
/// <see cref="OplPlayer.Update"/>가 불린다 (false = 곡 끝 / 되풀이 시작). 이 틀이 그것을 VGM (OPL2 / OPL3)으로 모으거나,
/// AdPlug 시험 (test/playertest.cpp)과 같은 글 기록 ("reg &lt;- val", "setchip(n)", update마다 "r주기")으로 낸다 — AdPlug testref와 줄 단위로 비교할 수 있다.
/// </summary>
public interface IOplOut
{
    void Write(int reg, int val);
    void SetChip(int n);
    int Chip { get; }
    /// <summary>OPL 초기화 (AdPlug Copl::init — 기록에는 "init" 줄).</summary>
    void Init();
}

public abstract class OplPlayer
{
    protected IOplOut Opl;

    /// <summary>재생기를 쓰기 대상에 붙인다 (불러오기 · 처음으로 가기가 쓰는 레지스터도 여기로).</summary>
    public void Attach(IOplOut opl) { Opl = opl; }

    /// <summary>곡 데이터를 읽고 처음으로 간다 (AdPlug load와 같이 rewind까지).</summary>
    public abstract void Load(byte[] data);
    public abstract bool Update();
    public abstract void Rewind(int subsong);
    /// <summary>update 주기 (Hz).</summary>
    public abstract float Refresh { get; }
    public virtual string Title { get { return ""; } }
    public virtual string Author { get { return ""; } }
    public virtual string Description { get { return ""; } }
    public abstract string TypeName { get; }
}

public static class AdPlugHost
{
    /// <summary>OPL3 레지스터 0x000~0x1FF를 칩 0 / 1로 나눠 쓴다 (AdPlug rad2.cpp writeOPL과 같이: 칩이 바뀔 때만 setchip).</summary>
    public static void Write3(IOplOut opl, int reg, int val)
    {
        int chip = reg >> 8;
        if (opl.Chip != chip) opl.SetChip(chip);
        opl.Write(reg & 0xFF, val & 0xFF);
    }

    private sealed class LogOut : IOplOut
    {
        public readonly AdlibStreamFormats.OplLog Log = new AdlibStreamFormats.OplLog();
        public int Chip { get; private set; }
        public void SetChip(int n) { if (n >= 0 && n < 2) { Chip = n; Log.Chip = n; } }
        public void Write(int reg, int val) { Log.Write(reg, val); }
        public void Init() { }
    }

    private sealed class TextOut : IOplOut
    {
        public readonly StringBuilder S = new StringBuilder();
        public int Chip { get; private set; }
        public void SetChip(int n) { if (n >= 0 && n < 2) Chip = n; S.Append("setchip(").Append(n).Append(")\n"); }
        public void Write(int reg, int val) { S.Append(reg.ToString("x")).Append(" <- ").Append(val.ToString("x")).Append('\n'); }
        public void Init() { S.Append("init\n"); }
    }

    /// <summary>곡 끝 (Update = false)까지 또는 maxSeconds까지 돌려 VGM으로.</summary>
    public static VgmSong Render(OplPlayer p, byte[] data, string title, string source, string system = "PC (AdLib)", double maxSeconds = AdlibStreamFormats.MaxSeconds)
    {
        var o = new LogOut();
        p.Attach(o);
        p.Load(data);
        double t = 0;
        bool more = true;
        while (more && t < maxSeconds)
        {
            o.Log.Now = t;
            more = p.Update();
            float hz = p.Refresh;
            t += 1.0 / (hz > 0 ? hz : 50);
        }
        o.Log.Now = t;
        string name = p.Title.Length > 0 ? p.Title : title;
        var v = AdlibStreamFormats.OplLogToVgm(o.Log, AdlibStreamFormats.OplHw.Unknown, name, source, system);
        v.TitleEn = name;
        if (p.Author.Length > 0) v.AuthorEn = p.Author;
        if (p.Description.Length > 0) v.Notes = p.Description;
        if (more) v.FadeSeconds = 5;   // 끝을 못 만난 곡: 끝에서 줄여 끝냄
        return v;
    }

    /// <summary>AdPlug playertest와 같은 글 기록 (testref/*.ref와 비교용). maxUpdates = 끝나지 않는 곡 막음.</summary>
    public static string Trace(OplPlayer p, byte[] data, int maxUpdates = 2000000)
    {
        var o = new TextOut();
        p.Attach(o);
        p.Load(data);
        for (int i = 0; i < maxUpdates && p.Update(); i++)
            o.S.Append('r').Append(p.Refresh.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');
        return o.S.ToString();
    }
}
