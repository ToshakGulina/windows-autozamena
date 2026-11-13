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
/// Hosts the persistent Windows system tray icon, context menu, settings window, and global input hooks.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _uiSyncContext;
    private readonly AppConfig _config;
    private readonly AutoCorrectEngine _engine;
    private readonly WindowContextGuard _contextGuard;
    private readonly LowLevelInputHook _hook;

    private readonly Icon _iconActive;
    private readonly Icon _iconStandby;
    private readonly Icon _iconPaused;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _miSessionStats;
    private readonly ToolStripMenuItem _miTogglePause;
    private readonly ToolStripMenuItem _miExcludeLastApp;
    private readonly MainForm _mainForm;
    private readonly RegisteredWaitHandle? _activateWaitRegistration;

    private ContextStatus _currentContextStatus = ContextStatus.Allowed;
    private string _lastExternalProcess = string.Empty;
    private int _sessionFixedCount;

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

        _iconActive = TrayIconGenerator.CreateIcon(TrayVisualState.Active);
        _iconStandby = TrayIconGenerator.CreateIcon(TrayVisualState.StandbyFilteredApp);
        _iconPaused = TrayIconGenerator.CreateIcon(TrayVisualState.Paused);

        _mainForm = new MainForm(
            _config,
            _engine,
            _hook,
            _iconActive,
            onConfigUpdated: OnConfigUpdatedFromUi
        );

        _contextGuard.IsOwnSandboxFocused = () => _mainForm.IsSandboxInputFocused;

        // Build Tray Context Menu
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

        RefreshTrayVisuals();

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

    private void TogglePauseFromMenu()
    {
        _config.Engine.Enabled = !_config.Engine.Enabled;
        _hook.UpdateConfig(_config);
        _config.Save();
        RefreshTrayVisuals();
        _mainForm.RefreshStatusBanner();
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
                _trayIcon.ShowBalloonTip(
                    1800,
                    active ? "AutoZamena включена" : "AutoZamena на паузе",
                    active
                        ? "Автозамена снова исправляет опечатки на лету."
                        : $"Автозамена приостановлена ({_config.PauseToggleHotkey.ToDisplayString()} — включить обратно).",
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
            _mainForm.AppendHistoryEntry(entry);
        }, null);
    }

    private void OnContextStatusChanged(ContextStatus status, string procName)
    {
        _uiSyncContext.Post(_ =>
        {
            _currentContextStatus = status;
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

    private void RefreshTrayVisuals()
    {
        bool active = _config.Engine.Enabled;
        string hk = _config.PauseToggleHotkey.ToDisplayString();
        _miTogglePause.Text = active
            ? $"Поставить на паузу ({hk})"
            : $"Включить автозамену ({hk})";

        if (!active)
        {
            _trayIcon.Icon = _iconPaused;
            SetSafeTrayTooltip($"AutoZamena — На паузе ({hk})");
            return;
        }

        if (_currentContextStatus is ContextStatus.FilteredApp or ContextStatus.FullscreenGame or ContextStatus.PasswordField)
        {
            _trayIcon.Icon = _iconStandby;
            SetSafeTrayTooltip("AutoZamena — Отключена для текущего окна");
            return;
        }

        _trayIcon.Icon = _iconActive;
        SetSafeTrayTooltip($"AutoZamena — Активна ({hk})");
    }

    private void SetSafeTrayTooltip(string tip)
    {
        _trayIcon.Text = tip.Length <= 63 ? tip : tip[..63];
    }

    private void ExitCleanly()
    {
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
            _activateWaitRegistration?.Unregister(null);
            _hook.Dispose();
            _contextGuard.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _iconActive.Dispose();
            _iconStandby.Dispose();
            _iconPaused.Dispose();
            _mainForm.Dispose();
        }
        base.Dispose(disposing);
    }
}
