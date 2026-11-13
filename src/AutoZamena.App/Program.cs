using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AutoZamena.App.UI;

namespace AutoZamena.App;

internal static class Program
{
    internal const string ActivateWindowEventName = @"Local\AutoZamena_ActivateWindow_Event";

    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\AutoZamena_SingleInstance_Mutex",
            createdNew: out bool isFirstInstance
        );

        if (!isFirstInstance)
        {
            // Bring the running instance's settings window to the front
            try
            {
                using EventWaitHandle activateSignal = EventWaitHandle.OpenExisting(ActivateWindowEventName);
                activateSignal.Set();
            }
            catch
            {
                MessageBox.Show(
                    "AutoZamena уже запущена в области уведомлений.",
                    "AutoZamena",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool startMinimized = args.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase));

        using var activateEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ActivateWindowEventName
        );

        using var context = new TrayApplicationContext(startMinimized, activateEvent);
        Application.Run(context);
    }
}
