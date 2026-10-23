using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using VirtualKey = Windows.System.VirtualKey;
using ExplorerTabUtility.Hooks;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.UI;

public sealed class TabSearchWindow : Window
{
    private readonly ExplorerWatcher _watcher;
    private readonly IReadOnlyCollection<WindowRecord> _windows;
    private readonly ListView _list = new() { IsItemClickEnabled = true };
    private readonly TextBox _search = new() { PlaceholderText = "Search open and closed tabs" };
    private readonly List<WindowRecord> _results = new();
    private bool _closed;
    public TabSearchWindow(ExplorerWatcher watcher)
    {
        _watcher = watcher;
        _windows = watcher.GetWindows();
        Title = "Search tabs";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 520));
        var panel = new Grid { Padding = new Thickness(24), RowSpacing = 16 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        panel.Children.Add(_search); Grid.SetRow(_list, 1); panel.Children.Add(_list);
        Content = panel;
        _search.TextChanged += (_, _) => Refresh();
        _search.KeyDown += SearchKeyDown;
        _list.ItemClick += (_, args) => { if (args.ClickedItem is ListViewItem item && item.Tag is WindowRecord record) Switch(record); };
        panel.Loaded += (_, _) => _search.Focus(FocusState.Programmatic);
        Closed += (_, _) => _closed = true;
        Activated += (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated) Dismiss(); };
        Refresh();
    }
    private void Refresh()
    {
        _results.Clear(); _list.Items.Clear();
        _results.AddRange(_windows.Where(window => window.Name.Contains(_search.Text, StringComparison.OrdinalIgnoreCase) || window.DisplayLocation.Contains(_search.Text, StringComparison.OrdinalIgnoreCase)));
        foreach (var window in _results)
        {
            var text = new StackPanel { Spacing = 4 };
            text.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(window.Name) ? window.DisplayLocation : window.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = window.DisplayLocation, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            _list.Items.Add(new ListViewItem { Content = text, Tag = window, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }
    private void SearchKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { Dismiss(); args.Handled = true; }
        else if (args.Key == VirtualKey.Enter && _list.SelectedItem is ListViewItem item && item.Tag is WindowRecord record) { Switch(record); args.Handled = true; }
        else if (args.Key is VirtualKey.Down or VirtualKey.Up && _list.Items.Count > 0)
        {
            _list.SelectedIndex = (_list.SelectedIndex + (args.Key == VirtualKey.Down ? 1 : _list.Items.Count - 1)) % _list.Items.Count;
            _list.ScrollIntoView(_list.SelectedItem); args.Handled = true;
        }
    }
    private void Switch(WindowRecord record)
    {
        var asTab = !KeyboardSimulator.IsKeyPressed((int)WinAPI.VirtualKey.LeftShift) && !KeyboardSimulator.IsKeyPressed((int)WinAPI.VirtualKey.RightShift);
        var duplicate = KeyboardSimulator.IsKeyPressed((int)WinAPI.VirtualKey.LeftControl) || KeyboardSimulator.IsKeyPressed((int)WinAPI.VirtualKey.RightControl);
        _ = _watcher.SwitchTo(record.Location, record.Handle, record.SelectedItems, asTab, duplicate);
        Dismiss();
    }
    private void Dismiss() { if (_closed) return; _closed = true; Close(); }
}
