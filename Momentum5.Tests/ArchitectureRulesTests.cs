using System.Xml.Linq;

namespace Momentum5.Tests;

public sealed class ArchitectureRulesTests
{
	private static readonly Dictionary<string, string[]> Allowed = new()
	{
		["Momentum5.Core"] = [],
		["Momentum5.Protocol"] = ["Momentum5.Core"],
		["Momentum5.Client"] = ["Momentum5.Core", "Momentum5.Protocol"],
		["Momentum5.Simulation"] = ["Momentum5.Core", "Momentum5.Protocol"],
		["Momentum5.Transport.Rfcomm"] = ["Momentum5.Core"],
		["Momentum5.Transport.Ble"] = ["Momentum5.Core"],
		["Momentum5.Tests"] = ["Momentum5.Core", "Momentum5.Protocol", "Momentum5.Client", "Momentum5.Simulation"],
	};

	// Apps may reference every library project.
	private static readonly string[] Apps = ["Momentum5.App", "Momentum5.Cli"];

	private static string RepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Momentum5.slnx")))
		{
			dir = dir.Parent;
		}
		return dir?.FullName ?? throw new InvalidOperationException("Momentum5.slnx not found above " + AppContext.BaseDirectory);
	}

	public static IEnumerable<(string Project, string[] Refs)> ReadProjects(string root)
	{
		foreach (var file in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
		{
			if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
				file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
				file.Contains($"{Path.DirectorySeparatorChar}research{Path.DirectorySeparatorChar}"))
			{
				continue;
			}

			var refs = XDocument.Load(file)
				.Descendants("ProjectReference")
				.Select(e => Path.GetFileNameWithoutExtension(((string?)e.Attribute("Include") ?? "").Replace('\\', '/').Split('/').Last()))
				.ToArray();
			yield return (Path.GetFileNameWithoutExtension(file), refs);
		}
	}

	public static IEnumerable<string> Violations(IEnumerable<(string Project, string[] Refs)> projects)
	{
		foreach (var (project, refs) in projects)
		{
			if (Apps.Contains(project))
			{
				continue;
			}
			if (!Allowed.TryGetValue(project, out var allowed))
			{
				yield return $"{project}: not in the architecture matrix (add it to ref/03 and this test)";
				continue;
			}
			foreach (var r in refs.Where(r => !allowed.Contains(r)))
			{
				yield return $"{project} -> {r} is not allowed";
			}
		}
	}

	[Fact]
	public void ProjectReferencesFollowMatrix()
	{
		var violations = Violations(ReadProjects(RepoRoot())).ToList();
		Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
	}

	[Fact]
	public void MatrixDetectsForbiddenReference()
	{
		var bad = new[] { ("Momentum5.Protocol", new[] { "Momentum5.Core", "Momentum5.Client" }) };
		Assert.Single(Violations(bad));
	}
}
