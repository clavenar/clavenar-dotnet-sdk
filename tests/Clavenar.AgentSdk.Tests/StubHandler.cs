namespace Clavenar.AgentSdk.Tests;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

/// <summary>An HttpMessageHandler that routes requests to a test function — no socket required.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, StubResponse> _route;

    public StubHandler(Func<HttpRequestMessage, string, StubResponse> route) => _route = route;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var r = _route(request, body);
        if (r.Delay > TimeSpan.Zero)
        {
            await Task.Delay(r.Delay, cancellationToken);
        }

        if (r.Throw)
        {
            throw new HttpRequestException("simulated network failure");
        }

        var resp = new HttpResponseMessage((HttpStatusCode)r.Status);
        if (r.Body is not null)
        {
            resp.Content = new StringContent(r.Body);
        }

        if (r.CorrelationId is not null)
        {
            resp.Headers.TryAddWithoutValidation("X-Clavenar-Correlation-Id", r.CorrelationId);
        }

        return resp;
    }
}

internal sealed record StubResponse
{
    public int Status { get; init; } = 200;

    public string? Body { get; init; }

    public string? CorrelationId { get; init; }

    public TimeSpan Delay { get; init; }

    public bool Throw { get; init; }

    public static StubResponse Of(int status, string? body = null) => new() { Status = status, Body = body };
}
