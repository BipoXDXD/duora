using System.Collections.Frozen;

namespace Duora.Core.Matching;

/// <summary>Explicit private declaration. A display locale never implies proficiency.</summary>
public sealed class CommunicationDeclaration
{
    public CommunicationDeclaration(string displayLocale, IEnumerable<string> communicationLanguages,
        bool allowDifferentDisplayLocales)
    {
        if (displayLocale is not ("en-US" or "pt-BR"))
        {
            throw new ArgumentException("Unsupported display locale.", nameof(displayLocale));
        }

        ArgumentNullException.ThrowIfNull(communicationLanguages);
        var languages = communicationLanguages.ToArray();
        if (languages.Length == 0 || languages.Any(language => language is not ("en" or "pt"))
            || languages.Distinct(StringComparer.Ordinal).Count() != languages.Length)
        {
            throw new ArgumentException("Declare supported languages without duplicates.", nameof(communicationLanguages));
        }

        DisplayLocale = displayLocale;
        CommunicationLanguages = languages.ToFrozenSet(StringComparer.Ordinal);
        AllowDifferentDisplayLocales = allowDifferentDisplayLocales;
    }

    public string DisplayLocale { get; }
    public IReadOnlySet<string> CommunicationLanguages { get; }
    public bool AllowDifferentDisplayLocales { get; }
}

public sealed record CommunicationAgreement(string Language, int PolicyVersion);

public static class CommunicationPolicy
{
    public const int Version = 1;

    public static CommunicationAgreement? Agree(CommunicationDeclaration? first, CommunicationDeclaration? second)
    {
        if (first is null || second is null
            || (first.DisplayLocale != second.DisplayLocale
                && !(first.AllowDifferentDisplayLocales && second.AllowDifferentDisplayLocales)))
        {
            return null;
        }

        var common = first.CommunicationLanguages.Intersect(second.CommunicationLanguages, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).FirstOrDefault();
        return common is null ? null : new CommunicationAgreement(common, Version);
    }
}
