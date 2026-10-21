using System.Text.Json;
using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Participation;

public sealed partial class ParticipationService
{
    public async Task<Guid?> PublishRoutesCatalogAsync(Guid administrator, UnlikelyRoutesContent enUS, UnlikelyRoutesContent ptBR,
        IReadOnlyList<CatalogAudioUpload> audio, bool editorialApproved, CancellationToken ct)
    {
        // Only public introductory narration may be shared. Private clues are never audio cues.
        // Editorial approval also attests that the recorded bytes match these public transcripts.
        if (!editorialApproved || audio.Count != 1 || audio[0] is not { CueId: "intro", EnUS: not null, PtBR: not null } cue
            || cue.EnUS.Transcript != enUS.Objective || cue.PtBR.Transcript != ptBR.Objective
            || cue.EnUS.AccessibleDescription != enUS.Title || cue.PtBR.AccessibleDescription != ptBR.Title)
        {
            return null;
        }
        var localized = new LocalizedUnlikelyRoutesContent(enUS, ptBR, audio.Select(ToAudio));
        return await PersistCatalogAsync(administrator, localized.ContentVersion, "unlikely-routes", enUS, ptBR, audio, ct);
    }

    internal static async Task<string?> ContentKindAsync(NpgsqlConnection connection, Guid id, CancellationToken ct)
    {
        await using var query = Command(connection,
            "select experience_kind from experiences.experience_versions where id=$1 and editorial_state in ('Published','Retired')", id);
        return await query.ExecuteScalarAsync(ct) as string;
    }

    internal static async Task<LocalizedUnlikelyRoutesContent?> RoutesContentAsync(NpgsqlConnection connection, Guid id, CancellationToken ct)
    {
        UnlikelyRoutesContent en;
        UnlikelyRoutesContent pt;
        CatalogAudioManifest[] manifest;
        await using (var query = Command(connection, "select content_en::text,content_pt::text,audio_manifest::text from experiences.experience_versions where id=$1 and experience_kind='unlikely-routes' and editorial_state in ('Published','Retired')", id))
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            en = JsonSerializer.Deserialize<UnlikelyRoutesContent>(reader.GetString(0), Json)!;
            pt = JsonSerializer.Deserialize<UnlikelyRoutesContent>(reader.GetString(1), Json)!;
            manifest = JsonSerializer.Deserialize<CatalogAudioManifest[]>(reader.GetString(2), Json)!;
        }
        List<LocalizedAudio> audio = [];
        foreach (var cue in manifest)
        {
            audio.Add(new(cue.CueId, await ReadVariant(cue.EnUS, "en-US"), await ReadVariant(cue.PtBR, "pt-BR")));
        }
        return new(en, pt, audio);

        async Task<LocalizedAudioVariant> ReadVariant(CatalogAudioManifestVariant variant, string locale)
        {
            await using var query = Command(connection, "select bytes,sha256 from experiences.audio_assets where experience_version_id=$1 and asset_id=$2 and display_locale=$3", id, variant.AssetId, locale);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.GetString(1) != variant.Sha256)
            {
                throw new InvalidOperationException("Pinned audio manifest is incomplete.");
            }

            return ToVariant(new(variant.AssetId, variant.Transcript, variant.AccessibleDescription, variant.Provenance, variant.Sha256, reader.GetFieldValue<byte[]>(0)));
        }
    }

}
