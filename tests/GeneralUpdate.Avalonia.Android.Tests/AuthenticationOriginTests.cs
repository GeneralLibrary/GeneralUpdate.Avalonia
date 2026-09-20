using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GeneralUpdate.Avalonia.Android.Abstractions;
using GeneralUpdate.Avalonia.Android.Enums;
using GeneralUpdate.Avalonia.Android.Models;
using GeneralUpdate.Avalonia.Android.Services;
using Xunit;

namespace GeneralUpdate.Avalonia.Android.Tests;

public sealed class AuthenticationOriginTests
{
    private const string VerificationUrl = "https://updates.example/verify";

    [Theory]
    [InlineData("https://updates.example/app.apk", true)]
    [InlineData("https://UPDATES.example:443/packages/app.apk", true)]
    [InlineData("http://updates.example/app.apk", false)]
    [InlineData("https://updates.example:444/app.apk", false)]
    [InlineData("https://updates.example.attacker.invalid/app.apk", false)]
    [InlineData("https://cdn.example/app.apk", false)]
    [InlineData("https://user@updates.example/app.apk", false)]
    public async Task Downloads_GlobalCredentialsAreLimitedToVerificationOrigin(string url, bool authenticated)
    {
        await AssertDownloadAuthenticationAsync(url, VerificationUrl, authenticated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/verify")]
    [InlineData("file:///verify")]
    public async Task Downloads_MissingOrInvalidTrustedOriginFailsClosed(string? verificationUrl)
    {
        await AssertDownloadAuthenticationAsync("https://cdn.example/app.apk", verificationUrl, false);
    }

    [Theory]
    [InlineData(VerificationUrl)]
    [InlineData(null)]
    public async Task Downloads_ExplicitCdnOptInAllowsGlobalCredentials(string? verificationUrl)
    {
        await AssertDownloadAuthenticationAsync("https://cdn.example/app.apk", verificationUrl, true,
            configure: options => options with
            {
                AllowedDownloadAuthenticationOrigins = new[] { new Uri("https://cdn.example/") }
            });
    }

    [Theory]
    [InlineData("http://cdn.example/app.apk")]
    [InlineData("https://cdn.example:444/app.apk")]
    [InlineData("https://cdn.example.attacker.invalid/app.apk")]
    public async Task Downloads_CdnOptInIsExactOriginOnly(string url)
    {
        await AssertDownloadAuthenticationAsync(url, VerificationUrl, false,
            configure: options => options with
            {
                AllowedDownloadAuthenticationOrigins = new[] { new Uri("https://cdn.example/") }
            });
    }

    [Fact]
    public async Task Downloads_ExplicitTrustedOriginWorksWithoutVerificationUrl()
    {
        await AssertDownloadAuthenticationAsync("https://cdn.example/app.apk", null, true,
            configure: options => options with
            {
                TrustedAuthenticationOrigin = new Uri("https://cdn.example/")
            });
    }

    [Fact]
    public async Task Downloads_InvalidExplicitOriginDoesNotFallBackToVerificationOrigin()
    {
        await AssertDownloadAuthenticationAsync("https://updates.example/app.apk", VerificationUrl, false,
            configure: options => options with
            {
                TrustedAuthenticationOrigin = new Uri("relative", UriKind.Relative)
            });
    }

    [Theory]
    [InlineData("https://updates.example/app.apk", true)]
    [InlineData("https://cdn.example/app.apk", false)]
    public async Task Downloads_MissingPackageCredentialsOnlyFallBackAtTrustedOrigin(string url, bool authenticated)
    {
        await AssertDownloadAuthenticationAsync(url, VerificationUrl, authenticated, packageScheme: AuthScheme.ApiKey);
    }

    [Theory]
    [InlineData("https://cdn.example/app.apk")]
    [InlineData("https://updates.example/app.apk")]
    public async Task Downloads_PerPackageCredentialsTakePrecedenceAtInitialPackageOrigin(string url)
    {
        await AssertDownloadAuthenticationAsync(url, VerificationUrl, false,
            packageScheme: AuthScheme.ApiKey, packageToken: Guid.NewGuid().ToString("N"));
    }

    [Theory]
    [InlineData("https://updates.example/verify", true)]
    [InlineData("https://cdn.example/verify", false)]
    public async Task Verification_ExplicitOriginLimitsGlobalAuthentication(string url, bool authenticated)
    {
        var credential = Guid.NewGuid().ToString("N");
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            Assert.Equal(authenticated, request.Headers.Contains("X-Custom-Auth"));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        using var client = new HttpUpdatePackageClient(http,
            new ApiKeyAuthProvider(credential, "X-Custom-Auth"),
            trustedAuthenticationOrigin: new Uri(VerificationUrl));

        Assert.Null(await client.GetPackageInfoAsync(url));
    }

    [Fact]
    public void InternalHandler_DisablesAutomaticRedirects()
    {
        using var handler = new HttpDownloadOptions().BuildHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_PlaintextAuthRequiresOptInBeforeProviderOrNetwork(bool allowInsecureAuthentication)
    {
        var provider = new RecordingAuthProvider();
        var networkCalls = 0;
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            networkCalls++;
            Assert.True(request.Headers.Contains("X-Custom-Auth"));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        using var client = new HttpUpdatePackageClient(http, provider,
            allowInsecureAuthentication: allowInsecureAuthentication);

        var request = client.GetPackageInfoAsync("http://updates.example/verify");
        if (allowInsecureAuthentication)
        {
            Assert.Null(await request);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => request);
        }
        Assert.Equal(allowInsecureAuthentication ? 1 : 0, provider.Calls);
        Assert.Equal(allowInsecureAuthentication ? 1 : 0, networkCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Downloads_PlaintextAuthRequiresOptInForGlobalAndPackageCredentials(
        bool packageCredentials, bool allowInsecureAuthentication)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".auth-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var provider = new RecordingAuthProvider();
            var networkCalls = 0;
            using var http = new HttpClient(new DelegateHandler(request =>
            {
                networkCalls++;
                Assert.True(request.Headers.Contains(packageCredentials ? "X-Api-Key" : "X-Custom-Auth"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 42 })
                };
            }));
            using var downloader = new HttpResumableApkDownloader(http, new PhysicalFileStorage(),
                new AndroidUpdateOptions
                {
                    DownloadDirectoryPath = directory,
                    UpdateServer = new UpdateServerOptions { RequestUrl = "http://updates.example/verify" }
                },
                new HttpDownloadOptions
                {
                    AuthProvider = provider,
                    AllowInsecureAuthentication = allowInsecureAuthentication,
                    MaxRetryAttempts = 1
                }, ownsClient: false);
            var result = await downloader.DownloadAsync(Package("http://updates.example/app.apk") with
            {
                AuthScheme = packageCredentials ? AuthScheme.ApiKey : null,
                AuthToken = packageCredentials ? Guid.NewGuid().ToString("N") : null
            }, null);

            Assert.Equal(allowInsecureAuthentication, result.Success);
            Assert.Equal(allowInsecureAuthentication ? 2 : 0, networkCalls);
            Assert.Equal(allowInsecureAuthentication && !packageCredentials ? 2 : 0, provider.Calls);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task InternalVerificationClient_DoesNotForwardCustomAuthOnRedirect(
        bool useOptions, bool sameOrigin, bool usePost)
    {
        using var source = StartListener();
        using var target = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sourceUrl = ListenerUrl(source);
        var targetUrl = sameOrigin ? "/target" : ListenerUrl(target);
        var credential = Guid.NewGuid().ToString("N");
        using var client = HttpUpdatePackageClient.Create(null,
            useOptions ? new HttpDownloadOptions
            {
                AuthProvider = new ApiKeyAuthProvider(credential, "X-Custom-Auth"),
                AllowInsecureAuthentication = true
            } : null,
            new SystemVersionComparer());

        var responseTask = RespondWithRedirectAsync(source, targetUrl, timeout.Token);
        var requestTask = usePost
            ? client.GetPackageInfoAsync(sourceUrl, new UpdatePackageRequest
            {
                Version = "1.0.0", AppKey = "test-app", Platform = 42, ProductId = "test-product"
            }, timeout.Token)
            : client.GetPackageInfoAsync(sourceUrl, timeout.Token);
        var incomingHeaders = await responseTask;
        await Assert.ThrowsAsync<HttpRequestException>(() => requestTask);

        Assert.Equal(useOptions, incomingHeaders.Contains("X-Custom-Auth: " + credential));
        Assert.StartsWith(usePost ? "POST " : "GET ", incomingHeaders);
        Assert.False(source.Pending());
        Assert.False(target.Pending());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InternalDownloader_DoesNotForwardCustomAuthOnRedirect(bool redirectHead)
    {
        using var source = StartListener();
        using var target = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sourceUrl = ListenerUrl(source);
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".auth-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var downloader = new HttpResumableApkDownloader(new PhysicalFileStorage(),
                new AndroidUpdateOptions
                {
                    DownloadDirectoryPath = directory,
                    UpdateServer = new UpdateServerOptions { RequestUrl = sourceUrl }
                },
                new HttpDownloadOptions
                {
                    AuthProvider = new ApiKeyAuthProvider(Guid.NewGuid().ToString("N"), "X-Custom-Auth"),
                    AllowInsecureAuthentication = true,
                    MaxRetryAttempts = 1
                });
            var requests = new List<string>();
            using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            async Task ServeRedirectsAsync()
            {
                try
                {
                    while (true)
                    {
                        requests.Add(await RespondWithRedirectAsync(source, ListenerUrl(target),
                            serverCancellation.Token, redirect: redirectHead || requests.Count > 0));
                    }
                }
                catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested)
                {
                }
            }
            var serverTask = ServeRedirectsAsync();
            var result = await downloader.DownloadAsync(Package(sourceUrl + "app.apk"), null, timeout.Token);
            await serverCancellation.CancelAsync();
            await serverTask;

            Assert.False(result.Success);
            Assert.NotEmpty(requests);
            Assert.Equal(redirectHead ? 1 : 2, requests.Count);
            Assert.StartsWith(redirectHead ? "HEAD " : "GET ", requests[^1]);
            Assert.All(requests, headers => Assert.Contains("X-Custom-Auth:", headers));
            Assert.False(target.Pending());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task InternalVerificationClient_DoesNotFollowHttpsToHttpDowngrade()
    {
        using var source = StartListener();
        using var target = StartListener();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var credential = Guid.NewGuid().ToString("N");
        using var client = HttpUpdatePackageClient.Create(null, new HttpDownloadOptions
        {
            AuthProvider = new ApiKeyAuthProvider(credential, "X-Custom-Auth"),
            SslValidationPolicy = new TestCertificatePolicy(certificate.Thumbprint)
        }, new SystemVersionComparer());
        async Task<string> ServeHttpsAsync()
        {
            using var connection = await source.AcceptTcpClientAsync(timeout.Token);
            await using var stream = new SslStream(connection.GetStream());
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate
            }, timeout.Token);
            return await WriteResponseAsync(stream, ListenerUrl(target), timeout.Token);
        }

        var responseTask = ServeHttpsAsync();
        var requestTask = client.GetPackageInfoAsync(
            ListenerUrl(source).Replace("http://", "https://"), timeout.Token);
        var headers = await responseTask;
        await Assert.ThrowsAsync<HttpRequestException>(() => requestTask);

        Assert.Contains("X-Custom-Auth: " + credential, headers);
        Assert.False(target.Pending());
    }

    private static async Task AssertDownloadAuthenticationAsync(
        string url, string? verificationUrl, bool authenticated,
        Func<HttpDownloadOptions, HttpDownloadOptions>? configure = null,
        AuthScheme? packageScheme = null, string? packageToken = null)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), ".auth-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var globalCredential = Guid.NewGuid().ToString("N");
            var methods = new List<HttpMethod>();
            using var http = new HttpClient(new DelegateHandler(request =>
            {
                methods.Add(request.Method);
                Assert.Equal(authenticated, request.Headers.Contains("X-Custom-Auth"));
                if (authenticated)
                {
                    Assert.Equal(globalCredential, Assert.Single(request.Headers.GetValues("X-Custom-Auth")));
                }
                Assert.Equal(packageToken is not null, request.Headers.Contains("X-Api-Key"));
                if (packageToken is not null)
                {
                    Assert.Equal(packageToken, Assert.Single(request.Headers.GetValues("X-Api-Key")));
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 42 })
                };
            }));
            var httpOptions = new HttpDownloadOptions
            {
                AuthProvider = new ApiKeyAuthProvider(globalCredential, "X-Custom-Auth"),
                MaxRetryAttempts = 1
            };
            using var downloader = new HttpResumableApkDownloader(http, new PhysicalFileStorage(),
                new AndroidUpdateOptions
                {
                    DownloadDirectoryPath = directory,
                    UpdateServer = verificationUrl is null ? null : new UpdateServerOptions { RequestUrl = verificationUrl }
                }, configure?.Invoke(httpOptions) ?? httpOptions, ownsClient: false);

            var result = await downloader.DownloadAsync(Package(url) with
            {
                AuthScheme = packageScheme,
                AuthToken = packageToken
            }, null);

            Assert.True(result.Success, result.Message + " " + result.Exception);
            Assert.Contains(HttpMethod.Head, methods);
            Assert.Contains(HttpMethod.Get, methods);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static UpdatePackageInfo Package(string url) => new()
    {
        Version = "2.0.0",
        DownloadUrl = url,
        FileName = "app.apk",
        FileSize = 1,
        Sha256 = new string('a', 64)
    };

    private static TcpListener StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static string ListenerUrl(TcpListener listener)
        => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

    private static async Task<string> RespondWithRedirectAsync(
        TcpListener listener, string destination, CancellationToken cancellationToken, bool redirect = true)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        return await WriteResponseAsync(stream, destination, cancellationToken, redirect);
    }

    private static async Task<string> WriteResponseAsync(
        Stream stream, string destination, CancellationToken cancellationToken, bool redirect = true)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var headers = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line)
        {
            headers.AppendLine(line);
        }
        var response = Encoding.ASCII.GetBytes(redirect
            ? $"HTTP/1.1 302 Found\r\nLocation: {destination}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            : "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        return headers.ToString();
    }

    private sealed class TestCertificatePolicy(string thumbprint) : ISslValidationPolicy
    {
        public bool ValidateCertificate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
            => certificate?.Thumbprint == thumbprint;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private sealed class RecordingAuthProvider : IHttpAuthProvider
    {
        public int Calls { get; private set; }

        public Task ApplyAuthAsync(HttpRequestMessage request, CancellationToken token = default)
        {
            Calls++;
            request.Headers.Add("X-Custom-Auth", Guid.NewGuid().ToString("N"));
            return Task.CompletedTask;
        }
    }
}
