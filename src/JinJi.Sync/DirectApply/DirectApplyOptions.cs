namespace JinJi.Sync.DirectApply;

/// <summary>資格情報の種類。クライアントシークレットは扱わない（overview §8「証明書かワークロード ID で認証する」）。</summary>
public enum GraphCredentialKind
{
    /// <summary>証明書（PFX）で署名したクライアント アサーション。</summary>
    Certificate,

    /// <summary>ワークロード ID（フェデレーションのトークンのファイル）をクライアント アサーションにする。</summary>
    WorkloadIdentity,
}

/// <summary>
/// Graph への直接反映の設定（<c>JinJi:Sync:DirectApply</c>）。<b>既定は無効</b>。読み取り用とは別の資格情報で、明示的に有効にしたときだけ使う（overview §1・§8）。
/// </summary>
/// <remarks>
/// 書き込みの権限（User.ReadWrite.All のアプリケーションのアクセス許可）を持つ資格情報なので、API・MCP の役割のプロセスには置かない運用にする
/// （直接反映は CLI / ワーカーからだけ起動し、REST API からは起動しない）。証明書のパスワードは設定に書かず、環境変数の名前だけを書く。
/// </remarks>
public sealed class DirectApplyOptions
{
    public const string SectionName = "JinJi:Sync:DirectApply";

    /// <summary>直接反映を使えるようにするか（既定 false）。</summary>
    public bool Enabled { get; init; }

    /// <summary>テナント ID（UUID か検証済みドメイン）。</summary>
    public string? TenantId { get; init; }

    /// <summary>書き込み用のアプリ（クライアント）ID。読み取り用とは別のアプリにする。</summary>
    public string? ClientId { get; init; }

    public GraphCredentialKind Credential { get; init; } = GraphCredentialKind.Certificate;

    /// <summary>証明書（PFX）のパス（Credential = Certificate）。</summary>
    public string? CertificatePath { get; init; }

    /// <summary>証明書のパスワードを読む環境変数の名前（値は設定に書かない。パスワードが無ければ null）。</summary>
    public string? CertificatePasswordEnvironmentVariable { get; init; }

    /// <summary>フェデレーションのトークンのファイル（Credential = WorkloadIdentity。null なら環境変数 AZURE_FEDERATED_TOKEN_FILE）。</summary>
    public string? FederatedTokenFile { get; init; }

    public string AuthorityHost { get; init; } = "https://login.microsoftonline.com";

    public string GraphBaseUrl { get; init; } = "https://graph.microsoft.com/v1.0";

    /// <summary>送信してよいホスト（認証と Graph。ここに無い接続先には何も送らない）。</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = ["login.microsoftonline.com", "graph.microsoft.com"];

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>429・503 のときに Retry-After を待って再送する回数。</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Retry-After の待ち時間の上限。</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    public Uri GraphBaseUri => new(GraphBaseUrl.TrimEnd('/') + "/");

    public Uri TokenEndpoint => new($"{AuthorityHost.TrimEnd('/')}/{Uri.EscapeDataString(TenantId ?? string.Empty)}/oauth2/v2.0/token");

    /// <summary>有効なら、必須の値と接続先を検査する。不正なら <see cref="InvalidOperationException"/>。</summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TenantId) || string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException($"{SectionName}: TenantId と ClientId は必須です。");
        }

        foreach (var url in new[] { AuthorityHost, GraphBaseUrl })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException($"{SectionName}: 接続先は https の URL にしてください（{url}）。");
            }

            if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{SectionName}: 接続先 {uri.Host} は AllowedHosts にありません。");
            }
        }

        if (Credential == GraphCredentialKind.Certificate && string.IsNullOrWhiteSpace(CertificatePath))
        {
            throw new InvalidOperationException($"{SectionName}: 証明書（CertificatePath）を指定してください。");
        }

        if (MaxRetries is < 0 or > 5 || Timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}: MaxRetries は 0〜5、Timeout は正の値にしてください。");
        }
    }
}
