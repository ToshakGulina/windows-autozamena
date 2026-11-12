using System;
using System.Runtime.InteropServices;
using System.Text;
using AutoZamena.App.Config;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Models;

namespace AutoZamena.App.Interop;

/// <summary>
/// Installs WH_KEYBOARD_LL and WH_MOUSE_LL hooks, tracks physical key rollover and millisecond intervals,
/// evaluates context guards, and dispatches atomic diff replacements.
/// </summary>
public sealed class LowLevelInputHook : IDisposable
{
    private const uint LLKHF_INJECTED = 0x10;
    private const uint ToUnicodeFlagNoStateChange = 0x4;

    private readonly AutoCorrectEngine _engine;
    private readonly AppConfig _config;
    private readonly WindowContextGuard _contextGuard;
    private readonly AtomicTextInjector _injector;

    // Strong delegate references to prevent GC collection while unmanaged hook is active
    private readonly NativeMethods.LowLevelProc _keyboardProc;
    private readonly NativeMethods.LowLevelProc _mouseProc;

    private readonly bool[] _swallowedVk = new bool[256];
    private readonly bool[] _typingKeyDown = new bool[256];
    private readonly byte[] _keyStateBuffer = new byte[256];
    private readonly StringBuilder _charBuffer = new(8);

    private IntPtr _keyboardHookHandle;
    private IntPtr _mouseHookHandle;
    private int _heldTypingKeysCount;
    private long _lastTypingKeyDownMs;
    private bool _disposed;

    public event Action? PauseToggledByHotkey;
    public event Action<CorrectionHistoryEntry>? CorrectionApplied;
    public event Action<ContextStatus, string>? ContextStatusChanged;

    /// <summary>
    /// Optional interceptor set while the user is recording a custom hotkey in Settings.
    /// Args: (vk, ctrl, alt, shift) -> return true to swallow key.
    /// </summary>
    public Func<int, bool, bool, bool, bool>? HotkeyRecorderInterceptor { get; set; }

    private ContextStatus _lastReportedStatus = ContextStatus.Allowed;
    private string _lastReportedProc = string.Empty;

    public LowLevelInputHook(
        AutoCorrectEngine engine,
        AppConfig config,
        WindowContextGuard contextGuard,
        AtomicTextInjector injector)
    {
        _engine = engine;
        _config = config;
        _contextGuard = contextGuard;
        _injector = injector;

        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void Start()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            return;
        }

        IntPtr hMod = NativeMethods.GetModuleHandleW(null);
        _keyboardHookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
        _mouseHookHandle = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);
    }

    public void Stop()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }

        if (_mouseHookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN)
            {
                // Clicking repositions text caret -> clear shadow typing buffer and probe password field
                _engine.ResetContext();
                _contextGuard.NotifyPotentialFocusChange();
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        NativeMethods.KBDLLHOOKSTRUCT info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        // 1. Always pass our own synthetic SendInput keys straight through!
        if (info.dwExtraInfo == NativeMethods.MagicExtraInfo)
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        // Ignore synthetic keys injected by third-party macros/tools
        if ((info.flags & LLKHF_INJECTED) != 0)
        {
            _engine.ResetContext();
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        int vk = (int)info.vkCode;
        int msg = wParam.ToInt32();
        bool isKeyDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        bool isKeyUp = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        // Track physical key release & swallow matching key-up of previously swallowed key-down
        if (isKeyUp)
        {
            if (vk is >= 0 and < 256)
            {
                if (_typingKeyDown[vk])
                {
                    _typingKeyDown[vk] = false;
                    if (_heldTypingKeysCount > 0) _heldTypingKeysCount--;
                }

                if (_swallowedVk[vk])
                {
                    _swallowedVk[vk] = false;
                    return (IntPtr)1;
                }
            }

            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        if (!isKeyDown)
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        // Ignore standalone modifier keydowns themselves
        if (IsModifierVk(vk))
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        bool ctrlDown = IsPressedAsync(NativeMethods.VK_CONTROL) || IsPressedAsync(NativeMethods.VK_LCONTROL) || IsPressedAsync(NativeMethods.VK_RCONTROL);
        bool altDown = IsPressedAsync(NativeMethods.VK_MENU) || IsPressedAsync(NativeMethods.VK_LMENU) || IsPressedAsync(NativeMethods.VK_RMENU);
        bool shiftDown = IsPressedAsync(NativeMethods.VK_SHIFT) || IsPressedAsync(NativeMethods.VK_LSHIFT) || IsPressedAsync(NativeMethods.VK_RSHIFT);
        bool winDown = IsPressedAsync(NativeMethods.VK_LWIN) || IsPressedAsync(NativeMethods.VK_RWIN);

        // 2. If Settings UI is currently recording a custom hotkey, hand it over
        var recorder = HotkeyRecorderInterceptor;
        if (recorder is not null && !winDown)
        {
            if (recorder(vk, ctrlDown, altDown, shiftDown))
            {
                if (vk is >= 0 and < 256) _swallowedVk[vk] = true;
                return (IntPtr)1;
            }
        }

        // 3. Global Pause/Resume Hotkey (works everywhere!)
        if (!winDown && _config.PauseToggleHotkey.Matches(vk, ctrlDown, altDown, shiftDown))
        {
            _config.Engine.Enabled = !_config.Engine.Enabled;
            _engine.Settings.Enabled = _config.Engine.Enabled;
            _engine.ResetContext();
            PauseToggledByHotkey?.Invoke();
            if (vk is >= 0 and < 256) _swallowedVk[vk] = true;
            return (IntPtr)1;
        }

        // Evaluate foreground window context
        (ContextStatus ctxStatus, string procName, bool focusChanged) = _contextGuard.Evaluate(_config);
        if (focusChanged)
        {
            _engine.ResetContext();
            ResetPhysicalKeyTracking();
        }

        if (ctxStatus != _lastReportedStatus || procName != _lastReportedProc)
        {
            _lastReportedStatus = ctxStatus;
            _lastReportedProc = procName;
            ContextStatusChanged?.Invoke(ctxStatus, procName);
        }

        if (ctxStatus != ContextStatus.Allowed)
        {
            _engine.ResetContext();
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        // 4. Explicit Convert-Last-Word-Layout Hotkey (e.g. Pause/Break)
        if (!winDown &&
            _config.Engine.LayoutMode != LayoutSwitchMode.Off &&
            _config.ConvertLayoutHotkey.Matches(vk, ctrlDown, altDown, shiftDown))
        {
            CorrectionResult hkResult = _engine.ConvertLastWordLayoutByHotkey();
            if (hkResult.HasChange)
            {
                _injector.Enqueue(hkResult);
                RecordHistory(hkResult, procName);
                if (vk is >= 0 and < 256) _swallowedVk[vk] = true;
                return (IntPtr)1;
            }
        }

        // 5. Any Ctrl / Alt / Win shortcut (Ctrl+C, Ctrl+V, Ctrl+Z, Alt+Tab, etc.) -> pass through & reset buffer
        if (ctrlDown || altDown || winDown)
        {
            _engine.ResetContext();
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        // 6. Navigation / editing keys that move the caret -> reset buffer
        if (IsNavigationOrResetKey(vk))
        {
            _engine.ResetContext();
            if (vk == NativeMethods.VK_TAB)
            {
                _contextGuard.NotifyPotentialFocusChange();
            }
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        // 7. Backspace -> check 1-key Undo or trim shadow buffer
        if (vk == NativeMethods.VK_BACK)
        {
            CorrectionResult undoRes = _engine.OnBackspace();
            if (undoRes.HasChange)
            {
                _injector.Enqueue(undoRes);
                RecordHistory(undoRes, procName);
                _swallowedVk[NativeMethods.VK_BACK] = true;
                return (IntPtr)1;
            }
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        // 8. Space or Enter
        long eventTimeMs = info.time;
        if (vk == NativeMethods.VK_SPACE)
        {
            CorrectionResult spaceRes = _engine.OnWhitespace(' ', eventTimeMs);
            if (spaceRes.HasChange)
            {
                _injector.Enqueue(spaceRes);
                RecordHistory(spaceRes, procName);
                _swallowedVk[NativeMethods.VK_SPACE] = true;
                return (IntPtr)1;
            }
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        if (vk == NativeMethods.VK_RETURN)
        {
            _contextGuard.NotifyPotentialFocusChange();
            CorrectionResult enterRes = _engine.OnCharacter(new KeyStrokeEvent('\n', 100, false, eventTimeMs));
            if (enterRes.HasChange)
            {
                _injector.Enqueue(enterRes);
                RecordHistory(enterRes, procName);
                _swallowedVk[NativeMethods.VK_RETURN] = true;
                return (IntPtr)1;
            }
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        // 9. Translate physical key to character using active window's HKL without altering dead-key state
        char? typedChar = TranslateVkToChar(info.vkCode, info.scanCode, shiftDown);
        if (!typedChar.HasValue || char.IsControl(typedChar.Value))
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        // Compute millisecond DeltaPrevMs and physical rollover overlap
        int deltaMs = _lastTypingKeyDownMs > 0
            ? (int)Math.Clamp(eventTimeMs - _lastTypingKeyDownMs, 1, 5000)
            : 120;
        bool overlapWithPrev = _heldTypingKeysCount > 0;
        _lastTypingKeyDownMs = eventTimeMs;

        if (vk is >= 0 and < 256 && !_typingKeyDown[vk])
        {
            _typingKeyDown[vk] = true;
            _heldTypingKeysCount++;
        }

        var keystroke = new KeyStrokeEvent(typedChar.Value, deltaMs, overlapWithPrev, eventTimeMs);
        CorrectionResult charRes = _engine.OnCharacter(keystroke);

        if (charRes.HasChange)
        {
            _injector.Enqueue(charRes);
            RecordHistory(charRes, procName);
            if (vk is >= 0 and < 256) _swallowedVk[vk] = true;
            return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private char? TranslateVkToChar(uint vkCode, uint scanCode, bool shiftDown)
    {
        Array.Clear(_keyStateBuffer, 0, _keyStateBuffer.Length);
        if (shiftDown)
        {
            _keyStateBuffer[NativeMethods.VK_SHIFT] = 0x80;
        }
        if ((NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 0x0001) != 0)
        {
            _keyStateBuffer[NativeMethods.VK_CAPITAL] = 0x01;
        }

        (_, _, IntPtr activeHkl, _) = NativeMethods.GetActiveFocusAndLayout();
        _charBuffer.Clear();
        int res = NativeMethods.ToUnicodeEx(
            vkCode,
            scanCode,
            _keyStateBuffer,
            _charBuffer,
            _charBuffer.Capacity,
            ToUnicodeFlagNoStateChange,
            activeHkl
        );

        if (res == 1 && _charBuffer.Length >= 1)
        {
            return _charBuffer[0];
        }

        return null;
    }

    private void ResetPhysicalKeyTracking()
    {
        Array.Clear(_typingKeyDown, 0, _typingKeyDown.Length);
        _heldTypingKeysCount = 0;
        _lastTypingKeyDownMs = 0;
    }

    private void RecordHistory(CorrectionResult result, string processName)
    {
        CorrectionApplied?.Invoke(new CorrectionHistoryEntry(
            DateTime.Now,
            result.Kind,
            result.OriginalText,
            result.ReplacementText,
            string.IsNullOrEmpty(processName) ? "System" : processName,
            result.Reason,
            WasUndone: result.Kind == CorrectionKind.UndoRestore
        ));
    }

    private static bool IsPressedAsync(ushort vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsModifierVk(int vk) =>
        vk is NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT
           or NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
           or NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU
           or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;

    private static bool IsNavigationOrResetKey(int vk) =>
        vk is NativeMethods.VK_LEFT or NativeMethods.VK_RIGHT or NativeMethods.VK_UP or NativeMethods.VK_DOWN
           or NativeMethods.VK_HOME or NativeMethods.VK_END or NativeMethods.VK_PRIOR or NativeMethods.VK_NEXT
           or NativeMethods.VK_DELETE or NativeMethods.VK_ESCAPE or NativeMethods.VK_TAB
           or (>= 0x70 and <= 0x87); // F1..F24

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
