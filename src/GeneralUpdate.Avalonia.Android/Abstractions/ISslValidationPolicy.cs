using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace GeneralUpdate.Avalonia.Android.Abstractions;

/// <summary>
/// Provides custom SSL/TLS certificate validation logic.
/// Used to configure <see cref="System.Net.Http.HttpClientHandler.ServerCertificateCustomValidationCallback"/>.
/// </summary>
public interface ISslValidationPolicy
{
    bool ValidateCertificate(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors);
}
