namespace EasyShare.Services;

public sealed record LocalDataCategory(string Name, int ItemCount, long Bytes);

public sealed record LocalDataInventory(
    int ItemCount,
    long Bytes,
    IReadOnlyList<LocalDataCategory> Categories)
{
    public static LocalDataInventory Empty { get; } = new(0, 0, []);
}

public sealed record LocalDataResetFailure(string Path, string Reason);

public sealed record LocalDataResetResult(
    LocalDataInventory Before,
    LocalDataInventory After,
    IReadOnlyList<LocalDataResetFailure> Failures)
{
    public bool Succeeded => Failures.Count == 0 && After.ItemCount == 0 && After.Bytes == 0;
}

/// <summary>
/// Inventories and removes every user-controlled artifact beneath EasyShare's local
/// data root. The pending marker lives beside the root so an interrupted reset can be
/// completed before services recreate databases, keys, caches, or profiles.
/// </summary>
public sealed class LocalDataResetService
{
    private const string PackageWebViewCategory = "Conta e sessão do navegador (perfil legado do pacote)";
    private const string PackageLocalStateCategory = "Outros dados locais do pacote";
    private const string PackageLocalStateFailurePrefix = "PackageLocalState";

    private readonly AppDataPaths _paths;
    private readonly ManagedRoot[] _managedRoots;
    private readonly HashSet<string> _apiClearableWebViewRoots;

    public LocalDataResetService(AppDataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _managedRoots = CreateManagedRoots(paths);
        _apiClearableWebViewRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizePath(paths.BrowserProfilePath)
        };
        if (!string.IsNullOrWhiteSpace(paths.PackageWebViewProfilePath))
        {
            _apiClearableWebViewRoots.Add(NormalizePath(paths.PackageWebViewProfilePath));
        }
    }

    public string PendingMarkerPath => _paths.DataDirectory.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar) + ".reset.pending";

    public Task<LocalDataInventory> InventoryAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Inventory(cancellationToken), cancellationToken);

    public void MarkPendingReset() => WritePendingMarker();

    public async Task<LocalDataResetResult> ResetAsync(
        IReadOnlyCollection<string>? verifiedClearedRoots = null,
        CancellationToken cancellationToken = default)
    {
        var exclusions = NormalizeVerifiedClearedRoots(verifiedClearedRoots);
        var before = await InventoryAsync(cancellationToken).ConfigureAwait(false);
        WritePendingMarker();
        var failures = await Task.Run(
                () => DeleteManagedData(cancellationToken, exclusions),
                cancellationToken)
            .ConfigureAwait(false);
        var after = await Task.Run(
                () => Inventory(cancellationToken, exclusions),
                cancellationToken)
            .ConfigureAwait(false);
        var finalFailures = failures.ToList();
        if (finalFailures.Count == 0 && after.ItemCount == 0 && after.Bytes == 0)
        {
            var markerFailure = TryDeleteMarker();
            if (markerFailure is not null)
            {
                finalFailures.Add(markerFailure);
            }
        }

        return new LocalDataResetResult(before, after, finalFailures);
    }

    public LocalDataResetResult CompletePendingReset()
    {
        FileAttributes? markerAttributes;
        try
        {
            markerAttributes = GetAttributesOrNull(PendingMarkerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LocalDataResetResult(
                LocalDataInventory.Empty,
                LocalDataInventory.Empty,
                [new LocalDataResetFailure(".reset.pending", ex.Message)]);
        }

        if (markerAttributes is null)
        {
            return new LocalDataResetResult(LocalDataInventory.Empty, LocalDataInventory.Empty, []);
        }

        var before = Inventory(CancellationToken.None);
        var failures = DeleteManagedData(
            CancellationToken.None,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var after = Inventory(CancellationToken.None);
        var finalFailures = failures.ToList();
        if (finalFailures.Count == 0 && after.ItemCount == 0 && after.Bytes == 0)
        {
            var markerFailure = TryDeleteMarker();
            if (markerFailure is not null)
            {
                finalFailures.Add(markerFailure);
            }
        }

        return new LocalDataResetResult(before, after, finalFailures);
    }

    public LocalDataResetResult CompletePendingResetOrThrow()
    {
        var result = CompletePendingReset();
        if (result.Succeeded)
        {
            return result;
        }

        var failure = result.Failures.FirstOrDefault();
        throw new IOException(failure is null
            ? "Pending local data deletion did not complete. EasyShare cannot start safely."
            : $"Pending local data deletion did not complete for '{failure.Path}': {failure.Reason}");
    }

    private LocalDataInventory Inventory(
        CancellationToken cancellationToken,
        IReadOnlySet<string>? excludedRoots = null)
    {
        var categories = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        var itemCount = 0;
        long totalBytes = 0;
        foreach (var root in _managedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExcluded(root.Path, excludedRoots))
            {
                continue;
            }

            var rootAttributes = GetAttributesOrNull(root.Path);
            if (rootAttributes is null)
            {
                continue;
            }

            if ((rootAttributes.Value & FileAttributes.ReparsePoint) != 0 ||
                (rootAttributes.Value & FileAttributes.Directory) == 0)
            {
                AddInventoryItem(root, root.Path, rootAttributes.Value, categories, ref itemCount, ref totalBytes);
                continue;
            }

            foreach (var entry in EnumerateManagedEntries(root.Path, excludedRoots))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    continue;
                }

                AddInventoryItem(
                    root,
                    entry.Path,
                    entry.Attributes,
                    categories,
                    ref itemCount,
                    ref totalBytes);
            }
        }

        return new LocalDataInventory(
            itemCount,
            totalBytes,
            categories.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(pair => new LocalDataCategory(pair.Key, pair.Value.Count, pair.Value.Bytes))
                .ToArray());
    }

    private void AddInventoryItem(
        ManagedRoot root,
        string path,
        FileAttributes attributes,
        IDictionary<string, (int Count, long Bytes)> categories,
        ref int itemCount,
        ref long totalBytes)
    {
        long length = 0;
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
        {
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked item still counts; its exact size can remain unknown.
            }
        }

        var category = GetCategory(root, path);
        categories.TryGetValue(category, out var current);
        categories[category] = (current.Count + 1, checked(current.Bytes + length));
        itemCount++;
        totalBytes = checked(totalBytes + length);
    }

    private IReadOnlyList<LocalDataResetFailure> DeleteManagedData(
        CancellationToken cancellationToken,
        IReadOnlySet<string> excludedRoots)
    {
        var failures = new List<LocalDataResetFailure>();
        foreach (var root in _managedRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExcluded(root.Path, excludedRoots))
            {
                continue;
            }

            DeleteManagedRoot(root, cancellationToken, excludedRoots, failures);
        }

        return failures
            .GroupBy(failure => failure.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private void DeleteManagedRoot(
        ManagedRoot root,
        CancellationToken cancellationToken,
        IReadOnlySet<string> excludedRoots,
        ICollection<LocalDataResetFailure> failures)
    {
        var rootAttributes = GetAttributesOrNull(root.Path);
        if (rootAttributes is null)
        {
            return;
        }

        if ((rootAttributes.Value & FileAttributes.ReparsePoint) != 0)
        {
            failures.Add(new LocalDataResetFailure(
                ToSafeRelativePath(root, root.Path),
                "A raiz autorizada é um link ou ponto de nova análise e não será seguida."));
            return;
        }

        if ((rootAttributes.Value & FileAttributes.Directory) == 0)
        {
            TryDeleteFile(root, root.Path, failures);
            return;
        }

        ManagedEntry[] entries;
        try
        {
            entries = EnumerateManagedEntries(root.Path, excludedRoots).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new LocalDataResetFailure(ToSafeRelativePath(root, root.Path), ex.Message));
            return;
        }

        foreach (var entry in entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteFile(root, entry.Path, failures);
        }

        foreach (var entry in entries.Where(entry => entry.IsDirectory)
                     .OrderByDescending(entry => entry.Path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var currentAttributes = GetAttributesOrNull(entry.Path);
                if (currentAttributes is null)
                {
                    continue;
                }

                if ((currentAttributes.Value & FileAttributes.Directory) == 0)
                {
                    TryDeleteFile(root, entry.Path, failures);
                    continue;
                }

                // recursive:false removes a directory reparse point itself without
                // traversing its target.
                Directory.Delete(entry.Path, recursive: false);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new LocalDataResetFailure(ToSafeRelativePath(root, entry.Path), ex.Message));
            }
        }

        if (!root.DeleteRoot || excludedRoots.Any(excluded => IsPathWithinOrEqual(excluded, root.Path)))
        {
            return;
        }

        try
        {
            var currentAttributes = GetAttributesOrNull(root.Path);
            if (currentAttributes is null)
            {
                return;
            }

            if ((currentAttributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                failures.Add(new LocalDataResetFailure(
                    ToSafeRelativePath(root, root.Path),
                    "A raiz autorizada tornou-se um link ou ponto de nova análise durante a exclusão."));
                return;
            }

            if ((currentAttributes.Value & FileAttributes.Directory) != 0)
            {
                Directory.Delete(root.Path, recursive: false);
            }
            else
            {
                TryDeleteFile(root, root.Path, failures);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new LocalDataResetFailure(ToSafeRelativePath(root, root.Path), ex.Message));
        }
    }

    private static void TryDeleteFile(
        ManagedRoot root,
        string path,
        ICollection<LocalDataResetFailure> failures)
    {
        try
        {
            var currentAttributes = GetAttributesOrNull(path);
            if (currentAttributes is null)
            {
                return;
            }

            if ((currentAttributes.Value & FileAttributes.ReparsePoint) == 0)
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(new LocalDataResetFailure(ToSafeRelativePath(root, path), ex.Message));
        }
    }

    private static IEnumerable<ManagedEntry> EnumerateManagedEntries(
        string root,
        IReadOnlySet<string>? excludedRoots)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var directoryAttributes = GetAttributesOrNull(directory);
            if (directoryAttributes is null ||
                (directoryAttributes.Value & FileAttributes.Directory) == 0 ||
                (directoryAttributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Não foi possível enumerar uma raiz de dados autorizada.", ex);
            }

            foreach (var entry in entries)
            {
                if (IsExcluded(entry, excludedRoots))
                {
                    continue;
                }

                var attributes = GetAttributesOrNull(entry);
                if (attributes is null)
                {
                    continue;
                }

                var managedEntry = new ManagedEntry(entry, attributes.Value);
                yield return managedEntry;
                if (managedEntry.IsDirectory && !managedEntry.IsReparsePoint)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private string GetCategory(ManagedRoot root, string path)
    {
        var relative = Path.GetRelativePath(root.Path, path);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (root.IsPackageLocalState)
        {
            return string.Equals(first, "EBWebView", StringComparison.OrdinalIgnoreCase)
                ? PackageWebViewCategory
                : PackageLocalStateCategory;
        }

        return first.ToUpperInvariant() switch
        {
            "BROWSERPROFILE" => "Conta e sessão do navegador",
            "UPLOADQUEUE" or "UPLOAD-PAYLOAD.KEY" => "Fila e envios locais",
            "OFFLINECACHE" or "OFFLINE-CACHE.KEY" => "Arquivos disponíveis offline",
            "LOGS" => "Diagnósticos locais",
            "EASYSHARE.DB" or "EASYSHARE.DB-WAL" or "EASYSHARE.DB-SHM" => "Configurações, rotas, histórico e pesquisa",
            "MSAL.CACHE" => "Conta Microsoft",
            _ => "Outros dados locais"
        };
    }

    private static string ToSafeRelativePath(ManagedRoot root, string path)
    {
        if (!IsPathWithinOrEqual(path, root.Path))
        {
            return string.IsNullOrWhiteSpace(root.FailurePrefix) ? "." : root.FailurePrefix;
        }

        var relative = Path.GetRelativePath(root.Path, NormalizePath(path));
        if (relative == ".")
        {
            return string.IsNullOrWhiteSpace(root.FailurePrefix) ? "." : root.FailurePrefix;
        }

        return string.IsNullOrWhiteSpace(root.FailurePrefix)
            ? relative
            : Path.Combine(root.FailurePrefix, relative);
    }

    private HashSet<string> NormalizeVerifiedClearedRoots(IReadOnlyCollection<string>? roots)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots ?? [])
        {
            var fullPath = NormalizePath(root);
            if (!_apiClearableWebViewRoots.Contains(fullPath))
            {
                throw new InvalidOperationException(
                    "Only an authorized WebView profile can be accepted as cleared by the WebView2 API.");
            }

            var attributes = GetAttributesOrNull(fullPath);
            if (attributes is not null && (attributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "A WebView profile link cannot be accepted as cleared because its target is outside the authorized root.");
            }

            normalized.Add(fullPath);
        }

        return normalized;
    }

    private static bool IsExcluded(string path, IReadOnlySet<string>? excludedRoots)
    {
        if (excludedRoots is null || excludedRoots.Count == 0)
        {
            return false;
        }

        var fullPath = NormalizePath(path);
        return excludedRoots.Any(root =>
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static FileAttributes? GetAttributesOrNull(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static ManagedRoot[] CreateManagedRoots(AppDataPaths paths)
    {
        var primary = new ManagedRoot(
            NormalizePath(paths.DataDirectory),
            string.Empty,
            DeleteRoot: true,
            IsPackageLocalState: false);
        if (string.Equals(
                primary.Path,
                Path.GetPathRoot(primary.Path),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The local data root cannot be a filesystem root.", nameof(paths));
        }

        if (string.IsNullOrWhiteSpace(paths.PackageLocalStatePath))
        {
            return [primary];
        }

        var packageLocalState = new ManagedRoot(
            NormalizePath(paths.PackageLocalStatePath),
            PackageLocalStateFailurePrefix,
            DeleteRoot: false,
            IsPackageLocalState: true);
        if (string.Equals(
                packageLocalState.Path,
                Path.GetPathRoot(packageLocalState.Path),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The package LocalState boundary cannot be a filesystem root.", nameof(paths));
        }

        if (IsPathWithinOrEqual(primary.Path, packageLocalState.Path) ||
            IsPathWithinOrEqual(packageLocalState.Path, primary.Path))
        {
            throw new InvalidOperationException(
                "The primary data root and package LocalState boundary must not overlap.");
        }

        return [primary, packageLocalState];
    }

    private static bool IsPathWithinOrEqual(string path, string root)
    {
        var fullPath = NormalizePath(path);
        var fullRoot = NormalizePath(root);
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private sealed record ManagedRoot(
        string Path,
        string FailurePrefix,
        bool DeleteRoot,
        bool IsPackageLocalState);

    private sealed record ManagedEntry(string Path, FileAttributes Attributes)
    {
        public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

        public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private void WritePendingMarker()
    {
        var parent = Path.GetDirectoryName(PendingMarkerPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(PendingMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
        PrivateFilePermissions.TryHardenFile(PendingMarkerPath);
    }

    private LocalDataResetFailure? TryDeleteMarker()
    {
        try
        {
            // File.Delete is intentionally unconditional: a marker that disappears
            // concurrently is already in the desired state, while an undeletable
            // marker must remain an explicit failure and keep startup fail-closed.
            File.Delete(PendingMarkerPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LocalDataResetFailure(".reset.pending", ex.Message);
        }
    }
}
