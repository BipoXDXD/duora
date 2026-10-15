using Duora.Core.Safety;
using static Duora.UnitTests.Safety.SafetyFixtures;

namespace Duora.UnitTests.Safety;

public sealed class CasePartiesTests
{
    [Theory]
    [InlineData("subject", true)]
    [InlineData("reporter", true)]
    [InlineData("stranger", false)]
    public void IncludesTheSubjectAndTheReporter(string person, bool expected)
    {
        var parties = new CaseParties(Bruno, Alice);
        var id = person switch
        {
            "subject" => Bruno,
            "reporter" => Alice,
            _ => Carla,
        };

        Assert.Equal(expected, parties.Includes(id));
    }

    [Fact]
    public void WithoutReporterOnlyTheSubjectIsAParty()
    {
        var parties = new CaseParties(Bruno, reporterId: null);

        Assert.True(parties.Includes(Bruno));
        Assert.False(parties.Includes(Alice));
    }

    [Fact]
    public void ReporterAndSubjectAreDifferentPeople()
    {
        Assert.Throws<ArgumentException>(() => new CaseParties(Bruno, Bruno));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdsCannotBeEmpty(bool emptySubject)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => emptySubject ? new CaseParties(Guid.Empty, Alice) : new CaseParties(Bruno, Guid.Empty));
    }
}
