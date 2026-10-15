using Duora.Core.Safety;

namespace Duora.UnitTests.Safety;

/// <summary>Relógio controlado pelo teste; o projeto não traz FakeTimeProvider e Safety só precisa do instante.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public static FixedClock AtStart() => new(SafetyFixtures.Start);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan elapsed) => _now += elapsed;
}

internal static class SafetyFixtures
{
    public static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    public static readonly Guid Bruno = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    public static readonly Guid Carla = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    public static readonly Guid ModeratorMaria = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid ModeratorPedro = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    public static readonly Guid Evidence = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    public static readonly Guid GameSession = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    public static readonly PolicyVersion Policy = new("conduta", "2026-10");
    public static readonly PolicyVersion RevisedPolicy = new("conduta", "2026-11");

    public static readonly TimeSpan OneWeek = TimeSpan.FromDays(7);

    public static ReportSubmission Submission(ReportCategory category = ReportCategory.Harassment) =>
        new(Alice, Bruno, category, new ReportContext.Room(GameSession), ReportNarrative.Parse("Insultos no chat."));

    public static ModerationDecision Decision(
        ModerationMeasure measure = ModerationMeasure.Suspension,
        ModerationAuthor? author = null,
        TimeSpan? duration = null) =>
        new(
            measure,
            Policy,
            [Evidence],
            "Assédio reiterado na sala.",
            author ?? new ModerationAuthor.Human(ModeratorMaria),
            duration ?? DefaultDurationFor(measure));

    public static ModerationAction SanctionOn(
        Guid subject,
        ModerationMeasure measure = ModerationMeasure.Suspension,
        TimeProvider? clock = null) =>
        ModerationAction.DecideWithoutReport(subject, Decision(measure), clock ?? FixedClock.AtStart());

    /// <summary>Leva uma denúncia de Alice contra Bruno até o estado pedido pelo caminho válido.</summary>
    public static Report ReportIn(ReportState state, TimeProvider? clock = null)
    {
        clock ??= FixedClock.AtStart();
        var report = Report.Submit(Submission(), clock);
        if (state == ReportState.Received)
        {
            return report;
        }

        report.Triage(ReportPriority.Normal, new ModerationAuthor.Automated());
        if (state == ReportState.Triaged)
        {
            return report;
        }

        report.StartReview(ModeratorMaria);
        if (state == ReportState.UnderReview)
        {
            return report;
        }

        if (state == ReportState.Closed)
        {
            report.Close(ModeratorMaria, "Sem violação da política.", clock);
            return report;
        }

        var action = report.TakeAction(Decision(), clock);
        if (state == ReportState.Actioned)
        {
            return report;
        }

        report.MarkAppealed(Appeal.File(action, Bruno, "Foi um mal-entendido.", clock));
        return report;
    }

    private static TimeSpan? DefaultDurationFor(ModerationMeasure measure) =>
        measure is ModerationMeasure.Restriction or ModerationMeasure.Suspension ? OneWeek : null;
}
