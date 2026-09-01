using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

if (args.Length < 4)
    throw new ArgumentException("Usage: ThematicSplitter <source.cs> <recovery.cs> <output-dir> <main-source-name>");

var sourcePath = Path.GetFullPath(args[0]);
var recoveryPath = Path.GetFullPath(args[1]);
var outputDir = Path.GetFullPath(args[2]);
var mainSourceName = args[3];

Directory.CreateDirectory(outputDir);
foreach (var f in Directory.EnumerateFiles(outputDir, "*.cs", SearchOption.TopDirectoryOnly))
    File.Delete(f);

var inputFiles = new[] { sourcePath, recoveryPath };
var allDecls = new List<DeclInfo>();
var globalUsingTexts = new SortedSet<string>(StringComparer.Ordinal);
var globalExternTexts = new SortedSet<string>(StringComparer.Ordinal);
var sourceIndex = 0;

foreach (var input in inputFiles)
{
    var text = await File.ReadAllTextAsync(input, Encoding.UTF8);
    var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest));
    var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    if (errors.Count > 0)
    {
        Console.WriteLine($"SOURCE_PARSE_ERRORS in {Path.GetFileName(input)} = {errors.Count}");
        foreach (var d in errors.Take(50)) Console.WriteLine(d.ToString());
        return 2;
    }

    var root = await tree.GetRootAsync();
    foreach (var ext in root.ChildNodes().OfType<ExternAliasDirectiveSyntax>())
        globalExternTexts.Add(ext.ToFullString().TrimEnd());
    foreach (var u in root.ChildNodes().OfType<UsingDirectiveSyntax>())
        globalUsingTexts.Add(u.ToFullString().TrimEnd());

    Collect(root.ChildNodes().OfType<MemberDeclarationSyntax>(), "", new List<string>(), input);
    sourceIndex++;
}

void Collect(IEnumerable<MemberDeclarationSyntax> members, string ns, List<string> inheritedUsings, string input)
{
    var localUsings = new List<string>(inheritedUsings);

    foreach (var member in members)
    {
        if (member is UsingDirectiveSyntax u)
        {
            localUsings.Add(u.ToFullString().TrimEnd());
            continue;
        }

        if (member is NamespaceDeclarationSyntax blockNs)
        {
            var nestedUsings = new List<string>(localUsings);
            nestedUsings.AddRange(blockNs.Members.OfType<UsingDirectiveSyntax>().Select(x => x.ToFullString().TrimEnd()));
            Collect(blockNs.Members, CombineNamespace(ns, blockNs.Name.ToString()), nestedUsings, input);
            continue;
        }

        if (member is FileScopedNamespaceDeclarationSyntax fileNs)
        {
            var nestedUsings = new List<string>(localUsings);
            nestedUsings.AddRange(fileNs.Members.OfType<UsingDirectiveSyntax>().Select(x => x.ToFullString().TrimEnd()));
            Collect(fileNs.Members, CombineNamespace(ns, fileNs.Name.ToString()), nestedUsings, input);
            continue;
        }

        if (member is BaseTypeDeclarationSyntax baseType)
        {
            var name = GetIdentifier(baseType);
            if (name == null) continue;
            allDecls.Add(new DeclInfo(
                ns,
                name,
                member.ToFullString(),
                Distinct(localUsings),
                input,
                allDecls.Count));
            continue;
        }

        if (member is DelegateDeclarationSyntax del)
        {
            allDecls.Add(new DeclInfo(
                ns,
                del.Identifier.Text,
                member.ToFullString(),
                Distinct(localUsings),
                input,
                allDecls.Count));
        }
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
    if (d.Name.Equals("MultiplayerCampaignSubModule", StringComparison.Ordinal)) return "MultiplayerCampaignSubModule.cs";

    var n = d.Name;
    var lower = n.ToLowerInvariant();
    var text = d.Text;

    if (lower.Contains("ui") || lower.Contains("gui") || lower.Contains("screen") || lower.Contains("viewmodel") || lower.Contains("menu"))
        return "MpcUI.cs";

    if (lower.Contains("save") || lower.Contains("recovery") || lower.Contains("load"))
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

var grouped = allDecls
    .GroupBy(d => (File: Classify(d), Namespace: d.Namespace), d => d)
    .OrderBy(g => g.Key.File, StringComparer.OrdinalIgnoreCase)
    .ThenBy(g => g.Key.Namespace, StringComparer.OrdinalIgnoreCase)
    .ToList();

var expected = allDecls.Count;
var emitted = 0;
var duplicateKeys = allDecls
    .GroupBy(d => (d.Namespace, d.Name))
    .Where(g => g.Count() > 1)
    .Select(g => $"{g.Key.Namespace}.{g.Key.Name} x{g.Count()}")
    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
    .ToList();

foreach (var fileGroup in grouped.GroupBy(g => g.Key.File, StringComparer.OrdinalIgnoreCase))
{
    var sb = new StringBuilder();
    sb.AppendLine("// Modularized from MultiplayerCampaignSubModule.cs; declarations preserved and grouped by responsibility.");
    foreach (var ext in globalExternTexts) sb.AppendLine(ext);
    foreach (var u in globalUsingTexts) sb.AppendLine(u);
    sb.AppendLine();

    foreach (var nsGroup in fileGroup.OrderBy(g => g.Key.Namespace, StringComparer.OrdinalIgnoreCase))
    {
        if (!string.IsNullOrWhiteSpace(nsGroup.Key.Namespace))
        {
            sb.Append("namespace ").Append(nsGroup.Key.Namespace).AppendLine();
            sb.AppendLine("{");
        }

        foreach (var usingText in nsGroup.SelectMany(x => x.SelectMany(d => d.Usings)).Distinct(StringComparer.Ordinal))
        {
            if (!globalUsingTexts.Contains(usingText)) sb.AppendLine(usingText);
        }

        foreach (var d in nsGroup.OrderBy(x => x.Order))
        {
            sb.AppendLine(d.Text.TrimEnd());
            sb.AppendLine();
            emitted++;
        }

        if (!string.IsNullOrWhiteSpace(nsGroup.Key.Namespace)) sb.AppendLine("}");
        sb.AppendLine();
    }

    File.WriteAllText(Path.Combine(outputDir, fileGroup.Key), sb.ToString(), new UTF8Encoding(false));
}

if (emitted != expected)
    throw new InvalidOperationException($"Declaration preservation failed: expected {expected}, emitted {emitted}.");

var mainPath = Path.Combine(outputDir, mainSourceName);
if (!File.Exists(mainPath))
    throw new InvalidOperationException("Main submodule output was not generated.");

var map = new StringBuilder();
map.AppendLine("THEMATIC MODULARIZATION MAP");
map.AppendLine($"DECLARATIONS={expected}");
map.AppendLine($"EMITTED={emitted}");
map.AppendLine($"FILES={grouped.Select(g => g.Key.File).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
map.AppendLine();
foreach (var d in allDecls.OrderBy(d => d.Order))
    map.AppendLine($"{d.Namespace}.{d.Name} -> {Classify(d)}");
map.AppendLine();
map.AppendLine("DUPLICATE DECLARATIONS:");
foreach (var x in duplicateKeys) map.AppendLine(x);
await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(outputDir)!, "REFACTOR_MAP.txt"), map.ToString(), new UTF8Encoding(false));

Console.WriteLine(map.ToString());
Console.WriteLine($"MODULAR_FILES={grouped.Select(g => g.Key.File).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
Console.WriteLine($"DECLARATIONS={expected}");
Console.WriteLine($"EMITTED={emitted}");

record DeclInfo(string Namespace, string Name, string Text, List<string> Usings, string SourceFile, int Order);
