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

        var (widths, numeric) = Measure(columns, rows, text);
        WriteHeader(output, columns, widths);
        foreach (var row in rows) WriteRow(output, row, widths, numeric, text);
    }

    /// <summary>Column widths and which columns are numeric (right-aligned), from the rows given.</summary>
    public static (int[] Widths, bool[] Numeric) Measure(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, Func<object?, string> text)
    {
        var widths = new int[columns.Count];
        var numeric = new bool[columns.Count];
        for (var c = 0; c < columns.Count; c++)
        {
            widths[c] = columns[c].Length;
            numeric[c] = rows.Count > 0;
        }

        foreach (var row in rows)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                var value = row[c];
                if (value is not (null or int or long or short or decimal or double or float)) numeric[c] = false;
                foreach (var line in Lines(text(value))) widths[c] = Math.Max(widths[c], line.Length);
            }
        }

        return (widths, numeric);
    }

    // header: names centered, as psql does
    public static void WriteHeader(TextWriter output, IReadOnlyList<string> columns, int[] widths)
    {
        output.WriteLine((" " + string.Join(" | ", columns.Select((name, c) => Center(name, widths[c])))).TrimEnd());
        output.WriteLine(string.Join("+", widths.Select(w => new string('-', w + 2))));
    }

    /// <summary>One row; a value wider than its column (after the measured window) simply runs longer.</summary>
    public static void WriteRow(TextWriter output, object?[] row, int[] widths, bool[] numeric, Func<object?, string> text)
    {
        var cells = new string[row.Length][];
        var height = 1;
        for (var c = 0; c < row.Length; c++)
        {
            cells[c] = Lines(text(row[c]));
            height = Math.Max(height, cells[c].Length);
        }

        for (var line = 0; line < height; line++)
        {
            var parts = new string[row.Length];
            for (var c = 0; c < row.Length; c++)
            {
                var value = line < cells[c].Length ? cells[c][line] : "";
                parts[c] = numeric[c] ? value.PadLeft(widths[c]) : value.PadRight(widths[c]);
            }

            output.WriteLine((" " + string.Join(" | ", parts)).TrimEnd());
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

    private static string[] Lines(string value) =>
        value.AsSpan().IndexOfAny('\r', '\n') < 0 ? [value] : value.ReplaceLineEndings("\n").Split('\n');

    private static string Center(string value, int width)
    {
        var left = (width - value.Length) / 2;
        return (new string(' ', left) + value).PadRight(width);
    }
}
