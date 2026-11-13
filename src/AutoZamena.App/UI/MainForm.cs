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

    private TableLayoutPanel _headerPanel = null!;
    private Label _lblStatusTitle = null!;
    private Label _lblStatusSub = null!;
    private Button _btnToggleEnabled = null!;
    private bool _dictionarySplitInitialized;

    // Tab 1: General
    private ComboBox _cmbSensitivity = null!;
    private CheckBox _chkFixTypos = null!;
    private CheckBox _chkSplitRunTogether = null!;
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

    // Tab 2: Applications
    private RadioButton _rbBlacklist = null!;
    private RadioButton _rbWhitelist = null!;
    private CheckBox _chkFullscreenGames = null!;
    private CheckBox _chkPasswordFields = null!;
    private ListBox _lstProcesses = null!;
    private TextBox _txtNewProcess = null!;

    // Tab 3: Dictionary
    private ListBox _lstIgnoredWords = null!;
    private TextBox _txtNewIgnored = null!;
    private ListView _lvCustomSnippets = null!;
    private TextBox _txtSnippetTrigger = null!;
    private TextBox _txtSnippetReplacement = null!;

    // Tab 4: Shortcuts
    private CheckBox _chkDoubleShiftConvert = null!;
    private CheckBox _chkBothShiftsPause = null!;
    private CheckBox _chkSingleShiftSwitch = null!;
    private CheckBox _chkDoubleCtrlLine = null!;
    private Label _lblCycleCaseHotkey = null!;
    private Label _lblTranslitHotkey = null!;
    private Label _lblPauseHotkey = null!;
    private Label _lblLayoutHotkey = null!;

    // Tab 5: Test & Log
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
        Text = "AutoZamena";
        Size = new Size(800, 640);
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.25f);
        BackColor = Color.FromArgb(245, 246, 248);

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
        _lblStatusTitle.ForeColor = enabled
            ? Color.FromArgb(21, 128, 61)
            : Color.FromArgb(180, 83, 9);

        _lblStatusTitle.Text = enabled
            ? "●  Работает"
            : "●  На паузе";

        string pauseHint = _config.UseBothShiftsPause
            ? "Левый + Правый Shift"
            : _config.PauseToggleHotkey.ToDisplayString();
        string convertHint = _config.UseDoubleShiftConvert
            ? "2× Shift"
            : _config.ConvertLayoutHotkey.ToDisplayString();

        _lblStatusSub.Text =
            $"Раскладка: {convertHint}   ·   " +
            $"Пауза: {pauseHint}   ·   " +
            $"Исправлено за сессию: {_totalCorrectionsCount}";

        _btnToggleEnabled.Text = enabled ? "Пауза" : "Включить";
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
        _headerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MinimumSize = new Size(0, 62),
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.White,
            Padding = new Padding(16, 10, 16, 10)
        };
        _headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _headerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _headerPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lblStatusTitle = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 12, 2),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        _lblStatusSub = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 12, 0),
            ForeColor = Color.FromArgb(95, 104, 114),
            Font = new Font("Segoe UI", 8.75f)
        };

        _btnToggleEnabled = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(120, 32),
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

        var divider = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Color.FromArgb(224, 228, 233)
        };

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(12, 5)
        };

        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildAppsFilterTab());
        tabs.TabPages.Add(BuildDictionaryTab());
        tabs.TabPages.Add(BuildHotkeysTab());
        tabs.TabPages.Add(BuildSandboxHistoryTab());

        var contentWrapper = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 10, 10, 10)
        };
        contentWrapper.Controls.Add(tabs);

        Controls.Add(contentWrapper);
        Controls.Add(divider);
        Controls.Add(_headerPanel);
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("Общие") { BackColor = Color.White, Padding = new Padding(16) };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        var sensPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 8)
        };
        sensPanel.Controls.Add(new Label
        {
            Text = "Чувствительность:",
            AutoSize = true,
            Padding = new Padding(0, 5, 10, 0)
        });
        _cmbSensitivity = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 320
        };
        _cmbSensitivity.Items.AddRange(new object[]
        {
            "Осторожная (только явные опечатки)",
            "Обычная (по умолчанию)",
            "Высокая"
        });
        _cmbSensitivity.SelectedIndexChanged += (_, _) => ApplyGeneralControls();
        sensPanel.Controls.Add(_cmbSensitivity);
        flow.Controls.Add(sensPanel);

        flow.Controls.Add(CreateSectionHeader("Автозамена и набор"));

        _chkFixTypos = CreateOptionCheck("Исправлять опечатки по геометрии клавиш и контексту");
        _chkSplitRunTogether = CreateOptionCheck("Разделять случайно слитые слова (приветмир → привет мир, неработает → не работает)");
        _chkFixDoubleCaps = CreateOptionCheck("Исправлять две заглавные в начале слова (ПРивет → Привет)");
        _chkFixInvertedCaps = CreateOptionCheck("Исправлять перевёрнутый регистр и выключать Caps Lock (пРИВЕТ → Привет)");
        _chkFixRollover = CreateOptionCheck("Склеивать слова, разорванные пробелом (приве т → привет)");
        _chkAutoCapSentence = CreateOptionCheck("Заглавная буква после точки, ! и ?");
        _chkAutoCapFirst = CreateOptionCheck("Заглавная буква в начале нового ввода");
        _chkDoubleSpacePeriod = CreateOptionCheck("Точка с пробелом по двойному пробелу (привет␣␣ → привет. )");
        _chkSmartTypography = CreateOptionCheck("Заменять двойной дефис между пробелами на тире ( -- → — )");
        _chkUndoBackspace = CreateOptionCheck("Отменять исправление клавишей Backspace сразу после пробела");
        _chkAutoLearnUndo = CreateOptionCheck("Добавлять отменённые слова в список исключений");

        flow.Controls.AddRange(new Control[]
        {
            _chkFixTypos, _chkSplitRunTogether, _chkFixDoubleCaps, _chkFixInvertedCaps, _chkFixRollover,
            _chkAutoCapSentence, _chkAutoCapFirst, _chkDoubleSpacePeriod, _chkSmartTypography,
            _chkUndoBackspace, _chkAutoLearnUndo
        });

        flow.Controls.Add(CreateSectionHeader("Раскладка клавиатуры"));

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
            Width = 390
        };
        _cmbLayoutMode.Items.AddRange(new object[]
        {
            "Выключено",
            "Только вручную (2× Shift или горячая клавиша)",
            "Автоматически и по 2× Shift"
        });
        _cmbLayoutMode.SelectedIndexChanged += (_, _) => ApplyGeneralControls();
        layoutRow.Controls.Add(_cmbLayoutMode);
        flow.Controls.Add(layoutRow);

        _chkSwitchOsLayout = CreateOptionCheck("Переключать системную раскладку окна после исправления");
        _chkPlaySound = CreateOptionCheck("Тихий щелчок при автоматическом переключении раскладки");
        flow.Controls.Add(_chkSwitchOsLayout);
        flow.Controls.Add(_chkPlaySound);

        flow.Controls.Add(CreateSectionHeader("Система"));
        _chkShowLangBadge = CreateOptionCheck("Показывать активную раскладку (RU / EN) на значке в трее");
        _chkStartup = CreateOptionCheck("Запускать вместе с Windows");
        _chkStartup.CheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            StartupManager.SetStartupEnabled(_chkStartup.Checked);
        };
        _chkNotifications = CreateOptionCheck("Показывать уведомление при переключении паузы");
        flow.Controls.Add(_chkShowLangBadge);
        flow.Controls.Add(_chkStartup);
        flow.Controls.Add(_chkNotifications);

        page.Controls.Add(flow);
        return page;
    }

    private TabPage BuildAppsFilterTab()
    {
        var page = new TabPage("Приложения") { BackColor = Color.White, Padding = new Padding(16) };

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 124,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };

        _rbBlacklist = new RadioButton
        {
            Text = "Чёрный список — работать везде, кроме указанных программ",
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 4)
        };
        _rbWhitelist = new RadioButton
        {
            Text = "Белый список — работать только в указанных программах",
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 8)
        };
        _rbBlacklist.CheckedChanged += (_, _) => OnFilterModeChanged();
        _rbWhitelist.CheckedChanged += (_, _) => OnFilterModeChanged();

        _chkFullscreenGames = new CheckBox
        {
            Text = "Отключать в полноэкранных 3D-приложениях и играх",
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
            Text = "Отключать в полях ввода паролей (UI Automation)",
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
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0)
        };

        _txtNewProcess = new TextBox { Width = 185, PlaceholderText = "Имя процесса (Code, cmd…)" };
        _txtNewProcess.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddProcessToActiveList(_txtNewProcess.Text);
                _txtNewProcess.Clear();
            }
        };

        var btnAddManual = new Button { Text = "Добавить", Width = 92, Height = 28 };
        btnAddManual.Click += (_, _) =>
        {
            AddProcessToActiveList(_txtNewProcess.Text);
            _txtNewProcess.Clear();
        };

        var btnPickRunning = new Button { Text = "Из открытых окон…", Width = 150, Height = 28 };
        btnPickRunning.Click += (_, _) =>
        {
            using var dlg = new ProcessPickerDialog();
            if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedProcessName))
            {
                AddProcessToActiveList(dlg.SelectedProcessName);
            }
        };

        var btnRemove = new Button { Text = "Удалить", Width = 92, Height = 28 };
        btnRemove.Click += (_, _) =>
        {
            if (_lstProcesses.SelectedItem is string sel)
            {
                ActiveProcessList().RemoveAll(x => string.Equals(x, sel, StringComparison.OrdinalIgnoreCase));
                RefreshProcessListBox();
                SaveAndNotify();
            }
        };

        var btnResetDefaults = new Button { Text = "По умолчанию", Width = 120, Height = 28 };
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
        var page = new TabPage("Словарь") { BackColor = Color.White, Padding = new Padding(14) };

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

        var leftHeader = new Label
        {
            Text = "Исключения (не исправлять):",
            Dock = DockStyle.Top,
            Height = 26,
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        _lstIgnoredWords = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

        var leftBottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 6, 0, 0)
        };
        _txtNewIgnored = new TextBox { Width = 140, PlaceholderText = "Слово" };
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
        var btnAddIgnored = new Button { Text = "+", Width = 40, Height = 27 };
        btnAddIgnored.Click += (_, _) => SubmitIgnored();
        var btnDelIgnored = new Button { Text = "Удалить", Width = 84, Height = 27 };
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

        var rightHeader = new Label
        {
            Text = "Сокращения и замены:",
            Dock = DockStyle.Top,
            Height = 26,
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        _lvCustomSnippets = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _lvCustomSnippets.Columns.Add("Сокращение", 120);
        _lvCustomSnippets.Columns.Add("Замена", 260);

        var rightBottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 6, 0, 0)
        };
        _txtSnippetTrigger = new TextBox { Width = 92, PlaceholderText = "тг" };
        _txtSnippetReplacement = new TextBox { Width = 165, PlaceholderText = "Telegram" };
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
        var btnAddSnippet = new Button { Text = "Сохранить", Width = 88, Height = 27 };
        btnAddSnippet.Click += (_, _) => SubmitSnippet();
        var btnDelSnippet = new Button { Text = "Удалить", Width = 80, Height = 27 };
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
            Height = 38,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 6, 0, 0)
        };

        var btnImportDict = new Button { Text = "Импорт…", Width = 115, Height = 27 };
        btnImportDict.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Импорт словаря",
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
                        $"Добавлено исключений: {ign}, замен: {snip}.",
                        "Импорт словаря",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Не удалось прочитать файл: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        };

        var btnExportDict = new Button { Text = "Экспорт…", Width = 115, Height = 27 };
        btnExportDict.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Экспорт словаря",
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
                    MessageBox.Show(this, $"Не удалось сохранить файл: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        };

        var lblMacroHint = new Label
        {
            Text = "Переменные в шаблонах: {{date}}, {{time}}, {{iso}}, {{clipboard}}, позиция курсора: |",
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 108, 118),
            Padding = new Padding(0, 6, 12, 0)
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
        var page = new TabPage("Клавиши") { BackColor = Color.White, Padding = new Padding(16) };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        flow.Controls.Add(CreateSectionHeader("Клавиши-модификаторы"));

        _chkDoubleShiftConvert = CreateOptionCheck(
            "Двойной Shift — перевести последнее слово, выделенный текст или отменить переключение"
        );
        _chkBothShiftsPause = CreateOptionCheck(
            "Левый Shift + Правый Shift — пауза и возобновление работы"
        );
        _chkSingleShiftSwitch = CreateOptionCheck(
            "Одиночный Shift — переключение системной раскладки"
        );
        _chkDoubleCtrlLine = CreateOptionCheck(
            "Двойной Ctrl — перевести всю набранную строку"
        );

        flow.Controls.AddRange(new Control[]
        {
            _chkDoubleShiftConvert,
            _chkBothShiftsPause,
            _chkSingleShiftSwitch,
            _chkDoubleCtrlLine
        });

        flow.Controls.Add(CreateSectionHeader("Выделенный текст"));

        flow.Controls.Add(BuildCustomBindingRow(
            "Смена регистра (ПРОПИСНЫЕ → строчные → Обычный):",
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
            "Транслитерация (Привет ↔ Privet):",
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

        flow.Controls.Add(CreateSectionHeader("Дополнительные сочетания"));

        flow.Controls.Add(BuildCustomBindingRow(
            "Пауза / возобновление:",
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
            "Перевод раскладки последнего слова:",
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
            ForeColor = Color.FromArgb(80, 88, 98),
            Margin = new Padding(0, 0, 0, 3)
        });

        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        var lbl = new Label
        {
            Width = 205,
            Height = 29,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(246, 248, 250),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold),
            Margin = new Padding(0, 0, 8, 0)
        };
        valueLabel = lbl;

        var btnSet = new Button { Text = "Задать…", Width = 110, Height = 29 };
        btnSet.Click += (_, _) => onRecord(lbl);

        var btnReset = new Button { Text = resetText, Width = 150, Height = 29 };
        btnReset.Click += (_, _) => onClearOrReset();

        row.Controls.AddRange(new Control[] { lbl, btnSet, btnReset });
        container.Controls.Add(row);
        return container;
    }

    private TabPage BuildSandboxHistoryTab()
    {
        var page = new TabPage("Проверка и журнал") { BackColor = Color.White, Padding = new Padding(14) };

        var topSandboxPanel = new Panel { Dock = DockStyle.Top, Height = 140 };
        var lblSandbox = new Label
        {
            Text = "Поле для проверки ввода:",
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        _txtSandbox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Segoe UI", 10.5f),
            BackColor = Color.White
        };
        _txtSandbox.GotFocus += (_, _) => _engine.ResetContext();

        topSandboxPanel.Controls.Add(_txtSandbox);
        topSandboxPanel.Controls.Add(lblSandbox);

        var historyHeader = new Label
        {
            Text = "Последние замены (хранятся только в памяти текущей сессии):",
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(0, 8, 0, 0),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Bold)
        };

        _lvHistory = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _lvHistory.Columns.Add("Время", 76);
        _lvHistory.Columns.Add("Набрано", 145);
        _lvHistory.Columns.Add("Заменено на", 155);
        _lvHistory.Columns.Add("Тип", 220);
        _lvHistory.Columns.Add("Процесс", 120);

        var historyBottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 6, 0, 0)
        };
        var btnWhitelistSelected = new Button
        {
            Text = "Добавить «Набрано» в исключения",
            Width = 235,
            Height = 28
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
                    $"Слово «{entry.OriginalText}» добавлено в список исключений.",
                    "Исключения",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
        };

        var btnClearHistory = new Button { Text = "Очистить журнал", Width = 135, Height = 28 };
        btnClearHistory.Click += (_, _) => _lvHistory.Items.Clear();

        var btnClearSandbox = new Button { Text = "Очистить поле", Width = 130, Height = 28 };
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
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
        ForeColor = Color.FromArgb(30, 41, 59),
        Margin = new Padding(0, 12, 0, 4)
    };

    private void LoadValuesFromConfig()
    {
        _suppressEvents = true;
        try
        {
            EngineSettings s = _config.Engine;
            _cmbSensitivity.SelectedIndex = (int)s.Sensitivity;
            _chkFixTypos.Checked = s.FixTypos;
            _chkSplitRunTogether.Checked = s.SplitRunTogetherWords;
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
        s.SplitRunTogetherWords = _chkSplitRunTogether.Checked;
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
        targetLabel.Text = "Нажмите сочетание… (Esc — отмена)";
        targetLabel.BackColor = Color.FromArgb(254, 249, 195);

        _hook.HotkeyRecorderInterceptor = (vk, ctrl, alt, shift) =>
        {
            BeginInvoke(() =>
            {
                _hook.HotkeyRecorderInterceptor = null;
                targetLabel.BackColor = Color.FromArgb(246, 248, 250);

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
