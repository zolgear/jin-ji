using System.Text.Json;
using JinJi.Application.Access;
using JinJi.Policy;
using Microsoft.AspNetCore.Http;

namespace JinJi.Mcp.Tools;

/// <summary>
/// jin-ji の MCP ツール。名前・説明・入力スキーマ・スコープ・エンドポイント種別を宣言し、Application 層のサービスを通して結果を返す。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>SDK のツールの属性・反射による登録は使わない（入力スキーマを手で書き、上限・形式を明示するため。ADR-0006）</item>
/// <item>人・組織・変更はすべて REST API と同じ Application 層のサービスを通し、人は <see cref="PersonView"/>（<c>PersonProjector</c> の投影）を写すだけ。
/// MCP だけの抜け道を作らない</item>
/// <item>許可リスト・スコープ・監査・返却人数の記録は <see cref="McpToolDispatcher"/> が共通に行う</item>
/// </list>
/// </remarks>
internal abstract class McpTool
{
    /// <summary>ツールの名前（<see cref="McpToolNames"/>）。</summary>
    public abstract string Name { get; }

    /// <summary>人が読む表示名。</summary>
    public abstract string Title { get; }

    /// <summary>エージェントが読む説明（引数の意味・上限・ページング・結果の形・データの扱い）。</summary>
    public abstract string Description { get; }

    /// <summary>入力の JSON Schema。</summary>
    public abstract JsonElement InputSchema { get; }

    /// <summary>要るスコープ（参照は jinji.read、提案は jinji.propose）。</summary>
    public virtual string Scope => Application.Auth.OAuthScopes.Read;

    /// <summary>エンドポイント種別（レート制限の段・返却人数・メトリクス。REST API の同じ操作と同じ種別）。</summary>
    public abstract RequestKindMetadata Kind { get; }

    /// <summary>参照だけか（ツールの注釈 readOnlyHint）。</summary>
    public virtual bool ReadOnly => true;

    /// <summary>ツールを実行する。引数の誤り・見つからない・拒否は <see cref="McpToolException"/>。</summary>
    public abstract Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken);
}

/// <summary>ツールの実行の文脈（1 回の tools/call）。</summary>
/// <param name="Access">入口の共通処理が作った観測値（閲覧者・トークン・返却人数）。</param>
internal sealed record McpToolContext(HttpContext Http, AccessContext Access, ViewerContext Viewer, IServiceProvider Services);

/// <summary>ツールの結果。</summary>
/// <param name="Structured">structuredContent に載せる値（JSON のオブジェクトになる型）。</param>
/// <param name="Summary">テキストでの要約（1〜2 文）。</param>
/// <param name="Subjects">結果に含めた人・対象にした人（監査ログの対象者。値は残さない）。</param>
/// <param name="PeopleReturned">返した人数（返却人数の制限に数える）。</param>
/// <param name="IsBroadListing">絞り込みの無い一覧（全社横断の取得。不正検知に渡す）。</param>
internal sealed record McpToolOutput(object Structured, string Summary, IReadOnlyCollection<Guid> Subjects, int PeopleReturned = 0, bool IsBroadListing = false);

/// <summary>ツールの誤りの種類（REST API の状態コードに対応させ、不正検知・メトリクスを揃える）。</summary>
internal enum McpToolErrorKind
{
    /// <summary>引数の誤り（400 相当）。</summary>
    InvalidArgument,

    /// <summary>見つからない・参照できない（404 相当。存在しないのか見えないのかは区別しない）。</summary>
    NotFound,

    /// <summary>許可されていない（403 相当。未来の時点の指定など）。</summary>
    Forbidden,

    /// <summary>対象の状態と合わない（409 相当）。</summary>
    Conflict,
}

/// <summary>ツールの誤り。エージェントが読んで直せるように、結果（isError）として返す（MCP 仕様: ツールの実行の誤りは JSON-RPC の誤りにしない）。</summary>
internal sealed class McpToolException : Exception
{
    public McpToolException()
    {
    }

    public McpToolException(string message)
        : base(message)
    {
    }

    public McpToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public McpToolException(McpToolErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public McpToolErrorKind Kind { get; } = McpToolErrorKind.InvalidArgument;

    /// <summary>誤りのコード（structuredContent の error.code）。</summary>
    public string Code => Kind switch
    {
        McpToolErrorKind.InvalidArgument => "invalid_argument",
        McpToolErrorKind.NotFound => "not_found",
        McpToolErrorKind.Forbidden => "forbidden",
        McpToolErrorKind.Conflict => "conflict",
        _ => "error",
    };

    /// <summary>対応する REST API の状態コード。</summary>
    public int Status => Kind switch
    {
        McpToolErrorKind.NotFound => StatusCodes.Status404NotFound,
        McpToolErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        McpToolErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

    public static McpToolException NotFound(string what) =>
        new(McpToolErrorKind.NotFound, $"{what}は存在しないか、参照できません（存在しないのか参照できないのかは区別しません）。");

    public static McpToolException Invalid(string message) => new(McpToolErrorKind.InvalidArgument, message);
}
