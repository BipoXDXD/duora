using System.Runtime.Versioning;
using Duora.Core.Media;
using Microsoft.Win32.SafeHandles;

namespace Duora.Infrastructure.Media;

internal sealed record PrivateGenerationEnrollment(PrivateGenerationDomain Domain, Guid Incarnation, uint AuthorityUid,
    uint[] ClientUids, PrivateGenerationNode Root, PrivateGenerationNode Control,
    Dictionary<string, PrivateGenerationNode> Files, Dictionary<string, PrivateGenerationNode> Data);

[SupportedOSPlatform("linux")]
internal sealed class PrivateGenerationLayout
{
    internal string Root { get; }
    internal PrivateGenerationEnrollment Enrollment { get; }
    internal string DataRoot => Path.Combine(Root, "data");
    internal string SocketPath => Path.Combine(Root, "rpc", "authority.sock");

    internal PrivateGenerationLayout(string root)
    {
        Root = root;
        using var directory = PrivateGenerationLinux.OpenDirectory(root);
        using var file = PrivateGenerationLinux.OpenChild(directory, "enrollment.json", directory: false);
        var info = PrivateGenerationLinux.Stat(file);
        if (info.Uid != 0 || (info.Mode & 0xfff) != 288 || info.Links != 1)
        {
            throw new InvalidDataException("Untrusted storage enrollment.");
        }
        Enrollment = PrivateGenerationLinux.ReadJson<PrivateGenerationEnrollment>(file);
        Enrollment.Domain.Validate();
        if (Enrollment.Incarnation == Guid.Empty || Enrollment.AuthorityUid == 0 || info.Gid != Enrollment.AuthorityUid
            || Enrollment.AuthorityUid != PrivateGenerationLinux.CurrentUid
            || Enrollment.AuthorityUid != PrivateGenerationLinux.CurrentGid
            || Enrollment.ClientUids is not { Length: > 0 and <= 16 }
            || Enrollment.ClientUids.Contains(Enrollment.AuthorityUid))
        {
            throw new InvalidDataException("Unadmitted storage identity.");
        }
        Validate();
    }

    internal void Validate()
    {
        var uid = Enrollment.AuthorityUid;
        using var root = PrivateGenerationLinux.OpenDirectory(Root);
        PrivateGenerationLinux.Verify(root, Enrollment.Root, 0, 0, 493, directory: true);
        using var control = PrivateGenerationLinux.OpenChild(root, "control", directory: true);
        PrivateGenerationLinux.Verify(control, Enrollment.Control, 0, uid, 488, directory: true);
        foreach (var name in new[] { "domain.lock", "domain.journal" })
        {
            using var file = OpenControl(name);
        }
        using var data = PrivateGenerationLinux.OpenChild(root, "data", directory: true);
        var dataInfo = PrivateGenerationLinux.Stat(data);
        if (dataInfo.Uid != 0 || (dataInfo.Mode & 0xfff) != 493)
        {
            throw new InvalidDataException("Untrusted storage data parent.");
        }
        foreach (var area in new[] { "review-candidates", "derivatives" })
        {
            using var directory = PrivateGenerationLinux.OpenChild(data, area, directory: true);
            PrivateGenerationLinux.Verify(directory, Enrollment.Data[area], uid, uid, 448, directory: true);
        }
        using var rpc = PrivateGenerationLinux.OpenChild(root, "rpc", directory: true);
        var rpcInfo = PrivateGenerationLinux.Stat(rpc);
        if (rpcInfo.Uid != 0 || rpcInfo.Gid != uid || (rpcInfo.Mode & 0xfff) != 505)
        {
            throw new InvalidDataException("Untrusted storage RPC parent.");
        }
    }

    internal SafeFileHandle OpenControl(string name)
    {
        using var root = PrivateGenerationLinux.OpenDirectory(Root);
        PrivateGenerationLinux.Verify(root, Enrollment.Root, 0, 0, 493, directory: true);
        using var control = PrivateGenerationLinux.OpenChild(root, "control", directory: true);
        PrivateGenerationLinux.Verify(control, Enrollment.Control, 0, Enrollment.AuthorityUid, 488, directory: true);
        var file = PrivateGenerationLinux.OpenChild(control, name, directory: false, writable: true);
        try
        {
            PrivateGenerationLinux.Verify(file, Enrollment.Files[name], 0, Enrollment.AuthorityUid, 432, directory: false);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal void SyncArea(PrivateGenerationArea area)
    {
        using var data = PrivateGenerationLinux.OpenDirectory(DataRoot);
        var name = area == PrivateGenerationArea.Candidate ? "review-candidates" : "derivatives";
        using var directory = PrivateGenerationLinux.OpenChild(data, name, directory: true);
        PrivateGenerationLinux.Verify(directory, Enrollment.Data[name], Enrollment.AuthorityUid,
            Enrollment.AuthorityUid, 448, directory: true);
        PrivateGenerationLinux.Sync(directory);
    }
}
