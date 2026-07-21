namespace Clavenar.AgentSdk;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Explicit side-effect-free authorization plus durable registered-executor execution.
/// </summary>
public sealed class GovernedExecutionClient
{
    public const string ExecutionContract = "clavenar.execution/v1";
    public const string DurableExecutionContract = "clavenar.sdk-durable-intent-outbox/v1";

    private readonly ClavenarOptions _decision;
    private readonly string _executorId;
    private readonly IToolExecutor _executor;
    private readonly IDurableExecutionStore _store;
    private readonly IReceiptSigner _signer;

    public GovernedExecutionClient(
        ClavenarOptions decision,
        string executorId,
        IToolExecutor executor,
        IDurableExecutionStore store,
        IReceiptSigner signer)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(signer);
        decision.Validate();
        if (string.IsNullOrWhiteSpace(executorId))
        {
            throw new ClavenarConfigException("governed execution requires an executor id");
        }

        _decision = decision;
        _executorId = executorId;
        _executor = executor;
        _store = store;
        _signer = signer;
    }

    public sealed record PreparedToolRequest(string IdempotencyId, string Name, JsonNode Arguments);

    public sealed record ToolExecutionRequest(
        string AuthorizationId,
        string IdempotencyId,
        string ExecutorId,
        JsonNode ExecutionPayload);

    public sealed record ExecutionEffect(JsonNode Result, string EffectId);

    public sealed record WorkloadSignature(
        string Algorithm,
        string CredentialFingerprint,
        string Value);

    public sealed record GovernedExecutionOutcome(
        JsonNode Result,
        string EffectId,
        string IdempotencyId,
        JsonObject Receipt);

    public interface IDurableExecutionStore
    {
        Task CommitIntentAsync(JsonObject intent, CancellationToken cancellationToken);

        Task CommitCompletionAndEnqueueReceiptAsync(
            JsonObject completion,
            CancellationToken cancellationToken);
    }

    public interface IToolExecutor
    {
        Task<ExecutionEffect> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken);
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
        var signed = await Transport.AuthorizeAsync(
            body,
            prepared.IdempotencyId,
            _decision,
            cancellationToken).ConfigureAwait(false);
        var authorization = ValidateAuthorization(signed, prepared, body);

        var intent = new JsonObject
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
        await _store.CommitIntentAsync(intent, cancellationToken).ConfigureAwait(false);

        var effect = await _executor.ExecuteAsync(
            new ToolExecutionRequest(
                Text(authorization, "authorization_id"),
                Text(authorization, "idempotency_id"),
                _executorId,
                authorization["execution_payload"]!.DeepClone()),
            cancellationToken).ConfigureAwait(false);
        if (effect is null
            || effect.Result is null
            || string.IsNullOrWhiteSpace(effect.EffectId))
        {
            throw new ClavenarConfigException("registered executor returned an invalid effect");
        }

        string resultSha256 = Sha256(effect.Result);
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
        var signature = await _signer.SignAsync(
            (JsonObject)unsigned.DeepClone(),
            cancellationToken).ConfigureAwait(false);
        if (signature is null
            || string.IsNullOrWhiteSpace(signature.Algorithm)
            || string.IsNullOrWhiteSpace(signature.CredentialFingerprint)
            || string.IsNullOrWhiteSpace(signature.Value))
        {
            throw new ClavenarConfigException(
                "receipt signer returned an invalid workload signature");
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
            ["actual_result"] = effect.Result.DeepClone(),
            ["actual_result_sha256"] = resultSha256,
            ["effect_id"] = effect.EffectId,
            ["receipt"] = receipt.DeepClone(),
        };
        await _store.CommitCompletionAndEnqueueReceiptAsync(
            completion,
            cancellationToken).ConfigureAwait(false);
        return new GovernedExecutionOutcome(
            effect.Result.DeepClone(),
            effect.EffectId,
            prepared.IdempotencyId,
            receipt);
    }

    private static JsonObject ValidateAuthorization(
        JsonObject signed,
        PreparedToolRequest prepared,
        JsonObject body)
    {
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
            "authorization_id",
            "idempotency_id",
            "agent_id",
            "agent_spiffe",
            "tenant",
            "credential_fingerprint",
            "method",
            "payload_sha256",
        })
        {
            _ = Text(authorization, field);
        }

        var modification = authorization["modification_diff"];
        if (modification is null
            && !JsonNode.DeepEquals(authorization["execution_payload"], body))
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

    private static string Text(JsonObject value, string field) =>
        TextOrNull(value, field) is string text && text.Length > 0
            ? text
            : throw new ClavenarConfigException($"authorization is missing binding: {field}");

    private static string? TextOrNull(JsonObject value, string field) =>
        value[field] is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;

    private static string Sha256(JsonNode value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Canonicalize(value).ToJsonString());
        return $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }

    private static JsonNode Canonicalize(JsonNode value)
    {
        if (value is JsonObject obj)
        {
            var sorted = new JsonObject();
            var names = new List<string>();
            foreach (var pair in obj)
            {
                names.Add(pair.Key);
            }

            names.Sort(StringComparer.Ordinal);
            foreach (string name in names)
            {
                sorted[name] = obj[name] is JsonNode child ? Canonicalize(child) : null;
            }

            return sorted;
        }

        if (value is JsonArray array)
        {
            var ordered = new JsonArray();
            foreach (var child in array)
            {
                ordered.Add(child is null ? null : Canonicalize(child));
            }

            return ordered;
        }

        return value.DeepClone();
    }
}
