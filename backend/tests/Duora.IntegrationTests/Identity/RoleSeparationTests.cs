using Duora.Core.Identity;
using Npgsql;

namespace Duora.IntegrationTests.Identity;

/// <summary>
/// D13: conta privilegiada é separada da de participante. A regra mora no banco (trigger em
/// <c>identity.user_roles</c>), para nenhum caminho de concessão de papel, inclusive o comando operacional do
/// bootstrap, conseguir juntar os dois tipos.
/// </summary>
public sealed class RoleSeparationTests(PostgresFixture postgres)
{
    private const string SeparationConstraint = "ck_user_roles_privileged_is_not_participant";

    [Fact]
    public async Task TheFixedRoleCatalogIsSeeded()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select name, normalized_name from identity.roles", connection);
        var roles = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                Assert.Equal(reader.GetString(0).ToUpperInvariant(), reader.GetString(1));
                roles.Add(reader.GetString(0));
            }
        }

        Assert.Equal(AccountRoles.All.Order(StringComparer.Ordinal), roles.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(AccountRoles.Participant, AccountRoles.Moderator)]
    [InlineData(AccountRoles.Moderator, AccountRoles.Participant)]
    [InlineData(AccountRoles.Participant, AccountRoles.ProductAdmin)]
    [InlineData(AccountRoles.Operator, AccountRoles.Participant)]
    public async Task AnAccountCannotBeParticipantAndPrivileged(string first, string second)
    {
        await using var connection = await OpenAsync();
        var userId = await InsertUserAsync(connection);
        await GrantAsync(connection, userId, first);

        var error = await Assert.ThrowsAsync<PostgresException>(() => GrantAsync(connection, userId, second));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(SeparationConstraint, error.ConstraintName);
        Assert.Equal([first], await RolesOfAsync(connection, userId));
    }

    [Fact]
    public async Task APrivilegedAccountCanHoldSeveralPrivilegedRoles()
    {
        await using var connection = await OpenAsync();
        var userId = await InsertUserAsync(connection);

        await GrantAsync(connection, userId, AccountRoles.Moderator);
        await GrantAsync(connection, userId, AccountRoles.Operator);

        Assert.Equal([AccountRoles.Moderator, AccountRoles.Operator], await RolesOfAsync(connection, userId));
    }

    [Fact]
    public async Task MovingAGrantToAnotherRoleIsCheckedToo()
    {
        await using var connection = await OpenAsync();
        var otherId = await InsertUserAsync(connection);
        await GrantAsync(connection, otherId, AccountRoles.Moderator);
        await GrantAsync(connection, otherId, AccountRoles.Operator);

        await using var update = new NpgsqlCommand(
            """
            update identity.user_roles set role_id = (select id from identity.roles where name = $2)
            where user_id = $1 and role_id = (select id from identity.roles where name = $3)
            """,
            connection);
        update.Parameters.AddWithValue(otherId);
        update.Parameters.AddWithValue(AccountRoles.Participant);
        update.Parameters.AddWithValue(AccountRoles.Operator);
        var error = await Assert.ThrowsAsync<PostgresException>(
            () => update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SeparationConstraint, error.ConstraintName);
    }

    [Fact]
    public async Task ConcurrentGrantsOfBothKindsLeaveOnlyOneKind()
    {
        // Sem serializar por conta, as duas transações veriam a conta sem papel e gravariam (write skew).
        await using var setup = await OpenAsync();
        var userId = await InsertUserAsync(setup);
        await using var first = await OpenAsync();
        await using var second = await OpenAsync();
        await using var firstTransaction = await first.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var secondTransaction = await second.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await GrantAsync(first, userId, AccountRoles.Participant, firstTransaction);

        var blocked = GrantAsync(second, userId, AccountRoles.Moderator, secondTransaction);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        Assert.False(blocked.IsCompleted, "A segunda concessão deveria esperar a primeira transação.");
        await firstTransaction.CommitAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<PostgresException>(() => blocked);

        Assert.Equal(SeparationConstraint, error.ConstraintName);
        Assert.Equal([AccountRoles.Participant], await RolesOfAsync(setup, userId));
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    internal static async Task<Guid> InsertUserAsync(NpgsqlConnection connection)
    {
        var id = Guid.CreateVersion7();
        var email = $"papel-{id:N}@example.com";
        await using var command = new NpgsqlCommand(
            """
            insert into identity.users
                (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, security_stamp,
                 concurrency_stamp, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count,
                 status, created_at)
            values ($1, $2, upper($2), $2, upper($2), true, 'stamp', 'concurrency', false, false, true, 0,
                    'Active', now())
            """,
            connection);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(email);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    internal static async Task GrantAsync(
        NpgsqlConnection connection,
        Guid userId,
        string role,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(
            """
            insert into identity.user_roles (user_id, role_id)
            select $1, id from identity.roles where name = $2
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(role);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<List<string>> RolesOfAsync(NpgsqlConnection connection, Guid userId)
    {
        await using var command = new NpgsqlCommand(
            """
            select r.name from identity.user_roles ur join identity.roles r on r.id = ur.role_id
            where ur.user_id = $1 order by r.name
            """,
            connection);
        command.Parameters.AddWithValue(userId);
        var roles = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            roles.Add(reader.GetString(0));
        }

        return roles;
    }
}
