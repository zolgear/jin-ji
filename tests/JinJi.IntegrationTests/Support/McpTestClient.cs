using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JinJi.IntegrationTests.Support;

/// <summary>MCP の応答（HTTP の状態コード・JSON-RPC のメッセージ）。</summary>
internal sealed record McpReply(HttpStatusCode Status, JsonElement? Message, HttpResponseMessage Raw)
{
    /// <summary>JSON-RPC の result。</summary>
    public JsonElement Result => Message?.GetProperty("result") ?? throw new InvalidOperationException($"JSON-RPC の応答がありません（{Status}）。");

    /// <summary>JSON-RPC の error（無ければ null）。</summary>
    public JsonElement? Error => Message is { } m && m.TryGetProperty("error", out var e) ? e : null;

    /// <summary>tools/call の結果が誤り（isError）か。</summary>
    public bool IsToolError => Result.TryGetProperty("isError", out var e) && e.GetBoolean();

    /// <summary>tools/call の structuredContent。</summary>
    public JsonElement Structured => Result.GetProperty("structuredContent");

    /// <summary>tools/call のテキスト。</summary>
    public string Text => Result.GetProperty("content")[0].GetProperty("text").GetString()!;

    /// <summary>tools/call の誤りのコード（structuredContent の error.code）。</summary>
    public string? ToolErrorCode => IsToolError ? Structured.GetProperty("error").GetProperty("code").GetString() : null;
}

/// <summary>
/// MCP（Streamable HTTP）の最小のテスト用クライアント。JSON-RPC の要求を POST し、SSE（text/event-stream）か JSON の応答を読む。
/// </summary>
internal sealed class McpTestClient(int port, string? token)
{
    /// <summary>初期化の取り決めで使うプロトコルの版（initialize の手順を持つ最新の版）。</summary>
    public const string ProtocolVersion = "2025-11-25";

    private int _id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>JSON-RPC の要求を送る。</summary>
    public async Task<McpReply> SendAsync(string method, object? parameters = null, string path = "/mcp", bool notification = false, string? protocolVersion = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (!notification)
        {
            message["id"] = Interlocked.Increment(ref _id);
        }

        if (parameters is not null)
        {
            message["params"] = JsonSerializer.SerializeToNode(parameters);
        }

        return await PostAsync(port, token, message.ToJsonString(), path, protocolVersion);
    }

    /// <summary>initialize → notifications/initialized。</summary>
    public async Task<McpReply> InitializeAsync()
    {
        var reply = await SendAsync("initialize", new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "jinji-tests", version = "1.0.0" },
        });
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        using var initialized = (await SendAsync("notifications/initialized", notification: true, protocolVersion: ProtocolVersion)).Raw;
        Assert.Equal(HttpStatusCode.Accepted, initialized.StatusCode);
        return reply;
    }

    /// <summary>tools/list のツールの名前。</summary>
    public async Task<IReadOnlyList<string>> ListToolNamesAsync()
    {
        var reply = await SendAsync("tools/list", new { }, protocolVersion: ProtocolVersion);
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        return [.. reply.Result.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!)];
    }

    /// <summary>tools/call。</summary>
    public Task<McpReply> CallAsync(string tool, object? arguments = null) =>
        SendAsync("tools/call", new { name = tool, arguments = arguments ?? new { } }, protocolVersion: ProtocolVersion);

    /// <summary>本文をそのまま POST する。</summary>
    public static async Task<McpReply> PostAsync(int port, string? token, string body, string path = "/mcp", string? protocolVersion = null, string accept = "application/json, text/event-stream")
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{port}{path}"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd(accept);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (protocolVersion is not null)
        {
            request.Headers.Add("MCP-Protocol-Version", protocolVersion);
        }

        var response = await http.SendAsync(request, Ct);
        return new McpReply(response.StatusCode, await ReadMessageAsync(response), response);
    }

    /// <summary>応答の JSON-RPC のメッセージ（SSE なら最初の data、JSON ならその本文。本文が無ければ null）。</summary>
    public static async Task<JsonElement?> ReadMessageAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            var data = text.Split('\n')
                .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => l["data:".Length..].Trim())
                .FirstOrDefault(l => l.Length > 0);
            if (data is null)
            {
                return null;
            }

            text = data;
        }

        if (!text.TrimStart().StartsWith('{'))
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
