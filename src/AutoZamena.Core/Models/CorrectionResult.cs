using System;

namespace AutoZamena.Core.Models;

public enum CorrectionKind
{
    None = 0,
    Typo = 1,
    ExplicitFix = 2,
    UserSnippet = 3,
    DoubleCaps = 4,
    SpacebarRolloverMerge = 5,
    SpacebarRolloverDropLateTail = 6,
    AutoCapitalize = 7,
    WrongLayout = 8,
    HotkeyLayoutSwitch = 9,
    UndoRestore = 10,
    InvertedCaps = 11,
    SmartPunctuation = 12
}

/// <summary>
/// Describes an atomic diff-based text edit to apply in the focused window.
/// </summary>
public sealed record CorrectionResult(
    CorrectionKind Kind,
    string OriginalText,
    string ReplacementText,
    int BackspacesCount,
    string AppendText,
    char? TrailingDelimiter,
    TargetLanguage SwitchLayoutTo = TargetLanguage.None,
    double ConfidenceScore = 0.0,
    string Reason = "",
    bool TurnOffCapsLock = false,
    int MoveCursorLeftCount = 0
)
{
    public static readonly CorrectionResult NoChange = new(
        CorrectionKind.None,
        string.Empty,
        string.Empty,
        0,
        string.Empty,
        null
    );

    public bool HasChange => Kind != CorrectionKind.None;

    /// <summary>
    /// Computes the minimal suffix replacement plan turning <paramref name="onScreenBeforeCaret"/>
    /// into <paramref name="desiredText"/> (plus optional <paramref name="trailingDelimiter"/>).
    /// </summary>
    public static CorrectionResult FromDiff(
        CorrectionKind kind,
        string originalToken,
        string onScreenBeforeCaret,
        string desiredText,
        char? trailingDelimiter,
        TargetLanguage switchLayoutTo = TargetLanguage.None,
        double confidenceScore = 1.0,
        string reason = "",
        bool turnOffCapsLock = false,
        int moveCursorLeftCount = 0)
    {
        int common = CommonPrefixLength(onScreenBeforeCaret, desiredText);
        int backspaces = onScreenBeforeCaret.Length - common;
        string append = desiredText[common..];

        return new CorrectionResult(
            kind,
            originalToken,
            desiredText,
            backspaces,
            append,
            trailingDelimiter,
            switchLayoutTo,
            confidenceScore,
            reason,
            turnOffCapsLock,
            moveCursorLeftCount
        );
    }

    public static int CommonPrefixLength(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int limit = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < limit && a[i] == b[i])
        {
            i++;
        }
        return i;
    }
}
