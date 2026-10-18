namespace JinJi.Infrastructure.Migrations;

/// <summary>
/// マイグレーションを適用できない状態（チェックサム不一致、順序違反、SQL の失敗など）。
/// </summary>
public sealed class MigrationException : Exception
{
    public MigrationException()
    {
    }

    public MigrationException(string message)
        : base(message)
    {
    }

    public MigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
