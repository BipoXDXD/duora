using Microsoft.Extensions.Logging;
using Npgsql;

namespace Duora.Infrastructure.Media;

/// <summary>Linhas apagadas por uma chamada, até um lote por tabela.</summary>
public sealed record PhotoDeletionRecordPurgeResult(int Receipts, int Tombstones)
{
    public int Total => Receipts + Tombstones;
}

/// <summary>
/// P3 da #173 (ADR-0035, decisões de 29/10/2026): recibos (<c>media.photo_deletion_commands</c>) e tombstones
/// (<c>media.photo_tombstones</c>) de exclusão de foto saem 1 ano depois de <c>created_at</c>/<c>deleted_at</c>.
/// A aplicação continua sem <c>DELETE</c> nessas tabelas: quem apaga é a função <c>SECURITY DEFINER</c>
/// <c>media.purge_photo_deletion_records</c>, que decide a elegibilidade e limita o instante pedido pelo relógio do
/// banco. Um lote por tabela, com <c>FOR UPDATE SKIP LOCKED</c>: instâncias concorrentes apagam linhas disjuntas.
/// </summary>
public sealed partial class PhotoDeletionRecordPurge
{
    /// <summary>Teto por tabela e por chamada; a função recusa mais de 1.000.</summary>
    internal const int BatchSize = 500;

    private const int CommandTimeoutSeconds = 3;

    private readonly NpgsqlDataSource _source;
    private readonly TimeProvider _time;
    private readonly PhotoDeletionRecordPurgeMetrics _metrics;
    private readonly ILogger _logger;

    internal PhotoDeletionRecordPurge(
        NpgsqlDataSource source,
        TimeProvider time,
        PhotoDeletionRecordPurgeMetrics metrics,
        ILogger logger)
    {
        _source = source;
        _time = time;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<PhotoDeletionRecordPurgeResult> PurgeOnceAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select receipts, tombstones from media.purge_photo_deletion_records($1, $2)", connection)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };
        command.Parameters.AddWithValue(_time.GetUtcNow());
        command.Parameters.AddWithValue(BatchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("media.purge_photo_deletion_records não devolveu a contagem.");
        }

        var result = new PhotoDeletionRecordPurgeResult(reader.GetInt32(0), reader.GetInt32(1));
        _metrics.RecordPurged(result);
        if (result.Total > 0)
        {
            LogPurged(_logger, result.Total, result.Receipts, result.Tombstones);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Expurgo de registros de exclusão de foto: {Total} linhas ({Receipts} recibos, {Tombstones} tombstones)")]
    private static partial void LogPurged(ILogger logger, int total, int receipts, int tombstones);
}
