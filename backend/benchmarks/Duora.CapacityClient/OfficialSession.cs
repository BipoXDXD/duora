using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Duora.CapacityClient;

/// <summary>Real HTTP cookies/CSRF and official SignalR. No production defaults or custom hub protocol.</summary>
public sealed class OfficialSession : IAsyncDisposable
{
    private readonly LoadProfile _profile;
    private readonly SyntheticUser _user;
    private readonly CapabilityBinding _capability;
    private readonly CookieContainer _cookies = new();
    private readonly X509Certificate2 _ca;
    private readonly X509Certificate2 _certificate;
    private readonly HttpClient _http;
    private readonly HttpMessageInvoker _websocketInvoker;
    private readonly Uri _origin;
    private readonly PhaseBudget _budget;
    private HubConnection? _hub;
    private IDisposable? _subscription;
    private bool _authenticated;
    private readonly LoginLifecycle _login = new();
    private long _ticks;
    private bool _hubConnected;
    private bool _reconnected;
    private long? _firstTick;
    private long _lastTick;
    private int _invalidNotification;
    public SequenceReceipt Sequence { get; } = new();
    public ParticipantReceipt Receipt() => new(_login.LoginAcknowledged, _authenticated, Interlocked.Read(ref _ticks),
        _hubConnected, _reconnected, Sequence.Received, Sequence.Gaps, Sequence.Duplicates, Sequence.OutOfOrder,
        _firstTick is { } first ? (long)Stopwatch.GetElapsedTime(first, _lastTick).TotalMilliseconds : 0,
        Volatile.Read(ref _invalidNotification) != 0);

    public OfficialSession(LoadProfile profile, SyntheticUser user, CapabilityBinding capability, PhaseBudget budget)
    {
        _profile = profile;
        _user = user;
        _capability = capability;
        _budget = budget;
        var target = profile.Target ?? throw new LoadRejectedException(LoadStopReason.Target);
        target.Validate();
        _origin = new Uri($"https://{target.ServerName}:{target.Port}");
        _ca = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(PrivateFiles.ReadPinned(profile.CertificateAuthority!)));
        _certificate = X509Certificate2.CreateFromPem(
            Encoding.UTF8.GetString(PrivateFiles.ReadPinned(profile.ClientCertificate!)),
            Encoding.UTF8.GetString(PrivateFiles.ReadPinned(profile.ClientKey!)));
        _http = new HttpClient(Handler(cookies: true)) { BaseAddress = _origin, Timeout = TimeSpan.FromSeconds(10) };
        _websocketInvoker = new HttpMessageInvoker(Handler(cookies: false));
    }

    private SocketsHttpHandler Handler(bool cookies)
    {
        var target = _profile.Target!;
        return new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = cookies,
            CookieContainer = _cookies,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxConnectionsPerServer = 1,
            MaxResponseHeadersLength = 16,
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = [_certificate],
                RemoteCertificateValidationCallback = ValidateCertificate
            },
            ConnectCallback = async (context, token) =>
            {
                token.ThrowIfCancellationRequested();
                _budget.CheckCleanup();
                if (context.DnsEndPoint.Host != target.ServerName || context.DnsEndPoint.Port != target.Port)
                { throw new LoadRejectedException(LoadStopReason.Target); }
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Parse(target.Address), target.Port, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
    }

    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? suppliedChain, SslPolicyErrors errors)
    {
        _ = sender;
        _ = suppliedChain;
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0 ||
            Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData())) != _profile.ServerCertificateSha256)
        { return false; }
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(leaf);
    }

    public async Task AuthenticateAsync(CancellationToken token)
    {
        _ = await SendAsync(HttpMethod.Get, "/api/v1/auth/csrf", null, HttpStatusCode.NoContent, token).ConfigureAwait(false);
        _ = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { _user.Email, _user.Password }, HttpStatusCode.NoContent, token).ConfigureAwait(false);
        _login.Acknowledged();
        // Auth status is not a JWT. Verify this exact synthetic participant before any room operation.
        using var me = await ReadJsonAsync("/api/v1/me", token).ConfigureAwait(false);
        var root = me.RootElement;
        if (root.GetProperty("id").GetGuid() != _user.Id || root.GetProperty("email").GetString() != _user.Email ||
            !root.GetProperty("emailConfirmed").GetBoolean() || root.GetProperty("status").GetString() != "Active" ||
            root.GetProperty("roles").EnumerateArray().Any(role => role.GetString() != "participant"))
        { throw new LoadRejectedException(LoadStopReason.Authentication); }
        _authenticated = true;
        _login.IdentityVerified();
    }

    public async Task TickAsync(CancellationToken token)
    {
        if (!_authenticated || Volatile.Read(ref _invalidNotification) != 0)
        { throw new LoadRejectedException(LoadStopReason.Authentication); }
        using (await ReadJsonAsync("/api/v1/events", token).ConfigureAwait(false))
        { }
        using var room = await ReadJsonAsync($"/api/v1/rooms/{_user.RoomId:D}", token).ConfigureAwait(false);
        if (room.RootElement.GetProperty("roomId").GetGuid() != _user.RoomId)
        { throw new LoadRejectedException(LoadStopReason.Capability); }
        Interlocked.Increment(ref _ticks);
        _lastTick = Stopwatch.GetTimestamp();
        _firstTick ??= _lastTick;
    }

    public async Task ConnectHubAsync(CancellationToken token)
    {
        if (_profile.Coverage != "signalr-invalidation" || !_authenticated || _capability.HubPath is null ||
            _capability.Notification is null || _capability.SequenceProperty is null)
        { throw new LoadRejectedException(LoadStopReason.Capability); }
        if (_hub is not null)
        { throw new LoadRejectedException(LoadStopReason.Capability); }
        var hubUri = new Uri(_origin, _capability.HubPath);
        _hub = new HubConnectionBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .WithUrl(hubUri, options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.ApplicationMaxBufferSize = 65536;
                options.TransportMaxBufferSize = 65536;
                options.HttpMessageHandlerFactory = previous => { previous.Dispose(); return Handler(cookies: true); };
                options.WebSocketFactory = async (context, cancellation) =>
                {
                    _budget.Check();
                    if (context.Uri.Scheme != "wss" || context.Uri.Host != _origin.Host || context.Uri.Port != _origin.Port ||
                        context.Uri.AbsolutePath != _capability.HubPath || context.Uri.Query.Length != 0)
                    { throw new LoadRejectedException(LoadStopReason.Target); }
                    var socket = new ClientWebSocket();
                    socket.Options.SetRequestHeader("Cookie", _cookies.GetCookieHeader(_origin));
                    socket.Options.SetRequestHeader("Origin", _origin.GetLeftPart(UriPartial.Authority));
                    try
                    {
                        await socket.ConnectAsync(context.Uri, _websocketInvoker, cancellation).ConfigureAwait(false);
                        return socket;
                    }
                    catch { socket.Dispose(); throw; }
                };
            }).Build();
        _subscription = _hub.On<JsonElement>(_capability.Notification, payload =>
        {
            // A notification never mutates authoritative state. The following HTTP tick reconciles it.
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty(_capability.SequenceProperty, out var sequence) || !sequence.TryGetInt64(out var number) ||
                number is <= 0 or > 1000000 || Sequence.Received >= 1000000)
            { Interlocked.Exchange(ref _invalidNotification, 1); return; }
            Sequence.Observe(number);
        });
        using var operation = await _budget.EnterAsync(token).ConfigureAwait(false);
        await _hub.StartAsync(token).ConfigureAwait(false);
        _hubConnected = true;
    }

    public async Task ReconnectAsync(CancellationToken token)
    {
        if (_hub is null)
        { throw new LoadRejectedException(LoadStopReason.Capability); }
        using (await _budget.EnterAsync(token).ConfigureAwait(false))
        {
            await _hub.StopAsync(token).ConfigureAwait(false);
            await _hub.StartAsync(token).ConfigureAwait(false);
        }
        await TickAsync(token).ConfigureAwait(false);
        _reconnected = true;
    }

    private async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken token)
    {
        var bytes = await SendAsync(HttpMethod.Get, path, null, HttpStatusCode.OK, token).ConfigureAwait(false);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private async Task<byte[]> SendAsync(HttpMethod method, string path, object? body, HttpStatusCode expected, CancellationToken token, bool cleanup = false)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", _origin.GetLeftPart(UriPartial.Authority));
        if (body is not null)
        {
            var csrf = _cookies.GetCookies(_origin)["XSRF-TOKEN"]?.Value;
            if (string.IsNullOrEmpty(csrf))
            { throw new LoadRejectedException(LoadStopReason.Authentication); }
            request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(csrf));
            request.Content = JsonContent.Create(body, options: PrivateFiles.JsonOptions);
        }
        Action? admitted = method == HttpMethod.Post && path == "/api/v1/auth/login" ? _login.Started : null;
        return await BoundedExchange.SendAsync(_http, request, expected, _budget, cleanup, token, admitted).ConfigureAwait(false);
    }

    public async Task LogoutAsync(CancellationToken token)
    {
        // Only this authenticated synthetic session. No account/session-list cleanup or retries.
        if (_login.RequiresLogout)
        {
            _ = await SendAsync(HttpMethod.Post, "/api/v1/auth/logout", new { }, HttpStatusCode.NoContent, token, cleanup: true).ConfigureAwait(false);
            _login.Closed();
        }
        if (!_login.CleanupKnown)
        { throw new LoadRejectedException(LoadStopReason.Cleanup); }
    }

    public async ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        try
        {
            if (_hub is not null)
            { await _hub.DisposeAsync().ConfigureAwait(false); }
        }
        finally { _http.Dispose(); _websocketInvoker.Dispose(); _certificate.Dispose(); _ca.Dispose(); }
    }
}
