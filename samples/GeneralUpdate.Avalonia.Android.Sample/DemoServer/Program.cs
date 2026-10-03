using System.Security.Cryptography;

const string expectedAppKey = "demo-client";
const string expectedProductId = "demo-product";
const int expectedPlatform = 3;

var apkArgumentIndex = Array.IndexOf(args, "--apk");
if (apkArgumentIndex < 0 || apkArgumentIndex == args.Length - 1)
{
    throw new ArgumentException("Use --apk <path> to specify the version 2 APK.");
}

var apkPath = Path.GetFullPath(args[apkArgumentIndex + 1]);
if (!File.Exists(apkPath))
{
    throw new FileNotFoundException("The version 2 APK was not found.", apkPath);
}

var apkInfo = new FileInfo(apkPath);
var apkSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(apkPath))).ToLowerInvariant();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5080");

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    version = "2.0.0",
    apkInfo.Length,
    sha256 = apkSha256
}));

app.MapPost("/Upgrade/Verification", (VerificationRequest request, HttpRequest httpRequest) =>
{
    if (!string.Equals(request.AppKey, expectedAppKey, StringComparison.Ordinal) ||
        !string.Equals(request.ProductId, expectedProductId, StringComparison.Ordinal) ||
        request.AppType != 1 ||
        request.Platform != expectedPlatform)
    {
        return Results.Json(new
        {
            code = 400,
            message = "The demo AppKey, ProductId, AppType or Platform is invalid.",
            body = Array.Empty<object>()
        });
    }

    var downloadUrl =
        $"{httpRequest.Scheme}://{httpRequest.Host}/packages/generalupdate-sample-v2.apk";

    return Results.Json(new
    {
        code = 200,
        body = new[]
        {
            new
            {
                version = "2.0.0",
                name = "GeneralUpdate Android Demo 2.0",
                updateLog = "演示升级：从 1.0.0 自动下载并安装 2.0.0。",
                url = downloadUrl,
                hash = apkSha256,
                size = apkInfo.Length,
                releaseDate = DateTimeOffset.UtcNow,
                isForcibly = false,
                isFreeze = false,
                packageType = 2,
                format = "apk"
            }
        }
    });
});

app.MapMethods("/packages/generalupdate-sample-v2.apk", ["GET", "HEAD"], () =>
    Results.File(
        apkPath,
        "application/vnd.android.package-archive",
        "generalupdate-sample-v2.apk",
        enableRangeProcessing: true));

Console.WriteLine("GeneralSpacestation demo endpoint: http://127.0.0.1:5080/Upgrade/Verification");
Console.WriteLine($"Serving APK: {apkPath}");
Console.WriteLine($"SHA-256: {apkSha256}");

app.Run();

internal sealed record VerificationRequest(
    string Version,
    string AppKey,
    int AppType,
    int Platform,
    string ProductId);
