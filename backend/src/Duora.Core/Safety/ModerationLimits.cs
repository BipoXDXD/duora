namespace Duora.Core.Safety;

/// <summary>
/// Tetos de duração das medidas temporárias, vindos do parameter set versionado (spec 4.3). Uma medida automatizada
/// longa equivaleria a um banimento sem humano (spec 12.3, TM-34), por isso tem teto próprio e menor.
/// </summary>
public sealed record ModerationLimits
{
    /// <summary>Teto absoluto de qualquer medida temporária; acima disso, só banimento decidido por pessoa.</summary>
    public const int AbsoluteMaxDays = 365;

    public ModerationLimits(TimeSpan maxAutomatedDuration, TimeSpan maxDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAutomatedDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAutomatedDuration, maxDuration);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDuration, TimeSpan.FromDays(AbsoluteMaxDays));
        MaxAutomatedDuration = maxAutomatedDuration;
        MaxDuration = maxDuration;
    }

    /// <summary>Restrição aplicada pelo sistema (contenção) dura no máximo isto.</summary>
    public TimeSpan MaxAutomatedDuration { get; }

    public TimeSpan MaxDuration { get; }

    internal TimeSpan MaxDurationFor(ModerationAuthor author) =>
        author is ModerationAuthor.Automated ? MaxAutomatedDuration : MaxDuration;
}
