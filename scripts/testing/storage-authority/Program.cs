using Duora.IntegrationTests.Media;

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("Linux runtime/UID acceptance must execute on Linux.");
    return 2;
}
using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
try
{
    if (args.Length == 1 && args[0] == "--alias-check")
    {
        await StorageAuthorityRuntimeChecks.RunAliasRegressionAsync(budget.Token);
        return 0;
    }
    if (args.Length == 3 && args[0] == "--client")
    {
        await StorageAuthorityRuntimeChecks.RunClientAsync(args[1], args[2], budget.Token);
        return 0;
    }
    await StorageAuthorityRuntimeChecks.RunAsync(budget.Token);
    await StorageAuthorityRuntimeChecks.RunAliasRegressionAsync(budget.Token);
    Console.WriteLine("PASS storage authority integrated synthetic acceptance; no operational qualification");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL storage authority acceptance: " + error);
    return 1;
}
