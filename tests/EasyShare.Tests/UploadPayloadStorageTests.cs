using System.Security.Cryptography;
using System.Text;
using EasyShare.Models;
using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class UploadPayloadStorageTests
{
    [Fact]
    public void DpapiProtectorBindsKeyMaterialToTheCurrentWindowsUser()
    {
        var protector = new DpapiUserDataProtector();
        var plaintext = RandomNumberGenerator.GetBytes(32);
        var protectedData = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, protectedData);
        Assert.Equal(plaintext, protector.Unprotect(protectedData));
    }

    [Fact]
    public async Task StoresAndReadsAuthenticatedChunksWithoutPlaintextOnDisk()
    {
        using var environment = new TestDirectory();
        var storage = CreateStorage(environment);
        var path = storage.CreatePayloadPath();
        var marker = Encoding.UTF8.GetBytes("PLAINTEXT-MARKER-DO-NOT-PERSIST");
        var payload = RandomNumberGenerator.GetBytes(220_000);
        marker.CopyTo(payload, 70_000);

        var info = await storage.StoreAsync(path, payload);
        var encrypted = await File.ReadAllBytesAsync(path);

        Assert.True(info.ChunkCount >= 4);
        Assert.True(encrypted.Length > payload.Length);
        Assert.False(ContainsSequence(encrypted, marker));
        Assert.Equal(payload, await storage.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task RejectsTamperedCiphertext()
    {
        using var environment = new TestDirectory();
        var storage = CreateStorage(environment);
        var path = storage.CreatePayloadPath();
        await storage.StoreAsync(path, RandomNumberGenerator.GetBytes(150_000));
        var encrypted = await File.ReadAllBytesAsync(path);
        encrypted[encrypted.Length / 2] ^= 0x40;
        await File.WriteAllBytesAsync(path, encrypted);

        await Assert.ThrowsAsync<InvalidEncryptedPayloadException>(
            () => storage.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task QuotaFailureLeavesNoPayloadOrTemporaryFile()
    {
        using var environment = new TestDirectory();
        var storage = CreateStorage(environment, new UploadPayloadStorageOptions
        {
            ChunkSizeBytes = 64 * 1024,
            MaxPayloadBytes = 1024 * 1024,
            MaxTotalBytes = 100_000
        });
        var path = storage.CreatePayloadPath();

        await Assert.ThrowsAsync<PayloadQuotaExceededException>(
            () => storage.StoreAsync(path, new byte[150_000]));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(storage.StorageDirectory, "*.tmp"));
    }

    [Fact]
    public async Task MigratesLegacyPlaintextWithAnAtomicEncryptedReplacement()
    {
        using var environment = new TestDirectory();
        var storage = CreateStorage(environment);
        var path = storage.CreatePayloadPath();
        var payload = Encoding.UTF8.GetBytes("legacy queue payload");
        await File.WriteAllBytesAsync(path, payload);

        Assert.True(await storage.MigrateLegacyPayloadAsync(path));
        Assert.True(await storage.IsEncryptedAsync(path));
        Assert.Equal(payload, await storage.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(storage.StorageDirectory, "*.tmp"));
    }

    [Fact]
    public async Task CleanupRetainsReferencedFilesAndDeletesExpiredOrphans()
    {
        using var environment = new TestDirectory();
        var storage = CreateStorage(environment, new UploadPayloadStorageOptions
        {
            ChunkSizeBytes = 64 * 1024,
            MaxPayloadBytes = 1024 * 1024,
            MaxTotalBytes = 4 * 1024 * 1024,
            OrphanRetention = TimeSpan.Zero,
            TemporaryFileRetention = TimeSpan.Zero
        });
        var retained = storage.CreatePayloadPath();
        var orphan = storage.CreatePayloadPath();
        await storage.StoreAsync(retained, new byte[100]);
        await storage.StoreAsync(orphan, new byte[100]);

        var result = await storage.CleanupAsync(
            [retained, "\0malformed"],
            DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.True(File.Exists(retained));
        Assert.False(File.Exists(orphan));
        Assert.Equal(1, result.OrphanFilesDeleted);
    }

    [Fact]
    public async Task ReusableEncryptedFileStoreSupportsIndependentCacheRoots()
    {
        using var environment = new TestDirectory();
        var cacheRoot = Path.Combine(environment.Root, "offline-cache");
        var store = new EncryptedFileStore(
            cacheRoot,
            Path.Combine(environment.Root, "offline-cache.key"),
            new UploadPayloadStorageOptions
            {
                ChunkSizeBytes = 64 * 1024,
                MaxPayloadBytes = 1024 * 1024,
                MaxTotalBytes = 4 * 1024 * 1024
            },
            new TestUserDataProtector(),
            ".cache");
        var path = store.CreateFilePath();

        await store.StoreAsync(path, Encoding.UTF8.GetBytes("offline content"));

        Assert.EndsWith(".cache", path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("offline content", Encoding.UTF8.GetString(await store.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task OpenReadStreamsWithVerifiedLengthAndSupportsReplayFromStart()
    {
        using var environment = new TestDirectory();
        var storage = CreateStorage(environment);
        var path = storage.CreatePayloadPath();
        var payload = RandomNumberGenerator.GetBytes(300_000);
        await storage.StoreAsync(path, payload);

        await using var plaintext = await storage.OpenReadAsync(path);
        Assert.True(plaintext.CanRead);
        Assert.True(plaintext.CanSeek);
        Assert.Equal(payload.Length, plaintext.Length);

        using var firstRead = new MemoryStream();
        await plaintext.CopyToAsync(firstRead);
        Assert.Equal(payload, firstRead.ToArray());

        plaintext.Position = 0;
        using var replay = new MemoryStream();
        await plaintext.CopyToAsync(replay);
        Assert.Equal(payload, replay.ToArray());
    }

    private static UploadPayloadStorage CreateStorage(
        TestDirectory environment,
        UploadPayloadStorageOptions? options = null) =>
        new(
            new AppDataPaths(Path.Combine(environment.Root, "data"), Path.Combine(environment.Root, "machine.json")),
            options ?? new UploadPayloadStorageOptions
            {
                ChunkSizeBytes = 64 * 1024,
                MaxPayloadBytes = 4 * 1024 * 1024,
                MaxTotalBytes = 16 * 1024 * 1024
            },
            new TestUserDataProtector());

    private static bool ContainsSequence(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;

    private sealed class TestUserDataProtector : IUserDataProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            var result = new byte[plaintext.Length + 1];
            result[0] = 0xA5;
            for (var index = 0; index < plaintext.Length; index++)
            {
                result[index + 1] = (byte)(plaintext[index] ^ 0x5A);
            }

            return result;
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
        {
            if (protectedData.Length < 1 || protectedData[0] != 0xA5)
            {
                throw new CryptographicException("Invalid test protection envelope.");
            }

            var result = new byte[protectedData.Length - 1];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = (byte)(protectedData[index + 1] ^ 0x5A);
            }

            return result;
        }
    }
}

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "EasyShare.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Test cleanup is best effort on antivirus-scanned Windows runners.
        }
    }
}
