using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Duora.ClosureBoundary;

[SupportedOSPlatform("linux")]
internal sealed class ProtectedLayout
{
    private static readonly string[] ControlNames = ["domain.lock", "domain.journal"];
    private static readonly string[] AreaNames = ["review-candidates", "derivatives"];
    internal string Root { get; }
    internal uint Group { get; }
    internal Enrollment Enrollment { get; }

    internal ProtectedLayout(string root, uint group)
    {
        Root = root;
        Group = group;
        using var directory = LinuxBoundary.OpenDirectory(root);
        using var enrollment = LinuxBoundary.OpenChild(directory, "enrollment.json", directory: false);
        var info = LinuxBoundary.Stat(enrollment);
        if (info.Uid != 0 || info.Gid != group || (info.Mode & 0xffff) != 0x8120 || info.Links != 1)
        {
            throw new InvalidDataException("Untrusted enrollment.");
        }

        Enrollment = LinuxBoundary.ReadJson<Enrollment>(enrollment);
        if (Enrollment.Domain == Guid.Empty || Enrollment.Epoch != 1 || Enrollment.ProtocolVersion != 2
            || Enrollment.Root is null || Enrollment.Control is null
            || Enrollment.Files is null || Enrollment.Data is null || Enrollment.Generations is null
            || !ControlNames.All(k => Enrollment.Files.TryGetValue(k, out var v) && v is not null)
            || !AreaNames.All(k => Enrollment.Data.TryGetValue(k, out var v) && v is not null)
            || Enrollment.Generations.Length is < 1 or > 32
            || Enrollment.Generations.Any(g => g is null || g.Generation == Guid.Empty || g.Area is not ("candidate" or "promotion") || !Nonce(g.Nonce))
            || Enrollment.Generations.Select(g => g.Generation).Distinct().Count() != Enrollment.Generations.Length)
        {
            throw new InvalidDataException("Invalid enrollment.");
        }

        Validate();
    }

    internal void Validate()
    {
        using var root = LinuxBoundary.OpenDirectory(Root);
        LinuxBoundary.Verify(root, Enrollment.Root, 0, 0, 493, directory: true);
        using var directory = LinuxBoundary.OpenChild(root, "control", directory: true);
        LinuxBoundary.Verify(directory, Enrollment.Control, 0, Group, 488, directory: true);
        foreach (var name in ControlNames)
        {
            using var file = OpenControl(name);
        }

        if (Group == 17010)
        {
            using var data = LinuxBoundary.OpenChild(root, "data", directory: true);
            var info = LinuxBoundary.Stat(data);
            if (info.Uid != 0 || info.Gid != 0 || (info.Mode & 0xfff) != 493)
            {
                throw new InvalidDataException("Untrusted data parent.");
            }

            foreach (var area in AreaNames)
            {
                using var areaDirectory = LinuxBoundary.OpenChild(data, area, directory: true);
                LinuxBoundary.Verify(areaDirectory, Enrollment.Data[area], 17010, 17010, 448, directory: true);
            }
        }
    }

    internal SafeFileHandle OpenControl(string name)
    {
        using var root = LinuxBoundary.OpenDirectory(Root);
        LinuxBoundary.Verify(root, Enrollment.Root, 0, 0, 493, directory: true);
        using var directory = LinuxBoundary.OpenChild(root, "control", directory: true);
        LinuxBoundary.Verify(directory, Enrollment.Control, 0, Group, 488, directory: true);
        var file = LinuxBoundary.OpenChild(directory, name, directory: false, writable: true);
        try
        {
            LinuxBoundary.Verify(file, Enrollment.Files[name], 0, Group, 432, directory: false);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal GenerationRegistration? Match(WriteGrant? grant)
    {
        if (grant is null || grant.Domain != Enrollment.Identity || !Nonce(grant.Nonce))
        {
            return null;
        }

        return Enrollment.Generations.SingleOrDefault(g => g.Generation == grant.Generation && g.Area == grant.Area
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(g.Nonce), Encoding.ASCII.GetBytes(grant.Nonce)));
    }

    private static bool Nonce(string value) => value is { Length: 64 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
}
