namespace Duora.Core.Identity;

/// <summary>Estados da conta (spec 4.2). Persistidos e expostos pelo nome, nunca pelo número.</summary>
public enum AccountStatus
{
    PendingEmail,
    PendingEligibility,
    Active,
    Restricted,
    Suspended,
    DeletionPending,
}
