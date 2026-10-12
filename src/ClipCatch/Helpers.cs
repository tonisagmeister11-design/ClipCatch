using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipCatch
{
    /// <summary>Reads the duration of an MP4 from its movie header, without decoding anything.</summary>
    public static class Mp4Info
    {
        public static TimeSpan? Duration(string path)
        {
            try
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new BinaryReader(f))
                {
                    long moov = FindBox(r, 0, f.Length, "moov", out long moovSize);
                    if (moov < 0) return null;
                    long mvhd = FindBox(r, moov + 8, moov + moovSize, "mvhd", out _);
                    if (mvhd < 0) return null;
                    f.Position = mvhd + 8;
                    int version = r.ReadByte();
                    f.Position += 3;
                    long timescale, duration;
                    if (version == 1)
                    {
                        f.Position += 16;
                        timescale = BigEndian(r.ReadBytes(4));
                        duration = BigEndian(r.ReadBytes(8));
                    }
                    else
                    {
                        f.Position += 8;
                        timescale = BigEndian(r.ReadBytes(4));
                        duration = BigEndian(r.ReadBytes(4));
                    }
                    if (timescale <= 0) return null;
                    return TimeSpan.FromSeconds(duration / (double)timescale);
                }
            }
            catch
            {
                return null;
            }
        }

        private static long FindBox(BinaryReader r, long start, long end, string type, out long size)
        {
            long pos = start;
            while (pos + 8 <= end)
            {
                r.BaseStream.Position = pos;
                size = BigEndian(r.ReadBytes(4));
                string name = Encoding.ASCII.GetString(r.ReadBytes(4));
                if (size == 1) size = BigEndian(r.ReadBytes(8));
                else if (size == 0) size = end - pos;
                if (size < 8) break;
                if (name == type) return pos;
                pos += size;
            }
            size = 0;
            return -1;
        }

        private static long BigEndian(byte[] b)
        {
            long v = 0;
            foreach (var x in b) v = (v << 8) | x;
            return v;
        }
    }

    public static class Format
    {
        public static string Size(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
            return $"{Math.Max(1, bytes / 1024)} KB";
        }

        public static string Duration(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>The modern Windows folder picker (IFileOpenDialog with FOS_PICKFOLDERS).</summary>
    public static class FolderPicker
    {
        private const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

        public static string Pick(IntPtr owner, string title, string initialFolder)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogRcw();
            try
            {
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
                dialog.SetTitle(title);
                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
                {
                    var iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref iid, out IShellItem folder) == 0)
                        dialog.SetFolder(folder);
                }
                int hr = dialog.Show(owner);
                if (hr == ERROR_CANCELLED) return null;
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                dialog.GetResult(out IShellItem result);
                result.GetDisplayName(SIGDN_FILESYSPATH, out string path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int place);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
