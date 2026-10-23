using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Windowing;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.UI;

public sealed partial class MainWindow : Window
{
    private readonly ProfileManager _profiles;
    private readonly Dictionary<ToggleSwitch, Func<bool>> _preferences = new();
    private bool _loading;
    private bool _exiting;
    private readonly System.Threading.Tasks.TaskCompletionSource _ready = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
    public System.Threading.Tasks.Task Ready => _ready.Task;
    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    public MainWindow(ProfileManager profiles)
    {
        _profiles = profiles;
        InitializeComponent();
        Root.Loaded += (_, _) => _ready.TrySetResult();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        void CaptionColors()
        {
            var dark = Root.ActualTheme == ElementTheme.Dark;
            AppWindow.TitleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        }
        Root.ActualThemeChanged += (_, _) => CaptionColors();
        CaptionColors();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Icon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(680, SettingsManager.WindowWidth), Math.Max(600, SettingsManager.WindowHeight)));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.PreferredMinimumWidth = 680;
        AppWindow.Closing += (_, args) =>
        {
            SaveWindowSize();
            if (!_exiting) { args.Cancel = true; AppWindow.Hide(); }
        };
        StatusBar.Closed += (_, _) => StatusBar.Visibility = Visibility.Collapsed;
        foreach (var profile in profiles.Profiles) ProfilesPanel.Children.Add(new ShortcutEditor(profile, profiles, this));
        profiles.Profiles.CollectionChanged += (_, _) => RebuildProfiles();
        AddPreference("Window hook", "Open new Explorer windows as tabs, including taskbar folder shortcuts.", () => SettingsManager.IsWindowHookActive, value => SettingsManager.IsWindowHookActive = value);
        AddPreference("Reuse tabs", "Switch to an existing folder tab when possible.", () => SettingsManager.ReuseTabs, value => SettingsManager.ReuseTabs = value);
        AddPreference("Keyboard shortcuts", "Enable your keyboard shortcuts.", () => SettingsManager.IsKeyboardHookActive, value => SettingsManager.IsKeyboardHookActive = value);
        AddPreference("Mouse shortcuts", "Enable your mouse shortcuts.", () => SettingsManager.IsMouseHookActive, value => SettingsManager.IsMouseHookActive = value);
        AddPreference("Start with Windows", "Run in the notification area when you sign in.", () => RegistryManager.IsStartupEnabled, value => { if (value != RegistryManager.IsStartupEnabled) RegistryManager.ToggleStartup(); });
        AddPreference("Show tray icon", "Open settings and control hooks from the notification area.", () => !SettingsManager.IsTrayIconHidden, value => SettingsManager.IsTrayIconHidden = !value);
        AddPreference("Check for updates", "Notify about new releases at startup and while running.", () => SettingsManager.AutoUpdate, value => SettingsManager.AutoUpdate = value);
        AddPreference("Remember closed tabs", "Keep closed locations between app sessions.", () => SettingsManager.SaveClosedHistory, value => SettingsManager.SaveClosedHistory = value);
        AddPreference("Restore open tabs", "Reopen your previous Explorer locations after a restart.", () => SettingsManager.RestorePreviousWindows, value => SettingsManager.RestorePreviousWindows = value);
        SettingsManager.StaticPropertyChanged += (_, _) => RefreshPreferences();
    }
    private void RebuildProfiles()
    {
        ProfilesPanel.Children.Clear();
        foreach (var profile in _profiles.Profiles) ProfilesPanel.Children.Add(new ShortcutEditor(profile, _profiles, this));
    }
    private void AddShortcut_Click(object sender, RoutedEventArgs args)
    {
        _profiles.Add();
        if (ProfilesPanel.Children[^1] is ShortcutEditor editor) editor.IsExpanded = true;
    }
    private void AddPreference(string title, string description, Func<bool> get, Action<bool> set)
    {
        var row = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 24, MinHeight = 74 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title });
        labels.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SettingsCaptionStyle"] });
        row.Children.Add(labels);
        var toggle = new ToggleSwitch { IsOn = get(), OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title);
        Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
        toggle.Toggled += (_, _) => { if (!_loading) set(toggle.IsOn); };
        _preferences.Add(toggle, get);
        PreferencesPanel.Children.Add(new Border
        {
            Child = row, Style = (Style)Application.Current.Resources["SettingsCardStyle"]
        });
    }
    public void RefreshPreferences()
    {
        _loading = true;
        foreach (var (toggle, get) in _preferences) toggle.IsOn = get();
        _loading = false;
    }
    public void ShowSettings()
    {
        RefreshPreferences();
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }
    private void SaveWindowSize()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            SettingsManager.SetWindowSize(AppWindow.Size.Width, AppWindow.Size.Height);
    }
    public void ShowStatus(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational, Uri? link = null)
    {
        StatusBar.Title = title; StatusBar.Message = message; StatusBar.Severity = severity;
        StatusBar.ActionButton = link == null ? null : new HyperlinkButton { Content = "View release", NavigateUri = link };
        StatusBar.IsOpen = true;
        StatusBar.Visibility = Visibility.Visible;
    }
    public void ExitWindow() { _exiting = true; Close(); }
    public void ShowUpdate(string message, Action install)
    {
        ShowStatus("Update available", message);
        var button = new Button { Content = "Update and restart" };
        button.Click += (_, _) => { button.IsEnabled = false; install(); };
        StatusBar.ActionButton = button;
    }
}
