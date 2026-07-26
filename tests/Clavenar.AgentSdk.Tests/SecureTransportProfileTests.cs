namespace Clavenar.AgentSdk.Tests;

using System;
using Xunit;

public sealed class SecureTransportProfileTests
{
    [Fact]
    public void AcquiresAndTrimsFreshTokenForEveryRequest()
    {
        int generation = 0;
        var profile = new SecureTransportProfile
        {
            CaBundlePath = "ca",
            ClientCertificatePath = "cert",
            PrivateKeyPath = "key",
            TokenSource = () => $" token-{++generation} ",
        };

        Assert.Equal("token-1", profile.Token());
        Assert.Equal("token-2", profile.Token());
    }

    [Fact]
    public void RejectsZeroTimeoutBeforeReadingCredentialFiles()
    {
        var profile = new SecureTransportProfile
        {
            CaBundlePath = "missing",
            ClientCertificatePath = "missing",
            PrivateKeyPath = "missing",
            ConnectTimeout = TimeSpan.Zero,
        };

        Assert.Throws<ClavenarConfigException>(profile.Validate);
    }

    [Fact]
    public void RejectsEmptyToken()
    {
        var profile = new SecureTransportProfile
        {
            CaBundlePath = "ca",
            ClientCertificatePath = "cert",
            PrivateKeyPath = "key",
            TokenSource = () => " ",
        };

        Assert.Throws<ClavenarConfigException>(profile.Token);
    }
}
