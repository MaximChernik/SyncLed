using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SyncLed.Avalonia.ViewModels;

namespace SyncLed.Avalonia.Views;

/// <summary>Редактор групп: создание, переименование, режим, цвет и состав группы.</summary>
public sealed partial class GroupsView : UserControl
{
    private MainWindowViewModel? _viewModel;
    private AppConfig? _config;
    private List<DeviceGroup> _groups = new();
    private List<DeviceConfig> _devices = new();
    private IReadOnlyList<DeviceViewModel>? _deviceViewModels;
    private DeviceGroup? _selected;
    private IBrush? _accentBrush;

    public GroupsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as MainWindowViewModel;
        ResolveSources();
        SelectGroup(_groups.FirstOrDefault());
    }

    /// <summary>
    /// MainWindowViewModel не публикует конфиг напрямую, поэтому достаём AppConfig
    /// через AppServices (публичное свойство или приватное поле _services).
    /// </summary>
    private static AppConfig? ResolveConfig(MainWindowViewModel viewModel)
    {
        var type = viewModel.GetType();
        if (type.GetProperty("Config", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(viewModel) is AppConfig direct)
            return direct;

        object? services =
            type.GetProperty("Services", BindingFlags.Public | BindingFlags.Instance)?.GetValue(viewModel) ??
            type.GetField("_services", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(viewModel);
        return services?.GetType().GetProperty("Config")?.GetValue(services) as AppConfig;
    }

    private void ResolveSources()
    {
        if (_viewModel == null)
        {
            _config = null;
            _groups = new List<DeviceGroup>();
            _devices = new List<DeviceConfig>();
            _deviceViewModels = null;
            return;
        }

        _config = ResolveConfig(_viewModel);
        _groups = _config?.Groups ?? (_viewModel.Groups as List<DeviceGroup>) ?? new List<DeviceGroup>();
        _devices = _config?.Devices ?? _viewModel.Devices.Select(d => d.Device).ToList();
        _deviceViewModels = _viewModel.Devices;
    }

    private void SelectGroup(DeviceGroup? group)
    {
        _selected = group;
        GroupsList.ItemsSource = _groups.ToList();
        if (group != null && _groups.Contains(group)) GroupsList.SelectedItem = group;
        UpdateEditor();
    }

    private void ReloadList()
    {
        var items = _groups.ToList();
        GroupsList.ItemsSource = items;
        if (_selected != null && items.Contains(_selected)) GroupsList.SelectedItem = _selected;
    }

    private void UpdateEditor()
    {
        bool hasSelection = _selected != null;
        EditorPanel.IsVisible = hasSelection;
        PlaceholderText.IsVisible = !hasSelection;
        if (_selected == null) return;

        NameBox.Text = _selected.Name;
        ColorBox.Text = _selected.StaticColor;
        SplitCheck.IsChecked = _selected.SplitPerimeter;
        UpdateModeButtons();
        RebuildMembers();
    }

    private void UpdateModeButtons()
    {
        var accent = AccentBrush;
        if (_selected == null) return;
        SetActive(ModeSyncBtn, _selected.Mode == "sync", accent);
        SetActive(ModeColorBtn, _selected.Mode == "color", accent);
        SetActive(ModeOffBtn, _selected.Mode == "off", accent);
    }

    private static void SetActive(Button button, bool active, IBrush accent)
    {
        if (active) button.BorderBrush = accent;
        else button.ClearValue(Button.BorderBrushProperty);
    }

    private IBrush AccentBrush => _accentBrush ??= BuildAccentBrush();

    private IBrush BuildAccentBrush()
    {
        if (this.TryFindResource("Brush.Accent", out var resource) && resource is IBrush brush)
            return brush;
        return new SolidColorBrush(Color.Parse("#817BFF"));
    }

    private void RebuildMembers()
    {
        MembersPanel.Children.Clear();
        if (_selected == null) return;

        int inGroup = 0;
        foreach (var device in _devices)
        {
            bool member = string.Equals(device.GroupId, _selected.Id, StringComparison.OrdinalIgnoreCase);
            if (member) inGroup++;

            var check = new CheckBox
            {
                Content = DeviceLabel(device),
                IsChecked = member,
                Tag = device,
                Margin = new Thickness(0, 3, 0, 3)
            };
            ToolTip.SetTip(check, string.IsNullOrEmpty(device.Uuid) ? device.ProductId : device.Uuid);
            check.Click += OnMemberClick;
            MembersPanel.Children.Add(check);
        }

        MembersCountText.Text = $"{inGroup} из {_devices.Count}";
    }

    private string DeviceLabel(DeviceConfig device)
    {
        var live = _deviceViewModels?.FirstOrDefault(d =>
            !string.IsNullOrEmpty(device.Uuid) &&
            string.Equals(d.Device.Uuid, device.Uuid, StringComparison.OrdinalIgnoreCase));

        string fallback = string.IsNullOrWhiteSpace(device.ProductId) ? "Лента" : device.ProductId;
        string title = live?.Title ?? AppConfig.DisplayName(device, fallback);
        string detail = live?.Detail ?? "";
        return string.IsNullOrWhiteSpace(detail) || string.Equals(title, detail, StringComparison.OrdinalIgnoreCase)
            ? title
            : $"{title} · {detail}";
    }

    private void NotifyChanged()
    {
        _viewModel?.RefreshGroups();
        _viewModel?.SaveSettings();
    }

    private void OnGroupSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GroupsList.SelectedItem is DeviceGroup group)
        {
            _selected = group;
            UpdateEditor();
        }
    }

    private void OnAddGroupClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        string name = NewNameBox.Text?.Trim() ?? "";
        DeviceGroup group;
        if (_config != null)
        {
            group = _config.CreateGroup(name);
        }
        else
        {
            group = new DeviceGroup
            {
                Id = UniqueGroupId(),
                Name = name.Length == 0 ? "Новая группа" : name
            };
            _groups.Add(group);
        }

        NewNameBox.Text = "";
        SelectGroup(group);
        NotifyChanged();
    }

    private void OnRenameGroupClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        string name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) return;

        _selected.Name = name;
        ReloadList();
        NotifyChanged();
    }

    private void OnDeleteGroupClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null || _viewModel == null) return;

        var group = _selected;
        _groups.Remove(group);

        // Убираем ссылки на удалённую группу из устройств, иначе обновление состава упадёт.
        foreach (var device in _devices)
            if (string.Equals(device.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
                device.GroupId = "";
        if (_deviceViewModels != null)
            foreach (var deviceViewModel in _deviceViewModels)
                if (string.Equals(deviceViewModel.Device.GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
                    deviceViewModel.Device.GroupId = "";

        SelectGroup(_groups.FirstOrDefault());
        NotifyChanged();
    }

    private void OnModeSyncClick(object? sender, RoutedEventArgs e) => ApplyMode("sync");

    private void OnModeColorClick(object? sender, RoutedEventArgs e) => ApplyMode("color");

    private void OnModeOffClick(object? sender, RoutedEventArgs e) => ApplyMode("off");

    private void ApplyMode(string mode)
    {
        if (_selected == null) return;
        _selected.Mode = mode;
        UpdateModeButtons();
        NotifyChanged();
    }

    private void OnApplyColorClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        string hex = (ColorBox.Text ?? "").Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        if (hex.Length != 7) return;
        foreach (char c in hex.AsSpan(1))
            if (!Uri.IsHexDigit(c)) return;

        _selected.StaticColor = hex.ToUpperInvariant();
        ColorBox.Text = _selected.StaticColor;
        NotifyChanged();
    }

    private void OnSplitPerimeterClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.SplitPerimeter = SplitCheck.IsChecked == true;
        NotifyChanged();
    }

    private void OnMemberClick(object? sender, RoutedEventArgs e)
    {
        if (_selected == null || sender is not CheckBox check || check.Tag is not DeviceConfig device) return;

        if (check.IsChecked == true)
        {
            device.GroupId = _selected.Id;
            if (device.Mode == "sync") device.Mode = "inherit";
        }
        else if (string.Equals(device.GroupId, _selected.Id, StringComparison.OrdinalIgnoreCase))
        {
            device.GroupId = "";
        }

        int inGroup = MembersPanel.Children.OfType<CheckBox>().Count(c => c.IsChecked == true);
        MembersCountText.Text = $"{inGroup} из {_devices.Count}";
        NotifyChanged();
    }

    private string UniqueGroupId()
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N")[..8];
        } while (_groups.Any(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)));
        return id;
    }
}
