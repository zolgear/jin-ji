namespace JinJi.Ingest.Suggestions;

/// <summary>LLM への 1 回の問い（Structured Outputs の JSON Schema 付き）。</summary>
/// <param name="System">システムの指示（固定文）。</param>
/// <param name="User">問いとデータ（データは区切りの中に JSON で入れる）。</param>
/// <param name="SchemaName">応答の JSON Schema の名前。</param>
/// <param name="Schema">応答の JSON Schema（strict）。</param>
public sealed record LlmPrompt(string System, string User, string SchemaName, string Schema);

/// <summary>LLM の応答。</summary>
/// <param name="Json">応答の本文（JSON。スキーマに合うかは呼び出し側で検証する）。</param>
/// <param name="Model">応答したモデル名。</param>
public sealed record LlmCompletion(string Json, string Model, int InputTokens, int OutputTokens);

/// <summary>
/// LLM の呼び出し口（OpenAI 互換。実装は JinJi.Llm の OpenAI 公式 SDK、テストではフェイク）。
/// </summary>
/// <remarks>
/// 送信先が許可リストにあるか・何を送るかは呼び出し側（<see cref="ProposalSuggester"/>）が決める。実装は渡された問いを送るだけ。
/// リトライは呼び出し側が行うので、実装（SDK）の自動リトライは切る。
/// </remarks>
public interface ILlmClient
{
    /// <summary>接続先のベース URL（例: https://api.openai.com/v1）。許可リストと照合する。</summary>
    Uri Endpoint { get; }

    /// <summary>使うモデル名（設定値）。</summary>
    string Model { get; }

    /// <exception cref="LlmTransientException">一時的な失敗（429・5xx・タイムアウト・通信の失敗）。リトライしてよい。</exception>
    /// <exception cref="LlmException">リトライしても直らない失敗（認証・要求の不正など）。</exception>
    Task<LlmCompletion> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken);
}

/// <summary>LLM の呼び出しの失敗（リトライしない）。</summary>
public class LlmException : Exception
{
    public LlmException()
    {
    }

    public LlmException(string message)
        : base(message)
    {
    }

    public LlmException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>LLM の呼び出しの一時的な失敗（429・5xx・タイムアウト・通信の失敗）。</summary>
public sealed class LlmTransientException : LlmException
{
    public LlmTransientException()
    {
    }

    public LlmTransientException(string message)
        : base(message)
    {
    }

    public LlmTransientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>接続先が許可リストに無い。何も送らない。</summary>
public sealed class LlmEndpointNotAllowedException : InvalidOperationException
{
    public LlmEndpointNotAllowedException()
    {
    }

    public LlmEndpointNotAllowedException(string message)
        : base(message)
    {
    }

    public LlmEndpointNotAllowedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// LLM の設定（<c>JinJi:Llm</c>）。接続先は OpenAI / Azure OpenAI / LiteLLM などの OpenAI 互換で、ベース URL・モデル名・API キーの環境変数名で切り替える。
/// </summary>
public sealed class LlmOptions
{
    public const string SectionName = "JinJi:Llm";

    /// <summary>LLM を使えるようにするか（既定 false）。true でも、取込ごとにオプトインしなければ使わない。</summary>
    public bool Enabled { get; init; }

    /// <summary>接続先の種類（openai / azure_openai / openai_compatible。表示と記録用。呼び出し方はどれも OpenAI 互換の Chat Completions）。</summary>
    public string Provider { get; init; } = "openai";

    /// <summary>接続先のベース URL（例: https://api.openai.com/v1、Azure は https://&lt;resource&gt;.openai.azure.com/openai/v1/）。</summary>
    public string BaseUrl { get; init; } = "https://api.openai.com/v1";

    /// <summary>モデル名（固定しない。設定で指定する）。</summary>
    public string? Model { get; init; }

    /// <summary>API キーを読む環境変数の名前（キーの値は設定ファイルに書かない）。</summary>
    public string ApiKeyEnvironmentVariable { get; init; } = "OPENAI_API_KEY";

    /// <summary>送信してよい接続先（ベース URL）。ここに無い接続先には何も送らない（§8・ADR-0001 の外向き通信の許可リスト）。</summary>
    public List<string> AllowedEndpoints { get; init; } = [];

    /// <summary>1 回の呼び出しのタイムアウト。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>一時的な失敗のリトライ回数（最初の 1 回を含まない）。</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>リトライの間隔の初期値（指数で伸ばす）。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>同時に呼び出す数。</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>1 回の取込で LLM に回す要確認の提案の上限（超えた分は回さず、件数を記録する）。</summary>
    public int MaxCallsPerBatch { get; init; } = 50;

    /// <summary>1 つの問いに入れる候補の上限（人・組織それぞれ）。</summary>
    public int MaxCandidates { get; init; } = 8;

    /// <summary>応答の最大トークン数。</summary>
    public int MaxOutputTokens { get; init; } = 800;

    /// <summary>温度（null なら送らない。推論モデルは温度を受け付けないものがある）。</summary>
    public float? Temperature { get; init; } = 0f;

    /// <summary>入力 100 万トークンあたりの料金（米ドル。料金の目安のログ用。null なら計算しない）。</summary>
    public decimal? InputPricePerMillionTokens { get; init; }

    /// <summary>出力 100 万トークンあたりの料金（米ドル）。</summary>
    public decimal? OutputPricePerMillionTokens { get; init; }

    /// <summary>ベース URL を Uri にする。</summary>
    public Uri EndpointUri => new(BaseUrl, UriKind.Absolute);

    /// <summary>接続先が許可リストにあるか（スキーム・ホスト・ポート・パスを正規化して完全一致で比べる）。</summary>
    public bool IsAllowed(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var key = EndpointKey(endpoint);
        return AllowedEndpoints.Any(a => Uri.TryCreate(a, UriKind.Absolute, out var allowed) && EndpointKey(allowed) == key);
    }

    /// <summary>料金の目安（単価が未設定なら null）。</summary>
    public decimal? EstimateCost(int inputTokens, int outputTokens) =>
        InputPricePerMillionTokens is { } input && OutputPricePerMillionTokens is { } output
            ? Math.Round((inputTokens * input + outputTokens * output) / 1_000_000m, 6)
            : null;

    /// <summary>値を検査する（有効なときだけ）。不正なら起動させない。</summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new ArgumentException("JinJi:Llm:Model を指定してください（モデル名は固定しません）。", nameof(Model));
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        {
            throw new ArgumentException("JinJi:Llm:BaseUrl は https の絶対 URL で指定してください（ループバックだけ http を許す）。", nameof(BaseUrl));
        }

        if (string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable))
        {
            throw new ArgumentException("JinJi:Llm:ApiKeyEnvironmentVariable を指定してください。", nameof(ApiKeyEnvironmentVariable));
        }

        if (Timeout <= TimeSpan.Zero || MaxRetries < 0 || RetryDelay < TimeSpan.Zero || MaxConcurrency < 1 || MaxCallsPerBatch < 1
            || MaxCandidates is < 2 or > 30 || MaxOutputTokens < 100)
        {
            throw new ArgumentOutOfRangeException(nameof(LlmOptions), "JinJi:Llm のタイムアウト・リトライ・同時実行数・上限の値が不正です。");
        }

        if (InputPricePerMillionTokens < 0 || OutputPricePerMillionTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LlmOptions), "JinJi:Llm の単価は 0 以上で指定してください。");
        }
    }

    private static string EndpointKey(Uri uri) =>
        $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}{uri.AbsolutePath.TrimEnd('/').ToLowerInvariant()}";
}
