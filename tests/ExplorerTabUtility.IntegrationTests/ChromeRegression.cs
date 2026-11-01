using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.WinAPI;

internal static partial class Program
{
    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFocusAutomation
    {
        void _VtblGap1_5();
        void GetFocusedElement(out IFocusElement element);
    }
    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFocusElement
    {
        void _VtblGap1_7();
        void GetCurrentPropertyValue(int id, [MarshalAs(UnmanagedType.Struct)] out object value);
    }
    private static bool AddressHasFocus()
    {
        if (WinApi.GetForegroundWindow() != _target) return false;
        var automation = (IFocusAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E"))!)!;
        try
        {
            automation.GetFocusedElement(out var element);
            if (element == null) return false;
            try
            {
                element.GetCurrentPropertyValue(30003, out var type);
                element.GetCurrentPropertyValue(30011, out var id);
                element.GetCurrentPropertyValue(30005, out var name);
                if (type is 50004) Console.WriteLine($"Focused element: type={type} id={id} name={name}");
                return type is 50004;
            }
            finally { Marshal.ReleaseComObject(element); }
        }
        finally { Marshal.ReleaseComObject(automation); }
    }
    private static async Task CheckAddressFocus()
    {
        if (!Helper.IsFileExplorerWindow(_target)) throw new Exception("Fixture window closed before the address-bar check; this does not establish a chrome freeze.");
        WinApi.RestoreWindowToForeground(_target);
        KeyboardSimulator.ModifiedKeyStroke(VirtualKey.LeftControl, VirtualKey.L);
        await Until(AddressHasFocus, "address bar accepts Ctrl+L after tab close");
        KeyboardSimulator.SendKeyPress(VirtualKey.Escape);
    }
    private static async Task ChromeRegression(string anchor, string folder)
    {
        for (var cycle = 0; cycle < (_detachOnly ? 0 : 12); cycle++)
        {
            await _watcher!.SwitchTo(folder, _target, new[] { Path.Combine(folder, "sample.txt") });
            await Until(() => HasSelection(folder, new[] { "sample.txt" }), "selected fixture tab");
            for (var i = 0; i < 3; i++)
                await _watcher.DuplicateActiveTab(_target, true);
            await Until(() => Helper.GetAllExplorerTabs(_target).Count() == 5, "five fixture tabs");
            WinApi.RestoreWindowToForeground(_target);
            // Keep Ctrl held as in repeated physical Ctrl+W, without replacing
            // Explorer's close command with an automation API.
            KeyboardSimulator.SendKeyDown(VirtualKey.LeftControl);
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    if (WinApi.GetForegroundWindow() != _target || Helper.GetAllExplorerTabs(_target).Count() <= 1)
                        throw new Exception("Fixture lost focus or its extra tabs before input completed.");
                    KeyboardSimulator.SendKeyPress(VirtualKey.W);
                    await Task.Delay(75); // Test input pacing only.
                }
            }
            finally { KeyboardSimulator.SendKeyUp(VirtualKey.LeftControl); }
            await Task.Delay(1000);
            await CheckAddressFocus();
            await Until(() => Helper.GetAllExplorerTabs(_target).Count() == 1, "four rapid native Ctrl+W closes");
            Console.WriteLine($"PASS tab close cycle {cycle + 1}: address bar responds");
            await _watcher.SwitchTo(anchor, _target);
        }

        var detached = Path.Combine(Root, "detached tab");
        Directory.CreateDirectory(detached);
        File.WriteAllText(Path.Combine(detached, "selected.txt"), "detach check");
        await _watcher!.SwitchTo(detached, _target, new[] { Path.Combine(detached, "selected.txt") });
        await _watcher.DetachCurrentTab(_target);
        await Until(() => Find(detached) is { } window && (nint)(int)window.HWND != _target &&
            (int)window.Document.SelectedItems().Count == 1 &&
            (string)window.Document.SelectedItems().Item(0).Path == Path.Combine(detached, "selected.txt"), "detached tab keeps selection in a separate window");
        await CheckAddressFocus();
        Console.WriteLine("PASS detach preserves selection and leaves the original address bar responsive");
    }
}
