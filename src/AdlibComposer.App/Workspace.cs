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

    public Song Song { get; private set; } = new Song();
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

    public void NewSong()
    {
        Song = new Song();
        undo.Clear(); redo.Clear();
        SongDirty = false;
        RaiseSongReplaced();
    }

    public void OpenSong(string path)
    {
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

    public void SaveSong(string path)
    {
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
        for (int v = 0; v < Song.MaxVoices; v++)
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
