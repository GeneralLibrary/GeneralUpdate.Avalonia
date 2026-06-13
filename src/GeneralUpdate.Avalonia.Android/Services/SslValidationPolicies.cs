using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using GeneralUpdate.Avalonia.Android.Abstractions;

namespace GeneralUpdate.Avalonia.Android.Services;

/// <summary>
/// Strict SSL validation policy: only accepts certificates with no policy errors.
/// This is the default and recommended policy for production use.
/// </summary>
public sealed class StrictSslValidationPolicy : ISslValidationPolicy
{
    public bool ValidateCertificate(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
        => sslPolicyErrors == SslPolicyErrors.None;
}

/// <summary>
/// Permissive SSL validation policy: accepts all certificates regardless of errors.
/// WARNING: This bypasses certificate validation and should ONLY be used
/// in development/testing environments with self-signed certificates.
/// Never use this in production.
/// </summary>
public sealed class AllowAllSslValidationPolicy : ISslValidationPolicy
{
    public bool ValidateCertificate(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
        => true;
}
