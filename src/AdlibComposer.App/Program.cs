using System;
using System.Windows.Forms;

namespace AdlibComposer.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AdlibComposer.Audio.Windows.WindowsAudio.Register();
        AdlibComposer.Audio.SystemDefinitions.OverridePath = System.IO.Path.Combine(AppSettings.ResourcePath, "systems.json");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
