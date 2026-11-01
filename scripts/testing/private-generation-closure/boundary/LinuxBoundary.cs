using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Duora.ClosureBoundary;

/// <summary>Linux-only, checked descriptors. The root provisioner and approved authority remain trusted.</summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxBoundary
{
    private const int CloseOnExec = 0x80000;
    private static int NoFollow => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => 0x8000,
        Architecture.X64 => 0x20000,
        _ => throw new IOException("Unsupported architecture."),
    };
    private static int DirectoryOnly => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => 0x4000,
        Architecture.X64 => 0x10000,
        _ => throw new IOException("Unsupported architecture."),
    };

    internal static uint CurrentUid => GetUid();
    internal static uint CurrentGid => GetGid();

    internal static SafeFileHandle OpenDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Absolute fixture root required.");
        }

        var current = Checked(Open("/", DirectoryOnly | NoFollow | CloseOnExec));
        try
        {
            foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (component is "." or "..")
                {
                    throw new InvalidDataException("Invalid root component.");
                }

                var next = OpenChild(current, component, directory: true);
                current.Dispose();
                current = next;
                var info = Stat(current);
                if (info.Uid != 0 || (info.Mode & 0x12) != 0)
                {
                    throw new InvalidDataException("Untrusted root ancestor.");
                }
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenChild(SafeFileHandle parent, string name, bool directory, bool writable = false)
    {
        if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal) || name is "." or "..")
        {
            throw new InvalidDataException("Invalid child name.");
        }

        return Checked(OpenAt(parent, name, CloseOnExec | NoFollow | (directory ? DirectoryOnly : 0) | (writable ? 2 : 0)));
    }

    internal static FileStatus Stat(SafeFileHandle handle)
    {
        if (Statx(handle, "", 0x1000, 0x7ff, out var result) != 0 || (result.Mask & 0x31f) != 0x31f)
        {
            throw Failure("statx");
        }

        return result;
    }

    internal static void Verify(SafeFileHandle handle, NodeIdentity expected, uint uid, uint gid, int mode, bool directory)
    {
        var info = Stat(handle);
        if (info.Inode != expected.Inode || info.DeviceMajor != expected.DeviceMajor || info.DeviceMinor != expected.DeviceMinor
            || info.Uid != uid || info.Gid != gid || (info.Mode & 0xfff) != mode
            || (info.Mode & 0xf000) != (directory ? 0x4000 : 0x8000) || (!directory && info.Links != 1))
        {
            throw new InvalidDataException("Control identity or permissions changed.");
        }
    }

    internal static bool TryLock(SafeFileHandle handle)
    {
        if (Flock(handle, 2 | 4) == 0)
        {
            return true;
        }

        if (Marshal.GetLastPInvokeError() == 11)
        {
            return false;
        }

        throw Failure("flock");
    }

    internal static void Sync(SafeFileHandle handle)
    {
        if (Fsync(handle) != 0)
        {
            throw Failure("fsync");
        }
    }

    internal static PeerIdentity Peer(Socket socket)
    {
        uint length = 12;
        if (GetSockOpt(socket.SafeHandle, 1, 17, out var peer, ref length) != 0 || length != 12)
        {
            throw Failure("SO_PEERCRED");
        }

        return new PeerIdentity(peer.Uid, peer.Gid, peer.Pid);
    }

    internal static T ReadJson<T>(SafeFileHandle handle) where T : class
    {
        using var file = new FileStream(handle, FileAccess.Read);
        if (file.Length is 0 or > 1048576)
        {
            throw new InvalidDataException("Invalid control size.");
        }

        return JsonSerializer.Deserialize<T>(file, Wire.Options) ?? throw new InvalidDataException("Empty control.");
    }

    private static SafeFileHandle Checked(int descriptor) => descriptor < 0
        ? throw Failure("open") : new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);

    // Only numeric errno/type, never paths or private request fields.
    private static IOException Failure(string operation) => new($"{operation} failed ({Marshal.GetLastPInvokeError()}).");

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct FileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(24)] public uint Gid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credentials
    {
        public int Pid;
        public int Uid;
        public int Gid;
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(SafeFileHandle parent, string path, int flags);
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(SafeFileHandle descriptor, string path, int flags, uint mask, out FileStatus status);
    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(SafeFileHandle descriptor, int operation);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(SafeFileHandle descriptor);
    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetSockOpt(SafeSocketHandle socket, int level, int option, out Credentials value, ref uint length);
    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();
    [LibraryImport("libc", EntryPoint = "getgid")]
    private static partial uint GetGid();
}
