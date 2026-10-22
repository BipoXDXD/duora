using System.Text.Json;
using Duora.Core.Media;

namespace Duora.IntegrationTests.Media;

internal static class NativeSanitizerCorpus
{
    internal static void AssertEquivalent(SanitizerBaselineCase item, JsonElement baseline, SanitizeOutcome actual) =>
        throw new NotImplementedException();
}
