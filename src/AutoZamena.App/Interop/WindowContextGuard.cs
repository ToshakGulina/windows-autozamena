using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using AutoZamena.App.Config;

namespace AutoZamena.App.Interop;

public enum ContextStatus
{
    Allowed = 0,
    FilteredApp = 1,
    FullscreenGame = 2,
    PasswordField = 3,
    OwnSettingsWindow = 4
}

/// <summary>
/// Evaluates whether autocorrect is permitted in the currently focused window/control
/// without ever blocking the low-level keyboard hook thread.
/// </summary>
public sealed class WindowContextGuard : IDisposable
{
    private readonly int _ownPid = Environment.ProcessId;
    private readonly ConcurrentDictionary<uint, (string Name, long CachedTick)> _pidNameCache = new();
    private readonly AutoResetEvent _uiaProbeSignal = new(false);
    private readonly Thread _uiaThread;

    private volatile bool _disposed;
    private volatile bool _isPasswordFocused;

    private IntPtr _lastTopHwnd;
    private IntPtr _lastFocusHwnd;
    private string _lastProcessName = string.Empty;
    private long _lastFullscreenCheckTick;
    private bool _lastIsFullscreen;

    public Func<bool>? IsOwnSandboxFocused { get; set; }

    public WindowContextGuard()
    {
        _uiaThread = new Thread(UiaPasswordProbeLoop)
        {
            IsBackground = true,
            Name = "AutoZamena-UIA-PasswordGuard"
        };
        _uiaThread.SetApartmentState(ApartmentState.STA);
        _uiaThread.Start();
    }

    public string LastActiveProcessName => _lastProcessName;

    public void NotifyPotentialFocusChange()
    {
        if (!_disposed)
        {
            _uiaProbeSignal.Set();
        }
    }

    public (ContextStatus Status, string ProcessName, bool FocusChanged) Evaluate(AppConfig config)
    {
        (IntPtr focusHwnd, IntPtr topHwnd, _, uint pid) = NativeMethods.GetActiveFocusAndLayout();
        if (topHwnd == IntPtr.Zero || pid == 0)
        {
            return (ContextStatus.FilteredApp, string.Empty, false);
        }

        bool focusChanged = topHwnd != _lastTopHwnd || focusHwnd != _lastFocusHwnd;
        if (focusChanged)
        {
            _lastTopHwnd = topHwnd;
            _lastFocusHwnd = focusHwnd;
            _isPasswordFocused = false;
            NotifyPotentialFocusChange();
        }

        // Own process check: allow only when typing inside the interactive Sandbox textbox.
        if ((int)pid == _ownPid)
        {
            _lastProcessName = "AutoZamena";
            bool inSandbox = IsOwnSandboxFocused?.Invoke() == true;
            return (inSandbox ? ContextStatus.Allowed : ContextStatus.OwnSettingsWindow, "AutoZamena", focusChanged);
        }

        string procName = ResolveProcessName(pid);
        _lastProcessName = procName;

        // 1. Check Blacklist / Whitelist
        if (config.FilterMode == WindowFilterMode.Blacklist)
        {
            if (MatchesProcessList(procName, config.BlacklistedProcesses))
            {
                return (ContextStatus.FilteredApp, procName, focusChanged);
            }
        }
        else
        {
            if (!MatchesProcessList(procName, config.WhitelistedProcesses))
            {
                return (ContextStatus.FilteredApp, procName, focusChanged);
            }
        }

        // 2. Check Fullscreen 3D Game / Exclusive Fullscreen
        if (config.AutoDisableInFullscreenGames)
        {
            long now = Environment.TickCount64;
            if (focusChanged || (now - _lastFullscreenCheckTick) > 1500)
            {
                _lastFullscreenCheckTick = now;
                _lastIsFullscreen = IsFullscreenGameWindow(topHwnd, procName);
            }

            if (_lastIsFullscreen)
            {
                return (ContextStatus.FullscreenGame, procName, focusChanged);
            }
        }

        // 3. Check Password field cached from non-blocking STA UIA probe
        if (config.AutoDisableInPasswordFields && _isPasswordFocused)
        {
            return (ContextStatus.PasswordField, procName, focusChanged);
        }

        return (ContextStatus.Allowed, procName, focusChanged);
    }

    private string ResolveProcessName(uint pid)
    {
        long now = Environment.TickCount64;
        if (_pidNameCache.TryGetValue(pid, out var cached) && (now - cached.CachedTick) < 30_000)
        {
            return cached.Name;
        }

        string name = string.Empty;
        try
        {
            using Process proc = Process.GetProcessById((int)pid);
            name = proc.ProcessName ?? string.Empty;
        }
        catch
        {
            name = string.Empty;
        }

        if (_pidNameCache.Count > 512)
        {
            _pidNameCache.Clear();
        }
        _pidNameCache[pid] = (name, now);
        return name;
    }

    private static bool MatchesProcessList(string procName, System.Collections.Generic.List<string> list)
    {
        if (string.IsNullOrEmpty(procName) || list is null)
        {
            return false;
        }

        string cleanProc = StripExeSuffix(procName);
        for (int i = 0; i < list.Count; i++)
        {
            string entry = StripExeSuffix(list[i].Trim());
            if (string.Equals(cleanProc, entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string StripExeSuffix(string s) =>
        s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(s)
            : s;

    private static bool IsFullscreenGameWindow(IntPtr topHwnd, string procName)
    {
        // Never treat Windows shell or standard maximized browsers as fullscreen 3D games unless F11 borderless
        if (string.Equals(procName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out int state) == 0 &&
                state == NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN)
            {
                return true;
            }

            // Also check borderless fullscreen window covering entire monitor without caption/thickframe
            // (Skip known browsers/messengers where F11 full-screen web typing is still desired)
            if (IsBrowserOrMessenger(procName))
            {
                return false;
            }

            uint style = NativeMethods.GetWindowLongW(topHwnd, NativeMethods.GWL_STYLE);
            // Custom-chromed desktop apps (Electron, WinUI, Chromium) retain WS_THICKFRAME for resize/Aero Snap,
            // whereas borderless fullscreen games use a frameless WS_POPUP window without caption or thickframe.
            if ((style & (NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME)) != 0)
            {
                return false;
            }

            if (!NativeMethods.GetWindowRect(topHwnd, out NativeMethods.RECT winRect))
            {
                return false;
            }

            IntPtr hMon = NativeMethods.MonitorFromWindow(topHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (hMon == IntPtr.Zero)
            {
                return false;
            }

            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfoW(hMon, ref mi))
            {
                return false;
            }

            return winRect.Left <= mi.rcMonitor.Left &&
                   winRect.Top <= mi.rcMonitor.Top &&
                   winRect.Right >= mi.rcMonitor.Right &&
                   winRect.Bottom >= mi.rcMonitor.Bottom;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBrowserOrMessenger(string procName)
    {
        return procName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("browser", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("Telegram", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("Discord", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("Slack", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("Teams", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("ms-teams", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("Obsidian", StringComparison.OrdinalIgnoreCase) ||
               procName.Equals("Notion", StringComparison.OrdinalIgnoreCase);
    }

    private void UiaPasswordProbeLoop()
    {
        while (!_disposed)
        {
            _uiaProbeSignal.WaitOne(1200);
            if (_disposed)
            {
                break;
            }

            try
            {
                AutomationElement? focused = AutomationElement.FocusedElement;
                if (focused is not null)
                {
                    object? val = focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, ignoreDefaultValue: false);
                    _isPasswordFocused = val is true;
                }
                else
                {
                    _isPasswordFocused = false;
                }
            }
            catch
            {
                _isPasswordFocused = false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _uiaProbeSignal.Set();
    }
}
