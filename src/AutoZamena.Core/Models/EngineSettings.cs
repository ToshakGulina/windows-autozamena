namespace AutoZamena.Core.Models;

public enum SensitivityLevel
{
    Conservative = 0,
    Balanced = 1,
    Aggressive = 2,
    Custom = 3
}

public enum LayoutSwitchMode
{
    Off = 0,
    HotkeyOnly = 1,
    AutoHighConfidenceAndHotkey = 2,
    AutoOnly = 3 // Automatic correction without manual conversion gestures/hotkeys
}

public static class LayoutSwitchModeExtensions
{
    public static bool AllowsAutomaticCorrection(this LayoutSwitchMode mode) =>
        mode is LayoutSwitchMode.AutoHighConfidenceAndHotkey or LayoutSwitchMode.AutoOnly;

    public static bool AllowsManualConversion(this LayoutSwitchMode mode) =>
        mode is LayoutSwitchMode.HotkeyOnly or LayoutSwitchMode.AutoHighConfidenceAndHotkey;

    public static LayoutSwitchMode WithAutomaticCorrection(this LayoutSwitchMode mode, bool enabled) =>
        enabled
            ? (mode.AllowsManualConversion() ? LayoutSwitchMode.AutoHighConfidenceAndHotkey : LayoutSwitchMode.AutoOnly)
            : (mode.AllowsManualConversion() ? LayoutSwitchMode.HotkeyOnly : LayoutSwitchMode.Off);
}

public enum TargetLanguage
{
    None = 0,
    Russian = 1,
    English = 2
}

public sealed class EngineSettings
{
    public bool Enabled { get; set; } = true;
    public SensitivityLevel Sensitivity { get; set; } = SensitivityLevel.Balanced;
    // 0 matches Conservative, 50 Balanced, 100 Aggressive. Used only for Custom.
    public int CustomSensitivity { get; set; } = 50;

    // Core corrections & mechanics
    public bool FixTypos { get; set; } = true;
    public bool SplitRunTogetherWords { get; set; } = true;
    public bool FixDoubleCaps { get; set; } = true;
    public bool FixInvertedCaps { get; set; } = true;
    public bool FixSpacebarRollover { get; set; } = true;
    public bool AutoCapitalizeAfterSentence { get; set; } = true;
    public bool AutoCapitalizeFirstWord { get; set; } = false;
    public bool DoubleSpacePeriod { get; set; } = true;
    public bool SmartTypography { get; set; } = true;

    // Layout correction (ghbdtn <-> привет)
    public LayoutSwitchMode LayoutMode { get; set; } = LayoutSwitchMode.AutoHighConfidenceAndHotkey;
    public bool SwitchSystemLayoutOnAutoFix { get; set; } = true;

    // Undo & learning
    public bool UndoOnImmediateBackspace { get; set; } = true;
    public bool AutoLearnOnUndo { get; set; } = true;

    public EngineSettings Clone()
    {
        return new EngineSettings
        {
            Enabled = Enabled,
            Sensitivity = Sensitivity,
            CustomSensitivity = CustomSensitivity,
            FixTypos = FixTypos,
            SplitRunTogetherWords = SplitRunTogetherWords,
            FixDoubleCaps = FixDoubleCaps,
            FixInvertedCaps = FixInvertedCaps,
            FixSpacebarRollover = FixSpacebarRollover,
            AutoCapitalizeAfterSentence = AutoCapitalizeAfterSentence,
            AutoCapitalizeFirstWord = AutoCapitalizeFirstWord,
            DoubleSpacePeriod = DoubleSpacePeriod,
            SmartTypography = SmartTypography,
            LayoutMode = LayoutMode,
            SwitchSystemLayoutOnAutoFix = SwitchSystemLayoutOnAutoFix,
            UndoOnImmediateBackspace = UndoOnImmediateBackspace,
            AutoLearnOnUndo = AutoLearnOnUndo
        };
    }
}
