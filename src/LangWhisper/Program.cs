using System.Threading;
using System.Windows.Forms;
using LangWhisper.Diagnostics;
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

        Log.Write($"--- LangWhisper starting (pid {Environment.ProcessId}) ---");
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
        Log.Write("--- LangWhisper exited ---");

        GC.KeepAlive(mutex);
    }
}
