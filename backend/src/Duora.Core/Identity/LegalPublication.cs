using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;

namespace Duora.Core.Identity;

public sealed record LegalDocumentSection(string Title, IReadOnlyList<string> Paragraphs);

public sealed record PublicLegalDocument(
    string Document,
    string Version,
    string Locale,
    string Title,
    string? ControllerName,
    string? PrivacyContact,
    bool Approved,
    IReadOnlyList<LegalDocumentSection> Sections)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Approval is a publication decision; content identity includes the controller and contact.
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        Document,
        Version,
        Locale,
        Title,
        ControllerName,
        PrivacyContact,
        Sections,
    }, Json)));
}

/// <summary>Published document identities are independent of account or interface language.</summary>
public sealed class LegalPublication
{
    public LegalPublication(string termsVersion, string privacyVersion, IReadOnlyList<PublicLegalDocument> documents)
    {
        TermsVersion = termsVersion;
        PrivacyVersion = privacyVersion;
        Documents = Array.AsReadOnly(documents.Select(document => document with
        {
            Sections = Array.AsReadOnly(document.Sections.Select(section => section with
            {
                Paragraphs = Array.AsReadOnly(section.Paragraphs.ToArray()),
            }).ToArray()),
        }).ToArray());

        var keys = Documents.Select(document => (document.Document, document.Version, document.Locale)).ToArray();
        var valid = keys.Distinct().Count() == keys.Length
            && Documents.All(document => document.Document is "terms" or "privacy"
                && document.Locale is "pt-BR" or "en-US"
                && ValidVersion(document.Version)
                && !string.IsNullOrWhiteSpace(document.Title)
                && document.Sections.Count > 0
                && document.Sections.All(section => !string.IsNullOrWhiteSpace(section.Title)
                    && section.Paragraphs.Count > 0 && section.Paragraphs.All(paragraph => !string.IsNullOrWhiteSpace(paragraph))));
        var active = Documents.Where(document => document.Version == (document.Document == "terms" ? termsVersion : privacyVersion)).ToArray();
        RegistrationAvailable = valid && active.Length == 4 && active.All(IsPublished)
            && active.Select(document => document.ControllerName).Distinct(StringComparer.Ordinal).Count() == 1
            && active.Select(document => document.PrivacyContact).Distinct(StringComparer.Ordinal).Count() == 1;
    }

    public string TermsVersion { get; }
    public string PrivacyVersion { get; }
    public IReadOnlyList<PublicLegalDocument> Documents { get; }
    public bool RegistrationAvailable { get; }

    public bool Accepts(string termsVersion, string privacyVersion, string? termsLocale, string? privacyLocale, string? termsSha256 = null, string? privacySha256 = null) =>
        AcceptsTerms(termsVersion, termsLocale, termsSha256)
        && AcceptsActive("privacy", PrivacyVersion, privacyVersion, privacyLocale, privacySha256);

    /// <summary>
    /// Exact receipt of the active Terms alone (API-A17, re-acceptance by an existing account): the active version,
    /// a supported locale and the digest of that exact text, under a publication open for acceptance.
    /// </summary>
    public bool AcceptsTerms(string termsVersion, string? termsLocale, string? termsSha256) =>
        AcceptsActive("terms", TermsVersion, termsVersion, termsLocale, termsSha256);

    private bool AcceptsActive(string kind, string activeVersion, string version, string? locale, string? sha256) =>
        RegistrationAvailable
        && string.Equals(version, activeVersion, StringComparison.Ordinal)
        && locale is "pt-BR" or "en-US"
        && Documents.Any(document => document.Document == kind && document.Version == version
            && document.Locale == locale && string.Equals(document.Sha256, sha256, StringComparison.Ordinal));

    public static bool IsPublished(PublicLegalDocument document) => document.Approved
        && !string.IsNullOrWhiteSpace(document.ControllerName)
        && !string.IsNullOrWhiteSpace(document.PrivacyContact)
        && MailAddress.TryCreate(document.PrivacyContact, out var address)
        && string.Equals(address.Address, document.PrivacyContact, StringComparison.Ordinal);

    private static bool ValidVersion(string version) => version.Length is > 0 and <= 32
        && char.IsAsciiLetterOrDigit(version[0])
        && version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
