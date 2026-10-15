using Duora.Core.Profiles;

namespace Duora.UnitTests.Profiles;

/// <summary>
/// I2: reconstituir do banco não revalida. Se a regra de entrada endurecer, uma linha antiga continua legível; quem
/// garante o mínimo no banco é o CHECK de tamanho.
/// </summary>
public sealed class StoredProfileTextTests
{
    [Fact]
    public void DisplayNameFromStorageKeepsATextTheInputRuleNowRejects()
    {
        const string Stored = "Ana\u200BSouza";

        Assert.False(DisplayName.TryParse(Stored, out _, out _));
        Assert.Equal(Stored, DisplayName.FromStorage(Stored).Value);
    }

    [Fact]
    public void BioFromStorageKeepsATextTheInputRuleNowRejects()
    {
        const string Stored = "Oi\u200Dtudo";

        Assert.False(ProfileBio.TryParse(Stored, out _, out _));
        Assert.Equal(Stored, ProfileBio.FromStorage(Stored).Value);
    }
}
