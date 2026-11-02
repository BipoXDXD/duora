using Duora.Core.Media;
using Npgsql;

namespace Duora.Infrastructure.Media;

internal sealed record PrivateGenerationReferenceState(string State, long Revision);

internal sealed class PrivateGenerationReference(
    NpgsqlDataSource source, PrivateGenerationDomain domain, Guid incarnation, Guid session)
{
    internal async Task AttachAsync(CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("select storage_control.attach($1,$2,$3,$4,$5)");
        command.Parameters.AddWithValue(domain.Id);
        command.Parameters.AddWithValue(domain.Epoch);
        command.Parameters.AddWithValue(domain.Layout);
        command.Parameters.AddWithValue(incarnation);
        command.Parameters.AddWithValue(session);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidDataException("Storage epoch requires custodian readmission after proven withdrawal.");
        }
    }

    internal async Task<PrivateGenerationReferenceState> TransitionAsync(PrivateGenerationIdentity identity,
        string target, CancellationToken cancellationToken)
    {
        if (identity.Domain.Id != domain.Id || identity.Domain.Layout != domain.Layout
            || identity.Domain.Epoch < 1 || identity.Domain.Epoch > domain.Epoch)
        {
            throw new InvalidDataException("Storage epoch mismatch.");
        }
        await using var command = source.CreateCommand(
            "select state,revision from storage_control.transition($1,$2,$3,$4,$5,$6,$7,$8,$9)");
        command.Parameters.AddWithValue(identity.Domain.Id);
        command.Parameters.AddWithValue(identity.Domain.Epoch);
        command.Parameters.AddWithValue(identity.Domain.Layout);
        command.Parameters.AddWithValue(incarnation);
        command.Parameters.AddWithValue(session);
        command.Parameters.AddWithValue((int)identity.Area);
        command.Parameters.AddWithValue(identity.Generation);
        command.Parameters.AddWithValue(identity.ManifestHash);
        command.Parameters.AddWithValue(target);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("Storage reference unavailable.");
        }
        return new(reader.GetString(0), reader.GetInt64(1));
    }
}
