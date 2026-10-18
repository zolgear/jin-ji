using JinJi.Domain;
using Npgsql;

namespace JinJi.IntegrationTests.Support;

/// <summary>テスト用の最小データ（法人・組織・人）を作る。</summary>
internal static class SampleData
{
    public sealed record Ids(Guid LegalEntity, Guid OrgUnitA, Guid OrgUnitB, Guid Person);

    /// <summary>
    /// 接続のセッション全体に、hr_admin の閲覧者を設定する（RLS は閲覧者が未設定だと何も見せず、書き込ませない）。
    /// </summary>
    /// <remarks>
    /// テスト専用。アプリは必ずトランザクション単位（set_config の第 3 引数 true = SET LOCAL 相当）で設定する
    /// （JinJi.Infrastructure の ViewerTransaction）。セッション単位の設定は接続プールで次の利用者に引き継がれるため、アプリでは使わない。
    /// </remarks>
    public static Task ActAsHrAdminAsync(NpgsqlConnection connection, CancellationToken cancellationToken) =>
        SetSessionViewerAsync(connection, "system", "hr_admin", null, cancellationToken);

    /// <summary>接続のセッション全体に閲覧者を設定する（テスト専用。<see cref="ActAsHrAdminAsync"/> を参照）。</summary>
    public static async Task SetSessionViewerAsync(
        NpgsqlConnection connection,
        string kind,
        string roles,
        Guid? personId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT set_config('jinji.viewer_kind', $1, false),
                   set_config('jinji.viewer_roles', $2, false),
                   set_config('jinji.viewer_person_id', $3, false)
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = kind });
        command.Parameters.Add(new NpgsqlParameter { Value = roles });
        command.Parameters.Add(new NpgsqlParameter { Value = personId?.ToString() ?? string.Empty });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>法人 1・組織 2・人 1 を登録する。</summary>
    public static async Task<Ids> InsertBasicsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var ids = new Ids(EntityId.New(), EntityId.New(), EntityId.New(), EntityId.New());

        await ExecuteAsync(connection, "INSERT INTO legal_entity (id, code, name) VALUES ($1, 'C001', 'テスト株式会社')", cancellationToken, ids.LegalEntity);
        await ExecuteAsync(connection, "INSERT INTO org_unit (id, legal_entity_id) VALUES ($1, $2), ($3, $2)", cancellationToken, ids.OrgUnitA, ids.LegalEntity, ids.OrgUnitB);
        await ExecuteAsync(connection, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, 'E0001')", cancellationToken, ids.Person, ids.LegalEntity);
        return ids;
    }

    /// <summary>所属を 1 行追加し、その ID を返す。</summary>
    public static async Task<Guid> InsertAssignmentAsync(
        NpgsqlConnection connection,
        Guid personId,
        Guid orgUnitId,
        string assignmentType,
        string validPeriod,
        CancellationToken cancellationToken)
    {
        var id = EntityId.New();
        await ExecuteAsync(
            connection,
            "INSERT INTO assignment (id, person_id, org_unit_id, assignment_type, valid_period) VALUES ($1, $2, $3, $4, $5::daterange)",
            cancellationToken,
            id,
            personId,
            orgUnitId,
            assignmentType,
            validPeriod);
        return id;
    }

    public static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
