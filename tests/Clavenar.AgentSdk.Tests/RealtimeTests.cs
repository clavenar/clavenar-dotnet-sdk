namespace Clavenar.AgentSdk.Tests;

using System.Threading.Tasks;

public class RealtimeTests
{
    [Fact]
    public void NormalizeValidJson()
    {
        var tc = Realtime.Normalize(new Realtime.FunctionCallDone("call_1", "transfer", "{\"amount\":100}"));
        Assert.Equal("call_1", tc.Id);
        Assert.Equal("transfer", tc.Name);
        Assert.Equal(100, (int)tc.Input!["amount"]!);
    }

    [Fact]
    public void NormalizeInvalidJsonFallsBackToString()
    {
        var tc = Realtime.Normalize(new Realtime.FunctionCallDone("call_2", "transfer", "not json"));
        Assert.Equal("not json", (string?)tc.Input);
    }

    [Fact]
    public async Task InspectAllow()
    {
        var h = new StubHandler((_, _) => StubResponse.Of(200));
        var v = await Realtime.InspectAsync(
            new Realtime.FunctionCallDone("call_3", "f", "{}"), Fixtures.Opts(h));
        Assert.Equal(VerdictKind.Allow, v.Kind);
    }
}
