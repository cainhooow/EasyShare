using EasyShare.Models;

namespace EasyShare.Services;

/// <summary>
/// Queue-specific facade over the reusable encrypted-file primitive.
/// </summary>
public sealed class UploadPayloadStorage : EncryptedFileStore
{
    public UploadPayloadStorage(
        AppDataPaths paths,
        UploadPayloadStorageOptions? options = null,
        IUserDataProtector? protector = null)
        : base(
            (paths ?? throw new ArgumentNullException(nameof(paths))).UploadQueueDirectory,
            paths.UploadPayloadKeyPath,
            options,
            protector,
            ".upload")
    {
    }

    public string CreatePayloadPath() => CreateFilePath();
}
