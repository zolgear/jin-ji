using System.ClientModel;
using System.ClientModel.Primitives;
using JinJi.Ingest.Suggestions;
using OpenAI;
using OpenAI.Chat;

namespace JinJi.Llm;

/// <summary>
/// OpenAI 公式 .NET SDK による LLM の呼び出し（Chat Completions + Structured Outputs）。接続先はベース URL で切り替える
/// （OpenAI: https://api.openai.com/v1、Azure OpenAI: https://&lt;resource&gt;.openai.azure.com/openai/v1/、LiteLLM などの OpenAI 互換のプロキシ）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>API キーは環境変数（名前は設定 <see cref="LlmOptions.ApiKeyEnvironmentVariable"/>）から読む。値をログ・例外のメッセージに出さない</item>
/// <item>接続先が許可リストに無ければ作らない（送信の前に <see cref="ProposalSuggester"/> も確かめる。二重の守り）</item>
/// <item>SDK の自動リトライは切る（リトライは <see cref="ProposalSuggester"/> がまとめて行い、回数を数える）。タイムアウトは SDK の通信と呼び出し側の両方にかける</item>
/// </list>
/// </remarks>
public sealed class OpenAiLlmClient : ILlmClient
{
    private readonly ChatClient _chat;
    private readonly LlmOptions _options;

    private OpenAiLlmClient(ChatClient chat, LlmOptions options)
    {
        _chat = chat;
        _options = options;
        Endpoint = options.EndpointUri;
        Model = options.Model!;
    }

    public Uri Endpoint { get; }

    public string Model { get; }

    /// <summary>設定と環境変数から作る。</summary>
    /// <param name="environment">環境変数を読む関数（テストで差し替える）。</param>
    /// <exception cref="LlmEndpointNotAllowedException">接続先が許可リストに無い。</exception>
    /// <exception cref="InvalidOperationException">API キーの環境変数が未設定。</exception>
    public static OpenAiLlmClient Create(LlmOptions options, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!options.IsAllowed(options.EndpointUri))
        {
            throw new LlmEndpointNotAllowedException($"LLM の接続先 {options.EndpointUri.Host} は許可リスト（JinJi:Llm:AllowedEndpoints）にありません。");
        }

        environment ??= Environment.GetEnvironmentVariable;
        var key = environment(options.ApiKeyEnvironmentVariable);
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException($"LLM の API キーの環境変数 {options.ApiKeyEnvironmentVariable} が未設定です。");
        }

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = options.EndpointUri,
            NetworkTimeout = options.Timeout,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            UserAgentApplicationId = "jinji",
        };
        return new OpenAiLlmClient(new ChatClient(options.Model, new ApiKeyCredential(key), clientOptions), options);
    }

    public async Task<LlmCompletion> CompleteAsync(LlmPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var completionOptions = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(prompt.SchemaName, BinaryData.FromString(prompt.Schema), jsonSchemaIsStrict: true),
            MaxOutputTokenCount = _options.MaxOutputTokens,
        };
        if (_options.Temperature is { } temperature)
        {
            completionOptions.Temperature = temperature;
        }

        ChatMessage[] messages = [new SystemChatMessage(prompt.System), new UserChatMessage(prompt.User)];
        try
        {
            ClientResult<ChatCompletion> result = await _chat.CompleteChatAsync(messages, completionOptions, cancellationToken);
            var completion = result.Value;
            var text = completion.Content.Count > 0 ? completion.Content[0].Text : string.Empty;
            return new LlmCompletion(text, completion.Model ?? Model, completion.Usage?.InputTokenCount ?? 0, completion.Usage?.OutputTokenCount ?? 0);
        }
        catch (ClientResultException ex) when (ex.Status is 408 or 409 or 429 or >= 500 || ex.Status == 0)
        {
            // 0 は応答が無い（通信の失敗）。メッセージに要求の中身・キーは含めない
            throw new LlmTransientException($"LLM の呼び出しが一時的に失敗しました（HTTP {ex.Status}）。", ex);
        }
        catch (ClientResultException ex)
        {
            throw new LlmException($"LLM の呼び出しが失敗しました（HTTP {ex.Status}）。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmTransientException("LLM への通信が失敗しました。", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmTransientException("LLM の呼び出しがタイムアウトしました。", ex);
        }
    }
}
