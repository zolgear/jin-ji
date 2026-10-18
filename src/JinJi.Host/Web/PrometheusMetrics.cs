using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using JinJi.Application.Access;
using JinJi.Application.Corrections;
using JinJi.Application.Retention;
using JinJi.Application.Webhooks;

namespace JinJi.Host.Web;

/// <summary>
/// メトリクスを Prometheus のテキスト形式（0.0.4）で公開する小さな出力器。<see cref="AccessMetrics"/> と、同じプロセスにワーカーがあれば
/// <see cref="RetentionMetrics"/>（保持期限の消去）・<see cref="CorrectionMetrics"/>（修正依頼の通知）・<see cref="WebhookMetrics"/>（社内システムへの Webhook）の Meter だけを購読する。
/// </summary>
/// <remarks>
/// <para>
/// OpenTelemetry の Prometheus 出力器（OpenTelemetry.Exporter.Prometheus.AspNetCore）はプレリリース版しか無く、
/// 依存も数系統増えるため使わない（アーキテクチャ概要 §10「既定では Prometheus 形式のエンドポイントのみ」）。
/// 計装は標準の System.Diagnostics.Metrics なので、OTLP で送る必要が出たら OpenTelemetry の出力器を足すだけで済む（計装は変えない）。
/// </para>
/// <list type="bullet">
/// <item>カウンタは合計、ヒストグラムは固定の境界（秒）の累積の度数・合計・件数</item>
/// <item>同じプロセスの別の <see cref="AccessMetrics"/>（テストで複数のホストを立てる場合）の値は混ぜない（Meter の参照で区別する）</item>
/// </list>
/// </remarks>
internal sealed class PrometheusMetrics : IDisposable
{
    /// <summary>レイテンシのヒストグラムの境界（秒）。</summary>
    private static readonly double[] Buckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<Instrument, InstrumentState> _instruments = new();

    public PrometheusMetrics(AccessMetrics metrics, RetentionMetrics? retention = null, CorrectionMetrics? corrections = null, WebhookMetrics? webhooks = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter)
                || (retention is not null && ReferenceEquals(instrument.Meter, retention.Meter))
                || (corrections is not null && ReferenceEquals(instrument.Meter, corrections.Meter))
                || (webhooks is not null && ReferenceEquals(instrument.Meter, webhooks.Meter)))
            {
                _instruments.TryAdd(instrument, new InstrumentState(instrument));
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>テキスト形式で書き出す。</summary>
    public string Render()
    {
        var text = new StringBuilder();
        foreach (var state in _instruments.Values.OrderBy(s => s.Instrument.Name, StringComparer.Ordinal))
        {
            state.Render(text);
        }

        return text.ToString();
    }

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (_instruments.TryGetValue(instrument, out var state))
        {
            state.Record(value, Labels(tags));
        }
    }

    private static string Labels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var pairs = new List<string>(tags.Length);
        foreach (var tag in tags)
        {
            pairs.Add($"{Sanitize(tag.Key)}=\"{Escape(Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty)}\"");
        }

        pairs.Sort(StringComparer.Ordinal);
        return string.Join(',', pairs);
    }

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        return builder.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private sealed class InstrumentState(Instrument instrument)
    {
        private readonly ConcurrentDictionary<string, Series> _series = new(StringComparer.Ordinal);

        public Instrument Instrument { get; } = instrument;

        private bool IsHistogram => Instrument is Histogram<double> or Histogram<long>;

        /// <summary>Prometheus の名前（ドットを _ に、単位が秒なら _seconds、カウンタは _total）。</summary>
        private string Name
        {
            get
            {
                var name = Sanitize(Instrument.Name);
                if (Instrument.Unit == "s")
                {
                    name += "_seconds";
                }

                return IsHistogram ? name : name + "_total";
            }
        }

        public void Record(double value, string labels)
        {
            var series = _series.GetOrAdd(labels, _ => new Series(IsHistogram ? new long[Buckets.Length] : null));
            series.Add(value);
        }

        public void Render(StringBuilder text)
        {
            var name = Name;
            text.Append("# HELP ").Append(name).Append(' ').Append((Instrument.Description ?? string.Empty).Replace("\n", " ", StringComparison.Ordinal)).Append('\n');
            text.Append("# TYPE ").Append(name).Append(IsHistogram ? " histogram" : " counter").Append('\n');
            foreach (var (labels, series) in _series.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                var (sum, count, buckets) = series.Read();
                if (buckets is null)
                {
                    text.Append(name).Append(Braces(labels)).Append(' ').Append(Number(sum)).Append('\n');
                    continue;
                }

                for (var i = 0; i < Buckets.Length; i++)
                {
                    text.Append(name).Append("_bucket").Append(Braces(Join(labels, $"le=\"{Number(Buckets[i])}\""))).Append(' ').Append(buckets[i]).Append('\n');
                }

                text.Append(name).Append("_bucket").Append(Braces(Join(labels, "le=\"+Inf\""))).Append(' ').Append(count).Append('\n');
                text.Append(name).Append("_sum").Append(Braces(labels)).Append(' ').Append(Number(sum)).Append('\n');
                text.Append(name).Append("_count").Append(Braces(labels)).Append(' ').Append(count).Append('\n');
            }
        }

        private static string Braces(string labels) => labels.Length == 0 ? string.Empty : "{" + labels + "}";

        private static string Join(string labels, string extra) => labels.Length == 0 ? extra : labels + "," + extra;
    }

    private sealed class Series(long[]? buckets)
    {
        private readonly Lock _lock = new();
        private double _sum;
        private long _count;

        public void Add(double value)
        {
            lock (_lock)
            {
                _sum += value;
                _count++;
                if (buckets is null)
                {
                    return;
                }

                for (var i = 0; i < Buckets.Length; i++)
                {
                    if (value <= Buckets[i])
                    {
                        buckets[i]++;
                    }
                }
            }
        }

        public (double Sum, long Count, long[]? Buckets) Read()
        {
            lock (_lock)
            {
                return (_sum, _count, buckets is null ? null : [.. buckets]);
            }
        }
    }
}
