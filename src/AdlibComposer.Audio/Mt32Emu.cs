using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AdlibComposer.Audio;

/// <summary>
/// Roland MT-32 / CM-32L 에뮬레이터: Munt libmt32emu (LGPL-2.1+, https://github.com/munt/munt)의 C API를 부른다.
/// 실행 폴더의 <c>mt32emu.dll</c>(src/AdlibComposer.Mt32에서 빌드)과 사용자의 ROM 파일(컨트롤 + PCM)이 있어야 한다.
/// 출력은 칩 본래 주기(MT-32 = 32000Hz) 스테레오. 엔진이 <see cref="Rate"/>에서 44.1kHz로 옮긴다.
/// 이벤트는 <see cref="Render"/> 사이에 바로 넣고, 16프레임씩 미리 만들어 둔다 (타이밍 오차 0.5ms 이하).
/// </summary>
public sealed class Mt32Emu : IDisposable
{
    private const string Dll = "mt32emu.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mt32emu_get_library_version_string();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mt32emu_create_context(IntPtr reportHandler, IntPtr instanceData);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void mt32emu_free_context(IntPtr ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int mt32emu_add_rom_data(IntPtr ctx, byte[] data, UIntPtr size, IntPtr sha1);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int mt32emu_merge_and_add_rom_data(IntPtr ctx, byte[] d1, UIntPtr s1, IntPtr h1, byte[] d2, UIntPtr s2, IntPtr h2);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int mt32emu_open_synth(IntPtr ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void mt32emu_close_synth(IntPtr ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern uint mt32emu_get_actual_stereo_output_samplerate(IntPtr ctx);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void mt32emu_play_msg_now(IntPtr ctx, uint msg);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void mt32emu_play_sysex_now(IntPtr ctx, byte[] sysex, uint len);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void mt32emu_render_bit16s(IntPtr ctx, short[] stream, uint frames);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void mt32emu_set_output_gain(IntPtr ctx, float gain);

    private const int RcAddedControl = 1, RcAddedPcm = 2, RcAddedPartialControl = 3, RcAddedPartialPcm = 4;
    private const int Block = 16;

    private IntPtr ctx;
    private readonly short[] buf = new short[Block * 2];
    private int pos = Block;

    /// <summary>열린 신디사이저의 출력 주기 (Hz).</summary>
    public int Rate { get; private set; } = 32000;
    /// <summary>찾은 ROM 설명 (예: "MT32_CONTROL.ROM + MT32_PCM.ROM").</summary>
    public string RomDescription { get; private set; } = "";
    public bool IsOpen { get { return ctx != IntPtr.Zero; } }

    /// <summary>mt32emu.dll을 불러올 수 있는지 (버전 문자열, 없으면 null).</summary>
    public static string LibraryVersion()
    {
        try { return Marshal.PtrToStringAnsi(mt32emu_get_library_version_string()); }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (BadImageFormatException) { return null; }
    }

    /// <summary>
    /// ROM 폴더의 파일을 모두 넣어 보고(SHA-1로 알아본다) 신디사이저를 연다. 반으로 나뉜 ROM(CM-32L 등)은 짝을 맞춰 합친다.
    /// 실패하면 이유를 담은 예외 (DLL 없음 / ROM 없음).
    /// </summary>
    public static Mt32Emu Open(string romFolder)
    {
        if (LibraryVersion() == null) throw new InvalidOperationException("mt32emu.dll을 찾을 수 없습니다 (실행 폴더).");
        if (string.IsNullOrEmpty(romFolder) || !Directory.Exists(romFolder)) throw new InvalidOperationException("MT-32 ROM 폴더가 없습니다: " + romFolder);
        var e = new Mt32Emu { ctx = mt32emu_create_context(IntPtr.Zero, IntPtr.Zero) };
        try
        {
            string control = null, pcm = null;
            var partials = new List<(string name, byte[] data)>();
            foreach (var f in Directory.GetFiles(romFolder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var len = new FileInfo(f).Length;
                if (len < 16 * 1024 || len > 2 * 1024 * 1024) continue;
                var data = File.ReadAllBytes(f);
                int rc = mt32emu_add_rom_data(e.ctx, data, (UIntPtr)data.Length, IntPtr.Zero);
                if (rc == RcAddedControl && control == null) control = Path.GetFileName(f);
                else if (rc == RcAddedPcm && pcm == null) pcm = Path.GetFileName(f);
                else if (rc == RcAddedPartialControl || rc == RcAddedPartialPcm) partials.Add((Path.GetFileName(f), data));
            }
            // 나뉜 ROM: 둘씩 합쳐 본다
            for (int i = 0; i < partials.Count && (control == null || pcm == null); i++)
                for (int j = i + 1; j < partials.Count; j++)
                {
                    int rc = mt32emu_merge_and_add_rom_data(e.ctx, partials[i].data, (UIntPtr)partials[i].data.Length, IntPtr.Zero, partials[j].data, (UIntPtr)partials[j].data.Length, IntPtr.Zero);
                    string both = partials[i].name + "+" + partials[j].name;
                    if (rc == RcAddedControl && control == null) { control = both; break; }
                    if (rc == RcAddedPcm && pcm == null) { pcm = both; break; }
                }
            if (control == null || pcm == null)
                throw new InvalidOperationException("MT-32 ROM을 찾지 못했습니다 (" + (control == null ? "컨트롤 ROM" : "") + (control == null && pcm == null ? ", " : "") + (pcm == null ? "PCM ROM" : "") + " 없음): " + romFolder);
            int open = mt32emu_open_synth(e.ctx);
            if (open != 0) throw new InvalidOperationException("MT-32 에뮬레이터를 열 수 없습니다 (코드 " + open + ").");
            e.Rate = (int)mt32emu_get_actual_stereo_output_samplerate(e.ctx);
            e.RomDescription = control + " + " + pcm;
            return e;
        }
        catch
        {
            e.Dispose();
            throw;
        }
    }

    /// <summary>짧은 MIDI 메시지 (상태 | d1 &lt;&lt; 8 | d2 &lt;&lt; 16).</summary>
    public void Send(int status, int d1, int d2)
    {
        if (ctx == IntPtr.Zero) return;
        mt32emu_play_msg_now(ctx, (uint)((status & 0xFF) | ((d1 & 0x7F) << 8) | ((d2 & 0x7F) << 16)));
    }

    /// <summary>SysEx (F0 … F7 전체).</summary>
    public void SendSysEx(byte[] data)
    {
        if (ctx == IntPtr.Zero || data == null || data.Length < 2) return;
        mt32emu_play_sysex_now(ctx, data, (uint)data.Length);
    }

    /// <summary>프레임 1개 (16bit 범위).</summary>
    public void Render(out int left, out int right)
    {
        if (ctx == IntPtr.Zero) { left = right = 0; return; }
        if (pos >= Block)
        {
            mt32emu_render_bit16s(ctx, buf, Block);
            pos = 0;
        }
        left = buf[2 * pos];
        right = buf[2 * pos + 1];
        pos++;
    }

    /// <summary>모든 채널 음 끄기 + 리셋 (GS/GM 리셋 대신 MT-32 방식: 채널마다 CC 121 · 123).</summary>
    public void AllOff()
    {
        for (int c = 0; c < 16; c++) { Send(0xB0 | c, 64, 0); Send(0xB0 | c, 123, 0); Send(0xB0 | c, 121, 0); }
    }

    /// <summary>신디사이저를 닫았다 다시 연다 (ROM은 그대로): 사용자 음색 · 리버브 · 파트 설정을 전원을 켠 상태로.</summary>
    public void Reset()
    {
        if (ctx == IntPtr.Zero) return;
        mt32emu_close_synth(ctx);
        mt32emu_open_synth(ctx);
        pos = Block;
    }

    public void Dispose()
    {
        if (ctx == IntPtr.Zero) return;
        try { mt32emu_close_synth(ctx); mt32emu_free_context(ctx); } catch (Exception) { }
        ctx = IntPtr.Zero;
    }
}
