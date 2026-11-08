using Chameleon.CatalogTool;
using Chameleon.Host.Plugins;

// catalog <packagesDir> <outputJson> [--providers <dir>]
// catalog --from-sources <src1,src2> --packages <Id@ver,...> --providers <dir> --merge <catalogJson>
const string Usage = "usage: catalog <packagesDir> <outputJson> [--providers <dir>] | catalog --from-sources <src,...> --packages <Id@ver,...> --providers <dir> --merge <catalogJson>";
var positional = new List<string>();
var options = new Dictionary<string, string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--providers" or "--from-sources" or "--packages" or "--merge")
    {
        if (i + 1 >= args.Length) { Console.Error.WriteLine($"missing value for {args[i]}"); return 2; }
        options[args[i]] = args[++i];
    }
    else if (args[i].StartsWith("--")) { Console.Error.WriteLine($"unknown argument {args[i]}\n{Usage}"); return 2; }
    else positional.Add(args[i]);
}

if (options.ContainsKey("--from-sources"))
{
    if (positional.Count != 0 || !options.ContainsKey("--packages") || !options.ContainsKey("--providers") || !options.ContainsKey("--merge"))
    { Console.Error.WriteLine(Usage); return 2; }
    string[] Split(string v) => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return await CatalogMerge.FromSourcesAsync(Split(options["--from-sources"]), Split(options["--packages"]), options["--providers"], options["--merge"], default);
}

if (positional.Count != 2 || options.ContainsKey("--packages") || options.ContainsKey("--merge")) { Console.Error.WriteLine(Usage); return 2; }
var entries = CatalogBuilder.Build(positional[0], options.GetValueOrDefault("--providers"));
File.WriteAllText(positional[1], Catalog.Serialize(entries) + "\n");
Console.WriteLine($"{entries.Count} connector(s) written to {positional[1]}");
return 0;
