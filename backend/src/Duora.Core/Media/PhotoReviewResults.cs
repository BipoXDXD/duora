namespace Duora.Core.Media;

/// <summary>
/// Resultados públicos editoriais do upload (ADR-0035). Não são rejeições técnicas do decoder nem códigos de
/// validação em errors. M1 prepara a leitura/persistência; nenhum escritor os produz nesta entrega.
/// </summary>
public static class PhotoReviewResults
{
    public const string Rejected = "PHOTO_REVIEW_REJECTED";

    public const string Expired = "PHOTO_REVIEW_EXPIRED";

    public const string Unavailable = "PHOTO_REVIEW_UNAVAILABLE";

    public static IReadOnlyList<string> Codes { get; } = Array.AsReadOnly(new[] { Rejected, Expired, Unavailable });
}
