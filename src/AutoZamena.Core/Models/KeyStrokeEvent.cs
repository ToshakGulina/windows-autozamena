namespace AutoZamena.Core.Models;

/// <summary>
/// Represents a single typed character along with physical keystroke timing metadata.
/// </summary>
public readonly record struct KeyStrokeEvent(
    char Character,
    int DeltaPrevMs = 120,
    bool OverlapWithPrev = false,
    long TimestampMs = 0
);
