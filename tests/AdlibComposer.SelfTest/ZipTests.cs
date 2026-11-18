using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>ZIP을 폴더처럼 (ZipFolder): 풀기 · 이름(한글 CP949) · zip slip 막기 · 다시 쓰기 · 안의 곡 · 가사 · 뱅크.</summary>
public static class ZipTests
{
    public static void Run(Action<string, bool, string> check)
    {
        string work = Path.Combine(Path.GetTempPath(), "fmopl_zip_test");
        string savedBase = ZipFolder.CacheBase;
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            ZipFolder.CacheBase = Path.Combine(work, "cache");
            string samples = SopTests.FindDir("samples");
            byte[] rol = samples != null && File.Exists(Path.Combine(samples, "demo.rol")) ? File.ReadAllBytes(Path.Combine(samples, "demo.rol")) : null;
            byte[] bnk = samples != null && File.Exists(Path.Combine(samples, "demo.bnk")) ? File.ReadAllBytes(Path.Combine(samples, "demo.bnk")) : null;
            if (rol == null || bnk == null) { Console.WriteLine("SKIP  zip (samples/demo.rol · demo.bnk not found)"); return; }

            var w = new VgmWriter();
            w.SetClock(VgmWriter.ClockAy8910, 1789772.5);
            w.Write(0xA0, 8, 15); w.Write(0xA0, 0, 0xFE); w.Write(0xA0, 7, 0x3E);
            w.Wait(44100);
            byte[] vgm = w.ToArray();

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cp949 = Encoding.GetEncoding(949);
            string zipPath = Path.Combine(work, "모음.zip");
            // 옛 zip처럼 이름을 완성형으로 (UTF-8 깃발 없음)
            using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create, cp949))
            {
                Add(z, "SONG.ROL", rol);
                Add(z, "SONG.BNK", bnk);
                Add(z, "SONG.ISS", new byte[16]);
                Add(z, "노래.ROL", rol);
                z.CreateEntry("sub/");
                Add(z, "sub/chip.vgm", vgm);
                Add(z, "../evil.txt", Encoding.ASCII.GetBytes("x"));
                Add(z, "C:/abs.txt", Encoding.ASCII.GetBytes("x"));
                Add(z, "sub/../../evil2.txt", Encoding.ASCII.GetBytes("x"));
                // zip 안의 zip
                var inner = new MemoryStream();
                using (var iz = new ZipArchive(inner, ZipArchiveMode.Create, true)) Add(iz, "INNER.ROL", rol);
                Add(z, "inner.zip", inner.ToArray());
            }

            var sw = Stopwatch.StartNew();
            string dir = ZipFolder.Extract(zipPath);
            double first = sw.Elapsed.TotalMilliseconds;
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)).Where(f => !f.StartsWith(".fmopl")).OrderBy(f => f).ToList();
            check("Zip: extracts songs, bank, lyrics, subfolder", files.Contains("SONG.ROL") && files.Contains("SONG.BNK") && files.Contains("SONG.ISS") && files.Contains(Path.Combine("sub", "chip.vgm")),
                string.Join(", ", files));
            check("Zip: CP949 (no UTF-8 flag) Korean names decoded", files.Contains("노래.ROL"), string.Join(", ", files));
            bool escaped = File.Exists(Path.Combine(work, "evil.txt")) || File.Exists(Path.Combine(work, "cache", "evil.txt")) || File.Exists(@"C:\abs.txt") || File.Exists(Path.Combine(work, "evil2.txt"));
            check("Zip: zip-slip names stay inside the cache folder", !escaped && Directory.GetFiles(dir, "evil*.txt", SearchOption.AllDirectories).All(f => f.StartsWith(dir)), "");
            check("Zip: cache path recognized", ZipFolder.IsCachePath(Path.Combine(dir, "SONG.ROL")) && !ZipFolder.IsCachePath(zipPath), "");

            sw.Restart();
            string again = ZipFolder.Extract(zipPath);
            double second = sw.Elapsed.TotalMilliseconds;
            check("Zip: second open reuses the extracted folder", again == dir && second < first + 50, first.ToString("0.0") + " ms → " + second.ToString("0.0") + " ms");

            // 안의 곡을 경로로 읽기 (Jukebox · Composer가 하는 것)
            Song s; InstrumentBank b;
            using (var fs = File.OpenRead(Path.Combine(dir, "SONG.ROL"))) s = new RolFormat().Read(fs);
            using (var fs = File.OpenRead(Path.Combine(dir, "SONG.BNK"))) b = new BnkFormat().Read(fs);
            check("Zip: ROL + same-name BNK from inside load", s.LengthTicks > 0 && b.Instruments.Count > 0, s.LengthTicks + " ticks, " + b.Instruments.Count + " instruments");
            var v = ChipSongs.Load(Path.Combine(dir, "sub", "chip.vgm"));
            check("Zip: VGM in subfolder plays", v.HasPsg && Math.Abs(v.LengthSeconds - 1) < 0.01, v.ChipSummary);

            string innerDir = ZipFolder.Extract(Path.Combine(dir, "inner.zip"));
            check("Zip: zip inside zip opens", File.Exists(Path.Combine(innerDir, "INNER.ROL")), innerDir);

            // zip이 바뀌면 새로 푼다
            using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Update)) Add(z, "NEW.ROL", rol);
            File.SetLastWriteTimeUtc(zipPath, DateTime.UtcNow.AddMinutes(1));
            string changed = ZipFolder.Extract(zipPath);
            check("Zip: changed zip extracts again", changed != dir && File.Exists(Path.Combine(changed, "NEW.ROL")), "");

            check("Zip: entry name safety", ZipFolder.SafeRelative("../a/./b:c.rol") == Path.Combine("a", "b_c.rol") && ZipFolder.SafeRelative("/..") == "", ZipFolder.SafeRelative("../a/./b:c.rol"));

            DirectRead(work, rol, bnk, vgm, check);
        }
        finally
        {
            ZipFolder.CacheBase = savedBase;
            try { Directory.Delete(work, true); } catch (Exception) { }
        }
    }

    /// <summary>풀지 않고 zip 안을 읽기 (ZipPath): ROL · BNK · ISS · SOP · MIDI를 "zip\안 경로"로, 캐시 폴더는 만들지 않는다.</summary>
    private static void DirectRead(string work, byte[] rol, byte[] bnk, byte[] vgm, Action<string, bool, string> check)
    {
        // 작은 MIDI 파일 (형식 0, 노트 하나)
        byte[] mid =
        {
            0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 0, 96,
            0x4D, 0x54, 0x72, 0x6B, 0, 0, 0, 12, 0, 0x90, 60, 100, 96, 0x80, 60, 0, 0, 0xFF, 0x2F, 0,
        };
        var sop = SopConvert.FromSong(new RolFormat().Read(new MemoryStream(rol)), n => new BnkFormat().Read(new MemoryStream(bnk)).Find(n));
        var sopBytes = new MemoryStream();
        SopFormat.Write(sop, sopBytes);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string zipPath = Path.Combine(work, "바로.zip");
        using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create, Encoding.GetEncoding(949)))
        {
            Add(z, "가요/SONG.ROL", rol);
            Add(z, "가요/song.bnk", bnk);
            Add(z, "TUNE.MID", mid);
            Add(z, "TUNE2.SOP", sopBytes.ToArray());
        }
        int cachesBefore = Directory.Exists(ZipFolder.CacheBase) ? Directory.GetDirectories(ZipFolder.CacheBase).Length : 0;

        check("ZipPath: songs-only zip browses without extracting", ZipPath.CanBrowse(zipPath, f => !f.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)), "");
        check("ZipPath: zip with other formats is extracted instead", !ZipPath.CanBrowse(Path.Combine(work, "모음.zip"), f => true), "");
        check("ZipPath: split", ZipPath.TrySplit(Path.Combine(zipPath, "가요", "SONG.ROL"), out string zf, out string inner) && zf == zipPath && inner == Path.Combine("가요", "SONG.ROL"), inner ?? "");
        var top = ZipPath.GetFiles(zipPath).Select(Path.GetFileName).OrderBy(x => x).ToList();
        var dirs = ZipPath.GetDirectories(zipPath).Select(Path.GetFileName).ToList();
        check("ZipPath: lists files and folders (Korean names)", top.SequenceEqual(new[] { "TUNE.MID", "TUNE2.SOP" }) && dirs.SequenceEqual(new[] { "가요" }), string.Join(", ", top) + " | " + string.Join(", ", dirs));

        string rolPath = Path.Combine(zipPath, "가요", "SONG.ROL");
        Song s; InstrumentBank b;
        using (var fs = ZipPath.OpenRead(rolPath)) s = new RolFormat().Read(fs);
        string side = ZipPath.GetFiles(Path.GetDirectoryName(rolPath), ".bnk").FirstOrDefault();
        using (var fs = ZipPath.OpenRead(side)) b = new BnkFormat().Read(fs);
        check("ZipPath: ROL + same-name BNK read in place", s.LengthTicks > 0 && b.Instruments.Count > 0 && ZipPath.FileExists(rolPath) && !ZipPath.FileExists(rolPath + "x"), s.LengthTicks + " ticks, bank " + Path.GetFileName(side));
        var m = MidiFiles.Load(Path.Combine(zipPath, "TUNE.MID"));
        check("ZipPath: MIDI read in place", m.Events.Count > 0 && m.FilePath.EndsWith("TUNE.MID"), m.Events.Count + " events");
        var so = SopFormat.Load(Path.Combine(zipPath, "TUNE2.SOP"));
        check("ZipPath: SOP read in place", so.TrackCount > 0 && so.LengthTicks > 0, so.TrackCount + " tracks");
        check("ZipPath: in-zip path is not saved in place", ZipFolder.IsCachePath(rolPath), "");
        int cachesAfter = Directory.Exists(ZipFolder.CacheBase) ? Directory.GetDirectories(ZipFolder.CacheBase).Length : 0;
        check("ZipPath: no cache folder created", cachesAfter == cachesBefore, cachesBefore + " → " + cachesAfter);
    }

    private static void Add(ZipArchive z, string name, byte[] data)
    {
        var e = z.CreateEntry(name);
        using (var s = e.Open()) s.Write(data, 0, data.Length);
    }
}
