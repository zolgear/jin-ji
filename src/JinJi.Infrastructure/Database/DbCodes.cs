using JinJi.Application;
using JinJi.Application.Audit;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Infrastructure.Database;

/// <summary>DB のコード値（CHECK 制約の値）とドメインの列挙の対応。</summary>
internal static class DbCodes
{
    private static readonly Dictionary<string, PersonnelActionType> ActionTypes = new(StringComparer.Ordinal)
    {
        ["hire"] = PersonnelActionType.Hire,
        ["termination"] = PersonnelActionType.Termination,
        ["transfer"] = PersonnelActionType.Transfer,
        ["position_appointment"] = PersonnelActionType.PositionAppointment,
        ["position_dismissal"] = PersonnelActionType.PositionDismissal,
        ["concurrent_start"] = PersonnelActionType.ConcurrentStart,
        ["concurrent_end"] = PersonnelActionType.ConcurrentEnd,
        ["secondment_start"] = PersonnelActionType.SecondmentStart,
        ["secondment_end"] = PersonnelActionType.SecondmentEnd,
        ["suspension_start"] = PersonnelActionType.SuspensionStart,
        ["suspension_end"] = PersonnelActionType.SuspensionEnd,
    };

    public static PersonnelActionType ParseActionType(string code) => Lookup(ActionTypes, code, "発令の種類");

    public static AssignmentType ParseAssignmentType(string code) => code switch
    {
        "primary" => AssignmentType.Primary,
        "concurrent" => AssignmentType.Concurrent,
        _ => throw Unknown("主務/兼務", code),
    };

    public static SecondmentType ParseSecondmentType(string code) => code switch
    {
        "none" => SecondmentType.None,
        "outbound" => SecondmentType.Outbound,
        "inbound" => SecondmentType.Inbound,
        _ => throw Unknown("出向の区別", code),
    };

    public static PolicyTargetKind ParseTargetKind(string code) => code switch
    {
        "all" => PolicyTargetKind.All,
        "org_unit" => PolicyTargetKind.OrgUnit,
        "person" => PolicyTargetKind.Person,
        _ => throw Unknown("ポリシーの対象", code),
    };

    public static string ToCode(PolicyTargetKind kind) => kind switch
    {
        PolicyTargetKind.All => "all",
        PolicyTargetKind.OrgUnit => "org_unit",
        PolicyTargetKind.Person => "person",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static OrgLevelSchemeStatus ParseLevelSchemeStatus(string code) => code switch
    {
        "proposed" => OrgLevelSchemeStatus.Proposed,
        "approved" => OrgLevelSchemeStatus.Approved,
        "rejected" => OrgLevelSchemeStatus.Rejected,
        "superseded" => OrgLevelSchemeStatus.Superseded,
        _ => throw Unknown("段の定義の状態", code),
    };

    public static PolicyRuleStatus ParseRuleStatus(string code) => code switch
    {
        "proposed" => PolicyRuleStatus.Proposed,
        "approved" => PolicyRuleStatus.Approved,
        "rejected" => PolicyRuleStatus.Rejected,
        "retired" => PolicyRuleStatus.Retired,
        _ => throw Unknown("ポリシーの状態", code),
    };

    public static string ToCode(ActorType actor) => actor switch
    {
        ActorType.User => "user",
        ActorType.Service => "service",
        ActorType.Agent => "agent",
        ActorType.System => "system",
        ActorType.BreakGlass => "break_glass",
        _ => throw new ArgumentOutOfRangeException(nameof(actor)),
    };

    public static string ToCode(Channel channel) => channel switch
    {
        Channel.Ui => "ui",
        Channel.Api => "api",
        Channel.Mcp => "mcp",
        Channel.Worker => "worker",
        Channel.Cli => "cli",
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    public static string ToCode(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => "success",
        AuditOutcome.Denied => "denied",
        AuditOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static T Lookup<T>(Dictionary<string, T> map, string code, string what) =>
        map.TryGetValue(code, out var value) ? value : throw Unknown(what, code);

    private static InvalidOperationException Unknown(string what, string code) => new($"DB に未知の{what}があります: {code}");
}
