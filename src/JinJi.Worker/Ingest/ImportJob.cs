using System.Security.Cryptography;
using System.Threading.Channels;
using JinJi.Connectors;
using JinJi.Ingest;
using JinJi.Policy;
using AccessChannel = JinJi.Policy.Channel;
using Channel = System.Threading.Channels.Channel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JinJi.Worker.Ingest;

/// <summary>取込のジョブの要求。</summary>
/// <param name="MappingPath">項目マッピング（JSON）のパス。</param>
/// <param name="FilePath">取込元のファイル（CSV / xlsx）のパス。</param>
/// <param name="ExecutedByPersonId">取込の実行者（人）。その取込の提案を承認できない（§5.5）。</param>
/// <param name="EffectiveDate">変更の有効日。null なら差分を訂正として提案する。</param>
/// <param name="UseLlm">要確認の提案に LLM による候補提示を行うか（取込ごとのオプトイン。既定 false）。</param>
public sealed record ImportJobRequest(string MappingPath, string FilePath, Guid ExecutedByPersonId, DateOnly? EffectiveDate = null, bool UseLlm = false);

/// <summary>
/// 取込のジョブ: 項目マッピングを読み、コネクタで取込元を読み（許可リスト外の列はここで捨てる）、取込の中核（<see cref="ImportService"/>）に渡す。
/// ワーカー（<see cref="ImportJobRunner"/>）と開発用の CLI（<c>JinJi.Host import</c>）から呼ぶ。
/// </summary>
public sealed class ImportJob
{
    private readonly ImportService _service;

    public ImportJob(ImportService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public async Task<ImportResult> RunAsync(ViewerContext viewer, ImportJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(request);

        var mappingBytes = await File.ReadAllBytesAsync(request.MappingPath, cancellationToken);
        var mapping = ConnectorMapping.Parse(mappingBytes);
        var content = await File.ReadAllBytesAsync(request.FilePath, cancellationToken);
        var fileName = Path.GetFileName(request.FilePath);
        var table = SourceReader.Read(content, fileName, mapping);

        return await _service.ImportAsync(
            viewer,
            new ImportRequest
            {
                Table = table,
                Profile = mapping.Profile,
                FileName = fileName,
                FileSha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
                MappingName = mapping.Name,
                MappingSha256 = mapping.Sha256,
                ExecutedByPersonId = request.ExecutedByPersonId,
                EffectiveDate = request.EffectiveDate,
                UseLlm = request.UseLlm,
            },
            cancellationToken);
    }
}

/// <summary>取込のジョブの待ち行列（プロセス内）。API からの起動は後の手順で足す。</summary>
public sealed class ImportJobQueue
{
    private readonly Channel<ImportJobRequest> _channel = Channel.CreateUnbounded<ImportJobRequest>(new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(ImportJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _channel.Writer.WriteAsync(request, cancellationToken);
    }

    internal ChannelReader<ImportJobRequest> Reader => _channel.Reader;
}

/// <summary>
/// 取込のジョブを順に実行するホスト型サービス（--role worker / all）。閲覧者は jin-ji 自身（system、経路 worker）で、実行者は要求の人。
/// </summary>
internal sealed partial class ImportJobRunner(ImportJobQueue queue, IServiceProvider services, ILogger<ImportJobRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        try
        {
            await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // DB の設定はジョブを実行するときに初めて解決する（DB を使わない構成でもワーカーを起動できるように）
                    var job = services.GetRequiredService<ImportJob>();
                    var result = await job.RunAsync(ViewerContext.ForSystem(AccessChannel.Worker), request, stoppingToken);
                    LogCompleted(logger, result.BatchId, result.RowCount, result.ProposalCount, result.NeedsReviewCount, result.CorrectionCount);
                    if (result.Llm is { } llm)
                    {
                        // 送った値は出さない（回数・トークン数・料金の目安だけ）
                        LogLlm(logger, result.BatchId, llm.EndpointHost, llm.Model, llm.Questions, llm.Calls, llm.InputTokens, llm.OutputTokens, llm.EstimatedCostUsd?.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) ?? "単価未設定");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(logger, ex, Path.GetFileName(request.FilePath));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停止要求による終了
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ワーカーを開始しました（取込のジョブを待ちます）")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "取込が完了しました: バッチ {BatchId}、{Rows} 行、提案 {Proposals} 件、要確認 {NeedsReview} 件、修正依頼 {Corrections} 件")]
    private static partial void LogCompleted(ILogger logger, Guid batchId, int rows, int proposals, int needsReview, int corrections);

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM による候補提示: バッチ {BatchId}、送信先 {Endpoint}、モデル {Model}、問い {Questions} 件、呼び出し {Calls} 回、入力 {InputTokens} / 出力 {OutputTokens} トークン、料金の目安 {CostUsd} USD")]
    private static partial void LogLlm(ILogger logger, Guid batchId, string endpoint, string model, int questions, int calls, int inputTokens, int outputTokens, string costUsd);

    [LoggerMessage(Level = LogLevel.Error, Message = "取込に失敗しました: {FileName}")]
    private static partial void LogFailed(ILogger logger, Exception exception, string fileName);
}
