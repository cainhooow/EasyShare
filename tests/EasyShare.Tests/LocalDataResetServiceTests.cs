using EasyShare.Services;
using Xunit;

namespace EasyShare.Tests;

public sealed class LocalDataResetServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"EasyShareResetTests-{Guid.NewGuid():N}");

    [Fact]
    public async Task InventoryAndResetCoverEveryManagedCategory()
    {
        var paths = CreatePaths();
        Write(paths.DatabasePath, new byte[11]);
        Write(paths.TokenCachePath, new byte[12]);
        Write(Path.Combine(paths.BrowserProfilePath, "Cookies"), new byte[13]);
        Write(Path.Combine(paths.UploadQueueDirectory, "pending.upload"), new byte[14]);
        Write(paths.UploadPayloadKeyPath, new byte[15]);
        Write(Path.Combine(paths.OfflineCacheDirectory, "item.offline"), new byte[16]);
        Write(paths.OfflineCacheKeyPath, new byte[17]);
        Write(Path.Combine(paths.LogDirectory, "startup.log"), new byte[18]);
        Write(Path.Combine(paths.PackageWebViewProfilePath!, "Default", "Network", "Cookies"), new byte[19]);
        Write(Path.Combine(paths.PackageWebViewProfilePath!, "Default", "History"), new byte[20]);

        var service = new LocalDataResetService(paths);
        var inventory = await service.InventoryAsync();
        var result = await service.ResetAsync();

        Assert.Equal(10, inventory.ItemCount);
        Assert.Equal(155, inventory.Bytes);
        Assert.Contains(inventory.Categories, category => category.Name == "Conta e sessão do navegador");
        Assert.Contains(
            inventory.Categories,
            category => category.Name == "Conta e sessão do navegador (perfil legado do pacote)");
        Assert.Contains(inventory.Categories, category => category.Name == "Fila e envios locais");
        Assert.Contains(inventory.Categories, category => category.Name == "Arquivos disponíveis offline");
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.After.ItemCount);
        Assert.Equal(0, result.After.Bytes);
        Assert.False(Directory.Exists(paths.DataDirectory));
        Assert.False(Directory.Exists(paths.PackageWebViewProfilePath));
        Assert.False(File.Exists(service.PendingMarkerPath));
    }

    [Fact]
    public async Task ResetClearsEntirePackageLocalStateWithoutTouchingItsParentSiblings()
    {
        var paths = CreatePaths();
        var cookies = Path.Combine(paths.PackageWebViewProfilePath!, "Default", "Network", "Cookies");
        var history = Path.Combine(paths.PackageWebViewProfilePath!, "Default", "History");
        var cache = Path.Combine(paths.PackageWebViewProfilePath!, "Default", "Cache", "entry.data");
        var localStateSibling = Path.Combine(paths.PackageLocalStatePath!, "future-residue.json");
        var packageParentSibling = Path.Combine(
            Path.GetDirectoryName(paths.PackageLocalStatePath)!,
            "keep.txt");
        Write(paths.DatabasePath, new byte[10]);
        Write(cookies, new byte[11]);
        Write(history, new byte[12]);
        Write(cache, new byte[13]);
        Write(localStateSibling, new byte[14]);
        Write(packageParentSibling, new byte[15]);
        var service = new LocalDataResetService(paths);

        var before = await service.InventoryAsync();
        var result = await service.ResetAsync();

        Assert.Equal(5, before.ItemCount);
        var legacyCategory = Assert.Single(
            before.Categories,
            category => category.Name == "Conta e sessão do navegador (perfil legado do pacote)");
        Assert.Equal(3, legacyCategory.ItemCount);
        Assert.Equal(36, legacyCategory.Bytes);
        var packageCategory = Assert.Single(
            before.Categories,
            category => category.Name == "Outros dados locais do pacote");
        Assert.Equal(1, packageCategory.ItemCount);
        Assert.Equal(14, packageCategory.Bytes);
        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(paths.DataDirectory));
        Assert.False(Directory.Exists(paths.PackageWebViewProfilePath));
        Assert.False(File.Exists(localStateSibling));
        Assert.True(Directory.Exists(paths.PackageLocalStatePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.PackageLocalStatePath!));
        Assert.Equal(new byte[15], await File.ReadAllBytesAsync(packageParentSibling));
    }

    [Fact]
    public async Task ASecondResetIsSafeAndDoesNotCreateDirectoriesOrKeys()
    {
        var paths = CreatePaths();
        Write(paths.UploadPayloadKeyPath, [1, 2, 3]);
        var service = new LocalDataResetService(paths);

        Assert.True((await service.ResetAsync()).Succeeded);
        var second = await service.ResetAsync();

        Assert.True(second.Succeeded);
        Assert.Equal(LocalDataInventory.Empty, second.Before);
        Assert.False(Directory.Exists(paths.DataDirectory));
        Assert.False(File.Exists(paths.UploadPayloadKeyPath));
        Assert.False(File.Exists(paths.OfflineCacheKeyPath));
    }

    [Fact]
    public async Task LockedFileProducesActionablePartialFailureAndPendingRetry()
    {
        var paths = CreatePaths();
        var lockedPath = Path.Combine(paths.UploadQueueDirectory, "locked.upload");
        Write(lockedPath, new byte[32]);
        var service = new LocalDataResetService(paths);

        LocalDataResetResult first;
        using (var lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = await service.ResetAsync();
            Assert.False(first.Succeeded);
            Assert.Contains(first.Failures, failure => failure.Path.EndsWith("locked.upload", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(service.PendingMarkerPath));
        }

        var retry = service.CompletePendingReset();
        Assert.True(retry.Succeeded);
        Assert.False(File.Exists(service.PendingMarkerPath));
        Assert.False(Directory.Exists(paths.DataDirectory));
    }

    [Fact]
    public async Task LockedLegacyPackageProfileProducesPendingRetryAndSafeRelativeFailure()
    {
        var paths = CreatePaths();
        var lockedPath = Path.Combine(
            paths.PackageWebViewProfilePath!,
            "Default",
            "Network",
            "Cookies");
        Write(lockedPath, new byte[32]);
        var service = new LocalDataResetService(paths);

        LocalDataResetResult first;
        using (var lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = await service.ResetAsync();
            Assert.False(first.Succeeded);
            Assert.Contains(
                first.Failures,
                failure => failure.Path.StartsWith(
                    "PackageLocalState\\EBWebView",
                    StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                first.Failures,
                failure => failure.Path.Contains(_root, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(service.PendingMarkerPath));
        }

        var retry = service.CompletePendingResetOrThrow();
        Assert.True(retry.Succeeded);
        Assert.False(File.Exists(service.PendingMarkerPath));
        Assert.False(Directory.Exists(paths.PackageWebViewProfilePath));
    }

    [Fact]
    public void PendingResetFailureBlocksStartupUntilCleanupCanFinish()
    {
        var paths = CreatePaths();
        var lockedPath = Path.Combine(paths.UploadQueueDirectory, "startup-locked.upload");
        Write(lockedPath, new byte[32]);
        var service = new LocalDataResetService(paths);
        service.MarkPendingReset();

        using (var lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = Assert.Throws<IOException>(() => service.CompletePendingResetOrThrow());

            Assert.Contains("startup-locked.upload", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(service.PendingMarkerPath));
            Assert.True(File.Exists(lockedPath));
        }

        var completed = service.CompletePendingResetOrThrow();
        Assert.True(completed.Succeeded);
        Assert.False(File.Exists(service.PendingMarkerPath));
        Assert.False(Directory.Exists(paths.DataDirectory));
    }

    [Fact]
    public void LockedPendingMarkerBlocksStartupEvenAfterDataCleanupSucceeded()
    {
        var paths = CreatePaths();
        var service = new LocalDataResetService(paths);
        service.MarkPendingReset();

        using (var markerLock = new FileStream(
                   service.PendingMarkerPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var exception = Assert.Throws<IOException>(() => service.CompletePendingResetOrThrow());

            Assert.Contains(".reset.pending", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(service.PendingMarkerPath));
        }

        var completed = service.CompletePendingResetOrThrow();
        Assert.True(completed.Succeeded);
        Assert.False(File.Exists(service.PendingMarkerPath));
    }

    [Fact]
    public async Task RootReparsePointIsNotFollowedOrDeleted()
    {
        var paths = CreatePaths();
        var outsideTarget = Path.Combine(_root, "OutsideWebViewTarget");
        var outsideCookie = Path.Combine(outsideTarget, "EBWebView", "Default", "Network", "Cookies");
        Write(outsideCookie, new byte[41]);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PackageLocalStatePath!)!);
        Directory.CreateSymbolicLink(paths.PackageLocalStatePath!, outsideTarget);
        var service = new LocalDataResetService(paths);

        var result = await service.ResetAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures,
            failure => failure.Path == "PackageLocalState" &&
                       failure.Reason.Contains("link", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new byte[41], await File.ReadAllBytesAsync(outsideCookie));
        Assert.True(Directory.Exists(paths.PackageLocalStatePath));
        Assert.True(File.Exists(service.PendingMarkerPath));

        Directory.Delete(paths.PackageLocalStatePath!, recursive: false);
        Assert.True(service.CompletePendingResetOrThrow().Succeeded);
        Assert.Equal(new byte[41], await File.ReadAllBytesAsync(outsideCookie));
    }

    [Fact]
    public async Task NestedDirectoryReparsePointIsRemovedWithoutFollowingItsTarget()
    {
        var paths = CreatePaths();
        var outsideTarget = Path.Combine(_root, "NestedOutsideTarget");
        var outsideCookie = Path.Combine(outsideTarget, "Cookies");
        var link = Path.Combine(paths.PackageWebViewProfilePath!, "Default", "ExternalCache");
        Write(outsideCookie, new byte[37]);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, outsideTarget);
        Write(Path.Combine(paths.PackageWebViewProfilePath!, "Default", "History"), new byte[17]);
        var service = new LocalDataResetService(paths);

        var result = await service.ResetAsync();

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(paths.PackageWebViewProfilePath));
        Assert.Equal(new byte[37], await File.ReadAllBytesAsync(outsideCookie));
        Assert.False(File.Exists(service.PendingMarkerPath));
    }

    [Fact]
    public async Task ApiVerificationOutsideAuthorizedWebViewRootsIsRejectedBeforeMutation()
    {
        var paths = CreatePaths();
        var outsideRoot = Path.Combine(_root, "UnmanagedProfile");
        var outsideCookie = Path.Combine(outsideRoot, "Cookies");
        Write(outsideCookie, new byte[23]);
        var service = new LocalDataResetService(paths);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResetAsync([outsideRoot]));

        Assert.Equal(new byte[23], await File.ReadAllBytesAsync(outsideCookie));
        Assert.False(File.Exists(service.PendingMarkerPath));
    }

    [Fact]
    public async Task InventoryDoesNotChangeExistingData()
    {
        var paths = CreatePaths();
        Write(paths.TokenCachePath, [4, 5, 6, 7]);
        var service = new LocalDataResetService(paths);

        var inventory = await service.InventoryAsync();

        Assert.Equal(1, inventory.ItemCount);
        Assert.Equal(4, inventory.Bytes);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, await File.ReadAllBytesAsync(paths.TokenCachePath));
        Assert.False(File.Exists(service.PendingMarkerPath));
    }

    [Fact]
    public async Task VerifiedWebViewProfileCanRemainAsEmptyRuntimeInfrastructure()
    {
        var paths = CreatePaths();
        var runtimeFile = Path.Combine(paths.BrowserProfilePath, "runtime.lock");
        Write(runtimeFile, new byte[20]);
        Write(paths.DatabasePath, new byte[10]);
        var service = new LocalDataResetService(paths);

        var result = await service.ResetAsync([paths.BrowserProfilePath]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Before.ItemCount);
        Assert.Equal(0, result.After.ItemCount);
        Assert.True(File.Exists(runtimeFile));
        Assert.False(File.Exists(paths.DatabasePath));
        Assert.False(File.Exists(service.PendingMarkerPath));
    }

    private AppDataPaths CreatePaths() => new(
        Path.Combine(_root, "EasyShare"),
        packageWebViewProfilePath: Path.Combine(_root, "Package", "LocalState", "EBWebView"));

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            var marker = Path.Combine(_root, "EasyShare.reset.pending");
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
        catch
        {
        }
    }
}
