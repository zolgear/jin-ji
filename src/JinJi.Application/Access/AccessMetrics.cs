using System.Diagnostics.Metrics;

namespace JinJi.Application.Access;

/// <summary>
/// API・MCP のメトリクス（アーキテクチャ概要 §7）。System.Diagnostics.Metrics で計装し、公開形式（Prometheus のテキスト形式・OTLP）は Host が決める。
/// </summary>
/// <remarks>
/// ラベルのクライアントは登録済みのクライアント ID（未認証は <c>anonymous</c>、未登録は <c>unregistered</c>）に限り、値の種類が増え続けないようにする。
/// 人・リクエストを特定する値（person ID・パス・クエリ）はラベルにしない。
/// </remarks>
public sealed class AccessMetrics
{
    /// <summary>Meter の名前。</summary>
    public const string MeterName = "JinJi.Access";

    /// <summary>未認証の要求のクライアントのラベル。</summary>
    public const string AnonymousClient = "anonymous";

    /// <summary>登録されていないクライアントのラベル。</summary>
    public const string UnregisteredClient = "unregistered";

    private readonly Counter<long> _requests;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _peopleReturned;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _abuse;

    public AccessMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        Meter = meterFactory.Create(MeterName);
        _requests = Meter.CreateCounter<long>("jinji.requests", "{request}", "リクエスト数（クライアント・経路・エンドポイント種別・状態コード別）");
        _rejected = Meter.CreateCounter<long>("jinji.requests.rejected", "{request}", "拒否したリクエスト数（理由別）");
        _peopleReturned = Meter.CreateCounter<long>("jinji.people.returned", "{person}", "返した人数");
        _duration = Meter.CreateHistogram<double>("jinji.request.duration", "s", "レイテンシ");
        _abuse = Meter.CreateCounter<long>("jinji.abuse.detections", "{detection}", "不正検知の発火数（規則・対応別）");
    }

    /// <summary>計装の Meter（公開側が購読する）。</summary>
    public Meter Meter { get; }

    /// <summary>1 リクエストを記録する。</summary>
    public void RecordRequest(string client, string channel, string kind, int statusCode, TimeSpan elapsed, string? rejectReason, int peopleReturned)
    {
        var status = statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _requests.Add(1, new("client", client), new("channel", channel), new("kind", kind), new("status", status));
        _duration.Record(elapsed.TotalSeconds, new("client", client), new("channel", channel), new("kind", kind));
        if (rejectReason is not null)
        {
            _rejected.Add(1, new("client", client), new("channel", channel), new("reason", rejectReason));
        }

        if (peopleReturned > 0)
        {
            _peopleReturned.Add(peopleReturned, new("client", client), new("channel", channel));
        }
    }

    /// <summary>不正検知の発火・解除を記録する。</summary>
    public void RecordAbuse(string client, string rule, string action) =>
        _abuse.Add(1, new("client", client), new("rule", rule), new("action", action));
}
