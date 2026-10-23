using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Shell32;

namespace ExplorerTabUtility.Interop;

internal static class PinnedFolderReader
{
    // Explorer's jump-list pins are also exposed by its Quick Access namespace.
    // Keep all paths for a display name: duplicate names must not be guessed.
    public static Dictionary<string, HashSet<string>> Read()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        Shell? shell = null;
        Folder? folder = null;
        FolderItems? items = null;
        try
        {
            shell = new Shell();
            folder = shell.NameSpace("shell:::{679F85CB-0220-4080-B29B-5540CC05AAB6}");
            if (folder == null) return result;
            items = folder.Items();
            for (var i = 0; i < items.Count; i++)
            {
                FolderItem? item = null;
                try
                {
                    item = items.Item(i);
                    if (item is not FolderItem2 extended || extended.ExtendedProperty("System.Home.IsPinned") is not true || !item.IsFolder) continue;
                    var name = item.Name;
                    var path = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                    if (!result.TryGetValue(name, out var paths))
                        result.Add(name, paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    paths.Add(path);
                }
                catch (COMException) { }
                finally { if (item != null) Marshal.ReleaseComObject(item); }
            }
            return result;
        }
        finally
        {
            if (items != null) Marshal.ReleaseComObject(items);
            if (folder != null) Marshal.ReleaseComObject(folder);
            if (shell != null) Marshal.ReleaseComObject(shell);
        }
    }
}
