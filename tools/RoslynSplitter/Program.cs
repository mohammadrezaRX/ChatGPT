using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

if (args.Length < 3)
    throw new ArgumentException("Usage: RoslynSplitter <source.cs> <split-dir> <main-output-dir>");

var sourcePath = Path.GetFullPath(args[0]);
var splitDir = Path.GetFullPath(args[1]);
var mainDir = Path.GetFullPath(args[2]);
Directory.CreateDirectory(splitDir);
Directory.CreateDirectory(mainDir);

var source = await File.ReadAllTextAsync(sourcePath, Encoding.UTF8);
var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
var root = await tree.GetRootAsync();

var diagnostics = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
if (diagnostics.Count > 0)
{
    Console.WriteLine("SOURCE_PARSE_ERRORS=" + diagnostics.Count);
    foreach (var d in diagnostics.Take(50))
        Console.WriteLine(d.ToString());
    return 2;
}

var compileHeader = new StringBuilder();
foreach (var node in root.ChildNodes())
{
    if (node is ExternAliasDirectiveSyntax || node is UsingDirectiveSyntax)
        compileHeader.Append(node.ToFullString());
}

foreach (var f in Directory.EnumerateFiles(splitDir, "*.cs", SearchOption.TopDirectoryOnly))
    File.Delete(f);

var written = new List<string>();
var mainTypeName = "MultiplayerCampaignSubModule";

void WriteType(string typeText, string? namespaceName, string fileName)
{
    var sb = new StringBuilder();
    sb.Append(compileHeader);
    sb.AppendLine();

    if (!string.IsNullOrWhiteSpace(namespaceName))
    {
        sb.Append("namespace ").Append(namespaceName).AppendLine();
        sb.AppendLine("{");
        sb.Append(typeText.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("}");
    }
    else
    {
        sb.Append(typeText.TrimEnd());
        sb.AppendLine();
    }

    var outputPath = fileName.Equals("MultiplayerCampaignSubModule.cs", StringComparison.OrdinalIgnoreCase)
        ? Path.Combine(mainDir, fileName)
        : Path.Combine(splitDir, fileName);

    File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
    written.Add(Path.GetRelativePath(Path.GetDirectoryName(mainDir)!, outputPath));
}

void VisitMembers(IEnumerable<MemberDeclarationSyntax> members, string? namespaceName)
{
    foreach (var member in members)
    {
        switch (member)
        {
            case NamespaceDeclarationSyntax ns:
                VisitMembers(ns.Members, ns.Name.ToString());
                break;

            case FileScopedNamespaceDeclarationSyntax fns:
                VisitMembers(fns.Members, fns.Name.ToString());
                break;

            case BaseTypeDeclarationSyntax baseType:
            {
                var identifier = GetTypeIdentifier(baseType);
                if (identifier == null)
                    continue;

                var fileName = identifier.Equals(mainTypeName, StringComparison.Ordinal)
                    ? "MultiplayerCampaignSubModule.cs"
                    : "Mpc_" + Sanitize(identifier) + ".cs";

                WriteType(member.ToFullString(), namespaceName, fileName);
                break;
            }

            case DelegateDeclarationSyntax del:
                WriteType(member.ToFullString(), namespaceName, "Mpc_" + Sanitize(del.Identifier.Text) + ".cs");
                break;
        }
    }
}

static string? GetTypeIdentifier(BaseTypeDeclarationSyntax type)
{
    return type switch
    {
        TypeDeclarationSyntax t => t.Identifier.Text,
        EnumDeclarationSyntax e => e.Identifier.Text,
        _ => null
    };
}

static string Sanitize(string name)
{
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
        sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
    return sb.ToString();
}

VisitMembers(root.ChildNodes().OfType<MemberDeclarationSyntax>(), null);

if (!written.Any(x => x.EndsWith("MultiplayerCampaignSubModule.cs", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Main MultiplayerCampaignSubModule type was not found.");

var report = new StringBuilder();
report.AppendLine("Roslyn syntax-tree split");
report.AppendLine("Types written: " + written.Count);
foreach (var item in written.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
    report.AppendLine(item.Replace('\\', '/'));

await File.WriteAllTextAsync(Path.Combine(mainDir, "REFACTOR_MAP.txt"), report.ToString(), new UTF8Encoding(false));
Console.WriteLine(report.ToString());
return 0;
