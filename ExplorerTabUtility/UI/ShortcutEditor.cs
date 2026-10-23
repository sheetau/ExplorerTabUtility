using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.UI;

internal sealed class ShortcutEditor : UserControl
{
    private readonly Expander _expander = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 76, CornerRadius = new CornerRadius(8) };
    public bool IsExpanded { get => _expander.IsExpanded; set => _expander.IsExpanded = value; }
    private readonly HotKeyProfile _profile;
    private readonly ProfileManager _profiles;
    private readonly MainWindow _window;
    private readonly TextBlock _title = new();
    private readonly TextBlock _summary = new() { FontSize = 12 };
    private readonly ToggleSwitch _enabled = new() { OffContent = "", OnContent = "", MinWidth = 0 };
    private readonly Button _shortcut = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _scope = new() { Header = "Scope", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _action = new() { Header = "Action", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _path = new() { Header = "Folder or location", PlaceholderText = "Default Explorer location" };
    private readonly ToggleSwitch _asTab = new() { Header = "Open in a tab" };
    private readonly ToggleSwitch _doubleClick = new() { Header = "Require a double-click" };
    private readonly ToggleSwitch _handled = new() { Header = "Suppress original keyboard input" };
    private bool _initializing = true;

    public ShortcutEditor(HotKeyProfile profile, ProfileManager profiles, MainWindow window)
    {
        _profile = profile; _profiles = profiles; _window = window;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var heading = new Grid { ColumnSpacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(_title); labels.Children.Add(_summary);
        _summary.Style = (Style)Application.Current.Resources["SettingsCaptionStyle"];
        heading.Children.Add(labels);
        Grid.SetColumn(_enabled, 1); heading.Children.Add(_enabled);
        _expander.Header = heading;
        _enabled.IsOn = profile.IsEnabled;
        AutomationProperties.SetName(_enabled, "Enable shortcut");
        _enabled.Toggled += (_, _) => { _profile.IsEnabled = _enabled.IsOn; Save(); };

        var panel = new StackPanel { Spacing = 16 };
        var name = new TextBox { Header = "Name", Text = profile.Name ?? "" };
        name.TextChanged += (_, _) => { _profile.Name = name.Text; Save(); };
        panel.Children.Add(name);
        _shortcut.Click += async (_, _) => { await ShortcutRecorder.Record(_profile, _profiles, _window); Refresh(); };
        AutomationProperties.SetName(_shortcut, "Record shortcut");
        panel.Children.Add(new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Shortcut" }, _shortcut } });

        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition());
        _scope.Items.Add(new ComboBoxItem { Content = "Everywhere", Tag = HotkeyScope.Global });
        _scope.Items.Add(new ComboBoxItem { Content = "File Explorer", Tag = HotkeyScope.FileExplorer });
        _scope.SelectedIndex = (int)profile.Scope;
        _scope.SelectionChanged += (_, _) => { if (_initializing) return; _profile.Scope = (HotkeyScope)_scope.SelectedIndex; PopulateActions(); Save(); };
        row.Children.Add(_scope); Grid.SetColumn(_action, 1); row.Children.Add(_action);
        panel.Children.Add(row);
        PopulateActions();
        _action.SelectionChanged += (_, _) => { if (_initializing || _action.SelectedItem is not ComboBoxItem selected) return; _profile.Action = (HotKeyAction)selected.Tag; UpdateVisibility(); Save(); };
        _path.Text = profile.Path ?? "";
        _path.TextChanged += (_, _) => { _profile.Path = _path.Text; Save(); };
        panel.Children.Add(_path);
        _asTab.IsOn = profile.IsAsTab;
        _asTab.Toggled += (_, _) => { _profile.IsAsTab = _asTab.IsOn; Save(); };
        panel.Children.Add(_asTab);
        _handled.IsOn = profile.IsHandled;
        _handled.Toggled += (_, _) => { _profile.IsHandled = _handled.IsOn; Save(); };
        panel.Children.Add(_handled);
        _doubleClick.IsOn = profile.IsDoubleClick;
        _doubleClick.Toggled += (_, _) => { _profile.IsDoubleClick = _doubleClick.IsOn; Save(); };
        panel.Children.Add(_doubleClick);
        var delay = new NumberBox { Header = "Delay (ms)", Minimum = 0, Maximum = 60000, Value = profile.Delay, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 10 };
        delay.ValueChanged += (_, _) => { if (double.IsFinite(delay.Value)) { _profile.Delay = (int)delay.Value; Save(); } };
        panel.Children.Add(delay);
        var delete = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 16 }, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(delete, "Delete shortcut");
        ToolTipService.SetToolTip(delete, "Delete shortcut");
        delete.Click += (_, _) => _profiles.Remove(_profile);
        panel.Children.Add(delete);
        _expander.Content = panel;
        Content = _expander;
        _initializing = false;
        Refresh();
    }

    private void Save() { if (_initializing) return; Refresh(); _profiles.Save(); }
    private void Refresh()
    {
        _title.Text = string.IsNullOrWhiteSpace(_profile.Name) ? "New shortcut" : _profile.Name;
        var keys = _profile.HotKeys?.Length > 0 ? _profile.HotKeys.HotKeysToString(_profile.IsDoubleClick) : "Not set";
        AutomationProperties.SetName(_expander, _title.Text);
        AutomationProperties.SetName(_enabled, "Enable " + _title.Text);
        _summary.Text = keys + " · " + ActionName(_profile.Action);
        _shortcut.Content = _profile.HotKeys?.Length > 0 ? keys : "Record shortcut";
        UpdateVisibility();
    }
    private void UpdateVisibility()
    {
        _path.Visibility = _profile.Action == HotKeyAction.Open ? Visibility.Visible : Visibility.Collapsed;
        _asTab.Visibility = _profile.Action is HotKeyAction.Open or HotKeyAction.Duplicate or HotKeyAction.ReopenClosed ? Visibility.Visible : Visibility.Collapsed;
        _doubleClick.Visibility = _profile.IsMouse ? Visibility.Visible : Visibility.Collapsed;
        _handled.Visibility = _profile.IsMouse ? Visibility.Collapsed : Visibility.Visible;
    }
    private void PopulateActions()
    {
        var wasInitializing = _initializing; _initializing = true;
        _action.Items.Clear();
        foreach (var action in Enum.GetValues<HotKeyAction>())
        {
            if (_profile.Scope == HotkeyScope.Global && action is HotKeyAction.Duplicate or HotKeyAction.ReopenClosed or HotKeyAction.NavigateBack or HotKeyAction.NavigateUp or HotKeyAction.NavigateForward or HotKeyAction.SetTargetWindow or HotKeyAction.DetachTab) continue;
            var item = new ComboBoxItem { Content = ActionName(action), Tag = action };
            _action.Items.Add(item);
            if (action == _profile.Action) _action.SelectedItem = item;
        }
        if (_action.SelectedIndex < 0) { _action.SelectedIndex = 0; _profile.Action = (HotKeyAction)((ComboBoxItem)_action.SelectedItem).Tag; }
        _initializing = wasInitializing;
    }
    private static string ActionName(HotKeyAction action) => action switch
    {
        HotKeyAction.Open => "Open location", HotKeyAction.Duplicate => "Duplicate tab", HotKeyAction.ReopenClosed => "Reopen closed tab",
        HotKeyAction.TabSearch => "Search tabs", HotKeyAction.NavigateBack => "Go back", HotKeyAction.NavigateUp => "Go up",
        HotKeyAction.NavigateForward => "Go forward", HotKeyAction.SetTargetWindow => "Set target window", HotKeyAction.ToggleWinHook => "Toggle window hook",
        HotKeyAction.ToggleReuseTabs => "Toggle tab reuse", HotKeyAction.ToggleVisibility => "Show settings", HotKeyAction.DetachTab => "Detach tab",
        HotKeyAction.SnapRight => "Snap right", HotKeyAction.SnapLeft => "Snap left", HotKeyAction.SnapUp => "Snap up", HotKeyAction.SnapDown => "Snap down", _ => action.ToString()
    };
}
