using System.Runtime.InteropServices;

namespace Tasky;

internal sealed record RecentItem(string Name, string Path);

/// <summary>
/// An app's recent documents, as Windows tracks them for the taskbar's jump lists. Only the Recent
/// list is public (IApplicationDocumentLists); apps' custom Tasks are readable by Explorer alone.
/// </summary>
internal static class JumpList
{
    public static List<RecentItem> Recent(string appId, int max)
    {
        var result = new List<RecentItem>();
        IApplicationDocumentLists? lists = null;
        try
        {
            lists = (IApplicationDocumentLists)new ApplicationDocumentLists();
            lists.SetAppID(appId);
            var iid = typeof(IObjectArray).GUID;
            if (lists.GetList(0 /* ADLT_RECENT */, (uint)max, ref iid, out var obj) != 0 || obj is not IObjectArray array) return result;
            try
            {
                array.GetCount(out uint count);
                var itemIid = typeof(Native.IShellItem).GUID;
                for (uint i = 0; i < count && result.Count < max; i++)
                {
                    if (array.GetAt(i, ref itemIid, out var o) != 0 || o is not Native.IShellItem item) continue; // links (not files) are skipped
                    try
                    {
                        item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var name);
                        item.GetDisplayName(unchecked((int)0x80028000) /* SIGDN_DESKTOPABSOLUTEPARSING */, out var path);
                        // Virtual shell locations without a friendly name come back as a bare {GUID}.
                        bool unnamed = name.StartsWith('{') && name.EndsWith('}');
                        if (!string.IsNullOrEmpty(path) && !unnamed) result.Add(new RecentItem(name, path));
                    }
                    catch { }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            finally { Marshal.ReleaseComObject(array); }
        }
        catch (Exception ex) { Log.Error(ex, $"recent items for {appId}"); }
        finally { if (lists != null) Marshal.ReleaseComObject(lists); }
        return result;
    }

    /// <summary>
    /// The App User Model ID Windows files an exe's recent documents under when the app doesn't set
    /// its own: the exe's path, with a known-folder GUID in place of the folder.
    /// </summary>
    public static string ImplicitAppId(string exePath)
    {
        if (exePath.EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase)) return "Microsoft.Windows.Explorer";
        var folders = new (Environment.SpecialFolder Folder, string Guid)[]
        {
            (Environment.SpecialFolder.System, "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"),
            (Environment.SpecialFolder.ProgramFilesX86, "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"),
            (Environment.SpecialFolder.ProgramFiles, "{6D809377-6AF0-444B-8957-A3773F02200E}"),
            (Environment.SpecialFolder.LocalApplicationData, "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"),
            (Environment.SpecialFolder.ApplicationData, "{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"),
            (Environment.SpecialFolder.Windows, "{F38BF404-1D43-42F2-9305-67DE0B28FC23}"),
        };
        foreach (var (folder, guid) in folders)
        {
            var root = Environment.GetFolderPath(folder).TrimEnd('\\') + '\\';
            if (root.Length > 1 && exePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return guid + '\\' + exePath[root.Length..];
        }
        return exePath;
    }

    [ComImport, Guid("86BEC222-30F2-47E0-9F25-60D11CD75C28")]
    class ApplicationDocumentLists { }

    [ComImport, Guid("3C594F9F-9F30-47A1-979A-C9E83D3D0A06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationDocumentLists
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int GetList(int listType, uint itemsDesired, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object list);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectArray
    {
        void GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);
    }
}
