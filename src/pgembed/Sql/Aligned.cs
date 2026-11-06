namespace PgEmbed.Cli.Sql;

/// <summary>
/// psql-style plain text: never wrapped, one row per line (a value with line breaks takes several),
/// numbers right-aligned, NULL as an empty cell. Safe for files, pipes, grep and copy-paste.
/// </summary>
internal static class Aligned
{
    public static void Write(TextWriter output, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, bool expanded, Func<object?, string> text)
    {
        if (expanded)
        {
            WriteExpanded(output, columns, rows, text);
            return;
        }

        var cells = rows.Select(r => r.Select(v => Lines(text(v))).ToArray()).ToList();
        var numeric = Enumerable.Range(0, columns.Count)
            .Select(c => rows.Count > 0 && rows.All(r => r[c] is null or int or long or short or decimal or double or float)).ToArray();
        var widths = Enumerable.Range(0, columns.Count)
            .Select(c => Math.Max(columns[c].Length, cells.Count == 0 ? 0 : cells.Max(r => r[c].Max(l => l.Length))))
            .ToArray();

        // header: names centered, as psql does
        output.WriteLine((" " + string.Join(" | ", columns.Select((name, c) => Center(name, widths[c])))).TrimEnd());
        output.WriteLine(string.Join("+", widths.Select(w => new string('-', w + 2))));

        foreach (var row in cells)
        {
            var height = row.Max(lines => lines.Length);
            for (var line = 0; line < height; line++)
            {
                var parts = row.Select((lines, c) =>
                {
                    var value = line < lines.Length ? lines[line] : "";
                    return numeric[c] ? value.PadLeft(widths[c]) : value.PadRight(widths[c]);
                });
                output.WriteLine((" " + string.Join(" | ", parts)).TrimEnd());
            }
        }
    }

    private static void WriteExpanded(TextWriter output, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, Func<object?, string> text)
    {
        var nameWidth = columns.Count == 0 ? 0 : columns.Max(c => c.Length);
        for (var r = 0; r < rows.Count; r++)
        {
            output.WriteLine($"-[ RECORD {r + 1} ]" + new string('-', Math.Max(1, nameWidth - 8)));
            for (var c = 0; c < columns.Count; c++)
            {
                var lines = Lines(text(rows[r][c]));
                output.WriteLine($"{columns[c].PadRight(nameWidth)} | {lines[0]}".TrimEnd());
                foreach (var more in lines.Skip(1)) output.WriteLine($"{new string(' ', nameWidth)} | {more}".TrimEnd());
            }
        }
    }

    private static string[] Lines(string value) => value.ReplaceLineEndings("\n").Split('\n');

    private static string Center(string value, int width)
    {
        var left = (width - value.Length) / 2;
        return (new string(' ', left) + value).PadRight(width);
    }
}
