using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using JinJi.Application.Webhooks;
using JinJi.Host.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JinJi.IntegrationTests.Support;

/// <summary>受け手が受け取った 1 回の POST。</summary>
internal sealed record ReceivedWebhook(string Path, string? Id, string? Timestamp, string? Signature, string Body, DateTimeOffset ReceivedAt);

/// <summary>
/// テスト用の Webhook の受け手（https。自己署名の証明書をテストのたびに作る）。パスごとに受け取った要求を記録し、応答の状態コードを決められる。
/// 送信先のホスト名は <see cref="Host"/>（DNS には無い名前）で、<see cref="Sender"/> の接続の処理がループバックの待ち受けに繋ぐ
/// （本番の送信はループバックに繋がない。<see cref="PublicAddressGuard"/>）。
/// </summary>
internal sealed class WebhookReceiver : IAsyncDisposable
{
    public const string Host = "receiver.jinji.test";

    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;
    private readonly ConcurrentQueue<ReceivedWebhook> _received = new();
    private readonly ConcurrentDictionary<string, Func<int>> _responders = new(StringComparer.Ordinal);

    private WebhookReceiver(WebApplication app, X509Certificate2 certificate, int port)
    {
        _app = app;
        _certificate = certificate;
        Port = port;
    }

    public int Port { get; }

    public IReadOnlyList<ReceivedWebhook> Received => [.. _received];

    /// <summary>パスの送信先の URL。</summary>
    public static string Url(string path) => $"https://{Host}/{path.TrimStart('/')}";

    public IReadOnlyList<ReceivedWebhook> For(string path) => [.. _received.Where(r => r.Path == "/" + path.TrimStart('/'))];

    /// <summary>パスの応答の状態コードを決める（既定 200）。</summary>
    public void Respond(string path, Func<int> status) => _responders["/" + path.TrimStart('/')] = status;

    public static async Task<WebhookReceiver> StartAsync()
    {
        var certificate = CreateCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        WebhookReceiver? receiver = null;
        app.MapPost("/{**path}", async (HttpContext http) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync(http.RequestAborted);
            var path = http.Request.Path.Value ?? "/";
            receiver!._received.Enqueue(new ReceivedWebhook(
                path,
                http.Request.Headers[WebhookSignature.IdHeader].FirstOrDefault(),
                http.Request.Headers[WebhookSignature.TimestampHeader].FirstOrDefault(),
                http.Request.Headers[WebhookSignature.SignatureHeader].FirstOrDefault(),
                body,
                DateTimeOffset.UtcNow));
            var status = receiver._responders.TryGetValue(path, out var responder) ? responder() : 200;
            if (status is >= 300 and < 400)
            {
                http.Response.Headers.Location = $"https://{Host}/redirected";
            }

            return Results.StatusCode(status);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        receiver = new WebhookReceiver(app, certificate, new Uri(address).Port);
        return receiver;
    }

    /// <summary>
    /// テスト用の送信（本番と同じ <see cref="WebhookSender"/>。接続先だけをこの受け手に向け、この受け手の証明書だけを信頼する。リダイレクトには従わない）。
    /// </summary>
    public WebhookSender Sender(WebhookOptions options)
    {
        var thumbprint = _certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), thumbprint, StringComparison.OrdinalIgnoreCase);
        return new WebhookSender(options, handler);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _certificate.Dispose();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={Host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Host);
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Linux の SslStream は秘密鍵を持つ PKCS#12 から読み直した証明書を要る
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }
}
