namespace Clavenar.AgentSdk;

using System;
using System.Collections.Generic;
using System.IO;
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
    internal const string DecisionContract = "clavenar.decision/v1";
    internal const string DecisionContractHeader = "X-Clavenar-Decision-Contract";
    internal const string IdempotencyIdHeader = "X-Clavenar-Idempotency-Id";
    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxErrorPreviewBytes = 4 * 1024;
    private const int MaxToolArgumentBytes = 1024 * 1024;
    private const int MaxBatchRequestBytes = 4 * 1024 * 1024;
    private const int MaxIdentifierBytes = 1024;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    internal static async Task<Verdict> InspectAsync(
        NormalizedToolCall call, ClavenarOptions opts, CancellationToken ct)
    {
        ValidateCall(call);
        string idempotencyId = Guid.NewGuid().ToString();
        var root = ToolRequest(call.Name, call.Input, idempotencyId);
        return await InspectDecisionAsync(root, idempotencyId, opts, ct).ConfigureAwait(false);
    }

    internal static Task<Verdict> InspectBatchAsync(
        IReadOnlyList<NormalizedToolCall> calls, ClavenarOptions opts, CancellationToken ct)
    {
        if (calls is null || calls.Count < 1 || calls.Count > 128)
        {
            throw new ClavenarConfigException("atomic decision batch must contain 1..128 calls");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        string idempotencyId = Guid.NewGuid().ToString();
        var encodedCalls = new JsonArray();
        foreach (var call in calls)
        {
            if (string.IsNullOrEmpty(call.Id)
                || string.IsNullOrEmpty(call.Name)
                || !ids.Add(call.Id))
            {
                throw new ClavenarConfigException(
                    "atomic decision calls require unique non-empty ids and names");
            }

            ValidateCall(call);

            encodedCalls.Add(new JsonObject
            {
                ["id"] = call.Id,
                ["name"] = call.Name,
                ["arguments"] = call.Input?.DeepClone() ?? new JsonObject(),
            });
        }

        var root = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idempotencyId,
            ["method"] = "clavenar/tools.batch",
            ["params"] = new JsonObject
            {
                ["name"] = "clavenar.atomic-batch",
                ["arguments"] = new JsonObject
                {
                    ["contract"] = "clavenar.atomic-tool-call-batch/v1",
                    ["calls"] = encodedCalls,
                },
            },
        };
        return InspectDecisionAsync(root, idempotencyId, opts, ct);
    }

    private static async Task<Verdict> InspectDecisionAsync(
        JsonObject root, string idempotencyId, ClavenarOptions opts, CancellationToken ct)
    {
        var retry = opts.Retry;
        string encoded = Encode(root, "inspect");

        ClavenarTransportException? last = null;
        for (int attempt = 0; attempt < retry.MaxAttempts; attempt++)
        {
            try
            {
                return await InspectOnceAsync(encoded, idempotencyId, opts, ct).ConfigureAwait(false);
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
        string bodyJson, string idempotencyId, ClavenarOptions opts, CancellationToken ct)
    {
        using var req = DecisionRequest(bodyJson, idempotencyId, opts);

        var (resp, body) = await SendAsync(opts, req, ct, "inspect").ConfigureAwait(false);
        using (resp)
        {
            string? corr =
                resp.Headers.TryGetValues(CorrelationHeader, out var vals) ? FirstOrNull(vals) : null;
            int status = (int)resp.StatusCode;
            string? contract =
                resp.Headers.TryGetValues(DecisionContractHeader, out var contracts)
                    ? FirstOrNull(contracts)
                    : null;
            if (contract is not null && contract != DecisionContract)
            {
                throw new ClavenarTransportException(
                    $"clavenar inspect: unsupported decision contract {contract}", status);
            }

            return status switch
            {
                200 => ParseAllow(body, corr),
                403 => ParseDeny(body, corr),
                202 => ParsePending(body, corr),
                429 => ParseRateLimited(body, corr),
                _ => throw new ClavenarTransportException(UnexpectedMsg("inspect", status, body), status),
            };
        }
    }

    internal static async Task<JsonObject> AuthorizeAsync(
        JsonObject body, string idempotencyId, ClavenarOptions opts, CancellationToken ct)
    {
        var retry = opts.Retry;
        string encoded = Encode(body, "authorization");
        ClavenarTransportException? last = null;
        for (int attempt = 0; attempt < retry.MaxAttempts; attempt++)
        {
            try
            {
                using var req = DecisionRequest(encoded, idempotencyId, opts);
                var (response, responseBody) = await SendAsync(opts, req, ct, "authorization")
                    .ConfigureAwait(false);
                using (response)
                {
                    int status = (int)response.StatusCode;
                    if (status == 200)
                    {
                        try
                        {
                            return JsonNode.Parse(responseBody) as JsonObject
                                ?? throw new ClavenarTransportException(
                                    "clavenar authorization returned a non-object", 200);
                        }
                        catch (ClavenarTransportException)
                        {
                            throw;
                        }
                        catch (JsonException error)
                        {
                            throw new ClavenarTransportException(
                                $"clavenar authorization returned invalid JSON: {error.Message}", 200);
                        }
                    }

                    last = new ClavenarTransportException(
                        UnexpectedMsg("authorization", status, responseBody), status);
                }
            }
            catch (ClavenarTransportException error)
            {
                last = error;
            }

            if (last is null || !IsRetriable(last) || attempt == retry.MaxAttempts - 1)
            {
                throw last!;
            }

            await Task.Delay(Backoff(retry.BaseDelay, attempt), ct).ConfigureAwait(false);
        }

        throw last!;
    }

    internal static JsonObject ToolRequest(string name, JsonNode? input, string idempotencyId) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = input?.DeepClone() ?? new JsonObject(),
            },
            ["id"] = idempotencyId,
        };

    private static HttpRequestMessage DecisionRequest(
        string body, string idempotencyId, ClavenarOptions opts)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, JoinUrl(opts.Endpoint, "/mcp"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(DecisionContractHeader, DecisionContract);
        request.Headers.TryAddWithoutValidation(IdempotencyIdHeader, idempotencyId);
        var token = opts.EffectiveToken;
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        return request;
    }

    internal static async Task<ClavenarPendingView> PollPendingOnceAsync(
        string correlationId, ClavenarOptions opts, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(correlationId)
            || Encoding.UTF8.GetByteCount(correlationId) > MaxIdentifierBytes)
        {
            throw new ClavenarConfigException(
                $"pending correlation id must be non-empty and no greater than {MaxIdentifierBytes} bytes");
        }

        var url = JoinUrl(opts.Endpoint, "/pending/" + Uri.EscapeDataString(correlationId));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var token = opts.EffectiveToken;
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
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

            if (view.CorrelationId != correlationId
                || string.IsNullOrEmpty(view.AgentId)
                || string.IsNullOrEmpty(view.ToolType)
                || string.IsNullOrEmpty(view.Method)
                || view.ReviewReasons is null
                || string.IsNullOrEmpty(view.RequestedAt))
            {
                throw new ClavenarTransportException(
                    "clavenar poll with unexpected body shape or correlation id", 200);
            }

            return view;
        }
    }

    private static async Task<(HttpResponseMessage Response, string Body)> SendAsync(
        ClavenarOptions opts, HttpRequestMessage req, CancellationToken ct, string op)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(opts.EffectiveTimeout);
        try
        {
            var (client, owned) = opts.AcquireClient();
            try
            {
                var resp = await client
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                try
                {
                    int status = (int)resp.StatusCode;
                    int limit = ResponseLimit(status);
                    if (resp.Content.Headers.ContentLength is long declared && declared > limit)
                    {
                        throw new ClavenarTransportException(
                            $"clavenar {op} response exceeded {limit} bytes", status);
                    }

                    string body = await ReadBoundedAsync(resp, limit, op, cts.Token)
                        .ConfigureAwait(false);
                    return (resp, body);
                }
                catch
                {
                    resp.Dispose();
                    throw;
                }
            }
            finally
            {
                if (owned)
                {
                    client.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ClavenarTransportException(
                $"clavenar {op} timed out after {opts.EffectiveTimeout.TotalMilliseconds}ms");
        }
        catch (HttpRequestException e)
        {
            throw new ClavenarTransportException(
                $"clavenar {op} failed: {e.Message}", e);
        }
    }

    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response, int limit, string op, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(limit, 8192));
        var buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int remaining = (limit + 1) - total;
            if (remaining <= 0)
            {
                break;
            }

            int read = await stream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            total += read;
        }

        if (total > limit)
        {
            throw new ClavenarTransportException(
                $"clavenar {op} response exceeded {limit} bytes", (int)response.StatusCode);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, total);
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
            throw new ClavenarTransportException(
                $"clavenar 403 with unexpected body shape: {Preview(body)}", 403);
        }

        return new Verdict(
            VerdictKind.Deny,
            corr,
            StringList(obj["reasons"]),
            StringList(obj["review_reasons"]),
            AsString(obj["intent_category"]) ?? string.Empty,
            AsString(obj["layer"]),
            ParseVerdictDetail(obj["detail"]));
    }

    private static Verdict ParseAllow(string body, string? corr)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            JsonObject? obj;
            try
            {
                obj = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException e)
            {
                throw new ClavenarTransportException(
                    $"clavenar 200 with unparseable body: {e.Message}", 200);
            }

            if (obj is null || obj.Count != 1 || AsString(obj["verdict"]) != "allow")
            {
                throw new ClavenarTransportException(
                    $"clavenar 200 with unexpected body shape: {Preview(body)}", 200);
            }
        }

        return new Verdict(
            VerdictKind.Allow,
            corr,
            Array.Empty<string>(),
            Array.Empty<string>(),
            string.Empty,
            null);
    }

    // Parse the optional verbose-verdict breakdown. Lenient: a missing or
    // malformed block yields null (the gateway omits it unless
    // CLAVENAR_PROXY_VERBOSE_VERDICTS=true).
    private static VerdictDetail? ParseVerdictDetail(JsonNode? node)
    {
        if (node is not JsonObject obj || obj["detectors"] is not JsonArray rawDetectors)
        {
            return null;
        }

        var detectors = new List<DetectorScore>();
        foreach (var item in rawDetectors)
        {
            if (item is not JsonObject d
                || AsString(d["detector"]) is not string name
                || d["score"] is not JsonValue sv
                || !sv.TryGetValue<double>(out var score))
            {
                continue;
            }

            var flagged = d["flagged"] is JsonValue fv && fv.TryGetValue<bool>(out var f) && f;
            detectors.Add(new DetectorScore(name, score, flagged));
        }

        return new VerdictDetail(detectors, StringList(obj["degraded"]));
    }

    // Lenient like the deny parser: only the string `error` code is required; the verdict falls
    // back to rate_limited when the body omits it (both codes ride HTTP 429).
    private static Verdict ParseRateLimited(string body, string? corr)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException e)
        {
            throw new ClavenarTransportException($"clavenar 429 with unparseable body: {e.Message}", 429);
        }

        if (obj is null || AsString(obj["error"]) is null)
        {
            throw new ClavenarTransportException(
                $"clavenar 429 with unexpected body shape: {Preview(body)}", 429);
        }

        string code = AsString(obj["verdict"]) == "quota_exceeded" ? "quota_exceeded" : "rate_limited";
        int? retryAfterSecs =
            obj["retry_after_secs"] is JsonValue rv
                && rv.TryGetValue<int>(out var secs)
                && secs >= 0
                ? secs
                : null;
        string? id = !string.IsNullOrEmpty(corr) ? corr : AsString(obj["correlation_id"]);
        return new Verdict(
            VerdictKind.RateLimited,
            id,
            StringList(obj["reasons"]),
            Array.Empty<string>(),
            string.Empty,
            AsString(obj["layer"]),
            null,
            code,
            retryAfterSecs);
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
            throw new ClavenarTransportException(
                $"clavenar 202 with unexpected body shape: {Preview(body)}", 202);
        }

        string bodyId = AsString(obj!["correlation_id"])!;
        if (!string.IsNullOrEmpty(corr)
            && !string.IsNullOrEmpty(bodyId)
            && !string.Equals(corr, bodyId, StringComparison.Ordinal))
        {
            throw new ClavenarTransportException(
                "clavenar 202 correlation id header/body mismatch", 202);
        }

        string id = !string.IsNullOrEmpty(corr) ? corr! : bodyId;
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
        double ceilingMs = Math.Min(
            MaxRetryDelay.TotalMilliseconds,
            baseDelay.TotalMilliseconds * (1 << Math.Min(attempt, 30)));
        return TimeSpan.FromMilliseconds(ceilingMs * (0.5 + (Random.Shared.NextDouble() * 0.5)));
    }

    private static string UnexpectedMsg(string op, int status, string body)
    {
        var text = Preview(body).Trim();
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

    private static string Encode(JsonObject body, string operation)
    {
        string encoded;
        try
        {
            encoded = body.ToJsonString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new ClavenarTransportException(
                $"clavenar {operation}: failed to encode request: {e.Message}", e);
        }

        if (Encoding.UTF8.GetByteCount(encoded) > MaxBatchRequestBytes)
        {
            throw new ClavenarTransportException(
                $"clavenar {operation} request exceeded {MaxBatchRequestBytes} bytes");
        }

        return encoded;
    }

    private static void ValidateCall(NormalizedToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (string.IsNullOrEmpty(call.Id) || string.IsNullOrEmpty(call.Name))
        {
            throw new ClavenarConfigException("tool call requires a non-empty id and name");
        }

        if (Encoding.UTF8.GetByteCount(call.Id) > MaxIdentifierBytes
            || Encoding.UTF8.GetByteCount(call.Name) > MaxIdentifierBytes)
        {
            throw new ClavenarConfigException(
                $"tool call id and name must not exceed {MaxIdentifierBytes} bytes");
        }

        int argumentBytes = Encoding.UTF8.GetByteCount(call.Input?.ToJsonString() ?? "null");
        if (argumentBytes > MaxToolArgumentBytes)
        {
            throw new ClavenarConfigException(
                $"tool call arguments exceeded {MaxToolArgumentBytes} bytes");
        }
    }

    private static int ResponseLimit(int status) =>
        status is 200 or 202 or 403 or 429 ? MaxResponseBytes : MaxErrorPreviewBytes;

    private static string Preview(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        return bytes.Length <= MaxErrorPreviewBytes
            ? value
            : Encoding.UTF8.GetString(bytes, 0, MaxErrorPreviewBytes);
    }
}
