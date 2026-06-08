namespace Clavenar.AgentSdk;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Wire transport: POST /mcp inspection with retry, and GET /pending/{id} polling.</summary>
internal static class Transport
{
    private const string CorrelationHeader = "X-Clavenar-Correlation-Id";

    internal static async Task<Verdict> InspectAsync(
        NormalizedToolCall call, ClavenarOptions opts, CancellationToken ct)
    {
        var retry = opts.Retry;
        if (retry.MaxAttempts < 1)
        {
            throw new ClavenarTransportException(
                $"clavenar: Retry.MaxAttempts must be >= 1, got {retry.MaxAttempts}");
        }

        ClavenarTransportException? last = null;
        for (int attempt = 0; attempt < retry.MaxAttempts; attempt++)
        {
            try
            {
                return await InspectOnceAsync(call, opts, ct).ConfigureAwait(false);
            }
            catch (ClavenarTransportException e)
            {
                last = e;
                if (!IsRetriable(e) || attempt == retry.MaxAttempts - 1)
                {
                    throw;
                }

                await Task.Delay(Backoff(retry.BaseDelay, attempt), ct).ConfigureAwait(false);
            }
        }

        throw last!;
    }

    private static async Task<Verdict> InspectOnceAsync(
        NormalizedToolCall call, ClavenarOptions opts, CancellationToken ct)
    {
        var root = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = call.Name,
                ["arguments"] = call.Input?.DeepClone() ?? new JsonObject(),
            },
            ["id"] = call.Id,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, JoinUrl(opts.Endpoint, "/mcp"))
        {
            Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(opts.Token))
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {opts.Token}");
        }

        var (resp, body) = await SendAsync(opts, req, ct, "inspect").ConfigureAwait(false);
        using (resp)
        {
            string? corr =
                resp.Headers.TryGetValues(CorrelationHeader, out var vals) ? FirstOrNull(vals) : null;
            int status = (int)resp.StatusCode;
            return status switch
            {
                200 => new Verdict(
                    VerdictKind.Allow, corr, Array.Empty<string>(), Array.Empty<string>(), string.Empty, null),
                403 => ParseDeny(body, corr),
                202 => ParsePending(body, corr),
                _ => throw new ClavenarTransportException(UnexpectedMsg("inspect", status, body), status),
            };
        }
    }

    internal static async Task<ClavenarPendingView> PollPendingOnceAsync(
        string correlationId, ClavenarOptions opts, CancellationToken ct)
    {
        var url = JoinUrl(opts.Endpoint, "/pending/" + Uri.EscapeDataString(correlationId));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(opts.Token))
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {opts.Token}");
        }

        var (resp, body) = await SendAsync(opts, req, ct, "poll").ConfigureAwait(false);
        using (resp)
        {
            int status = (int)resp.StatusCode;
            if (status != 200)
            {
                throw new ClavenarTransportException(UnexpectedMsg("poll", status, body), status);
            }

            ClavenarPendingView? view;
            try
            {
                view = JsonSerializer.Deserialize<ClavenarPendingView>(body);
            }
            catch (JsonException e)
            {
                throw new ClavenarTransportException(
                    $"clavenar poll with unparseable body: {e.Message}", 200);
            }

            if (view is null)
            {
                throw new ClavenarTransportException("clavenar poll with empty body", 200);
            }

            if (view.Decision is not null && view.Decision != "allow" && view.Decision != "deny")
            {
                throw new ClavenarTransportException(
                    $"clavenar poll with unexpected decision: {view.Decision}", 200);
            }

            return view;
        }
    }

    private static async Task<(HttpResponseMessage Response, string Body)> SendAsync(
        ClavenarOptions opts, HttpRequestMessage req, CancellationToken ct, string op)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(opts.Timeout);
        try
        {
            var resp = await opts.EffectiveClient
                .SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return (resp, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ClavenarTransportException(
                $"clavenar {op} timed out after {opts.Timeout.TotalMilliseconds}ms");
        }
        catch (HttpRequestException e)
        {
            throw new ClavenarTransportException($"clavenar {op} failed: {e.Message}");
        }
    }

    private static Verdict ParseDeny(string body, string? corr)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException e)
        {
            throw new ClavenarTransportException($"clavenar 403 with unparseable body: {e.Message}", 403);
        }

        if (obj is null || AsString(obj["error"]) is null)
        {
            throw new ClavenarTransportException($"clavenar 403 with unexpected body shape: {body}", 403);
        }

        return new Verdict(
            VerdictKind.Deny,
            corr,
            StringList(obj["reasons"]),
            StringList(obj["review_reasons"]),
            AsString(obj["intent_category"]) ?? string.Empty,
            AsString(obj["layer"]));
    }

    private static Verdict ParsePending(string body, string? corr)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException e)
        {
            throw new ClavenarTransportException($"clavenar 202 with unparseable body: {e.Message}", 202);
        }

        bool ok =
            obj is not null
            && AsString(obj["status"]) == "pending"
            && AsString(obj["correlation_id"]) is not null
            && obj["review_reasons"] is JsonArray;
        if (!ok)
        {
            throw new ClavenarTransportException($"clavenar 202 with unexpected body shape: {body}", 202);
        }

        string id = !string.IsNullOrEmpty(corr) ? corr! : AsString(obj!["correlation_id"])!;
        if (string.IsNullOrEmpty(id))
        {
            throw new ClavenarTransportException(
                "clavenar 202 missing correlation id (header and body both empty)", 202);
        }

        return new Verdict(
            VerdictKind.Pending, id, Array.Empty<string>(), StringList(obj!["review_reasons"]), string.Empty, null);
    }

    /// <summary>
    /// Join base + path, trimming one trailing/leading slash. Deliberately not <c>Uri</c> resolution,
    /// which drops a base path for partners on an endpoint like <c>https://gw/clavenar</c>.
    /// </summary>
    internal static string JoinUrl(string baseUrl, string path)
    {
        var b = baseUrl.EndsWith('/') ? baseUrl[..^1] : baseUrl;
        var p = path.StartsWith('/') ? path[1..] : path;
        return $"{b}/{p}";
    }

    private static bool IsRetriable(ClavenarTransportException e) =>
        e.Status == 0 || (e.Status >= 500 && e.Status < 600);

    private static TimeSpan Backoff(TimeSpan baseDelay, int attempt)
    {
        long ceilingMs = (long)baseDelay.TotalMilliseconds << attempt;
        return TimeSpan.FromMilliseconds(ceilingMs * (0.5 + (Random.Shared.NextDouble() * 0.5)));
    }

    private static string UnexpectedMsg(string op, int status, string body)
    {
        var text = body?.Trim() ?? string.Empty;
        return text.Length == 0
            ? $"clavenar {op}: unexpected status {status}"
            : $"clavenar {op}: unexpected status {status}: {text}";
    }

    private static string? FirstOrNull(IEnumerable<string> values)
    {
        foreach (var v in values)
        {
            return v;
        }

        return null;
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static IReadOnlyList<string> StringList(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var e in arr)
            {
                if (e is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    list.Add(s);
                }
            }
        }

        return list;
    }
}
