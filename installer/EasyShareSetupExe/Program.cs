using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

const string installScript = "Install-EasyShare.ps1";
const string appPackageName = "EasyShare_1.0.26.0_x64.msix";
const string windowsAppRuntimePackageName = "Microsoft.WindowsAppRuntime.2.msix";
const string machinePrerequisitesArgument = "--machine-prerequisites";

if (args.Length == 1 && string.Equals(args[0], "--metadata-json", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        Console.WriteLine(JsonSerializer.Serialize(
            ReadEmbeddedPackageMetadata(),
            SetupMetadataJsonContext.Default.EmbeddedPackageMetadata));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Nao foi possivel ler os metadados do pacote incorporado: {ex.Message}");
        return 2;
    }
}

var machinePrerequisitesOnly = args.Length == 1 &&
    string.Equals(args[0], machinePrerequisitesArgument, StringComparison.OrdinalIgnoreCase);
if (machinePrerequisitesOnly)
{
    if (!IsAdministrator())
    {
        Console.Error.WriteLine("A preparacao dos pre-requisitos requer permissao de administrador.");
        return 1;
    }

    return RunMachinePrerequisites();
}

if (args.Length != 0)
{
    Console.Error.WriteLine("Uso: EasyPointShareSetup.exe [--metadata-json]");
    return 2;
}

var prerequisiteExitCode = IsAdministrator()
    ? RunMachinePrerequisites()
    : RunElevatedMachinePrerequisites();
if (prerequisiteExitCode != 0)
{
    Console.Error.WriteLine($"A preparacao dos pre-requisitos terminou com codigo {prerequisiteExitCode}.");
    return prerequisiteExitCode;
}

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

    var exitCode = RunInstallScript(
        Path.Combine(tempRoot, installScript),
        logPath,
        machinePrerequisitesOnly: false);
    if (exitCode != 0)
    {
        Console.Error.WriteLine($"Instalacao encerrada com codigo {exitCode}. Log: {logPath}");
        return exitCode;
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
    var expectedResources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [installScript] = installScript,
        [appPackageName] = appPackageName,
        ["EasyShare_TestCertificate.cer"] = "EasyShare_TestCertificate.cer",
        [windowsAppRuntimePackageName] = windowsAppRuntimePackageName,
        ["winfsp-2.1.25156.msi"] = "winfsp-2.1.25156.msi"
    };
    var resourceNames = assembly.GetManifestResourceNames();
    var unexpectedResources = resourceNames
        .Where(name => !expectedResources.ContainsKey(name))
        .ToArray();
    var missingResources = expectedResources.Keys
        .Where(expected => !resourceNames.Contains(expected, StringComparer.OrdinalIgnoreCase))
        .ToArray();
    if (resourceNames.Length != expectedResources.Count ||
        unexpectedResources.Length != 0 ||
        missingResources.Length != 0)
    {
        throw new InvalidOperationException(
            $"Payload incorporado inesperado. Ausentes: {string.Join(", ", missingResources)}; " +
            $"nao reconhecidos: {string.Join(", ", unexpectedResources)}.");
    }

    foreach (var resource in expectedResources)
    {
        var resourceName = resourceNames.Single(name =>
            string.Equals(name, resource.Key, StringComparison.OrdinalIgnoreCase));
        var destinationPath = Path.Combine(targetDirectory, resource.Value);
        using (var resourceStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Recurso ausente: {resourceName}"))
        using (var output = File.Create(destinationPath))
        {
            resourceStream.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        using var expectedStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Recurso ausente: {resourceName}");
        using var extractedStream = File.OpenRead(destinationPath);
        var expectedHash = SHA256.HashData(expectedStream);
        var extractedHash = SHA256.HashData(extractedStream);
        if (expectedStream.Length != extractedStream.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedHash, extractedHash))
        {
            throw new InvalidOperationException(
                $"Falha de integridade ao extrair o recurso: {resourceName}");
        }
    }
}

static int RunElevatedMachinePrerequisites()
{
    try
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Nao foi possivel localizar o executavel do instalador.");
        Console.WriteLine("Solicitando permissao de administrador para preparar os pre-requisitos...");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add(machinePrerequisitesArgument);
        using var elevatedProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Nao foi possivel iniciar a preparacao como administrador.");
        elevatedProcess.WaitForExit();
        return elevatedProcess.ExitCode;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"A instalacao requer permissao de administrador: {ex.Message}");
        return 1;
    }
}

static int RunMachinePrerequisites()
{
    var stagingDirectory = CreateSecureMachineStagingDirectory();
    var logPath = Path.Combine(
        stagingDirectory,
        $"machine-prerequisites-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    try
    {
        Console.WriteLine("Preparando pre-requisitos do EasyShare em area protegida...");
        Console.WriteLine($"Log dos pre-requisitos: {logPath}");
        ExtractPayload(stagingDirectory);
        return RunInstallScript(
            Path.Combine(stagingDirectory, installScript),
            logPath,
            machinePrerequisitesOnly: true);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Falha ao preparar os pre-requisitos: {ex.Message}");
        return 1;
    }
    finally
    {
        try
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
        catch
        {
            // A protected staging directory never remains on the executable search path.
        }
    }
}

static int RunInstallScript(string scriptPath, string logPath, bool machinePrerequisitesOnly)
{
    var powershellPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");
    if (!File.Exists(powershellPath))
    {
        throw new FileNotFoundException("O Windows PowerShell nao foi encontrado no System32.", powershellPath);
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = powershellPath,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-ExecutionPolicy");
    startInfo.ArgumentList.Add("Bypass");
    startInfo.ArgumentList.Add("-File");
    startInfo.ArgumentList.Add(scriptPath);
    if (machinePrerequisitesOnly)
    {
        startInfo.ArgumentList.Add("-MachinePrerequisitesOnly");
        startInfo.ArgumentList.Add("-NoLaunch");
    }
    startInfo.ArgumentList.Add("-LogPath");
    startInfo.ArgumentList.Add(logPath);

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Nao foi possivel iniciar o PowerShell.");
    process.OutputDataReceived += (_, eventArgs) =>
    {
        if (eventArgs.Data is not null)
        {
            Console.WriteLine(eventArgs.Data);
        }
    };
    process.ErrorDataReceived += (_, eventArgs) =>
    {
        if (eventArgs.Data is not null)
        {
            Console.Error.WriteLine(eventArgs.Data);
        }
    };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    process.WaitForExit();
    return process.ExitCode;
}

static string CreateSecureMachineStagingDirectory()
{
    if (!IsAdministrator())
    {
        throw new InvalidOperationException("A area protegida so pode ser criada por um administrador.");
    }

    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    var easyShareRoot = Path.Combine(programFiles, "EasyShare");
    var stagingRoot = Path.Combine(easyShareRoot, "InstallerStaging");
    EnsureDirectoryTreeHasNoReparsePoints(programFiles, "EasyShare", "InstallerStaging");

    var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    var security = new DirectorySecurity();
    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    security.SetOwner(administrators);
    security.AddAccessRule(new FileSystemAccessRule(
        administrators,
        FileSystemRights.FullControl,
        inheritance,
        PropagationFlags.None,
        AccessControlType.Allow));
    security.AddAccessRule(new FileSystemAccessRule(
        system,
        FileSystemRights.FullControl,
        inheritance,
        PropagationFlags.None,
        AccessControlType.Allow));

    new DirectoryInfo(easyShareRoot).SetAccessControl(security);
    new DirectoryInfo(stagingRoot).SetAccessControl(security);

    var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
    new DirectoryInfo(stagingDirectory).Create(security);
    if ((File.GetAttributes(stagingDirectory) & FileAttributes.ReparsePoint) != 0)
    {
        throw new InvalidOperationException("A area temporaria protegida foi redirecionada inesperadamente.");
    }

    return stagingDirectory;
}

static void EnsureDirectoryTreeHasNoReparsePoints(string root, params string[] children)
{
    var current = root;
    foreach (var child in children)
    {
        current = Path.Combine(current, child);
        Directory.CreateDirectory(current);
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"A area protegida contem um redirecionamento inesperado: {current}");
        }
    }
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static EmbeddedPackageMetadata ReadEmbeddedPackageMetadata()
{
    var assembly = Assembly.GetExecutingAssembly();
    var resourceNames = assembly.GetManifestResourceNames();
    var expectedResourceNames = new[]
    {
        installScript,
        appPackageName,
        "EasyShare_TestCertificate.cer",
        windowsAppRuntimePackageName,
        "winfsp-2.1.25156.msi"
    };
    var unexpectedResources = resourceNames
        .Where(name => !expectedResourceNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        .ToArray();
    if (resourceNames.Length != expectedResourceNames.Length || unexpectedResources.Length != 0)
    {
        throw new InvalidOperationException(
            $"Foram encontrados recursos incorporados inesperados: {string.Join(", ", unexpectedResources)}.");
    }

    var appResource = GetSingleResourceName(
        resourceNames,
        name => name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase),
        "pacote do aplicativo");
    var runtimeResource = GetSingleResourceName(
        resourceNames,
        name => name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase),
        "pacote do Windows App Runtime");
    var winFspResource = GetSingleResourceName(
        resourceNames,
        name => string.Equals(name, "winfsp-2.1.25156.msi", StringComparison.OrdinalIgnoreCase),
        "instalador do WinFsp");
    var installScriptResource = GetSingleResourceName(
        resourceNames,
        name => string.Equals(name, installScript, StringComparison.OrdinalIgnoreCase),
        "script de instalacao");
    var certificateResource = GetSingleResourceName(
        resourceNames,
        name => string.Equals(name, "EasyShare_TestCertificate.cer", StringComparison.OrdinalIgnoreCase),
        "certificado de sideload");

    var app = ReadResourceMetadata(assembly, appResource);
    var runtime = ReadResourceMetadata(assembly, runtimeResource);
    var winFsp = ReadResourceMetadata(assembly, winFspResource);
    var script = ReadResourceMetadata(assembly, installScriptResource);
    var certificate = ReadResourceMetadata(assembly, certificateResource);
    return new EmbeddedPackageMetadata(
        appPackageName,
        app.Length,
        app.Sha256,
        windowsAppRuntimePackageName,
        runtime.Length,
        runtime.Sha256,
        "winfsp-2.1.25156.msi",
        winFsp.Length,
        winFsp.Sha256,
        installScript,
        script.Length,
        script.Sha256,
        "EasyShare_TestCertificate.cer",
        certificate.Length,
        certificate.Sha256);
}

static string GetSingleResourceName(
    string[] resourceNames,
    Func<string, bool> predicate,
    string description)
{
    var matches = resourceNames.Where(predicate).ToArray();
    if (matches.Length != 1)
    {
        throw new InvalidOperationException(
            $"Era esperado exatamente um {description}, mas foram encontrados {matches.Length}.");
    }

    return matches[0];
}

static ResourceMetadata ReadResourceMetadata(Assembly assembly, string resourceName)
{
    using var resource = assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException($"Recurso ausente: {resourceName}");
    return new ResourceMetadata(resource.Length, Convert.ToHexString(SHA256.HashData(resource)));
}

internal sealed record ResourceMetadata(long Length, string Sha256);

internal sealed record EmbeddedPackageMetadata(
    string FileName,
    long Length,
    string Sha256,
    string RuntimeFileName,
    long RuntimeLength,
    string RuntimeSha256,
    string WinFspFileName,
    long WinFspLength,
    string WinFspSha256,
    string InstallScriptFileName,
    long InstallScriptLength,
    string InstallScriptSha256,
    string CertificateFileName,
    long CertificateLength,
    string CertificateSha256);

[JsonSerializable(typeof(EmbeddedPackageMetadata))]
internal partial class SetupMetadataJsonContext : JsonSerializerContext;
