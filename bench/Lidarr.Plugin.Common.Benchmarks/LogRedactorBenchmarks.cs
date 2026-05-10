using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Lidarr.Plugin.Common.Observability;

namespace Lidarr.Plugin.Common.Benchmarks;

/// <summary>
/// Baseline for <see cref="LogRedactor.Redact"/> across representative inputs:
/// nothing-to-redact, a bearer-style token, a JSON-shaped credential blob,
/// and a query string with sensitive parameters.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class LogRedactorBenchmarks
{
    private const string PlainMessage =
        "GET /v1/catalog/items completed in 42ms";

    private const string BearerToken =
        "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkphbmUiLCJhZG1pbiI6dHJ1ZSwiaWF0IjoxNzE2MjM5MDIyfQ.8aGthLPdXh3BkuewU1z6m2t8vUE3wAJzM6n3bRYI4Pc";

    private const string JsonCredential =
        "{\"client_id\":\"abcd\",\"client_secret\":\"S3cr3tValueWithEntropy_x77y!\",\"refresh_token\":\"rt_98765abcdef\",\"region\":\"us\"}";

    private const string QueryWithSecret =
        "https://api.example.test/v1/auth?app_id=app_123&app_secret=hunter2hunter2hunter2&token=tok_qwerty1234567890&region=eu";

    [Benchmark(Description = "Redact — plain message (no match)")]
    public string Plain() => LogRedactor.Redact(PlainMessage);

    [Benchmark(Description = "Redact — bearer token in header line")]
    public string Bearer() => LogRedactor.Redact(BearerToken);

    [Benchmark(Description = "Redact — JSON credential blob")]
    public string JsonBlob() => LogRedactor.Redact(JsonCredential);

    [Benchmark(Description = "Redact — query string with sensitive params")]
    public string QueryString() => LogRedactor.Redact(QueryWithSecret);
}
