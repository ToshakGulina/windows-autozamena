namespace AutoZamena.Core.Models;

public sealed record CorrectionHistoryEntry(
    DateTime Timestamp,
    CorrectionKind Kind,
    string OriginalText,
    string ReplacementText,
    string ProcessName,
    string Reason,
    bool WasUndone = false
);
