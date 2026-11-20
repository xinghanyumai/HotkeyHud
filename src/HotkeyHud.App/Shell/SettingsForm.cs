using HotkeyHud.Core;
using CoreKeys = HotkeyHud.Core.Keys;
using WfKeys = System.Windows.Forms.Keys;

namespace HotkeyHud.App.Shell;

/// <summary>
/// 设置页（S7.6 Part B）。WinForms 单窗体、**手写布局**、零 NuGet、非模态、关闭即 Dispose。
/// <para>
/// 三条硬规则：
/// ① 内置档案只读——保存时只写 <c>%APPDATA%\HotkeyHud\apps\&lt;app&gt;.json</c>，绝不碰安装目录；
/// ② 加条目直接按组合键录入，不让用户手打格式；
/// ③ 颜色只给 8 个预设（深底上自由取色很容易做出看不清的组合）。
/// </para>
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly ShortcutRepository _repository;
    private readonly Action _onSaved;

    private readonly ListBox _apps = new();
    private readonly ListView _items = new();
    private readonly TextBox _keys = new();
    private readonly TextBox _action = new();
    private readonly ComboBox _color = new();
    private readonly Label _hint = new();
    private readonly Label _source = new();

    private readonly List<Row> _rows = new();
    private AppShortcutProfile? _current;
    private bool _capturing;
    private bool _scaled;

    public SettingsForm(ShortcutRepository repository, Action onSaved)
    {
        _repository = repository;
        _onSaved = onSaved;

        Text = "HotkeyHud 设置";
        StartPosition = FormStartPosition.CenterScreen;

        // 手写布局也要按 DPI 缩放：不设这个，200% 屏上 780×560 会按物理像素画，
        // 控件挤成一团（实测）
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        ClientSize = new Size(780, 560);
        MinimumSize = new Size(660, 440);
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;

        BuildLayout();
        LoadApps();
    }

    /// <summary>当前编辑的应用（验收用）。</summary>
    public string? CurrentApp => _current?.App;

    /// <summary>当前列表行数（验收用）。</summary>
    public int RowCount => _rows.Count;

    // ---------------- 布局（手写，不用 Designer）----------------

    private void BuildLayout()
    {
        var appHeader = new Label
        {
            Text = "应用",
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };

        _apps.Dock = DockStyle.Fill;
        _apps.IntegralHeight = false;
        _apps.SelectedIndexChanged += (_, _) => OnAppSelected();

        var addApp = new Button { Text = "添加应用…", Dock = DockStyle.Bottom, Height = 32 };
        addApp.Click += (_, _) => AddAppRequested?.Invoke();

        var left = new Panel { Dock = DockStyle.Left, Width = 240, Padding = new Padding(8) };
        left.Controls.Add(_apps);
        left.Controls.Add(addApp);
        left.Controls.Add(appHeader);

        var itemHeader = new Label
        {
            Text = "快捷键",
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };

        _source.Dock = DockStyle.Top;
        _source.Height = 20;
        _source.ForeColor = Color.DimGray;
        _source.Padding = new Padding(8, 0, 0, 0);
        _source.Text = "";

        _items.Dock = DockStyle.Fill;
        _items.View = View.Details;
        _items.FullRowSelect = true;
        _items.HideSelection = false;
        _items.MultiSelect = false;
        _items.Columns.Add("键位", 150);
        _items.Columns.Add("动作", 300);
        _items.Columns.Add("颜色", 70);
        _items.SelectedIndexChanged += (_, _) => OnRowSelected();

        var editor = BuildEditor();

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 6, 8, 6) };
        var add = new Button { Text = "+ 添加", Width = 88, Dock = DockStyle.Left };
        add.Click += (_, _) => AddRow();
        var remove = new Button { Text = "删除", Width = 72, Dock = DockStyle.Left };
        remove.Click += (_, _) => RemoveRow();
        buttons.Controls.Add(remove);
        buttons.Controls.Add(add);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        right.Controls.Add(_items);
        right.Controls.Add(buttons);
        right.Controls.Add(editor);
        right.Controls.Add(_source);
        right.Controls.Add(itemHeader);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8, 8, 8, 8) };
        var save = new Button { Text = "保存", Width = 88, Dock = DockStyle.Right };
        save.Click += (_, _) => SaveAll();
        var close = new Button { Text = "关闭", Width = 88, Dock = DockStyle.Right };
        close.Click += (_, _) => Close();

        _hint.Text = "改完点保存，托盘会自动重载";
        _hint.Dock = DockStyle.Fill;
        _hint.TextAlign = ContentAlignment.MiddleLeft;
        _hint.ForeColor = Color.DimGray;

        bottom.Controls.Add(_hint);
        bottom.Controls.Add(close);
        bottom.Controls.Add(save);

        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(bottom);
    }

    private Panel BuildEditor()
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(0, 4, 0, 4) };

        var keysLabel = new Label { Text = "键位", Width = 40, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft };
        _keys.Dock = DockStyle.Left;
        _keys.Width = 160;
        _keys.ReadOnly = true;
        _keys.BackColor = Color.White;
        _keys.Cursor = Cursors.Hand;
        _keys.Enter += (_, _) => StartCapture();
        _keys.Click += (_, _) => StartCapture();
        _keys.KeyDown += OnKeysKeyDown;

        var actionLabel = new Label { Text = "动作", Width = 40, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft };
        _action.Dock = DockStyle.Left;
        _action.Width = 220;

        var colorLabel = new Label { Text = "颜色", Width = 40, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft };
        _color.Dock = DockStyle.Left;
        _color.Width = 80;
        _color.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach ((string name, _) in HudColors.Presets)
        {
            _color.Items.Add(name);
        }

        _color.SelectedIndex = 0;

        var apply = new Button { Text = "应用", Width = 64, Dock = DockStyle.Left };
        apply.Click += (_, _) => ApplyEditor();

        panel.Controls.Add(apply);
        panel.Controls.Add(_color);
        panel.Controls.Add(colorLabel);
        panel.Controls.Add(_action);
        panel.Controls.Add(actionLabel);
        panel.Controls.Add(_keys);
        panel.Controls.Add(keysLabel);
        return panel;
    }

    /// <summary>由外壳接上「添加应用…」（复用托盘入口的文件选择）。</summary>
    public event Action? AddAppRequested;

    /// <summary>截图/自检用：选中某行并进入按键录入态。</summary>
    internal void DemoEdit(int rowIndex, int colorIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count || rowIndex >= _items.Items.Count)
        {
            return;
        }

        _items.Items[rowIndex].Selected = true;
        _color.SelectedIndex = Math.Max(0, Math.Min(colorIndex, _color.Items.Count - 1));
        _action.Text = _rows[rowIndex].Action;
        StartCapture();
    }

    /// <summary>外壳加完应用后让设置页重读档案。</summary>
    public void ReloadApps() => LoadApps();

    // ---------------- 数据 ----------------

    private void LoadApps()
    {
        _apps.BeginUpdate();
        _apps.Items.Clear();
        foreach (AppShortcutProfile profile in _repository.Profiles)
        {
            string mark = profile.IsUserOwned ? " · 我的" : " · 内置";
            _apps.Items.Add(new AppItem(profile, $"{profile.DisplayName}  {profile.Shortcuts.Count} 条{mark}"));
        }

        _apps.EndUpdate();

        if (_apps.Items.Count > 0)
        {
            _apps.SelectedIndex = 0;
        }
    }

    private void OnAppSelected()
    {
        if (_apps.SelectedItem is not AppItem item)
        {
            return;
        }

        _current = item.Profile;
        _rows.Clear();
        foreach (ShortcutEntry entry in item.Profile.Shortcuts)
        {
            _rows.Add(new Row
            {
                Keys = entry.Keys,
                Action = entry.Action,
                Group = entry.Group,
                Color = entry.Color,
            });
        }

        _source.Text = item.Profile.IsUserOwned
            ? "我的档案（可改）：%APPDATA%\\HotkeyHud\\apps"
            : "内置档案（只读）：保存时会复制到 %APPDATA%\\HotkeyHud\\apps";
        RefreshList();
    }

    private void RefreshList()
    {
        _items.BeginUpdate();
        _items.Items.Clear();
        foreach (Row row in _rows)
        {
            var view = new ListViewItem(row.Keys);
            view.SubItems.Add(row.Action);
            view.SubItems.Add(HudColors.DisplayName(row.Color));
            _items.Items.Add(view);
        }

        _items.EndUpdate();
    }

    private void OnRowSelected()
    {
        int index = SelectedIndex();
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        Row row = _rows[index];
        _keys.Text = row.Keys;
        _action.Text = row.Action;
        _color.SelectedIndex = Math.Max(0, IndexOfColor(row.Color));
    }

    private void AddRow()
    {
        _rows.Add(new Row { Keys = string.Empty, Action = string.Empty, Group = "自定义" });
        RefreshList();
        _items.Items[_rows.Count - 1].Selected = true;
        _action.Text = string.Empty;
        _keys.Text = string.Empty;
        _color.SelectedIndex = 0;
        StartCapture();
    }

    private void RemoveRow()
    {
        int index = SelectedIndex();
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        _rows.RemoveAt(index);
        RefreshList();
    }

    private void ApplyEditor()
    {
        int index = SelectedIndex();
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        string keys = _keys.Text.Trim();
        if (!CoreKeys.TryParse(keys, out _))
        {
            _hint.Text = $"键位 \"{keys}\" 解析不了：先点键位框，然后直接按组合键";
            _hint.ForeColor = Color.Firebrick;
            return;
        }

        if (string.IsNullOrWhiteSpace(_action.Text))
        {
            _hint.Text = "动作描述不能为空";
            _hint.ForeColor = Color.Firebrick;
            return;
        }

        Row row = _rows[index];
        row.Keys = keys;
        row.Action = _action.Text.Trim();
        row.Color = HudColors.Normalize(ColorHex(_color.SelectedIndex));
        RefreshList();
        _items.Items[index].Selected = true;
        _hint.Text = "改完点保存，托盘会自动重载";
        _hint.ForeColor = Color.DimGray;
    }

    private void SaveAll()
    {
        if (_current is null)
        {
            return;
        }

        var entries = new List<ShortcutEntry>(_rows.Count);
        foreach (Row row in _rows)
        {
            if (!CoreKeys.TryParse(row.Keys, out Combo combo))
            {
                _hint.Text = $"有条目键位解析失败：\"{row.Keys}\"，未保存";
                _hint.ForeColor = Color.Firebrick;
                return;
            }

            entries.Add(new ShortcutEntry
            {
                Source = ShortcutSource.User,
                Keys = row.Keys,
                Action = row.Action,
                Group = row.Group,
                Color = row.Color,
                Combo = combo,
                IsValid = true,
            });
        }

        var profile = new AppShortcutProfile
        {
            App = _current.App,
            DisplayName = _current.DisplayName,
            ProcessNames = _current.ProcessNames,
            IsUserOwned = true,
            Shortcuts = entries,
        };

        try
        {
            string path = _repository.SaveUserProfile(profile);
            _hint.Text = $"已保存到 {path}";
            _hint.ForeColor = Color.DimGray;
            _onSaved();
            LoadApps();
        }
        catch (Exception ex)
        {
            _hint.Text = $"保存失败：{ex.Message}";
            _hint.ForeColor = Color.Firebrick;
        }
    }

    // ---------------- 按键录入 ----------------

    private void StartCapture()
    {
        _capturing = true;
        _keys.Text = "按下组合键…";
        _keys.Focus();
    }

    private void OnKeysKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;

        if (e.KeyCode == WfKeys.Escape)
        {
            _capturing = false;
            _keys.Text = string.Empty;
            return;
        }

        if (!TryBuildCombo(e.Modifiers, e.KeyCode, out Combo combo))
        {
            _keys.Text = "需要至少一个修饰键 + 一个主键";
            return;
        }

        _keys.Text = CoreKeys.Format(combo);
        _capturing = false;
    }

    /// <summary>
    /// 把一次按键（修饰键状态 + 主键）转成组合。只按下修饰键时返回 false（继续等主键）。
    /// 抽成静态方法是为了让「按键录入」可以被用例直接验证，不必驱动真实键盘。
    /// </summary>
    internal static bool TryBuildCombo(WfKeys modifiers, WfKeys keyCode, out Combo combo)
    {
        combo = default;

        if (keyCode is WfKeys.ControlKey or WfKeys.ShiftKey or WfKeys.Menu
            or WfKeys.LWin or WfKeys.RWin or WfKeys.None)
        {
            return false;
        }

        Modifier modifier = Modifier.None;
        if ((modifiers & WfKeys.Control) != 0)
        {
            modifier |= Modifier.Ctrl;
        }

        if ((modifiers & WfKeys.Alt) != 0)
        {
            modifier |= Modifier.Alt;
        }

        if ((modifiers & WfKeys.Shift) != 0)
        {
            modifier |= Modifier.Shift;
        }

        if (modifier == Modifier.None)
        {
            return false;
        }

        combo = new Combo(modifier, (int)keyCode);
        return true;
    }

    // ---------------- 小工具 ----------------

    private int SelectedIndex()
        => _items.SelectedIndices.Count > 0 ? _items.SelectedIndices[0] : -1;

    private static int IndexOfColor(string? hex)
    {
        string name = HudColors.DisplayName(hex);
        for (int i = 0; i < HudColors.Presets.Count; i++)
        {
            if (string.Equals(HudColors.Presets[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    private static string ColorHex(int index)
        => index >= 0 && index < HudColors.Presets.Count ? HudColors.Presets[index].Hex : string.Empty;

    /// <summary>
    /// 手写布局的尺寸都是 96 DPI 下的逻辑值，这里按当前屏的 DPI 整体放大一次。
    /// 不放大时 200% 屏上窗口只有一半大、控件挤在一起（实测）。
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        float factor = DeviceDpi / 96f;
        if (factor > 1.01f && !_scaled)
        {
            _scaled = true;
            Scale(new SizeF(factor, factor));
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Dispose(); // 不常驻：关掉就释放
    }

    private sealed record AppItem(AppShortcutProfile Profile, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed class Row
    {
        public string Keys { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string? Group { get; set; }

        public string? Color { get; set; }
    }
}
