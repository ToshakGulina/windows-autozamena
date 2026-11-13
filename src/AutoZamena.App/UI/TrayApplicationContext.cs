using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AutoZamena.App.Config;
using AutoZamena.App.Interop;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Lexicon;
using AutoZamena.Core.Models;

namespace AutoZamena.App.UI;

/// <summary>
/// Hosts the persistent Windows system tray icon, dynamic RU/EN language badge, Caramba quick toggles,
/// settings window, and global input hooks.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _uiSyncContext;
    private readonly AppConfig _config;
    private readonly AutoCorrectEngine _engine;
    private readonly WindowContextGuard _contextGuard;
    private readonly LowLevelInputHook _hook;

    private readonly Icon _iconActiveGeneric;
    private readonly Icon _iconActiveRu;
    private readonly Icon _iconActiveEn;
    private readonly Icon _iconStandbyGeneric;
    private readonly Icon _iconStandbyRu;
    private readonly Icon _iconStandbyEn;
    private readonly Icon _iconPaused;

    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _layoutPollTimer;
    private readonly ToolStripMenuItem _miSessionStats;
    private readonly ToolStripMenuItem _miTogglePause;
    private readonly ToolStripMenuItem _miAutoLayoutSwitch;
    private readonly ToolStripMenuItem _miSingleShift;
    private readonly ToolStripMenuItem _miSwitchSound;
    private readonly ToolStripMenuItem _miExcludeLastApp;
    private readonly MainForm _mainForm;
    private readonly RegisteredWaitHandle? _activateWaitRegistration;

    private ContextStatus _currentContextStatus = ContextStatus.Allowed;
    private TargetLanguage _currentDetectedLayout = TargetLanguage.None;
    private string _lastExternalProcess = string.Empty;
    private int _sessionFixedCount;
    private bool _suppressTrayMenuEvents;

    public TrayApplicationContext(bool startMinimized, EventWaitHandle? activateSignal = null)
    {
        WindowsFormsSynchronizationContext.AutoInstall = true;
        _uiSyncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _config = AppConfig.Load();

        var userDict = new UserDictionary();
        userDict.Load(_config.IgnoredWords, _config.CustomReplacements);

        _engine = new AutoCorrectEngine(
            lexicon: LexiconDatabase.Default,
            userDictionary: userDict,
            settings: _config.Engine
        );

        _contextGuard = new WindowContextGuard();
        _hook = new LowLevelInputHook(_engine, _contextGuard, _config);

        _iconActiveGeneric = TrayIconGenerator.CreateIcon(TrayVisualState.Active, "А");
        _iconActiveRu = TrayIconGenerator.CreateIcon(TrayVisualState.Active, "RU", isEnglishAccent: false);
        _iconActiveEn = TrayIconGenerator.CreateIcon(TrayVisualState.Active, "EN", isEnglishAccent: true);
        _iconStandbyGeneric = TrayIconGenerator.CreateIcon(TrayVisualState.StandbyFilteredApp, "А");
        _iconStandbyRu = TrayIconGenerator.CreateIcon(TrayVisualState.StandbyFilteredApp, "RU");
        _iconStandbyEn = TrayIconGenerator.CreateIcon(TrayVisualState.StandbyFilteredApp, "EN");
        _iconPaused = TrayIconGenerator.CreateIcon(TrayVisualState.Paused, "А");

        _mainForm = new MainForm(
            _config,
            _engine,
            _hook,
            _iconActiveGeneric,
            onConfigUpdated: OnConfigUpdatedFromUi
        );

        _contextGuard.IsOwnSandboxFocused = () => _mainForm.IsSandboxInputFocused;

        // Build Tray Context Menu (with Caramba Switcher quick toggles right in the tray!)
        var menu = new ContextMenuStrip();

        _miSessionStats = new ToolStripMenuItem("Исправлено за сессию: 0")
        {
            Enabled = false
        };

        var miOpen = new ToolStripMenuItem("Открыть настройки и песочницу…")
        {
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        miOpen.Click += (_, _) => ShowMainForm();

        _miTogglePause = new ToolStripMenuItem();
        _miTogglePause.Click += (_, _) => TogglePauseFromMenu();

        _miAutoLayoutSwitch = new ToolStripMenuItem("Автопереключение раскладки (RU ↔ EN)")
        {
            CheckOnClick = true
        };
        _miAutoLayoutSwitch.CheckedChanged += (_, _) =>
        {
            if (_suppressTrayMenuEvents) return;
            _config.Engine.LayoutMode = _miAutoLayoutSwitch.Checked
                ? LayoutSwitchMode.AutoHighConfidenceAndHotkey
                : LayoutSwitchMode.HotkeyOnly;
            SaveAndSyncWithMainForm();
        };

        _miSingleShift = new ToolStripMenuItem("Менять язык одиночным Shift (Single Shift)")
        {
            CheckOnClick = true
        };
        _miSingleShift.CheckedChanged += (_, _) =>
        {
            if (_suppressTrayMenuEvents) return;
            _config.UseSingleShiftLayoutSwitch = _miSingleShift.Checked;
            SaveAndSyncWithMainForm();
        };

        _miSwitchSound = new ToolStripMenuItem("Звуковой щелчок при автопереключении")
        {
            CheckOnClick = true
        };
        _miSwitchSound.CheckedChanged += (_, _) =>
        {
            if (_suppressTrayMenuEvents) return;
            _config.PlaySoundOnAutoSwitch = _miSwitchSound.Checked;
            SaveAndSyncWithMainForm();
        };

        _miExcludeLastApp = new ToolStripMenuItem("Не исправлять в активном приложении")
        {
            Enabled = false
        };
        _miExcludeLastApp.Click += (_, _) => ExcludeLastExternalProcess();

        var miStartup = new ToolStripMenuItem("Запускать вместе с Windows")
        {
            Checked = StartupManager.IsStartupEnabled(),
            CheckOnClick = true
        };
        miStartup.CheckedChanged += (_, _) => StartupManager.SetStartupEnabled(miStartup.Checked);

        var miExit = new ToolStripMenuItem("Выход из AutoZamena");
        miExit.Click += (_, _) => ExitCleanly();

        menu.Items.Add(miOpen);
        menu.Items.Add(_miTogglePause);
        menu.Items.Add(_miSessionStats);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miAutoLayoutSwitch);
        menu.Items.Add(_miSingleShift);
        menu.Items.Add(_miSwitchSound);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miExcludeLastApp);
        menu.Items.Add(miStartup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainForm();

        _currentDetectedLayout = AtomicTextInjector.GetActiveLanguageTag();
        RefreshTrayVisuals();

        // Lightweight poll keeping the RU / EN tray badge in sync with foreground window layout
        _layoutPollTimer = new System.Windows.Forms.Timer { Interval = 450 };
        _layoutPollTimer.Tick += (_, _) =>
        {
            if (!_config.ShowLanguageBadgeInTray)
            {
                return;
            }
            TargetLanguage lang = AtomicTextInjector.GetActiveLanguageTag();
            if (lang != _currentDetectedLayout)
            {
                _currentDetectedLayout = lang;
                RefreshTrayVisuals();
            }
        };
        _layoutPollTimer.Start();

        // Wire hook events (all marshaled asynchronously onto UI thread so WH_KEYBOARD_LL never blocks)
        _hook.PauseToggledByHotkey += OnPauseToggledByHotkey;
        _hook.CorrectionApplied += OnCorrectionApplied;
        _hook.ContextStatusChanged += OnContextStatusChanged;
        _hook.Start();

        if (activateSignal is not null)
        {
            _activateWaitRegistration = ThreadPool.RegisterWaitForSingleObject(
                activateSignal,
                (_, _) => _uiSyncContext.Post(_ => ShowMainForm(), null),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false
            );
        }

        if (!startMinimized)
        {
            ShowMainForm();
        }
    }

    private void ShowMainForm()
    {
        if (!_mainForm.Visible)
        {
            _mainForm.Show();
        }

        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }

        _mainForm.Activate();
        _mainForm.BringToFront();
    }

    private void SaveAndSyncWithMainForm()
    {
        _hook.UpdateConfig(_config);
        _config.Save();
        RefreshTrayVisuals();
        _mainForm.ReloadFromConfigExternal();
    }

    private void TogglePauseFromMenu()
    {
        _config.Engine.Enabled = !_config.Engine.Enabled;
        SaveAndSyncWithMainForm();
    }

    private void OnPauseToggledByHotkey()
    {
        _uiSyncContext.Post(_ =>
        {
            _config.Save();
            RefreshTrayVisuals();
            _mainForm.RefreshStatusBanner();

            if (_config.ShowTrayNotifications)
            {
                bool active = _config.Engine.Enabled;
                string pauseHint = GetPauseShortcutLabel();
                _trayIcon.ShowBalloonTip(
                    1800,
                    active ? "AutoZamena включена" : "AutoZamena на паузе",
                    active
                        ? "Автозамена снова исправляет опечатки и раскладку на лету."
                        : $"Автозамена приостановлена ({pauseHint} — включить обратно).",
                    ToolTipIcon.Info
                );
            }
        }, null);
    }

    private void OnCorrectionApplied(CorrectionHistoryEntry entry)
    {
        _uiSyncContext.Post(_ =>
        {
            if (!entry.WasUndone)
            {
                _sessionFixedCount++;
                _miSessionStats.Text = $"Исправлено за сессию: {_sessionFixedCount}";
            }
            _currentDetectedLayout = AtomicTextInjector.GetActiveLanguageTag();
            RefreshTrayVisuals();
            _mainForm.AppendHistoryEntry(entry);
        }, null);
    }

    private void OnContextStatusChanged(ContextStatus status, string procName)
    {
        _uiSyncContext.Post(_ =>
        {
            _currentContextStatus = status;
            _currentDetectedLayout = AtomicTextInjector.GetActiveLanguageTag();
            RefreshTrayVisuals();

            if (!string.IsNullOrWhiteSpace(procName) &&
                !string.Equals(procName, "AutoZamena", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(procName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                _lastExternalProcess = procName;
                _miExcludeLastApp.Enabled = true;
                _miExcludeLastApp.Text = $"Добавить «{procName}» в чёрный список";
            }
        }, null);
    }

    private void ExcludeLastExternalProcess()
    {
        if (string.IsNullOrWhiteSpace(_lastExternalProcess))
        {
            return;
        }

        if (!_config.BlacklistedProcesses.Any(p => string.Equals(p, _lastExternalProcess, StringComparison.OrdinalIgnoreCase)))
        {
            _config.BlacklistedProcesses.Add(_lastExternalProcess);
            _config.Save();
            _hook.UpdateConfig(_config);
            _mainForm.RefreshExternalProcessFilter();
            _trayIcon.ShowBalloonTip(
                1800,
                "Приложение добавлено в исключения",
                $"В программе «{_lastExternalProcess}» автозамена больше не будет срабатывать.",
                ToolTipIcon.Info
            );
        }
    }

    private void OnConfigUpdatedFromUi()
    {
        _hook.UpdateConfig(_config);
        RefreshTrayVisuals();
    }

    private string GetPauseShortcutLabel()
    {
        if (_config.UseBothShiftsPause)
        {
            return "Левый + Правый Shift";
        }
        return _config.PauseToggleHotkey.ToDisplayString();
    }

    private void RefreshTrayVisuals()
    {
        _suppressTrayMenuEvents = true;
        try
        {
            _miAutoLayoutSwitch.Checked = _config.Engine.LayoutMode == LayoutSwitchMode.AutoHighConfidenceAndHotkey;
            _miSingleShift.Checked = _config.UseSingleShiftLayoutSwitch;
            _miSwitchSound.Checked = _config.PlaySoundOnAutoSwitch;
        }
        finally
        {
            _suppressTrayMenuEvents = false;
        }

        bool active = _config.Engine.Enabled;
        string pauseHint = GetPauseShortcutLabel();
        _miTogglePause.Text = active
            ? $"Поставить на паузу ({pauseHint})"
            : $"Включить автозамену ({pauseHint})";

        if (!active)
        {
            _trayIcon.Icon = _iconPaused;
            SetSafeTrayTooltip($"AutoZamena — На паузе ({pauseHint})");
            return;
        }

        string langTag = _currentDetectedLayout switch
        {
            TargetLanguage.Russian => " [RU]",
            TargetLanguage.English => " [EN]",
            _ => string.Empty
        };

        if (_currentContextStatus is ContextStatus.FilteredApp or ContextStatus.FullscreenGame or ContextStatus.PasswordField)
        {
            _trayIcon.Icon = !_config.ShowLanguageBadgeInTray ? _iconStandbyGeneric
                : _currentDetectedLayout == TargetLanguage.Russian ? _iconStandbyRu
                : _currentDetectedLayout == TargetLanguage.English ? _iconStandbyEn
                : _iconStandbyGeneric;
            SetSafeTrayTooltip($"AutoZamena{langTag} — Отключена для текущего окна");
            return;
        }

        _trayIcon.Icon = !_config.ShowLanguageBadgeInTray ? _iconActiveGeneric
            : _currentDetectedLayout == TargetLanguage.Russian ? _iconActiveRu
            : _currentDetectedLayout == TargetLanguage.English ? _iconActiveEn
            : _iconActiveGeneric;
        SetSafeTrayTooltip($"AutoZamena{langTag} — Активна (2×Shift — перевод)");
    }

    private void SetSafeTrayTooltip(string tip)
    {
        _trayIcon.Text = tip.Length <= 63 ? tip : tip[..63];
    }

    private void ExitCleanly()
    {
        _layoutPollTimer.Stop();
        _activateWaitRegistration?.Unregister(null);
        _hook.Stop();
        _trayIcon.Visible = false;
        _mainForm.RequestApplicationExit();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _layoutPollTimer.Dispose();
            _activateWaitRegistration?.Unregister(null);
            _hook.Dispose();
            _contextGuard.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _iconActiveGeneric.Dispose();
            _iconActiveRu.Dispose();
            _iconActiveEn.Dispose();
            _iconStandbyGeneric.Dispose();
            _iconStandbyRu.Dispose();
            _iconStandbyEn.Dispose();
            _iconPaused.Dispose();
            _mainForm.Dispose();
        }
        base.Dispose(disposing);
    }
}
