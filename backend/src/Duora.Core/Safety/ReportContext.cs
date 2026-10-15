namespace Duora.Core.Safety;

/// <summary>
/// Onde aconteceu o que se denuncia. Arco exclusivo (proposta D8 do modelo de dados): sala, conversa ou perfil,
/// cada caso com o próprio id tipado, sem par tipo+id em string. Perfil sem foto mapeia para nenhuma das colunas
/// de contexto; perfil com foto, para `media_asset_id`.
/// </summary>
public abstract record ReportContext
{
    private ReportContext()
    {
    }

    public sealed record Room : ReportContext
    {
        public Room(Guid gameSessionId)
        {
            ArgumentOutOfRangeException.ThrowIfEqual(gameSessionId, Guid.Empty);
            GameSessionId = gameSessionId;
        }

        public Guid GameSessionId { get; }
    }

    public sealed record Conversation : ReportContext
    {
        public Conversation(Guid conversationId)
        {
            ArgumentOutOfRangeException.ThrowIfEqual(conversationId, Guid.Empty);
            ConversationId = conversationId;
        }

        public Guid ConversationId { get; }
    }

    public sealed record Profile : ReportContext
    {
        public Profile(Guid? mediaAssetId)
        {
            if (mediaAssetId == Guid.Empty)
            {
                throw new ArgumentOutOfRangeException(nameof(mediaAssetId), "Foto denunciada precisa de id.");
            }

            MediaAssetId = mediaAssetId;
        }

        /// <summary>Foto específica do perfil, quando a denúncia é sobre ela.</summary>
        public Guid? MediaAssetId { get; }
    }
}
