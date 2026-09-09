using System.Threading;
using System.Windows.Forms;
using LangWhisper.Tray;

namespace LangWhisper;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\LangWhisper_SingleInstance_8f2c";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another copy is already running in the tray.
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());

        GC.KeepAlive(mutex);
    }
}
