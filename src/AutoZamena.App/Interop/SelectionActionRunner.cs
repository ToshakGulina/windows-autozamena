using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using AutoZamena.Core.Layout;
using AutoZamena.Core.Lexicon;
using AutoZamena.Core.Models;

namespace AutoZamena.App.Interop;

public enum SelectionTransformKind
{
    ConvertLayout = 0,
    CycleCase = 1,
    Transliterate = 2
}

/// <summary>
/// Runs selected-text transformations (layout conversion, case cycling, transliteration)
/// on a background STA thread while backing up and restoring clipboard contents.
/// </summary>
internal static class SelectionActionRunner
{
    private static int _busyFlag;

    public static void RunAsync(
        SelectionTransformKind kind,
        bool toggleLayoutIfNoSelection,
        Action<string, string, string>? onCompleted = null)
    {
        if (Interlocked.CompareExchange(ref _busyFlag, 1, 0) != 0)
        {
            return;
        }

        var staThread = new Thread(() =>
        {
            try
            {
                ExecuteSta(kind, toggleLayoutIfNoSelection, onCompleted);
            }
            catch
            {
                // Ignore transient clipboard lock contention
            }
            finally
            {
                Interlocked.Exchange(ref _busyFlag, 0);
            }
        })
        {
            IsBackground = true,
            Name = "AutoZamena.SelectionWorker"
        };
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
    }

    public static string? TryReadPlainClipboardTextSafe()
    {
        string? text = null;
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    text = Clipboard.GetText();
                }
            }
            catch
            {
                // Ignore
            }
            return text;
        }

        using var done = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    text = Clipboard.GetText();
                }
            }
            catch
            {
                // Ignore
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        done.Wait(60);
        return text;
    }

    private static void ExecuteSta(
        SelectionTransformKind kind,
        bool toggleLayoutIfNoSelection,
        Action<string, string, string>? onCompleted)
    {
        // Let physical modifier key-up settle in the target window
        Thread.Sleep(18);

        DataObject? backup = BackupClipboard(out bool clipboardWasEmpty);
        uint seqBefore = NativeMethods.GetClipboardSequenceNumber();

        AtomicTextInjector.SendCtrlShortcut((ushort)'C');

        bool copied = false;
        for (int attempt = 0; attempt < 13; attempt++)
        {
            Thread.Sleep(10);
            if (NativeMethods.GetClipboardSequenceNumber() != seqBefore)
            {
                copied = true;
                break;
            }
        }

        if (!copied)
        {
            if (toggleLayoutIfNoSelection)
            {
                AtomicTextInjector.ToggleActiveWindowKeyboardLayout();
            }
            return;
        }

        if (IsEmptySelectionWholeLineCopy())
        {
            RestoreClipboard(backup, clipboardWasEmpty, clipboardWasOverwritten: true);
            if (toggleLayoutIfNoSelection)
            {
                AtomicTextInjector.ToggleActiveWindowKeyboardLayout();
            }
            return;
        }

        string selectedText = string.Empty;
        try
        {
            if (Clipboard.ContainsText())
            {
                selectedText = Clipboard.GetText();
            }
        }
        catch
        {
            RestoreClipboard(backup, clipboardWasEmpty, clipboardWasOverwritten: true);
            return;
        }

        if (string.IsNullOrEmpty(selectedText))
        {
            RestoreClipboard(backup, clipboardWasEmpty, clipboardWasOverwritten: true);
            if (toggleLayoutIfNoSelection)
            {
                AtomicTextInjector.ToggleActiveWindowKeyboardLayout();
            }
            return;
        }

        string transformed;
        TargetLanguage switchTo = TargetLanguage.None;
        string reason;

        switch (kind)
        {
            case SelectionTransformKind.ConvertLayout:
                (transformed, switchTo) = SmartTextConverter.ConvertSelectionLayout(selectedText, LexiconDatabase.Default);
                reason = switchTo == TargetLanguage.Russian
                    ? "Выделение EN→RU (Double Shift)"
                    : "Выделение RU→EN (Double Shift)";
                break;

            case SelectionTransformKind.CycleCase:
                transformed = SmartTextConverter.CycleCaseThreeWay(selectedText);
                reason = "Смена регистра выделения";
                break;

            case SelectionTransformKind.Transliterate:
                transformed = SmartTextConverter.Transliterate(selectedText);
                reason = "Транслитерация выделения";
                break;

            default:
                RestoreClipboard(backup, clipboardWasEmpty, clipboardWasOverwritten: true);
                return;
        }

        if (string.Equals(transformed, selectedText, StringComparison.Ordinal))
        {
            RestoreClipboard(backup, clipboardWasEmpty, clipboardWasOverwritten: true);
            return;
        }

        try
        {
            Clipboard.SetText(transformed);
            Thread.Sleep(10);
            AtomicTextInjector.SendCtrlShortcut((ushort)'V');
            Thread.Sleep(75);
        }
        finally
        {
            RestoreClipboard(backup, clipboardWasEmpty, clipboardWasOverwritten: true);
        }

        if (switchTo != TargetLanguage.None)
        {
            AtomicTextInjector.SwitchActiveWindowKeyboardLayout(switchTo);
        }

        string previewOrig = TruncatePreview(selectedText);
        string previewNew = TruncatePreview(transformed);
        onCompleted?.Invoke(previewOrig, previewNew, reason);
    }

    private static bool IsEmptySelectionWholeLineCopy()
    {
        try
        {
            IDataObject? dataObj = Clipboard.GetDataObject();
            if (dataObj is null)
            {
                return false;
            }

            if (dataObj.GetDataPresent("MSDEVLineSelect", autoConvert: false) ||
                dataObj.GetDataPresent("VisualStudioEditorOperationsLineCutCopyClipboardTag", autoConvert: false) ||
                dataObj.GetDataPresent("Borland IDE Block Type", autoConvert: false))
            {
                return true;
            }

            if (dataObj.GetDataPresent("vscode-editor-data", autoConvert: false))
            {
                object? raw = dataObj.GetData("vscode-editor-data", autoConvert: false);
                string? meta = raw as string;
                if (meta is null && raw is System.IO.MemoryStream ms)
                {
                    meta = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                }

                if (meta is not null && meta.Contains("\"isFromEmptySelection\":true", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore custom clipboard format inspection failures
        }

        return false;
    }

    private static DataObject? BackupClipboard(out bool wasEmpty)
    {
        wasEmpty = true;
        try
        {
            IDataObject? current = Clipboard.GetDataObject();
            if (current is null)
            {
                return null;
            }

            string[] presentFormats = current.GetFormats(autoConvert: false);
            wasEmpty = presentFormats is null || presentFormats.Length == 0;
            if (wasEmpty)
            {
                return null;
            }

            var clone = new DataObject();
            string[] textFormats =
            {
                DataFormats.UnicodeText,
                DataFormats.Text,
                DataFormats.Html,
                DataFormats.Rtf
            };

            int savedCount = 0;
            foreach (string fmt in textFormats)
            {
                try
                {
                    if (current.GetDataPresent(fmt, autoConvert: false))
                    {
                        object? data = current.GetData(fmt, autoConvert: false);
                        if (data is not null)
                        {
                            clone.SetData(fmt, autoConvert: false, data);
                            savedCount++;
                        }
                    }
                }
                catch
                {
                    // Skip unserializable OLE handle format
                }
            }

            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    if (files is { Count: > 0 })
                    {
                        clone.SetFileDropList(files);
                        savedCount++;
                    }
                }
            }
            catch
            {
                // Ignore file drop read failure
            }

            try
            {
                if (Clipboard.ContainsImage())
                {
                    using System.Drawing.Image? img = Clipboard.GetImage();
                    if (img is not null)
                    {
                        clone.SetImage(new System.Drawing.Bitmap(img));
                        savedCount++;
                    }
                }
            }
            catch
            {
                // Ignore image conversion failure
            }

            return savedCount > 0 ? clone : null;
        }
        catch
        {
            return null;
        }
    }

    private static void RestoreClipboard(DataObject? backup, bool clipboardWasEmpty, bool clipboardWasOverwritten)
    {
        try
        {
            if (backup is not null)
            {
                Clipboard.SetDataObject(backup, copy: true);
            }
            else if (clipboardWasEmpty && clipboardWasOverwritten)
            {
                Clipboard.Clear();
            }
        }
        catch
        {
            // Ignore transient clipboard lock
        }
    }

    private static string TruncatePreview(string s)
    {
        string singleLine = s.Replace("\r\n", " ⏎ ").Replace('\n', ' ');
        return singleLine.Length <= 36 ? singleLine : singleLine[..33] + "…";
    }
}
