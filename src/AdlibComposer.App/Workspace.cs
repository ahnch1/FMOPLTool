using System;
using System.Collections.Generic;
using System.IO;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.App;

/// <summary>두 화면(작곡/악기 편집)이 공유하는 상태: 현재 곡, 악기 뱅크, 오디오 엔진, 실행취소 기록.</summary>
internal sealed class Workspace : IDisposable
{
    private readonly List<Song> undo = new List<Song>();
    private readonly List<Song> redo = new List<Song>();

    public Song Song { get; private set; } = CreateNewSong();
    public InstrumentBank Bank { get; private set; } = InstrumentBank.CreateNew();
    public FormatRegistry Formats { get; } = FormatRegistry.CreateDefault();
    public AudioEngine Audio { get; } = new AudioEngine();
    public bool SongDirty { get; set; }
    public bool BankDirty { get; set; }
    public AdlibInstrument CurrentInstrument { get; private set; }

    public event EventHandler SongReplaced;
    public event EventHandler BankReplaced;
    public event EventHandler CurrentInstrumentChanged;

    public Workspace()
    {
        CurrentInstrument = FirstUsed();
    }

    private AdlibInstrument FirstUsed()
    {
        foreach (var i in Bank.Instruments) if (i.IsUsed) return i;
        return null;
    }

    public AdlibInstrument Resolve(string name) { return Bank.Find(name); }

    public void SelectInstrument(AdlibInstrument ins)
    {
        CurrentInstrument = ins;
        if (CurrentInstrumentChanged != null) CurrentInstrumentChanged(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------ 실행취소

    public bool CanUndo { get { return undo.Count > 0; } }
    public bool CanRedo { get { return redo.Count > 0; } }

    public void PushUndo()
    {
        undo.Add(Song.Clone());
        if (undo.Count > 100) undo.RemoveAt(0);
        redo.Clear();
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        redo.Add(Song);
        Song = undo[undo.Count - 1];
        undo.RemoveAt(undo.Count - 1);
        SongDirty = true;
        RaiseSongReplaced();
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        undo.Add(Song);
        Song = redo[redo.Count - 1];
        redo.RemoveAt(redo.Count - 1);
        SongDirty = true;
        RaiseSongReplaced();
    }

    private void RaiseSongReplaced() { if (SongReplaced != null) SongReplaced(this, EventArgs.Empty); }

    // ------------------------------------------------------------ 곡 파일

    /// <summary>새 곡의 보이스별 기본 악기 (원본 Display → Instrument 기본값: 1~6 piano1, BD bdrum, SD snare1, TO tom1, CY cymbal1, HH hihat1).
    /// STANDARD.BNK에 "hihat"은 없고 "hihat1"이 있어 hihat1을 쓴다. 멜로딕 모드의 7~9도 piano1.</summary>
    public static readonly string[] DefaultInstruments =
        { "piano1", "piano1", "piano1", "piano1", "piano1", "piano1", "bdrum", "snare1", "tom1", "cymbal1", "hihat1" };

    public static string DefaultInstrumentFor(int voice, bool melodic)
    {
        if (voice >= DefaultInstruments.Length) return "piano1";   // SOP 보이스 12~20
        return melodic && voice < Song.MelodicVoices ? "piano1" : DefaultInstruments[voice];
    }

    private static Song CreateNewSong()
    {
        var s = new Song();
        for (int v = 0; v < Song.TotalVoices; v++)
            s.Voices[v].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = DefaultInstrumentFor(v, s.IsMelodic) });
        return s;
    }

    public void NewSong()
    {
        Song = CreateNewSong();
        undo.Clear(); redo.Clear();
        SongDirty = false;
        RaiseSongReplaced();
    }

    /// <summary>
    /// 곡 열기. SOP는 곡 안 악기로 새 작업 뱅크를 만든다 (이름이 같은 BNK 악기와 소리가 다를 수 있어 섞지 않는다).
    /// </summary>
    public void OpenSong(string path)
    {
        if (SopFormat.IsSop(path))
        {
            List<AdlibInstrument> instruments;
            var sop = SopFormat.Load(path);
            var s = SopConvert.ToSong(sop, out instruments);
            s.FilePath = path;
            s.Title = Path.GetFileNameWithoutExtension(path);
            var bank = new InstrumentBank();
            foreach (var ins in instruments) bank.Add(ins);
            Bank = bank;
            BankDirty = false;
            CurrentInstrument = FirstUsed();
            if (BankReplaced != null) BankReplaced(this, EventArgs.Empty);
            Song = s;
            undo.Clear(); redo.Clear();
            SongDirty = false;
            RaiseSongReplaced();
            return;
        }
        var fmt = Formats.FindSong(path);
        if (fmt == null || !fmt.CanRead) throw new InvalidDataException("읽을 수 없는 곡 형식입니다: " + Path.GetExtension(path));
        Song song;
        using (var fs = File.OpenRead(path)) song = fmt.Read(fs);
        song.FilePath = path;
        song.Title = Path.GetFileNameWithoutExtension(path);
        Song = song;
        undo.Clear(); redo.Clear();
        SongDirty = false;
        RaiseSongReplaced();
    }

    /// <summary>지금 곡 + 뱅크 → SOP (재생 · 저장).</summary>
    public SopSong BuildSop() { return SopConvert.FromSong(Song, Bank.Find); }

    public void SaveSong(string path)
    {
        if (SopFormat.IsSop(path))
        {
            // ROL/IMS 모드 곡을 SOP로 저장하면 SOP 모드로 바꾼다 (새로 보이는 보이스 12~20에 악기가 없으면 기본 악기)
            var before = Song.Clone();
            if (!Song.IsSop)
            {
                for (int v = Song.MaxVoices; v < Song.TotalVoices; v++)
                    if (Song.Voices[v].Timbres.Count == 0)
                        Song.Voices[v].Timbres.Add(new TimbreEvent { Tick = 0, InstrumentName = DefaultInstrumentFor(v, Song.IsMelodic) });
                Song.IsSop = true;
            }
            Song.FilePath = path;
            try
            {
                var sop = BuildSop();
                var buf = new MemoryStream();
                SopFormat.Write(sop, buf);
                File.WriteAllBytes(path, buf.ToArray());
            }
            catch { Song = before; throw; }
            Song.Title = Path.GetFileNameWithoutExtension(path);
            SongDirty = false;
            RaiseSongReplaced();
            return;
        }
        if (Song.IsSop)
            throw new InvalidDataException("SOP 모드 곡은 .sop로만 저장할 수 있습니다.\nROL / IMS로 저장하려면 Options → With / Without Percussion으로 바꾸세요 (보이스 12~20 · 팬 · 4-op는 빠집니다).");
        var fmt = Formats.FindSong(path);
        if (fmt == null || !fmt.CanWrite) throw new InvalidDataException("저장할 수 없는 곡 형식입니다: " + Path.GetExtension(path));
        var ms = new MemoryStream();
        fmt.Write(Song, ms);
        File.WriteAllBytes(path, ms.ToArray());
        Song.FilePath = path;
        Song.Title = Path.GetFileNameWithoutExtension(path);
        SongDirty = false;
        RaiseSongReplaced();
    }

    /// <summary>곡이 참조하지만 현재 뱅크에 없는 악기 이름들.</summary>
    public List<string> MissingInstruments()
    {
        var missing = new List<string>();
        for (int v = 0; v < Math.Max(Song.MaxVoices, Song.VoiceCount); v++)
            foreach (var t in Song.Voices[v].Timbres)
                if (Bank.Find(t.InstrumentName) == null && !missing.Contains(t.InstrumentName))
                    missing.Add(t.InstrumentName);
        return missing;
    }

    // ------------------------------------------------------------ 뱅크 / 악기 파일

    public void NewBank()
    {
        Bank = InstrumentBank.CreateNew();
        BankDirty = false;
        CurrentInstrument = FirstUsed();
        if (BankReplaced != null) BankReplaced(this, EventArgs.Empty);
    }

    public void OpenBank(string path)
    {
        var fmt = Formats.FindBank(path);
        if (fmt == null) throw new InvalidDataException("읽을 수 없는 뱅크 형식입니다.");
        InstrumentBank bank;
        using (var fs = File.OpenRead(path)) bank = fmt.Read(fs);
        bank.FilePath = path;
        Bank = bank;
        BankDirty = false;
        CurrentInstrument = FirstUsed();
        if (BankReplaced != null) BankReplaced(this, EventArgs.Empty);
    }

    /// <summary>
    /// 다른 뱅크의 악기를 복사해 현재 뱅크에 넣는다 (뱅크 브라우저).
    /// replace = true면 같은 이름의 악기를 덮어쓰고, false면 새 이름(NAME2 ...)으로 추가한다. 넣은 개수를 돌려준다.
    /// </summary>
    public int AddInstruments(IEnumerable<AdlibInstrument> instruments, bool replace)
    {
        int n = 0;
        AdlibInstrument last = null;
        foreach (var src in instruments)
        {
            var c = src.Clone();
            c.IsUsed = true;
            var existing = Bank.Find(c.Name);
            if (existing != null && replace)
            {
                Bank.Instruments[Bank.Instruments.IndexOf(existing)] = c;
                if (ReferenceEquals(CurrentInstrument, existing)) CurrentInstrument = c;
            }
            else Bank.Add(c);
            last = c;
            n++;
        }
        if (n == 0) return 0;
        BankDirty = true;
        CurrentInstrument = last;
        if (BankReplaced != null) BankReplaced(this, EventArgs.Empty);
        return n;
    }

    public void SaveBank(string path)
    {
        var fmt = Formats.FindBank(path);
        if (fmt == null || !fmt.CanWrite) throw new InvalidDataException("저장할 수 없는 뱅크 형식입니다.");
        var ms = new MemoryStream();
        fmt.Write(Bank, ms);
        File.WriteAllBytes(path, ms.ToArray());
        Bank.FilePath = path;
        BankDirty = false;
        if (BankReplaced != null) BankReplaced(this, EventArgs.Empty);
    }

    public AdlibInstrument ImportInstrument(string path)
    {
        var fmt = Formats.FindInstrument(path);
        if (fmt == null) throw new InvalidDataException("읽을 수 없는 악기 형식입니다.");
        AdlibInstrument ins;
        using (var fs = File.OpenRead(path)) ins = fmt.Read(fs, Path.GetFileNameWithoutExtension(path));
        Bank.Add(ins);
        BankDirty = true;
        SelectInstrument(ins);
        return ins;
    }

    public void ExportInstrument(string path, AdlibInstrument ins)
    {
        var fmt = Formats.FindInstrument(path);
        if (fmt == null) throw new InvalidDataException("저장할 수 없는 악기 형식입니다.");
        var ms = new MemoryStream();
        fmt.Write(ins, ms);
        File.WriteAllBytes(path, ms.ToArray());
    }

    public void Dispose() { Audio.Dispose(); }
}
