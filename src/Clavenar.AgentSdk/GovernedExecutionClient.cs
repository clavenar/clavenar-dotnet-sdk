namespace Clavenar.AgentSdk;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Explicit side-effect-free authorization plus verified, recoverable registered-executor
/// execution.
/// </summary>
public sealed class GovernedExecutionClient
{
    public const string ExecutionContract = "clavenar.execution/v1";
    public const string DurableExecutionContract = "clavenar.sdk-durable-intent-outbox/v1";

    private const long MaxSafeInteger = 9_007_199_254_740_991L;
    private static readonly TimeSpan DefaultFinalizationTimeout = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> PayloadFields =
        new(new[] { "jsonrpc", "id", "method", "params" }, StringComparer.Ordinal);
    private static readonly HashSet<string> ParamFields =
        new(new[] { "name", "arguments" }, StringComparer.Ordinal);
    private static readonly JsonSerializerOptions StringOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ClavenarOptions _decision;
    private readonly string _executorId;
    private readonly IToolExecutor _executor;
    private readonly IDurableExecutionStore _store;
    private readonly IReceiptSigner _signer;
    private readonly IAuthorizationVerifier _authorizationVerifier;
    private readonly IEffectRecoverer? _recoverer;
    private readonly TimeSpan _finalizationTimeout;

    public GovernedExecutionClient(
        ClavenarOptions decision,
        string executorId,
        IToolExecutor executor,
        IDurableExecutionStore store,
        IReceiptSigner signer,
        IAuthorizationVerifier authorizationVerifier)
        : this(
            decision,
            executorId,
            executor,
            store,
            signer,
            authorizationVerifier,
            recoverer: null,
            DefaultFinalizationTimeout)
    { }

    public GovernedExecutionClient(
        ClavenarOptions decision,
        string executorId,
        IToolExecutor executor,
        IDurableExecutionStore store,
        IReceiptSigner signer,
        IAuthorizationVerifier authorizationVerifier,
        IEffectRecoverer? recoverer,
        TimeSpan finalizationTimeout)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(authorizationVerifier);
        decision.Validate();
        if (string.IsNullOrWhiteSpace(executorId))
        {
            throw new ClavenarConfigException("governed execution requires an executor id");
        }

        if (finalizationTimeout <= TimeSpan.Zero || finalizationTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ClavenarConfigException(
                "governed execution finalization timeout must be between zero and five minutes");
        }

        _decision = decision;
        _executorId = executorId;
        _executor = executor;
        _store = store;
        _signer = signer;
        _authorizationVerifier = authorizationVerifier;
        _recoverer = recoverer;
        _finalizationTimeout = finalizationTimeout;
    }

    public sealed record PreparedToolRequest(string IdempotencyId, string Name, JsonNode Arguments);

    public sealed record ToolExecutionRequest(
        string AuthorizationId,
        string IdempotencyId,
        string ExecutorId,
        JsonNode ExecutionPayload);

    public sealed record ExecutionEffect(JsonNode Result, string EffectId);

    public sealed record ExecutionState(JsonObject? Intent = null, JsonObject? Completion = null);

    public sealed record WorkloadSignature(
        string Algorithm,
        string CredentialFingerprint,
        string Value);

    public sealed record GovernedExecutionOutcome(
        JsonNode Result,
        string EffectId,
        string IdempotencyId,
        JsonObject Receipt);

    /// <summary>
    /// The implementation must atomically reject duplicate intent creation and atomically retain
    /// completion with its receipt outbox row.
    /// </summary>
    public interface IDurableExecutionStore
    {
        Task<ExecutionState> LoadExecutionAsync(
            string idempotencyId,
            CancellationToken cancellationToken);

        Task CommitIntentAsync(JsonObject intent, CancellationToken cancellationToken);

        Task CommitCompletionAndEnqueueReceiptAsync(
            JsonObject completion,
            CancellationToken cancellationToken);
    }

    public interface IToolExecutor
    {
        /// <summary>The provider boundary must use <c>request.IdempotencyId</c>.</summary>
        Task<ExecutionEffect> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken);
    }

    public interface IEffectRecoverer
    {
        /// <summary>
        /// Return a conclusive provider result, or null while the persisted intent is ambiguous.
        /// </summary>
        Task<ExecutionEffect?> RecoverAsync(
            JsonObject intent,
            CancellationToken cancellationToken);
    }

    public interface IAuthorizationVerifier
    {
        /// <summary>Cryptographically verify Identity's exact signed authorization.</summary>
        Task VerifyAsync(JsonObject signedAuthorization, CancellationToken cancellationToken);
    }

    public interface IReceiptSigner
    {
        Task<WorkloadSignature> SignAsync(
            JsonObject unsignedReceipt,
            CancellationToken cancellationToken);
    }

    public static PreparedToolRequest Prepare(string name, JsonNode? arguments) =>
        Restore(Guid.NewGuid().ToString("D"), name, arguments);

    public static PreparedToolRequest Restore(
        string idempotencyId,
        string name,
        JsonNode? arguments)
    {
        var prepared = new PreparedToolRequest(
            idempotencyId,
            name,
            arguments?.DeepClone() ?? new JsonObject());
        ValidatePrepared(prepared);
        return prepared;
    }

    public Task<GovernedExecutionOutcome> ExecuteAsync(
        string name,
        JsonNode? arguments,
        CancellationToken cancellationToken = default) =>
        ExecutePreparedAsync(Prepare(name, arguments), cancellationToken);

    public async Task<GovernedExecutionOutcome> ExecutePreparedAsync(
        PreparedToolRequest prepared,
        CancellationToken cancellationToken = default)
    {
        ValidatePrepared(prepared);
        var body = Transport.ToolRequest(
            prepared.Name,
            prepared.Arguments,
            prepared.IdempotencyId);
        var state = await _store.LoadExecutionAsync(
            prepared.IdempotencyId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new ClavenarConfigException("durable store returned null execution state");
        if (state.Completion is not null)
        {
            return await RecoverCompletionAsync(prepared, body, state, cancellationToken)
                .ConfigureAwait(false);
        }

        if (state.Intent is not null)
        {
            var authorization = await ValidateStoredIntentAsync(
                state.Intent,
                prepared,
                body,
                cancellationToken).ConfigureAwait(false);
            if (_recoverer is null)
            {
                throw new ClavenarRecoveryRequiredException(prepared.IdempotencyId);
            }

            var recovered = await _recoverer.RecoverAsync(
                (JsonObject)state.Intent.DeepClone(),
                cancellationToken).ConfigureAwait(false);
            if (recovered is null)
            {
                throw new ClavenarRecoveryRequiredException(prepared.IdempotencyId);
            }

            return await CompleteExecutionAsync(
                (JsonObject)state.Intent["authorization"]!.DeepClone(),
                authorization,
                recovered,
                prepared.IdempotencyId).ConfigureAwait(false);
        }

        var signed = await Transport.AuthorizeAsync(
            body,
            prepared.IdempotencyId,
            _decision,
            cancellationToken).ConfigureAwait(false);
        var auth = ValidateAuthorization(signed, prepared, body);
        await VerifyAuthorizationAsync(signed, stored: false, cancellationToken)
            .ConfigureAwait(false);
        var intent = ExecutionIntent(signed, auth);
        await _store.CommitIntentAsync(intent, cancellationToken).ConfigureAwait(false);

        var effect = await _executor.ExecuteAsync(
            new ToolExecutionRequest(
                Text(auth, "authorization_id"),
                Text(auth, "idempotency_id"),
                _executorId,
                auth["execution_payload"]!.DeepClone()),
            cancellationToken).ConfigureAwait(false);
        return await CompleteExecutionAsync(
            signed,
            auth,
            effect,
            prepared.IdempotencyId).ConfigureAwait(false);
    }

    private JsonObject ExecutionIntent(JsonObject signed, JsonObject authorization) => new()
    {
        ["contract"] = DurableExecutionContract,
        ["stage"] = "execution.intent",
        ["authorization_id"] = Text(authorization, "authorization_id"),
        ["idempotency_id"] = Text(authorization, "idempotency_id"),
        ["tenant"] = Text(authorization, "tenant"),
        ["workload_id"] = Text(authorization, "agent_id"),
        ["workload_spiffe"] = Text(authorization, "agent_spiffe"),
        ["payload_sha256"] = Text(authorization, "payload_sha256"),
        ["executor_id"] = _executorId,
        ["authorization"] = signed.DeepClone(),
    };

    private async Task<GovernedExecutionOutcome> CompleteExecutionAsync(
        JsonObject signed,
        JsonObject authorization,
        ExecutionEffect effect,
        string idempotencyId)
    {
        if (effect is null
            || effect.Result is null
            || string.IsNullOrWhiteSpace(effect.EffectId))
        {
            throw new ClavenarConfigException("registered executor returned an invalid effect");
        }

        var stableEffect = new ExecutionEffect(effect.Result.DeepClone(), effect.EffectId);
        string resultSha256 = Sha256(stableEffect.Result);
        var unsigned = UnsignedReceipt(signed, authorization, stableEffect, resultSha256);
        using var signerTimeout = new CancellationTokenSource(_finalizationTimeout);
        WorkloadSignature signature;
        try
        {
            signature = await _signer.SignAsync(
                (JsonObject)unsigned.DeepClone(),
                signerTimeout.Token).WaitAsync(_finalizationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException error)
        {
            throw new ClavenarTransportException(
                $"receipt signing timed out after {_finalizationTimeout}",
                innerException: error);
        }

        if (signature is null
            || string.IsNullOrWhiteSpace(signature.Algorithm)
            || string.IsNullOrWhiteSpace(signature.CredentialFingerprint)
            || string.IsNullOrWhiteSpace(signature.Value))
        {
            throw new ClavenarConfigException(
                "receipt signer returned an invalid workload signature");
        }

        if (!string.Equals(
            signature.CredentialFingerprint,
            Text(authorization, "credential_fingerprint"),
            StringComparison.Ordinal))
        {
            throw new ClavenarConfigException(
                "receipt signer credential does not match the authorization");
        }

        var receipt = (JsonObject)unsigned.DeepClone();
        receipt["workload_signature"] = new JsonObject
        {
            ["algorithm"] = signature.Algorithm,
            ["credential_fingerprint"] = signature.CredentialFingerprint,
            ["value"] = signature.Value,
        };
        var completion = new JsonObject
        {
            ["contract"] = DurableExecutionContract,
            ["stage"] = "execution.completed",
            ["authorization_id"] = Text(authorization, "authorization_id"),
            ["idempotency_id"] = Text(authorization, "idempotency_id"),
            ["executor_id"] = _executorId,
            ["actual_result"] = stableEffect.Result.DeepClone(),
            ["actual_result_sha256"] = resultSha256,
            ["effect_id"] = stableEffect.EffectId,
            ["receipt"] = receipt.DeepClone(),
        };
        using var completionTimeout = new CancellationTokenSource(_finalizationTimeout);
        try
        {
            await _store.CommitCompletionAndEnqueueReceiptAsync(
                completion,
                completionTimeout.Token).WaitAsync(_finalizationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException error)
        {
            throw new ClavenarTransportException(
                $"durable completion timed out after {_finalizationTimeout}",
                innerException: error);
        }

        return new GovernedExecutionOutcome(
            stableEffect.Result.DeepClone(),
            stableEffect.EffectId,
            idempotencyId,
            (JsonObject)receipt.DeepClone());
    }

    private static JsonObject UnsignedReceipt(
        JsonObject signed,
        JsonObject authorization,
        ExecutionEffect effect,
        string resultSha256)
    {
        var unsigned = new JsonObject
        {
            ["contract"] = ExecutionContract,
            ["stage"] = "execution.completed",
        };
        foreach (string field in new[]
        {
            "authorization_id",
            "idempotency_id",
            "correlation_id",
            "agent_id",
            "agent_spiffe",
            "tenant",
            "credential_fingerprint",
            "method",
            "payload_sha256",
        })
        {
            unsigned[field] = Text(authorization, field);
        }

        unsigned["authorization"] = signed.DeepClone();
        unsigned["result_sha256"] = resultSha256;
        unsigned["effect_id"] = effect.EffectId;
        return unsigned;
    }

    private async Task<JsonObject> ValidateStoredIntentAsync(
        JsonObject intent,
        PreparedToolRequest prepared,
        JsonObject body,
        CancellationToken cancellationToken)
    {
        if (TextOrNull(intent, "contract") != DurableExecutionContract
            || TextOrNull(intent, "stage") != "execution.intent"
            || TextOrNull(intent, "idempotency_id") != prepared.IdempotencyId
            || TextOrNull(intent, "executor_id") != _executorId
            || intent["authorization"] is not JsonObject signed)
        {
            throw new ClavenarConfigException(
                "stored execution intent does not match the prepared request");
        }

        var authorization = ValidateAuthorization(signed, prepared, body);
        if (TextOrNull(intent, "authorization_id") != TextOrNull(authorization, "authorization_id")
            || TextOrNull(intent, "tenant") != TextOrNull(authorization, "tenant")
            || TextOrNull(intent, "workload_id") != TextOrNull(authorization, "agent_id")
            || TextOrNull(intent, "workload_spiffe") != TextOrNull(authorization, "agent_spiffe")
            || TextOrNull(intent, "payload_sha256") != TextOrNull(authorization, "payload_sha256"))
        {
            throw new ClavenarConfigException(
                "stored execution intent changed an authorization binding");
        }

        await VerifyAuthorizationAsync(signed, stored: true, cancellationToken)
            .ConfigureAwait(false);
        return authorization;
    }

    private async Task<GovernedExecutionOutcome> RecoverCompletionAsync(
        PreparedToolRequest prepared,
        JsonObject body,
        ExecutionState state,
        CancellationToken cancellationToken)
    {
        if (state.Intent is null || state.Completion is null)
        {
            throw new ClavenarConfigException(
                "durable completion is missing its execution intent");
        }

        var authorization = await ValidateStoredIntentAsync(
            state.Intent,
            prepared,
            body,
            cancellationToken).ConfigureAwait(false);
        var completion = state.Completion;
        if (TextOrNull(completion, "contract") != DurableExecutionContract
            || TextOrNull(completion, "stage") != "execution.completed"
            || TextOrNull(completion, "authorization_id") != TextOrNull(authorization, "authorization_id")
            || TextOrNull(completion, "idempotency_id") != prepared.IdempotencyId
            || TextOrNull(completion, "executor_id") != _executorId
            || string.IsNullOrWhiteSpace(TextOrNull(completion, "effect_id"))
            || completion["receipt"] is not JsonObject receipt
            || completion["actual_result"] is not JsonNode result)
        {
            throw new ClavenarConfigException("stored execution completion is invalid");
        }

        string resultSha256 = Sha256(result);
        if (receipt["workload_signature"] is not JsonObject signature
            || TextOrNull(receipt, "contract") != ExecutionContract
            || TextOrNull(receipt, "stage") != "execution.completed"
            || TextOrNull(completion, "actual_result_sha256") != resultSha256
            || TextOrNull(receipt, "result_sha256") != resultSha256
            || TextOrNull(receipt, "authorization_id") != TextOrNull(authorization, "authorization_id")
            || TextOrNull(receipt, "idempotency_id") != prepared.IdempotencyId
            || TextOrNull(receipt, "effect_id") != TextOrNull(completion, "effect_id")
            || !ReceiptBindingsMatchAuthorization(receipt, authorization)
            || !JsonNode.DeepEquals(receipt["authorization"], state.Intent["authorization"])
            || TextOrNull(signature, "credential_fingerprint") != TextOrNull(authorization, "credential_fingerprint")
            || string.IsNullOrWhiteSpace(TextOrNull(signature, "algorithm"))
            || string.IsNullOrWhiteSpace(TextOrNull(signature, "value")))
        {
            throw new ClavenarConfigException(
                "stored execution completion failed integrity validation");
        }

        return new GovernedExecutionOutcome(
            result.DeepClone(),
            Text(completion, "effect_id"),
            prepared.IdempotencyId,
            (JsonObject)receipt.DeepClone());
    }

    private static bool ReceiptBindingsMatchAuthorization(
        JsonObject receipt,
        JsonObject authorization)
    {
        foreach (string field in new[]
        {
            "authorization_id",
            "idempotency_id",
            "correlation_id",
            "agent_id",
            "agent_spiffe",
            "tenant",
            "credential_fingerprint",
            "method",
            "payload_sha256",
        })
        {
            if (TextOrNull(receipt, field) != TextOrNull(authorization, field))
            {
                return false;
            }
        }

        return true;
    }

    private async Task VerifyAuthorizationAsync(
        JsonObject signed,
        bool stored,
        CancellationToken cancellationToken)
    {
        try
        {
            await _authorizationVerifier.VerifyAsync(
                (JsonObject)signed.DeepClone(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ClavenarConfigException)
        {
            string prefix = stored ? "stored authorization" : "authorization";
            throw new ClavenarConfigException(
                $"{prefix} signature verification failed: {error.Message}");
        }
    }

    private static JsonObject ValidateAuthorization(
        JsonObject signed,
        PreparedToolRequest prepared,
        JsonObject body)
    {
        if (signed["identity_signature"] is not JsonObject signature || signature.Count == 0)
        {
            throw new ClavenarConfigException(
                "authorization is missing a valid identity signature");
        }

        if (signed["authorization"] is not JsonObject authorization
            || TextOrNull(authorization, "contract") != ExecutionContract
            || TextOrNull(authorization, "stage") != "authorization")
        {
            throw new ClavenarConfigException(
                "invalid governed execution authorization contract");
        }

        if (TextOrNull(authorization, "idempotency_id") != prepared.IdempotencyId)
        {
            throw new ClavenarConfigException(
                "authorization changed the idempotency identity");
        }

        RequireGuid(TextOrNull(authorization, "authorization_id"));
        RequireGuid(TextOrNull(authorization, "correlation_id"));
        foreach (string field in new[]
        {
            "agent_id",
            "agent_spiffe",
            "tenant",
            "credential_fingerprint",
            "brain_version",
        })
        {
            _ = Text(authorization, field);
        }

        if (!ValidSha256(TextOrNull(authorization, "payload_sha256"))
            || !ValidSha256(TextOrNull(authorization, "brain_evidence_sha256")))
        {
            throw new ClavenarConfigException(
                "authorization is missing an execution digest binding");
        }

        if (authorization["decision_principal"] is not JsonObject
            || authorization["policy_bundle"] is not JsonObject)
        {
            throw new ClavenarConfigException(
                "authorization contains invalid decision evidence");
        }

        if (TextOrNull(authorization, "method") != "tools/call"
            || TextOrNull(authorization, "tool_name") != prepared.Name)
        {
            throw new ClavenarConfigException("authorization changed the tool binding");
        }

        if (authorization["execution_payload"] is not JsonObject payload
            || !PayloadFields.SetEquals(payload.Select(pair => pair.Key))
            || TextOrNull(payload, "jsonrpc") != "2.0"
            || TextOrNull(payload, "method") != "tools/call"
            || TextOrNull(payload, "id") != prepared.IdempotencyId
            || payload["params"] is not JsonObject parameters
            || !ParamFields.SetEquals(parameters.Select(pair => pair.Key))
            || TextOrNull(parameters, "name") != prepared.Name)
        {
            throw new ClavenarConfigException(
                "authorization execution payload changed a protected request binding");
        }

        if (TextOrNull(authorization, "payload_sha256") != Sha256(payload))
        {
            throw new ClavenarConfigException(
                "authorization payload digest does not match execution payload");
        }

        if (authorization["modification_diff"] is null
            && !string.Equals(CanonicalJson(payload), CanonicalJson(body), StringComparison.Ordinal))
        {
            throw new ClavenarConfigException(
                "authorization changed an unmodified execution payload");
        }

        return authorization;
    }

    private static void ValidatePrepared(PreparedToolRequest prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (string.IsNullOrWhiteSpace(prepared.Name) || prepared.Arguments is null)
        {
            throw new ClavenarConfigException(
                "prepared tool name and JSON arguments are required");
        }

        RequireGuid(prepared.IdempotencyId);
        _ = CanonicalJson(prepared.Arguments);
    }

    private static void RequireGuid(string? value)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed)
            || !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            throw new ClavenarConfigException(
                "idempotency and authorization ids must be canonical UUIDs");
        }
    }

    private static bool ValidSha256(string? value) =>
        value is { Length: 71 }
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static string Text(JsonObject value, string field) =>
        TextOrNull(value, field) is string text && text.Length > 0
            ? text
            : throw new ClavenarConfigException($"authorization is missing binding: {field}");

    private static string? TextOrNull(JsonObject value, string field) =>
        value[field] is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;

    private static string Sha256(JsonNode value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(CanonicalJson(value));
        return $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }

    private static string CanonicalJson(JsonNode value)
    {
        switch (value)
        {
            case JsonObject obj:
                return "{" + string.Join(
                    ",",
                    obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair =>
                            $"{EncodeString(pair.Key)}:{CanonicalNullable(pair.Value)}")) + "}";
            case JsonArray array:
                return "[" + string.Join(",", array.Select(CanonicalNullable)) + "]";
            case JsonValue item:
                return CanonicalValue(item);
            default:
                throw new ClavenarConfigException("value is not a supported JSON value");
        }
    }

    private static string CanonicalNullable(JsonNode? value) =>
        value is null ? "null" : CanonicalJson(value);

    private static string CanonicalValue(JsonValue value)
    {
        if (value.TryGetValue<string>(out var text))
        {
            return EncodeString(text);
        }

        if (value.TryGetValue<bool>(out bool boolean))
        {
            return boolean ? "true" : "false";
        }

        if (value.TryGetValue<long>(out long signed))
        {
            if (Math.Abs((decimal)signed) > MaxSafeInteger)
            {
                throw new ClavenarConfigException("JSON integers must be safely representable");
            }

            return signed.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<ulong>(out ulong unsigned))
        {
            if (unsigned > MaxSafeInteger)
            {
                throw new ClavenarConfigException("JSON integers must be safely representable");
            }

            return unsigned.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<decimal>(out decimal exact))
        {
            if (decimal.Truncate(exact) == exact && Math.Abs(exact) > MaxSafeInteger)
            {
                throw new ClavenarConfigException("JSON integers must be safely representable");
            }

            return EcmaNumber((double)exact);
        }

        if (value.TryGetValue<double>(out double number))
        {
            return EcmaNumber(number);
        }

        if (value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Null)
        {
            return "null";
        }

        throw new ClavenarConfigException("value is not a supported JSON scalar");
    }

    private static string EncodeString(string value) =>
        JsonSerializer.Serialize(value, StringOptions);

    private static string EcmaNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ClavenarConfigException("JSON numbers must be finite");
        }

        if (value == 0)
        {
            return "0";
        }

        string raw = value.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        if (!raw.Contains('e', StringComparison.Ordinal))
        {
            return raw.EndsWith(".0", StringComparison.Ordinal) ? raw[..^2] : raw;
        }

        string[] parts = raw.Split('e', 2);
        string coefficient = parts[0];
        int exponent = int.Parse(parts[1], CultureInfo.InvariantCulture);
        string sign = string.Empty;
        if (coefficient.StartsWith("-", StringComparison.Ordinal))
        {
            sign = "-";
            coefficient = coefficient[1..];
        }

        string digits = coefficient.Replace(".", string.Empty, StringComparison.Ordinal);
        int decimalPosition = 1 + exponent;
        double absolute = Math.Abs(value);
        if (absolute >= 1e-6 && absolute < 1e21)
        {
            if (decimalPosition <= 0)
            {
                return sign + "0." + new string('0', -decimalPosition) + digits;
            }

            if (decimalPosition >= digits.Length)
            {
                return sign + digits + new string('0', decimalPosition - digits.Length);
            }

            return sign + digits[..decimalPosition] + "." + digits[decimalPosition..];
        }

        string tail = digits[1..].TrimEnd('0');
        string normalized = digits[..1] + (tail.Length > 0 ? "." + tail : string.Empty);
        return sign + normalized + "e" + (exponent >= 0 ? "+" : "-")
            + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
    }
}
