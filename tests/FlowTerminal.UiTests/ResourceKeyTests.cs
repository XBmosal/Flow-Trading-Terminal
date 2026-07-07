using System.Text.RegularExpressions;
using Xunit;

namespace FlowTerminal.UiTests;

/// <summary>
/// Guards against the failure mode where code-behind calls <c>FindResource("Key")</c> with
/// a key that no XAML resource defines — which throws
/// <c>ResourceReferenceKeyNotFoundException</c> at runtime (it cannot be caught by the
/// compiler or by headless rendering tests). This test parses the app's XAML for every
/// <c>x:Key</c> and asserts every string-literal <c>FindResource</c> key in code exists.
/// </summary>
public class ResourceKeyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlowTerminal.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("could not locate repo root");
    }

    [Fact]
    public void Every_FindResource_Literal_In_App_Code_Is_Defined_In_Xaml()
    {
        var app = Path.Combine(RepoRoot(), "src", "FlowTerminal.App");

        // Collect every resource key declared in the app's XAML (themes, App.xaml, windows).
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var xaml in Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(xaml), @"x:Key=""(?<k>[^""]+)"""))
                defined.Add(m.Groups["k"].Value);

        Assert.NotEmpty(defined); // sanity: we actually found the XAML

        // Every string-literal FindResource("…") key in code-behind must be defined.
        var missing = new List<string>();
        foreach (var cs in Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            if (cs.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(cs), @"FindResource\(""(?<k>[^""]+)""\)"))
                if (!defined.Contains(m.Groups["k"].Value))
                    missing.Add($"{Path.GetFileName(cs)} → '{m.Groups["k"].Value}'");
        }

        Assert.True(missing.Count == 0,
            "FindResource references to undefined resources: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_StaticResource_Reference_In_Xaml_Is_Defined()
    {
        // A typo'd {StaticResource X} in XAML throws at window load — the same crash
        // class as the FindResource bug, but in markup. This guard parses every XAML
        // reference with a simple named key and asserts a matching x:Key exists
        // somewhere in the app's XAML (themes are merged app-wide).
        var app = Path.Combine(RepoRoot(), "src", "FlowTerminal.App");

        var defined = new HashSet<string>(StringComparer.Ordinal);
        var references = new List<(string File, string Key)>();
        foreach (var xaml in Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            if (xaml.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string text = File.ReadAllText(xaml);
            foreach (Match m in Regex.Matches(text, @"x:Key=""(?<k>[^""]+)"""))
                defined.Add(m.Groups["k"].Value);
            foreach (Match m in Regex.Matches(text, @"\{(?:StaticResource|DynamicResource)\s+(?<k>\w+)\s*\}"))
                references.Add((Path.GetFileName(xaml), m.Groups["k"].Value));
        }

        Assert.NotEmpty(defined);
        Assert.NotEmpty(references);

        var missing = references.Where(r => !defined.Contains(r.Key))
            .Select(r => $"{r.File} → '{r.Key}'").Distinct().ToList();
        Assert.True(missing.Count == 0,
            "XAML StaticResource/DynamicResource references to undefined keys: " + string.Join(", ", missing));
    }
}
