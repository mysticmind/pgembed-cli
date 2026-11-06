using System.Text.Json;
using System.Text.RegularExpressions;

namespace PgEmbed.Cli.Core;

/// <summary>What pgembed remembers about a named instance: the settings to start it again with the same port.</summary>
internal sealed record InstanceRecord(
    string Name,
    string Version,
    int Port,
    Dictionary<string, string> ServerParams,
    List<string> Extensions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastStartedAt);

/// <summary>
/// The instance registry: one <c>instance.json</c> per instance folder. Instances created by the library
/// alone (a test using <c>InstanceName</c>) have no entry; they are still listed, from their data folder.
/// </summary>
internal sealed class InstanceRegistry(Workspace workspace)
{
    private const string FileName = "instance.json";

    public InstanceRecord? Get(string name)
    {
        var file = Path.Combine(workspace.InstanceDir(name), FileName);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<InstanceRecord>(File.ReadAllText(file), Json.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public void Save(InstanceRecord record)
    {
        var dir = workspace.InstanceDir(record.Name);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, FileName);
        var temp = $"{file}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json.Options));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Every instance folder: those with a registry entry and those with only data.</summary>
    public IReadOnlyList<string> Names() =>
        Directory.Exists(workspace.InstancesDir)
            ? Directory.EnumerateDirectories(workspace.InstancesDir)
                .Where(d => File.Exists(Path.Combine(d, FileName)) || File.Exists(Path.Combine(d, "data", "PG_VERSION")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>The Postgres major an instance's data was created with, or null before its first start.</summary>
    public string? DataMajor(string name)
    {
        var file = Path.Combine(workspace.InstanceDir(name), "data", "PG_VERSION");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    /// <summary>Names matching a pattern with <c>*</c> and <c>?</c> wildcards, or the name itself.</summary>
    public IReadOnlyList<string> Match(string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?')) return [pattern];

        var regex = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");
        return Names().Where(n => regex.IsMatch(n)).ToList();
    }
}
