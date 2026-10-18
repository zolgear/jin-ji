using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JinJi.Ingest.Suggestions;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// テスト用の LLM（実 LLM は呼ばない）。送られた問いを記録し、応答は関数で決める（決定的）。
/// </summary>
internal sealed partial class FakeLlmClient : ILlmClient
{
    public const string DefaultEndpoint = "https://llm.example.test/v1";

    private readonly Func<LlmPrompt, JsonObject, int, Task<string>> _respond;
    private readonly List<LlmPrompt> _prompts = [];
    private int _calls;

    /// <param name="respond">問い・データ（区切りの中の JSON）・この問いの何回目の呼び出しか（0 始まり）→ 応答の JSON。</param>
    public FakeLlmClient(Func<LlmPrompt, JsonObject, int, Task<string>> respond, string endpoint = DefaultEndpoint)
    {
        _respond = respond;
        Endpoint = new Uri(endpoint);
    }

    public FakeLlmClient(Func<JsonObject, string> respond, string endpoint = DefaultEndpoint)
        : this((_, data, _) => Task.FromResult(respond(data)), endpoint)
    {
    }

    public Uri Endpoint { get; }

    public string Model => "fake-model";

    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<LlmPrompt> Prompts
    {
        get
        {
            lock (_prompts)
            {
                return [.. _prompts];
            }
        }
    }

    /// <summary>テスト用の設定（許可リストに既定の接続先。リトライの待ちなし）。</summary>
    public static LlmOptions Options(string? allowed = DefaultEndpoint, int maxRetries = 2, TimeSpan? timeout = null, int maxCalls = 50) => new()
    {
        Enabled = true,
        BaseUrl = DefaultEndpoint,
        Model = "fake-model",
        AllowedEndpoints = allowed is null ? [] : [allowed],
        RetryDelay = TimeSpan.Zero,
        MaxRetries = maxRetries,
        Timeout = timeout ?? TimeSpan.FromSeconds(10),
        MaxCallsPerBatch = maxCalls,
        InputPricePerMillionTokens = 0.4m,
        OutputPricePerMillionTokens = 1.6m,
    };

    /// <summary>問いのデータ（区切りの中の JSON）。</summary>
    public static JsonObject Data(LlmPrompt prompt)
    {
        var match = DataBlock().Match(prompt.User);
        Assert.True(match.Success, "データの区切りがありません");
        return JsonNode.Parse(match.Groups["json"].Value)!.AsObject();
    }

    /// <summary>候補のラベルを、データの順に並べた応答（確信度は 0.9 から下げる）。<paramref name="extra"/> は候補に無いラベル。</summary>
    public static string RankInOrder(JsonObject data, string reason = "理由", params string[] extra)
    {
        static JsonArray Ranking(JsonArray? candidates, string reason, string[] extra)
        {
            var result = new JsonArray();
            var labels = (candidates ?? []).Select(c => c!["id"]!.GetValue<string>()).Concat(candidates is null ? [] : extra).ToList();
            for (var i = 0; i < labels.Count; i++)
            {
                result.Add(new JsonObject { ["id"] = labels[i], ["confidence"] = Math.Round(0.9 - (0.1 * i), 2), ["reason"] = reason });
            }

            return result;
        }

        return new JsonObject
        {
            ["person_ranking"] = Ranking(data["person_candidates"] as JsonArray, reason, extra),
            ["org_ranking"] = Ranking(data["org_candidates"] as JsonArray, reason, extra),
        }.ToJsonString();
    }

    public async Task<LlmCompletion> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        int attempt;
        lock (_prompts)
        {
            attempt = _prompts.Count(p => p.User == prompt.User);
            _prompts.Add(prompt);
        }

        Interlocked.Increment(ref _calls);
        var json = await _respond(prompt, Data(prompt), attempt).WaitAsync(cancellationToken);
        return new LlmCompletion(json, "fake-model-2026", 100, 20);
    }

    [GeneratedRegex(@"<data-(?<nonce>[0-9a-f]+)>\r?\n(?<json>.*)\r?\n</data-\k<nonce>>", RegexOptions.Singleline)]
    private static partial Regex DataBlock();
}
