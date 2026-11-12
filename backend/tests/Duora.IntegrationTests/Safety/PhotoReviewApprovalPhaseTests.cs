using System.Data.Common;
using System.Text.RegularExpressions;
using Duora.Core.Media;
using Duora.Infrastructure.Media;
using Duora.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Duora.IntegrationTests.Safety;

/// <summary>
/// Caracteriza as fases da decisão humana de aprovação: cada saída antecipada, a ordem dos locks e dos efeitos
/// e onde a transação é confirmada ou descartada. As sequências abaixo são as observadas no código de 11/11/2026.
/// </summary>
public sealed partial class PhotoReviewApprovalTests
{
    // Cada verificação de autoridade termina no passo "authorize"; antes dele o guard do ator é travado ("guard").
    private static readonly string[] Preauthorization = ["begin", "guard", "authorize", "replay"];
    private static readonly string[] GuardedStart = ["begin", "guard", "guard", "guard", "authorize", "replay"];
    private static readonly string[] Authorization = ["guard", "authorize"];
    private static readonly string[] SharedLocks =
        ["lock-upload-share", "lock-case-share", "lock-candidate-share", "lock-variants"];
    private static readonly string[] ExclusiveLocks =
        ["lock-upload-update", "lock-case-update", "lock-candidate-update", "lock-variants"];
    private static readonly string[] CaseLoad = ["load-case", "load-variants"];
    private static readonly string[] PromotionReads = ["lock-promotion", .. Authorization, "load-promotion", "load-promotion-variants", .. Authorization];
    private static readonly string[] ApprovalWrites =
    [
        "insert-asset-and-variants", "commit-promotion", "insert-decision", "insert-decision-variant",
        "insert-decision-variant", "insert-decision-variant", "approve-upload", "bump-case", "retain-candidate",
        "insert-receipt", "audit",
    ];

    /// <summary>Fase inicial até o último passo antes de confirmar: dois guards, locks compartilhados, leitura e prazo.</summary>
    private static string[] InitialChecks => [.. Preauthorization, "discover", .. GuardedStart, .. SharedLocks,
        .. Authorization, .. CaseLoad, .. Authorization];

    /// <summary>Fase final até a leitura da promoção: os mesmos passos, com locks exclusivos.</summary>
    private static string[] FinalChecks => [.. Preauthorization, "discover", .. GuardedStart, .. ExclusiveLocks,
        .. Authorization, .. CaseLoad, .. Authorization];

    [Fact]
    public async Task AnApprovalIssuesItsLocksAndEffectsInOneOrderAndCommitsTheGuardSetupBeforeTheDecision()
    {
        var (reviewer, item) = await PreparedAsync();
        var trace = new PhaseTrace();

        var result = await ApproveTracedAsync(reviewer, item, trace);

        Assert.IsType<PhotoReviewApprovalResult.Approved>(result);
        Assert.Equal(
            [.. InitialChecks, "commit", .. FinalChecks, .. PromotionReads, .. ApprovalWrites, .. Authorization, "commit"],
            trace.Steps());
    }

    [Fact]
    public async Task AnObservationAfterAFailedPreparationRechecksTheCaseButNeverCommits()
    {
        var (reviewer, item) = await PreparedAsync();
        await using (var db = Context())
        {
            var generation = await db.Database.SqlQuery<Guid>($"""
                select candidate_generation as "Value" from safety.photo_review_cases where id={item.CaseId}
                """).SingleAsync(Ct);
            await new FileSystemObjectStore(_root.FullName).DeleteAsync(ObjectKey.ForReviewCandidate(generation, PhotoVariant.Full), Ct);
        }

        var trace = new PhaseTrace();
        var result = await ApproveTracedAsync(reviewer, item, trace);

        Assert.IsType<PhotoReviewApprovalResult.Unavailable>(result);
        // A primeira fase confirma o guard; a observação depois da cópia falha, tem o snapshot e descarta a transação.
        Assert.Equal([.. InitialChecks, "commit", .. InitialChecks], trace.Steps());
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task ApprovalOfAnUnknownCaseIsNotFoundBeforeAnyStorageAccess()
    {
        var (reviewer, _) = await PreparedAsync();
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);
        var trace = new PhaseTrace();

        var result = await new PhotoReviewApprovals(Factory(trace.Interceptors), noStorage, _time)
            .ApproveAsync(reviewer, Guid.NewGuid(), 2, PhotoReviewApprovalReason.MeetsPolicy, Key(), Ct);

        Assert.IsType<PhotoReviewApprovalResult.NotFound>(result);
        Assert.Equal([.. Preauthorization, "discover"], trace.Steps());
        Assert.Empty(noStorage.Opened);
        Assert.Equal(0, noStorage.PutAttempts);
    }

    [Fact]
    public async Task ApprovalReplayIsAnsweredFromItsReceiptWithoutLocatingTheCase()
    {
        var (reviewer, item) = await PreparedAsync();
        var key = Key();
        var first = Assert.IsType<PhotoReviewApprovalResult.Approved>(await ApproveAsync(reviewer, item, key));
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);

        // RefuseCaseLookup falha se qualquer consulta tocar safety.photo_review_cases.
        var replay = await new PhotoReviewApprovals(Factory(new RefuseCaseLookup()), noStorage, _time)
            .ApproveAsync(reviewer, item.CaseId, 1, PhotoReviewApprovalReason.MeetsPolicy, key, Ct);

        Assert.Equal(first, Assert.IsType<PhotoReviewApprovalResult.Approved>(replay));
        Assert.Equal(0, noStorage.PutAttempts);
        Assert.Empty(noStorage.Opened);
    }

    [Fact]
    public async Task AReceiptCommittedWhileTheCaseIsBeingLocatedIsReplayedAfterTheGuards()
    {
        var (reviewer, item) = await PreparedAsync();
        var key = Key();
        PhotoReviewApprovalResult? competing = null;
        var trace = new PhaseTrace().On("discover", async () => competing = await ApproveAsync(reviewer, item, key));
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);

        var result = await new PhotoReviewApprovals(Factory(trace.Interceptors), noStorage, _time)
            .ApproveAsync(reviewer, item.CaseId, 2, PhotoReviewApprovalReason.MeetsPolicy, key, Ct);

        Assert.Equal(Assert.IsType<PhotoReviewApprovalResult.Approved>(competing), Assert.IsType<PhotoReviewApprovalResult.Approved>(result));
        Assert.Equal([.. Preauthorization, "discover", .. GuardedStart], trace.Steps());
        Assert.Empty(noStorage.Opened);
        Assert.Equal(0, noStorage.PutAttempts);
        Assert.Equal(1, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
    }

    [Fact]
    public async Task ApprovalOfACaseWhoseOwnerVanishedAfterItWasLocatedIsNotFound()
    {
        var (reviewer, item) = await PreparedAsync();
        var trace = new PhaseTrace().On("discover", async () =>
        {
            // Sem a linha de usuário e sem o guard já criado, o guard do titular não existe ao ser travado.
            await SuperuserAsync("delete from safety.user_guards where user_id=$1", bypassForeignKeys: true, item.OwnerId);
            await SuperuserAsync("delete from identity.users where id=$1", bypassForeignKeys: true, item.OwnerId);
        });
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);

        var result = await new PhotoReviewApprovals(Factory(trace.Interceptors), noStorage, _time)
            .ApproveAsync(reviewer, item.CaseId, 2, PhotoReviewApprovalReason.MeetsPolicy, Key(), Ct);

        Assert.IsType<PhotoReviewApprovalResult.NotFound>(result);
        Assert.Equal([.. Preauthorization, "discover", .. GuardedStart], trace.Steps());
        Assert.Equal(0, noStorage.PutAttempts);
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
    }

    [Fact]
    public async Task ApprovalWithAnIncompleteCandidateManifestIsUnavailableBeforeAnyCopy()
    {
        var (reviewer, item) = await PreparedAsync();
        await SuperuserAsync("""
            delete from media.review_candidate_variants
            where kind='card' and generation=(select candidate_generation from safety.photo_review_cases where id=$1)
            """, bypassForeignKeys: true, item.CaseId);
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);
        var trace = new PhaseTrace();

        var result = await ApproveTracedAsync(reviewer, item, trace, noStorage);

        Assert.IsType<PhotoReviewApprovalResult.Unavailable>(result);
        // Termina na leitura das variantes: o manifesto é recusado antes de reautorizar e de qualquer cópia.
        Assert.Equal([.. Preauthorization, "discover", .. GuardedStart, .. SharedLocks, .. Authorization, .. CaseLoad], trace.Steps());
        Assert.Empty(noStorage.Opened);
        Assert.Equal(0, noStorage.PutAttempts);
    }

    [Fact]
    public async Task ApprovalPastTheReviewDeadlineIsAStateConflictAfterTheAuthorityCheck()
    {
        var (reviewer, item) = await PreparedAsync();
        _time.Advance(ReviewWait + TimeSpan.FromHours(1));
        await RefreshSessionAsync(reviewer);
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);
        var trace = new PhaseTrace();

        var result = await ApproveTracedAsync(reviewer, item, trace, noStorage);

        Assert.IsType<PhotoReviewApprovalResult.StateConflict>(result);
        Assert.Equal(InitialChecks, trace.Steps());
        Assert.Equal(0, noStorage.PutAttempts);
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task ApprovalOfAPhotoWhoseVersionDriftedFromItsCandidateIsAStateConflict()
    {
        var (reviewer, item) = await PreparedAsync();
        await ExecuteAsync("update media.uploads set version=version+1 where id=$1", item.UploadId);
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);
        var trace = new PhaseTrace();

        var result = await ApproveTracedAsync(reviewer, item, trace, noStorage);

        Assert.IsType<PhotoReviewApprovalResult.StateConflict>(result);
        Assert.Equal(InitialChecks, trace.Steps());
        Assert.Equal(0, noStorage.PutAttempts);
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    [Fact]
    public async Task ApprovalWhoseEvidenceChangesBetweenItsPhasesIsAStateConflict()
    {
        var (reviewer, item) = await PreparedAsync();
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName));
        // O segundo "discover" é o da fase final, já com a cópia preparada e nenhum lock SQL retido.
        var trace = new PhaseTrace().On("discover", () => SuperuserAsync("""
            update media.review_candidate_variants set sha256=decode(repeat('ab',32),'hex')
            where kind='full' and generation=(select candidate_generation from safety.photo_review_cases where id=$1)
            """, bypassForeignKeys: true, item.CaseId), occurrence: 2);

        var result = await ApproveTracedAsync(reviewer, item, trace, store);

        Assert.IsType<PhotoReviewApprovalResult.StateConflict>(result);
        Assert.Equal([.. InitialChecks, "commit", .. Preauthorization, "discover", .. GuardedStart, .. ExclusiveLocks,
            .. Authorization, .. CaseLoad], trace.Steps());
        Assert.Equal(3, store.PutAttempts);
        Assert.Equal(0, await ScalarAsync("select count(*) from safety.photo_review_decisions where case_id=$1", item.CaseId));
        Assert.Equal(0, await ScalarAsync("select count(*) from media.media_assets where upload_id=$1", item.UploadId));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    public async Task ApprovalWhosePromotedManifestDiffersFromTheCandidateIsUnavailable(string tamper)
    {
        var (reviewer, item) = await PreparedAsync();
        var puts = 0;
        var store = new ApprovalStore(new FileSystemObjectStore(_root.FullName), afterPut: async _ =>
        {
            if (++puts == 3)
            {
                await SuperuserAsync(tamper == "changed"
                    ? """
                        update media.review_promotion_variants set sha256=decode(repeat('ab',32),'hex')
                        where kind='full' and generation=(select generation from media.review_promotions where case_id=$1)
                        """
                    : """
                        delete from media.review_promotion_variants
                        where kind='full' and generation=(select generation from media.review_promotions where case_id=$1)
                        """, bypassForeignKeys: true, item.CaseId);
            }
        });
        var trace = new PhaseTrace();

        var result = await ApproveTracedAsync(reviewer, item, trace, store);

        Assert.IsType<PhotoReviewApprovalResult.Unavailable>(result);
        // Termina na leitura do manifesto promovido: nada é reautorizado nem escrito depois dele.
        Assert.Equal([.. InitialChecks, "commit", .. FinalChecks, "lock-promotion", .. Authorization, "load-promotion",
            "load-promotion-variants"], trace.Steps());
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task ApprovalForbiddenAfterTheBusinessRowLocksStopsBeforeLoadingTheCase()
    {
        var (reviewer, item) = await PreparedAsync();
        var trace = new PhaseTrace().On("lock-variants", () => RevokeDecideAsync(reviewer));
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);

        var result = await ApproveTracedAsync(reviewer, item, trace, noStorage);

        Assert.IsType<PhotoReviewApprovalResult.Forbidden>(result);
        Assert.Equal([.. Preauthorization, "discover", .. GuardedStart, .. SharedLocks, .. Authorization], trace.Steps());
        Assert.Equal(0, noStorage.PutAttempts);
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task ApprovalForbiddenAfterTheCaseIsLoadedStopsBeforeCheckingTheDeadlines()
    {
        var (reviewer, item) = await PreparedAsync();
        var trace = new PhaseTrace().On("load-variants", () => RevokeDecideAsync(reviewer));
        var noStorage = new ApprovalStore(new FileSystemObjectStore(_root.FullName), refuseAll: true);

        var result = await ApproveTracedAsync(reviewer, item, trace, noStorage);

        Assert.IsType<PhotoReviewApprovalResult.Forbidden>(result);
        Assert.Equal(InitialChecks, trace.Steps());
        Assert.Equal(0, noStorage.PutAttempts);
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task ApprovalForbiddenAfterThePromotionLockStopsBeforeReadingThePreparedPromotion()
    {
        var (reviewer, item) = await PreparedAsync();
        var trace = new PhaseTrace().On("lock-promotion", () => RevokeDecideAsync(reviewer));

        var result = await ApproveTracedAsync(reviewer, item, trace);

        Assert.IsType<PhotoReviewApprovalResult.Forbidden>(result);
        Assert.Equal([.. InitialChecks, "commit", .. FinalChecks, "lock-promotion", .. Authorization], trace.Steps());
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task ApprovalForbiddenAfterThePromotedManifestStopsBeforeAnyWrite()
    {
        var (reviewer, item) = await PreparedAsync();
        var trace = new PhaseTrace().On("load-promotion-variants", () => RevokeDecideAsync(reviewer));

        var result = await ApproveTracedAsync(reviewer, item, trace);

        Assert.IsType<PhotoReviewApprovalResult.Forbidden>(result);
        Assert.Equal([.. InitialChecks, "commit", .. FinalChecks, .. PromotionReads], trace.Steps());
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task ApprovalForbiddenAfterTheAuditRollsBackEveryWrite()
    {
        var (reviewer, item) = await PreparedAsync();
        var trace = new PhaseTrace().On("audit", () => RevokeDecideAsync(reviewer));

        var result = await ApproveTracedAsync(reviewer, item, trace);

        Assert.IsType<PhotoReviewApprovalResult.Forbidden>(result);
        Assert.Equal([.. InitialChecks, "commit", .. FinalChecks, .. PromotionReads, .. ApprovalWrites, .. Authorization],
            trace.Steps());
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task ApprovalWhosePrepareDeadlinePassesBeforeItsPromotionIsReadIsUnavailable()
    {
        var (reviewer, item) = await PreparedAsync();
        var trace = new PhaseTrace().On("lock-promotion", () => AdvanceAsync(TimeSpan.FromMinutes(5)));

        var result = await ApproveTracedAsync(reviewer, item, trace);

        Assert.IsType<PhotoReviewApprovalResult.Unavailable>(result);
        Assert.Equal([.. InitialChecks, "commit", .. FinalChecks, "lock-promotion", .. Authorization, "load-promotion"],
            trace.Steps());
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task ApprovalWhosePrepareDeadlinePassesAfterTheManifestIsReadIsUnavailableBeforeAnyWrite()
    {
        var (reviewer, item) = await PreparedAsync();
        var trace = new PhaseTrace().On("load-promotion-variants", () => AdvanceAsync(TimeSpan.FromMinutes(5)));

        var result = await ApproveTracedAsync(reviewer, item, trace);

        Assert.IsType<PhotoReviewApprovalResult.Unavailable>(result);
        Assert.Equal([.. InitialChecks, "commit", .. FinalChecks, .. PromotionReads], trace.Steps());
        await AssertNoApprovalAsync(item);
    }

    [Fact]
    public async Task ApprovalWhosePrepareDeadlinePassesDuringItsWritesRollsBackEveryWrite()
    {
        var (reviewer, item) = await PreparedAsync();
        var trace = new PhaseTrace().On("audit", () => AdvanceAsync(TimeSpan.FromMinutes(5)));

        var result = await ApproveTracedAsync(reviewer, item, trace);

        Assert.IsType<PhotoReviewApprovalResult.Unavailable>(result);
        Assert.Equal([.. InitialChecks, "commit", .. FinalChecks, .. PromotionReads, .. ApprovalWrites, .. Authorization],
            trace.Steps());
        await AssertNoApprovalAsync(item);
    }

    [Theory]
    [InlineData("media.uploads", "new.state = 'Approved'")]
    [InlineData("safety.photo_review_cases", "new.version > old.version")]
    [InlineData("media.review_candidates", "new.cleanup_requested_at is not null and old.cleanup_requested_at is null")]
    [InlineData("media.review_promotions", "new.committed_at is not null")]
    public async Task AProjectionThatChangesNoRowAbortsTheWholeDecision(string table, string condition)
    {
        var (reviewer, item) = await PreparedAsync();
        // O gatilho BEFORE UPDATE descarta só a linha da projeção, e o UPDATE passa a afetar zero linhas.
        await SuperuserAsync($"""
            create function public.skip_projection_row() returns trigger language plpgsql as $$ begin return null; end $$;
            create trigger aaa_skip_projection before update on {table} for each row when ({condition})
              execute function public.skip_projection_row();
            """, bypassForeignKeys: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApproveTracedAsync(reviewer, item, new PhaseTrace()));

        Assert.Equal("All guarded human decision projections must change exactly once.", error.Message);
        await AssertNoApprovalAsync(item);
    }

    private async Task<PhotoReviewApprovalResult> ApproveTracedAsync(PhotoReviewActor reviewer, CaseData item,
        PhaseTrace trace, IObjectStore? store = null) =>
        await new PhotoReviewApprovals(Factory(trace.Interceptors), store ?? new FileSystemObjectStore(_root.FullName), _time)
            .ApproveAsync(reviewer, item.CaseId, 2, PhotoReviewApprovalReason.MeetsPolicy, Key(), Ct);

    /// <summary>Revoga Decide sem travar o guard do ator, que a decisão em andamento já segura.</summary>
    private Task RevokeDecideAsync(PhotoReviewActor reviewer) => ExecuteAsync("""
        insert into safety.photo_review_grants(id,user_id,capability,version,enabled,occurred_at)
        select $1,user_id,capability,version+1,false,$3 from safety.photo_review_grants
        where user_id=$2 and capability='decide' order by version desc limit 1
        """, Guid.NewGuid(), reviewer.UserId, _time.GetUtcNow());

    private Task AdvanceAsync(TimeSpan span)
    {
        _time.Advance(span);
        return Task.CompletedTask;
    }

    /// <summary>Renova a sessão sintética depois de um salto de relógio longo, sem tocar em nada da decisão.</summary>
    private Task RefreshSessionAsync(PhotoReviewActor reviewer)
    {
        var now = _time.GetUtcNow();
        return ExecuteAsync("""
            update identity.login_sessions set last_seen_at=$2,idle_expires_at=$3,absolute_expires_at=$4,
              mfa_verified_at=$2,totp_verified_at=$2 where id=$1
            """, reviewer.SessionId, now, now.AddMinutes(10), now.AddHours(1));
    }

    private async Task SuperuserAsync(string sql, bool bypassForeignKeys, params object[] values)
    {
        await using var connection = new NpgsqlConnection(postgres.ScratchSuperuserConnectionString(_database!));
        await connection.OpenAsync(Ct);
        if (bypassForeignKeys)
        {
            await using var role = new NpgsqlCommand("set session_replication_role=replica", connection);
            await role.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>
    /// Registra, na ordem, as transações das fases da decisão (as que passam por "authorize") e os comandos que
    /// importam em cada uma. Uma transação sem "commit" foi descartada. Gatilhos disparam uma vez, quando o comando
    /// com o rótulo é emitido, para mudar o mundo exatamente naquele ponto.
    /// </summary>
    private sealed partial class PhaseTrace
    {
        private static readonly (string Fragment, string Label)[] Labels =
        [
            ("as \"totpverifiedat\"", "authorize"),
            ("insert into safety.user_guards", "guard"),
            ("from safety.photo_review_decision_commands where actor_id", "replay"),
            ("select u.owner_id as \"ownerid\",u.id as \"uploadid\",r.candidate_generation", "discover"),
            ("select 1 from media.uploads where id=@p0 for share", "lock-upload-share"),
            ("select 1 from safety.photo_review_cases where id=@p0 for share", "lock-case-share"),
            ("select 1 from media.review_candidates where generation=@p0 for share", "lock-candidate-share"),
            ("select 1 from media.uploads where id=@p0 for update", "lock-upload-update"),
            ("select 1 from safety.photo_review_cases where id=@p0 for update", "lock-case-update"),
            ("select 1 from media.review_candidates where generation=@p0 for update", "lock-candidate-update"),
            ("from media.review_candidate_variants where generation=@p0 order by kind for share", "lock-variants"),
            ("join identity.users owner on owner.id=u.owner_id", "load-case"),
            ("from media.review_candidate_variants where generation=@p0 and object_key=", "load-variants"),
            ("select 1 from media.review_promotions where generation=@p0 for update", "lock-promotion"),
            ("select prepare_deadline as \"deadline\"", "load-promotion"),
            ("from media.review_promotion_variants where generation=@p0 and object_key=", "load-promotion-variants"),
            ("insert into media.media_assets", "insert-asset-and-variants"),
            ("update media.review_promotions set committed_at=", "commit-promotion"),
            ("insert into safety.photo_review_decisions(", "insert-decision"),
            ("insert into safety.photo_review_decision_variants", "insert-decision-variant"),
            ("update media.uploads set state='approved'", "approve-upload"),
            ("update safety.photo_review_cases set version=", "bump-case"),
            ("update media.review_candidates set retention_until=", "retain-candidate"),
            ("insert into safety.photo_review_decision_commands", "insert-receipt"),
            ("insert into ops.audit_events", "audit"),
        ];

        private readonly Lock _gate = new();
        private readonly List<Segment> _segments = [];
        private readonly Dictionary<string, (Func<Task> Action, int Remaining)> _triggers = new(StringComparer.Ordinal);

        internal IInterceptor[] Interceptors => [new Commands(this), new Transactions(this)];

        /// <summary>Dispara <paramref name="trigger"/> na <paramref name="occurrence"/>-ésima emissão do rótulo numa fase da decisão.</summary>
        internal PhaseTrace On(string label, Func<Task> trigger, int occurrence = 1)
        {
            _triggers.Add(label, (trigger, occurrence));
            return this;
        }

        internal string[] Steps()
        {
            lock (_gate)
            {
                return [.. _segments.Where(segment => segment.Authorized).SelectMany(segment => segment.Steps())];
            }
        }

        private async Task RecordAsync(DbCommand command)
        {
            var label = LabelOf(command.CommandText);
            Func<Task>? trigger = null;
            lock (_gate)
            {
                var inPhase = command.Transaction is null;
                if (command.Transaction is null)
                {
                    if (label is not null)
                    {
                        _segments.Add(new Segment(null) { Authorized = true, Labels = { label } });
                    }
                }
                else if (label is not null)
                {
                    var segment = OpenSegment(command.Transaction);
                    segment.Labels.Add(label);
                    segment.Authorized |= label == "authorize";
                    inPhase = segment.Authorized;
                }

                // O preparo da cópia reusa alguns comandos; só os de uma fase da decisão disparam.
                if (inPhase && label is not null && _triggers.TryGetValue(label, out var armed))
                {
                    if (armed.Remaining > 1)
                    {
                        _triggers[label] = (armed.Action, armed.Remaining - 1);
                    }
                    else
                    {
                        _triggers.Remove(label);
                        trigger = armed.Action;
                    }
                }
            }

            if (trigger is not null)
            {
                await trigger();
            }
        }

        private static string? LabelOf(string commandText)
        {
            var sql = Whitespace().Replace(commandText, " ").Trim().ToLowerInvariant();
            foreach (var (fragment, label) in Labels)
            {
                if (sql.Contains(fragment, StringComparison.Ordinal))
                {
                    return label;
                }
            }

            return null;
        }

        private Segment OpenSegment(DbTransaction transaction)
        {
            var open = _segments.LastOrDefault(segment => ReferenceEquals(segment.Transaction, transaction));
            if (open is null)
            {
                open = new Segment(transaction);
                _segments.Add(open);
            }

            return open;
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();

        private sealed class Segment(DbTransaction? transaction)
        {
            internal DbTransaction? Transaction { get; } = transaction;
            internal List<string> Labels { get; } = [];
            internal bool Authorized { get; set; }
            internal bool Committed { get; set; }

            internal List<string> Steps()
            {
                if (Transaction is null)
                {
                    return [.. Labels];
                }

                var steps = new List<string> { "begin" };
                steps.AddRange(Labels);
                if (Committed)
                {
                    steps.Add("commit");
                }

                return steps;
            }
        }

        private sealed class Commands(PhaseTrace trace) : DbCommandInterceptor
        {
            public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
                CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            {
                await trace.RecordAsync(command);
                return result;
            }

            public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
                CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                await trace.RecordAsync(command);
                return result;
            }
        }

        private sealed class Transactions(PhaseTrace trace) : DbTransactionInterceptor
        {
            public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection,
                TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
            {
                lock (trace._gate)
                {
                    trace._segments.Add(new Segment(result));
                }

                return ValueTask.FromResult(result);
            }

            public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
                CancellationToken cancellationToken = default)
            {
                lock (trace._gate)
                {
                    trace.OpenSegment(transaction).Committed = true;
                }

                return Task.CompletedTask;
            }
        }
    }
}
