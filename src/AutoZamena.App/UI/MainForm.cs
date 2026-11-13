using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AutoZamena.App.Config;
using AutoZamena.App.Interop;
using AutoZamena.Core.Engine;
using AutoZamena.Core.Models;

namespace AutoZamena.App.UI;

internal sealed class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly AutoCorrectEngine _engine;
    private readonly LowLevelInputHook _hook;
    private readonly Action _onConfigUpdated;

    private bool _suppressEvents;
    private bool _allowRealClose;

    // Header banner controls
    private TableLayoutPanel _headerPanel = null!;
    private Label _lblStatusTitle = null!;
    private Label _lblStatusSub = null!;
    private Button _btnToggleEnabled = null!;
    private bool _dictionarySplitInitialized;

    // Tab 1: General & Mechanics
    private ComboBox _cmbSensitivity = null!;
    private CheckBox _chkFixTypos = null!;
    private CheckBox _chkFixDoubleCaps = null!;
    private CheckBox _chkFixInvertedCaps = null!;
    private CheckBox _chkFixRollover = null!;
    private CheckBox _chkAutoCapSentence = null!;
    private CheckBox _chkAutoCapFirst = null!;
    private CheckBox _chkDoubleSpacePeriod = null!;
    private CheckBox _chkSmartTypography = null!;
    private CheckBox _chkUndoBackspace = null!;
    private CheckBox _chkAutoLearnUndo = null!;
    private ComboBox _cmbLayoutMode = null!;
    private CheckBox _chkSwitchOsLayout = null!;
    private CheckBox _chkPlaySound = null!;
    private CheckBox _chkShowLangBadge = null!;
    private CheckBox _chkStartup = null!;
    private CheckBox _chkNotifications = null!;

    // Tab 2: Window Filter
    private RadioButton _rbBlacklist = null!;
    private RadioButton _rbWhitelist = null!;
    private CheckBox _chkFullscreenGames = null!;
    private CheckBox _chkPasswordFields = null!;
    private ListBox _lstProcesses = null!;
    private TextBox _txtNewProcess = null!;

    // Tab 3: User Dictionary
    private ListBox _lstIgnoredWords = null!;
    private TextBox _txtNewIgnored = null!;
    private ListView _lvCustomSnippets = null!;
    private TextBox _txtSnippetTrigger = null!;
    private TextBox _txtSnippetReplacement = null!;

    // Tab 4: Hotkeys (Caramba gestures + customizable bindings)
    private CheckBox _chkDoubleShiftConvert = null!;
    private CheckBox _chkBothShiftsPause = null!;
    private CheckBox _chkSingleShiftSwitch = null!;
    private CheckBox _chkDoubleCtrlLine = null!;
    private Label _lblCycleCaseHotkey = null!;
    private Label _lblTranslitHotkey = null!;
    private Label _lblPauseHotkey = null!;
    private Label _lblLayoutHotkey = null!;

    // Tab 5: Sandbox & History
    private TextBox _txtSandbox = null!;
    private ListView _lvHistory = null!;
    private int _totalCorrectionsCount;

    public bool IsSandboxInputFocused => Visible && WindowState != FormWindowState.Minimized && _txtSandbox is { Focused: true };

    public MainForm(
        AppConfig config,
        AutoCorrectEngine engine,
        LowLevelInputHook hook,
        Icon appIcon,
        Action onConfigUpdated)
    {
        _config = config;
        _engine = engine;
        _hook = hook;
        _onConfigUpdated = onConfigUpdated;

        Icon = appIcon;
        Text = "AutoZamena — Умная автозамена для Windows";
        Size = new Size(820, 650);
        MinimumSize = new Size(740, 580);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(246, 248, 250);

        BuildLayout();
        LoadValuesFromConfig();

        _engine.UserDictionary.Changed += OnUserDictionaryChangedSafe;
    }

    public void RequestApplicationExit()
    {
        _allowRealClose = true;
        Close();
    }

    public void RefreshStatusBanner()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshStatusBanner);
            return;
        }

        bool enabled = _config.Engine.Enabled;
        _headerPanel.BackColor = enabled
            ? Color.FromArgb(232, 248, 240)
            : Color.FromArgb(254, 243, 226);

        _lblStatusTitle.ForeColor = enabled
            ? Color.FromArgb(14, 116, 70)
            : Color.FromArgb(165, 92, 18);

        _lblStatusTitle.Text = enabled
            ? "Автозамена активна — печатайте свободно без остановок"
            : "Автозамена на паузе";

        string pauseHint = _config.UseBothShiftsPause
            ? "Левый+Правый Shift"
            : _config.PauseToggleHotkey.ToDisplayString();
        string convertHint = _config.UseDoubleShiftConvert
            ? "Двойной Shift (слово / выделение)"
            : _config.ConvertLayoutHotkey.ToDisplayString();

        _lblStatusSub.Text =
            $"Перевод раскладки: {convertHint}   |   " +
            $"Пауза: {pauseHint}   |   " +
            $"Исправлено за сессию: {_totalCorrectionsCount}";

        _btnToggleEnabled.Text = enabled ? "Поставить на паузу" : "Включить";
    }

    public void ReloadFromConfigExternal()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ReloadFromConfigExternal);
            return;
        }
        LoadValuesFromConfig();
    }

    public void AppendHistoryEntry(CorrectionHistoryEntry entry)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendHistoryEntry(entry));
            return;
        }

        if (!entry.WasUndone)
        {
            _totalCorrectionsCount++;
        }
        RefreshStatusBanner();

        var item = new ListViewItem(entry.Timestamp.ToString("HH:mm:ss")) { Tag = entry };
        item.SubItems.Add(entry.OriginalText);
        item.SubItems.Add(entry.ReplacementText);
        item.SubItems.Add(entry.Reason);
        item.SubItems.Add(entry.ProcessName);

        _lvHistory.Items.Insert(0, item);
        while (_lvHistory.Items.Count > 200)
        {
            _lvHistory.Items.RemoveAt(_lvHistory.Items.Count - 1);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _hook.HotkeyRecorderInterceptor = null;
        if (!_allowRealClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _engine.UserDictionary.Changed -= OnUserDictionaryChangedSafe;
        base.OnFormClosing(e);
    }

    private void OnUserDictionaryChangedSafe()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(OnUserDictionaryChangedSafe);
            return;
        }

        SyncDictionaryToConfigAndSave();
        RefreshDictionaryLists();
    }

    public void RefreshExternalProcessFilter()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshExternalProcessFilter);
            return;
        }
        RefreshProcessListBox();
    }

    private void BuildLayout()
    {
        // High-DPI-safe top status banner using 2-column TableLayoutPanel
        _headerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MinimumSize = new Size(0, 74),
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(18, 12, 18, 12)
        };
        _headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _headerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lblStatusTitle = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 12, 3),
            Font = new Font("Segoe UI", 11.5f, FontStyle.Bold)
        };

        _lblStatusSub = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 12, 0),
            ForeColor = Color.FromArgb(80, 90, 102),
            Font = new Font("Segoe UI", 9f)
        };

        _btnToggleEnabled = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(165, 38),
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8, 2, 0, 2),
            FlatStyle = FlatStyle.System
        };
        _btnToggleEnabled.Click += (_, _) =>
        {
            _config.Engine.Enabled = !_config.Engine.Enabled;
            _engine.Settings.Enabled = _config.Engine.Enabled;
            _engine.ResetContext();
            SaveAndNotify();
            RefreshStatusBanner();
        };

        _headerPanel.Controls.Add(_lblStatusTitle, 0, 0);
        _headerPanel.Controls.Add(_lblStatusSub, 0, 1);
        _headerPanel.Controls.Add(_btnToggleEnabled, 1, 0);
        _headerPanel.SetRowSpan(_btnToggleEnabled, 2);

        // Main TabControl
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(12, 6)
        };

        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildAppsFilterTab());
        tabs.TabPages.Add(BuildDictionaryTab());
        tabs.TabPages.Add(BuildHotkeysTab());
        tabs.TabPages.Add(BuildSandboxHistoryTab());

        var contentWrapper = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 10, 12, 12)
        };
        contentWrapper.Controls.Add(tabs);

        Controls.Add(contentWrapper);
        Controls.Add(_headerPanel);
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("Основные") { BackColor = Color.White, Padding = new Padding(18) };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        // Sensitivity row
        var sensPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 12)
        };
        sensPanel.Controls.Add(new Label
        {
            Text = "Чувствительность автозамены:",
            AutoSize = true,
            Padding = new Padding(0, 5, 10, 0),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        });
        _cmbSensitivity = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 340
        };
        _cmbSensitivity.Items.AddRange(new object[]
        {
            "Бережная (только 100% очевидные промахи)",
            "Сбалансированная — как на смартфоне (рекомендуется)",
            "Активная (исправлять больше сложных опечаток)"
        });
        _cmbSensitivity.SelectedIndexChanged += (_, _) => ApplyGeneralControls();
        sensPanel.Controls.Add(_cmbSensitivity);
        flow.Controls.Add(sensPanel);

        // Group: Core typing mechanics
        flow.Controls.Add(CreateSectionHeader("Механика печати на лету"));

        _chkFixTypos = CreateOptionCheck("Исправлять опечатки по геометрии клавиш, таймингам нажатий и контексту слов");
        _chkFixDoubleCaps = CreateOptionCheck("Исправлять залипший Shift в начале слова (ПРивет → Привет)");
        _chkFixInvertedCaps = CreateOptionCheck("Исправлять случайный Caps Lock (пРИВЕТ → Привет) и автоматически выключать Caps Lock");
        _chkFixRollover = CreateOptionCheck("Склеивать слова, разорванные ранним пробелом (приве т → привет)");
        _chkAutoCapSentence = CreateOptionCheck("Автоматически делать заглавной букву после точки, ! и ? (. на → . На, с защитой т. д. / г.)");
        _chkAutoCapFirst = CreateOptionCheck("Делать заглавной первую букву в самом начале нового ввода");
        _chkDoubleSpacePeriod = CreateOptionCheck("Ставить точку с пробелом по двойному нажатию пробела — как на смартфоне (привет␣␣ → привет. )");
        _chkSmartTypography = CreateOptionCheck("Умная типографика: заменять двойной дефис между пробелами на длинное тире ( -- → — )");
        _chkUndoBackspace = CreateOptionCheck("Мгновенный откат автозамены одним нажатием Backspace сразу после пробела");
        _chkAutoLearnUndo = CreateOptionCheck("Самообучение (как в Caramba): запоминать отменённые слова в личный список исключений");

        flow.Controls.AddRange(new Control[]
        {
            _chkFixTypos, _chkFixDoubleCaps, _chkFixInvertedCaps, _chkFixRollover,
            _chkAutoCapSentence, _chkAutoCapFirst, _chkDoubleSpacePeriod, _chkSmartTypography,
            _chkUndoBackspace, _chkAutoLearnUndo
        });

        // Group: Wrong layout
        flow.Controls.Add(CreateSectionHeader("Забытая раскладка клавиатуры (ghbdtn ↔ привет, пщщпдуюсщь → google.com)"));

        var layoutRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 2, 0, 6)
        };
        layoutRow.Controls.Add(new Label
        {
            Text = "Режим:",
            AutoSize = true,
            Padding = new Padding(0, 5, 8, 0)
        });
        _cmbLayoutMode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 445
        };
        _cmbLayoutMode.Items.AddRange(new object[]
        {
            "Выключено",
            "Только вручную по Двойному Shift / горячей клавише",
            "Автоматически (слова, фразы z yt pyf., знаки ghbdtn?, адреса) + Двойной Shift"
        });
        _cmbLayoutMode.SelectedIndexChanged += (_, _) => ApplyGeneralControls();
        layoutRow.Controls.Add(_cmbLayoutMode);
        flow.Controls.Add(layoutRow);

        _chkSwitchOsLayout = CreateOptionCheck("Переключать системную раскладку активного окна после автоисправления слова");
        _chkPlaySound = CreateOptionCheck("Лёгкий звуковой щелчок при автоматическом переключении раскладки");
        flow.Controls.Add(_chkSwitchOsLayout);
        flow.Controls.Add(_chkPlaySound);

        // Group: System
        flow.Controls.Add(CreateSectionHeader("Система и трей"));
        _chkShowLangBadge = CreateOptionCheck("Показывать активную раскладку (RU / EN) цветом и буквами прямо на иконке в трее");
        _chkStartup = CreateOptionCheck("Запускать AutoZamena автоматически вместе с Windows (в трее)");
        _chkStartup.CheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            StartupManager.SetStartupEnabled(_chkStartup.Checked);
        };
        _chkNotifications = CreateOptionCheck("Показывать всплывающую подсказку в трее при переключении паузы");
        flow.Controls.Add(_chkShowLangBadge);
        flow.Controls.Add(_chkStartup);
        flow.Controls.Add(_chkNotifications);

        page.Controls.Add(flow);
        return page;
    }

    private TabPage BuildAppsFilterTab()
    {
        var page = new TabPage("Приложения и Игры") { BackColor = Color.White, Padding = new Padding(16) };

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 128,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };

        _rbBlacklist = new RadioButton
        {
            Text = "Чёрный список — работать везде, КРОМЕ выбранных программ (терминалы, IDE и др.)",
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 4)
        };
        _rbWhitelist = new RadioButton
        {
            Text = "Белый список — работать ТОЛЬКО в выбранных программах (браузеры, мессенджеры и др.)",
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 8)
        };
        _rbBlacklist.CheckedChanged += (_, _) => OnFilterModeChanged();
        _rbWhitelist.CheckedChanged += (_, _) => OnFilterModeChanged();

        _chkFullscreenGames = new CheckBox
        {
            Text = "Автоматически отключать в полноэкранных 3D-играх (защита WASD и античитов)",
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 4)
        };
        _chkFullscreenGames.CheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _config.AutoDisableInFullscreenGames = _chkFullscreenGames.Checked;
            SaveAndNotify();
        };

        _chkPasswordFields = new CheckBox
        {
            Text = "Автоматически отключать в полях ввода паролей (определение через UI Automation)",
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 4)
        };
        _chkPasswordFields.CheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _config.AutoDisableInPasswordFields = _chkPasswordFields.Checked;
            SaveAndNotify();
        };

        topPanel.Controls.AddRange(new Control[] { _rbBlacklist, _rbWhitelist, _chkFullscreenGames, _chkPasswordFields });

        _lstProcesses = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false
        };

        var bottomBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0)
        };

        _txtNewProcess = new TextBox { Width = 190, PlaceholderText = "Например: Code или cmd" };
        _txtNewProcess.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddProcessToActiveList(_txtNewProcess.Text);
                _txtNewProcess.Clear();
            }
        };

        var btnAddManual = new Button { Text = "Добавить", Width = 95, Height = 29 };
        btnAddManual.Click += (_, _) =>
        {
            AddProcessToActiveList(_txtNewProcess.Text);
            _txtNewProcess.Clear();
        };

        var btnPickRunning = new Button { Text = "Из открытых окон…", Width = 155, Height = 29 };
        btnPickRunning.Click += (_, _) =>
        {
            using var dlg = new ProcessPickerDialog();
            if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedProcessName))
            {
                AddProcessToActiveList(dlg.SelectedProcessName);
            }
        };

        var btnRemove = new Button { Text = "Удалить", Width = 95, Height = 29 };
        btnRemove.Click += (_, _) =>
        {
            if (_lstProcesses.SelectedItem is string sel)
            {
                ActiveProcessList().RemoveAll(x => string.Equals(x, sel, StringComparison.OrdinalIgnoreCase));
                RefreshProcessListBox();
                SaveAndNotify();
            }
        };

        var btnResetDefaults = new Button { Text = "По умолчанию", Width = 125, Height = 29 };
        btnResetDefaults.Click += (_, _) =>
        {
            if (_config.FilterMode == WindowFilterMode.Blacklist)
            {
                _config.BlacklistedProcesses = AppConfig.CreateDefaultBlacklist();
            }
            else
            {
                _config.WhitelistedProcesses = AppConfig.CreateDefaultWhitelist();
            }
            RefreshProcessListBox();
            SaveAndNotify();
        };

        bottomBar.Controls.AddRange(new Control[]
        {
            _txtNewProcess, btnAddManual, btnPickRunning, btnRemove, btnResetDefaults
        });

        page.Controls.Add(_lstProcesses);
        page.Controls.Add(topPanel);
        page.Controls.Add(bottomBar);
        return page;
    }

    private TabPage BuildDictionaryTab()
    {
        var page = new TabPage("Личный словарь") { BackColor = Color.White, Padding = new Padding(14) };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical
        };
        split.SizeChanged += (_, _) =>
        {
            if (!_dictionarySplitInitialized && split.ClientSize.Width > 420)
            {
                _dictionarySplitInitialized = true;
                split.Panel1MinSize = 240;
                split.Panel2MinSize = 280;
                split.SplitterDistance = (int)(split.ClientSize.Width * 0.42);
            }
        };

        // Left: Ignored / Whitelisted words
        var leftHeader = new Label
        {
            Text = "Исключения (слова, которые нельзя трогать):",
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };

        _lstIgnoredWords = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

        var leftBottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 6, 0, 0)
        };
        _txtNewIgnored = new TextBox { Width = 140, PlaceholderText = "Слово / сленг" };
        void SubmitIgnored()
        {
            if (!string.IsNullOrWhiteSpace(_txtNewIgnored.Text))
            {
                _engine.UserDictionary.AddIgnoredWord(_txtNewIgnored.Text);
                _txtNewIgnored.Clear();
            }
        }
        _txtNewIgnored.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SubmitIgnored();
            }
        };
        var btnAddIgnored = new Button { Text = "+", Width = 42, Height = 28 };
        btnAddIgnored.Click += (_, _) => SubmitIgnored();
        var btnDelIgnored = new Button { Text = "Удалить", Width = 85, Height = 28 };
        btnDelIgnored.Click += (_, _) =>
        {
            if (_lstIgnoredWords.SelectedItem is string w)
            {
                _engine.UserDictionary.RemoveIgnoredWord(w);
            }
        };
        leftBottom.Controls.AddRange(new Control[] { _txtNewIgnored, btnAddIgnored, btnDelIgnored });

        split.Panel1.Controls.Add(_lstIgnoredWords);
        split.Panel1.Controls.Add(leftHeader);
        split.Panel1.Controls.Add(leftBottom);

        // Right: Custom Snippets / Triggers
        var rightHeader = new Label
        {
            Text = "Свои автозамены и сокращения (триггер → замена):",
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };

        _lvCustomSnippets = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _lvCustomSnippets.Columns.Add("Сокращение", 125);
        _lvCustomSnippets.Columns.Add("Заменять на", 265);

        var rightBottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 6, 0, 0)
        };
        _txtSnippetTrigger = new TextBox { Width = 95, PlaceholderText = "Напр.: тг" };
        _txtSnippetReplacement = new TextBox { Width = 165, PlaceholderText = "Напр.: Telegram" };
        void SubmitSnippet()
        {
            if (!string.IsNullOrWhiteSpace(_txtSnippetTrigger.Text) &&
                !string.IsNullOrEmpty(_txtSnippetReplacement.Text))
            {
                _engine.UserDictionary.SetCustomReplacement(_txtSnippetTrigger.Text, _txtSnippetReplacement.Text);
                _txtSnippetTrigger.Clear();
                _txtSnippetReplacement.Clear();
                _txtSnippetTrigger.Focus();
            }
        }
        _txtSnippetReplacement.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SubmitSnippet();
            }
        };
        var btnAddSnippet = new Button { Text = "Сохранить", Width = 90, Height = 28 };
        btnAddSnippet.Click += (_, _) => SubmitSnippet();
        var btnDelSnippet = new Button { Text = "Удалить", Width = 80, Height = 28 };
        btnDelSnippet.Click += (_, _) =>
        {
            if (_lvCustomSnippets.SelectedItems.Count > 0)
            {
                string trig = _lvCustomSnippets.SelectedItems[0].Text;
                _engine.UserDictionary.RemoveCustomReplacement(trig);
            }
        };
        rightBottom.Controls.AddRange(new Control[]
        {
            _txtSnippetTrigger, _txtSnippetReplacement, btnAddSnippet, btnDelSnippet
        });

        split.Panel2.Controls.Add(_lvCustomSnippets);
        split.Panel2.Controls.Add(rightHeader);
        split.Panel2.Controls.Add(rightBottom);

        var dictToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 6, 0, 0)
        };

        var btnImportDict = new Button { Text = "Импорт словаря…", Width = 145, Height = 28 };
        btnImportDict.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Импорт личного словаря AutoZamena",
                Filter = "JSON файлы (*.json)|*.json|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    (int ign, int snip) = AppConfig.ImportPersonalDictionary(dlg.FileName, _engine.UserDictionary);
                    SyncDictionaryToConfigAndSave();
                    RefreshDictionaryLists();
                    MessageBox.Show(
                        this,
                        $"Импорт завершён: добавлено исключений — {ign}, своих автозамен — {snip}.",
                        "Импорт словаря",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Не удалось прочитать файл словаря: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        };

        var btnExportDict = new Button { Text = "Экспорт словаря…", Width = 145, Height = 28 };
        btnExportDict.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Экспорт личного словаря AutoZamena",
                Filter = "JSON файлы (*.json)|*.json",
                FileName = "autozamena-dictionary.json"
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                try
                {
                    AppConfig.ExportPersonalDictionary(dlg.FileName, _engine.UserDictionary);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Не удалось сохранить файл словаря: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        };

        var lblMacroHint = new Label
        {
            Text = "Макросы в заменах: {{date}}, {{time}}, {{iso}}, {{clipboard}} и позиция курсора «|»",
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 110, 122),
            Padding = new Padding(0, 6, 14, 0)
        };

        dictToolbar.Controls.Add(btnImportDict);
        dictToolbar.Controls.Add(btnExportDict);
        dictToolbar.Controls.Add(lblMacroHint);

        page.Controls.Add(split);
        page.Controls.Add(dictToolbar);
        return page;
    }

    private TabPage BuildHotkeysTab()
    {
        var page = new TabPage("Горячие клавиши") { BackColor = Color.White, Padding = new Padding(18) };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        flow.Controls.Add(CreateSectionHeader("Управление как в Caramba Switcher (без Pause/Break — удобно на ноутбуках и 75%/65% клавиатурах)"));

        _chkDoubleShiftConvert = CreateOptionCheck(
            "Двойной Shift (2× Shift) — перевести последнее слово, отменить автопереключение или перевести ВЫДЕЛЕННЫЙ текст"
        );
        _chkBothShiftsPause = CreateOptionCheck(
            "Левый Shift + Правый Shift (одновременно) — мгновенно поставить AutoZamena на паузу / включить обратно"
        );
        _chkSingleShiftSwitch = CreateOptionCheck(
            "Одиночный Shift (Single Shift) — переключать активную раскладку клавиатуры одним коротким нажатием Shift"
        );
        _chkDoubleCtrlLine = CreateOptionCheck(
            "Двойной Ctrl (2× Ctrl) — перевести всю набранную фразу на текущей строке без выделения и без буфера обмена"
        );

        flow.Controls.AddRange(new Control[]
        {
            _chkDoubleShiftConvert,
            _chkBothShiftsPause,
            _chkSingleShiftSwitch,
            _chkDoubleCtrlLine
        });

        flow.Controls.Add(CreateSectionHeader("Операции с выделенным текстом (регистр КАПСЛОК и транслит)"));

        flow.Controls.Add(BuildCustomBindingRow(
            "Сменить регистр выделенного текста (строчные → Обычный → ПРОПИСНЫЕ):",
            out _lblCycleCaseHotkey,
            onRecord: lbl => StartRecordingBinding(lbl, b => _config.CycleCaseHotkey = b),
            onClearOrReset: () =>
            {
                _config.CycleCaseHotkey = new HotkeyBinding(true, true, false, 0x4B);
                RefreshHotkeyLabels();
                SaveAndNotify();
            },
            resetText: "Сброс (Ctrl+Alt+K)"
        ));

        flow.Controls.Add(BuildCustomBindingRow(
            "Транслитерация выделенного текста (Привет ↔ Privet):",
            out _lblTranslitHotkey,
            onRecord: lbl => StartRecordingBinding(lbl, b => _config.TransliterateHotkey = b),
            onClearOrReset: () =>
            {
                _config.TransliterateHotkey = new HotkeyBinding(true, true, false, 0x54);
                RefreshHotkeyLabels();
                SaveAndNotify();
            },
            resetText: "Сброс (Ctrl+Alt+T)"
        ));

        flow.Controls.Add(CreateSectionHeader("Дополнительные пользовательские сочетания клавиш"));

        flow.Controls.Add(BuildCustomBindingRow(
            "Дополнительная клавиша паузы / включения:",
            out _lblPauseHotkey,
            onRecord: lbl => StartRecordingBinding(lbl, b => _config.PauseToggleHotkey = b),
            onClearOrReset: () =>
            {
                _config.PauseToggleHotkey = new HotkeyBinding();
                RefreshHotkeyLabels();
                RefreshStatusBanner();
                SaveAndNotify();
            },
            resetText: "Очистить"
        ));

        flow.Controls.Add(BuildCustomBindingRow(
            "Дополнительная клавиша перевода раскладки слова (необязательно, работает вместе с 2× Shift):",
            out _lblLayoutHotkey,
            onRecord: lbl => StartRecordingBinding(lbl, b => _config.ConvertLayoutHotkey = b),
            onClearOrReset: () =>
            {
                _config.ConvertLayoutHotkey = new HotkeyBinding();
                RefreshHotkeyLabels();
                RefreshStatusBanner();
                SaveAndNotify();
            },
            resetText: "Очистить"
        ));

        page.Controls.Add(flow);
        return page;
    }

    private static Control BuildCustomBindingRow(
        string caption,
        out Label valueLabel,
        Action<Label> onRecord,
        Action onClearOrReset,
        string resetText)
    {
        var container = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 6)
        };

        container.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = Color.FromArgb(80, 90, 102),
            Margin = new Padding(0, 0, 0, 3)
        });

        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var lbl = new Label
        {
            Width = 210,
            Height = 30,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(245, 247, 250),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 8, 0)
        };
        valueLabel = lbl;

        var btnSet = new Button { Text = "Задать…", Width = 115, Height = 30 };
        btnSet.Click += (_, _) => onRecord(lbl);

        var btnReset = new Button { Text = resetText, Width = 155, Height = 30 };
        btnReset.Click += (_, _) => onClearOrReset();

        row.Controls.AddRange(new Control[] { lbl, btnSet, btnReset });
        container.Controls.Add(row);
        return container;
    }

    private TabPage BuildSandboxHistoryTab()
    {
        var page = new TabPage("Песочница и Журнал") { BackColor = Color.White, Padding = new Padding(14) };

        var topSandboxPanel = new Panel { Dock = DockStyle.Top, Height = 155 };
        var lblSandbox = new Label
        {
            Text = "Живая песочница — кликни ниже и попробуй: «возврапщатся наза », «z yt pyf. », «пРИВЕТ », двойной пробел или 2× Shift:",
            Dock = DockStyle.Top,
            Height = 42,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };

        _txtSandbox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Segoe UI", 11f),
            BackColor = Color.FromArgb(252, 254, 255)
        };
        _txtSandbox.GotFocus += (_, _) => _engine.ResetContext();

        topSandboxPanel.Controls.Add(_txtSandbox);
        topSandboxPanel.Controls.Add(lblSandbox);

        var historyHeader = new Label
        {
            Text = "Журнал последних автозамен:",
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(0, 8, 0, 0),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };

        _lvHistory = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _lvHistory.Columns.Add("Время", 78);
        _lvHistory.Columns.Add("Набрано", 145);
        _lvHistory.Columns.Add("Исправлено на", 155);
        _lvHistory.Columns.Add("Причина", 230);
        _lvHistory.Columns.Add("Приложение", 120);

        var historyBottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 6, 0, 0)
        };
        var btnWhitelistSelected = new Button
        {
            Text = "Добавить «Набрано» в исключения",
            Width = 245,
            Height = 29
        };
        btnWhitelistSelected.Click += (_, _) =>
        {
            if (_lvHistory.SelectedItems.Count > 0 &&
                _lvHistory.SelectedItems[0].Tag is CorrectionHistoryEntry entry &&
                !entry.WasUndone &&
                !string.IsNullOrWhiteSpace(entry.OriginalText))
            {
                _engine.UserDictionary.AddIgnoredWord(entry.OriginalText);
                MessageBox.Show(
                    this,
                    $"Слово «{entry.OriginalText}» добавлено в личный белый список и больше не будет исправляться.",
                    "Добавлено в исключения",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
        };

        var btnClearHistory = new Button { Text = "Очистить журнал", Width = 140, Height = 29 };
        btnClearHistory.Click += (_, _) => _lvHistory.Items.Clear();

        var btnClearSandbox = new Button { Text = "Очистить песочницу", Width = 155, Height = 29 };
        btnClearSandbox.Click += (_, _) =>
        {
            _txtSandbox.Clear();
            _engine.ResetContext();
            _txtSandbox.Focus();
        };

        historyBottom.Controls.AddRange(new Control[] { btnWhitelistSelected, btnClearHistory, btnClearSandbox });

        page.Controls.Add(_lvHistory);
        page.Controls.Add(historyHeader);
        page.Controls.Add(topSandboxPanel);
        page.Controls.Add(historyBottom);

        return page;
    }

    private CheckBox CreateOptionCheck(string text)
    {
        var chk = new CheckBox
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 3)
        };
        chk.CheckedChanged += (_, _) => ApplyGeneralControls();
        return chk;
    }

    private static Label CreateSectionHeader(string title) => new()
    {
        Text = title,
        AutoSize = true,
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
        ForeColor = Color.FromArgb(24, 48, 76),
        Margin = new Padding(0, 12, 0, 5)
    };

    private void LoadValuesFromConfig()
    {
        _suppressEvents = true;
        try
        {
            EngineSettings s = _config.Engine;
            _cmbSensitivity.SelectedIndex = (int)s.Sensitivity;
            _chkFixTypos.Checked = s.FixTypos;
            _chkFixDoubleCaps.Checked = s.FixDoubleCaps;
            _chkFixInvertedCaps.Checked = s.FixInvertedCaps;
            _chkFixRollover.Checked = s.FixSpacebarRollover;
            _chkAutoCapSentence.Checked = s.AutoCapitalizeAfterSentence;
            _chkAutoCapFirst.Checked = s.AutoCapitalizeFirstWord;
            _chkDoubleSpacePeriod.Checked = s.DoubleSpacePeriod;
            _chkSmartTypography.Checked = s.SmartTypography;
            _chkUndoBackspace.Checked = s.UndoOnImmediateBackspace;
            _chkAutoLearnUndo.Checked = s.AutoLearnOnUndo;
            _cmbLayoutMode.SelectedIndex = (int)s.LayoutMode;
            _chkSwitchOsLayout.Checked = s.SwitchSystemLayoutOnAutoFix;
            _chkPlaySound.Checked = _config.PlaySoundOnAutoSwitch;
            _chkShowLangBadge.Checked = _config.ShowLanguageBadgeInTray;

            _chkDoubleShiftConvert.Checked = _config.UseDoubleShiftConvert;
            _chkBothShiftsPause.Checked = _config.UseBothShiftsPause;
            _chkSingleShiftSwitch.Checked = _config.UseSingleShiftLayoutSwitch;
            _chkDoubleCtrlLine.Checked = _config.UseDoubleCtrlConvertLine;

            _chkStartup.Checked = StartupManager.IsStartupEnabled();
            _chkNotifications.Checked = _config.ShowTrayNotifications;

            _rbBlacklist.Checked = _config.FilterMode == WindowFilterMode.Blacklist;
            _rbWhitelist.Checked = _config.FilterMode == WindowFilterMode.Whitelist;
            _chkFullscreenGames.Checked = _config.AutoDisableInFullscreenGames;
            _chkPasswordFields.Checked = _config.AutoDisableInPasswordFields;

            RefreshProcessListBox();
            RefreshDictionaryLists();
            RefreshHotkeyLabels();
            RefreshStatusBanner();
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void ApplyGeneralControls()
    {
        if (_suppressEvents) return;

        EngineSettings s = _config.Engine;
        s.Sensitivity = (SensitivityLevel)Math.Clamp(_cmbSensitivity.SelectedIndex, 0, 2);
        s.FixTypos = _chkFixTypos.Checked;
        s.FixDoubleCaps = _chkFixDoubleCaps.Checked;
        s.FixInvertedCaps = _chkFixInvertedCaps.Checked;
        s.FixSpacebarRollover = _chkFixRollover.Checked;
        s.AutoCapitalizeAfterSentence = _chkAutoCapSentence.Checked;
        s.AutoCapitalizeFirstWord = _chkAutoCapFirst.Checked;
        s.DoubleSpacePeriod = _chkDoubleSpacePeriod.Checked;
        s.SmartTypography = _chkSmartTypography.Checked;
        s.UndoOnImmediateBackspace = _chkUndoBackspace.Checked;
        s.AutoLearnOnUndo = _chkAutoLearnUndo.Checked;
        s.LayoutMode = (LayoutSwitchMode)Math.Clamp(_cmbLayoutMode.SelectedIndex, 0, 2);
        s.SwitchSystemLayoutOnAutoFix = _chkSwitchOsLayout.Checked;

        _config.PlaySoundOnAutoSwitch = _chkPlaySound.Checked;
        _config.ShowLanguageBadgeInTray = _chkShowLangBadge.Checked;
        _config.UseDoubleShiftConvert = _chkDoubleShiftConvert.Checked;
        _config.UseBothShiftsPause = _chkBothShiftsPause.Checked;
        _config.UseSingleShiftLayoutSwitch = _chkSingleShiftSwitch.Checked;
        _config.UseDoubleCtrlConvertLine = _chkDoubleCtrlLine.Checked;
        _config.ShowTrayNotifications = _chkNotifications.Checked;

        _engine.Settings = s;
        RefreshStatusBanner();
        SaveAndNotify();
    }

    private void OnFilterModeChanged()
    {
        if (_suppressEvents) return;
        _config.FilterMode = _rbWhitelist.Checked ? WindowFilterMode.Whitelist : WindowFilterMode.Blacklist;
        RefreshProcessListBox();
        SaveAndNotify();
    }

    private List<string> ActiveProcessList() =>
        _config.FilterMode == WindowFilterMode.Blacklist
            ? _config.BlacklistedProcesses
            : _config.WhitelistedProcesses;

    private void AddProcessToActiveList(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return;
        string cleaned = rawName.Trim();
        if (cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^4];
        }

        List<string> list = ActiveProcessList();
        if (!list.Any(x => string.Equals(x, cleaned, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(cleaned);
            RefreshProcessListBox();
            SaveAndNotify();
        }
    }

    private void RefreshProcessListBox()
    {
        _lstProcesses.BeginUpdate();
        _lstProcesses.Items.Clear();
        foreach (string p in ActiveProcessList().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            _lstProcesses.Items.Add(p);
        }
        _lstProcesses.EndUpdate();
    }

    private void RefreshDictionaryLists()
    {
        _lstIgnoredWords.BeginUpdate();
        _lstIgnoredWords.Items.Clear();
        foreach (string w in _engine.UserDictionary.IgnoredWords)
        {
            _lstIgnoredWords.Items.Add(w);
        }
        _lstIgnoredWords.EndUpdate();

        _lvCustomSnippets.BeginUpdate();
        _lvCustomSnippets.Items.Clear();
        foreach (var kv in _engine.UserDictionary.CustomReplacements.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ListViewItem(kv.Key);
            item.SubItems.Add(kv.Value);
            _lvCustomSnippets.Items.Add(item);
        }
        _lvCustomSnippets.EndUpdate();
    }

    private void SyncDictionaryToConfigAndSave()
    {
        _config.IgnoredWords = _engine.UserDictionary.IgnoredWords.ToList();
        _config.CustomReplacements = new Dictionary<string, string>(_engine.UserDictionary.CustomReplacements);
        _config.Save();
    }

    private void StartRecordingBinding(Label targetLabel, Action<HotkeyBinding> applyBinding)
    {
        targetLabel.Text = "Нажмите клавишу… (Esc — отмена)";
        targetLabel.BackColor = Color.FromArgb(255, 244, 214);

        _hook.HotkeyRecorderInterceptor = (vk, ctrl, alt, shift) =>
        {
            BeginInvoke(() =>
            {
                _hook.HotkeyRecorderInterceptor = null;
                targetLabel.BackColor = Color.FromArgb(245, 247, 250);

                if (vk != NativeMethods.VK_ESCAPE)
                {
                    applyBinding(new HotkeyBinding(ctrl, alt, shift, vk));
                    SaveAndNotify();
                }

                RefreshHotkeyLabels();
                RefreshStatusBanner();
            });
            return true;
        };
    }

    private void RefreshHotkeyLabels()
    {
        _lblCycleCaseHotkey.Text = _config.CycleCaseHotkey.ToDisplayString();
        _lblTranslitHotkey.Text = _config.TransliterateHotkey.ToDisplayString();
        _lblPauseHotkey.Text = _config.PauseToggleHotkey.ToDisplayString();
        _lblLayoutHotkey.Text = _config.ConvertLayoutHotkey.ToDisplayString();
    }

    private void SaveAndNotify()
    {
        SyncDictionaryToConfigAndSave();
        _onConfigUpdated();
    }
}
