using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Duora.ClosureProbe;

[SupportedOSPlatform("linux")]
internal static partial class LinuxStorage
{
    private const int CloseOnExec = 0x80000;
    // Linux arm64 and x86-64 have different open(2) flag values.
    private static int NoFollow => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => 0x8000,
        Architecture.X64 => 0x20000,
        _ => throw new IOException("Unsupported Linux architecture."),
    };

    private static int DirectoryOnly => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => 0x4000,
        Architecture.X64 => 0x10000,
        _ => throw new IOException("Unsupported Linux architecture."),
    };
    private const int ReadWrite = 2;

    internal static SafeFileHandle OpenLock(string path) => OpenChecked(path, ReadWrite | CloseOnExec | NoFollow);

    internal static bool TryLock(SafeFileHandle handle)
    {
        if (Flock(handle, 2 | 4) == 0) // LOCK_EX | LOCK_NB
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == 11) // EWOULDBLOCK on qualified Linux architectures only.
        {
            return false;
        }

        throw new IOException("flock failed.", new Win32Exception(error));
    }

    internal static void SyncDirectory(string path)
    {
        using var handle = OpenChecked(path, DirectoryOnly | CloseOnExec | NoFollow);
        if (Fsync(handle) != 0)
        {
            throw new IOException("Directory fsync failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    private static SafeFileHandle OpenChecked(string path, int flags)
    {
        var descriptor = Open(path, flags);
        if (descriptor < 0)
        {
            throw new IOException("open failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(SafeFileHandle descriptor, int operation);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(SafeFileHandle descriptor);
}
