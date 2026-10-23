using System;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using H.Hooks;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Interop;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.UI;

internal static class ShortcutRecorder
{
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref System.Drawing.Point point);
    public static async Task Record(HotKeyProfile profile, ProfileManager profiles, MainWindow window)
    {
        var surface = new Button { Content = "Click here to record a mouse shortcut", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 64 };
        var panel = new StackPanel { Spacing = 16, Children =
        {
            new TextBlock { Text = "Press a key combination, or click below with the mouse button and modifiers you want to use.", TextWrapping = TextWrapping.Wrap }, surface
        }};
        var dialog = new ContentDialog { Title = "Record shortcut", Content = panel, CloseButtonText = "Cancel", SecondaryButtonText = "Clear", XamlRoot = window.Content.XamlRoot };
        using var keyboard = new LowLevelKeyboardHook { Handling = true };
        using var mouse = new LowLevelMouseHook { Handling = true, AddKeyboardKeys = true };
        ScreenBounds bounds = default;
        var captured = false;
        void UpdateBounds()
        {
            var position = surface.TransformToVisual(window.Content).TransformPoint(new Windows.Foundation.Point());
            var origin = new System.Drawing.Point();
            ClientToScreen(window.Handle, ref origin);
            var scale = surface.XamlRoot.RasterizationScale;
            bounds = new(origin.X + position.X * scale, origin.Y + position.Y * scale, surface.ActualWidth * scale, surface.ActualHeight * scale);
        }
        surface.Loaded += (_, _) => UpdateBounds();
        surface.SizeChanged += (_, _) => UpdateBounds();
        void Capture(KeyboardEventArgs e, bool isMouse)
        {
            if (captured) return;
            if (!isMouse && e.CurrentKey == Key.Escape) { e.IsHandled = true; UiDispatcher.Current.BeginInvoke(() => dialog.Hide()); return; }
            var keys = e.Keys.Values.ToArray();
            if (!isMouse && !keys.Any(k => k is Key.Ctrl or Key.Alt or Key.Shift or Key.LWin or Key.RWin or >= Key.F1 and <= Key.F24)) return;
            if (!isMouse && keys.All(k => k is Key.Ctrl or Key.Alt or Key.Shift or Key.LWin or Key.RWin)) return;
            e.IsHandled = true;
            captured = true;
            UiDispatcher.Current.BeginInvoke(() =>
            {
                profile.HotKeys = keys.OrderByDescending(k => k is Key.LWin or Key.RWin).ThenBy(k => k).ToArray();
                profile.IsMouse = isMouse;
                profile.IsDoubleClick = false;
                if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = profile.HotKeys.HotKeysToString();
                profiles.Save();
                dialog.Hide();
            });
        }
        keyboard.Down += (_, e) => Capture(e, false);
        mouse.Down += (_, e) => { if (bounds.Contains(e.Position.X, e.Position.Y)) Capture(e, true); };
        void Deactivated(object? sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) dialog.Hide();
        }
        window.Activated += Deactivated;
        profiles.BeginRecording();
        try
        {
            dialog.Opened += (_, _) => { keyboard.Start(); mouse.Start(); };
            if (await dialog.ShowAsync() == ContentDialogResult.Secondary)
            {
                profile.HotKeys = [];
                profiles.Save();
            }
        }
        finally
        {
            keyboard.Stop(); mouse.Stop();
            window.Activated -= Deactivated;
            profiles.EndRecording();
        }
    }
}
