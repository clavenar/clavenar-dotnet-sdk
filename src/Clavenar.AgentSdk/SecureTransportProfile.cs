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
/// One reusable cached mTLS, token, deadline, and proxy profile with explicit rotation.
/// </summary>
public sealed class SecureTransportProfile : IDisposable
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private TransportSnapshot? _snapshot;
    private bool _disposed;

    public required string CaBundlePath { get; init; }

    public required string ClientCertificatePath { get; init; }

    public required string PrivateKeyPath { get; init; }

    public Func<string?>? TokenSource { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public SecureProxyMode ProxyMode { get; init; } = SecureProxyMode.Direct;

    public Uri? ProxyUri { get; init; }

    internal HttpClient Client()
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            _snapshot ??= CreateSnapshot();
            return _snapshot.Client;
        }
    }

    /// <summary>Atomically replace the cached client after rotating credential files.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            EnsureNotDisposed();
        }

        var next = CreateSnapshot();
        TransportSnapshot? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                next.Dispose();
                throw new ObjectDisposedException(nameof(SecureTransportProfile));
            }

            previous = _snapshot;
            _snapshot = next;
        }

        previous?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        TransportSnapshot? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            previous = _snapshot;
            _snapshot = null;
        }

        previous?.Dispose();
    }

    private TransportSnapshot CreateSnapshot()
    {
        Validate();
        X509Certificate2? identity = null;
        X509Certificate2Collection? roots = null;
        SocketsHttpHandler? handler = null;
        try
        {
            identity =
                X509Certificate2.CreateFromPemFile(ClientCertificatePath, PrivateKeyPath);
            roots = new X509Certificate2Collection();
            roots.ImportFromPem(File.ReadAllText(CaBundlePath));
            if (roots.Count == 0)
            {
                throw new ClavenarConfigException(
                    "secure transport CA bundle contains no certificates");
            }

            var clientCertificates = new X509CertificateCollection { identity };
            handler = new SocketsHttpHandler
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
            var client = new HttpClient(handler, disposeHandler: true) { Timeout = RequestTimeout };
            var snapshot = new TransportSnapshot(client, identity, roots);
            handler = null;
            identity = null;
            roots = null;
            return snapshot;
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
        finally
        {
            handler?.Dispose();
            identity?.Dispose();
            if (roots is not null)
            {
                foreach (var root in roots)
                {
                    root.Dispose();
                }
            }
        }
    }

    internal string? Token()
    {
        EnsureNotDisposed();
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

        if (token.Contains('\r') || token.Contains('\n'))
        {
            throw new ClavenarConfigException(
                "secure transport token source returned a multi-line token");
        }

        return token;
    }

    internal void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero
            || RequestTimeout <= TimeSpan.Zero
            || ConnectTimeout > MaxTimeout
            || RequestTimeout > MaxTimeout)
        {
            throw new ClavenarConfigException(
                "secure transport timeouts must be positive and no greater than 5 minutes");
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
                || (ProxyUri.Scheme != Uri.UriSchemeHttp && ProxyUri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(ProxyUri.UserInfo)
                || !string.IsNullOrEmpty(ProxyUri.Query)
                || !string.IsNullOrEmpty(ProxyUri.Fragment)))
        {
            throw new ClavenarConfigException(
                "secure transport explicit proxy must use an absolute HTTP(S) URL");
        }

        if (ProxyMode != SecureProxyMode.Explicit && ProxyUri is not null)
        {
            throw new ClavenarConfigException(
                "secure transport ProxyUri is valid only in Explicit mode");
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

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class TransportSnapshot(
        HttpClient client,
        X509Certificate2 identity,
        X509Certificate2Collection roots) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public void Dispose()
        {
            Client.Dispose();
            identity.Dispose();
            foreach (var root in roots)
            {
                root.Dispose();
            }
        }
    }
}
