using System.IO;
using System.Runtime.InteropServices;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.App.Services;

/// <summary>Sends files and folders to the Recycle Bin with SHFileOperation (FOF_ALLOWUNDO).</summary>
public sealed class RecycleBinDeleter : IFileDeleter
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    public void Delete(string path)
    {
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0", // marshaling adds the second terminating null
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };

        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
        {
            throw new IOException($"Couldn't move \"{Path.GetFileName(path)}\" to the Recycle Bin (error 0x{result:X}).");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
