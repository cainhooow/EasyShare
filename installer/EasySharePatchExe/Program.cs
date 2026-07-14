using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using EasyShare.Services;

const string installScript = "Install-EasyShare.ps1";
const string certificateName = "EasyShare_TestCertificate.cer";
const string legacyPackageIdentityName = "AAD584E5-8AD2-4CE5-9C65-B1C66E02383A";

if (args.Length == 1 && string.Equals(args[0], "--metadata-json", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        Console.WriteLine(JsonSerializer.Serialize(
            ReadEmbeddedPatchMetadata(),
            PatchMetadataJsonContext.Default.IncrementalPatchMetadata));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Nao foi possivel ler os metadados do patch: {ex.Message}");
        return 2;
    }
}

if (args.Length == 3 && string.Equals(args[0], "--verify-base-target-json", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        Console.WriteLine(JsonSerializer.Serialize(
            VerifyEmbeddedPatch(args[1], args[2]),
            PatchMetadataJsonContext.Default.IncrementalPatchMetadata));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Nao foi possivel validar o patch completo: {ex.Message}");
        return 2;
    }
}

if (args.Length != 0)
{
    Console.Error.WriteLine("Uso: EasySharePatch.exe [--metadata-json | --verify-base-target-json <base.msix> <target.msix>]");
    return 2;
}

var tempRoot = Path.Combine(Path.GetTempPath(), "EasySharePatch-" + Guid.NewGuid().ToString("N"));
var logRoot = Path.Combine(Path.GetTempPath(), "EasyShareInstallerLogs");
var logPath = Path.Combine(logRoot, $"patch-{DateTime.Now:yyyyMMdd-HHmmss}.log");
Directory.CreateDirectory(tempRoot);
Directory.CreateDirectory(logRoot);

try
{
    var patchResource = GetPatchResourceName();

    var patchPath = ExtractResource(patchResource, tempRoot);
    var metadata = IncrementalPatch.ReadMetadata(patchPath);
    var packageCache = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyShare",
        "Packages",
        metadata.BaseFileName);
    if (!File.Exists(packageCache))
    {
        throw new FileNotFoundException(
            "O pacote base desta atualizacao nao esta no cache local. Use a recuperacao manual na pagina da release.",
            packageCache);
    }

    var targetPackage = Path.Combine(tempRoot, metadata.TargetFileName);
    IncrementalPatch.Apply(packageCache, patchPath, targetPackage);
    ExtractResource(installScript, tempRoot);
    ExtractResource(certificateName, tempRoot);
    StopRunningEasyShareProcesses();

    var scriptPath = Path.Combine(tempRoot, installScript);
    var startInfo = new ProcessStartInfo
    {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -PackagePath \"{targetPackage}\" -SkipWinFspInstall -LogPath \"{logPath}\"",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Nao foi possivel iniciar o PowerShell.");
    process.OutputDataReceived += (_, args) =>
    {
        if (args.Data is not null)
        {
            Console.WriteLine(args.Data);
        }
    };
    process.ErrorDataReceived += (_, args) =>
    {
        if (args.Data is not null)
        {
            Console.Error.WriteLine(args.Data);
        }
    };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"O instalador da atualizacao retornou o codigo {process.ExitCode}. Consulte o log para obter detalhes.");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Falha ao aplicar a atualizacao incremental:");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine($"Log: {logPath}");
    ReportFailure(ex, logPath);
    return 2;
}
finally
{
    try
    {
        Directory.Delete(tempRoot, recursive: true);
    }
    catch
    {
        // Temporary files can be removed by Windows later.
    }
}

static string ExtractResource(string resourceName, string targetDirectory)
{
    var assembly = Assembly.GetExecutingAssembly();
    using var resource = assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException($"Recurso ausente: {resourceName}");
    var targetPath = Path.Combine(targetDirectory, Path.GetFileName(resourceName));
    using var output = File.Create(targetPath);
    resource.CopyTo(output);
    return targetPath;
}

static string GetPatchResourceName()
{
    var patchResources = Assembly.GetExecutingAssembly()
        .GetManifestResourceNames()
        .Where(name => name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    return patchResources.Length == 1
        ? patchResources[0]
        : throw new InvalidOperationException(
            $"Era esperado exatamente um recurso de patch incremental, mas foram encontrados {patchResources.Length}.");
}

static IncrementalPatchMetadata ReadEmbeddedPatchMetadata()
{
    var metadataRoot = Path.Combine(Path.GetTempPath(), "EasySharePatchMetadata-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(metadataRoot);
    try
    {
        var patchPath = ExtractResource(GetPatchResourceName(), metadataRoot);
        return IncrementalPatch.ReadMetadata(patchPath);
    }
    finally
    {
        try
        {
            Directory.Delete(metadataRoot, recursive: true);
        }
        catch
        {
            // The metadata probe must not fail only because Windows retained a temporary handle.
        }
    }
}

static IncrementalPatchMetadata VerifyEmbeddedPatch(string basePackagePath, string targetPackagePath)
{
    if (!File.Exists(basePackagePath))
    {
        throw new FileNotFoundException("O pacote base informado nao foi encontrado.", basePackagePath);
    }

    if (!File.Exists(targetPackagePath))
    {
        throw new FileNotFoundException("O pacote alvo informado nao foi encontrado.", targetPackagePath);
    }

    var verificationRoot = Path.Combine(Path.GetTempPath(), "EasySharePatchVerification-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(verificationRoot);
    try
    {
        var patchPath = ExtractResource(GetPatchResourceName(), verificationRoot);
        var reconstructedPackagePath = Path.Combine(verificationRoot, "reconstructed.msix");
        var metadata = IncrementalPatch.Apply(basePackagePath, patchPath, reconstructedPackagePath);
        if (!FilesAreIdentical(reconstructedPackagePath, targetPackagePath))
        {
            throw new InvalidDataException("O patch nao reconstruiu o pacote alvo byte a byte.");
        }

        return metadata;
    }
    finally
    {
        try
        {
            Directory.Delete(verificationRoot, recursive: true);
        }
        catch
        {
            // A failed cleanup does not invalidate an otherwise complete verification.
        }
    }
}

static bool FilesAreIdentical(string leftPath, string rightPath)
{
    using var left = File.OpenRead(leftPath);
    using var right = File.OpenRead(rightPath);
    if (left.Length != right.Length)
    {
        return false;
    }

    var leftBuffer = new byte[128 * 1024];
    var rightBuffer = new byte[leftBuffer.Length];
    while (true)
    {
        var leftRead = left.Read(leftBuffer);
        var rightRead = right.Read(rightBuffer);
        if (leftRead != rightRead)
        {
            return false;
        }

        if (leftRead == 0)
        {
            return true;
        }

        if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
        {
            return false;
        }
    }
}

static void StopRunningEasyShareProcesses()
{
    foreach (var process in Process.GetProcessesByName("EasyShare"))
    {
        using (process)
        {
            if (!IsLegacyEasyShareProcess(process))
            {
                continue;
            }

            try
            {
                if (process.CloseMainWindow() && process.WaitForExit(3000))
                {
                    continue;
                }

                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                Console.Error.WriteLine($"Nao foi possivel encerrar uma instancia antiga do EasyShare: {ex.Message}");
            }
        }
    }
}

static bool IsLegacyEasyShareProcess(Process process)
{
    try
    {
        var executablePath = process.MainModule?.FileName;
        var installDirectory = string.IsNullOrWhiteSpace(executablePath)
            ? null
            : Path.GetDirectoryName(executablePath);
        var manifestPath = string.IsNullOrWhiteSpace(installDirectory)
            ? null
            : Path.Combine(installDirectory, "AppxManifest.xml");
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return false;
        }

        var manifest = XDocument.Load(manifestPath, LoadOptions.None);
        var identityName = manifest.Root?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Identity")?
            .Attribute("Name")?
            .Value;
        return string.Equals(identityName, legacyPackageIdentityName, StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or XmlException)
    {
        return false;
    }
}

static void ReportFailure(Exception exception, string logPath)
{
    try
    {
        File.AppendAllText(
            logPath,
            $"[{DateTimeOffset.Now:O}] Falha ao aplicar a atualizacao incremental.{Environment.NewLine}{exception}{Environment.NewLine}");
    }
    catch
    {
        // The dialog still explains the failure when the log cannot be written.
    }

    try
    {
        _ = MessageBox(
            IntPtr.Zero,
            $"Nao foi possivel aplicar a atualizacao incremental.{Environment.NewLine}{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}Use o instalador manual na pagina da release para recuperar esta instalacao.{Environment.NewLine}{Environment.NewLine}Log: {logPath}",
            "EasyShare - atualizacao",
            0x00000010);
    }
    catch
    {
        // Console and log output remain available on systems without an interactive desktop.
    }
}

[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
static extern int MessageBox(IntPtr windowHandle, string text, string caption, uint type);

[JsonSerializable(typeof(IncrementalPatchMetadata))]
internal partial class PatchMetadataJsonContext : JsonSerializerContext;
