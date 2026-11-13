using Duora.Core.Experiences;
using Duora.Core.Experiences.Constellation;
using Microsoft.Extensions.Logging;
using Npgsql;
using static Duora.Infrastructure.Participation.ParticipationSql;

namespace Duora.Infrastructure.Participation;

public sealed record ParticipationJob(Guid Id, string Kind, Guid AggregateId, DateTimeOffset DueAt, long Token);

public sealed partial class ParticipationService
{
    private static readonly TimeSpan JobTimeLimit = TimeSpan.FromSeconds(3);

    private static Task<int> ScheduleAsync(NpgsqlConnection connection, string kind, Guid aggregate, DateTimeOffset due, CancellationToken ct) =>
        Execute(connection, "insert into ops.jobs(id,kind,aggregate_id,due_at,available_at) values($1,$2,$3,$4,$4) on conflict do nothing", ct,
            Guid.CreateVersion7(), kind, aggregate, due);

    public async Task<ParticipationJob?> ClaimJobAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        ParticipationJob job;
        string status;
        await using (var command = Command(connection, """
            with picked as (select id from ops.jobs where kind in ('build-round','close-round','expire-invite','game-deadline') and (kind<>'build-round' or $2) and ((status='Pending' and available_at<=$1)
              or (status='InFlight' and lease_expires_at<=$1)) order by available_at,id for update skip locked limit 1)
            update ops.jobs j set fencing_token=fencing_token+1,attempts=attempts+1,
              status=case when attempts>=8 then 'Failed' else 'InFlight' end,
              lease_expires_at=case when attempts>=8 then null else $1+interval '60 seconds' end,
              completed_at=case when attempts>=8 then $1 else null end,
              last_error_code=case when attempts>=8 then 'LeaseExpiredExhausted' else null end
            from picked where j.id=picked.id returning j.id,j.kind,j.aggregate_id,j.due_at,j.fencing_token,j.status
            """, Now, MatchingEnabled))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            job = new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt64(4));
            status = reader.GetString(5);
        }

        if (status != "Failed")
        {
            await tx.CommitAsync(ct);
            return job;
        }

        int released;
        try
        {
            released = await ReleaseRoomsOfExhaustedJobAsync(connection, job, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            await tx.RollbackAsync(CancellationToken.None);
            LogReleaseFailed(job, error);
            await PostponeExhaustedClaimAsync(connection, job, ct);
            throw;
        }

        await tx.CommitAsync(ct);
        LogJobExhausted(job, "LeaseExpiredExhausted", released);
        return null;
    }

    public async Task<bool> CompleteJobAsync(ParticipationJob job, CancellationToken ct)
    {
        if (job.Kind == "build-round" && !MatchingEnabled)
        {
            return false;
        }
        using var deadline = new CancellationTokenSource(JobTimeLimit, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = linked.Token;
        await using var connection = await source.OpenConnectionAsync(ct);
        // The single read that decides the completion and stamps its effects: rereading later would let a clock that
        // steps back before due_at turn the job Done without effect. Only the released_at stamps of reservation
        // releases reread the clock, and they decide nothing (see RequeueEarlyJobAsync and ADR-0021). It happens before
        // the transaction and its locks, so the instants derived from it (invite_expires_at = now + 60 s in
        // BuildRoundAsync, completed_at) are older than the commit, by up to about JobTimeLimit (3 s): the token
        // cancels the work at that limit, but a COMMIT already sent may still land a little after it. The invite window
        // may be that much shorter. Accepted in ADR-0021.
        var now = Now;
        if (job.DueAt > now)
        {
            if (await RequeueEarlyJobAsync(connection, job, now, ct) == 1 && logger is not null)
            {
                LogEarlyJobRequeued(logger, job.Id, job.Kind);
            }

            return false;
        }

        await using var tx = await connection.BeginTransactionAsync(ct);
        // Position zero: fencing, completion and aggregate changes commit together.
        var owned = await Execute(connection, """
            update ops.jobs set status='Done',completed_at=$3,lease_expires_at=null
            where id=$1 and fencing_token=$2 and status='InFlight' and lease_expires_at>$3
            """, ct, job.Id, job.Token, now);
        if (owned == 0)
        {
            return false;
        }

        if (job.Kind == "close-round")
        {
            await CloseRoundAsync(connection, job.AggregateId, now, ct);
        }
        else if (job.Kind == "build-round")
        {
            await BuildRoundAsync(connection, job.AggregateId, now, ct);
        }
        else
        {
            Guid? user;
            await using (var query = Command(connection, "select user_id from experiences.game_participants where game_session_id=$1 order by user_id limit 1", job.AggregateId))
            {
                user = (Guid?)await query.ExecuteScalarAsync(ct);
            }

            var row = user is null ? null : await LoadRoomAsync(connection, job.AggregateId, user.Value, true, ct, authorize: false);
            if (row is not null && job.Kind == "expire-invite" && row.State == "Invited" && row.Expires == job.DueAt && row.Expires <= now)
            {
                await ReleaseAsync(connection, row, "Expired", ct);
            }

            if (row?.Public is not null && job.Kind == "game-deadline" && row.State == "Active" && row.Kind == "unlikely-routes")
            {
                await ResolveRoutesDeadlineAsync(connection, row, job.DueAt, now, ct);
            }
            else if (row?.Public is not null && job.Kind == "game-deadline" && row.State == "Active")
            {
                var content = await ContentAsync(connection, row.ContentId, ct) ?? throw new InvalidOperationException("Pinned catalog missing.");
                var session = await RestoreAsync(connection, row, content, ct);
                if (session.Schedule.Deadline == job.DueAt && EngineAt(now).HandleDeadline(session, session.Version) is DeadlineOutcome<ConstellationState, ConstellationAction>.Resolved resolved)
                {
                    await SaveAsync(connection, row, ConstellationSnapshot.Capture(resolved.Session), ct);
                    await ReleaseReservationsAsync(connection, row.PairingId, ct);
                }
            }
        }
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Nenhum job de participação (<c>build-round</c>, <c>close-round</c>, <c>expire-invite</c>, <c>game-deadline</c>)
    /// entregue antes do <c>due_at</c> pode ser consumido: a conclusão veria o prazo ainda aberto e nada o reagendaria.
    /// Como o claim e a conclusão usam o mesmo relógio, isso só acontece com <c>available_at</c> mexido à mão ou com o
    /// relógio do processo recuando entre o claim e a conclusão. Volta a <c>Pending</c> no próprio <c>due_at</c>,
    /// devolvendo a tentativa do claim. Um só comando com o fencing do claim: só o dono do token o devolve, uma vez.
    /// A decisão, este UPDATE e toda a conclusão normal usam o mesmo <c>now</c>: o UPDATE de <c>Done</c>, o fechamento da
    /// rodada, a expiração do convite, <c>BuildRoundAsync</c> e o motor do jogo no <c>game-deadline</c>. Se o relógio
    /// recuar para antes do <c>due_at</c> depois da decisão, o job ainda tem efeito; não vira <c>Done</c> vazio. Só os
    /// carimbos de <c>released_at</c> da liberação de reservas releem o relógio, e eles não decidem nada.
    /// </summary>
    private static Task<int> RequeueEarlyJobAsync(NpgsqlConnection connection, ParticipationJob job, DateTimeOffset now, CancellationToken ct) =>
        Execute(connection, """
            update ops.jobs set status='Pending',available_at=due_at,attempts=attempts-1,lease_expires_at=null
            where id=$1 and fencing_token=$2 and status='InFlight' and lease_expires_at>$3 and due_at>$3
            """, ct, job.Id, job.Token, now);

    private async Task CloseRoundAsync(NpgsqlConnection connection, Guid round, DateTimeOffset now, CancellationToken ct)
    {
        await LockRoundAsync(connection, round, ct);
        List<(Guid Room, Guid User)> rooms = [];
        await using (var q = Command(connection, "select s.id,p.user_low from experiences.game_sessions s join events.pairings p on p.id=s.pairing_id join events.rounds r on r.id=p.round_id where r.id=$1 and r.deadline_at<=$2 order by s.id", round, now))
        await using (var reader = await q.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rooms.Add((reader.GetGuid(0), reader.GetGuid(1)));
            }
        }
        foreach (var (room, user) in rooms)
        {
            var row = await LoadRoomAsync(connection, room, user, true, ct, authorize: false);
            if (row is not null && row.State is "Invited" or "Active")
            {
                await ReleaseAsync(connection, row, "Expired", ct);
            }
        }
        await FinishRoundAsync(connection, round, now, ct);
    }

    private static async Task FinishRoundAsync(NpgsqlConnection connection, Guid round, DateTimeOffset now, CancellationToken ct)
    {
        // Same rule as Round.Close and Round.Resume: only a round in progress closes (no writer persists Running, so that is
        // Inviting), one that never started is cancelled, and a finished round never changes.
        await Execute(connection, """
            update events.rounds set state=case state when 'Inviting' then 'Closed' else 'Cancelled' end,version=version+1
            where id=$1 and deadline_at<=$2 and state in ('Scheduled','Inviting')
            """, ct, round, now);
        await Execute(connection, """
            update events.events set state='Completed',version=version+1 where id=(select event_id from events.rounds where id=$1)
              and not exists(select 1 from events.rounds r where r.event_id=events.events.id and r.state not in ('Closed','Cancelled'))
            """, ct, round);
    }

    private static async Task LockRoundAsync(NpgsqlConnection connection, Guid round, CancellationToken ct)
    {
        List<Guid> users = [];
        await using (var q = Command(connection, "select user_id from events.pairing_participants where round_id=$1 order by user_id", round))
        await using (var reader = await q.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                users.Add(reader.GetGuid(0));
            }
        }
        await Guard(connection, users, ct);
        await Execute(connection, "select 1 from events.events where id=(select event_id from events.rounds where id=$1) for update", ct, round);
        await Execute(connection, "select 1 from events.rounds where id=$1 for update", ct, round);
    }

    public async Task RetryJobAsync(ParticipationJob job, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var retry = Command(connection, """
            update ops.jobs set status=case when attempts>=8 then 'Failed' else 'Pending' end,
              completed_at=case when attempts>=8 then $3 else null end,lease_expires_at=null,
              available_at=$3+interval '15 seconds',last_error_code='ProcessingFailed'
            where id=$1 and fencing_token=$2 and status='InFlight' and lease_expires_at>$3
            returning status
            """, job.Id, job.Token, Now);
        var status = (string?)await retry.ExecuteScalarAsync(ct);
        var released = status == "Failed" ? await ReleaseRoomsOfExhaustedJobAsync(connection, job, ct) : 0;
        await tx.CommitAsync(ct);
        if (status == "Failed")
        {
            LogJobExhausted(job, "ProcessingFailed", released);
        }
    }

    /// <summary>
    /// Só <c>close-round</c> libera aqui: é o último job da rodada. Convite e prazo de jogo que falham ficam cobertos por
    /// ele, e <c>build-round</c> que falha não gravou reserva. Uma atualização só, sem ler o estado do jogo, para a limpeza
    /// não depender do que fez o job falhar. Sala já resolvida fica como está.
    /// </summary>
    private async Task<int> ReleaseRoomsOfExhaustedJobAsync(NpgsqlConnection connection, ParticipationJob job, CancellationToken ct)
    {
        if (job.Kind != "close-round")
        {
            return 0;
        }

        using var deadline = new CancellationTokenSource(JobTimeLimit, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        await LockRoundAsync(connection, job.AggregateId, linked.Token);
        var now = Now;
        await using var release = Command(connection, """
            with rooms as (
              update experiences.game_sessions s set state='Expired',version=s.version+1
              from events.pairings p join events.rounds r on r.id=p.round_id
              where p.id=s.pairing_id and r.id=$1 and r.deadline_at<=$2 and s.state in ('Invited','Active')
              returning s.pairing_id),
            pairings as (
              update events.pairings set released_at=coalesce(released_at,$2),release_reason='Finished'
              where id in (select pairing_id from rooms) returning id),
            participants as (
              update events.pairing_participants set released_at=coalesce(released_at,$2)
              where pairing_id in (select pairing_id from rooms) returning user_id)
            select count(*)::integer from rooms
            """, job.AggregateId, now);
        var released = (int)(await release.ExecuteScalarAsync(linked.Token))!;
        await FinishRoundAsync(connection, job.AggregateId, now, linked.Token);
        return released;
    }

    private void LogReleaseFailed(ParticipationJob job, Exception error)
    {
        if (logger is not null)
        {
            LogReleaseRefused(logger, job.Id, job.Kind, error.GetType().Name);
        }
    }

    /// <summary>Sem isto, um job esgotado que não consegue liberar seria sempre o primeiro da fila e travaria os outros.</summary>
    private async Task PostponeExhaustedClaimAsync(NpgsqlConnection connection, ParticipationJob job, CancellationToken ct) =>
        await Execute(connection, """
            update ops.jobs set lease_expires_at=$2+interval '15 seconds'
            where id=$1 and status='InFlight' and lease_expires_at<=$2
            """, ct, job.Id, Now);

    private void LogJobExhausted(ParticipationJob job, string cause, int roomsReleased)
    {
        if (logger is not null)
        {
            LogExhausted(logger, job.Id, job.Kind, cause, roomsReleased);
        }
    }

    /// <summary>Só o tipo: a mensagem da exceção pode citar valores do banco. Este giro não consome tentativa e se repete até liberar.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Participation job {JobId} ({Kind}) exhausted its attempts but could not release its open rooms ({Cause}); it will be tried again.")]
    private static partial void LogReleaseRefused(ILogger logger, Guid jobId, string kind, string cause);

    /// <summary>Só o id e o tipo do job: nada da sala nem de quem participa.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Participation job {JobId} ({Kind}) was delivered before it was due; it returned to the queue without spending an attempt.")]
    private static partial void LogEarlyJobRequeued(ILogger logger, Guid jobId, string kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Participation job {JobId} ({Kind}) exhausted its attempts ({Cause}); {RoomsReleased} open rooms were released.")]
    private static partial void LogExhausted(ILogger logger, Guid jobId, string kind, string cause, int roomsReleased);
}
