using System.Security.Cryptography;
using System.Text;
using Duora.Core.Identity;
using Duora.Core.Safety;
using Duora.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Duora.Infrastructure.Safety;

public sealed partial class AccountSuspensions
{
    /// <summary>
    /// Executada na transação da medida, depois da trava da conta: aprovação, exclusão e revogação da selfie usam
    /// essa mesma trava. A entrega fica travada até o commit, serializando também sua revisão e retenção. Só lê
    /// identidade estável do evento e ledger atual, nunca o payload ou a tentativa já expurgada (#319).
    /// </summary>
    private async Task<SuspensionOutcome?> ValidateSelfieRecoveryAsync(Guid subject, Guid delivery,
        CancellationToken cancellationToken)
    {
        var verificationIds = await _db.Database.SqlQuery<Guid>($"""
            select e.resource_id as "Value"
            from ops.outbox_deliveries d join ops.outbox_events e on e.id=d.outbox_event_id
            where d.id={delivery} and d.consumer={SelfieAttemptsSuspensionOutbox.Consumer}
              and d.status='Failed' and d.completed_at is not null and d.reviewed_at is null
              and e.type={SelfieAttemptsSuspensionOutbox.EventType} and e.schema_version=1
              and e.resource_type='selfie_verification'
            for update of d
            """).ToListAsync(cancellationToken);
        if (verificationIds is not [var verification])
        {
            return new SuspensionOutcome.RecoveryUnavailable();
        }

        var bound = await _db.Database.SqlQuery<bool>($"""
            select exists (select 1 from identity.selfie_failures
              where verification_id={verification} and user_id={subject}) as "Value"
            """).SingleAsync(cancellationToken);
        if (!bound)
        {
            return new SuspensionOutcome.RecoveryUnavailable();
        }

        return await SelfieFailureLedger.StandingAsync(_db, subject, _time.GetUtcNow(), cancellationToken)
            is SelfieAttemptStanding.Exhausted ? null : new SuspensionOutcome.RecoveryNotRequired();
    }

    // Domínio separado do comando comum e ligado à entrega, conta, autoria, motivo e texto. A versão do hash do
    // comando comum não muda, preservando replays existentes. Depois de uma revogação, replay só lê a medida antiga.
    private static byte[] RecoveryIntent(SuspensionOrder order, Guid delivery) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"selfie-recovery-v1\n{delivery:D}\n{Convert.ToHexString(Intent(order))}"));
}
