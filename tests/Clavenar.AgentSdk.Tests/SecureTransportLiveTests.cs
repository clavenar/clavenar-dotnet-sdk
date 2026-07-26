namespace Clavenar.AgentSdk.Tests;

using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

public sealed class SecureTransportLiveTests
{
    [Fact]
    public async Task RealMtlsAndCertificateTokenRotation()
    {
        string? endpoint = Environment.GetEnvironmentVariable(
            "CLAVENAR_SECURE_TRANSPORT_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        string cert = Required("CLAVENAR_SECURE_TRANSPORT_CLIENT_CERT");
        string key = Required("CLAVENAR_SECURE_TRANSPORT_CLIENT_KEY");
        int generation = 0;
        var profile = new SecureTransportProfile
        {
            CaBundlePath = Required("CLAVENAR_SECURE_TRANSPORT_CA"),
            ClientCertificatePath = cert,
            PrivateKeyPath = key,
            TokenSource = () => $"matrix-token-{++generation}",
        };
        var inspector = new ClavenarInspector(new ClavenarOptions
        {
            Endpoint = endpoint,
            SecureTransport = profile,
        });
        var call = new NormalizedToolCall("matrix", "matrix_probe", new JsonObject());
        Assert.Equal(VerdictKind.Allow, (await inspector.InspectAsync(call)).Kind);

        File.Copy(Required("CLAVENAR_SECURE_TRANSPORT_NEXT_CERT"), cert, overwrite: true);
        File.Copy(Required("CLAVENAR_SECURE_TRANSPORT_NEXT_KEY"), key, overwrite: true);
        Assert.Equal(VerdictKind.Allow, (await inspector.InspectAsync(call)).Kind);
        Assert.Equal(2, generation);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"{name} is required");
}
