using JinJi.Application.Groups;
using JinJi.Domain.Organization;
using JinJi.Policy;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>グループ（org_group・org_group_version・org_group_member。0022）の読み書き。閲覧者を RLS に渡す。</summary>
/// <remarks>
/// 版とメンバーは二つの時間軸で持ち、変更は「その日に有効な行の記録期間を閉じ、前の部分と新しい部分の行を追加する」で行う。
/// DB が拒否した書き込み（RLS・トリガーの insufficient_privilege）は <see cref="GroupException"/>（Forbidden）にする。
/// </remarks>
public sealed class NpgsqlGroupStore : IGroupStore
{
    private const string MemberOverlap = "org_group_member_no_overlap";

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlGroupStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<GroupRecord>> ListAsync(ViewerContext viewer, GroupQuery query, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(query);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(
            GroupSelect +
            """
              AND ($2::text IS NULL OR v.kind = $2)
              AND ($3::uuid IS NULL OR EXISTS (SELECT 1 FROM org_group_member m WHERE m.group_id = g.id AND m.person_id = $3
                                                 AND upper_inf(m.recorded_period) AND m.valid_period @> $1::date))
              AND ($4::uuid IS NULL OR v.owner_org_unit_id = $4)
              AND (NOT $5 OR g.managed)
            ORDER BY v.name, g.id
            LIMIT $6
            """);
        command.Parameters.Add(P(date));
        command.Parameters.Add(P(query.Kind is { } kind ? GroupCodes.Of(kind) : null, NpgsqlDbType.Text));
        command.Parameters.Add(P(query.PersonId, NpgsqlDbType.Uuid));
        command.Parameters.Add(P(query.OwnerOrgUnitId, NpgsqlDbType.Uuid));
        command.Parameters.Add(P(query.ManagedOnly));
        command.Parameters.Add(P(Math.Clamp(query.Limit, 1, 1000)));
        var groups = await ReadGroupsAsync(command, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return groups;
    }

    public async Task<GroupDetail?> FindAsync(ViewerContext viewer, Guid id, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        GroupRecord? group;
        await using (var command = tx.CreateCommand(GroupSelect + " AND g.id = $2"))
        {
            command.Parameters.Add(P(date));
            command.Parameters.Add(P(id));
            group = (await ReadGroupsAsync(command, cancellationToken)).SingleOrDefault();
        }

        if (group is null)
        {
            await tx.CommitAsync(cancellationToken);
            return null;
        }

        var members = new List<GroupMemberRecord>();
        await using (var command = tx.CreateCommand(
            """
            SELECT person_id, role, lower(valid_period), upper(valid_period), added_by_person_id FROM org_group_member
            WHERE group_id = $1 AND upper_inf(recorded_period) AND valid_period @> $2::date
            ORDER BY CASE role WHEN 'owner' THEN 0 WHEN 'lead' THEN 1 ELSE 2 END, person_id
            """))
        {
            command.Parameters.Add(P(id));
            command.Parameters.Add(P(date));
            await using var r = await command.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                members.Add(new GroupMemberRecord(
                    r.GetGuid(0),
                    GroupCodes.ParseRole(r.GetString(1)),
                    r.GetFieldValue<DateOnly>(2),
                    r.IsDBNull(3) ? null : r.GetFieldValue<DateOnly>(3),
                    r.GetGuid(4)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return new GroupDetail(group, members);
    }

    public Task CreateAsync(ViewerContext viewer, Guid id, GroupCreateInput input, DateOnly from, CancellationToken cancellationToken) =>
        WriteAsync(viewer, cancellationToken, async tx =>
        {
            await ExecuteAsync(
                tx,
                "INSERT INTO org_group (id, visibility, created_by_person_id) VALUES ($1, $2, jinji_viewer_person_id())",
                cancellationToken,
                P(id),
                P(GroupCodes.Of(input.Visibility)));
            await ExecuteAsync(
                tx,
                "INSERT INTO org_group_version (id, group_id, name, kind, owner_org_unit_id, valid_period) VALUES ($1, $2, $3, $4, $5, daterange($6, NULL))",
                cancellationToken,
                P(Guid.CreateVersion7()),
                P(id),
                P(input.Name),
                P(GroupCodes.Of(input.Kind)),
                P(input.OwnerOrgUnitId, NpgsqlDbType.Uuid),
                P(from));
            await ExecuteAsync(
                tx,
                """
                INSERT INTO org_group_member (id, group_id, person_id, role, added_by_person_id, valid_period)
                VALUES ($1, $2, jinji_viewer_person_id(), 'owner', jinji_viewer_person_id(), daterange($3, NULL))
                """,
                cancellationToken,
                P(Guid.CreateVersion7()),
                P(id),
                P(from));
            return true;
        });

    public Task<bool> ReviseAsync(ViewerContext viewer, Guid id, GroupUpdateInput input, DateOnly date, CancellationToken cancellationToken) =>
        WriteAsync(viewer, cancellationToken, tx => SplitAsync(
            tx,
            "org_group_version",
            "group_id = $1",
            [P(id)],
            date,
            new Dictionary<string, string>
            {
                ["name"] = "coalesce($5, name)",
                ["kind"] = "coalesce($6, kind)",
                ["owner_org_unit_id"] = "CASE WHEN $8 THEN NULL ELSE coalesce($7, owner_org_unit_id) END",
            },
            [P(input.Name, NpgsqlDbType.Text), P(input.Kind is { } k ? GroupCodes.Of(k) : null, NpgsqlDbType.Text), P(input.OwnerOrgUnitId, NpgsqlDbType.Uuid), P(input.ClearOwnerOrgUnit)],
            cancellationToken));

    public Task<bool> CloseAsync(ViewerContext viewer, Guid id, DateOnly date, CancellationToken cancellationToken) =>
        WriteAsync(viewer, cancellationToken, async tx =>
        {
            if (!await EndAsync(tx, "org_group_version", "group_id = $1", [P(id)], date, cancellationToken))
            {
                return false;
            }

            // その日以後も続くメンバーの期間を前日までにする（その日から始まる予定の行は取り消す）
            var people = new List<Guid>();
            await using (var command = tx.CreateCommand(
                "SELECT DISTINCT person_id FROM org_group_member WHERE group_id = $1 AND upper_inf(recorded_period) AND (upper_inf(valid_period) OR upper(valid_period) > $2::date)"))
            {
                command.Parameters.Add(P(id));
                command.Parameters.Add(P(date));
                await using var r = await command.ExecuteReaderAsync(cancellationToken);
                while (await r.ReadAsync(cancellationToken))
                {
                    people.Add(r.GetGuid(0));
                }
            }

            foreach (var person in people)
            {
                await ExecuteAsync(
                    tx,
                    """
                    UPDATE org_group_member SET recorded_period = tstzrange(lower(recorded_period), now())
                    WHERE group_id = $1 AND person_id = $2 AND upper_inf(recorded_period) AND lower(valid_period) >= $3::date
                    """,
                    cancellationToken,
                    P(id),
                    P(person),
                    P(date));
                await EndAsync(tx, "org_group_member", "group_id = $1 AND person_id = $2", [P(id), P(person)], date, cancellationToken);
            }

            return true;
        });

    public Task<bool> SetSettingsAsync(ViewerContext viewer, Guid id, GroupVisibility? visibility, bool? managed, CancellationToken cancellationToken) =>
        WriteAsync(viewer, cancellationToken, async tx => await ExecuteAsync(
            tx,
            "UPDATE org_group SET visibility = coalesce($2, visibility), managed = coalesce($3, managed) WHERE id = $1",
            cancellationToken,
            P(id),
            P(visibility is { } v ? GroupCodes.Of(v) : null, NpgsqlDbType.Text),
            P(managed, NpgsqlDbType.Boolean)) == 1);

    public Task<bool> AddMemberAsync(ViewerContext viewer, Guid id, Guid personId, GroupRole role, DateOnly from, DateOnly? until, CancellationToken cancellationToken) =>
        WriteAsync(viewer, cancellationToken, async tx =>
        {
            try
            {
                await ExecuteAsync(
                    tx,
                    """
                    INSERT INTO org_group_member (id, group_id, person_id, role, added_by_person_id, valid_period)
                    VALUES ($1, $2, $3, $4, jinji_viewer_person_id(), daterange($5, $6))
                    """,
                    cancellationToken,
                    P(Guid.CreateVersion7()),
                    P(id),
                    P(personId),
                    P(GroupCodes.Of(role)),
                    P(from),
                    P(until, NpgsqlDbType.Date));
                return true;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation && ex.ConstraintName == MemberOverlap)
            {
                return false;
            }
        });

    public Task<bool> ChangeRoleAsync(ViewerContext viewer, Guid id, Guid personId, GroupRole role, DateOnly date, CancellationToken cancellationToken) =>
        WriteAsync(viewer, cancellationToken, tx => SplitAsync(
            tx,
            "org_group_member",
            "group_id = $1 AND person_id = $2",
            [P(id), P(personId)],
            date,
            new Dictionary<string, string> { ["role"] = "$5", ["added_by_person_id"] = "jinji_viewer_person_id()" },
            [P(GroupCodes.Of(role))],
            cancellationToken));

    public Task<bool> EndMemberAsync(ViewerContext viewer, Guid id, Guid personId, DateOnly date, CancellationToken cancellationToken) =>
        WriteAsync(viewer, cancellationToken, tx => EndAsync(tx, "org_group_member", "group_id = $1 AND person_id = $2", [P(id), P(personId)], date, cancellationToken));

    // -----------------------------------------------------------------------
    // 二つの時間軸の行の書き換え
    // -----------------------------------------------------------------------

    /// <summary>写すときの列（id・valid_period・recorded_period を除く）。</summary>
    private static readonly Dictionary<string, string[]> CopiedColumns = new(StringComparer.Ordinal)
    {
        ["org_group_version"] = ["group_id", "name", "kind", "owner_org_unit_id"],
        ["org_group_member"] = ["group_id", "person_id", "role", "added_by_person_id"],
    };

    /// <summary>
    /// その日に有効な行を、その日から変える: 記録期間を閉じ、[始まり, その日) を元の値で、[その日, 終わり) を変えた値で追加する。
    /// <paramref name="overrides"/> は列 → 式（引数は $5 から <paramref name="values"/> の順）。
    /// </summary>
    private static async Task<bool> SplitAsync(
        ViewerTransaction tx,
        string table,
        string where,
        NpgsqlParameter[] keys,
        DateOnly date,
        IReadOnlyDictionary<string, string> overrides,
        NpgsqlParameter[] values,
        CancellationToken cancellationToken)
    {
        if (await CloseRowAsync(tx, table, where, keys, date, cancellationToken) is not { } row)
        {
            return false;
        }

        if (row.From < date)
        {
            await CopyAsync(tx, table, row.Id, row.From, date, new Dictionary<string, string>(), [], cancellationToken);
        }

        await CopyAsync(tx, table, row.Id, date, row.Until, overrides, values, cancellationToken);
        return true;
    }

    /// <summary>その日に有効な行の期間を前日までにする（その日に始まる行なら取り消す）。</summary>
    private static async Task<bool> EndAsync(ViewerTransaction tx, string table, string where, NpgsqlParameter[] keys, DateOnly date, CancellationToken cancellationToken)
    {
        if (await CloseRowAsync(tx, table, where, keys, date, cancellationToken) is not { } row)
        {
            return false;
        }

        if (row.From < date)
        {
            await CopyAsync(tx, table, row.Id, row.From, date, new Dictionary<string, string>(), [], cancellationToken);
        }

        return true;
    }

    private static async Task<(Guid Id, DateOnly From, DateOnly? Until)?> CloseRowAsync(
        ViewerTransaction tx,
        string table,
        string where,
        NpgsqlParameter[] keys,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        (Guid Id, DateOnly From, DateOnly? Until)? row = null;
        await using (var select = tx.CreateCommand(
            $"SELECT id, lower(valid_period), upper(valid_period) FROM {table} WHERE {where} AND upper_inf(recorded_period) AND valid_period @> ${keys.Length + 1}::date FOR UPDATE"))
        {
            select.Parameters.AddRange(keys.Select(k => k.Clone()).ToArray());
            select.Parameters.Add(P(date));
            await using var r = await select.ExecuteReaderAsync(cancellationToken);
            if (await r.ReadAsync(cancellationToken))
            {
                row = (r.GetGuid(0), r.GetFieldValue<DateOnly>(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateOnly>(2));
            }
        }

        if (row is not { } found)
        {
            return null;
        }

        await ExecuteAsync(tx, $"UPDATE {table} SET recorded_period = tstzrange(lower(recorded_period), now()) WHERE id = $1", cancellationToken, P(found.Id));
        return found;
    }

    /// <summary>行を写して追加する（新しい ID・期間。<paramref name="overrides"/> の列は式の値にする。式の引数は $5 から）。</summary>
    private static Task<int> CopyAsync(
        ViewerTransaction tx,
        string table,
        Guid source,
        DateOnly from,
        DateOnly? until,
        IReadOnlyDictionary<string, string> overrides,
        NpgsqlParameter[] values,
        CancellationToken cancellationToken)
    {
        var columns = CopiedColumns[table];
        var expressions = columns.Select(c => overrides.TryGetValue(c, out var e) ? e : c);
        var parameters = new List<NpgsqlParameter> { P(Guid.CreateVersion7()), P(from), P(until, NpgsqlDbType.Date), P(source) };
        parameters.AddRange(values.Select(v => v.Clone()));
        return ExecuteAsync(
            tx,
            $"INSERT INTO {table} (id, {string.Join(", ", columns)}, valid_period) SELECT $1, {string.Join(", ", expressions)}, daterange($2, $3) FROM {table} WHERE id = $4",
            cancellationToken,
            [.. parameters]);
    }

    // -----------------------------------------------------------------------
    // 共通
    // -----------------------------------------------------------------------

    private const string GroupSelect =
        """
        SELECT g.id, v.name, v.kind, v.owner_org_unit_id, g.visibility, g.managed, lower(v.valid_period), upper(v.valid_period),
               g.created_by_person_id, g.created_at,
               (SELECT count(*) FROM org_group_member m WHERE m.group_id = g.id AND upper_inf(m.recorded_period) AND m.valid_period @> $1::date)
        FROM org_group g JOIN org_group_version v ON v.group_id = g.id
        WHERE upper_inf(v.recorded_period) AND v.valid_period @> $1::date
        """;

    private static async Task<List<GroupRecord>> ReadGroupsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var groups = new List<GroupRecord>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            groups.Add(new GroupRecord(
                r.GetGuid(0),
                r.GetString(1),
                GroupCodes.ParseKind(r.GetString(2)),
                r.IsDBNull(3) ? null : r.GetGuid(3),
                r.GetString(4) == "members" ? GroupVisibility.Members : GroupVisibility.All,
                r.GetBoolean(5),
                r.GetFieldValue<DateOnly>(6),
                r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7),
                r.GetGuid(8),
                r.GetFieldValue<DateTimeOffset>(9),
                (int)r.GetInt64(10)));
        }

        return groups;
    }

    /// <summary>1 トランザクションで書く。DB の拒否は Forbidden、制約違反は Conflict の <see cref="GroupException"/> にする。</summary>
    private async Task<T> WriteAsync<T>(ViewerContext viewer, CancellationToken cancellationToken, Func<ViewerTransaction, Task<T>> write)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        T result;
        try
        {
            result = await write(tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new GroupException(GroupError.Forbidden, "この変更は許されていません: " + ex.MessageText);
        }
        catch (PostgresException ex) when (ex.SqlState.StartsWith("23", StringComparison.Ordinal))
        {
            throw new GroupException(GroupError.Conflict, $"他の変更と衝突しました（{ex.ConstraintName ?? ex.SqlState}）。");
        }

        if (result is false)
        {
            await tx.Transaction.RollbackAsync(cancellationToken);
            return result;
        }

        await tx.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<int> ExecuteAsync(ViewerTransaction tx, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = tx.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter P(object value) => new() { Value = value };

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
