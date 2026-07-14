using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EasyShare.Models;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class DiagnosticsAndSupportBundleTests
{
    [Fact]
    public void RedactorRemovesCredentialsAndPersonalIdentifiers()
    {
        var redactor = new SensitiveDataRedactor();
        const string raw = "Authorization: Bearer abc.def.ghi access_token=opaque123 " +
                           "user@example.com C:\\Users\\Alice\\file.txt " +
                           "https://login.example/callback?code=auth-code&state=ok 192.168.10.20\n" +
                           "Cookie: FedAuth=secret";

        var redacted = redactor.Redact(raw);

        Assert.DoesNotContain("opaque123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("FedAuth=secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("\\Alice\\", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auth-code", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.10.20", redacted, StringComparison.Ordinal);
        Assert.Contains(SensitiveDataRedactor.RedactedValue, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredLogsRotateWithinBoundsAndStayRedacted()
    {
        using var environment = new TestDirectory();
        var log = new RotatingDiagnosticLog(
            Path.Combine(environment.Root, "Logs", "startup.log"),
            new DiagnosticLogOptions
            {
                MaxFileBytes = 64 * 1024,
                MaxArchiveFiles = 2,
                Retention = TimeSpan.FromDays(1),
                MaxEventCharacters = 20_000
            });

        for (var index = 0; index < 12; index++)
        {
            log.Write(DiagnosticEvent.Create(
                DiagnosticLevel.Error,
                "test.event",
                $"password=super-secret user{index}@example.com {new string('x', 10_000)}",
                new InvalidOperationException("access_token=exception-secret")));
        }

        var files = log.GetLogFiles();
        Assert.InRange(files.Count, 2, 3);
        foreach (var path in files)
        {
            Assert.True(new FileInfo(path).Length <= 64 * 1024);
            foreach (var line in File.ReadLines(path))
            {
                using var _ = JsonDocument.Parse(line);
                Assert.DoesNotContain("super-secret", line, StringComparison.Ordinal);
                Assert.DoesNotContain("exception-secret", line, StringComparison.Ordinal);
                Assert.DoesNotContain("@example.com", line, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void OversizedDiagnosticEventIsTruncatedBelowTheFileBound()
    {
        using var environment = new TestDirectory();
        var path = Path.Combine(environment.Root, "Logs", "startup.log");
        var log = new RotatingDiagnosticLog(
            path,
            new DiagnosticLogOptions
            {
                MaxFileBytes = 64 * 1024,
                MaxArchiveFiles = 0,
                MaxEventCharacters = 128 * 1024
            });

        log.Write(DiagnosticEvent.Create(
            DiagnosticLevel.Information,
            "oversized",
            new string('\u2603', 100_000)));

        Assert.True(new FileInfo(path).Length <= 64 * 1024);
        using var _ = JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public async Task SupportBundleUsesAllowlistAndRedactsLogsAndMetadata()
    {
        using var environment = new TestDirectory();
        var paths = new AppDataPaths(
            Path.Combine(environment.Root, "data"),
            Path.Combine(environment.Root, "machine-policy.json"));
        paths.EnsureLogDirectoryCreated();
        var log = new RotatingDiagnosticLog(Path.Combine(paths.LogDirectory, "startup.log"));
        log.Write(DiagnosticEvent.Create(
            DiagnosticLevel.Error,
            "support.test",
            "access_token=log-secret someone@example.com"));

        Directory.CreateDirectory(paths.UploadQueueDirectory);
        await File.WriteAllTextAsync(Path.Combine(paths.UploadQueueDirectory, "private.upload"), "PAYLOAD-MARKER");
        await File.WriteAllTextAsync(paths.TokenCachePath, "TOKEN-CACHE-MARKER");
        Directory.CreateDirectory(paths.BrowserProfilePath);
        await File.WriteAllTextAsync(Path.Combine(paths.BrowserProfilePath, "Cookies"), "COOKIE-MARKER");

        var destination = Path.Combine(environment.Root, "support.zip");
        var service = new SupportBundleService(paths, log);
        var result = await service.CreateAsync(
            destination,
            new SupportBundleContext(Metadata: new Dictionary<string, string?>
            {
                ["account"] = "owner@example.com",
                ["accessToken"] = "metadata-secret"
            }));

        Assert.True(result.Succeeded, result.Error);
        using var archive = ZipFile.OpenRead(destination);
        Assert.All(archive.Entries, entry =>
            Assert.True(entry.FullName == "manifest.json" || entry.FullName.StartsWith("logs/", StringComparison.Ordinal)));
        var combined = new StringBuilder();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            combined.Append(await reader.ReadToEndAsync());
        }

        var text = combined.ToString();
        Assert.DoesNotContain("log-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("someone@example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("owner@example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PAYLOAD-MARKER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN-CACHE-MARKER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("COOKIE-MARKER", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnterprisePolicyCanDisableSupportBundles()
    {
        using var environment = new TestDirectory();
        var paths = new AppDataPaths(Path.Combine(environment.Root, "data"));
        var snapshot = new EnterprisePolicySnapshot(
            new EnterprisePolicy { SupportBundlesAllowed = false },
            true,
            [EnterprisePolicySource.Defaults, EnterprisePolicySource.LocalMachine],
            Array.Empty<EnterprisePolicyIssue>());

        var result = await new SupportBundleService(paths).CreateAsync(
            Path.Combine(environment.Root, "blocked.zip"),
            new SupportBundleContext(snapshot));

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(environment.Root, "blocked.zip")));
    }
}
