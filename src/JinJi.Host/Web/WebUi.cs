using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace JinJi.Host.Web;

/// <summary>
/// Web UI の配信とセキュリティヘッダーの設定（appsettings の <c>JinJi:Web</c>）。
/// </summary>
internal sealed class WebUiOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:Web";

    /// <summary>
    /// SPA のビルド成果物（<c>index.html</c> と <c>assets/</c>）の場所。相対パスはコンテンツルートから解決する（既定 <c>web/dist</c>）。
    /// 無ければ（index.html が無ければ）SPA を配信せず、API だけで起動する。
    /// </summary>
    public string RootPath { get; init; } = "web/dist";

    /// <summary>HSTS（Strict-Transport-Security）。</summary>
    public HstsOptions Hsts { get; init; } = new();

    /// <summary>値を検査する。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            throw new ArgumentException("JinJi:Web:RootPath を空にはできません。", nameof(RootPath));
        }

        if (Hsts.Enabled && Hsts.MaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Hsts), Hsts.MaxAge, "JinJi:Web:Hsts:MaxAge は正の時間で指定してください。");
        }
    }
}

/// <summary>
/// HSTS の設定。jin-ji は HTTP で待ち受け、TLS はリバースプロキシで終端する前提（§7）なので、要求のスキームでは判断せず設定で付ける。
/// ブラウザは HTTP の応答の HSTS を無視するので、TLS の終端の後ろに置くときだけ有効にすればよい。
/// </summary>
internal sealed class HstsOptions
{
    /// <summary>HSTS を付けるか（既定 false。TLS を終端するリバースプロキシの後ろに置くとき true にする）。</summary>
    public bool Enabled { get; init; }

    /// <summary>max-age（既定 365 日）。</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(365);

    /// <summary>includeSubDomains を付けるか（既定 false。サブドメインの全サービスが HTTPS のときだけ）。</summary>
    public bool IncludeSubDomains { get; init; }
}

/// <summary>
/// API の公開面での SPA の配信（静的ファイルと、クライアント側のルーティングのためのフォールバック）とセキュリティヘッダー。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>配信は API のポートだけ（MCP・メトリクスのポートからは見えない）。/api・/auth・/openapi・/healthz の下は配信もフォールバックもしない</item>
/// <item>静的ファイルは認証・レート制限の前に返す（中身は公開してよい画面の殻だけで、データは API から認証付きで取る。
/// フォントの分割ファイルなど要求数が多く、未認証の要求の制限に当たらないようにするため）</item>
/// <item>フォールバック: GET / HEAD で、ファイルが無く、最後の区切りに拡張子が無いパスは index.html を返す（拡張子のあるパスは 404）</item>
/// <item>キャッシュ: ハッシュ付きのファイル（<c>/assets/</c> の下。Vite が内容のハッシュを名前に入れる）は 1 年・immutable、
/// それ以外（index.html・favicon など）は no-cache（ETag で再検証）。API の応答は no-store（個人情報を中間のキャッシュに残さない）</item>
/// <item>セキュリティヘッダーは API・MCP の公開面のすべての応答に付ける（エラー・401・429 を含む）</item>
/// </list>
/// </remarks>
internal static partial class WebUi
{
    /// <summary>
    /// Content-Security-Policy。外部のオリジンを一切許さない（ADR-0001「外部 CDN 不使用」）。
    /// <list type="bullet">
    /// <item>style-src 'self': スタイルはビルド済みの CSS ファイルだけ。React・React Flow・motion が要素に付ける style は CSSOM 経由なので CSP の対象外で、'unsafe-inline' は要らない</item>
    /// <item>img-src / worker-src の data: blob:: three.js がテクスチャ・ワーカーを data: / blob: の URL で作ることがあるため。WebGL 自体に CSP の指定は要らない</item>
    /// <item>frame-ancestors 'none'（meta では効かないのでヘッダーで付ける）、base-uri 'none'、object-src 'none'</item>
    /// </list>
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; "
        + "worker-src 'self' blob:; manifest-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// Permissions-Policy。画面が使わない機能をすべて止める（WebGL は対象外で、止める必要もない）。
    /// パスキー（WebAuthn の create / get）は同じオリジンだけに許す（ローカルアカウントのログインと登録。ADR-0004）。
    /// </summary>
    public const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), geolocation=(), gyroscope=(), "
        + "magnetometer=(), microphone=(), midi=(), payment=(), publickey-credentials-create=(self), publickey-credentials-get=(self), "
        + "screen-wake-lock=(), usb=(), xr-spatial-tracking=()";

    /// <summary>ハッシュ付きのファイルの置き場所（Vite の build.assetsDir の既定）。</summary>
    public static readonly PathString ImmutablePrefix = new("/assets");

    /// <summary>ハッシュ付きのファイルの Cache-Control。</summary>
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    /// <summary>index.html などの Cache-Control（毎回 ETag で再検証）。</summary>
    public const string RevalidateCacheControl = "no-cache";

    /// <summary>API の応答の Cache-Control。</summary>
    public const string ApiCacheControl = "no-store";

    private static readonly PathString[] ExcludedPrefixes = [new("/api"), new("/auth"), new("/oauth"), new("/.well-known"), new("/openapi"), new("/healthz"), new("/metrics")];

    /// <summary>設定を読む。SPA の成果物があればその場所の絶対パス、無ければ null。</summary>
    public static (WebUiOptions Options, string? Root) Resolve(IConfiguration configuration, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(WebUiOptions.SectionName).Get<WebUiOptions>() ?? new WebUiOptions();
        options.Validate();
        var root = Path.GetFullPath(options.RootPath, contentRootPath);
        return (options, File.Exists(Path.Combine(root, "index.html")) ? root : null);
    }

    /// <summary>セキュリティヘッダーを付ける（API・MCP の公開面）。パイプラインの最初に置く。</summary>
    public static IApplicationBuilder UseJinJiSecurityHeaders(this IApplicationBuilder app, SurfacePortMap ports, WebUiOptions options)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(options);
        var hsts = options.Hsts.Enabled
            ? $"max-age={(long)options.Hsts.MaxAge.TotalSeconds}" + (options.Hsts.IncludeSubDomains ? "; includeSubDomains" : string.Empty)
            : null;

        return app.Use(async (http, next) =>
        {
            if (ports.Resolve(http.Connection.LocalPort) is Surface.Api or Surface.Mcp)
            {
                http.Response.OnStarting(() =>
                {
                    var headers = http.Response.Headers;
                    headers.ContentSecurityPolicy = ContentSecurityPolicy;
                    headers.XContentTypeOptions = "nosniff";
                    headers.XFrameOptions = "DENY";
                    headers["Referrer-Policy"] = "no-referrer";
                    headers["Permissions-Policy"] = PermissionsPolicy;
                    headers["Cross-Origin-Opener-Policy"] = "same-origin";
                    headers["Cross-Origin-Resource-Policy"] = "same-origin";
                    if (hsts is not null)
                    {
                        headers.StrictTransportSecurity = hsts;
                    }

                    if (IsExcluded(http.Request.Path) && string.IsNullOrEmpty(headers.CacheControl))
                    {
                        headers.CacheControl = ApiCacheControl;
                    }

                    return Task.CompletedTask;
                });
            }

            await next(http);
        });
    }

    /// <summary>
    /// SPA を配信する（API の公開面だけ）。認証・レート制限より前に置く。<paramref name="root"/> が null なら何もしない（API だけで動く）。
    /// </summary>
    public static IApplicationBuilder UseJinJiSpa(this IApplicationBuilder app, SurfacePortMap ports, string? root, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(ports);
        if (root is null)
        {
            LogSpaNotFound(logger);
            return app;
        }

        LogSpaRoot(logger, root);
        var files = new PhysicalFileProvider(root);
        return app.UseWhen(
            http => ports.Resolve(http.Connection.LocalPort) == Surface.Api
                && (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
                && !IsExcluded(http.Request.Path),
            branch =>
            {
                branch.Use(async (http, next) =>
                {
                    // クライアント側のルーティング: ファイルが無く、拡張子の無いパスは index.html を返す
                    var path = http.Request.Path;
                    if (!files.GetFileInfo(path.Value ?? "/").Exists && !HasExtension(path))
                    {
                        http.Request.Path = "/index.html";
                    }

                    await next(http);
                });
                branch.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = files,
                    ServeUnknownFileTypes = false,
                    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
                        context.Context.Request.Path.StartsWithSegments(ImmutablePrefix, StringComparison.Ordinal) ? ImmutableCacheControl : RevalidateCacheControl,
                });
            });
    }

    private static bool IsExcluded(PathString path) =>
        ExcludedPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool HasExtension(PathString path)
    {
        var value = path.Value ?? string.Empty;
        var last = value[(value.LastIndexOf('/') + 1)..];
        return last.Contains('.', StringComparison.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Web UI を {Root} から配信します")]
    private static partial void LogSpaRoot(ILogger logger, string root);

    [LoggerMessage(Level = LogLevel.Information, Message = "Web UI の成果物（JinJi:Web:RootPath の index.html）が無いため、API だけで起動します")]
    private static partial void LogSpaNotFound(ILogger logger);
}
