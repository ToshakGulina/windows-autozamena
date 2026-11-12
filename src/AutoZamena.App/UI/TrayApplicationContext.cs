using System;
using System.Drawing;
using System.Windows.Forms;
using AutoZamena.App.Config;
using AutoZamena.App.Interop;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Lexicon;

namespace AutoZamena.App.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly AutoCorrectEngine _engine;
    private readonly WindowContextGuard _contextGuard;
    private readonly AtomicTextInjector _injector;
    private readonly LowLevelInputHook _hook;

    private readonly Icon _iconActive;
    private readonly Icon _iconStandby;
    private readonly Icon _iconPaused;

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _miTogglePause;
    private readonly MainForm _mainForm;

    private ContextStatus _currentContextStatus = ContextStatus.Allowed;

    public TrayApplicationContext(bool startMinimized)
    {
        _config = AppConfig.Load();

        var userDict = new UserDictionary();
        userDict.Load(_config.IgnoredWords, _config.CustomReplacements);

        _engine = new AutoCorrectEngine(LexiconDatabase.Default, userDict, _config.Engine);
        _contextGuard = new WindowContextGuard();
        _injector = new AtomicTextInjector();
        _hook = new LowLevelInputHook(_engine, _config, _contextGuard, _injector);

        _iconActive = TrayIconGenerator.CreateIcon(TrayVisualState.Active, 32);
        _iconStandby = TrayIconGenerator.CreateIcon(TrayVisualState.StandbyFilteredApp, 32);
        _iconPaused = TrayIconGenerator.CreateIcon(TrayVisualState.Paused, 32);

        _mainForm = new MainForm(_config, _engine, _hook, _iconActive, RefreshTrayState);
        _contextGuard.IsOwnSandboxFocused = () => _mainForm.IsSandboxInputFocused;

        var menu = new ContextMenuStrip();
        var miOpen = new ToolStripMenuItem("Настройки и Песочница…", null, (_, _) => ShowMainWindow())
        {
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        _miTogglePause = new ToolStripMenuItem("Поставить на паузу", null, (_, _) => TogglePauseFromMenu());
        var miExit = new ToolStripMenuItem("Выход", null, (_, _) => ExitApp());

        menu.Items.Add(miOpen);
        menu.Items.Add(_miTogglePause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);

        _notifyIcon = new NotifyIcon
        {
            Icon = _iconActive,
            Text = "AutoZamena — Активна",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        _hook.PauseToggledByHotkey += () =>
        {
            _config.Save();
            _mainForm.RefreshStatusBanner();
            RefreshTrayState();

            if (_config.ShowTrayNotifications)
            {
                _notifyIcon.ShowBalloonTip(
                    1500,
                    "AutoZamena",
                    _config.Engine.Enabled ? "Автозамена снова включена." : "Автозамена поставлена на паузу.",
                    ToolTipIcon.Info
                );
            }
        };

        _hook.CorrectionApplied += entry => _mainForm.AppendHistoryEntry(entry);

        _hook.ContextStatusChanged += (status, _) =>
        {
            _currentContextStatus = status;
            if (_mainForm.IsHandleCreated)
            {
                _mainForm.BeginInvoke(RefreshTrayState);
            }
            else
            {
                RefreshTrayState();
            }
        };

        RefreshTrayState();
        _hook.Start();

        if (!startMinimized)
        {
            ShowMainWindow();
        }
    }

    private void ShowMainWindow()
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
    }

    private void TogglePauseFromMenu()
    {
        _config.Engine.Enabled = !_config.Engine.Enabled;
        _engine.Settings.Enabled = _config.Engine.Enabled;
        _engine.ResetContext();
        _config.Save();
        _mainForm.RefreshStatusBanner();
        RefreshTrayState();
    }

    private void RefreshTrayState()
    {
        bool enabled = _config.Engine.Enabled;
        _miTogglePause.Text = enabled ? "Поставить на паузу" : "Включить автозамену";

        if (!enabled)
        {
            _notifyIcon.Icon = _iconPaused;
            _notifyIcon.Text = "AutoZamena — На паузе";
            return;
        }

        if (_currentContextStatus is ContextStatus.FilteredApp or ContextStatus.FullscreenGame or ContextStatus.PasswordField)
        {
            _notifyIcon.Icon = _iconStandby;
            _notifyIcon.Text = "AutoZamena — Отключена для текущего окна";
            return;
        }

        _notifyIcon.Icon = _iconActive;
        _notifyIcon.Text = "AutoZamena — Активна";
    }

    private void ExitApp()
    {
        _notifyIcon.Visible = false;
        _hook.Dispose();
        _injector.Dispose();
        _contextGuard.Dispose();
        _mainForm.RequestApplicationExit();

        _notifyIcon.Dispose();
        _iconActive.Dispose();
        _iconStandby.Dispose();
        _iconPaused.Dispose();

        ExitThread();
    }
}
