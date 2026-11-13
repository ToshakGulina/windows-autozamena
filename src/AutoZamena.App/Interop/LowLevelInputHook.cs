using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AutoZamena.App.Config;
using AutoZamena.App.UI;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Models;

namespace AutoZamena.App.Interop;

/// <summary>
/// Global low-level keyboard + mouse hook (<c>WH_KEYBOARD_LL</c> &amp; <c>WH_MOUSE_LL</c>).
/// Captures millisecond keystroke cadence, overlap timings, Caramba Switcher modifier gestures
/// (Double Shift, Left+Right Shift, Single Shift, Double Ctrl), selection helpers, and dispatches atomic fixes.
/// </summary>
public sealed class LowLevelInputHook : IDisposable
{
    private readonly AutoCorrectEngine _engine;
    private readonly WindowContextGuard _contextGuard;
    private readonly CarambaGestureTracker _gestureTracker = new();
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
        _engine.ClipboardTextProvider = SelectionActionRunner.TryReadPlainClipboardTextSafe;
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
                // Caramba-style cursor awareness:
                // 1) Invalidate Shift+Click so Shift-selecting text never triggers Single/Double Shift
                // 2) Clear live word buffer (so Double Shift after mouse-drag selection converts the selection!)
                // 3) Suppress partial fragment correction if user clicked inside an existing word
                _gestureTracker.OnMouseClick();
                lock (_engineLock)
                {
                    _engine.NotifyPointerOrFocusChanged(suppressFragmentUntilDelimiter: true);
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
        long nowMs = Environment.TickCount64;

        // Handle Modifier keys (both Down & Up) for Caramba Switcher gestures (Double Shift, Left+Right Shift, Single Shift, Double Ctrl)
        if (IsModifierVk(vk))
        {
            if ((uint)vk < 256)
            {
                _vkDown[vk] = isKeyDown;
            }

            if ((isKeyDown || isKeyUp) && HotkeyRecorderInterceptor is null)
            {
                AppConfig modCfg = _config;
                bool anyAltOrWin = IsVkDownNow(NativeMethods.VK_MENU) ||
                                   IsVkDownNow(NativeMethods.VK_LMENU) ||
                                   IsVkDownNow(NativeMethods.VK_RMENU) ||
                                   IsVkDownNow(NativeMethods.VK_LWIN) ||
                                   IsVkDownNow(NativeMethods.VK_RWIN);
                bool mouseHeld = IsVkDownNow(NativeMethods.VK_LBUTTON) || IsVkDownNow(NativeMethods.VK_RBUTTON);
                if (mouseHeld)
                {
                    _gestureTracker.OnMouseClick();
                }

                CarambaGesture gesture = _gestureTracker.ProcessModifierEvent(
                    vk,
                    isKeyDown,
                    nowMs,
                    anyAltOrWin,
                    enableDoubleShift: modCfg.UseDoubleShiftConvert,
                    enableBothShiftsPause: modCfg.UseBothShiftsPause,
                    enableSingleShift: modCfg.UseSingleShiftLayoutSwitch,
                    enableDoubleCtrl: modCfg.UseDoubleCtrlConvertLine
                );

                // Let the physical modifier Up/Down pass through cleanly first, then execute the Caramba gesture asynchronously!
                IntPtr nextRes = NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
                if (gesture != CarambaGesture.None)
                {
                    DispatchCarambaGestureAsync(gesture, modCfg);
                }
                return nextRes;
            }

            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        if (isKeyUp)
        {
            if ((uint)vk < 256 && _vkDown[vk])
            {
                _vkDown[vk] = false;
                if (_heldPrintableKeysCount > 0)
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

        // Any non-modifier key-down invalidates pure Shift / Ctrl tap gestures
        _gestureTracker.OnNonModifierKeyDown();

        // Track key-down overlap for rollover / fat-finger detection
        bool isRepeat = (uint)vk < 256 && _vkDown[vk];
        bool overlapped = !isRepeat && _heldPrintableKeysCount > 0;
        if ((uint)vk < 256 && !_vkDown[vk])
        {
            _vkDown[vk] = true;
            _heldPrintableKeysCount++;
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
            ToggleGlobalPause(cfg);
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

        // 4b. Selected-Text Case Cycling & Transliteration hotkeys (allowed everywhere except fullscreen games & password fields!)
        if (!win && cfg.Engine.Enabled &&
            status is not (ContextStatus.FullscreenGame or ContextStatus.PasswordField or ContextStatus.OwnSettingsWindow))
        {
            if (cfg.CycleCaseHotkey.Matches(vk, ctrl, alt, shift))
            {
                TriggerSelectionTransform(SelectionTransformKind.CycleCase, procName);
                return (IntPtr)1;
            }

            if (cfg.TransliterateHotkey.Matches(vk, ctrl, alt, shift))
            {
                TriggerSelectionTransform(SelectionTransformKind.Transliterate, procName);
                return (IntPtr)1;
            }
        }

        if (status != ContextStatus.Allowed)
        {
            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        // 5. Optional custom Layout-Convert Hotkey (if assigned by user in Settings alongside Double Shift)
        if (!win && !cfg.ConvertLayoutHotkey.IsEmpty && cfg.Engine.LayoutMode != LayoutSwitchMode.Off)
        {
            bool singleMatch = cfg.ConvertLayoutHotkey.Matches(vk, ctrl, alt, shift);
            bool phraseMatch = !singleMatch &&
                               !cfg.ConvertLayoutHotkey.Shift &&
                               shift &&
                               cfg.ConvertLayoutHotkey.Matches(vk, ctrl, alt, shiftDown: false);

            if (singleMatch || phraseMatch)
            {
                CorrectionResult hkRes;
                lock (_engineLock)
                {
                    hkRes = phraseMatch
                        ? _engine.ConvertPhraseLayoutByHotkey()
                        : _engine.ConvertLastWordLayoutByHotkey();
                }

                if (hkRes.HasChange)
                {
                    AtomicTextInjector.Dispatch(hkRes);
                    NotifyCorrection(hkRes, procName, cfg.PlaySoundOnAutoSwitch);
                }
                else
                {
                    TriggerSelectionTransform(SelectionTransformKind.ConvertLayout, procName, toggleLayoutIfNoSelection: true);
                }
                return (IntPtr)1;
            }
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
        if (IsNavigationVk(vk) || vk == NativeMethods.VK_RETURN)
        {
            lock (_engineLock)
            {
                _engine.ResetContext();
            }
            return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
        }

        int deltaMs = _lastKeyDownTick > 0 ? (int)Math.Clamp(nowMs - _lastKeyDownTick, 0, 10_000) : 500;
        _lastKeyDownTick = nowMs;

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
                NotifyCorrection(undoRes, procName, playSwitchSound: false);
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
                NotifyCorrection(delimRes, procName, cfg.PlaySoundOnAutoSwitch);
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
            NotifyCorrection(charRes, procName, cfg.PlaySoundOnAutoSwitch);
            return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
    }

    private void DispatchCarambaGestureAsync(CarambaGesture gesture, AppConfig cfg)
    {
        if (gesture == CarambaGesture.BothShiftsTogglePause)
        {
            ThreadPool.QueueUserWorkItem(_ => ToggleGlobalPause(cfg));
            return;
        }

        (ContextStatus status, string procName, _) = _contextGuard.Evaluate(cfg);

        if (gesture == CarambaGesture.SingleShift)
        {
            if (status != ContextStatus.FullscreenGame)
            {
                ThreadPool.QueueUserWorkItem(_ => AtomicTextInjector.ToggleActiveWindowKeyboardLayout());
            }
            return;
        }

        if (!cfg.Engine.Enabled || status is ContextStatus.FullscreenGame or ContextStatus.PasswordField or ContextStatus.OwnSettingsWindow)
        {
            return;
        }

        if (gesture == CarambaGesture.DoubleCtrl)
        {
            if (status == ContextStatus.Allowed && cfg.Engine.LayoutMode != LayoutSwitchMode.Off)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(8);
                    CorrectionResult lineRes;
                    lock (_engineLock)
                    {
                        lineRes = _engine.ConvertPhraseLayoutByHotkey();
                    }
                    if (lineRes.HasChange)
                    {
                        AtomicTextInjector.Dispatch(lineRes);
                        NotifyCorrection(lineRes, procName, cfg.PlaySoundOnAutoSwitch);
                    }
                });
            }
            return;
        }

        if (gesture == CarambaGesture.DoubleShift && cfg.Engine.LayoutMode != LayoutSwitchMode.Off)
        {
            bool hasLiveToken = false;
            if (status == ContextStatus.Allowed)
            {
                lock (_engineLock)
                {
                    hasLiveToken = _engine.HasLiveConvertibleToken;
                }
            }

            if (hasLiveToken)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(6);
                    CorrectionResult hkRes;
                    lock (_engineLock)
                    {
                        hkRes = _engine.ConvertLastWordLayoutByHotkey();
                    }
                    if (hkRes.HasChange)
                    {
                        AtomicTextInjector.Dispatch(hkRes);
                        NotifyCorrection(hkRes, procName, cfg.PlaySoundOnAutoSwitch);
                    }
                    else
                    {
                        TriggerSelectionTransform(SelectionTransformKind.ConvertLayout, procName, toggleLayoutIfNoSelection: true);
                    }
                });
            }
            else
            {
                // No live word buffer -> convert highlighted selection (even inside editors!) or toggle OS layout if nothing selected!
                TriggerSelectionTransform(SelectionTransformKind.ConvertLayout, procName, toggleLayoutIfNoSelection: true);
            }
        }
    }

    private void ToggleGlobalPause(AppConfig cfg)
    {
        cfg.Engine.Enabled = !cfg.Engine.Enabled;
        lock (_engineLock)
        {
            _engine.Settings.Enabled = cfg.Engine.Enabled;
            _engine.ResetContext();
        }
        PauseToggledByHotkey?.Invoke();
    }

    private void TriggerSelectionTransform(
        SelectionTransformKind kind,
        string procName,
        bool toggleLayoutIfNoSelection = false)
    {
        SelectionActionRunner.RunAsync(kind, toggleLayoutIfNoSelection, (origPreview, newPreview, reason) =>
        {
            CorrectionApplied?.Invoke(new CorrectionHistoryEntry(
                Timestamp: DateTime.Now,
                Kind: CorrectionKind.HotkeyLayoutSwitch,
                OriginalText: origPreview,
                ReplacementText: newPreview,
                ProcessName: procName,
                Reason: reason,
                WasUndone: false
            ));
        });
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

    private void NotifyCorrection(CorrectionResult res, string procName, bool playSwitchSound)
    {
        if (playSwitchSound && res.SwitchLayoutTo != TargetLanguage.None)
        {
            SoftSwitchSoundPlayer.PlayForLanguage(res.SwitchLayoutTo);
        }

        CorrectionApplied?.Invoke(new CorrectionHistoryEntry(
            Timestamp: DateTime.Now,
            Kind: res.Kind,
            OriginalText: res.OriginalText,
            ReplacementText: res.Kind == CorrectionKind.UndoRestore ? res.AppendText : res.ReplacementText,
            ProcessName: procName,
            Reason: res.Reason,
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
