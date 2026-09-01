using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

internal static class Program
{
    private sealed record DeclInfo(string Namespace, string Name, string Text, List<string> Usings, string SourceFile, int Order);

    private static readonly string[] TargetFiles =
    {
        "MultiplayerCampaignSubModule.cs", "MpcCore.cs", "MpcNetwork.cs", "MpcWorld.cs", "MpcPlayers.cs",
        "MpcCampaign.cs", "MpcParty.cs", "MpcUI.cs", "MpcSaveRecovery.cs", "MpcPatches.cs"
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length < 2)
                throw new ArgumentException("Usage: ThematicSplitter <source-file-or-directory> <output-directory> [recovery-file]");

            string sourcePath = Path.GetFullPath(args[0]);
            string outputDir = Path.GetFullPath(args[1]);
            string? optionalRecovery = args.Length >= 3 ? Path.GetFullPath(args[2]) : null;

            var inputPaths = new List<string>();
            if (Directory.Exists(sourcePath))
                inputPaths.AddRange(Directory.EnumerateFiles(sourcePath, "*.cs", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            else
            {
                inputPaths.Add(sourcePath);
                if (!string.IsNullOrWhiteSpace(optionalRecovery) && File.Exists(optionalRecovery)) inputPaths.Add(optionalRecovery);
            }

            if (inputPaths.Count == 0)
                throw new InvalidOperationException("No C# source files were found for thematic modularization.");

            var declarations = new List<DeclInfo>();
            var globalUsings = new SortedSet<string>(StringComparer.Ordinal);
            var globalExterns = new SortedSet<string>(StringComparer.Ordinal);
            int order = 0;

            foreach (string inputPath in inputPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string text = await File.ReadAllTextAsync(inputPath, Encoding.UTF8);
                var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest));
                var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                if (errors.Count > 0)
                {
                    Console.WriteLine($"SOURCE_PARSE_ERRORS={errors.Count} FILE={Path.GetFileName(inputPath)}");
                    foreach (var error in errors.Take(50)) Console.WriteLine(error);
                    return 2;
                }

                var root = tree.GetCompilationUnitRoot();
                foreach (var ext in root.Externs) globalExterns.Add(ext.ToFullString().TrimEnd());
                foreach (var use in root.Usings) globalUsings.Add(use.ToFullString().TrimEnd());
                CollectMembers(root.Members, string.Empty, new List<string>(), inputPath, declarations, ref order);
            }

            Directory.CreateDirectory(outputDir);
            foreach (string file in Directory.EnumerateFiles(outputDir, "*.cs", SearchOption.TopDirectoryOnly)) File.Delete(file);
            string oldSplit = Path.Combine(outputDir, "split");
            if (Directory.Exists(oldSplit)) Directory.Delete(oldSplit, true);

            var classified = declarations.Select(d => (Declaration: d, File: Classify(d))).ToList();
            var missingClasses = classified.Where(x => string.IsNullOrWhiteSpace(x.File)).ToList();
            if (missingClasses.Count > 0)
                throw new InvalidOperationException($"Unclassified declarations: {string.Join(", ", missingClasses.Select(x => x.Declaration.Name))}");

            foreach (string file in TargetFiles)
            {
                var group = classified.Where(x => string.Equals(x.File, file, StringComparison.OrdinalIgnoreCase)).Select(x => x.Declaration).OrderBy(d => d.Order).ToList();
                if (group.Count == 0) continue;

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

            var expected = declarations.GroupBy(d => (d.Namespace, d.Name)).ToDictionary(g => g.Key, g => g.Count());
            var emitted = new List<DeclInfo>();
            foreach (string file in TargetFiles)
            {
                string path = Path.Combine(outputDir, file);
                if (!File.Exists(path)) continue;
                string generated = await File.ReadAllTextAsync(path, Encoding.UTF8);
                var tree = CSharpSyntaxTree.ParseText(generated, new CSharpParseOptions(LanguageVersion.Latest));
                var parseErrors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                if (parseErrors.Count > 0)
                {
                    Console.WriteLine($"GENERATED_PARSE_ERRORS={parseErrors.Count} FILE={file}");
                    foreach (var error in parseErrors.Take(50)) Console.WriteLine(error);
                    return 3;
                }
                emitted.AddRange(ExtractDeclarations(tree.GetCompilationUnitRoot(), file));
            }

            var actual = emitted.GroupBy(d => (d.Namespace, d.Name)).ToDictionary(g => g.Key, g => g.Count());
            if (expected.Count != actual.Count || expected.Any(kv => !actual.TryGetValue(kv.Key, out int count) || count != kv.Value))
            {
                Console.WriteLine("DECLARATION_PRESERVATION_FAILURE");
                foreach (var kv in expected)
                {
                    int count = actual.TryGetValue(kv.Key, out int value) ? value : 0;
                    if (count != kv.Value) Console.WriteLine($"MISSING_OR_MISMATCH={kv.Key.Namespace}.{kv.Key.Name} expected={kv.Value} actual={count}");
                }
                foreach (var kv in actual)
                    if (!expected.ContainsKey(kv.Key)) Console.WriteLine($"UNEXPECTED={kv.Key.Namespace}.{kv.Key.Name} actual={kv.Value}");
                return 4;
            }

            if (!File.Exists(Path.Combine(outputDir, "MultiplayerCampaignSubModule.cs"))) return 5;
            string recoveryOutput = Path.Combine(outputDir, "MpcSaveRecovery.cs");
            if (!File.Exists(recoveryOutput)) return 6;
            string recoveryText = await File.ReadAllTextAsync(recoveryOutput, Encoding.UTF8);
            if (!recoveryText.Contains("MpcRecoveryRuntime", StringComparison.Ordinal) || !recoveryText.Contains("MpcSaveTransferPatch", StringComparison.Ordinal))
            {
                Console.WriteLine("RECOVERY_MERGED=FALSE");
                return 6;
            }

            var existingFiles = TargetFiles.Where(f => File.Exists(Path.Combine(outputDir, f))).ToList();
            var map = new StringBuilder();
            map.AppendLine("THEMATIC MPC REFACTOR");
            map.AppendLine($"DECLARATIONS={declarations.Count}");
            map.AppendLine($"EMITTED={emitted.Count}");
            map.AppendLine($"FILES={existingFiles.Count}");
            map.AppendLine("RECOVERY_MERGED=TRUE");
            map.AppendLine();
            map.AppendLine("FILES:");
            foreach (var file in existingFiles) map.AppendLine(file);
            map.AppendLine();
            map.AppendLine("DECLARATION MAP:");
            foreach (var item in classified.OrderBy(x => x.Declaration.Order)) map.AppendLine($"{item.Declaration.Namespace}.{item.Declaration.Name} -> {item.File}");

            await File.WriteAllTextAsync(Path.Combine(outputDir, "REFACTOR_MAP.txt"), map.ToString(), new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(outputDir, "BUILD_BASELINE.txt"), $"Declarations preserved: {declarations.Count}\nDeclarations emitted: {emitted.Count}\nFiles generated: {existingFiles.Count}\n", new UTF8Encoding(false));
            Console.WriteLine(map.ToString());
            Console.WriteLine($"MODULAR_FILES={existingFiles.Count}");
            Console.WriteLine($"DECLARATIONS={declarations.Count}");
            Console.WriteLine($"EMITTED={emitted.Count}");
            Console.WriteLine("THEMATIC_SPLIT_SUCCESS=TRUE");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("THEMATIC_SPLITTER_EXCEPTION");
            Console.WriteLine(ex);
            return 99;
        }
    }

    private static void CollectMembers(IEnumerable<MemberDeclarationSyntax> members, string namespaceName, List<string> inheritedUsings, string inputPath, List<DeclInfo> declarations, ref int order)
    {
        foreach (var member in members)
        {
            if (member is NamespaceDeclarationSyntax ns)
            {
                var nested = new List<string>(inheritedUsings);
                nested.AddRange(ns.Usings.Select(u => u.ToFullString().TrimEnd()));
                CollectMembers(ns.Members, CombineNamespace(namespaceName, ns.Name.ToString()), nested, inputPath, declarations, ref order);
                continue;
            }
            if (member is FileScopedNamespaceDeclarationSyntax fns)
            {
                var nested = new List<string>(inheritedUsings);
                nested.AddRange(fns.Usings.Select(u => u.ToFullString().TrimEnd()));
                CollectMembers(fns.Members, CombineNamespace(namespaceName, fns.Name.ToString()), nested, inputPath, declarations, ref order);
                continue;
            }
            if (member is BaseTypeDeclarationSyntax type)
            {
                string? name = GetIdentifier(type);
                if (name != null) declarations.Add(new DeclInfo(namespaceName, name, member.ToFullString(), Distinct(inheritedUsings), inputPath, order++));
                continue;
            }
            if (member is DelegateDeclarationSyntax del)
                declarations.Add(new DeclInfo(namespaceName, del.Identifier.Text, member.ToFullString(), Distinct(inheritedUsings), inputPath, order++));
        }
    }

    private static IEnumerable<DeclInfo> ExtractDeclarations(CompilationUnitSyntax root, string outputFile)
    {
        var result = new List<DeclInfo>();
        void Visit(IEnumerable<MemberDeclarationSyntax> members, string ns)
        {
            foreach (var member in members)
            {
                if (member is NamespaceDeclarationSyntax n) { Visit(n.Members, CombineNamespace(ns, n.Name.ToString())); continue; }
                if (member is FileScopedNamespaceDeclarationSyntax f) { Visit(f.Members, CombineNamespace(ns, f.Name.ToString())); continue; }
                if (member is BaseTypeDeclarationSyntax type) { var id = GetIdentifier(type); if (id != null) result.Add(new DeclInfo(ns, id, string.Empty, new List<string>(), outputFile, 0)); continue; }
                if (member is DelegateDeclarationSyntax del) result.Add(new DeclInfo(ns, del.Identifier.Text, string.Empty, new List<string>(), outputFile, 0));
            }
        }
        Visit(root.Members, string.Empty);
        return result;
    }

    private static List<string> Distinct(IEnumerable<string> values) => values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToList();
    private static string CombineNamespace(string parent, string child) => string.IsNullOrWhiteSpace(parent) ? child : parent + "." + child;
    private static string? GetIdentifier(BaseTypeDeclarationSyntax type) => type switch { TypeDeclarationSyntax t => t.Identifier.Text, EnumDeclarationSyntax e => e.Identifier.Text, _ => null };

    private static string Classify(DeclInfo d)
    {
        string n = d.Name;
        string lower = n.ToLowerInvariant();
        string text = d.Text;

        if (n.Equals("MultiplayerCampaignSubModule", StringComparison.Ordinal)) return "MultiplayerCampaignSubModule.cs";
        if (n.Equals("InitialMenuPatch", StringComparison.Ordinal) || lower.Contains("screen") || lower.Contains("viewmodel") || lower.EndsWith("vm") || lower.Contains("guistate") || lower.Contains("uistate") || lower.Equals("multiplayercampaignvm")) return "MpcUI.cs";
        if (lower.Equals("mpcsavetransferpatch") || lower.Contains("recovery") || lower.Contains("save") || lower.Contains("load") || text.Contains("MCC_Transfer", StringComparison.Ordinal) || text.Contains("WriteTransferSave", StringComparison.Ordinal)) return "MpcSaveRecovery.cs";
        if (lower.Contains("patch") || text.Contains("HarmonyPatch", StringComparison.Ordinal)) return "MpcPatches.cs";
        if (lower.StartsWith("world") || lower.Contains("worldsync") || lower.Contains("worldtransfer") || lower.Contains("worldready") || lower.Contains("worldstate") || lower.Contains("worldrevision") || lower.Contains("worldsynchron") || lower.Contains("transferreceiver") || lower.Contains("transferhost") || lower.Contains("transferpacket") || lower.Contains("transferprovider") || lower.Contains("transfervalidator")) return "MpcWorld.cs";
        if (lower.Contains("party")) return "MpcParty.cs";
        if (lower.StartsWith("network") || lower.Contains("network") || lower.Contains("packet") || lower.Contains("protocol") || lower.Contains("handshake") || lower.Contains("connection") || lower.StartsWith("session") || lower.Contains("session") || lower.StartsWith("hostclient") || lower.Contains("hostconnection") || lower.Contains("playerready") || lower.Contains("socket") || n.Equals("MultiplayerCampaignHost", StringComparison.Ordinal) || n.Equals("MultiplayerCampaignConnection", StringComparison.Ordinal) || n.Equals("MultiplayerCampaignStatus", StringComparison.Ordinal)) return "MpcNetwork.cs";
        if (lower.StartsWith("remoteplayer") || lower.StartsWith("localplayer") || lower.StartsWith("campaignplayer") || lower.StartsWith("character") || lower.StartsWith("playersnapshot") || lower.StartsWith("snapshot") || lower.Contains("remoteplayer") || lower.Contains("playeridentity") || n.Equals("MultiplayerCampaignPlayers", StringComparison.Ordinal)) return "MpcPlayers.cs";
        if (lower.Contains("campaign") || lower.Contains("readiness") || lower.Contains("tick") || lower.Contains("startup") || lower.Contains("initialization") || lower.Contains("thread") || n.Equals("MultiplayerCampaignBehavior", StringComparison.Ordinal) || n.Equals("MultiplayerCampaignBehaviorV2", StringComparison.Ordinal)) return "MpcCampaign.cs";
        return "MpcCore.cs";
    }
}
