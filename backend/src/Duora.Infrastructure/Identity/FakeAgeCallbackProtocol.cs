using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duora.Core.Identity;

namespace Duora.Infrastructure.Identity;

/// <summary>
/// Protocolo de callback do fornecedor simulado, no estilo do Stripe (ADR-0024, decisão 2): header
/// <c>Duora-Fake-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;[,v1=&lt;hex&gt;]</c>, com HMAC-SHA256 sobre
/// <c>"{t}." + corpo</c>, os bytes exatamente como chegaram. Aceita qualquer <c>v1</c> que confira com qualquer dos
/// segredos configurados (rotação) dentro de ±5 minutos do relógio da aplicação.
/// </summary>
internal static class FakeAgeCallbackProtocol
{
    public const string SignatureHeader = "Duora-Fake-Signature";

    /// <summary>Teto do corpo do webhook (model.md, <c>ops.webhook_receipts</c>).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    public const string CompletedType = "age_check.completed";

    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private const int MaxHeaderLength = 512;
    private const int SignatureHexLength = 64;
    private const int MaxFieldLength = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 4,
    };

    /// <summary>Assinatura de um corpo, como o fornecedor simulado manda (também usada pelos testes).</summary>
    public static string Sign(ReadOnlySpan<byte> body, long unixSeconds, string secret)
    {
        var mac = Compute(body, unixSeconds.ToString(CultureInfo.InvariantCulture), secret);
        return $"t={unixSeconds.ToString(CultureInfo.InvariantCulture)},v1={Convert.ToHexStringLower(mac)}";
    }

    /// <summary>Corpo JSON do callback de conclusão.</summary>
    public static byte[] Body(string eventId, string reference, string nonce, string outcome) =>
        JsonSerializer.SerializeToUtf8Bytes(new FakeCallbackBody(eventId, CompletedType, reference, nonce, outcome), Json);

    /// <summary>
    /// Confere a assinatura antes de qualquer parse. Todas as comparações de MAC são de tempo constante e todas as
    /// combinações de <c>v1</c> e segredo são testadas, para o tempo não dizer qual falhou.
    /// </summary>
    public static bool IsAuthentic(ReadOnlySpan<byte> body, string? header, IReadOnlyList<string> secrets, DateTimeOffset now)
    {
        if (!TryParseHeader(header, out var timestamp, out var signatures))
        {
            return false;
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds)
            || unixSeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            || (now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds)).Duration() > Tolerance)
        {
            return false;
        }

        var matched = false;
        foreach (var secret in secrets)
        {
            var expected = Compute(body, timestamp, secret);
            foreach (var signature in signatures)
            {
                matched |= CryptographicOperations.FixedTimeEquals(expected, signature);
            }
        }

        return matched;
    }

    /// <summary>Lê o corpo já autenticado contra o schema próprio; qualquer desvio é inválido.</summary>
    public static bool TryReadBody(ReadOnlySpan<byte> body, out AgeCallback? callback)
    {
        callback = null;
        FakeCallbackBody? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FakeCallbackBody>(body, Json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null
            || parsed.Type != CompletedType
            || !IsField(parsed.Id)
            || !IsField(parsed.Reference)
            || !IsField(parsed.Nonce)
            || ParseOutcome(parsed.Outcome) is not { } outcome)
        {
            return false;
        }

        callback = new AgeCallback(parsed.Id, parsed.Reference, parsed.Nonce, outcome);
        return true;
    }

    private static AgeCheckOutcome? ParseOutcome(string? value) => value switch
    {
        "verified_adult" => AgeCheckOutcome.VerifiedAdult,
        "not_adult" => AgeCheckOutcome.NotAdult,
        "inconclusive" => AgeCheckOutcome.Inconclusive,
        _ => null,
    };

    private static bool IsField([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= MaxFieldLength } && value.All(c => c is > ' ' and <= '~');

    private static byte[] Compute(ReadOnlySpan<byte> body, string timestamp, string secret)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
    }

    private static bool TryParseHeader(string? header, out string timestamp, out List<byte[]> signatures)
    {
        timestamp = "";
        signatures = [];
        if (string.IsNullOrEmpty(header) || header.Length > MaxHeaderLength)
        {
            return false;
        }

        foreach (var part in header.Split(','))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            var (key, value) = (part[..separator], part[(separator + 1)..]);
            if (key == "t" && timestamp.Length == 0 && value.Length is > 0 and <= 19)
            {
                timestamp = value;
            }
            else if (key == "v1" && value.Length == SignatureHexLength && value.All(char.IsAsciiHexDigit))
            {
                signatures.Add(Convert.FromHexString(value));
            }
            else if (key is "t" or "v1")
            {
                return false;
            }
        }

        return timestamp.Length > 0 && signatures.Count > 0;
    }

    private sealed record FakeCallbackBody(string? Id, string? Type, string? Reference, string? Nonce, string? Outcome);
}

/// <summary>Callback autenticado e validado, já no vocabulário do Duora.</summary>
internal sealed record AgeCallback(string EventId, string ProviderReference, string RequestNonce, AgeCheckOutcome Outcome);

/// <summary>
/// Lado "fornecedor" do simulado: entrega um callback já assinado uma vez, ou duas vezes iguais no modo
/// <see cref="FakeAgeProviderMode.DuplicateCallback"/>, como um fornecedor que reenvia (T-17).
/// </summary>
internal sealed class FakeAgeProviderSimulator(FakeAgeProviderMode mode)
{
    public IReadOnlyList<(byte[] Body, string Signature)> Deliveries(byte[] body, string signature) =>
        mode == FakeAgeProviderMode.DuplicateCallback ? [(body, signature), (body, signature)] : [(body, signature)];
}
