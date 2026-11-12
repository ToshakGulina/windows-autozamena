using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AutoZamena.App.UI;

namespace AutoZamena.App;

internal static class Program
{
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
            MessageBox.Show(
                "AutoZamena уже запущена и работает в системном трее (рядом с часами).",
                "AutoZamena",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool startMinimized = args.Any(a =>
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-m", StringComparison.OrdinalIgnoreCase));

        using var context = new TrayApplicationContext(startMinimized);
        Application.Run(context);
    }
}
