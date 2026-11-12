using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AutoZamena.App.Config;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Models;

namespace AutoZamena.App.Interop;

/// <summary>
/// Global low-level keyboard + mouse hook (<c>WH_KEYBOARD_LL</c> &amp; <c>WH_MOUSE_LL</c>).
/// Captures millisecond keystroke cadence, overlap timings, global hotkeys, and dispatches atomic fixes.
/// </summary>
public sealed class LowLevelInputHook : IDisposable
{
    private readonly AutoCorrectEngine _engine;
    private readonly WindowContextGuard _contextGuard;
    private readonly object _engineLock = new();
    private readonly bool[] _vkDown = new bool[256];
    private readonly byte[] _keyStateBuf = new byte[256];
    private readonly StringBuilder _unicodeBuf = new(8);

    // Keep strong references to hook delegates so GC never collects them!
    private readonly NativeMethods.LowLevelProc _kbdProc;
    private readonly NativeMethods.LowLevelProc _mouseProc;

    private IntPtr _hKeyboardHook;
    private IntPtr _hMouseHook;
    private AppConfig _config;

    private long _lastKeyDownTick;
    private int _lastKeyDownVk;
    private int _heldPrintableKeysCount;
    private ContextStatus _lastReportedStatus = ContextStatus.Allowed;
    private string _lastReportedProc = string.Empty;

    public event Action? PauseToggledByHotkey;
    public event Action<CorrectionHistoryEntry>? CorrectionApplied;
    public event Action<ContextStatus, string>? ContextStatusChanged;

    /// <summary>
    /// Optional interceptor used by the Settings UI when recording a custom hotkey binding.
    /// </summary>
    public Func<int, bool, bool, bool, bool>? HotkeyRecorderInterceptor { get; set; }

    public LowLevelInputHook(AutoCorrectEngine engine, WindowContextGuard contextGuard, AppConfig config)
    {
        _engine = engine;
        _contextGuard = contextGuard;
        _config = config;
        _kbdProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        lock (_engineLock)
        {
            _engine.Settings = config.Engine;
        }
    }

    public void Start()
    {
        if (_hKeyboardHook != IntPtr.Zero)
        {
            return;
        }

        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule? curModule = curProcess.MainModule;
        IntPtr hMod = NativeMethods.GetModuleHandleW(curModule?.ModuleName);

        _hKeyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _kbdProc, hMod, 0);
        _hMouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);
    }

    public void Stop()
    {
        if (_hKeyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hKeyboardHook);
            _hKeyboardHook = IntPtr.Zero;
        }

        if (_hMouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hMouseHook);
            _hMouseHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN)
            {
                // Mouse click repositions caret -> clear active word buffer cleanly
                lock (_engineLock)
                {
                    _engine.ResetContext();
                }
                _contextGuard.NotifyPotentialFocusChange();
            }
        }

        return NativeMethods.CallNextHookEx(_hMouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        NativeMethods.KBDLLHOOKSTRUCT kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        // 1. Immediately pass through our own synthetic SendInput events
        if (kb.dwExtraInfo == NativeMethods.MagicExtraInfo)
        {
            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        int msg = (int)wParam;
        bool isKeyDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        bool isKeyUp = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
        int vk = (int)kb.vkCode;

        if (isKeyUp)
        {
            if ((uint)vk < 256 && _vkDown[vk])
            {
                _vkDown[vk] = false;
                if (!IsModifierVk(vk) && _heldPrintableKeysCount > 0)
                {
                    _heldPrintableKeysCount--;
                }
            }
            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        if (!isKeyDown)
        {
            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        // Track key-down overlap for rollover / fat-finger detection
        bool isRepeat = (uint)vk < 256 && _vkDown[vk];
        bool overlapped = !isRepeat && !IsModifierVk(vk) && _heldPrintableKeysCount > 0;
        if ((uint)vk < 256 && !_vkDown[vk])
        {
            _vkDown[vk] = true;
            if (!IsModifierVk(vk))
            {
                _heldPrintableKeysCount++;
            }
        }

        // Ignore pure modifier presses themselves
        if (IsModifierVk(vk))
        {
            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        bool ctrl = IsVkDownNow(NativeMethods.VK_CONTROL) || IsVkDownNow(NativeMethods.VK_LCONTROL) || IsVkDownNow(NativeMethods.VK_RCONTROL);
        bool alt = IsVkDownNow(NativeMethods.VK_MENU) || IsVkDownNow(NativeMethods.VK_LMENU) || IsVkDownNow(NativeMethods.VK_RMENU);
        bool rAlt = IsVkDownNow(NativeMethods.VK_RMENU);
        bool shift = IsVkDownNow(NativeMethods.VK_SHIFT) || IsVkDownNow(NativeMethods.VK_LSHIFT) || IsVkDownNow(NativeMethods.VK_RSHIFT);
        bool win = IsVkDownNow(NativeMethods.VK_LWIN) || IsVkDownNow(NativeMethods.VK_RWIN);
        // On European/international layouts, AltGr is reported as Ctrl + RightAlt (VK_RMENU)
        bool isAltGr = ctrl && rAlt && !win;

        // 2. Settings UI Hotkey Recorder hook
        var recorder = HotkeyRecorderInterceptor;
        if (recorder is not null)
        {
            if (recorder(vk, ctrl, alt, shift))
            {
                return (IntPtr)1;
            }
        }

        AppConfig cfg = _config;

        // 3. Global Pause/Resume Hotkey (works everywhere so user is always in control)
        if (!win && cfg.PauseToggleHotkey.Matches(vk, ctrl, alt, shift))
        {
            cfg.Engine.Enabled = !cfg.Engine.Enabled;
            lock (_engineLock)
            {
                _engine.Settings.Enabled = cfg.Engine.Enabled;
                _engine.ResetContext();
            }
            PauseToggledByHotkey?.Invoke();
            return (IntPtr)1;
        }

        // 4. Evaluate active window context (Blacklist / Whitelist / Fullscreen game / Password field)
        (ContextStatus status, string procName, bool focusChanged) = _contextGuard.Evaluate(cfg);
        if (focusChanged)
        {
            lock (_engineLock)
            {
                _engine.ResetContext();
            }
        }

        if (status != _lastReportedStatus || !string.Equals(procName, _lastReportedProc, StringComparison.Ordinal))
        {
            _lastReportedStatus = status;
            _lastReportedProc = procName;
            ContextStatusChanged?.Invoke(status, procName);
        }

        if (status != ContextStatus.Allowed)
        {
            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        // 5. Explicit Layout-Convert Hotkey (e.g. Pause/Break -> converts last word RU <-> EN)
        if (!win && cfg.Engine.LayoutMode != LayoutSwitchMode.Off &&
            cfg.ConvertLayoutHotkey.Matches(vk, ctrl, alt, shift))
        {
            CorrectionResult hkRes;
            lock (_engineLock)
            {
                hkRes = _engine.ConvertLastWordLayoutByHotkey();
            }

            if (hkRes.HasChange)
            {
                AtomicTextInjector.Dispatch(hkRes);
                NotifyCorrection(hkRes, procName);
            }
            return (IntPtr)1;
        }

        // 6. Any Ctrl / Alt / Win shortcut (EXCEPT AltGr printable symbols like '@'!) resets word buffer and passes through
        if ((win || ((ctrl || alt) && !isAltGr)))
        {
            lock (_engineLock)
            {
                _engine.ResetContext();
            }
            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        // 7. Navigation keys & Enter: reset word buffer cleanly and NEVER swallow!
        // Passing VK_RETURN straight through preserves Shift+Enter vs Enter in Telegram/Discord/Slack
        // and prevents surprising pre-send edits right as the user hits Enter to send a message.
        if (IsNavigationVk(vk) || vk == NativeMethods.VK_RETURN)
        {
            lock (_engineLock)
            {
                _engine.ResetContext();
            }
            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        long nowMs = Environment.TickCount64;
        int deltaMs = _lastKeyDownTick > 0 ? (int)Math.Clamp(nowMs - _lastKeyDownTick, 0, 10_000) : 500;
        _lastKeyDownTick = nowMs;
        _lastKeyDownVk = vk;

        // 8. Backspace: check immediate 1-key Undo!
        if (vk == NativeMethods.VK_BACK)
        {
            CorrectionResult undoRes;
            lock (_engineLock)
            {
                undoRes = _engine.OnBackspace();
            }

            if (undoRes.HasChange)
            {
                AtomicTextInjector.Dispatch(undoRes);
                NotifyCorrection(undoRes, procName);
                return (IntPtr)1;
            }

            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        // 9. Space / Tab delimiters
        if (vk is NativeMethods.VK_SPACE or NativeMethods.VK_TAB)
        {
            char delimChar = vk == NativeMethods.VK_SPACE ? ' ' : '\t';

            CorrectionResult delimRes;
            lock (_engineLock)
            {
                delimRes = _engine.OnWhitespace(delimChar, nowMs);
            }

            if (delimRes.HasChange)
            {
                AtomicTextInjector.Dispatch(delimRes);
                NotifyCorrection(delimRes, procName);
                return (IntPtr)1;
            }

            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        // 10. Translate printable character using active window's keyboard layout (HKL)
        char? translated = TryTranslateKeyToChar(kb.vkCode, kb.scanCode, shift, isAltGr);
        if (!translated.HasValue)
        {
            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        var stroke = new KeyStrokeEvent(
            Character: translated.Value,
            DeltaPrevMs: deltaMs,
            OverlapWithPrev: overlapped,
            TimestampMs: nowMs
        );

        CorrectionResult charRes;
        lock (_engineLock)
        {
            charRes = _engine.OnCharacter(stroke);
        }

        if (charRes.HasChange)
        {
            AtomicTextInjector.Dispatch(charRes);
            NotifyCorrection(charRes, procName);
            return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
    }

    private char? TryTranslateKeyToChar(uint vkCode, uint scanCode, bool shift, bool isAltGr)
    {
        Array.Clear(_keyStateBuf, 0, _keyStateBuf.Length);
        if (shift)
        {
            _keyStateBuf[NativeMethods.VK_SHIFT] = 0x80;
        }

        if (isAltGr)
        {
            _keyStateBuf[NativeMethods.VK_CONTROL] = 0x80;
            _keyStateBuf[NativeMethods.VK_MENU] = 0x80;
            _keyStateBuf[NativeMethods.VK_RMENU] = 0x80;
        }

        if ((NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 0x0001) != 0)
        {
            _keyStateBuf[NativeMethods.VK_CAPITAL] = 0x01;
        }

        (_, _, IntPtr hkl, _) = NativeMethods.GetActiveFocusAndLayout();
        _unicodeBuf.Clear();

        // Flag 0x04 (Windows 10 1607+): do not change keyboard dead-key state inside hook!
        int rc = NativeMethods.ToUnicodeEx(vkCode, scanCode, _keyStateBuf, _unicodeBuf, _unicodeBuf.Capacity, 0x04u, hkl);
        if (rc < 0)
        {
            // Dead key encountered -> reset word buffer so accented dead-key composition stays untouched
            lock (_engineLock)
            {
                _engine.ResetContext();
            }
            return null;
        }

        if (rc == 1 && _unicodeBuf.Length >= 1)
        {
            char ch = _unicodeBuf[0];
            if (!char.IsControl(ch))
            {
                return ch;
            }
        }

        return null;
    }

    private void NotifyCorrection(CorrectionResult res, string procName)
    {
        CorrectionApplied?.Invoke(new CorrectionHistoryEntry(
            Timestamp: DateTime.Now,
            OriginalText: res.OriginalToken,
            ReplacementText: res.Kind == CorrectionKind.UndoRestore ? res.AppendText : res.ReplacementText,
            Kind: res.Kind,
            Reason: res.Reason,
            ProcessName: procName,
            WasUndone: res.Kind == CorrectionKind.UndoRestore
        ));
    }

    private static bool IsVkDownNow(int vk) =>
        (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsModifierVk(int vk) =>
        vk is NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT
           or NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
           or NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU
           or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN or NativeMethods.VK_CAPITAL;

    private static bool IsNavigationVk(int vk) =>
        vk is NativeMethods.VK_LEFT or NativeMethods.VK_RIGHT or NativeMethods.VK_UP or NativeMethods.VK_DOWN
           or NativeMethods.VK_HOME or NativeMethods.VK_END or NativeMethods.VK_PRIOR or NativeMethods.VK_NEXT
           or NativeMethods.VK_DELETE or NativeMethods.VK_ESCAPE;

    public void Dispose()
    {
        Stop();
    }
}
