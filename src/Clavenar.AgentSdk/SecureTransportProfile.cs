namespace Clavenar.AgentSdk;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

/// <summary>Explicit proxy behavior for a secure transport profile.</summary>
public enum SecureProxyMode
{
    Direct,
    Environment,
    Explicit,
}

/// <summary>
/// One reusable reload-before-request mTLS, token, deadline, and proxy profile.
/// </summary>
public sealed record SecureTransportProfile
{
    public required string CaBundlePath { get; init; }

    public required string ClientCertificatePath { get; init; }

    public required string PrivateKeyPath { get; init; }

    public Func<string?>? TokenSource { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public SecureProxyMode ProxyMode { get; init; } = SecureProxyMode.Direct;

    public Uri? ProxyUri { get; init; }

    internal HttpClient CreateClient()
    {
        Validate();
        try
        {
            var identity =
                X509Certificate2.CreateFromPemFile(ClientCertificatePath, PrivateKeyPath);
            var roots = new X509Certificate2Collection();
            roots.ImportFromPem(File.ReadAllText(CaBundlePath));
            if (roots.Count == 0)
            {
                throw new ClavenarConfigException(
                    "secure transport CA bundle contains no certificates");
            }

            var clientCertificates = new X509CertificateCollection { identity };
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = ConnectTimeout,
                SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificates = clientCertificates,
                    RemoteCertificateValidationCallback =
                        (_, certificate, _, errors) => ValidateServer(certificate, errors, roots),
                },
            };
            ConfigureProxy(handler);
            return new HttpClient(handler, disposeHandler: true) { Timeout = RequestTimeout };
        }
        catch (ClavenarConfigException)
        {
            throw;
        }
        catch (Exception error) when (
            error is IOException
                or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException)
        {
            throw new ClavenarConfigException(
                $"cannot build secure transport profile: {error.Message}");
        }
    }

    internal string? Token()
    {
        var token = TokenSource?.Invoke();
        if (token is null)
        {
            return null;
        }

        token = token.Trim();
        if (token.Length == 0)
        {
            throw new ClavenarConfigException(
                "secure transport token source returned an empty token");
        }

        return token;
    }

    internal void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero)
        {
            throw new ClavenarConfigException("secure transport timeouts must be positive");
        }

        foreach (
            var (label, path) in
            new[]
            {
                ("CA bundle", CaBundlePath),
                ("client certificate", ClientCertificatePath),
                ("private key", PrivateKeyPath),
            })
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new ClavenarConfigException($"secure transport {label} is missing");
            }

            if (new FileInfo(path).Length == 0)
            {
                throw new ClavenarConfigException($"secure transport {label} is empty");
            }
        }

        if (
            ProxyMode == SecureProxyMode.Explicit
            && (
                ProxyUri is null
                || !ProxyUri.IsAbsoluteUri
                || (ProxyUri.Scheme != Uri.UriSchemeHttp && ProxyUri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new ClavenarConfigException(
                "secure transport explicit proxy must use an absolute HTTP(S) URL");
        }
    }

    private void ConfigureProxy(SocketsHttpHandler handler)
    {
        switch (ProxyMode)
        {
            case SecureProxyMode.Direct:
                handler.UseProxy = false;
                break;
            case SecureProxyMode.Environment:
                handler.UseProxy = true;
                handler.Proxy = null;
                break;
            case SecureProxyMode.Explicit:
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(ProxyUri!);
                break;
            default:
                throw new ClavenarConfigException("unknown secure transport proxy mode");
        }
    }

    private static bool ValidateServer(
        X509Certificate? certificate,
        SslPolicyErrors errors,
        X509Certificate2Collection roots)
    {
        if (
            certificate is null
            || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0
            || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        {
            return false;
        }

        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        return chain.Build(leaf);
    }
}
