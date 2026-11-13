using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AutoZamena.Core.Models;

namespace AutoZamena.App.Interop;

/// <summary>
/// Builds and dispatches atomic Win32 <c>SendInput</c> batches (minimal Backspace diff + UTF-16 KEYEVENTF_UNICODE text + delimiter)
/// stamped with <see cref="NativeMethods.MagicExtraInfo"/> so our keyboard hook ignores synthetic events.
/// Dispatches synchronously in &lt; 0.05 ms so fast subsequent physical keystrokes can never overtake the replacement!
/// </summary>
internal static class AtomicTextInjector
{
    private const ushort PrimaryLangRussian = 0x19;
    private const ushort PrimaryLangEnglish = 0x09;

    private static IntPtr[] _cachedHkls = Array.Empty<IntPtr>();
    private static long _lastHklRefreshTick;

    public static void Dispatch(CorrectionResult result)
    {
        if (!result.HasChange)
        {
            return;
        }

        ExecuteInjection(result);
    }

    private static void ExecuteInjection(CorrectionResult result)
    {
        var inputs = new List<NativeMethods.INPUT>(
            (result.BackspacesCount + result.AppendText.Length + 4) * 2
        );

        // If user accidentally had Caps Lock ON ("пРИВЕТ" -> "Привет"), turn physical Caps Lock OFF in the same atomic batch!
        if (result.TurnOffCapsLock && (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 0x0001) != 0)
        {
            AddVkKey(inputs, NativeMethods.VK_CAPITAL, keyUp: false);
            AddVkKey(inputs, NativeMethods.VK_CAPITAL, keyUp: true);
        }

        // Temporarily release physical Shift if held so synthetic VK_BACK / VK_SPACE never act as Shift+Backspace
        bool lShiftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LSHIFT) & 0x8000) != 0;
        bool rShiftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RSHIFT) & 0x8000) != 0;

        if (lShiftDown) AddVkKey(inputs, NativeMethods.VK_LSHIFT, keyUp: true);
        if (rShiftDown) AddVkKey(inputs, NativeMethods.VK_RSHIFT, keyUp: true);

        // 1. Minimal Backspace diff
        for (int i = 0; i < result.BackspacesCount; i++)
        {
            AddVkKey(inputs, NativeMethods.VK_BACK, keyUp: false);
            AddVkKey(inputs, NativeMethods.VK_BACK, keyUp: true);
        }

        // 2. Replacement text via layout-independent UTF-16 KEYEVENTF_UNICODE
        AppendUnicodeString(inputs, result.AppendText);

        // 3. Trailing delimiter (when delimiter keystroke was swallowed for atomic replacement)
        if (result.TrailingDelimiter.HasValue)
        {
            char d = result.TrailingDelimiter.Value;
            if (d == ' ')
            {
                AddVkKey(inputs, NativeMethods.VK_SPACE, keyUp: false);
                AddVkKey(inputs, NativeMethods.VK_SPACE, keyUp: true);
            }
            else if (d == '\t')
            {
                AddVkKey(inputs, NativeMethods.VK_TAB, keyUp: false);
                AddVkKey(inputs, NativeMethods.VK_TAB, keyUp: true);
            }
            else if (d is '\r' or '\n')
            {
                AddVkKey(inputs, NativeMethods.VK_RETURN, keyUp: false);
                AddVkKey(inputs, NativeMethods.VK_RETURN, keyUp: true);
            }
            else
            {
                AppendUnicodeString(inputs, d.ToString());
            }
        }

        // 4. Optional cursor backtrack for dynamic snippets with '|' cursor placeholder (e.g. «|»)
        for (int i = 0; i < result.MoveCursorLeftCount; i++)
        {
            AddVkKey(inputs, NativeMethods.VK_LEFT, keyUp: false, extended: true);
            AddVkKey(inputs, NativeMethods.VK_LEFT, keyUp: true, extended: true);
        }

        // Restore physical Shift state if user is still holding it for the next word
        if (lShiftDown) AddVkKey(inputs, NativeMethods.VK_LSHIFT, keyUp: false);
        if (rShiftDown) AddVkKey(inputs, NativeMethods.VK_RSHIFT, keyUp: false);

        if (inputs.Count > 0)
        {
            NativeMethods.INPUT[] arr = inputs.ToArray();
            NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<NativeMethods.INPUT>());
        }

        if (result.SwitchLayoutTo != TargetLanguage.None)
        {
            SwitchActiveWindowKeyboardLayout(result.SwitchLayoutTo);
        }
    }

    public static void SendCtrlShortcut(ushort asciiUpperVk)
    {
        var inputs = new List<NativeMethods.INPUT>(12);

        bool lShiftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LSHIFT) & 0x8000) != 0;
        bool rShiftDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RSHIFT) & 0x8000) != 0;
        bool lAltDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LMENU) & 0x8000) != 0;
        bool rAltDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RMENU) & 0x8000) != 0;

        if (lShiftDown) AddVkKey(inputs, NativeMethods.VK_LSHIFT, keyUp: true);
        if (rShiftDown) AddVkKey(inputs, NativeMethods.VK_RSHIFT, keyUp: true);
        if (lAltDown) AddVkKey(inputs, NativeMethods.VK_LMENU, keyUp: true);
        if (rAltDown) AddVkKey(inputs, NativeMethods.VK_RMENU, keyUp: true);

        AddVkKey(inputs, NativeMethods.VK_LCONTROL, keyUp: false);
        AddVkKey(inputs, asciiUpperVk, keyUp: false);
        AddVkKey(inputs, asciiUpperVk, keyUp: true);
        AddVkKey(inputs, NativeMethods.VK_LCONTROL, keyUp: true);

        NativeMethods.INPUT[] arr = inputs.ToArray();
        NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    public static TargetLanguage GetActiveLanguageTag()
    {
        (_, _, IntPtr activeHkl, _) = NativeMethods.GetActiveFocusAndLayout();
        ushort langId = (ushort)((ulong)activeHkl & 0xFFFF);
        ushort primary = (ushort)(langId & 0x03FF);
        return primary switch
        {
            PrimaryLangRussian => TargetLanguage.Russian,
            PrimaryLangEnglish => TargetLanguage.English,
            _ => TargetLanguage.None
        };
    }

    public static TargetLanguage ToggleActiveWindowKeyboardLayout()
    {
        (IntPtr focusHwnd, IntPtr topHwnd, IntPtr activeHkl, _) = NativeMethods.GetActiveFocusAndLayout();
        IntPtr targetHwnd = focusHwnd != IntPtr.Zero ? focusHwnd : topHwnd;
        if (targetHwnd == IntPtr.Zero)
        {
            return TargetLanguage.None;
        }

        ushort activeLangId = (ushort)((ulong)activeHkl & 0xFFFF);
        ushort activePrimary = (ushort)(activeLangId & 0x03FF);

        // Prefer explicit RU <-> EN toggle when both are installed (Caramba style), falling back to cyclic next HKL
        TargetLanguage desiredOpposite = activePrimary == PrimaryLangRussian
            ? TargetLanguage.English
            : TargetLanguage.Russian;

        IntPtr[] hkls = GetInstalledKeyboardLayoutsCached();
        if (hkls.Length >= 2)
        {
            ushort desiredPrimary = desiredOpposite == TargetLanguage.Russian
                ? PrimaryLangRussian
                : PrimaryLangEnglish;

            for (int i = 0; i < hkls.Length; i++)
            {
                ushort lid = (ushort)((ulong)hkls[i] & 0xFFFF);
                if ((lid & 0x03FF) == desiredPrimary)
                {
                    NativeMethods.PostMessageW(targetHwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkls[i]);
                    return desiredOpposite;
                }
            }

            // Fallback: cycle to next installed HKL
            int curIdx = Array.IndexOf(hkls, activeHkl);
            IntPtr nextHkl = hkls[(curIdx + 1) % hkls.Length];
            NativeMethods.PostMessageW(targetHwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, nextHkl);
        }

        return desiredOpposite;
    }

    private static void AddVkKey(List<NativeMethods.INPUT> list, ushort vk, bool keyUp, bool extended = false)
    {
        uint flags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0u;
        if (extended)
        {
            flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        }

        list.Add(new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = NativeMethods.MagicExtraInfo
                }
            }
        });
    }

    private static void AppendUnicodeString(List<NativeMethods.INPUT> list, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            ushort codeUnit = text[i];
            list.Add(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = codeUnit,
                        dwFlags = NativeMethods.KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = NativeMethods.MagicExtraInfo
                    }
                }
            });

            list.Add(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = codeUnit,
                        dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = NativeMethods.MagicExtraInfo
                    }
                }
            });
        }
    }

    public static void SwitchActiveWindowKeyboardLayout(TargetLanguage target)
    {
        ushort desiredPrimaryLang = target switch
        {
            TargetLanguage.Russian => PrimaryLangRussian,
            TargetLanguage.English => PrimaryLangEnglish,
            _ => (ushort)0
        };

        if (desiredPrimaryLang == 0)
        {
            return;
        }

        IntPtr[] hkls = GetInstalledKeyboardLayoutsCached();
        IntPtr matchedHkl = IntPtr.Zero;
        for (int i = 0; i < hkls.Length; i++)
        {
            ushort langId = (ushort)((ulong)hkls[i] & 0xFFFF);
            ushort primary = (ushort)(langId & 0x03FF);
            if (primary == desiredPrimaryLang)
            {
                matchedHkl = hkls[i];
                break;
            }
        }

        if (matchedHkl == IntPtr.Zero)
        {
            return;
        }

        (IntPtr focusHwnd, IntPtr topHwnd, _, _) = NativeMethods.GetActiveFocusAndLayout();
        IntPtr targetHwnd = focusHwnd != IntPtr.Zero ? focusHwnd : topHwnd;
        if (targetHwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessageW(targetHwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, matchedHkl);
        }
    }

    private static IntPtr[] GetInstalledKeyboardLayoutsCached()
    {
        long now = Environment.TickCount64;
        if (_cachedHkls.Length > 0 && (now - _lastHklRefreshTick) < 10_000)
        {
            return _cachedHkls;
        }

        int count = NativeMethods.GetKeyboardLayoutList(0, null);
        if (count <= 0)
        {
            return Array.Empty<IntPtr>();
        }

        var hkls = new IntPtr[count];
        int actual = NativeMethods.GetKeyboardLayoutList(count, hkls);
        if (actual <= 0)
        {
            return Array.Empty<IntPtr>();
        }

        if (actual != count)
        {
            Array.Resize(ref hkls, actual);
        }

        _cachedHkls = hkls;
        _lastHklRefreshTick = now;
        return hkls;
    }
}
