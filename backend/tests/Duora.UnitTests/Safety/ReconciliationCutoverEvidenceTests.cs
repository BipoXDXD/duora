using Duora.Core.Safety;

namespace Duora.UnitTests.Safety;

/// <summary>
/// Descritor do corte da reconciliação (issue #321): vem só da configuração do servidor e precisa nascer válido,
/// com identificador fechado e instantes UTC explícitos.
/// </summary>
public sealed class ReconciliationCutoverEvidenceTests
{
    private const string Lower = "2026-10-31T12:00:00Z";
    private const string Upper = "2026-10-31T12:10:00.5Z";

    [Fact]
    public void AReviewedDescriptorKeepsItsIdAndTheUtcWindow()
    {
        Assert.True(ReconciliationCutoverEvidence.TryParse("deploy-2026-10-31.r1:migration", Lower, Upper, out var evidence));

        Assert.Equal("deploy-2026-10-31.r1:migration", evidence!.Id);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), evidence.Cutover.Lower);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 12, 10, 0, 500, TimeSpan.Zero), evidence.Cutover.Upper);
    }

    /// <summary>O PostgreSQL guarda microssegundos: um limite mais fino nunca voltaria igual do run gravado.</summary>
    [Theory]
    [InlineData("2026-10-31T12:00:00.123456Z", true)]
    [InlineData("2026-10-31T12:00:00.1234567Z", false)]
    public void TheBoundsStopAtMicroseconds(string lower, bool accepted) =>
        Assert.Equal(accepted, ReconciliationCutoverEvidence.TryParse("deploy-1", lower, Upper, out _));

    [Fact]
    public void AnExistingCutoverKeepsTheClosedAlphabetOfTheId()
    {
        var cutover = ReconciliationCutover.Between(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.Equal("deploy-1", ReconciliationCutoverEvidence.Of("deploy-1", cutover).Id);
        Assert.Throws<ArgumentException>(() => ReconciliationCutoverEvidence.Of("deploy 1", cutover));
    }

    [Fact]
    public void AnExistingCutoverCannotCarryLessThanAMicrosecond()
    {
        var cutover = ReconciliationCutover.Between(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddTicks(1));

        Assert.Throws<ArgumentException>(() => ReconciliationCutoverEvidence.Of("deploy-1", cutover));
    }

    [Fact]
    public void AnAtomicCutoverHasEqualBounds()
    {
        Assert.True(ReconciliationCutoverEvidence.TryParse("atomic", Lower, Lower, out var evidence));

        Assert.Equal(evidence!.Cutover.Lower, evidence.Cutover.Upper);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("deploy 1")]
    [InlineData("deploy/1")]
    [InlineData("implantação")]
    [InlineData("deploy\u00001")]
    [InlineData("deploy\n1")]
    public void AnIdOutsideTheClosedAlphabetIsRefused(string? id)
    {
        Assert.False(ReconciliationCutoverEvidence.TryParse(id, Lower, Upper, out var evidence));
        Assert.Null(evidence);
    }

    [Fact]
    public void TheIdLengthHasAnInclusiveCeiling()
    {
        var atLimit = new string('a', ReconciliationCutoverEvidence.MaxIdLength);

        Assert.True(ReconciliationCutoverEvidence.TryParse(atLimit, Lower, Upper, out _));
        Assert.False(ReconciliationCutoverEvidence.TryParse(atLimit + "a", Lower, Upper, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-31T12:00:00")]
    [InlineData("2026-10-31T12:00:00+00:00")]
    [InlineData("2026-10-31T09:00:00-03:00")]
    [InlineData("2026-10-31 12:00:00Z")]
    [InlineData("31/10/2026 12:00:00Z")]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59.9999999Z")]
    [InlineData("infinity")]
    public void ALowerBoundThatIsNotAnExplicitFiniteUtcInstantIsRefused(string? lower)
    {
        Assert.False(ReconciliationCutoverEvidence.TryParse("deploy", lower, Upper, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2026-10-31T12:10:00")]
    [InlineData("9999-12-31T23:59:59.9999999Z")]
    public void AnUpperBoundThatIsNotAnExplicitFiniteUtcInstantIsRefused(string? upper)
    {
        Assert.False(ReconciliationCutoverEvidence.TryParse("deploy", Lower, upper, out _));
    }

    [Fact]
    public void AnUpperBoundBeforeTheLowerIsRefused()
    {
        Assert.False(ReconciliationCutoverEvidence.TryParse("deploy", "2026-10-31T12:00:00.0000001Z", "2026-10-31T12:00:00Z",
            out _));
    }
}
