using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AutoZamena.Core.Models;

namespace AutoZamena.App.Interop;

/// <summary>
/// Executes diff-based text replacements atomically via Win32 SendInput on a dedicated
/// highest-priority worker thread so WH_KEYBOARD_LL never blocks or risks hook timeouts.
/// </summary>
public sealed class AtomicTextInjector : IDisposable
{
    private const ushort PrimaryLangRussian = 0x19;
    private const ushort PrimaryLangEnglish = 0x09;

    private readonly BlockingCollection<CorrectionResult> _queue = new(64);
    private readonly Thread _worker;
    private volatile bool _disposed;

    public AtomicTextInjector()
    {
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "AutoZamena-SendInputWorker"
        };
        _worker.Start();
    }

    public void Enqueue(CorrectionResult correction)
    {
        if (_disposed || !correction.HasChange)
        {
            return;
        }

        _queue.TryAdd(correction);
    }

    private void WorkerLoop()
    {
        int inputSize = Marshal.SizeOf<NativeMethods.INPUT>();

        try
        {
            foreach (CorrectionResult job in _queue.GetConsumingEnumerable())
            {
                if (_disposed)
                {
                    break;
                }

                ExecuteInjection(job, inputSize);
            }
        }
        catch (ObjectDisposedException)
        {
            // Clean shutdown
        }
    }

    private static void ExecuteInjection(CorrectionResult job, int inputStructSize)
    {
        int totalEvents = (job.BackspacesCount * 2) + (job.AppendText.Length * 2) + (job.TrailingDelimiter.HasValue ? 2 : 0);
        if (totalEvents > 0)
        {
            var inputs = new List<NativeMethods.INPUT>(totalEvents);

            // 1. Minimal diff Backspaces
            for (int i = 0; i < job.BackspacesCount; i++)
            {
                AddVirtualKeyTap(inputs, NativeMethods.VK_BACK);
            }

            // 2. Replacement suffix via KEYEVENTF_UNICODE (immune to CapsLock, held Shift, and pending layout change)
            foreach (char ch in job.AppendText)
            {
                AddUnicodeCharTap(inputs, ch);
            }

            // 3. Re-emit swallowed delimiter
            if (job.TrailingDelimiter.HasValue)
            {
                char delim = job.TrailingDelimiter.Value;
                if (delim == ' ')
                {
                    AddVirtualKeyTap(inputs, NativeMethods.VK_SPACE);
                }
                else if (delim == '\t')
                {
                    AddVirtualKeyTap(inputs, NativeMethods.VK_TAB);
                }
                else if (delim is '\r' or '\n')
                {
                    AddVirtualKeyTap(inputs, NativeMethods.VK_RETURN);
                }
                else
                {
                    AddUnicodeCharTap(inputs, delim);
                }
            }

            NativeMethods.INPUT[] arr = inputs.ToArray();
            _ = NativeMethods.SendInput((uint)arr.Length, arr, inputStructSize);
        }

        // 4. Switch active window keyboard layout if requested (e.g. "ghbdtn" -> "привет" switches EN -> RU)
        if (job.SwitchLayoutTo != TargetLanguage.None)
        {
            SwitchActiveWindowLayout(job.SwitchLayoutTo);
        }
    }

    private static void AddVirtualKeyTap(List<NativeMethods.INPUT> list, ushort vk)
    {
        list.Add(new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = 0,
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
                    wVk = vk,
                    wScan = 0,
                    dwFlags = NativeMethods.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = NativeMethods.MagicExtraInfo
                }
            }
        });
    }

    private static void AddUnicodeCharTap(List<NativeMethods.INPUT> list, char utf16Char)
    {
        list.Add(new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = utf16Char,
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
                    wScan = utf16Char,
                    dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = NativeMethods.MagicExtraInfo
                }
            }
        });
    }

    public static void SwitchActiveWindowLayout(TargetLanguage targetLanguage)
    {
        ushort wantedPrimary = targetLanguage switch
        {
            TargetLanguage.Russian => PrimaryLangRussian,
            TargetLanguage.English => PrimaryLangEnglish,
            _ => (ushort)0
        };

        if (wantedPrimary == 0)
        {
            return;
        }

        IntPtr targetHkl = FindInstalledLayout(wantedPrimary);
        if (targetHkl == IntPtr.Zero)
        {
            return;
        }

        (IntPtr focusHwnd, IntPtr topHwnd, _, _) = NativeMethods.GetActiveFocusAndLayout();
        if (focusHwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessageW(focusHwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, targetHkl);
        }
        if (topHwnd != IntPtr.Zero && topHwnd != focusHwnd)
        {
            NativeMethods.PostMessageW(topHwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, targetHkl);
        }
    }

    private static IntPtr FindInstalledLayout(ushort primaryLangId)
    {
        int count = NativeMethods.GetKeyboardLayoutList(0, null);
        if (count <= 0)
        {
            return IntPtr.Zero;
        }

        var list = new IntPtr[count];
        int actual = NativeMethods.GetKeyboardLayoutList(count, list);
        for (int i = 0; i < actual; i++)
        {
            ushort langId = unchecked((ushort)((long)list[i] & 0xFFFF));
            ushort primary = (ushort)(langId & 0x03FF);
            if (primary == primaryLangId)
            {
                return list[i];
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
    }
}
