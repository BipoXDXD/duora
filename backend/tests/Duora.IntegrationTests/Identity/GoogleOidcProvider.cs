using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Duora.IntegrationTests.Identity;

/// <summary>Only the network boundary is simulated: production OIDC handler validates the signed JWT.</summary>
internal sealed class GoogleOidcProvider : HttpMessageHandler
{
    private static readonly string[] CodeResponse = ["code"];
    private static readonly string[] PublicSubject = ["public"];
    private static readonly string[] Rs256 = ["RS256"];
    private static readonly string[] S256 = ["S256"];
    private readonly RSA _key = RSA.Create(2048);
    private readonly Dictionary<string, (string Nonce, string Challenge, string Redirect)> _codes = new(StringComparer.Ordinal);
    public string Subject { get; set; } = $"synthetic-google-{Guid.NewGuid():N}";
    public string Email { get; set; } = IdentityScenario.NewEmail();
    public string Issuer { get; set; } = "https://accounts.google.com";
    public string Audience { get; set; } = "synthetic-duora-client";
    public bool VerifiedEmail { get; set; } = true;
    public bool WrongNonce { get; set; }
    public bool Expired { get; set; }
    public bool WrongSignature { get; set; }
    public int TokenRequests { get; private set; }

    public string Callback(Uri authorization)
    {
        var query = QueryHelpers.ParseQuery(authorization.Query);
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.Equal("synthetic-duora-client", query["client_id"].ToString());
        var code = Guid.NewGuid().ToString("N");
        _codes.Add(code, (query["nonce"].ToString(), query["code_challenge"].ToString(), query["redirect_uri"].ToString()));
        return QueryHelpers.AddQueryString("/signin-google-oidc", new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = query["state"].ToString(),
            ["iss"] = "https://accounts.google.com",
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            return Json(new
            {
                issuer = "https://accounts.google.com",
                authorization_endpoint = "https://accounts.google.com/authorize",
                token_endpoint = "https://accounts.google.com/token",
                jwks_uri = "https://accounts.google.com/keys",
                response_types_supported = CodeResponse,
                subject_types_supported = PublicSubject,
                id_token_signing_alg_values_supported = Rs256,
                code_challenge_methods_supported = S256,
            });
        }
        if (url.EndsWith("/keys", StringComparison.Ordinal))
        {
            var key = _key.ExportParameters(false);
            return Json(new { keys = new[] { new { kty = "RSA", use = "sig", kid = "synthetic-key", alg = "RS256", n = Base64Url.EncodeToString(key.Modulus!), e = Base64Url.EncodeToString(key.Exponent!) } } });
        }
        Assert.EndsWith("/token", url, StringComparison.Ordinal);
        TokenRequests++;
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
        Assert.Equal("authorization_code", form["grant_type"].ToString());
        Assert.Equal("synthetic-duora-client", form["client_id"].ToString());
        Assert.Equal("synthetic-duora-secret", form["client_secret"].ToString());
        if (!_codes.Remove(form["code"].ToString(), out var issued))
        {
            return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
        }
        Assert.Equal(issued.Redirect, form["redirect_uri"].ToString());
        Assert.Equal(issued.Challenge, Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Encode(new { alg = "RS256", kid = "synthetic-key", typ = "JWT" });
        var payload = Encode(new { iss = Issuer, aud = Audience, sub = Subject, email = Email, email_verified = VerifiedEmail, nonce = WrongNonce ? "wrong-nonce" : issued.Nonce, iat = now - 1, exp = Expired ? now - 600 : now + 300 });
        var signing = header + "." + payload;
        using var wrong = WrongSignature ? RSA.Create(2048) : null;
        var signature = (wrong ?? _key).SignData(Encoding.ASCII.GetBytes(signing), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Json(new { token_type = "Bearer", expires_in = 300, access_token = "synthetic-unused-access-token", id_token = signing + "." + Base64Url.EncodeToString(signature) });
    }

    private static string Encode<T>(T value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));
    private static HttpResponseMessage Json<T>(T value, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _key.Dispose();
        }

        base.Dispose(disposing);
    }
}
