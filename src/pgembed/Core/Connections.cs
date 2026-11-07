using System.Text;
using MysticMind.PostgresEmbed;
using Npgsql;

namespace PgEmbed.Cli.Core;

/// <summary>
/// Connection strings in the formats other tools use, and the reverse: reading any of them back into an
/// Npgsql connection string. Every format carries the same five things: host, port, user, database and,
/// optionally, a schema (the search path).
/// </summary>
internal static partial class Connections
{
    public static readonly string[] FormatNames = ["npgsql", "uri", "libpq", "jdbc", "env"];

    public static string Format(string format, int port, string database, string? schema, string host = "localhost",
        string user = InstanceManager.DefaultUser, string? password = null)
    {
        var nl = Environment.NewLine;
        return format switch
        {
            "npgsql" => $"Host={host};Port={port};Username={user};Database={database}" + (schema is null ? "" : $";Search Path={schema}")
                        + (password is null ? "" : $";Password={password}"),
            "uri" => $"postgresql://{Uri.EscapeDataString(user)}{(password is null ? "" : ":" + Uri.EscapeDataString(password))}@{host}:{port}/{Uri.EscapeDataString(database)}"
                     + (schema is null ? "" : "?options=" + Uri.EscapeDataString($"-c search_path={schema}")),
            "libpq" => $"host={host} port={port} user={LibpqQuote(user)} dbname={LibpqQuote(database)}"
                       + (schema is null ? "" : $" options={LibpqQuote($"-c search_path={schema}")}")
                       + (password is null ? "" : $" password={LibpqQuote(password)}"),
            "jdbc" => $"jdbc:postgresql://{host}:{port}/{Uri.EscapeDataString(database)}?user={Uri.EscapeDataString(user)}"
                      + (schema is null ? "" : $"&currentSchema={Uri.EscapeDataString(schema)}")
                      + (password is null ? "" : $"&password={Uri.EscapeDataString(password)}"),
            "env" => $"PGHOST={host}{nl}PGPORT={port}{nl}PGUSER={user}{nl}PGDATABASE={database}"
                     + (schema is null ? "" : $"{nl}PGOPTIONS='-c search_path={schema}'")
                     + (password is null ? "" : $"{nl}PGPASSWORD={password}"),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    private static string LibpqQuote(string value) =>
        value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c is not '\'' and not '\\')
            ? value
            : "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    /// <summary>Is this a connection string or URI rather than an instance name?</summary>
    public static bool IsConnectionString(string target) =>
        target.Contains('=') || target.Contains("://", StringComparison.Ordinal) || target.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads an Npgsql connection string, a <c>postgresql://</c> URI, a libpq <c>key=value</c> string or a
    /// JDBC URL. No password is needed in it: Npgsql also reads PGPASSWORD and the .pgpass file.
    /// </summary>
    public static NpgsqlConnectionStringBuilder Parse(string value)
    {
        var text = value.Trim();
        try
        {
            var builder =
                text.StartsWith("jdbc:postgresql:", StringComparison.OrdinalIgnoreCase) ? FromJdbc(text["jdbc:".Length..])
                : text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ? FromUri(text, jdbc: false)
                : !text.Contains(';') && TryLibpq(text, out var libpq) ? libpq
                : new NpgsqlConnectionStringBuilder(text);

            // as libpq: no host means PGHOST, else this machine
            if (string.IsNullOrEmpty(builder.Host)) builder.Host = Environment.GetEnvironmentVariable("PGHOST") ?? "localhost";
            return builder;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            throw new PostgresEmbedException($"Could not read the connection string: {Mask(ex.Message)}");
        }
    }

    // libpq (and URI query) keywords, mapped to Npgsql's
    private static readonly Dictionary<string, string> LibpqKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["host"] = "Host", ["hostaddr"] = "Host", ["port"] = "Port", ["user"] = "Username", ["password"] = "Password",
        ["dbname"] = "Database", ["sslmode"] = "SSL Mode", ["sslrootcert"] = "Root Certificate", ["sslcert"] = "SSL Certificate",
        ["sslkey"] = "SSL Key", ["application_name"] = "Application Name", ["connect_timeout"] = "Timeout",
        ["options"] = "Options", ["passfile"] = "Passfile", ["target_session_attrs"] = "Target Session Attributes",
        ["client_encoding"] = "Client Encoding"
    };

    private static NpgsqlConnectionStringBuilder FromUri(string text, bool jdbc)
    {
        // scheme://[user[:password]@][host[:port][,host[:port]...]][/dbname][?key=value&...]
        var rest = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var query = "";
        var q = rest.IndexOf('?');
        if (q >= 0) (rest, query) = (rest[..q], rest[(q + 1)..]);

        var builder = new NpgsqlConnectionStringBuilder();
        var at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            var credentials = rest[..at];
            rest = rest[(at + 1)..];
            var colon = credentials.IndexOf(':');
            builder.Username = Uri.UnescapeDataString(colon < 0 ? credentials : credentials[..colon]);
            if (colon >= 0) builder.Password = Uri.UnescapeDataString(credentials[(colon + 1)..]);
        }

        var slash = rest.IndexOf('/');
        var hosts = slash < 0 ? rest : rest[..slash];
        if (slash >= 0 && rest[(slash + 1)..] is { Length: > 0 } database) builder.Database = Uri.UnescapeDataString(database);

        if (hosts.Length > 0)
        {
            // a single host:port becomes Host and Port; several stay together, as Npgsql reads them
            var list = hosts.Split(',');
            if (list.Length == 1 && SplitHostPort(list[0]) is var (host, port))
            {
                builder.Host = host;
                if (port is not null) builder.Port = port.Value;
            }
            else builder.Host = Uri.UnescapeDataString(hosts);
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = Uri.UnescapeDataString(eq < 0 ? "" : pair[(eq + 1)..].Replace('+', ' '));
            if (jdbc && Jdbc(builder, key, value)) continue;
            builder[MapLibpqKey(key)] = value;
        }

        return builder;
    }

    private static (string Host, int? Port) SplitHostPort(string value)
    {
        // [::1]:5432 for IPv6
        if (value.StartsWith('[') && value.IndexOf(']') is var close and > 0)
        {
            var port = value.Length > close + 2 && value[close + 1] == ':' ? int.Parse(value[(close + 2)..]) : (int?)null;
            return (value[1..close], port);
        }

        var colon = value.LastIndexOf(':');
        return colon < 0 ? (Uri.UnescapeDataString(value), null) : (Uri.UnescapeDataString(value[..colon]), int.Parse(value[(colon + 1)..]));
    }

    private static NpgsqlConnectionStringBuilder FromJdbc(string text) => FromUri(text, jdbc: true);

    // JDBC's own parameter names
    private static bool Jdbc(NpgsqlConnectionStringBuilder builder, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "currentschema": builder.SearchPath = value; return true;
            case "applicationname": builder.ApplicationName = value; return true;
            case "ssl":
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase) && builder.SslMode == SslMode.Prefer) builder.SslMode = SslMode.Require;
                return true;
            case "connecttimeout": builder.Timeout = int.Parse(value); return true;
            default: return false;
        }
    }

    private static string MapLibpqKey(string key) =>
        LibpqKeys.TryGetValue(key, out var mapped) ? mapped : throw new ArgumentException($"unknown parameter '{key}'");

    // host=localhost port=5432 dbname='my db' : whitespace-separated pairs, values optionally single-quoted
    private static bool TryLibpq(string text, out NpgsqlConnectionStringBuilder builder)
    {
        builder = new NpgsqlConnectionStringBuilder();
        var i = 0;
        var pairs = new List<(string Key, string Value)>();
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;

            var keyStart = i;
            while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i])) i++;
            var key = text[keyStart..i];
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '=') return false;
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

            var value = new StringBuilder();
            if (i < text.Length && text[i] == '\'')
            {
                i++;
                while (i < text.Length && text[i] != '\'')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    value.Append(text[i++]);
                }

                if (i >= text.Length) return false; // unterminated quote
                i++;
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i])) value.Append(text[i++]);
            }

            pairs.Add((key, value.ToString()));
        }

        // only libpq when every key is one; "Host=x" alone is left to Npgsql, which reads it the same way
        if (pairs.Count == 0) return false;
        if (pairs.FirstOrDefault(p => !LibpqKeys.ContainsKey(p.Key)) is { Key: { } unknown })
        {
            // several space-separated pairs can only be libpq, so name the key that is wrong
            if (pairs.Count > 1)
            {
                throw new ArgumentException($"unknown key '{unknown}'; libpq keys are {string.Join(", ", LibpqKeys.Keys.Order())}");
            }

            return false;
        }
        foreach (var (key, value) in pairs)
        {
            if (key.Equals("port", StringComparison.OrdinalIgnoreCase)) builder.Port = int.Parse(value);
            else builder[LibpqKeys[key]] = value;
        }

        return true;
    }

    /// <summary>The connection string with its secrets left out, for display.</summary>
    public static string Redacted(NpgsqlConnectionStringBuilder builder)
    {
        var copy = new NpgsqlConnectionStringBuilder(builder.ConnectionString);
        if (!string.IsNullOrEmpty(copy.Password)) copy.Password = "***";
        if (!string.IsNullOrEmpty(copy.SslPassword)) copy.SslPassword = "***";
        return copy.ConnectionString;
    }

    /// <summary>
    /// Any text that may hold a connection string (an argument repeated in an error, say) with passwords masked:
    /// <c>user:secret@</c> in URIs, schemeless ones too, and <c>password=</c>, <c>pwd=</c> and SSL passwords in
    /// key=value forms and URI queries.
    /// </summary>
    public static string Mask(string text)
    {
        var masked = UserInfoPassword().Replace(text, "$1***@");
        return KeyPassword().Replace(masked, "$1***");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"((?:^|://|\s)[^:/@\s;]*:)[^@/\s]+@")]
    private static partial System.Text.RegularExpressions.Regex UserInfoPassword();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?i)(\b(?:pgpassword|password|pwd|ssl ?password|sslpassword)\s*=\s*)('(?:[^'\\]|\\.)*'|[^;&\s]*)")]
    private static partial System.Text.RegularExpressions.Regex KeyPassword();

    /// <summary>A PostgreSQL position (1-based, in Unicode code points) as a 0-based UTF-16 index.</summary>
    public static int CodePointToIndex(string text, int position)
    {
        var index = 0;
        for (var points = 1; points < position && index < text.Length; points++)
        {
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
        }

        return index;
    }

    public static int CountCodePoints(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++, count++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
        }

        return count;
    }
}
