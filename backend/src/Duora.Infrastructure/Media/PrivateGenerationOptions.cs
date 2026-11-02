using Duora.Core.Media;

namespace Duora.Infrastructure.Media;

internal sealed class PrivateGenerationOptions
{
    internal const string Section = "Media:PrivateGenerations";
    public bool Enabled { get; set; }
    public string SocketPath { get; set; } = "";
    public uint AuthorityUid { get; set; }
    public Guid DomainId { get; set; }
    public long Epoch { get; set; } = 1;
    public int Layout { get; set; } = 1;
    internal PrivateGenerationDomain Domain => new(DomainId, Epoch, Layout);
}
