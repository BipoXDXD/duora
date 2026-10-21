using DotNet.Testcontainers.Configurations;
using Duora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Duora.IntegrationTests.PostgresFixture))]

namespace Duora.IntegrationTests;

/// <summary>
/// Um PostgreSQL descartável por execução da suíte, na mesma versão fixada no Compose de desenvolvimento
/// (garantido por <see cref="ComposeFileTests"/>), com os papéis da imagem de produção: o mesmo script de initdb de
/// <c>infra/images/postgres</c> cria <c>duora_migrator</c>, <c>duora_app</c>, o banco <c>duora</c> e os default
/// privileges. As migrations rodam como <c>duora_migrator</c>, como fará o job migrador, seguidas de
/// <c>reconcile-privileges.sql</c>, como faz o deploy; a aplicação nunca migra no startup e os testes conectam como
/// <c>duora_app</c>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:18.6";

    /// <summary>Banco criado pelo initdb da imagem.</summary>
    public const string DatabaseName = "duora";

    // Mesmos caminhos da imagem derivada (infra/images/postgres/Dockerfile).
    private const string InitdbScriptTarget = "/docker-entrypoint-initdb.d/10-duora-roles.sh";
    private const string ReconcileScriptTarget = "/usr/local/share/duora/reconcile-privileges.sql";
    private const string MigratorPasswordTarget = "/run/secrets/db_migrator_password";
    private const string AppPasswordTarget = "/run/secrets/db_app_password";
    private const string BackupPasswordTarget = "/run/secrets/db_backup_password";

    // Cópia do banco recém-criado pelo initdb, antes das migrations: os bancos avulsos nascem dela com os mesmos
    // default privileges, sem repetir o SQL do initdb. Ninguém conecta nela (CREATE DATABASE ... TEMPLATE exige).
    private const string PristineTemplate = "duora_pristine";

    private const UnixFileModes Executable = UnixFileModes.UserRead | UnixFileModes.UserWrite |
        UnixFileModes.UserExecute | UnixFileModes.GroupRead | UnixFileModes.GroupExecute | UnixFileModes.OtherRead |
        UnixFileModes.OtherExecute;

    // Senhas sintéticas e descartáveis, novas a cada execução; nunca ficam no repositório.
    private readonly string _migratorPassword = Guid.NewGuid().ToString("N");
    private readonly string _appPassword = Guid.NewGuid().ToString("N");

    // O initdb também cria o papel duora_backup do pgBackRest; os testes não o usam, mas o script exige a senha.
    private readonly string _backupPassword = Guid.NewGuid().ToString("N");

    /// <summary>Teto de conexões do servidor de teste (ver o construtor); conferido por um teste.</summary>
    public const int MaxConnections = 400;

    private readonly PostgreSqlContainer _container;

    public PostgresFixture()
    {
        var images = Path.Combine(RepositoryRoot.Find(), "infra", "images", "postgres");
        _container = new PostgreSqlBuilder(Image)
            .WithResourceMapping(File.ReadAllBytes(Path.Combine(images, "initdb", "10-duora-roles.sh")),
                InitdbScriptTarget, fileMode: Executable)
            .WithResourceMapping(File.ReadAllBytes(Path.Combine(images, "reconcile-privileges.sql")),
                ReconcileScriptTarget)
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(_migratorPassword), MigratorPasswordTarget)
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(_appPassword), AppPasswordTarget)
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(_backupPassword), BackupPasswordTarget)
            .WithEnvironment("DUORA_MIGRATOR_PASSWORD_FILE", MigratorPasswordTarget)
            .WithEnvironment("DUORA_APP_PASSWORD_FILE", AppPasswordTarget)
            .WithEnvironment("DUORA_BACKUP_PASSWORD_FILE", BackupPasswordTarget)
            // A suíte sobe dezenas de hosts web em paralelo contra este servidor, cada um com o próprio pool do
            // Npgsql (conexões ociosas ficam abertas); com o padrão de 100 o pico já passava de 90 e os testes de
            // concorrência recebiam 53300. Vale só para o servidor de teste: produção segue o compose.prod.yml.
            .WithCommand("-c", $"max_connections={MaxConnections}")
            .Build();
    }

    /// <summary>Conexão da aplicação (<c>duora_app</c>) no banco compartilhado e migrado.</summary>
    public string ConnectionString => ConnectAs("duora_app", _appPassword, DatabaseName);

    internal string ContainerId => _container.Id;

    /// <summary>Conexão do migrador, dono das tabelas: para inspecionar o catálogo sem o filtro de privilégios do
    /// <c>information_schema</c> e para ler o histórico de migrations, que o app não enxerga.</summary>
    public string MigratorConnectionString => ConnectAs("duora_migrator", _migratorPassword, DatabaseName);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await ExecuteAsSuperuserAsync(
            $"create database {PristineTemplate} template {DatabaseName} owner duora_migrator",
            TestContext.Current.CancellationToken);
        await ExecuteAsSuperuserAsync(
            $"alter database {PristineTemplate} with is_template true allow_connections false; " +
            $"revoke all on database {PristineTemplate} from public",
            TestContext.Current.CancellationToken);
        await MigrateAsync(DatabaseName, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    internal DuoraDbContext CreateDbContext() => CreateDbContext(ConnectionString);

    internal static DuoraDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<DuoraDbContext>();
        options.UseDuoraNpgsql(connectionString);
        return new DuoraDbContext(options.Options);
    }

    /// <summary>
    /// Banco novo e migrado, só do teste que o pediu, e a conexão da aplicação nele. Para quem consome filas globais
    /// (outbox): no banco compartilhado, o consumidor pegaria itens gravados por testes que rodam em paralelo.
    /// </summary>
    public async Task<string> CreateMigratedDatabaseAsync(CancellationToken cancellationToken)
    {
        var database = await CreateEmptyDatabaseAsync(cancellationToken);
        await MigrateAsync(database.Name, cancellationToken);
        return database.AppConnectionString;
    }

    /// <summary>
    /// Cria um banco vazio, sem migrations, no mesmo servidor, com os papéis e default privileges do initdb.
    /// </summary>
    public async Task<ScratchDatabase> CreateEmptyDatabaseAsync(CancellationToken cancellationToken)
    {
        var name = $"empty_{Guid.NewGuid():N}";
        // Identificador gerado aqui (prefixo + hex), não vem de fora; CREATE DATABASE e GRANT não aceitam parâmetro.
        // O ACL do banco não vem do template; repete o do initdb, e
        // AppRolePrivilegesTests.ScratchDatabasesGetTheSameDatabaseAclAsTheMainOne pega a divergência.
        await ExecuteAsSuperuserAsync(
            $"create database {name} template {PristineTemplate} owner duora_migrator", cancellationToken);
        await ExecuteAsSuperuserAsync(
            $"revoke all on database {name} from public; grant connect on database {name} to duora_app",
            cancellationToken);

        return new ScratchDatabase(
            name,
            ConnectAs("duora_app", _appPassword, name),
            ConnectAs("duora_migrator", _migratorPassword, name));
    }

    private async Task MigrateAsync(string database, CancellationToken cancellationToken)
    {
        await using (var dbContext = CreateDbContext(ConnectAs("duora_migrator", _migratorPassword, database)))
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
        }

        var reconcile = await _container.ExecAsync(
            ["psql", "--no-psqlrc", "-v", "ON_ERROR_STOP=1", "--username", "postgres", "--dbname", database,
                "--file", ReconcileScriptTarget],
            cancellationToken);
        if (reconcile.ExitCode != 0)
        {
            throw new InvalidOperationException($"reconcile-privileges.sql falhou em {database}: {reconcile.Stderr}");
        }
    }

    private async Task ExecuteAsSuperuserAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string ConnectAs(string role, string password, string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = role,
            Password = password,
            Database = database,
            // Each scratch database has a different connection string. Migrator pools would otherwise retain
            // one idle backend per database until the process exits, starving unrelated HTTP tests. These
            // administrative connections are short-lived; application pools and the server budget stay intact.
            Pooling = role != "duora_migrator",
        }.ConnectionString;
}

/// <summary>Banco avulso de um teste: a aplicação conecta como <c>duora_app</c>; quem migra, como <c>duora_migrator</c>.</summary>
public sealed record ScratchDatabase(string Name, string AppConnectionString, string MigratorConnectionString)
{
    // As connection strings levam senha; ToString (mensagens de assert, logs) mostra só o nome.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Name = ").Append(Name);
        return true;
    }
}
