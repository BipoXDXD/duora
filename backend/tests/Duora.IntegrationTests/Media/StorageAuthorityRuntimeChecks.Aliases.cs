using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Duora.IntegrationTests.Media;

public static partial class StorageAuthorityRuntimeChecks
{
    public static async Task RunAliasRegressionAsync(CancellationToken cancellationToken)
    {
        Assert.Equal(0U, Duora.Infrastructure.Media.PrivateGenerationLinux.CurrentUid);
        await using var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        await using var database = await postgres.CreateMigratedScratchDatabaseAsync(cancellationToken);
        await using var owner = NpgsqlDataSource.Create(database.MigratorConnectionString);
        // Deliberately create both schemas in ONE disposable database to prove aliases cannot bypass admission.
        await ExecuteAsync(owner, await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot.Find(), "scripts/storage-authority/reference.sql"), cancellationToken),
            cancellationToken);
        var domain = new Duora.Core.Media.PrivateGenerationDomain(Guid.NewGuid(), 1, 1);
        var incarnation = Guid.NewGuid();
        await ExecuteAsync(owner, """
            insert into storage_control.epochs(domain,epoch,layout,incarnation,active) values($1,1,1,$2,true)
            """, cancellationToken, domain.Id, incarnation);
        var direct = new NpgsqlConnectionStringBuilder(database.MigratorConnectionString);
        var addresses = await Dns.GetHostAddressesAsync(direct.Host
            ?? throw new InvalidDataException("Missing fixture host."), cancellationToken);
        direct.Host = addresses.First(address => address.AddressFamily == AddressFamily.InterNetwork).ToString();
        Assert.NotEqual(new NpgsqlConnectionStringBuilder(database.AppConnectionString).Host, direct.Host);
        var root = "/duora-authority-alias-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        try
        {
            Provision(root, domain, incarnation, database.AppConnectionString, direct.ConnectionString);
            await using var denied = await AuthorityProcess.StartAsync(root, expectReady: false, cancellationToken);
            Assert.Equal(2, denied.Process.ExitCode);
            Assert.Equal(1L, await ScalarAsync(owner,
                "select count(*) from storage_control.epochs where domain=$1 and session is null",
                cancellationToken, domain.Id));
            Console.WriteLine("PASS aliases of the same real database are rejected before epoch consumption");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
