namespace Duora.Contracts.Identity;

public sealed record LegalDocumentSectionResponse(string Title, IReadOnlyList<string> Paragraphs);

public sealed record LegalDocumentResponse(
    string Document, string Version, string Locale, string Title,
    string? ControllerName, string? PrivacyContact, bool Published, string Sha256,
    IReadOnlyList<LegalDocumentSectionResponse> Sections);

public sealed record LegalDocumentCatalogResponse(
    bool RegistrationAvailable, string TermsVersion, string PrivacyVersion,
    IReadOnlyList<LegalDocumentResponse> Documents);
