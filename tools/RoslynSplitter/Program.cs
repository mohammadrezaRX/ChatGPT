using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

if (args.Length < 2)
    throw new ArgumentException("Usage: RoslynSplitter <source.cs> <output-dir>");

var sourcePath = Path.GetFullPath(args[0]);
var outputDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outputDir);

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
foreach (var e in root.ChildNodes())
{
    if (e is ExternAliasDirectiveSyntax || e is UsingDirectiveSyntax)
        compileHeader.AppendLine(e.ToFullString());
}

foreach (var f in Directory.EnumerateFiles(outputDir, "*.cs", SearchOption.TopDirectoryOnly))
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

    var path = Path.Combine(outputDir, fileName);
    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    written.Add(Path.GetFileName(path));
}

void VisitMembers(IEnumerable<MemberDeclarationSyntax> members, string? namespaceName)
{
    foreach (var member in members)
    {
        switch (member)
        {
            case NamespaceDeclarationSyntax ns:
                var nsName = ns.Name.ToString();
                VisitMembers(ns.Members, nsName);
                break;

            case FileScopedNamespaceDeclarationSyntax fns:
                var fnsName = fns.Name.ToString();
                VisitMembers(fns.Members, fnsName);
                break;

            case BaseTypeDeclarationSyntax baseType:
                var identifier = GetTypeIdentifier(baseType);
                if (identifier == null)
                    continue;
                var fileName = identifier == mainTypeName
                    ? "MultiplayerCampaignSubModule.cs"
                    : "Mpc_" + Sanitize(identifier) + ".cs";
                WriteType(member.ToFullString(), namespaceName, fileName);
                break;

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

if (!written.Any(x => x.Equals("MultiplayerCampaignSubModule.cs", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Main MultiplayerCampaignSubModule type was not found.");

var report = new StringBuilder();
report.AppendLine("Roslyn lossless type split");
report.AppendLine("Types written: " + written.Count);
foreach (var w in written.OrderBy(x => x))
    report.AppendLine(w);

await File.WriteAllTextAsync(Path.Combine(outputDir, "REFACTOR_MAP.txt"), report.ToString(), new UTF8Encoding(false));
Console.WriteLine(report.ToString());
return 0;
