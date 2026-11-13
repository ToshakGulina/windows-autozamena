namespace AutoZamena.App.Interop;

public enum CarambaGesture
{
    None = 0,
    SingleShift = 1,
    DoubleShift = 2,
    BothShiftsTogglePause = 3,
    DoubleCtrl = 4
}

/// <summary>
/// Deterministic state machine for Caramba Switcher modifier gestures:
/// - Double Shift (2x Shift): convert last word / cancel auto-switch / convert selected text
/// - Left Shift + Right Shift: toggle Pause / Resume
/// - Single Shift (optional): switch active OS layout
/// - Double Ctrl (2x Ctrl): convert entire typed line without selection
/// </summary>
public sealed class CarambaGestureTracker
{
    private const int MaxSingleTapHoldMs = 420;
    private const int MaxDoubleTapGapMs = 430;

    private bool _lShiftDown;
    private bool _rShiftDown;
    private bool _bothShiftsTouchedInChord;
    private bool _bothShiftsFiredInChord;
    private bool _shiftInvalidatedByOtherAction;
    private long _shiftChordStartMs;
    private long _lastPureShiftUpMs;

    private bool _lCtrlDown;
    private bool _rCtrlDown;
    private bool _ctrlInvalidatedByOtherAction;
    private long _ctrlPressStartMs;
    private long _lastPureCtrlUpMs;

    public void Reset()
    {
        _lShiftDown = false;
        _rShiftDown = false;
        _bothShiftsTouchedInChord = false;
        _bothShiftsFiredInChord = false;
        _shiftInvalidatedByOtherAction = false;
        _shiftChordStartMs = 0;
        _lastPureShiftUpMs = 0;

        _lCtrlDown = false;
        _rCtrlDown = false;
        _ctrlInvalidatedByOtherAction = false;
        _ctrlPressStartMs = 0;
        _lastPureCtrlUpMs = 0;
    }

    public void OnMouseClick()
    {
        _shiftInvalidatedByOtherAction = true;
        _ctrlInvalidatedByOtherAction = true;
        _lastPureShiftUpMs = 0;
        _lastPureCtrlUpMs = 0;
    }

    public void OnNonModifierKeyDown()
    {
        _shiftInvalidatedByOtherAction = true;
        _ctrlInvalidatedByOtherAction = true;
        _lastPureShiftUpMs = 0;
        _lastPureCtrlUpMs = 0;
    }

    public CarambaGesture ProcessModifierEvent(
        int vk,
        bool isKeyDown,
        long nowMs,
        bool anyAltOrWinDown,
        bool enableDoubleShift,
        bool enableBothShiftsPause,
        bool enableSingleShift,
        bool enableDoubleCtrl)
    {
        bool isShiftKey = vk is NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT or NativeMethods.VK_SHIFT;
        bool isCtrlKey = vk is NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL or NativeMethods.VK_CONTROL;

        if (!isShiftKey && !isCtrlKey)
        {
            // Alt or Win pressed -> invalidate any pure Shift / Ctrl tap in progress
            _shiftInvalidatedByOtherAction = true;
            _ctrlInvalidatedByOtherAction = true;
            _lastPureShiftUpMs = 0;
            _lastPureCtrlUpMs = 0;
            return CarambaGesture.None;
        }

        if (anyAltOrWinDown)
        {
            _shiftInvalidatedByOtherAction = true;
            _ctrlInvalidatedByOtherAction = true;
            _lastPureShiftUpMs = 0;
            _lastPureCtrlUpMs = 0;
        }

        if (isShiftKey)
        {
            // Pressing Shift invalidates any pure Ctrl tap sequence (e.g. Ctrl+Shift OS layout switch!)
            _ctrlInvalidatedByOtherAction = true;
            _lastPureCtrlUpMs = 0;

            if (_lCtrlDown || _rCtrlDown)
            {
                _shiftInvalidatedByOtherAction = true;
                _lastPureShiftUpMs = 0;
            }

            return HandleShift(vk, isKeyDown, nowMs, anyAltOrWinDown, enableDoubleShift, enableBothShiftsPause, enableSingleShift);
        }

        // Ctrl key event: invalidate any pure Shift tap sequence
        _shiftInvalidatedByOtherAction = true;
        _lastPureShiftUpMs = 0;
        if (_lShiftDown || _rShiftDown)
        {
            _ctrlInvalidatedByOtherAction = true;
            _lastPureCtrlUpMs = 0;
        }

        return HandleCtrl(vk, isKeyDown, nowMs, anyAltOrWinDown, enableDoubleCtrl);
    }

    private CarambaGesture HandleShift(
        int vk,
        bool isKeyDown,
        long nowMs,
        bool anyAltOrWinDown,
        bool enableDoubleShift,
        bool enableBothShiftsPause,
        bool enableSingleShift)
    {
        if (isKeyDown)
        {
            bool wasAnyShiftDown = _lShiftDown || _rShiftDown;

            if (vk == NativeMethods.VK_RSHIFT) _rShiftDown = true;
            else _lShiftDown = true;

            if (!wasAnyShiftDown)
            {
                _shiftChordStartMs = nowMs;
                _bothShiftsTouchedInChord = false;
                _bothShiftsFiredInChord = false;
                _shiftInvalidatedByOtherAction = anyAltOrWinDown || _lCtrlDown || _rCtrlDown;
            }
            else if (_lShiftDown && _rShiftDown)
            {
                _bothShiftsTouchedInChord = true;
            }

            return CarambaGesture.None;
        }

        // KeyUp
        if (vk == NativeMethods.VK_RSHIFT) _rShiftDown = false;
        else _lShiftDown = false;

        bool stillHoldingOtherShift = _lShiftDown || _rShiftDown;

        // 1. Left Shift + Right Shift simultaneous chord (fires once when the first of the two Shifts releases cleanly)
        if (_bothShiftsTouchedInChord)
        {
            _lastPureShiftUpMs = 0;
            if (!_bothShiftsFiredInChord &&
                !_shiftInvalidatedByOtherAction &&
                enableBothShiftsPause &&
                (nowMs - _shiftChordStartMs) <= 1200)
            {
                _bothShiftsFiredInChord = true;
                return CarambaGesture.BothShiftsTogglePause;
            }
            return CarambaGesture.None;
        }

        if (stillHoldingOtherShift)
        {
            return CarambaGesture.None;
        }

        // 2. Single / Double pure Shift tap
        long holdDuration = nowMs - _shiftChordStartMs;
        if (_shiftInvalidatedByOtherAction || holdDuration < 0 || holdDuration > MaxSingleTapHoldMs)
        {
            _lastPureShiftUpMs = 0;
            return CarambaGesture.None;
        }

        if (enableDoubleShift && _lastPureShiftUpMs > 0 && (nowMs - _lastPureShiftUpMs) <= MaxDoubleTapGapMs)
        {
            _lastPureShiftUpMs = 0;
            return CarambaGesture.DoubleShift;
        }

        _lastPureShiftUpMs = nowMs;
        return enableSingleShift ? CarambaGesture.SingleShift : CarambaGesture.None;
    }

    private CarambaGesture HandleCtrl(
        int vk,
        bool isKeyDown,
        long nowMs,
        bool anyAltOrWinDown,
        bool enableDoubleCtrl)
    {
        if (isKeyDown)
        {
            bool wasAnyCtrlDown = _lCtrlDown || _rCtrlDown;
            if (vk == NativeMethods.VK_RCONTROL) _rCtrlDown = true;
            else _lCtrlDown = true;

            if (!wasAnyCtrlDown)
            {
                _ctrlPressStartMs = nowMs;
                _ctrlInvalidatedByOtherAction = anyAltOrWinDown || _lShiftDown || _rShiftDown;
            }
            else if (_lCtrlDown && _rCtrlDown)
            {
                _ctrlInvalidatedByOtherAction = true;
            }

            return CarambaGesture.None;
        }

        if (vk == NativeMethods.VK_RCONTROL) _rCtrlDown = false;
        else _lCtrlDown = false;

        if (_lCtrlDown || _rCtrlDown)
        {
            return CarambaGesture.None;
        }

        long holdDuration = nowMs - _ctrlPressStartMs;
        if (_ctrlInvalidatedByOtherAction || holdDuration < 0 || holdDuration > MaxSingleTapHoldMs)
        {
            _lastPureCtrlUpMs = 0;
            return CarambaGesture.None;
        }

        if (enableDoubleCtrl && _lastPureCtrlUpMs > 0 && (nowMs - _lastPureCtrlUpMs) <= MaxDoubleTapGapMs)
        {
            _lastPureCtrlUpMs = 0;
            return CarambaGesture.DoubleCtrl;
        }

        _lastPureCtrlUpMs = nowMs;
        return CarambaGesture.None;
    }
}
