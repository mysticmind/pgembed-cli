using System.Text.Json;
using System.Text.Json.Serialization;
using MysticMind.PostgresEmbed;
using PgEmbed.Cli.Commands;

namespace PgEmbed.Cli.Core;

// What --json prints and what MCP tools return, as named types so serialization is generated at build time
// (Native AOT cannot serialize anonymous types by reflection). Property names are the JSON names, camelCased.

internal sealed record VersionMajor(string Major, string Newest, int Count);

internal sealed record InstalledVersion(string Version, string Platform, string InstallDir);

internal sealed record FormattedSql(string? Formatted);

internal sealed record SyntaxErrorOutput(string Error, int Position);

/// <summary>One file of <c>format</c>: the formatted text, or with --write/--check whether it was formatted, or its error.</summary>
internal sealed record FormatFileResult(string File, object? Formatted = null, string? Error = null, int? Position = null);

internal sealed record StartedInstance(string Name, string Version, int Port, bool Attached, string ConnectionString, string? Uri = null);

internal sealed record InstanceActionResult(string Name, string Result);

internal sealed record RestartedInstance(string Name, string Version, int Port);

internal sealed record InstanceStatusOutput(
    string Name, string? Version, string? DataMajor, string State, int? Port, int? ProcessId, DateTimeOffset? StartedAt, long DataBytes);

internal sealed record ConnectionStrings(string Name, int Port, bool Running, Dictionary<string, string> Formats);

internal sealed record DoctorCheckOutput(string Name, string Outcome, string Detail);

internal sealed record CacheListing(string CacheDir, List<CacheEntry> Entries);

internal sealed record CacheCleaned(bool DryRun, List<CacheEntry> Removed, long FreedBytes, int SkippedInUse);

// MCP tool results

internal sealed record McpVersions(string Platform, string? Major = null, List<string>? Versions = null, List<VersionMajor>? Majors = null, string? Warning = null);

internal sealed record McpExtensions(string Version, IReadOnlyList<PgAvailableExtension> Extensions);

internal sealed record InstanceSummary(string Name, string? Version, string State, int? Port, long? UptimeSeconds, long DataBytes);

internal sealed record McpInstances(List<InstanceSummary> Instances);

internal sealed record McpStopped(string Name, bool Stopped, string Message);

internal sealed record McpConnectionString(string Name, string Format, string ConnectionString);

internal sealed record McpLogs(string Name, List<string> Lines);

internal sealed record McpDoctor(bool Ok, List<DoctorCheckOutput> Checks);

internal sealed record McpRelations(IReadOnlyList<RelationInfo> Relations);

internal sealed record McpExplain(JsonElement Plan, bool Analyzed, bool RolledBack);

internal sealed record McpSqlError(string? SqlState, string Message, int Position, string? Detail, string? Hint, string Statement);

internal sealed record McpSqlResults(List<Dictionary<string, object?>> Results, McpSqlError? Error = null);

/// <summary>Serialization for every type pgembed writes or reads as JSON, generated at build time.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
// files pgembed keeps
[JsonSerializable(typeof(InstanceRecord))]
[JsonSerializable(typeof(VersionCatalog.Cached))]
// --json output
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<VersionMajor>))]
[JsonSerializable(typeof(InstalledVersion))]
[JsonSerializable(typeof(IReadOnlyList<PgAvailableExtension>))]
[JsonSerializable(typeof(FormattedSql))]
[JsonSerializable(typeof(SyntaxErrorOutput))]
[JsonSerializable(typeof(List<FormatFileResult>))]
[JsonSerializable(typeof(StartedInstance))]
[JsonSerializable(typeof(List<StartedInstance>))]
[JsonSerializable(typeof(List<InstanceActionResult>))]
[JsonSerializable(typeof(RestartedInstance))]
[JsonSerializable(typeof(List<InstanceStatusOutput>))]
[JsonSerializable(typeof(ConnectionStrings))]
[JsonSerializable(typeof(List<DoctorCheckOutput>))]
[JsonSerializable(typeof(CacheListing))]
[JsonSerializable(typeof(CacheCleaned))]
[JsonSerializable(typeof(IReadOnlyList<RelationInfo>))]
[JsonSerializable(typeof(TableSchema))]
[JsonSerializable(typeof(List<TableSchema>))]
[JsonSerializable(typeof(CheckResult))]
// MCP tool results
[JsonSerializable(typeof(McpVersions))]
[JsonSerializable(typeof(McpExtensions))]
[JsonSerializable(typeof(McpInstances))]
[JsonSerializable(typeof(McpStopped))]
[JsonSerializable(typeof(McpConnectionString))]
[JsonSerializable(typeof(McpLogs))]
[JsonSerializable(typeof(McpDoctor))]
[JsonSerializable(typeof(McpRelations))]
[JsonSerializable(typeof(McpExplain))]
[JsonSerializable(typeof(McpSqlResults))]
// values inside results: SQL values (typed as object), and MCP tool parameters
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(List<object?[]>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(long?))]
[JsonSerializable(typeof(short))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class PgEmbedJsonContext : JsonSerializerContext;
