using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace GateKit.Platform.Windows;

/// <summary>NTFS junctions (mount points): folder links that need no privilege, unlike symbolic links.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class Junction
{
    public static void Create(string target, string link)
    {
        Directory.CreateDirectory(link);
        try
        {
            var handle = Native.CreateFile(link, Native.GenericWrite, 0, 0, Native.OpenExisting,
                Native.FileFlagBackupSemantics | Native.FileFlagOpenReparsePoint, 0);
            if (handle == Native.InvalidHandle)
                throw new IOException($"could not open {link}: Win32 error {Marshal.GetLastPInvokeError()}");
            try
            {
                var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
                var print = Encoding.Unicode.GetBytes(target);
                // REPARSE_DATA_BUFFER, MountPointReparseBuffer: both names, each followed by a NUL.
                var dataLength = 8 + substitute.Length + 2 + print.Length + 2;
                var buffer = new byte[8 + dataLength];
                BitConverter.TryWriteBytes(buffer.AsSpan(0), Native.ReparseTagMountPoint);
                BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)dataLength);
                BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);                        // substitute offset
                BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substitute.Length);
                BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substitute.Length + 2)); // print offset
                BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)print.Length);
                substitute.CopyTo(buffer, 16);
                print.CopyTo(buffer, 16 + substitute.Length + 2);
                fixed (byte* data = buffer)
                {
                    if (Native.DeviceIoControl(handle, Native.FsctlSetReparsePoint, data, (uint)buffer.Length, null, 0, out _, 0) == 0)
                        throw new IOException($"could not make the junction {link} -> {target}: Win32 error {Marshal.GetLastPInvokeError()}");
                }
            }
            finally { Native.CloseHandle(handle); }
        }
        catch
        {
            try { Directory.Delete(link); } catch (IOException) { }
            throw;
        }
    }
}
