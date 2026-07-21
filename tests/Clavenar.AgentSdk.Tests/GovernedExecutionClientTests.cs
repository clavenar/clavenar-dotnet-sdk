namespace Clavenar.AgentSdk.Tests;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

public sealed class GovernedExecutionClientTests
{
    private const string IdempotencyId = "cfcc8767-4c73-41cc-8ece-b855863924c4";

    [Fact]
    public async Task CommitsIntentBeforeOneEffectAndReturnsActualResult()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((request, body) =>
        {
            captured = request;
            return StubResponse.Of(200, Authorization(body));
        });
        var order = new List<string>();
        var store = new RecordingStore(order);
        var client = new GovernedExecutionClient(
            Fixtures.Opts(handler),
            "payments-provider",
            new Executor(order),
            store,
            new Signer());
        var prepared = GovernedExecutionClient.Restore(
            IdempotencyId,
            "payments.transfer",
            JsonNode.Parse("{\"amount\":100}"));

        var outcome = await client.ExecutePreparedAsync(prepared);

        Assert.Equal(new[] { "intent", "effect", "completion" }, order);
        Assert.True((bool)outcome.Result["ok"]!);
        Assert.Equal("provider-operation-123", outcome.EffectId);
        Assert.Equal(
            Transport.DecisionContract,
            captured!.Headers.GetValues(Transport.DecisionContractHeader).Single());
        Assert.Equal(
            IdempotencyId,
            captured.Headers.GetValues(Transport.IdempotencyIdHeader).Single());
        Assert.Equal(
            "sha256:4062edaf750fb8074e7e83e0c9028c94e32468a8b6f1614774328ef045150f93",
            (string?)store.Completion!["actual_result_sha256"]);
    }

    [Fact]
    public async Task IntentFailureInvokesNoExecutor()
    {
        var handler = new StubHandler((_, body) => StubResponse.Of(200, Authorization(body)));
        var order = new List<string>();
        var executor = new Executor(order);
        var client = new GovernedExecutionClient(
            Fixtures.Opts(handler),
            "payments-provider",
            executor,
            new RecordingStore(order, failIntent: true),
            new Signer());
        var prepared = GovernedExecutionClient.Restore(
            IdempotencyId,
            "payments.transfer",
            JsonNode.Parse("{\"amount\":100}"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExecutePreparedAsync(prepared));
        Assert.False(executor.Called);
    }

    private static string Authorization(string requestBody)
    {
        var authorization = new JsonObject
        {
            ["contract"] = GovernedExecutionClient.ExecutionContract,
            ["stage"] = "authorization",
            ["authorization_id"] = "354c33ed-e5d3-4af7-a1b8-b009d50b0bc5",
            ["idempotency_id"] = IdempotencyId,
            ["correlation_id"] = "c1a28e4c-a17d-5b3d-884b-e5b627f762c2",
            ["agent_id"] = "payments-agent",
            ["agent_spiffe"] =
                "spiffe://clavenar.local/tenant/acme/agent/payments-agent/instance/one",
            ["tenant"] = "acme",
            ["credential_fingerprint"] = "sha256:" + new string('1', 64),
            ["method"] = "tools/call",
            ["tool_name"] = "payments.transfer",
            ["execution_payload"] = JsonNode.Parse(requestBody),
            ["payload_sha256"] = "sha256:" + new string('2', 64),
            ["decision_principal"] = new JsonObject { ["subject"] = "system:policy-brain" },
            ["modification_diff"] = null,
            ["policy_bundle"] = new JsonObject { ["schema_version"] = 1 },
            ["brain_version"] = "brain-fixture",
            ["brain_evidence_sha256"] = "sha256:" + new string('3', 64),
        };
        return new JsonObject
        {
            ["authorization"] = authorization,
            ["identity_signature"] = new JsonObject { ["algorithm"] = "Ed25519" },
        }.ToJsonString();
    }

    private sealed class Executor : GovernedExecutionClient.IToolExecutor
    {
        private readonly List<string> _order;

        public Executor(List<string> order) => _order = order;

        public bool Called { get; private set; }

        public Task<GovernedExecutionClient.ExecutionEffect> ExecuteAsync(
            GovernedExecutionClient.ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Called = true;
            _order.Add("effect");
            Assert.Equal(IdempotencyId, request.IdempotencyId);
            return Task.FromResult(new GovernedExecutionClient.ExecutionEffect(
                new JsonObject { ["ok"] = true },
                "provider-operation-123"));
        }
    }

    private sealed class RecordingStore : GovernedExecutionClient.IDurableExecutionStore
    {
        private readonly List<string> _order;
        private readonly bool _failIntent;

        public RecordingStore(List<string> order, bool failIntent = false)
        {
            _order = order;
            _failIntent = failIntent;
        }

        public JsonObject? Completion { get; private set; }

        public Task CommitIntentAsync(JsonObject intent, CancellationToken cancellationToken)
        {
            _order.Add("intent");
            if (_failIntent)
            {
                throw new InvalidOperationException("store unavailable");
            }

            Assert.Equal("payments-provider", (string?)intent["executor_id"]);
            return Task.CompletedTask;
        }

        public Task CommitCompletionAndEnqueueReceiptAsync(
            JsonObject completion,
            CancellationToken cancellationToken)
        {
            _order.Add("completion");
            Completion = completion;
            return Task.CompletedTask;
        }
    }

    private sealed class Signer : GovernedExecutionClient.IReceiptSigner
    {
        public Task<GovernedExecutionClient.WorkloadSignature> SignAsync(
            JsonObject unsignedReceipt,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GovernedExecutionClient.WorkloadSignature(
                "ES256",
                "sha256:" + new string('1', 64),
                "signed"));
    }
}
