using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using JinJi.Application.Access;
using JinJi.Application.Changes;
using JinJi.Application.Organization;
using JinJi.Application.People;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace JinJi.Api;

/// <summary>
/// REST API の登録口。Host（--role api / all）から呼ばれる。
/// エンドポイントをどのポートで受けるか、認証・レート制限・不正検知の入口の共通処理は Host が決める（このライブラリはポートを知らない）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>人・組織・変更の情報は、すべて Application 層のサービス（<see cref="PersonDirectoryService"/>・<see cref="OrgDirectoryService"/>・
/// <see cref="ChangeFeedService"/>）を通して返す。人は <c>PersonProjector</c> の投影（<c>PersonView</c>）を写すだけ</item>
/// <item>閲覧者は入口の共通処理がトークンから作り、<see cref="AccessContext"/> に置く（<see cref="ViewerContextFactory"/>）</item>
/// <item>エラーは RFC 9457 Problem Details。存在しない ID と見えない ID は同じ 404 にする（列挙対策）</item>
/// <item>OpenAPI 3.1 の文書は <c>/openapi/v1.json</c>（組み込みの生成器）</item>
/// </list>
/// </remarks>
public static partial class ApiModule
{
    /// <summary>API の URL の接頭辞。</summary>
    public const string Prefix = "/api/v1";

    /// <summary>OpenAPI の文書の名前。</summary>
    public const string DocumentName = "v1";

    /// <summary>API が使うサービスを登録する（DB を使うサービスは Host が登録する）。</summary>
    public static IServiceCollection AddJinJiApi(this IServiceCollection services, ApiOptions? options = null)
    {
        var apiOptions = options ?? new ApiOptions();
        var configuredKey = apiOptions.CursorKeyBytes();
        services.TryAddSingleton(apiOptions);
        services.TryAddSingleton(sp =>
        {
            if (configuredKey is not null)
            {
                return new CursorProtector(configuredKey);
            }

            Log.EphemeralCursorKey(sp.GetRequiredService<ILoggerFactory>().CreateLogger("JinJi.Api"));
            return new CursorProtector(RandomNumberGenerator.GetBytes(32));
        });

        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            // 値の無い項目も省略せずに null を書く（OpenAPI の文書では応答の型の全項目を required にし、null になり得る項目は型に null を含める）。
            // 見えないのか値が無いのかは visible_fields で区別する
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
            // 数値を文字列で受け付けない（Web の既定の AllowReadingFromString のままだと、文書の数値の型が integer | string になる）
            json.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });
        services.AddProblemDetails();
        services.AddOpenApi(DocumentName, openApi =>
        {
            openApi.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            openApi.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "jin-ji API",
                    Version = DocumentName,
                    Description = "全社公開レベルの人事情報（人・組織・所属・上長）の時点指定の参照と変更フィード。応答は閲覧者のポリシーとクライアントの項目上限で絞られる。",
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "jin-ji の設定（JinJi:Auth:Issuers）に登録した発行者のアクセストークン",
                };
                document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] }];
                return Task.CompletedTask;
            });
            openApi.AddSchemaTransformer(OpenApiConventions.TransformSchemaAsync);
        });
        return services;
    }

    /// <summary>API のエンドポイントを登録する。</summary>
    public static IEndpointRouteBuilder MapJinJiApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok", "api")))
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithMetadata(new RequestKindMetadata(RequestKind.Other, false));
        endpoints.MapOpenApi("/openapi/{documentName}.json")
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithMetadata(new RequestKindMetadata(RequestKind.Other, false));

        var api = endpoints.MapGroup(Prefix)
            .RequireAuthorization()
            .AddEndpointFilter<AccessDeniedFilter>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        PeopleEndpoints.Map(api);
        OrgEndpoints.Map(api);
        SiteEndpoints.Map(api);
        WorkplaceEndpoints.Map(api);
        MetaEndpoints.Map(api);
        ChangeEndpoints.Map(api);
        AdminEndpoints.Map(api);
        IngestEndpoints.Map(api);
        SyncEndpoints.Map(api);
        CorrectionEndpoints.Map(api);
        FieldEndpoints.Map(api);
        GroupEndpoints.Map(api);
        PlanEndpoints.Map(api);
        return endpoints;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "JinJi:Api:CursorKey が未設定のため、カーソルの署名鍵を起動ごとに作ります。再起動・別のプロセスでは以前のカーソル（変更フィードの位置を含む）が使えません。")]
        public static partial void EphemeralCursorKey(ILogger logger);
    }
}
