using System;
using AutoZamena.Core.Models;

namespace AutoZamena.App.Interop;

/// <summary>Distinguishes our own delayed HKL change from a user's manual layout shortcut.</summary>
internal static class KeyboardLayoutTransition
{
    internal static bool ShouldReset(IntPtr previous, IntPtr current, TargetLanguage pendingAutoTarget)
    {
        if (previous == IntPtr.Zero || current == IntPtr.Zero || previous == current)
        {
            return false;
        }

        // HKL low word is a LANGID; PRIMARYLANGID occupies its low ten bits.
        int primaryLanguage = (int)(current.ToInt64() & 0x3ff);
        return pendingAutoTarget switch
        {
            TargetLanguage.Russian when primaryLanguage == 0x19 => false,
            TargetLanguage.English when primaryLanguage == 0x09 => false,
            _ => true
        };
    }
}
