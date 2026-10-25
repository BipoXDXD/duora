using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Duora.CapacityClient;

public sealed class LoadProfile
{
    public string? Coverage { get; init; }
    public TargetBinding? Target { get; init; }
    public string? SourceCommit { get; init; }
    public PinnedFile? Dataset { get; init; }
    public PinnedFile? Capability { get; init; }
    public PinnedFile? CertificateAuthority { get; init; }
    public PinnedFile? ClientCertificate { get; init; }
    public PinnedFile? ClientKey { get; init; }
    public string? ServerCertificateSha256 { get; init; }
    public string? LeasePath { get; init; }
    public string? LeaseProducerSha256 { get; init; }
    public string? QualificationReceiptSha256 { get; init; }
    public string? ExternalSupervisorReceiptSha256 { get; init; }
    public DateTimeOffset? AdmittedUntil { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public LoadPhase[]? Phases { get; init; }
    public int MaxRequestsPerPhase { get; init; }
    public int MaxInFlight { get; init; }
    public int TickMilliseconds { get; init; }

    public void Validate(DateTimeOffset now)
    {
        // An operator-pinned profile is admission input, never proof of runtime isolation/capacity.
        if (Coverage is not ("http-read" or "signalr-invalidation") || Target is null ||
            !Pins.IsHex(SourceCommit, 40) || !Pins.IsHex(ServerCertificateSha256, 64) ||
            !Pins.IsHex(LeaseProducerSha256, 64) || !Pins.IsHex(QualificationReceiptSha256, 64) ||
            !Pins.IsHex(ExternalSupervisorReceiptSha256, 64) || Dataset is null || Capability is null ||
            CertificateAuthority is null || ClientCertificate is null || ClientKey is null ||
            LeasePath is null || !Path.IsPathFullyQualified(LeasePath) ||
            AdmittedUntil is null || StartedAt is null || StartedAt > now ||
            now - StartedAt > TimeSpan.FromSeconds(3) || AdmittedUntil <= now ||
            MaxRequestsPerPhase is < 1 or > 1000000 || MaxInFlight is < 1 or > 100 ||
            TickMilliseconds is < 1000 or > 5000 || Phases is null)
        { throw new LoadRejectedException(LoadStopReason.Profile); }
        Target.Validate();
        PhasePlan.Validate(Phases);
        var seconds = Phases.Sum(p => p.Seconds + 120);
        if (AdmittedUntil < StartedAt.Value.AddSeconds(seconds))
        { throw new LoadRejectedException(LoadStopReason.Profile); }
    }
}

public sealed record LoadPhase(string Name, int Users, int Seconds, bool Reconnect);

public static class PhasePlan
{
    public static LoadPhase[] Required()
    {
        var phases = new List<LoadPhase>();
        for (var repetition = 1; repetition <= 3; repetition++)
        {
            phases.Add(new($"warmup-{repetition}", 10, 300, false));
            foreach (var users in new[] { 10, 25, 50, 100 })
            { phases.Add(new($"ramp-{repetition}-{users}", users, 300, false)); }
            phases.Add(new($"steady-{repetition}", 100, 1800, false));
            phases.Add(new($"burst-{repetition}", 100, 60, false));
            for (var reconnect = 1; reconnect <= 3; reconnect++)
            { phases.Add(new($"reconnect-{repetition}-{reconnect}", 100, 10, true)); }
            phases.Add(new($"recovery-{repetition}", 10, 900, false));
        }
        return [.. phases];
    }
    public static void Validate(LoadPhase[] phases)
    {
        if (!phases.SequenceEqual(Required()))
        { throw new LoadRejectedException(LoadStopReason.Profile); }
    }
}

public sealed class SequenceReceipt
{
    public long Received { get; private set; }
    public long Gaps { get; private set; }
    public long Duplicates { get; private set; }
    public long OutOfOrder { get; private set; }
    private long _last;
    private readonly object _gate = new();
    public void Observe(long sequence)
    {
        lock (_gate)
        {
            if (sequence <= 0)
            { throw new LoadRejectedException(LoadStopReason.Capability); }
            Received++;
            if (sequence == _last)
            { Duplicates++; }
            else if (sequence < _last)
            { OutOfOrder++; }
            else
            { Gaps += sequence - _last - 1; _last = sequence; }
        }
    }
}

public sealed record PinnedFile(string Path, string Sha256);
public sealed record SyntheticUser(Guid Id, string Email, string Password, Guid RoomId);
public sealed record SyntheticDataset(string RunId, string SourceCommit, bool SyntheticOnly, SyntheticUser[] Users);
public sealed record CapabilityBinding(string RunId, string SourceCommit, string DatasetSha256,
    bool OperatorQualified, PinnedFile OpenApi, string? HubPath, string? Notification,
    string? SequenceProperty, string? SignalRContractReceiptSha256);

public static class Pins
{
    public static bool IsHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void ValidateDataset(SyntheticDataset data, LoadProfile profile)
    {
        if (!data.SyntheticOnly || data.RunId != profile.Target!.RunId || data.SourceCommit != profile.SourceCommit ||
            data.Users.Length != 100 || data.Users.Select(x => x.Id).Distinct().Count() != 100 ||
            data.Users.Select(x => x.Email).Distinct(StringComparer.Ordinal).Count() != 100 ||
            data.Users.Any(x => x.Id == Guid.Empty || x.RoomId == Guid.Empty || x.Password.Length is < 12 or > 128 ||
                x.Email != $"participant-{x.Id:N}@{data.RunId}.invalid"))
        { throw new LoadRejectedException(LoadStopReason.Profile); }
    }
}

public static class PrivateFiles
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static byte[] Read(string path, int maxBytes = 1048576)
    {
        if (OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path))
        { throw new LoadRejectedException(LoadStopReason.Profile); }
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || file.Length > maxBytes || file.Length == 0 ||
            (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        { throw new LoadRejectedException(LoadStopReason.Profile); }
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null ||
                (File.GetUnixFileMode(directory.FullName) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            { throw new LoadRejectedException(LoadStopReason.Profile); }
        }
        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        int read;
        while ((read = stream.Read(block)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            { throw new LoadRejectedException(LoadStopReason.Profile); }
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    public static byte[] ReadPinned(PinnedFile file)
    {
        var bytes = Read(file.Path);
        if (!Pins.IsHex(file.Sha256, 64) || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
        { throw new LoadRejectedException(LoadStopReason.Profile); }
        return bytes;
    }

    public static T ReadJson<T>(PinnedFile file) =>
        JsonSerializer.Deserialize<T>(ReadPinned(file), JsonOptions) ?? throw new LoadRejectedException(LoadStopReason.Profile);
}
