using System.Buffers.Binary;
using System.Text.Json;
using Duora.Core.Experiences;
using Duora.Core.Experiences.Constellation;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Participation;

public sealed record CatalogAudioUpload(string CueId, CatalogAudioUploadVariant EnUS, CatalogAudioUploadVariant PtBR);
public sealed record CatalogAudioUploadVariant(string AssetId, string Transcript, string AccessibleDescription,
    string Provenance, string Sha256, byte[] Bytes);
public sealed record RoomAudioAsset(byte[] Bytes, string ContentType, string Sha256);
internal sealed record CatalogAudioManifest(string CueId, CatalogAudioManifestVariant EnUS, CatalogAudioManifestVariant PtBR);
internal sealed record CatalogAudioManifestVariant(string AssetId, string Transcript, string AccessibleDescription,
    string Provenance, string Sha256);

public sealed partial class ParticipationService
{
    public const int MaxCatalogBodyBytes = 24 * 1024 * 1024;
    private const int MaxAudioBytes = 6 * 1024 * 1024;

    public async Task<bool> IsProductAdministratorAsync(Guid user, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var query = Command(connection, """
            select exists(select 1 from identity.users u join identity.user_roles ur on ur.user_id=u.id
              join identity.roles r on r.id=ur.role_id where u.id=$1 and u.status='Active' and r.name='product_admin')
            """, user);
        return (bool)(await query.ExecuteScalarAsync(ct))!;
    }

    public async Task<Guid?> PublishCatalogAsync(Guid administrator, ConstellationContent enUS, ConstellationContent ptBR,
        IReadOnlyList<CatalogAudioUpload> audio, bool editorialApproved, CancellationToken ct)
    {
        if (!editorialApproved || audio.Count is 0 or > 8 || audio.Any(cue => cue is null || cue.EnUS is null || cue.PtBR is null)
            || audio.SelectMany(cue => new[] { cue.EnUS, cue.PtBR }).Sum(variant => (long)(variant.Bytes?.Length ?? 0)) > 16 * 1024 * 1024)
        {
            return null;
        }
        var localized = new LocalizedConstellationContent(enUS, ptBR, audio.Select(ToAudio));
        var manifest = audio.Select(cue => new CatalogAudioManifest(cue.CueId, Manifest(cue.EnUS), Manifest(cue.PtBR))).ToArray();
        if (audio.SelectMany(cue => new[] { cue.EnUS.AssetId, cue.PtBR.AssetId }).Distinct(StringComparer.Ordinal).Count() != audio.Count * 2)
        {
            return null;
        }
        var manifestJson = JsonSerializer.Serialize(manifest, Json);
        var enJson = JsonSerializer.Serialize(enUS, Json);
        var ptJson = JsonSerializer.Serialize(ptBR, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(enJson) > 32768 || System.Text.Encoding.UTF8.GetByteCount(ptJson) > 32768
            || System.Text.Encoding.UTF8.GetByteCount(manifestJson) > 30000)
        {
            return null;
        }
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await Guard(connection, [administrator], ct);
        await using var authorized = Command(connection, """
            select exists(select 1 from identity.users u join identity.user_roles ur on ur.user_id=u.id
              join identity.roles r on r.id=ur.role_id where u.id=$1 and u.status='Active' and r.name='product_admin')
            """, administrator);
        if (!(bool)(await authorized.ExecuteScalarAsync(ct))!)
        {
            return null;
        }
        // The immutable catalog UUID is the persistence identity; the numeric content version is scoped to it.
        var id = Guid.CreateVersion7();
        await Execute(connection, """
            insert into experiences.experience_versions(id,content_version,rules_version,schema_version,editorial_state,content_en,content_pt,audio_manifest,approved_by)
            values($1,$2,1,1,'Draft',$3::jsonb,$4::jsonb,$5::jsonb,$6)
            """, ct, id, localized.ContentVersion, enJson, ptJson, manifestJson, administrator);
        foreach (var cue in audio)
        {
            foreach (var (locale, variant) in new[] { ("en-US", cue.EnUS), ("pt-BR", cue.PtBR) })
            {
                await Execute(connection, """
                    insert into experiences.audio_assets(experience_version_id,asset_id,display_locale,sha256,content_type,bytes)
                    values($1,$2,$3,$4,'audio/wav',$5)
                    """, ct, id, variant.AssetId, locale, variant.Sha256, variant.Bytes);
            }
        }
        await Execute(connection, "update experiences.experience_versions set editorial_state='Published',published_at=$2 where id=$1", ct, id, time.GetUtcNow());
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<RoomAudioAsset?> AudioAsync(Guid room, Guid user, string assetId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var row = await LoadRoomAsync(connection, room, user, true, ct);
        if (row is null || row.State is not ("Active" or "Completed"))
        {
            return null;
        }
        var content = await ContentAsync(connection, row.ContentId, ct);
        if (content is null || !content.ProjectAudio(row.Locale).Any(item => item.AssetId == assetId))
        {
            return null;
        }
        await using var query = Command(connection, "select bytes,content_type,sha256 from experiences.audio_assets where experience_version_id=$1 and asset_id=$2 and display_locale=$3", row.ContentId, assetId, row.Locale);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var result = new RoomAudioAsset(reader.GetFieldValue<byte[]>(0), reader.GetString(1), reader.GetString(2));
        await reader.CloseAsync();
        await tx.CommitAsync(ct);
        return result;
    }

    private static LocalizedAudio ToAudio(CatalogAudioUpload cue) => new(cue.CueId, ToVariant(cue.EnUS), ToVariant(cue.PtBR));
    private static LocalizedAudioVariant ToVariant(CatalogAudioUploadVariant variant)
    {
        RequirePcmWave(variant.Bytes);
        return new(variant.AssetId, variant.Transcript, variant.AccessibleDescription, variant.Provenance, variant.Sha256, variant.Bytes);
    }
    private static CatalogAudioManifestVariant Manifest(CatalogAudioUploadVariant variant) =>
        new(variant.AssetId, variant.Transcript, variant.AccessibleDescription, variant.Provenance, variant.Sha256);

    private static void RequirePcmWave(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        // Canonical PCM WAV only: no embedded tags, compressed codecs, scripts or runtime decoding dependencies.
        if (bytes.Length is < 46 or > MaxAudioBytes || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !bytes.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8) || !bytes.AsSpan(36, 4).SequenceEqual("data"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != bytes.Length - 8
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) != 16
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)) != 16
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)) != bytes.Length - 44)
        {
            throw new ArgumentException("Expected canonical 16-bit PCM WAV audio.", nameof(bytes));
        }
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22));
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24));
        var align = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32));
        if (channels is < 1 or > 2 || rate is < 8000 or > 48000 || align != channels * 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)) != rate * align
            || (bytes.Length - 44) % align != 0 || bytes.Length - 44 > rate * align * 60)
        {
            throw new ArgumentException("Audio exceeds the supported PCM bounds.", nameof(bytes));
        }
    }
}
