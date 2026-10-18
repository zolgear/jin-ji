using System.Text.Json;
using System.Threading.RateLimiting;
using JinJi.Application.Auth;
using Microsoft.AspNetCore.DataProtection;

namespace JinJi.Host.Auth;

/// <summary>
/// ログインの試行のレート制限（ASP.NET Core の RateLimiter。ADR-0004「ブルートフォース」）。
/// </summary>
/// <remarks>
/// 接続元アドレスごとの制限を先に確かめ、超えていればアカウントの段には数えない（1 つの接続元から多くのアカウントを試させない）。
/// 次に接続元アドレス × アカウント（正規化したユーザー名）で数える。既定では後者を Identity のロックアウトの回数より小さくし、
/// 1 つの接続元からの試行だけではアカウントをロックアウトさせない（ロックアウトによる妨害を抑える）。状態はプロセス内（§1）。
/// </remarks>
internal sealed class LoginThrottle : IDisposable
{
    private const int SegmentsPerWindow = 6;
    private readonly PartitionedRateLimiter<string> _perAddress;
    private readonly PartitionedRateLimiter<string> _perAccount;
    private readonly TimeSpan _window;

    public LoginThrottle(LoginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var limits = options.RateLimit;
        _window = limits.Window;
        _perAddress = PartitionedRateLimiter.Create<string, string>(key => Sliding(key, limits.PerAddress, limits.Window));
        _perAccount = PartitionedRateLimiter.Create<string, string>(key => Sliding(key, limits.PerAddressAndAccount, limits.Window));
    }

    /// <summary>試行を 1 回数える。超えていれば待つべき時間（Retry-After）。</summary>
    /// <param name="account">正規化したユーザー名（パスキーのようにまだ分からなければ null）。</param>
    public TimeSpan? TryAcquire(HttpContext http, string? account)
    {
        ArgumentNullException.ThrowIfNull(http);
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        using (var lease = _perAddress.AttemptAcquire(address))
        {
            if (!lease.IsAcquired)
            {
                return RetryAfter(lease);
            }
        }

        if (account is null)
        {
            return null;
        }

        using var accountLease = _perAccount.AttemptAcquire($"{address}|{account}");
        return accountLease.IsAcquired ? null : RetryAfter(accountLease);
    }

    public void Dispose()
    {
        _perAddress.Dispose();
        _perAccount.Dispose();
    }

    private TimeSpan RetryAfter(RateLimitLease lease) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : _window / SegmentsPerWindow;

    private static RateLimitPartition<string> Sliding(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            SegmentsPerWindow = SegmentsPerWindow,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
}

/// <summary>ログイン・登録の途中の状態の段階。</summary>
internal enum PendingStep
{
    /// <summary>パスワードは通り、TOTP かリカバリコードを待っている。</summary>
    TwoFactor,

    /// <summary>パスキーでのログインの challenge を出した。</summary>
    PasskeyLogin,

    /// <summary>ログイン中の人のパスキーの登録の challenge を出した。</summary>
    PasskeyRegistration,

    /// <summary>リンクによるパスキーの登録の challenge を出した。</summary>
    ResetPasskey,
}

/// <summary>ログイン・登録の途中の状態（短命の Cookie に Data Protection で保護して持たせる）。</summary>
internal sealed record PendingState(
    PendingStep Step,
    Guid? AccountId,
    string? SecurityStamp,
    string? PasskeyState,
    Guid? LinkId,
    Guid? SessionId);

/// <summary>
/// ログイン・登録の途中の状態の Cookie（<c>__Host-jinji.pending</c>。5 分で失効）。中身は Data Protection の時間制限付きの保護。
/// 次の段の要求で読み、段が違えば・期限切れなら使わない。使ったら消す（同じ状態を 2 度使わせない）。
/// </summary>
internal sealed class PendingLoginCookie(IDataProtectionProvider dataProtection, TimeProvider timeProvider)
{
    public const string Name = "__Host-jinji.pending";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector("JinJi.Auth.Pending").ToTimeLimitedDataProtector();

    public void Write(HttpContext http, PendingState state)
    {
        ArgumentNullException.ThrowIfNull(http);
        var payload = _protector.Protect(JsonSerializer.Serialize(state), timeProvider.GetUtcNow() + Lifetime);
        http.Response.Cookies.Append(Name, payload, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            MaxAge = Lifetime,
        });
    }

    /// <summary>段が一致する状態を読み、Cookie を消す。無い・壊れている・期限切れ・段が違えば null。</summary>
    public PendingState? Take(HttpContext http, PendingStep step)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!http.Request.Cookies.TryGetValue(Name, out var payload) || string.IsNullOrEmpty(payload))
        {
            return null;
        }

        Clear(http);
        try
        {
            var state = JsonSerializer.Deserialize<PendingState>(_protector.Unprotect(payload, out var expiration));
            return state is not null && state.Step == step && expiration > timeProvider.GetUtcNow() ? state : null;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }

    public static void Clear(HttpContext http) =>
        http.Response.Cookies.Delete(Name, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
}
