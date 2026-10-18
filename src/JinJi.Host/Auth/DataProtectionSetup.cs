using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using JinJi.Application.Auth;
using JinJi.Application.Webhooks;
using JinJi.Infrastructure.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace JinJi.Host.Auth;

/// <summary>
/// Data Protection の鍵の設定（appsettings の <c>JinJi:DataProtection</c>）。鍵リングは PostgreSQL に保存し、X.509 証明書で暗号化する。
/// </summary>
/// <remarks>
/// 証明書は PKCS#12（.pfx / .p12）か PEM（証明書と秘密鍵を同じファイルに）のファイル、または環境変数に入れた PKCS#12 の base64 で渡す。
/// 証明書なしは <see cref="AllowUnprotectedKeys"/> を明示したときだけ許し、起動時に警告する（開発用）。どちらも無ければ起動しない。
/// </remarks>
internal sealed class DataProtectionSettings
{
    public const string SectionName = "JinJi:DataProtection";

    /// <summary>鍵を暗号化する証明書（新しい鍵の暗号化と復号の両方に使う）。</summary>
    public CertificateSource? Certificate { get; init; }

    /// <summary>入れ替え前の証明書（復号だけに使う）。</summary>
    public List<CertificateSource> PreviousCertificates { get; init; } = [];

    /// <summary>証明書なしで鍵を平文の XML として保存することを許す（開発用。既定 false）。</summary>
    public bool AllowUnprotectedKeys { get; init; }
}

/// <summary>証明書の渡し方（ファイルか base64 のどちらか一方）。</summary>
internal sealed class CertificateSource
{
    /// <summary>証明書のファイル（.pfx / .p12 は PKCS#12、それ以外は PEM）。</summary>
    public string? Path { get; init; }

    /// <summary>PKCS#12 の base64（環境変数 <c>JinJi__DataProtection__Certificate__Base64</c> などで渡す）。</summary>
    public string? Base64 { get; init; }

    /// <summary>PKCS#12 のパスワード（環境変数で渡す）。</summary>
    public string? Password { get; init; }

    public X509Certificate2 Load()
    {
        X509Certificate2 certificate;
        if (!string.IsNullOrWhiteSpace(Path) == !string.IsNullOrWhiteSpace(Base64))
        {
            throw new ArgumentException("JinJi:DataProtection の証明書は Path か Base64 のどちらか一方で指定してください。", nameof(Path));
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(Base64))
            {
                certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(Base64), Password, X509KeyStorageFlags.EphemeralKeySet);
            }
            else if (Path!.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(Path, Password, X509KeyStorageFlags.EphemeralKeySet);
            }
            else
            {
                certificate = X509Certificate2.CreateFromPemFile(Path);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"JinJi:DataProtection の証明書を読めません: {ex.Message}", nameof(Path), ex);
        }

        if (!certificate.HasPrivateKey || certificate.GetRSAPublicKey() is null)
        {
            certificate.Dispose();
            throw new ArgumentException("JinJi:DataProtection の証明書は RSA の秘密鍵を含めてください（鍵の暗号化・復号に使います）。", nameof(Path));
        }

        return certificate;
    }
}

/// <summary>Data Protection の登録。</summary>
internal static partial class DataProtectionSetup
{
    /// <summary>アプリケーション名（同じ DB を使うすべてのインスタンスで鍵を共有する）。</summary>
    public const string ApplicationName = "jin-ji";

    public static IServiceCollection AddJinJiDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new DataProtectionSettings();
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
            new ConfigureOptions<KeyManagementOptions>(options => options.XmlRepository = new PostgresXmlRepository(sp.GetRequiredService<IDataProtectionKeyStore>())));

        if (settings.Certificate is { } source)
        {
            var certificate = source.Load();
            builder.ProtectKeysWithCertificate(certificate);
            builder.UnprotectKeysWithAnyCertificate([certificate, .. settings.PreviousCertificates.Select(c => c.Load())]);
        }
        else if (settings.AllowUnprotectedKeys)
        {
            services.AddHostedService<UnprotectedKeysWarning>();
        }
        else
        {
            throw new InvalidOperationException(
                "Data Protection の鍵を暗号化する証明書がありません。JinJi:DataProtection:Certificate（Path か Base64）を設定してください"
                + "（開発で証明書なしにするなら JinJi:DataProtection:AllowUnprotectedKeys=true を明示します）。");
        }

        return services;
    }

    /// <summary>
    /// 人のログインの無い役割（ワーカー・MCP）の Data Protection。社内システムへの Webhook の署名の鍵を復号するために使う。
    /// 設定（証明書か AllowUnprotectedKeys）があるときだけ登録し、無ければ登録しない（鍵の復号は使うときに失敗し、Webhook の送信だけが止まる。
    /// Webhook を使わない構成のワーカーに証明書を求めない）。既に登録済み（人のログインの役割）なら何もしない。
    /// </summary>
    public static IServiceCollection AddJinJiDataProtectionIfConfigured(this IServiceCollection services, IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(IDataProtectionProvider)))
        {
            return services;
        }

        var settings = configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new DataProtectionSettings();
        if (settings.Certificate is null && !settings.AllowUnprotectedKeys)
        {
            return services;
        }

        services.TryAddSingleton<NpgsqlAuthStore>();
        services.TryAddSingleton<IDataProtectionKeyStore>(sp => sp.GetRequiredService<NpgsqlAuthStore>());
        return services.AddJinJiDataProtection(configuration);
    }

    /// <summary>Webhook の署名の鍵の暗号化。Data Protection が無い構成では、使うときに理由を示して失敗するものを登録する。</summary>
    public static IServiceCollection AddJinJiWebhookSecrets(this IServiceCollection services)
    {
        services.TryAddSingleton<IWebhookSecretProtector>(sp => sp.GetService<IDataProtectionProvider>() is { } provider
            ? new WebhookSecretProtector(provider)
            : new UnavailableWebhookSecretProtector());
        return services;
    }

    /// <summary>Data Protection の設定が無いワーカーの代わり（Webhook の鍵を扱おうとしたら理由を示して失敗する）。</summary>
    private sealed class UnavailableWebhookSecretProtector : IWebhookSecretProtector
    {
        public string Protect(byte[] secret) => throw Missing();

        public byte[] Unprotect(string protectedSecret) => throw Missing();

        private static InvalidOperationException Missing() => new(
            "Webhook の署名の鍵を扱うには Data Protection の設定（JinJi:DataProtection:Certificate。API の役割と同じ証明書）が要ります。");
    }

    /// <summary>証明書なしで動かしていることを起動時に警告する。</summary>
    private sealed partial class UnprotectedKeysWarning(ILogger<UnprotectedKeysWarning> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            LogUnprotected(logger);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        [LoggerMessage(Level = LogLevel.Warning, Message = "Data Protection の鍵を証明書で暗号化せずに DB に保存します（JinJi:DataProtection:AllowUnprotectedKeys）。開発用の設定です。本番では証明書を設定してください")]
        private static partial void LogUnprotected(ILogger logger);
    }
}

/// <summary>Data Protection の鍵リングの保存先（PostgreSQL の data_protection_key。追記だけ）。</summary>
internal sealed class PostgresXmlRepository(IDataProtectionKeyStore store) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements() => [.. store.LoadAll().Select(XElement.Parse)];

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        store.Store(friendlyName, element.ToString(SaveOptions.DisableFormatting));
    }
}
