using System.Diagnostics;
using System.Reflection;
using EasyShare.Services;

const string installScript = "Install-EasyShare.ps1";
const string certificateName = "EasyShare_TestCertificate.cer";

var tempRoot = Path.Combine(Path.GetTempPath(), "EasySharePatch-" + Guid.NewGuid().ToString("N"));
var logRoot = Path.Combine(Path.GetTempPath(), "EasyShareInstallerLogs");
var logPath = Path.Combine(logRoot, $"patch-{DateTime.Now:yyyyMMdd-HHmmss}.log");
Directory.CreateDirectory(tempRoot);
Directory.CreateDirectory(logRoot);

try
{
    var patchResource = Assembly.GetExecutingAssembly()
        .GetManifestResourceNames()
        .FirstOrDefault(name => name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
    if (patchResource is null)
    {
        throw new InvalidOperationException("O recurso do patch incremental nao foi encontrado.");
    }

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
            "O pacote base desta atualizacao nao esta no cache local. Baixe o instalador completo.",
            packageCache);
    }

    var targetPackage = Path.Combine(tempRoot, metadata.TargetFileName);
    IncrementalPatch.Apply(packageCache, patchPath, targetPackage);
    ExtractResource(installScript, tempRoot);
    ExtractResource(certificateName, tempRoot);

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
    return process.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Falha ao aplicar a atualizacao incremental:");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine($"Log: {logPath}");
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
