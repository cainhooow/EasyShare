using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EasyShare.Services;

public sealed record IncrementalPatchMetadata(
    string BaseFileName,
    string TargetFileName,
    long BaseLength,
    long TargetLength,
    string BaseSha256,
    string TargetSha256,
    int BlockCount,
    long PatchLength);

public static class IncrementalPatch
{
    private const string Magic = "EASYSHAREPATCH1";
    private const byte FormatVersion = 1;
    private const byte CopyBlock = 0;
    private const byte LiteralBlock = 1;
    private const int ChunkSize = 64 * 1024;

    public static IncrementalPatchMetadata Build(
        string basePath,
        string targetPath,
        string patchPath)
    {
        var baseBytes = File.ReadAllBytes(basePath);
        var targetBytes = File.ReadAllBytes(targetPath);
        var baseHash = SHA256.HashData(baseBytes);
        var targetHash = SHA256.HashData(targetBytes);
        var baseChunks = BuildChunkIndex(baseBytes);
        var blocks = new List<PatchBlock>();

        for (var targetOffset = 0; targetOffset < targetBytes.LongLength; targetOffset += ChunkSize)
        {
            var length = (int)Math.Min(ChunkSize, targetBytes.LongLength - targetOffset);
            var key = ChunkKey(targetBytes, targetOffset, length);
            if (baseChunks.TryGetValue(key, out var baseOffset) &&
                BytesEqual(baseBytes, baseOffset, targetBytes, targetOffset, length))
            {
                blocks.Add(PatchBlock.Copy(baseOffset, length));
            }
            else
            {
                blocks.Add(PatchBlock.Literal(targetBytes.AsSpan((int)targetOffset, length).ToArray()));
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(patchPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using (var output = File.Create(patchPath))
        using (var compressed = new BrotliStream(output, CompressionLevel.Optimal))
        using (var writer = new BinaryWriter(compressed, Encoding.UTF8))
        {
            WriteHeader(
                writer,
                Path.GetFileName(basePath),
                Path.GetFileName(targetPath),
                baseBytes.LongLength,
                targetBytes.LongLength,
                baseHash,
                targetHash,
                blocks.Count);

            foreach (var block in blocks)
            {
                writer.Write(block.Mode);
                if (block.Mode == CopyBlock)
                {
                    writer.Write(block.SourceOffset);
                    writer.Write(block.Length);
                }
                else
                {
                    writer.Write(block.Data!.Length);
                    writer.Write(block.Data);
                }
            }
        }

        return new IncrementalPatchMetadata(
            Path.GetFileName(basePath),
            Path.GetFileName(targetPath),
            baseBytes.LongLength,
            targetBytes.LongLength,
            Convert.ToHexString(baseHash),
            Convert.ToHexString(targetHash),
            blocks.Count,
            new FileInfo(patchPath).Length);
    }

    public static IncrementalPatchMetadata ReadMetadata(string patchPath)
    {
        using var input = File.OpenRead(patchPath);
        using var decompressed = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(decompressed, Encoding.UTF8);
        return ReadHeader(reader, new FileInfo(patchPath).Length);
    }

    public static IncrementalPatchMetadata Apply(
        string basePath,
        string patchPath,
        string targetPath)
    {
        using var baseStream = File.OpenRead(basePath);
        using var patchInput = File.OpenRead(patchPath);
        using var decompressed = new BrotliStream(patchInput, CompressionMode.Decompress);
        using var reader = new BinaryReader(decompressed, Encoding.UTF8);
        var metadata = ReadHeader(reader, new FileInfo(patchPath).Length);

        if (baseStream.Length != metadata.BaseLength ||
            !HashMatches(baseStream, metadata.BaseSha256))
        {
            throw new InvalidDataException("The cached base package does not match the incremental patch.");
        }

        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        try
        {
            using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var written = 0L;
                for (var index = 0; index < metadata.BlockCount; index++)
                {
                    var mode = reader.ReadByte();
                    var sourceOffset = mode == CopyBlock ? reader.ReadInt64() : 0;
                    var length = reader.ReadInt32();
                    if (length < 0 || length > ChunkSize || written + length > metadata.TargetLength)
                    {
                        throw new InvalidDataException("The incremental patch contains an invalid block length.");
                    }

                    if (mode == CopyBlock)
                    {
                        if (sourceOffset < 0 || sourceOffset + length > metadata.BaseLength)
                        {
                            throw new InvalidDataException("The incremental patch references data outside the base package.");
                        }

                        baseStream.Position = sourceOffset;
                        CopyExactly(baseStream, output, length);
                    }
                    else if (mode == LiteralBlock)
                    {
                        var data = reader.ReadBytes(length);
                        if (data.Length != length)
                        {
                            throw new InvalidDataException("The incremental patch ended before a literal block was complete.");
                        }

                        output.Write(data);
                    }
                    else
                    {
                        throw new InvalidDataException("The incremental patch contains an unknown block type.");
                    }

                    written += length;
                }

                if (written != metadata.TargetLength)
                {
                    throw new InvalidDataException("The incremental patch did not produce the expected package length.");
                }
            }

            if (!HashMatches(targetPath, metadata.TargetSha256))
            {
                throw new InvalidDataException("The reconstructed package failed SHA-256 verification.");
            }

            return metadata;
        }
        catch
        {
            TryDelete(targetPath);
            throw;
        }
    }

    private static Dictionary<string, long> BuildChunkIndex(byte[] bytes)
    {
        var index = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var offset = 0; offset < bytes.LongLength; offset += ChunkSize)
        {
            var length = (int)Math.Min(ChunkSize, bytes.LongLength - offset);
            index.TryAdd(ChunkKey(bytes, offset, length), offset);
        }

        return index;
    }

    private static string ChunkKey(byte[] bytes, long offset, int length) =>
        $"{length}:{Convert.ToHexString(SHA256.HashData(bytes.AsSpan((int)offset, length)))}";

    private static bool BytesEqual(byte[] left, long leftOffset, byte[] right, long rightOffset, int length) =>
        left.AsSpan((int)leftOffset, length).SequenceEqual(right.AsSpan((int)rightOffset, length));

    private static void WriteHeader(
        BinaryWriter writer,
        string baseFileName,
        string targetFileName,
        long baseLength,
        long targetLength,
        byte[] baseHash,
        byte[] targetHash,
        int blockCount)
    {
        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write(FormatVersion);
        writer.Write(ChunkSize);
        writer.Write(baseLength);
        writer.Write(targetLength);
        writer.Write(baseFileName);
        writer.Write(targetFileName);
        writer.Write(baseHash);
        writer.Write(targetHash);
        writer.Write(blockCount);
    }

    private static IncrementalPatchMetadata ReadHeader(BinaryReader reader, long patchLength)
    {
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length));
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The incremental patch signature is invalid.");
        }

        if (reader.ReadByte() != FormatVersion || reader.ReadInt32() != ChunkSize)
        {
            throw new InvalidDataException("The incremental patch format is not supported.");
        }

        var baseLength = reader.ReadInt64();
        var targetLength = reader.ReadInt64();
        var baseFileName = ValidateFileName(reader.ReadString(), "base");
        var targetFileName = ValidateFileName(reader.ReadString(), "target");
        var baseHash = reader.ReadBytes(32);
        var targetHash = reader.ReadBytes(32);
        var blockCount = reader.ReadInt32();

        if (baseLength < 0 || targetLength < 0 || blockCount < 0 || blockCount > 10_000_000 ||
            baseHash.Length != 32 || targetHash.Length != 32)
        {
            throw new InvalidDataException("The incremental patch header is invalid.");
        }

        return new IncrementalPatchMetadata(
            baseFileName,
            targetFileName,
            baseLength,
            targetLength,
            Convert.ToHexString(baseHash),
            Convert.ToHexString(targetHash),
            blockCount,
            patchLength);
    }

    private static string ValidateFileName(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException($"The incremental patch contains an invalid {label} file name.");
        }

        return value;
    }

    private static bool HashMatches(Stream stream, string expectedSha256)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HashMatches(string path, string expectedSha256)
    {
        using var stream = File.OpenRead(path);
        return HashMatches(stream, expectedSha256);
    }

    private static void CopyExactly(Stream source, Stream target, int length)
    {
        var buffer = new byte[Math.Min(81920, length)];
        var remaining = length;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new InvalidDataException("The incremental patch referenced incomplete base data.");
            }

            target.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A failed cleanup does not hide the patch validation error.
        }
    }

    private sealed record PatchBlock(byte Mode, long SourceOffset, int Length, byte[]? Data)
    {
        public static PatchBlock Copy(long sourceOffset, int length) => new(CopyBlock, sourceOffset, length, null);

        public static PatchBlock Literal(byte[] data) => new(LiteralBlock, 0, data.Length, data);
    }
}
