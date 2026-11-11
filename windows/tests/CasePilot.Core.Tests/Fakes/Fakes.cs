using System.Net;
using System.Text;
using CasePilot.Core.Abstractions;
using CasePilot.Core.Auth;
using CasePilot.Core.Models;

namespace CasePilot.Core.Tests.Fakes;

internal sealed record Sent(HttpMethod Method, string Url, Dictionary<string, string> Headers, string Body);

/// <summary>按顺序返回预设响应, 并记录所有请求.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Queue<Func<Sent, HttpResponseMessage>> _queue = new();
    public List<Sent> Requests { get; } = [];

    public FakeHttp Reply(int status, string json, params (string, string)[] headers)
    {
        _queue.Enqueue(_ =>
        {
            var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            foreach (var (k, v) in headers) r.Headers.TryAddWithoutValidation(k, v);
            return r;
        });
        return this;
    }

    /// <summary>模拟 HttpClient.Timeout 触发: 抛出 TaskCanceledException (调用方未取消).</summary>
    public FakeHttp Timeout()
    {
        _queue.Enqueue(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        return this;
    }

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        var sent = new Sent(request.Method, request.RequestUri!.AbsoluteUri, headers, body);
        Requests.Add(sent);
        if (_queue.Count == 0) throw new InvalidOperationException($"unexpected request {request.Method} {request.RequestUri}");
        return _queue.Dequeue()(sent);
    }
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 11, 3, 0, 0, TimeSpan.Zero);
}

/// <summary>记录等待时长; 可选地推进时钟.</summary>
internal sealed class FakeDelay(FakeClock? clock = null) : IDelay
{
    public List<TimeSpan> Calls { get; } = [];
    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        Calls.Add(delay);
        if (clock is not null) clock.UtcNow += delay;
        return Task.CompletedTask;
    }
}

internal sealed class SeqIds : IIdGenerator
{
    private int _n;
    public Guid NewId() => new(++_n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

internal sealed class FakeAz : IAzCli
{
    public Func<string[], AzResult> Handler { get; set; } = _ => new AzResult(0, "{}", "");
    public List<string[]> Calls { get; } = [];
    public Task<AzResult> RunAsync(string[] args, CancellationToken ct)
    {
        Calls.Add(args);
        return Task.FromResult(Handler(args));
    }
}

internal sealed class FakeAuth : IAuthProvider
{
    public AuthMode Mode => AuthMode.Cli;
    public AzureCloud Cloud { get; set; } = AzureCloud.Global;
    public string? User => "u@x";
    public string? HomeTenant => "t";
    public List<string?> Tenants { get; } = [];
    public Task<string> GetTokenAsync(string? tenant, string? resource, CancellationToken ct)
    {
        Tenants.Add(tenant);
        return Task.FromResult("tok");
    }
}
