using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

record DeclInfo(string Namespace, string Name, string Text, List<string> Usings, string SourceFile, int Order);

if (args.Length < 3)
    throw new ArgumentException("Usage: ThematicSplitter <source.cs> <output-dir> [recovery.cs]");

var sourcePath = Path.GetFullPath(args[0]);
var outputDir = Path.GetFullPath(args[1]);
var recoveryPath = args.Length >= 3 ? Path.GetFullPath(args[2]) : null;

var sourceText = await File.ReadAllTextAsync(sourcePath, Encoding.UTF8);
var inputs = new List<(string Path, string Text)> { (sourcePath, sourceText) };
if (!string.IsNullOrWhiteSpace(recoveryPath) && File.Exists(recoveryPath))
    inputs.Add((recoveryPath, await File.ReadAllTextAsync(recoveryPath, Encoding.UTF8)));

var allDecls = new List<DeclInfo>();
var globalUsings = new SortedSet<string>(StringComparer.Ordinal);
var globalExterns = new SortedSet<string>(StringComparer.Ordinal);
int ordinal = 0;

foreach (var input in inputs)
{
    var tree = CSharpSyntaxTree.ParseText(input.Text, new CSharpParseOptions(LanguageVersion.Latest));
    var parseErrors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    if (parseErrors.Count > 0)
    {
        Console.WriteLine($"SOURCE_PARSE_ERRORS={parseErrors.Count} FILE={Path.GetFileName(input.Path)}");
        foreach (var error in parseErrors.Take(50)) Console.WriteLine(error);
        return 2;
    }

    var root = tree.GetCompilationUnitRoot();
    foreach (var ext in root.Externs) globalExterns.Add(ext.ToFullString().TrimEnd());
    foreach (var use in root.Usings) globalUsings.Add(use.ToFullString().TrimEnd());

    CollectMembers(root.Members, "", new List<string>(), input.Path);
}

void CollectMembers(IEnumerable<MemberDeclarationSyntax> members, string namespaceName, List<string> inheritedUsings, string inputPath)
{
    var localUsings = new List<string>(inheritedUsings);

    foreach (var member in members)
    {
        if (member is NamespaceDeclarationSyntax ns)
        {
            var nestedUsings = new List<string>(localUsings);
            nestedUsings.AddRange(ns.Usings.Select(x => x.ToFullString().TrimEnd()));
            CollectMembers(ns.Members, CombineNamespace(namespaceName, ns.Name.ToString()), nestedUsings, inputPath);
            continue;
        }

        if (member is FileScopedNamespaceDeclarationSyntax fns)
        {
            var nestedUsings = new List<string>(localUsings);
            nestedUsings.AddRange(fns.Usings.Select(x => x.ToFullString().TrimEnd()));
            CollectMembers(fns.Members, CombineNamespace(namespaceName, fns.Name.ToString()), nestedUsings, inputPath);
            continue;
        }

        if (member is BaseTypeDeclarationSyntax type)
        {
            string? name = GetIdentifier(type);
            if (name != null)
                allDecls.Add(new DeclInfo(namespaceName, name, member.ToFullString(), Distinct(localUsings), inputPath, ordinal++));
            continue;
        }

        if (member is DelegateDeclarationSyntax del)
            allDecls.Add(new DeclInfo(namespaceName, del.Identifier.Text, member.ToFullString(), Distinct(localUsings), inputPath, ordinal++));
    }
}

static string CombineNamespace(string parent, string child)
    => string.IsNullOrWhiteSpace(parent) ? child : parent + "." + child;

static List<string> Distinct(IEnumerable<string> values)
    => values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();

static string? GetIdentifier(BaseTypeDeclarationSyntax type)
    => type switch
    {
        TypeDeclarationSyntax t => t.Identifier.Text,
        EnumDeclarationSyntax e => e.Identifier.Text,
        _ => null
    };

static string Classify(DeclInfo d)
{
    if (d.Name.Equals("MultiplayerCampaignSubModule", StringComparison.Ordinal))
        return "MultiplayerCampaignSubModule.cs";

    string n = d.Name;
    string lower = n.ToLowerInvariant();
    string text = d.Text;

    if (lower.Contains("ui") || lower.Contains("gui") || lower.Contains("screen") || lower.Contains("viewmodel") || lower.Contains("menu"))
        return "MpcUI.cs";

    if (lower.Contains("save") || lower.Contains("recovery") || lower.Contains("load") || text.Contains("MCC_Transfer", StringComparison.Ordinal) || text.Contains("WriteTransferSave", StringComparison.Ordinal))
        return "MpcSaveRecovery.cs";

    if (lower.Contains("patch") || text.Contains("HarmonyPatch", StringComparison.Ordinal))
        return "MpcPatches.cs";

    if (lower.Contains("network") || lower.Contains("packet") || lower.Contains("protocol") || lower.Contains("handshake") || lower.Contains("connection") || lower.Contains("hostclient") || lower.Contains("session"))
        return "MpcNetwork.cs";

    if (lower.Contains("party"))
        return "MpcParty.cs";

    if (lower.Contains("world") || lower.Contains("transfer") || lower.Contains("synchronization") || lower.Contains("revision"))
        return "MpcWorld.cs";

    if (lower.Contains("remoteplayer") || lower.Contains("player") || lower.Contains("character") || lower.Contains("snapshot") || lower.Contains("identity"))
        return "MpcPlayers.cs";

    if (lower.Contains("campaign") || lower.Contains("readiness") || lower.Contains("tickdispatcher") || lower.Contains("threaddispatcher"))
        return "MpcCampaign.cs";

    return "MpcCore.cs";
}

var groups = allDecls.GroupBy(Classify, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();

var targetFiles = new[]
{
    "MultiplayerCampaignSubModule.cs",
    "MpcCore.cs",
    "MpcNetwork.cs",
    "MpcWorld.cs",
    "MpcPlayers.cs",
    "MpcCampaign.cs",
    "MpcParty.cs",
    "MpcUI.cs",
    "MpcSaveRecovery.cs",
    "MpcPatches.cs"
};

Directory.CreateDirectory(outputDir);
foreach (var file in targetFiles)
{
    var path = Path.Combine(outputDir, file);
    if (File.Exists(path)) File.Delete(path);
}

var oldSplit = Path.Combine(outputDir, "split");
if (Directory.Exists(oldSplit)) Directory.Delete(oldSplit, true);

foreach (var file in targetFiles)
{
    var group = groups.FirstOrDefault(g => string.Equals(g.Key, file, StringComparison.OrdinalIgnoreCase));
    if (group == null) continue;

    var sb = new StringBuilder();
    sb.AppendLine("// Thematic MPC module. Original declarations are preserved and grouped by responsibility.");
    foreach (var ext in globalExterns) sb.AppendLine(ext);
    foreach (var use in globalUsings) sb.AppendLine(use);
    sb.AppendLine();

    foreach (var nsGroup in group.GroupBy(d => d.Namespace, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
    {
        if (!string.IsNullOrWhiteSpace(nsGroup.Key))
        {
            sb.Append("namespace ").Append(nsGroup.Key).AppendLine();
            sb.AppendLine("{");
        }

        foreach (var decl in nsGroup.OrderBy(d => d.Order))
        {
            sb.AppendLine(decl.Text.TrimEnd());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(nsGroup.Key)) sb.AppendLine("}");
        sb.AppendLine();
    }

    File.WriteAllText(Path.Combine(outputDir, file), sb.ToString(), new UTF8Encoding(false));
}

var expectedKeys = allDecls.GroupBy(d => (d.Namespace, d.Name)).ToDictionary(g => g.Key, g => g.Count());
var emittedDecls = new List<DeclInfo>();

foreach (var file in targetFiles)
{
    var path = Path.Combine(outputDir, file);
    if (!File.Exists(path)) continue;
    var generatedText = await File.ReadAllTextAsync(path, Encoding.UTF8);
    var generatedTree = CSharpSyntaxTree.ParseText(generatedText, new CSharpParseOptions(LanguageVersion.Latest));
    var generatedErrors = generatedTree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    if (generatedErrors.Count > 0)
    {
        Console.WriteLine($"GENERATED_PARSE_ERRORS={generatedErrors.Count} FILE={file}");
        foreach (var error in generatedErrors.Take(50)) Console.WriteLine(error);
        return 3;
    }
    emittedDecls.AddRange(ExtractDecls(generatedTree.GetCompilationUnitRoot(), file));
}

static IEnumerable<DeclInfo> ExtractDecls(CompilationUnitSyntax root, string outputFile)
{
    var result = new List<DeclInfo>();
    void Visit(IEnumerable<MemberDeclarationSyntax> members, string ns)
    {
        foreach (var member in members)
        {
            if (member is NamespaceDeclarationSyntax n)
            {
                Visit(n.Members, CombineNamespace(ns, n.Name.ToString()));
                continue;
            }
            if (member is FileScopedNamespaceDeclarationSyntax f)
            {
                Visit(f.Members, CombineNamespace(ns, f.Name.ToString()));
                continue;
            }
            if (member is BaseTypeDeclarationSyntax type)
            {
                var id = GetIdentifier(type);
                if (id != null) result.Add(new DeclInfo(ns, id, "", new List<string>(), outputFile, 0));
                continue;
            }
            if (member is DelegateDeclarationSyntax del)
                result.Add(new DeclInfo(ns, del.Identifier.Text, "", new List<string>(), outputFile, 0));
        }
    }
    Visit(root.Members, "");
    return result;
}

var actualKeys = emittedDecls.GroupBy(d => (d.Namespace, d.Name)).ToDictionary(g => g.Key, g => g.Count());
if (expectedKeys.Count != actualKeys.Count || expectedKeys.Any(kv => !actualKeys.TryGetValue(kv.Key, out var actualCount) || actualCount != kv.Value))
{
    Console.WriteLine("DECLARATION_PRESERVATION_FAILURE");
    foreach (var missing in expectedKeys.Where(kv => !actualKeys.TryGetValue(kv.Key, out var c) || c != kv.Value))
        Console.WriteLine($"MISSING_OR_MISMATCH={missing.Key.Namespace}.{missing.Key.Name} expected={missing.Value} actual={(actualKeys.TryGetValue(missing.Key, out var count) ? count : 0)}");
    return 4;
}

var mainPath = Path.Combine(outputDir, "MultiplayerCampaignSubModule.cs");
if (!File.Exists(mainPath))
{
    Console.WriteLine("MAIN_SUBMODULE_MISSING");
    return 5;
}

var map = new StringBuilder();
map.AppendLine("THEMATIC MPC REFACTOR");
map.AppendLine($"DECLARATIONS={allDecls.Count}");
map.AppendLine($"EMITTED={emittedDecls.Count}");
map.AppendLine($"FILES={groups.Count}");
map.AppendLine();
map.AppendLine("FILES:");
foreach (var g in groups) map.AppendLine(g.Key);
map.AppendLine();
map.AppendLine("DECLARATION MAP:");
foreach (var d in allDecls.OrderBy(d => d.Order)) map.AppendLine($"{d.Namespace}.{d.Name} -> {Classify(d)}");

await File.WriteAllTextAsync(Path.Combine(outputDir, "REFACTOR_MAP.txt"), map.ToString(), new UTF8Encoding(false));
await File.WriteAllTextAsync(Path.Combine(outputDir, "BUILD_BASELINE.txt"), $"Declarations preserved: {allDecls.Count}\nFiles generated: {groups.Count}\n", new UTF8Encoding(false));
Console.WriteLine(map.ToString());
Console.WriteLine($"MODULAR_FILES={groups.Count}");
Console.WriteLine($"DECLARATIONS={allDecls.Count}");
Console.WriteLine($"EMITTED={emittedDecls.Count}");
return 0;
