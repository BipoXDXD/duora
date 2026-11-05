using System.Globalization;
using Duora.Core.Events;
using Duora.Core.Safety;

namespace Duora.UnitTests.Safety;

/// <summary>
/// Digest de uma unidade da reconciliação (ADR-0049, D1). O valor literal foi calculado fora do código, com SHA-256
/// sobre o texto canônico v1: mudar a serialização invalida prévias em circulação e tem de quebrar este teste.
/// </summary>
public sealed class ReconciliationUnitDigestTests
{
    private const string Expected = "486d6d15d6f766e4ddda7c4fd584923d5e266d2630fdaf24c1fe904aa27bceab";

    private static readonly Guid Subject = Guid.Parse("0194f1e0-0000-7000-8000-000000000001");
    private static readonly Guid Measure = Guid.Parse("0194f1e0-0000-7000-8000-000000000002");
    private static readonly Guid FirstEvent = Guid.Parse("0194f1e0-0000-7000-8000-00000000000a");

    // Em Guid.CompareTo este vem antes do primeiro (o primeiro bloco é int32 com sinal); na ordem canônica, depois.
    private static readonly Guid SecondEvent = Guid.Parse("ffffffff-0000-7000-8000-00000000000b");

    private static readonly ReconciliationPreviewItem Confirmed = new(FirstEvent, 4, RegistrationState.Confirmed, 1, 1);
    private static readonly ReconciliationPreviewItem Waitlisted = new(SecondEvent, 2, RegistrationState.Waitlisted, 0, 0);

    private static ReconciliationCutoverEvidence Evidence(
        string id = "deploy-2026-10-31.r1:migration", string lower = "2026-10-31T12:00:00Z", string upper = "2026-10-31T12:10:00.5Z")
    {
        Assert.True(ReconciliationCutoverEvidence.TryParse(id, lower, upper, out var evidence));
        return evidence!;
    }

    private static string Digest(
        int selectorVersion = 1,
        ReconciliationCutoverEvidence? evidence = null,
        Guid? subject = null,
        Guid? measure = null,
        long measureVersion = 3,
        IReadOnlyList<ReconciliationPreviewItem>? items = null) =>
        ReconciliationUnitDigest.Compute(selectorVersion, evidence ?? Evidence(), subject ?? Subject, measure ?? Measure,
            measureVersion, items ?? [Confirmed, Waitlisted]);

    [Fact]
    public void TheCanonicalSerializationHasAFixedDigest() => Assert.Equal(Expected, Digest());

    [Fact]
    public void TheItemOrderIsCanonicalAndNotTheOrderOfTheRead() =>
        Assert.Equal(Expected, Digest(items: [Waitlisted, Confirmed]));

    /// <summary>
    /// A assinatura aceita negativo e a cultura muda só o sinal dele: é o único caso em que a formatação sem cultura
    /// fixa divergiria. Versão real é positiva; o caso negativo existe para prender a serialização invariante.
    /// </summary>
    [Theory]
    [InlineData("sv-SE")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public void TheCurrentCultureDoesNotChangeTheDigest(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);
        Assert.NotEqual(CultureInfo.InvariantCulture.NumberFormat.NegativeSign, culture.NumberFormat.NegativeSign);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariantNegative = Digest(selectorVersion: -1, measureVersion: -3);

            CultureInfo.CurrentCulture = culture;

            Assert.Equal(Expected, Digest());
            Assert.Equal(invariantNegative, Digest(selectorVersion: -1, measureVersion: -3));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    public static TheoryData<string> ChangedUnits =>
    [
        Digest(selectorVersion: 2),
        Digest(evidence: Evidence(id: "deploy-2")),
        Digest(evidence: Evidence(lower: "2026-10-31T12:00:01Z")),
        Digest(evidence: Evidence(upper: "2026-10-31T12:10:01Z")),
        Digest(subject: Guid.Parse("0194f1e0-0000-7000-8000-000000000003")),
        Digest(measure: Guid.Parse("0194f1e0-0000-7000-8000-000000000003")),
        Digest(measureVersion: 4),
        Digest(items: [Confirmed]),
        Digest(items: [Confirmed with { EventId = Guid.Parse("0194f1e0-0000-7000-8000-00000000000c") }, Waitlisted]),
        Digest(items: [Confirmed with { RegistrationVersion = 5 }, Waitlisted]),
        Digest(items: [Confirmed with { State = RegistrationState.CheckedIn }, Waitlisted]),
        Digest(items: [Confirmed with { MaxReleasedSeats = 0 }, Waitlisted]),
        Digest(items: [Confirmed with { MaxPromotions = 0 }, Waitlisted]),
    ];

    [Theory]
    [MemberData(nameof(ChangedUnits))]
    public void EveryFieldOfTheManifestChangesTheDigest(string changed) => Assert.NotEqual(Expected, changed);

    [Fact]
    public void TheDigestIsLowercaseHexOfSha256()
    {
        Assert.True(ReconciliationUnitDigest.IsWellFormed(Expected));
        Assert.Equal(ReconciliationUnitDigest.Length, Expected.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000f")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000A")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000g")]
    public void AnythingElseIsNotADigest(string? digest) => Assert.False(ReconciliationUnitDigest.IsWellFormed(digest));
}
