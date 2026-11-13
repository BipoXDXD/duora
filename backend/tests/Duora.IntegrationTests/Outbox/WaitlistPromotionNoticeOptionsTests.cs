using Duora.Infrastructure.Email;

namespace Duora.IntegrationTests.Outbox;

/// <summary>
/// Regras da opção do consumidor de avisos de promoção (ADR-0056): bordas do lote, intervalo e marca d'água.
/// </summary>
public sealed class WaitlistPromotionNoticeOptionsTests
{
    [Theory]
    [InlineData(1, 30, false, false, true)]
    [InlineData(100, 30, false, false, true)]
    [InlineData(0, 30, false, false, false)]
    [InlineData(101, 30, false, false, false)]
    [InlineData(20, 0, false, false, false)]
    [InlineData(20, 30, true, false, false)]
    [InlineData(20, 30, true, true, true)]
    [InlineData(20, 30, false, true, true)]
    public void TheOptionsAreValidOnlyInsideTheirBounds(int batchSize, int pollSeconds, bool enabled,
        bool hasWatermark, bool expected)
    {
        var options = new WaitlistPromotionNoticeOptions
        {
            BatchSize = batchSize,
            PollInterval = TimeSpan.FromSeconds(pollSeconds),
            Enabled = enabled,
            NotBefore = hasWatermark ? new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero) : null,
        };

        Assert.Equal(expected, options.IsValid());
    }

    [Fact]
    public void TheConsumerIsOffByDefault()
    {
        var options = new WaitlistPromotionNoticeOptions();

        Assert.False(options.Enabled);
        Assert.Null(options.NotBefore);
        Assert.True(options.IsValid());
    }
}
