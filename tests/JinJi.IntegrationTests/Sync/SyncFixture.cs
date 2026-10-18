using System.Text;
using System.Text.Json;
using JinJi.Application.Sync;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Sync;

namespace JinJi.IntegrationTests.Sync;

/// <summary>
/// 同期計画の単体テスト用のスナップショット（DB を使わない）と、Entra のユーザー一覧（Graph の JSON）の組み立て。
/// </summary>
/// <remarks>
/// <code>
/// 法人 A（テスト株式会社）
///   D1  第一本部（division）       長: boss
///     DP1 第一営業部（department） 長: boss
///       S1 営業一課（section）     長: chief      chief（E0002 課長）, member（E0003）
///       S2 営業二課（section、長なし）           solo（E0004）
/// 法人 B（出向先株式会社）
///   B1  出向先部（department）     長: hostboss
/// </code>
/// </remarks>
internal sealed class SyncFixture
{
    public static readonly DateOnly Today = new(2026, 10, 1);

    private readonly Dictionary<string, Guid> _ids = new(StringComparer.Ordinal);
    private readonly List<OrgUnit> _units = [];
    private readonly List<PersonRecord> _people = [];
    private readonly List<Assignment> _assignments = [];
    private readonly List<ExternalIdentityLink> _links = [];
    private readonly Dictionary<string, Site> _sites = new(StringComparer.Ordinal);
    private readonly List<Dictionary<string, object?>> _users = [];

    public SyncFixture()
    {
        EntityA = new LegalEntity(Id("A"), "A", "テスト株式会社");
        EntityB = new LegalEntity(Id("B"), "B", "出向先株式会社");
        Unit("D1", "A", "第一本部", "division", null, "boss");
        Unit("DP1", "A", "第一営業部", "department", "D1", "boss");
        Unit("S1", "A", "営業一課", "section", "DP1", "chief");
        Unit("S2", "A", "営業二課", "section", "DP1", null);
        Unit("B1", "B", "出向先部", "department", null, "hostboss");
        Person("boss", "E0001", "山田", "太郎", "DP1", "部長");
        Person("chief", "E0002", "髙橋", "一郎", "S1", "課長");
        Person("member", "E0003", "鈴木", "次郎", "S1", null, location: "東京本社");
        Person("solo", "E0004", "佐藤", "花子", "S2", null);
        Person("hostboss", "B0001", "出向", "受入", "B1", "部長", entity: "B");
    }

    public LegalEntity EntityA { get; }

    public LegalEntity EntityB { get; }

    public Guid Id(string key)
    {
        if (!_ids.TryGetValue(key, out var id))
        {
            _ids[key] = id = EntityId.New();
        }

        return id;
    }

    /// <summary>Entra のオブジェクト ID（キーごとに固定）。</summary>
    public string Object(string key) => Id("obj:" + key).ToString("D");

    public void Unit(string key, string entity, string name, string? level, string? parent, string? head) =>
        _units.Add(new OrgUnit(Id(key), Id(entity), key, name, null, level, parent is null ? null : Id(parent), head is null ? null : Id(head)));

    public void Person(
        string key,
        string number,
        string family,
        string? given,
        string unit,
        string? title,
        string? location = null,
        string? email = null,
        DateOnly? terminated = null,
        DateOnly? suspendedFrom = null,
        string entity = "A")
    {
        var version = new PersonVersion
        {
            VersionId = Id("v:" + key),
            ValidFrom = new DateOnly(2020, 4, 1),
            FamilyName = family,
            GivenName = given,
            DisplayName = $"{family} {given}".Trim(),
            WorkEmail = email,
            SiteId = location is null ? null : SiteNamed(location, entity).Id,
            EmploymentType = "regular",
            HireDate = new DateOnly(2020, 4, 1),
            TerminationDate = terminated,
        };
        _people.RemoveAll(p => p.Id == Id(key));
        _people.Add(new PersonRecord
        {
            Id = Id(key),
            LegalEntityId = Id(entity),
            EmployeeNumber = number,
            Current = version,
            History = [version],
            Suspension = suspendedFrom is { } from ? new SuspensionPeriod(from, null) : null,
            Actions = [],
        });
        _assignments.RemoveAll(a => a.PersonId == Id(key));
        if (terminated is null || terminated >= Today)
        {
            _assignments.Add(new Assignment(Id(key), Id(unit), AssignmentType.Primary, title, SecondmentType.None, new DateOnly(2020, 4, 1), null));
        }
    }

    /// <summary>出向（送り出し）の兼務を足す。</summary>
    public void Secondment(string key, string unit, string? title) =>
        _assignments.Add(new Assignment(Id(key), Id(unit), AssignmentType.Concurrent, title, SecondmentType.Outbound, new DateOnly(2026, 4, 1), null));

    /// <summary>外部 ID の対応と、Entra のユーザー（jin-ji と同じ値。上書きは <paramref name="overrides"/>）を足す。</summary>
    public string Account(string person, Dictionary<string, object?>? overrides = null, bool link = true, string? objectKey = null)
    {
        var objectId = Object(objectKey ?? person);
        var record = _people.Single(p => p.Id == Id(person));
        var snapshot = Snapshot();
        var desired = Desired(snapshot, record);
        var user = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = objectId,
            ["userPrincipalName"] = $"{record.EmployeeNumber.ToLowerInvariant()}@example.co.jp",
            ["mail"] = $"{record.EmployeeNumber.ToLowerInvariant()}@example.co.jp",
            ["accountEnabled"] = !record.IsTerminatedOn(Today) && !record.IsSuspended,
        };
        foreach (var (field, value) in desired)
        {
            user[field] = value;
        }

        foreach (var (field, value) in overrides ?? [])
        {
            user[field] = value;
        }

        _users.Add(user);
        if (link)
        {
            _links.Add(new ExternalIdentityLink(Id(person), objectId));
        }

        return objectId;
    }

    /// <summary>人ではない・対応付けの無いアカウント。</summary>
    public string Unlinked(string key, Dictionary<string, object?> values)
    {
        var user = new Dictionary<string, object?>(values, StringComparer.Ordinal) { ["id"] = Object(key) };
        _users.Add(user);
        return Object(key);
    }

    public OrgSnapshot Snapshot() => OrgSnapshot.Create(
        Today,
        [EntityA, EntityB],
        _units,
        _people,
        _assignments,
        [new OrgLevelScheme(null, [new OrgLevel("division", "本部", 1), new OrgLevel("department", "部", 2), new OrgLevel("section", "課", 3)])],
        _sites.Values);

    /// <summary>名前の拠点（人の拠点の上書きに使う。無ければ作る）。</summary>
    private Site SiteNamed(string name, string entity) =>
        _sites.TryGetValue(name, out var site)
            ? site
            : _sites[name] = new Site(Id("site:" + name), Id(entity), $"S{_sites.Count + 1:D2}", name, SiteType.Other, null, null);

    public IReadOnlyList<ExternalIdentityLink> Links => _links;

    public byte[] UsersJson() => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["value"] = _users });

    public IReadOnlyList<DirectoryUser> Users() => GraphDirectoryUsers.Parse(UsersJson());

    /// <param name="accountRest">アカウントの無い在籍者に、jin-ji と同じ値のアカウントを足してから計画する（作成の推奨を出さない）。</param>
    public SyncPlanDraft Plan(SyncSettings? settings = null, bool accountRest = true)
    {
        if (accountRest)
        {
            foreach (var person in _people.Where(p => !p.IsTerminatedOn(Today) && _links.All(l => l.PersonId != p.Id)).ToList())
            {
                Account(_ids.Single(kv => kv.Value == person.Id).Key);
            }
        }

        return new EntraSyncPlanner().Plan(new SyncPlanInput(Snapshot(), _links, Users(), settings ?? SyncSettings.Default, Today));
    }

    /// <summary>既定の設定でのあるべき値（Entra のユーザーを差分なしで作るため）。</summary>
    private Dictionary<string, object?> Desired(OrgSnapshot snapshot, PersonRecord person)
    {
        var assignment = snapshot.AssignmentsOf(person.Id).OrderBy(a => a.Secondment == SecondmentType.Outbound ? 0 : 1).FirstOrDefault();
        var unit = assignment is null ? null : snapshot.FindUnit(assignment.OrgUnitId);
        var entity = snapshot.FindLegalEntity(unit?.LegalEntityId ?? person.LegalEntityId)!;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["displayName"] = person.Current.DisplayName,
            ["givenName"] = person.Current.GivenName,
            ["surname"] = person.Current.FamilyName,
            ["department"] = unit?.Name,
            ["jobTitle"] = assignment?.PositionTitle,
            ["companyName"] = entity.Name,
            ["officeLocation"] = snapshot.SiteOf(person.Id, person.Current)?.Site.Name,
            ["employeeId"] = person.EmployeeNumber,
        };
    }

    public static SyncSettings Settings(string json) => SyncSettings.Parse(Encoding.UTF8.GetBytes(json));
}
