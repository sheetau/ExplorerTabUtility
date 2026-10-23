using SHDocVw;

namespace ExplorerTabUtility.Models;

public class WindowInfo
{
    internal ExplorerTabUtility.Interop.ShellViewReady? InitialViewReady { get; set; }
    internal Shell32.DShellFolderViewEvents_Event? SelectionView { get; set; }
    internal Shell32.DShellFolderViewEvents_SelectionChangedEventHandler? OnSelectionChanged { get; set; }
    internal bool SelectionActivationPending { get; set; }
    public bool IsConversionSource { get; set; }
    public string? Location { get; set; }
    public string? Name { get; set; }
    public DWebBrowserEvents2_OnQuitEventHandler? OnQuitHandler { get; set; }
    public DWebBrowserEvents2_NavigateComplete2EventHandler? OnNavigateHandler { get; set; }
}