using System.Diagnostics.Metrics;

namespace Duora.Infrastructure.Media;

/// <summary>
/// Total de linhas apagadas pelo expurgo, pelo <see cref="IMeterFactory"/> do host. O rótulo é só o nome fixo da tabela,
/// nunca id de foto ou de conta.
/// </summary>
internal sealed class PhotoDeletionRecordPurgeMetrics
{
    public const string MeterName = "Duora.Media";
    public const string PurgedInstrument = "duora.media.deletion_records.purged";

    private readonly Counter<long> _purged;

    public PhotoDeletionRecordPurgeMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _purged = meter.CreateCounter<long>(PurgedInstrument,
            description: "Tombstones e recibos de exclusão de foto apagados pela retenção de 1 ano.");
    }

    public void RecordPurged(PhotoDeletionRecordPurgeResult result)
    {
        if (result.Receipts > 0)
        {
            _purged.Add(result.Receipts, new KeyValuePair<string, object?>("table", "photo_deletion_commands"));
        }

        if (result.Tombstones > 0)
        {
            _purged.Add(result.Tombstones, new KeyValuePair<string, object?>("table", "photo_tombstones"));
        }
    }
}
