using JinJi.Application.Sync;
using JinJi.Sync.DirectApply;

namespace JinJi.Host.Evaluation;

/// <summary>
/// メモリ上の Entra ID（評価用。実テナントには接続しない）。入力のユーザー一覧から始め、直接反映の書き込みを受けて値を変える。
/// 反映の後の一覧（<see cref="Users"/>）から計画を作り直し、差分が無くなる（収束する）ことを確かめるのに使う。
/// </summary>
internal sealed class InMemoryDirectory : IDirectoryWriteClient
{
    private readonly Dictionary<string, Dictionary<string, string?>> _users;
    private readonly List<string> _order;

    public InMemoryDirectory(IEnumerable<DirectoryUser> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        var list = users.ToList();
        _order = [.. list.Select(u => u.ObjectId)];
        _users = list.ToDictionary(u => u.ObjectId, u =>
        {
            var values = new Dictionary<string, string?>(u.Values, StringComparer.Ordinal);
            values.TryAdd(EntraFields.Manager, null);
            return values;
        }, StringComparer.Ordinal);
    }

    /// <summary>書き込みの回数（PATCH・PUT）。</summary>
    public int Writes { get; private set; }

    /// <summary>今の値のユーザー一覧（上長を含む = $expand=manager 相当）。</summary>
    public IReadOnlyList<DirectoryUser> Users => [.. _order.Select((id, i) => new DirectoryUser(id, new Dictionary<string, string?>(_users[id], StringComparer.Ordinal), i))];

    /// <summary>Entra 側を計画の後に変える（事前条件の不一致の再現）。</summary>
    public void Set(string objectId, string field, string? value) => _users[objectId][field] = value;

    public Task<IReadOnlyDictionary<string, string?>> GetUserAsync(string objectId, IReadOnlyList<string> properties, CancellationToken cancellationToken)
    {
        var user = Find(objectId);
        IReadOnlyDictionary<string, string?> values = properties.ToDictionary(p => p, user.GetValueOrDefault, StringComparer.Ordinal);
        return Task.FromResult(values);
    }

    public Task<string?> GetManagerIdAsync(string objectId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(objectId).GetValueOrDefault(EntraFields.Manager));

    public Task UpdateUserAsync(string objectId, IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken)
    {
        var user = Find(objectId);
        foreach (var (key, value) in changes)
        {
            user[key] = value switch
            {
                bool b => b ? "true" : "false",
                null => null,
                _ => value.ToString(),
            };
        }

        Writes++;
        return Task.CompletedTask;
    }

    public Task SetManagerAsync(string objectId, string managerObjectId, CancellationToken cancellationToken)
    {
        Find(managerObjectId);
        Find(objectId)[EntraFields.Manager] = managerObjectId;
        Writes++;
        return Task.CompletedTask;
    }

    private Dictionary<string, string?> Find(string objectId) =>
        _users.TryGetValue(objectId, out var user) ? user : throw new GraphRequestException(404, "Request_ResourceNotFound", "ユーザーがありません。");
}
