using Duora.Core.Safety;

namespace Duora.UnitTests.Safety;

internal static class SafetyAssert
{
    public static T Accepted<T>(this SafetyResult<T> result)
        where T : class
    {
        Assert.True(result.IsSuccess, $"Rejeitado: {result.Rejection}");
        return result.Value;
    }

    public static void Accepted(this SafetyResult result) =>
        Assert.True(result.IsSuccess, $"Rejeitado: {result.Rejection}");

    public static void Rejected<T>(SafetyRejection expected, SafetyResult<T> result)
        where T : class =>
        Assert.Equal(expected, result.Rejection);

    public static void Rejected(SafetyRejection expected, SafetyResult result) =>
        Assert.Equal(expected, result.Rejection);
}
