using System.Text.Json;
using Duora.CapacityClient;

// No default profile/target. stdout contains only a closed status, never URLs, bodies, credentials or exception text.
if (args.Length != 6 || args[0] != "--profile" || args[2] != "--sha256" || args[4] != "--receipt")
{
    Console.WriteLine("{\"client_completed\":false,\"operational_accepted\":false,\"reason\":\"profile_required\"}");
    return 2;
}
try
{
    var profile = PrivateFiles.ReadJson<LoadProfile>(new PinnedFile(args[1], args[3]));
    profile.Validate(DateTimeOffset.UtcNow);
    // A new private output only. Reserve it before network I/O to avoid losing the sole receipt.
    if (OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(args[5]) ||
        Path.GetDirectoryName(args[5]) != Path.GetDirectoryName(args[1]))
    { throw new LoadRejectedException(LoadStopReason.Profile); }
    using var output = new FileStream(args[5], new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
    });
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; stop.Cancel(); };
    var receipts = await LoadRunner.RunAsync(profile, stop.Token);
    var completed = receipts.Count == profile.Phases!.Length && receipts.All(x => x.Completed && x.CleanupConfirmed);
    await JsonSerializer.SerializeAsync(output, new
    {
        profile.Target!.RunId,
        profile.SourceCommit,
        profile.Coverage,
        profile.QualificationReceiptSha256,
        profile.ExternalSupervisorReceiptSha256,
        dataset_sha256 = profile.Dataset!.Sha256,
        capability_sha256 = profile.Capability!.Sha256,
        client_completed = completed,
        operational_accepted = false,
        missing = CoverageGaps.Required,
        phases = receipts
    }, PrivateFiles.JsonOptions, CancellationToken.None);
    await output.FlushAsync(CancellationToken.None);
    Console.WriteLine(completed
        ? "{\"client_completed\":true,\"operational_accepted\":false,\"reason\":\"partial_scope_completed\"}"
        : "{\"client_completed\":false,\"operational_accepted\":false,\"reason\":\"phase_refused_or_cleanup_unknown\"}");
    return completed ? 0 : 2;
}
catch (Exception)
{
    Console.WriteLine("{\"client_completed\":false,\"operational_accepted\":false,\"reason\":\"refused\"}");
    return 2;
}
