using EasyShare.Services;

if (args.Length != 7 || !string.Equals(args[0], "build", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: EasySharePatchTool build --base <base-msix> --target <target-msix> --output <patch>");
    return 2;
}

var options = ParseOptions(args[1..]);
var metadata = IncrementalPatch.Build(
    options["--base"],
    options["--target"],
    options["--output"]);

var ratio = metadata.TargetLength == 0
    ? 0
    : metadata.PatchLength * 100d / metadata.TargetLength;
Console.WriteLine($"Patch: {metadata.BaseFileName} -> {metadata.TargetFileName}");
Console.WriteLine($"Base: {metadata.BaseLength:N0} bytes; target: {metadata.TargetLength:N0} bytes");
Console.WriteLine($"Patch: {metadata.PatchLength:N0} bytes ({ratio:F1}% of target)");
Console.WriteLine($"Blocks: {metadata.BlockCount:N0}");
return 0;

static Dictionary<string, string> ParseOptions(string[] values)
{
    if (values.Length != 6)
    {
        throw new ArgumentException("Expected --base, --target and --output options.");
    }

    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (values[index] is not ("--base" or "--target" or "--output") ||
            string.IsNullOrWhiteSpace(values[index + 1]))
        {
            throw new ArgumentException("Expected --base, --target and --output options.");
        }

        options[values[index]] = values[index + 1];
    }

    if (options.Count != 3)
    {
        throw new ArgumentException("Each patch option must be provided exactly once.");
    }

    return options;
}
