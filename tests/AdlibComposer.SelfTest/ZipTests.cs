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
        }
        finally
        {
            ZipFolder.CacheBase = savedBase;
            try { Directory.Delete(work, true); } catch (Exception) { }
        }
    }

    private static void Add(ZipArchive z, string name, byte[] data)
    {
        var e = z.CreateEntry(name);
        using (var s = e.Open()) s.Write(data, 0, data.Length);
    }
}
