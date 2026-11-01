using Duora.IntegrationTests.Media;

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("Linux runtime/UID acceptance must execute on Linux.");
    return 2;
}
using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
if (args.Length == 3 && args[0] == "--client")
{
    await StorageAuthorityRuntimeChecks.RunClientAsync(args[1], args[2], budget.Token);
    return 0;
}
await StorageAuthorityRuntimeChecks.RunAsync(budget.Token);
Console.WriteLine("PASS storage authority integrated synthetic acceptance; no operational qualification");
return 0;
