using JinJi.Ingest.Suggestions;

namespace JinJi.Llm;

/// <summary>
/// 最初の呼び出しのときに <see cref="OpenAiLlmClient"/> を作る（API キーの環境変数は使うときに初めて読む）。
/// LLM を有効にしたまま API キーを渡していない構成でも、LLM を使わない取込は動く。LLM にオプトインした取込は失敗する。
/// </summary>
public sealed class DeferredLlmClient : ILlmClient
{
    private readonly LlmOptions _options;
    private readonly Lazy<OpenAiLlmClient> _client;

    public DeferredLlmClient(LlmOptions options, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _client = new Lazy<OpenAiLlmClient>(() => OpenAiLlmClient.Create(options, environment), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Uri Endpoint => _options.EndpointUri;

    public string Model => _options.Model!;

    public Task<LlmCompletion> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken) =>
        _client.Value.CompleteAsync(prompt, cancellationToken);
}
