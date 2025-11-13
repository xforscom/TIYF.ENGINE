using ReconciliationProbe;

static (string Root, string Output, string? Adapter, string? Account) ParseArgs(string[] args)
{
    string? root = null;
    string output = "proof-artifacts/reconciliation";
    string? adapter = null;
    string? account = null;

    for (var i = 0; i < args.Length; i++)
    {
        var current = args[i];
        if (string.Equals(current, "--root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            root = args[++i];
            continue;
        }
        if (string.Equals(current, "--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            output = args[++i];
            continue;
        }
        if (string.Equals(current, "--adapter", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            adapter = args[++i];
            continue;
        }
        if (string.Equals(current, "--account", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            account = args[++i];
            continue;
        }
        throw new ArgumentException($"Unrecognized or incomplete argument '{current}'.");
    }

    if (string.IsNullOrWhiteSpace(root))
    {
        throw new ArgumentException("--root must be specified.");
    }

    return (Path.GetFullPath(root), Path.GetFullPath(output), adapter, account);
}

var (root, output, adapter, account) = ParseArgs(args);
var summary = ReconciliationProbeRunner.Analyze(root, adapter, account);
ReconciliationProbeRunner.WriteArtifacts(summary, output);

Console.WriteLine($"ReconciliationProbe completed root={root} records={summary.TotalRecords} mismatches={summary.MismatchesTotal}");
Console.WriteLine($"Artifacts written to {output}");
