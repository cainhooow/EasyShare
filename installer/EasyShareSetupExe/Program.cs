using System.Diagnostics;
using System.Reflection;

const string installScript = "Install-EasyShare.ps1";
const string appPackageName = "EasyShare_1.0.0.15_x64.msix";

var tempRoot = Path.Combine(Path.GetTempPath(), "EasyShareSetup-" + Guid.NewGuid().ToString("N"));
var logRoot = Path.Combine(Path.GetTempPath(), "EasyShareInstallerLogs");
var logPath = Path.Combine(logRoot, $"setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
Directory.CreateDirectory(tempRoot);
Directory.CreateDirectory(logRoot);
var success = false;

try
{
    Console.WriteLine("Preparando instalador EasyShare...");
    Console.WriteLine($"Log do instalador: {logPath}");
    ExtractPayload(tempRoot);

    var scriptPath = Path.Combine(tempRoot, installScript);
    var startInfo = new ProcessStartInfo
    {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -LogPath \"{logPath}\"",
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
        Console.Error.WriteLine($"Instalacao encerrada com codigo {process.ExitCode}. Log: {logPath}");
        return process.ExitCode;
    }

    Console.WriteLine("EasyShare instalado.");
    success = true;
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Falha ao instalar o EasyShare:");
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine($"Log: {logPath}");
    return 1;
}
finally
{
    if (success)
    {
        try
        {
            Directory.Delete(tempRoot, recursive: true);
        }
        catch
        {
            // Temporary installer files can be cleaned by Windows later.
        }
    }
    else
    {
        Console.Error.WriteLine($"Arquivos temporarios preservados para diagnostico: {tempRoot}");
    }
}

static void ExtractPayload(string targetDirectory)
{
    var assembly = Assembly.GetExecutingAssembly();
    foreach (var resourceName in assembly.GetManifestResourceNames())
    {
        var fileName = resourceName.Split('.').Length > 2 && resourceName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
            ? installScript
            : resourceName;

        if (resourceName.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
        {
            fileName = resourceName.Contains("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase)
                ? "Microsoft.WindowsAppRuntime.2.msix"
                : appPackageName;
        }
        else if (resourceName.EndsWith(".cer", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "EasyShare_TestCertificate.cer";
        }

        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Recurso ausente: {resourceName}");
        using var output = File.Create(Path.Combine(targetDirectory, fileName));
        resource.CopyTo(output);
    }
}
