namespace JinJi.Host;

/// <summary>プロセスの役割（アーキテクチャ概要 §2「プロセス構成」）。</summary>
[Flags]
internal enum HostRole
{
    None = 0,
    Api = 1,
    Mcp = 2,
    Worker = 4,
    All = Api | Mcp | Worker,
}

internal static class HostRoles
{
    /// <summary>コマンドライン（--role）と環境変数（JINJI_ROLE）から役割を決める。コマンドラインが優先。</summary>
    public static HostRole Resolve(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables("JINJI_")
            .AddCommandLine(args)
            .Build();
        return Parse(configuration["role"]);
    }

    public static HostRole Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return HostRole.All;
        }

        return value.Trim() switch
        {
            var v when v.Equals("all", StringComparison.OrdinalIgnoreCase) => HostRole.All,
            var v when v.Equals("api", StringComparison.OrdinalIgnoreCase) => HostRole.Api,
            var v when v.Equals("mcp", StringComparison.OrdinalIgnoreCase) => HostRole.Mcp,
            var v when v.Equals("worker", StringComparison.OrdinalIgnoreCase) => HostRole.Worker,
            _ => throw new ArgumentException($"不明な役割です: {value}（all / api / mcp / worker のいずれか）", nameof(value)),
        };
    }
}
