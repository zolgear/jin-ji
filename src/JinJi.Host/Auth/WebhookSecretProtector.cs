using JinJi.Application.Webhooks;
using Microsoft.AspNetCore.DataProtection;

namespace JinJi.Host.Auth;

/// <summary>
/// Webhook の署名の鍵の暗号化（Data Protection。目的 <c>JinJi.Webhooks.SigningSecret</c>）。鍵リングは DB に保存し、証明書で暗号化する（§6.4）。
/// API とワーカーを別のプロセスに分けるときは、両方に同じ Data Protection の設定（証明書）を与える（登録した鍵を送信のときに復号するため）。
/// </summary>
internal sealed class WebhookSecretProtector(IDataProtectionProvider provider) : IWebhookSecretProtector
{
    public const string Purpose = "JinJi.Webhooks.SigningSecret";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return _protector.Protect(Convert.ToBase64String(secret));
    }

    public byte[] Unprotect(string protectedSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedSecret);
        return Convert.FromBase64String(_protector.Unprotect(protectedSecret));
    }
}
