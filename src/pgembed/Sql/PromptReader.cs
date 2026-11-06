using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Consoles;
using PrettyPrompt.Documents;
using PrettyPrompt.Highlighting;

namespace PgEmbed.Cli.Sql;

/// <summary>
/// The line editor (PrettyPrompt): highlighting, completion, multi-line input and history. The tool
/// builds against PrettyPrompt 4 on .NET 8 and 9 and PrettyPrompt 6 on .NET 10 and later; both have the
/// same API for what is used here.
/// </summary>
internal sealed class PromptReader(string historyFile) : ILineReader
{
    private readonly Dictionary<string, Prompt> _prompts = [];
    private Func<SqlCatalog> _catalog = () => SqlCatalog.Empty;

    public async Task<LineResult?> ReadAsync(string prompt, Func<SqlCatalog> catalog)
    {
        _catalog = catalog;
        if (!_prompts.TryGetValue(prompt, out var editor))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyFile)!);
            editor = new Prompt(
                persistentHistoryFilepath: historyFile,
                callbacks: new Callbacks(() => _catalog()),
                configuration: new PromptConfiguration(
                    prompt: new FormattedString(prompt, new FormatSpan(0, prompt.Length, AnsiColor.BrightBlack))));
            _prompts[prompt] = editor;
        }

        var result = await editor.ReadLineAsync();
        return new LineResult(result.IsSuccess, result.Text);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var prompt in _prompts.Values) await prompt.DisposeAsync();
    }

    private sealed class Callbacks(Func<SqlCatalog> catalog) : PromptCallbacks
    {
        private static readonly ConsoleFormat Keyword = new(Foreground: AnsiColor.BrightBlue, Bold: true);
        private static readonly ConsoleFormat Literal = new(Foreground: AnsiColor.Green);
        private static readonly ConsoleFormat Number = new(Foreground: AnsiColor.Cyan);
        private static readonly ConsoleFormat Parameter = new(Foreground: AnsiColor.Magenta);
        private static readonly ConsoleFormat Operator = new(Foreground: AnsiColor.Yellow);
        private static readonly ConsoleFormat Comment = new(Foreground: AnsiColor.BrightBlack);
        private static readonly ConsoleFormat Meta = new(Foreground: AnsiColor.Magenta, Bold: true);
        private static readonly ConsoleFormat Error = new(Foreground: AnsiColor.BrightRed, Underline: true);

        protected override Task<IReadOnlyCollection<FormatSpan>> HighlightCallbackAsync(string text, CancellationToken cancellationToken)
        {
            var styled = SqlEditor.Highlight(text);

            // the error underline replaces the color of any token it overlaps
            if (styled.FirstOrDefault(s => s.Style == SqlStyle.Error) is { Length: > 0 } error)
            {
                styled = styled.Where(s => s == error || s.Start + s.Length <= error.Start || s.Start >= error.Start + error.Length).ToList();
            }

            var spans = styled.Select(s => new FormatSpan(s.Start, s.Length, s.Style switch
            {
                SqlStyle.Keyword => Keyword,
                SqlStyle.String => Literal,
                SqlStyle.Number => Number,
                SqlStyle.Parameter => Parameter,
                SqlStyle.Operator => Operator,
                SqlStyle.Comment => Comment,
                SqlStyle.Meta => Meta,
                SqlStyle.Error => Error,
                _ => ConsoleFormat.None
            })).ToList();

            return Task.FromResult<IReadOnlyCollection<FormatSpan>>(spans);
        }

        protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(
            string text, int caret, TextSpan spanToBeReplaced, CancellationToken cancellationToken)
        {
            if (text.TrimStart().StartsWith('\\')) return Task.FromResult<IReadOnlyList<CompletionItem>>([]);
            var items = SqlEditor.Complete(catalog(), text, spanToBeReplaced.Start, spanToBeReplaced.Length)
                .Take(200)
                .Select(c => new CompletionItem(c))
                .ToList();
            return Task.FromResult<IReadOnlyList<CompletionItem>>(items);
        }

        // Enter runs complete SQL; in unfinished SQL it starts a new line (the editor's Shift+Enter)
        protected override Task<KeyPress> TransformKeyPressAsync(string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
        {
            var key = keyPress.ConsoleKeyInfo;
            if (key.Key == ConsoleKey.Enter && key.Modifiers == 0 && !SqlEditor.IsComplete(text))
            {
                return Task.FromResult(new KeyPress(ConsoleKey.Enter.ToKeyInfo('\0', shift: true, alt: false, control: false)));
            }

            return Task.FromResult(keyPress);
        }
    }
}

/// <summary>
/// For terminals the line editor cannot draw in (no size, such as an editor's shell buffer): plain line
/// input, still multi-line until the statement is finished, with no highlighting or completion.
/// </summary>
internal sealed class PlainReader : ILineReader
{
    public async Task<LineResult?> ReadAsync(string prompt, Func<SqlCatalog> catalog)
    {
        var text = new System.Text.StringBuilder();
        Console.Write(prompt);
        while (true)
        {
            var line = await Task.Run(Console.ReadLine);
            if (line is null) return text.Length == 0 ? null : new LineResult(true, text.ToString());
            text.Append(text.Length == 0 ? "" : "\n").Append(line);
            if (SqlEditor.IsComplete(text.ToString())) return new LineResult(true, text.ToString());
            Console.Write(new string(' ', Math.Max(0, prompt.Length - 3)) + "-> ");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The full line editor where the terminal has a size it can draw in; plain input otherwise.</summary>
    public static ILineReader For(string historyFile)
    {
        int width;
        try
        {
            width = Console.WindowWidth;
        }
        catch (IOException)
        {
            width = 0;
        }

        var dumb = string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal);
        return width >= 20 && !dumb ? new PromptReader(historyFile) : new PlainReader();
    }
}
