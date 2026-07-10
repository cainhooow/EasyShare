using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using EasyShare.Models;
using EasyShare.Resources;
using Fsp;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using FspFileInfo = Fsp.Interop.FileInfo;
using WinFileAttributes = System.IO.FileAttributes;

namespace EasyShare.Services;

public sealed record VirtualDriveStatus(
    string RootPath,
    string State,
    string Detail,
    bool CanOpenInExplorer);

public interface IVirtualDriveService
{
    Task<VirtualDriveStatus> GetStatusAsync(AppSettings settings, IReadOnlyCollection<DriveRoute> routes);
}

public sealed class VirtualDriveService : IVirtualDriveService, IDisposable
{
    private readonly object _gate = new();
    private readonly SharePointBrowserContentService _contentService;
    private readonly UploadQueueService _uploadQueue;
    private FileSystemHost? _host;
    private string? _mountedAt;
    private string? _routeSignature;

    public VirtualDriveService(
        SharePointBrowserContentService contentService,
        UploadQueueService uploadQueue)
    {
        _contentService = contentService;
        _uploadQueue = uploadQueue;
    }

    public Task<VirtualDriveStatus> GetStatusAsync(AppSettings settings, IReadOnlyCollection<DriveRoute> routes)
    {
        lock (_gate)
        {
            return Task.FromResult(EnsureState(settings, routes.ToArray()));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopMountedHost();
        }
    }

    private VirtualDriveStatus EnsureState(AppSettings settings, IReadOnlyList<DriveRoute> routes)
    {
        var mountPoint = NormalizeMountPoint(settings.MountPoint);
        var displayRoot = FormatDisplayRoot(mountPoint);

        if (!settings.AutoStartVirtualDrive)
        {
            StopMountedHost();
            return new VirtualDriveStatus(
                displayRoot,
                AppText.Get("VirtualDrivePausedTitle"),
                AppText.Get("VirtualDrivePausedDetail"),
                CanOpenInExplorer: false);
        }

        if (!IsWinFspInstalled())
        {
            StopMountedHost();
            return new VirtualDriveStatus(
                displayRoot,
                AppText.Get("VirtualDriveWinFspMissingTitle"),
                AppText.Get("VirtualDriveWinFspMissingDetail"),
                CanOpenInExplorer: false);
        }

        if (routes.Count == 0)
        {
            StopMountedHost();
            return new VirtualDriveStatus(
                displayRoot,
                AppText.Get("VirtualDriveNoRoutesTitle"),
                AppText.Get("VirtualDriveNoRoutesDetail"),
                CanOpenInExplorer: false);
        }

        var signature = BuildRouteSignature(routes);
        if (_host is not null &&
            string.Equals(_mountedAt, mountPoint, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_routeSignature, signature, StringComparison.Ordinal))
        {
            return new VirtualDriveStatus(
                displayRoot,
                AppText.Get("VirtualDriveActiveTitle"),
                AppText.Format("VirtualDriveActiveDetailFormat", routes.Count),
                CanOpenInExplorer: true);
        }

        StopMountedHost();

        try
        {
            var fileSystem = new EasyShareFileSystem(routes, _contentService, _uploadQueue);
            var host = new FileSystemHost(fileSystem)
            {
                FileSystemName = "EasyShare",
                CaseSensitiveSearch = false,
                CasePreservedNames = true,
                UnicodeOnDisk = true,
                MaxComponentLength = 255,
                SectorSize = 4096,
                SectorsPerAllocationUnit = 1,
                FileInfoTimeout = 1000,
                DirInfoTimeout = 1000,
                VolumeInfoTimeout = 1000
            };

            var result = host.Mount(mountPoint, null, Synchronized: true, DebugLog: 0);
            if (result < 0)
            {
                host.Dispose();
                return new VirtualDriveStatus(
                    displayRoot,
                    AppText.Get("VirtualDriveMountFailedTitle"),
                    AppText.Format("VirtualDriveMountRejectedFormat", unchecked((uint)result)),
                    CanOpenInExplorer: false);
            }

            _host = host;
            _mountedAt = mountPoint;
            _routeSignature = signature;

            return new VirtualDriveStatus(
                displayRoot,
                AppText.Get("VirtualDriveActiveTitle"),
                AppText.Format("VirtualDriveActiveDetailFormat", routes.Count),
                CanOpenInExplorer: true);
        }
        catch (Exception ex)
        {
            StopMountedHost();
            return new VirtualDriveStatus(
                displayRoot,
                AppText.Get("VirtualDriveMountFailedTitle"),
                GetExceptionMessage(ex),
                CanOpenInExplorer: false);
        }
    }

    private static string GetExceptionMessage(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" ", messages.Distinct());
    }

    private void StopMountedHost()
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            _host.Unmount();
        }
        catch
        {
            // The driver may already have torn the mount down during process shutdown.
        }
        finally
        {
            _host.Dispose();
            _host = null;
            _mountedAt = null;
            _routeSignature = null;
        }
    }

    private static string BuildRouteSignature(IEnumerable<DriveRoute> routes) =>
        string.Join(
            "|",
            routes
                .OrderBy(route => route.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(route => route.Id)
                .Select(route => $"{route.Id:N}:{route.DisplayName}:{route.SharePointUrl}:{route.RemotePath}"));

    private static string NormalizeMountPoint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "S:";
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 1 && char.IsLetter(trimmed[0]))
        {
            return $"{char.ToUpperInvariant(trimmed[0])}:";
        }

        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
        {
            return $"{char.ToUpperInvariant(trimmed[0])}:";
        }

        return trimmed.TrimEnd('\\');
    }

    private static string FormatDisplayRoot(string mountPoint) =>
        mountPoint.Length == 2 && mountPoint[1] == ':'
            ? $@"{mountPoint}\"
            : mountPoint;

    private static bool IsWinFspInstalled()
    {
        if (File.Exists(@"C:\Program Files (x86)\WinFsp\bin\winfsp-x64.dll") ||
            File.Exists(@"C:\Program Files\WinFsp\bin\winfsp-x64.dll") ||
            Directory.EnumerateFiles(@"C:\Program Files (x86)\WinFsp", "winfsp-x64.dll", SearchOption.AllDirectories).AnySafe())
        {
            return true;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp") ??
                            Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private sealed class EasyShareFileSystem : FileSystemBase
    {
        private const ulong VolumeSize = 10UL * 1024 * 1024 * 1024;
        private readonly SharePointBrowserContentService _contentService;
        private readonly UploadQueueService _uploadQueue;
        private readonly byte[] _securityDescriptor;
        private readonly FspFileInfo _rootInfo;
        private readonly IReadOnlyList<VirtualNode> _rootChildren;
        private readonly Dictionary<string, VirtualNode> _rootNodes;
        private readonly ConcurrentDictionary<string, bool> _deleteOnClose = new(StringComparer.OrdinalIgnoreCase);

        public EasyShareFileSystem(
            IReadOnlyList<DriveRoute> routes,
            SharePointBrowserContentService contentService,
            UploadQueueService uploadQueue)
        {
            _contentService = contentService;
            _uploadQueue = uploadQueue;
            _securityDescriptor = CreateSecurityDescriptor();
            _rootInfo = CreateDirectoryInfo(1);
            _rootChildren = BuildRouteNodes(routes);
            _rootNodes = _rootChildren.ToDictionary(node => node.Path, StringComparer.OrdinalIgnoreCase);
        }

        public override int GetVolumeInfo(out VolumeInfo volumeInfo)
        {
            volumeInfo = new VolumeInfo
            {
                TotalSize = VolumeSize,
                FreeSize = VolumeSize
            };
            volumeInfo.SetVolumeLabel("EasyShare");
            return STATUS_SUCCESS;
        }

        public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo) =>
            GetVolumeInfo(out volumeInfo);

        public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
        {
            if (!TryGetNode(fileName, out var node, out var info))
            {
                fileAttributes = default;
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            fileAttributes = info.FileAttributes;
            if (securityDescriptor is not null)
            {
                securityDescriptor = _securityDescriptor;
            }

            return STATUS_SUCCESS;
        }

        public override int Open(
            string fileName,
            uint createOptions,
            uint grantedAccess,
            out object fileNode,
            out object fileDesc,
            out FspFileInfo fileInfo,
            out string normalizedName)
        {
            fileNode = default!;
            fileDesc = default!;
            fileInfo = default;
            normalizedName = default!;

            if (!TryGetNode(fileName, out var node, out var info))
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            var isDirectory = node?.IsDirectory ?? true;
            if ((createOptions & FILE_NON_DIRECTORY_FILE) != 0 && isDirectory)
            {
                return STATUS_FILE_IS_A_DIRECTORY;
            }

            if ((createOptions & FILE_DIRECTORY_FILE) != 0 && !isDirectory)
            {
                return STATUS_NOT_A_DIRECTORY;
            }

            fileNode = node ?? VirtualNode.Root;
            fileDesc = node is { IsDirectory: false, Route: not null }
                ? new WritableFileHandle(node)
                : fileNode;
            fileInfo = info;
            normalizedName = NormalizeFileName(fileName);
            return STATUS_SUCCESS;
        }

        public override int Create(
            string fileName,
            uint createOptions,
            uint grantedAccess,
            uint fileAttributes,
            byte[] securityDescriptor,
            ulong allocationSize,
            out object fileNode,
            out object fileDesc,
            out FspFileInfo fileInfo,
            out string normalizedName)
        {
            fileNode = default!;
            fileDesc = default!;
            fileInfo = default;
            normalizedName = default!;

            if (!TryResolveRoutePath(fileName, out var routeNode, out var relativePath) || routeNode.Route is null)
            {
                return STATUS_ACCESS_DENIED;
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return STATUS_ACCESS_DENIED;
            }

            var isDirectory = (createOptions & FILE_DIRECTORY_FILE) != 0;
            if (TryGetNode(fileName, out var existingNode, out _))
            {
                if (existingNode is not null && existingNode.IsDirectory != isDirectory)
                {
                    return isDirectory ? STATUS_NOT_A_DIRECTORY : STATUS_FILE_IS_A_DIRECTORY;
                }

                return STATUS_OBJECT_NAME_COLLISION;
            }

            normalizedName = NormalizeFileName(fileName);
            if (isDirectory)
            {
                if (!_contentService.CreateFolder(routeNode.Route, relativePath))
                {
                    return STATUS_ACCESS_DENIED;
                }

                var directoryNode = new VirtualNode(
                    normalizedName,
                    GetLeafName(relativePath),
                    CreateDirectoryInfo(CreateIndexNumber($"{routeNode.Route.Id:N}:{relativePath}")),
                    routeNode.Route,
                    relativePath,
                    IsDirectory: true);
                fileNode = directoryNode;
                fileDesc = directoryNode;
                fileInfo = directoryNode.Info;
                return STATUS_SUCCESS;
            }

            var now = DateTimeOffset.UtcNow;
            var writableNode = new VirtualNode(
                normalizedName,
                GetLeafName(relativePath),
                CreateFileInfo(CreateIndexNumber($"{routeNode.Route.Id:N}:{relativePath}"), 0, now),
                routeNode.Route,
                relativePath,
                IsDirectory: false);
            var handle = new WritableFileHandle(writableNode, isNew: true);
            fileNode = writableNode;
            fileDesc = handle;
            fileInfo = handle.Info;
            return STATUS_SUCCESS;
        }

        public override int GetFileInfo(object fileNode, object fileDesc, out FspFileInfo fileInfo)
        {
            if (fileNode is VirtualNode node && !node.IsRoot)
            {
                fileInfo = node.Info;
                return STATUS_SUCCESS;
            }

            fileInfo = _rootInfo;
            return STATUS_SUCCESS;
        }

        public override int Read(
            object fileNode,
            object fileDesc,
            IntPtr buffer,
            ulong offset,
            uint length,
            out uint bytesTransferred)
        {
            bytesTransferred = 0;
            if (fileNode is not VirtualNode { IsDirectory: false, Route: not null } node)
            {
                return STATUS_FILE_IS_A_DIRECTORY;
            }

            var bytes = _contentService.ReadFile(node.Route, node.RelativePath);
            if (bytes.Length == 0 && node.Info.FileSize > 0)
            {
                return STATUS_ACCESS_DENIED;
            }

            if (offset >= (ulong)bytes.Length)
            {
                return STATUS_END_OF_FILE;
            }

            var available = (uint)Math.Min(length, (ulong)bytes.Length - offset);
            Marshal.Copy(bytes, (int)offset, buffer, (int)available);
            bytesTransferred = available;
            return STATUS_SUCCESS;
        }

        public override int Write(
            object fileNode,
            object fileDesc,
            IntPtr buffer,
            ulong offset,
            uint length,
            bool writeToEndOfFile,
            bool constrainedIo,
            out uint bytesTransferred,
            out FspFileInfo fileInfo)
        {
            bytesTransferred = 0;
            fileInfo = default;

            if (fileDesc is not WritableFileHandle handle)
            {
                return STATUS_ACCESS_DENIED;
            }

            try
            {
                handle.Write(_contentService, buffer, offset, length, writeToEndOfFile, constrainedIo, out bytesTransferred);
                fileInfo = handle.Info;
                return STATUS_SUCCESS;
            }
            catch
            {
                return STATUS_ACCESS_DENIED;
            }
        }

        public override int Flush(object fileNode, object fileDesc, out FspFileInfo fileInfo)
        {
            fileInfo = fileNode is VirtualNode node && !node.IsRoot ? node.Info : _rootInfo;
            if (fileDesc is not WritableFileHandle handle)
            {
                return STATUS_SUCCESS;
            }

            return FlushWritableHandle(handle, out fileInfo);
        }

        public override int SetFileSize(
            object fileNode,
            object fileDesc,
            ulong newSize,
            bool setAllocationSize,
            out FspFileInfo fileInfo)
        {
            fileInfo = default;
            if (fileDesc is not WritableFileHandle handle)
            {
                return STATUS_ACCESS_DENIED;
            }

            try
            {
                handle.SetFileSize(_contentService, newSize, setAllocationSize);
                fileInfo = handle.Info;
                return STATUS_SUCCESS;
            }
            catch
            {
                return STATUS_ACCESS_DENIED;
            }
        }

        public override int SetBasicInfo(
            object fileNode,
            object fileDesc,
            uint fileAttributes,
            ulong creationTime,
            ulong lastAccessTime,
            ulong lastWriteTime,
            ulong changeTime,
            out FspFileInfo fileInfo)
        {
            if (fileDesc is WritableFileHandle handle)
            {
                handle.UpdateBasicInfo(fileAttributes, creationTime, lastAccessTime, lastWriteTime, changeTime);
                fileInfo = handle.Info;
                return STATUS_SUCCESS;
            }

            fileInfo = fileNode is VirtualNode node && !node.IsRoot ? node.Info : _rootInfo;
            return STATUS_SUCCESS;
        }

        public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
        {
            securityDescriptor = _securityDescriptor;
            return STATUS_SUCCESS;
        }

        public override int SetSecurity(
            object fileNode,
            object fileDesc,
            AccessControlSections sections,
            byte[] securityDescriptor) =>
            STATUS_SUCCESS;

        public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
        {
            if (fileDesc is WritableFileHandle handle)
            {
                FlushWritableHandle(handle, out _);
            }

            var node = fileDesc is WritableFileHandle writableHandle
                ? writableHandle.Node
                : fileNode as VirtualNode;
            if (node is { IsRoot: false, Route: not null } &&
                _deleteOnClose.TryRemove(node.Path, out var shouldDelete) &&
                shouldDelete)
            {
                _contentService.DeleteItem(node.Route, node.RelativePath, node.IsDirectory);
            }
        }

        public override void Close(object fileNode, object fileDesc)
        {
            if (fileDesc is WritableFileHandle handle)
            {
                FlushWritableHandle(handle, out _);
            }
        }

        public override int SetDelete(object fileNode, object fileDesc, string fileName, bool deleteFile)
        {
            var node = fileDesc is WritableFileHandle handle
                ? handle.Node
                : fileNode as VirtualNode;
            if (node is not { IsRoot: false, Route: not null })
            {
                return STATUS_ACCESS_DENIED;
            }

            _deleteOnClose[node.Path] = deleteFile;
            return STATUS_SUCCESS;
        }

        public override int Rename(
            object fileNode,
            object fileDesc,
            string fileName,
            string newFileName,
            bool replaceIfExists)
        {
            var node = fileDesc is WritableFileHandle handle
                ? handle.Node
                : fileNode as VirtualNode;
            if (node is not { IsRoot: false, Route: not null })
            {
                return STATUS_ACCESS_DENIED;
            }

            if (!TryResolveRoutePath(fileName, out var oldRouteNode, out var oldRelativePath) ||
                oldRouteNode.Route is null ||
                !TryResolveRoutePath(newFileName, out var newRouteNode, out var newRelativePath) ||
                newRouteNode.Route is null)
            {
                return STATUS_ACCESS_DENIED;
            }

            if (oldRouteNode.Route.Id != newRouteNode.Route.Id ||
                string.IsNullOrWhiteSpace(oldRelativePath) ||
                string.IsNullOrWhiteSpace(newRelativePath))
            {
                return STATUS_ACCESS_DENIED;
            }

            if (string.Equals(oldRelativePath, newRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                return STATUS_SUCCESS;
            }

            if (!replaceIfExists && TryGetNode(newFileName, out _, out _))
            {
                return STATUS_OBJECT_NAME_COLLISION;
            }

            if (!_contentService.RenameItem(
                    oldRouteNode.Route,
                    oldRelativePath,
                    newRelativePath,
                    node.IsDirectory,
                    replaceIfExists))
            {
                return STATUS_ACCESS_DENIED;
            }

            _deleteOnClose.TryRemove(node.Path, out _);
            return STATUS_SUCCESS;
        }

        public override bool ReadDirectoryEntry(
            object fileNode,
            object fileDesc,
            string pattern,
            string marker,
            ref object context,
            out string fileName,
            out FspFileInfo fileInfo)
        {
            var node = fileNode as VirtualNode ?? VirtualNode.Root;
            var enumerator = context as IEnumerator<DirectoryEntry>;
            if (enumerator is null)
            {
                enumerator = BuildDirectoryEntries(node, marker).GetEnumerator();
                context = enumerator;
            }

            if (enumerator.MoveNext())
            {
                fileName = enumerator.Current.Name;
                fileInfo = enumerator.Current.Info;
                return true;
            }

            fileName = default!;
            fileInfo = default;
            return false;
        }

        public override int GetDirInfoByName(
            object parentNode,
            object fileDesc,
            string fileName,
            out string normalizedName,
            out FspFileInfo fileInfo)
        {
            normalizedName = default!;
            fileInfo = default;

            var parent = parentNode as VirtualNode ?? VirtualNode.Root;
            if (!parent.IsDirectory)
            {
                return STATUS_NOT_A_DIRECTORY;
            }

            var child = GetChildren(parent)
                .FirstOrDefault(item => string.Equals(item.Name, fileName, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            normalizedName = child.Name;
            fileInfo = child.Info;
            return STATUS_SUCCESS;
        }

        private int FlushWritableHandle(WritableFileHandle handle, out FspFileInfo fileInfo)
        {
            fileInfo = handle.Info;
            if (!handle.IsDirty)
            {
                return STATUS_SUCCESS;
            }

            if (handle.Node.Route is null)
            {
                return STATUS_ACCESS_DENIED;
            }

            var bytes = handle.ToArray(_contentService);
            _contentService.CacheLocalFile(handle.Node.Route, handle.Node.RelativePath, bytes);
            _uploadQueue.Enqueue(
                handle.Node.Route,
                handle.Node.RelativePath,
                bytes,
                handle.ExpectedModifiedAt);
            handle.MarkUploaded();

            fileInfo = handle.Info;
            return STATUS_SUCCESS;
        }

        private bool TryGetNode(string fileName, out VirtualNode? node, out FspFileInfo info)
        {
            var normalized = NormalizeFileName(fileName);
            if (normalized == @"\")
            {
                node = VirtualNode.Root;
                info = _rootInfo;
                return true;
            }

            if (_rootNodes.TryGetValue(normalized, out node))
            {
                info = node.Info;
                return true;
            }

            var segments = normalized.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                node = VirtualNode.Root;
                info = _rootInfo;
                return true;
            }

            var routeNode = _rootChildren.FirstOrDefault(
                item => string.Equals(item.Name, segments[0], StringComparison.OrdinalIgnoreCase));
            if (routeNode is null || routeNode.Route is null)
            {
                node = null;
                info = default;
                return false;
            }

            var relativePath = string.Join("/", segments.Skip(1));
            var item = _contentService.GetItem(routeNode.Route, relativePath);
            if (item is null)
            {
                node = null;
                info = default;
                return false;
            }

            node = FromSharePointItem(routeNode, item, relativePath);
            info = node.Info;
            return true;
        }

        private bool TryResolveRoutePath(string fileName, out VirtualNode routeNode, out string relativePath)
        {
            routeNode = default!;
            relativePath = string.Empty;

            var segments = NormalizeFileName(fileName)
                .Trim('\\')
                .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 2)
            {
                return false;
            }

            var found = _rootChildren.FirstOrDefault(
                item => string.Equals(item.Name, segments[0], StringComparison.OrdinalIgnoreCase));
            if (found is null || found.Route is null)
            {
                return false;
            }

            routeNode = found;
            relativePath = string.Join("/", segments.Skip(1));
            return true;
        }

        private IEnumerable<DirectoryEntry> BuildDirectoryEntries(VirtualNode node, string marker)
        {
            var entries = new List<DirectoryEntry>
            {
                new(".", node.IsRoot ? _rootInfo : node.Info),
                new("..", GetParentInfo(node))
            };

            if (node.IsDirectory)
            {
                entries.AddRange(GetChildren(node).Select(child => new DirectoryEntry(child.Name, child.Info)));
            }

            if (string.IsNullOrEmpty(marker))
            {
                return entries;
            }

            return entries.Where(entry => string.Compare(entry.Name, marker, StringComparison.OrdinalIgnoreCase) > 0);
        }

        private FspFileInfo GetParentInfo(VirtualNode node)
        {
            if (node.IsRoot || string.IsNullOrWhiteSpace(node.RelativePath))
            {
                return _rootInfo;
            }

            var routeNode = _rootChildren.FirstOrDefault(item => item.Route?.Id == node.Route?.Id);
            if (routeNode is null || node.Route is null)
            {
                return _rootInfo;
            }

            var parentRelativePath = GetParentPath(node.RelativePath);
            if (string.IsNullOrWhiteSpace(parentRelativePath))
            {
                return routeNode.Info;
            }

            var parentItem = _contentService.GetItem(node.Route, parentRelativePath);
            return parentItem is null ? routeNode.Info : ToFileInfo(parentItem);
        }

        private IReadOnlyList<VirtualNode> GetChildren(VirtualNode node)
        {
            if (node.IsRoot)
            {
                return _rootChildren;
            }

            if (!node.IsDirectory || node.Route is null)
            {
                return [];
            }

            return _contentService
                .ListDirectory(node.Route, node.RelativePath)
                .Select(item => FromSharePointItem(node, item))
                .ToArray();
        }

        private static VirtualNode FromSharePointItem(VirtualNode parent, SharePointDriveItem item, string? relativePathOverride = null)
        {
            var relativePath = relativePathOverride ?? (string.IsNullOrWhiteSpace(parent.RelativePath)
                ? item.Name
                : $"{parent.RelativePath.TrimEnd('/')}/{item.Name}");
            var path = relativePathOverride is null
                ? $"{parent.Path.TrimEnd('\\')}\\{item.Name}"
                : $"{parent.Path.TrimEnd('\\')}\\{relativePathOverride.Replace('/', '\\')}";
            return new VirtualNode(path, item.Name, ToFileInfo(item), parent.Route, relativePath, item.IsDirectory);
        }

        private static IReadOnlyList<VirtualNode> BuildRouteNodes(IReadOnlyList<DriveRoute> routes)
        {
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var nodes = new List<VirtualNode>();
            var index = 2UL;

            foreach (var route in routes.OrderBy(route => route.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var name = MakeSafeName(route.DisplayName);
                if (names.TryGetValue(name, out var count))
                {
                    count++;
                    names[name] = count;
                    name = $"{name} ({count})";
                }
                else
                {
                    names[name] = 1;
                }

                nodes.Add(new VirtualNode($@"\{name}", name, CreateDirectoryInfo(index++), route, string.Empty, IsDirectory: true));
            }

            return nodes;
        }

        private static FspFileInfo ToFileInfo(SharePointDriveItem item) =>
            item.IsDirectory
                ? CreateDirectoryInfo(CreateIndexNumber(item.ServerRelativeUrl), item.ModifiedAt)
                : CreateFileInfo(CreateIndexNumber(item.ServerRelativeUrl), item.Length, item.ModifiedAt);

        private static FspFileInfo CreateDirectoryInfo(ulong indexNumber, DateTimeOffset? modifiedAt = null)
        {
            var now = (ulong)(modifiedAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToFileTimeUtc();
            return new FspFileInfo
            {
                FileAttributes = (uint)WinFileAttributes.Directory,
                AllocationSize = 0,
                FileSize = 0,
                CreationTime = now,
                LastAccessTime = now,
                LastWriteTime = now,
                ChangeTime = now,
                IndexNumber = indexNumber,
                HardLinks = 1
            };
        }

        private static FspFileInfo CreateFileInfo(ulong indexNumber, long length, DateTimeOffset modifiedAt)
        {
            var time = (ulong)modifiedAt.UtcDateTime.ToFileTimeUtc();
            var size = (ulong)Math.Max(0, length);
            var allocationSize = size == 0 ? 0 : ((size + 4095) / 4096) * 4096;
            return new FspFileInfo
            {
                FileAttributes = (uint)WinFileAttributes.Archive,
                AllocationSize = allocationSize,
                FileSize = size,
                CreationTime = time,
                LastAccessTime = time,
                LastWriteTime = time,
                ChangeTime = time,
                IndexNumber = indexNumber,
                HardLinks = 1
            };
        }

        private static ulong CreateIndexNumber(string value)
        {
            unchecked
            {
                ulong hash = 14695981039346656037;
                foreach (var character in value)
                {
                    hash ^= character;
                    hash *= 1099511628211;
                }

                return hash;
            }
        }

        private static byte[] CreateSecurityDescriptor()
        {
            var descriptor = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)");
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            return bytes;
        }

        private static string NormalizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName == @"\")
            {
                return @"\";
            }

            var normalized = fileName.Replace('/', '\\').TrimEnd('\\');
            return normalized.StartsWith('\\') ? normalized : $@"\{normalized}";
        }

        private static string GetParentPath(string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/').Trim('/');
            var index = normalized.LastIndexOf('/');
            return index < 0 ? string.Empty : normalized[..index];
        }

        private static string GetLeafName(string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/').Trim('/');
            var index = normalized.LastIndexOf('/');
            return index < 0 ? normalized : normalized[(index + 1)..];
        }

        private static string MakeSafeName(string value)
        {
            var safeName = string.Join(
                " ",
                value
                    .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            return string.IsNullOrWhiteSpace(safeName) ? AppText.Get("VirtualDriveDefaultFolderName") : safeName;
        }
    }

    private sealed class WritableFileHandle
    {
        private readonly bool _isNew;
        private MemoryStream? _buffer;

        public WritableFileHandle(VirtualNode node, bool isNew = false)
        {
            Node = node;
            Info = node.Info;
            _isNew = isNew;
            ExpectedModifiedAt = isNew || node.Info.LastWriteTime == 0
                ? null
                : DateTimeOffset.FromFileTime((long)node.Info.LastWriteTime).ToUniversalTime();
        }

        public VirtualNode Node { get; }

        public FspFileInfo Info { get; private set; }

        public DateTimeOffset? ExpectedModifiedAt { get; }

        public bool IsDirty { get; private set; }

        public void Write(
            SharePointBrowserContentService contentService,
            IntPtr source,
            ulong offset,
            uint length,
            bool writeToEndOfFile,
            bool constrainedIo,
            out uint bytesTransferred)
        {
            bytesTransferred = 0;
            if (length == 0)
            {
                return;
            }

            if (length > int.MaxValue)
            {
                throw new InvalidOperationException("Write block is too large.");
            }

            var buffer = EnsureBuffer(contentService);
            var writeOffset = writeToEndOfFile ? buffer.Length : checked((long)offset);
            var allowedLength = constrainedIo && writeOffset + length > buffer.Length
                ? (uint)Math.Max(0, buffer.Length - writeOffset)
                : length;
            if (allowedLength == 0)
            {
                return;
            }

            var bytes = new byte[allowedLength];
            Marshal.Copy(source, bytes, 0, bytes.Length);

            if (writeOffset > buffer.Length)
            {
                buffer.SetLength(writeOffset);
            }

            buffer.Position = writeOffset;
            buffer.Write(bytes, 0, bytes.Length);
            bytesTransferred = allowedLength;
            IsDirty = true;
            UpdateSize((ulong)buffer.Length);
        }

        public void SetFileSize(SharePointBrowserContentService contentService, ulong newSize, bool setAllocationSize)
        {
            var buffer = EnsureBuffer(contentService);
            if (newSize > long.MaxValue)
            {
                throw new InvalidOperationException("File is too large.");
            }

            if (!setAllocationSize || newSize < (ulong)buffer.Length)
            {
                buffer.SetLength((long)newSize);
                IsDirty = true;
            }

            UpdateSize((ulong)buffer.Length);
        }

        public byte[] ToArray(SharePointBrowserContentService contentService) =>
            EnsureBuffer(contentService).ToArray();

        public void UpdateBasicInfo(uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime)
        {
            var info = Info;
            if (fileAttributes != uint.MaxValue)
            {
                var requested = (WinFileAttributes)fileAttributes;
                info.FileAttributes = (uint)(requested & ~WinFileAttributes.ReadOnly);
                if (info.FileAttributes == 0)
                {
                    info.FileAttributes = (uint)WinFileAttributes.Archive;
                }
            }

            if (creationTime != 0)
            {
                info.CreationTime = creationTime;
            }

            if (lastAccessTime != 0)
            {
                info.LastAccessTime = lastAccessTime;
            }

            if (lastWriteTime != 0)
            {
                info.LastWriteTime = lastWriteTime;
            }

            if (changeTime != 0)
            {
                info.ChangeTime = changeTime;
            }

            Info = info;
        }

        public void ReplaceInfo(FspFileInfo info)
        {
            Info = info;
            IsDirty = false;
        }

        public void MarkUploaded()
        {
            IsDirty = false;
        }

        private MemoryStream EnsureBuffer(SharePointBrowserContentService contentService)
        {
            if (_buffer is not null)
            {
                return _buffer;
            }

            var bytes = _isNew || Node.Route is null
                ? []
                : contentService.ReadFile(Node.Route, Node.RelativePath);
            _buffer = new MemoryStream();
            _buffer.Write(bytes, 0, bytes.Length);
            _buffer.Position = 0;
            UpdateSize((ulong)_buffer.Length);
            return _buffer;
        }

        private void UpdateSize(ulong size)
        {
            var now = (ulong)DateTimeOffset.UtcNow.UtcDateTime.ToFileTimeUtc();
            var allocationSize = size == 0 ? 0 : ((size + 4095) / 4096) * 4096;
            var info = Info;
            info.FileAttributes = (uint)WinFileAttributes.Archive;
            info.FileSize = size;
            info.AllocationSize = allocationSize;
            info.LastWriteTime = now;
            info.ChangeTime = now;
            Info = info;
        }
    }

    private sealed record DirectoryEntry(string Name, FspFileInfo Info);

    private sealed record VirtualNode(
        string Path,
        string Name,
        FspFileInfo Info,
        DriveRoute? Route,
        string RelativePath,
        bool IsDirectory)
    {
        public static readonly VirtualNode Root = new(@"\", string.Empty, default, null, string.Empty, true);

        public bool IsRoot => Path == @"\";
    }
}

internal static class DirectoryEnumerationExtensions
{
    public static bool AnySafe(this IEnumerable<string> source)
    {
        try
        {
            return source.Any();
        }
        catch
        {
            return false;
        }
    }
}
