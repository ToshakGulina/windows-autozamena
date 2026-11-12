using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AutoZamena.App.UI;

internal sealed class ProcessPickerDialog : Form
{
    private readonly ListView _listView;
    private readonly Button _btnOk;

    public string? SelectedProcessName { get; private set; }

    public ProcessPickerDialog()
    {
        Text = "Выбор запущенного приложения";
        Size = new Size(560, 440);
        MinimumSize = new Size(460, 340);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(249, 250, 251);

        var lblHint = new Label
        {
            Text = "Выберите приложение из списка открытых окон:",
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(12, 10, 12, 0)
        };

        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            GridLines = true
        };
        _listView.Columns.Add("Процесс (.exe)", 175);
        _listView.Columns.Add("Заголовок окна", 340);
        _listView.DoubleClick += (_, _) => ConfirmSelection();

        var bottomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 8)
        };

        var btnCancel = new Button
        {
            Text = "Отмена",
            Width = 100,
            Height = 32,
            DialogResult = DialogResult.Cancel
        };

        _btnOk = new Button
        {
            Text = "Добавить",
            Width = 110,
            Height = 32,
            Enabled = false
        };
        _btnOk.Click += (_, _) => ConfirmSelection();

        var btnRefresh = new Button
        {
            Text = "Обновить",
            Width = 100,
            Height = 32
        };
        btnRefresh.Click += (_, _) => PopulateProcesses();

        _listView.SelectedIndexChanged += (_, _) => _btnOk.Enabled = _listView.SelectedItems.Count > 0;

        bottomPanel.Controls.Add(btnCancel);
        bottomPanel.Controls.Add(_btnOk);
        bottomPanel.Controls.Add(btnRefresh);

        var centerWrapper = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 4, 12, 4)
        };
        centerWrapper.Controls.Add(_listView);

        Controls.Add(centerWrapper);
        Controls.Add(lblHint);
        Controls.Add(bottomPanel);

        AcceptButton = _btnOk;
        CancelButton = btnCancel;

        PopulateProcesses();
    }

    private void PopulateProcesses()
    {
        _listView.BeginUpdate();
        _listView.Items.Clear();

        int ownPid = Environment.ProcessId;
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Process proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id == ownPid)
                {
                    continue;
                }

                string name = proc.ProcessName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string title = proc.MainWindowTitle ?? string.Empty;
                if (!seen.TryGetValue(name, out string? existingTitle) ||
                    (string.IsNullOrEmpty(existingTitle) && !string.IsNullOrEmpty(title)))
                {
                    seen[name] = title;
                }
            }
            catch
            {
                // Skip protected system processes
            }
            finally
            {
                proc.Dispose();
            }
        }

        // Put processes with visible window titles first
        var ordered = seen
            .OrderByDescending(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var kv in ordered)
        {
            var item = new ListViewItem(kv.Key) { Tag = kv.Key };
            item.SubItems.Add(string.IsNullOrWhiteSpace(kv.Value) ? "(фоновый процесс)" : kv.Value);
            _listView.Items.Add(item);
        }

        _listView.EndUpdate();
    }

    private void ConfirmSelection()
    {
        if (_listView.SelectedItems.Count == 0)
        {
            return;
        }

        SelectedProcessName = _listView.SelectedItems[0].Tag as string;
        DialogResult = DialogResult.OK;
        Close();
    }
}
