using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Spectre.Console;

namespace PgEmbed.Cli.Infrastructure;

/// <summary>A positional argument: <c>&lt;NAME&gt;</c> is required, <c>[NAME]</c> optional; a string[] takes several.</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class CommandArgumentAttribute(int position, string template) : Attribute
{
    public int Position { get; } = position;
    public string Template { get; } = template;
}

/// <summary>An option: <c>"-f|--file &lt;PATH&gt;"</c>, or <c>"--force"</c> for a flag; a string[] option repeats.</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class CommandOptionAttribute(string template) : Attribute
{
    public string Template { get; } = template;
}

/// <summary>A command's settings: properties marked as arguments and options, and a check across them.</summary>
internal abstract class CommandSettings
{
    public virtual ValidationResult Validate() => ValidationResult.Success();
}

internal sealed class CommandContext(string name)
{
    public string Name { get; } = name;
}

internal abstract class AsyncCommand<TSettings> where TSettings : CommandSettings
{
    public abstract Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken ct);
}

/// <summary>
/// The command line, on System.CommandLine: parsing, help, validation and errors. Commands keep a settings class
/// with attributes; this builds typed options from them and binds the parsed values back. It works under Native
/// AOT: properties are read through annotated reflection, and options are created per property type, with no
/// code generated at run time.
/// </summary>
internal sealed class CliApp
{
    public const int UsageError = 2;
    public const int Cancelled = 130;

    private readonly CliServices _services;
    private readonly Func<Exception, int> _handleError;

    // the one-line summary a command shows in its parent's list; its own help adds the examples
    private readonly Dictionary<Command, string> _summaries = [];

    public CliApp(string description, string version, CliServices services, Func<Exception, int> handleError)
    {
        _services = services;
        _handleError = handleError;
        Root = new RootCommand(description);

        // the version without the commit hash the build appends
        foreach (var option in Root.Options)
        {
            if (option is VersionOption) option.Action = new PrintVersion(version);
        }

        // help with short summaries in command lists (see WriteHelp)
        foreach (var option in Root.Options)
        {
            if (option is HelpOption) option.Action = new PrintHelp(this);
        }

        // `pgembed` alone shows the help, rather than an error
        Root.SetAction(ShowHelp);
    }

    public RootCommand Root { get; }

    /// <summary>Help text width; the terminal's when not set.</summary>
    public int? HelpWidth { get; set; }

    /// <summary>A group of commands, such as <c>cache</c>.</summary>
    public Command Branch(string name, string description)
    {
        var branch = new Command(name, description);
        branch.SetAction(ShowHelp); // `pgembed cache` alone shows its commands
        Root.Subcommands.Add(branch);
        return branch;
    }

    public Command Add<TCommand, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TSettings>(
        string name, string description, Func<TCommand> create, string[][]? examples = null, Command? parent = null, string? alias = null)
        where TCommand : AsyncCommand<TSettings>
        where TSettings : CommandSettings, new()
    {
        var text = description;
        if (examples is { Length: > 0 })
        {
            text += "\n\nExamples:\n" + string.Join("\n", examples.Select(e => "  pgembed " + string.Join(' ', e.Select(Quote))));
        }

        var command = new Command(name, text);
        _summaries[command] = description;
        if (alias is not null) command.Aliases.Add(alias);

        var defaults = new TSettings();
        var binders = new List<Action<ParseResult, TSettings>>();
        var properties = typeof(TSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        foreach (var property in properties
                     .Select(p => (Property: p, Attribute: p.GetCustomAttribute<CommandArgumentAttribute>()))
                     .Where(p => p.Attribute is not null)
                     .OrderBy(p => p.Attribute!.Position))
        {
            binders.Add(AddArgument<TSettings>(command, property.Property, property.Attribute!));
        }

        // the command's own options first, then the ones every command takes
        foreach (var property in properties
                     .Where(p => p.GetCustomAttribute<CommandOptionAttribute>() is not null)
                     .OrderBy(p => p.DeclaringType == typeof(TSettings) ? 0 : 1))
        {
            binders.Add(AddOption(command, property, property.GetCustomAttribute<CommandOptionAttribute>()!, defaults));
        }

        command.SetAction(async (parse, ct) =>
        {
            var settings = new TSettings();
            foreach (var bind in binders) bind(parse, settings);

            var validation = settings.Validate();
            if (!validation.Successful) return Usage(validation.Message ?? "invalid arguments");

            ApplyCacheDir(settings);
            try
            {
                return await create().ExecuteAsync(new CommandContext(name), settings, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Cancelled;
            }
            catch (Exception ex)
            {
                return _handleError(ex);
            }
        });

        (parent ?? Root).Subcommands.Add(command);
        return command;
    }

    /// <summary>Parses and runs. Parse errors print one line each and exit with 2, as usage errors do.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var parse = Root.Parse(args);

        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors) _services.Console.MarkupLineInterpolated($"[red]error:[/] {error.Message}");
            var path = CommandPath(parse.CommandResult.Command);
            _services.Console.MarkupLineInterpolated($"[dim]See: {path} --help[/]");
            return UsageError;
        }

        var configuration = new InvocationConfiguration
        {
            Output = _services.Out,
            Error = _services.Error,
            ProcessTerminationTimeout = null, // pgembed handles Ctrl+C and SIGTERM itself
            EnableDefaultExceptionHandler = false
        };

        var exit = await parse.InvokeAsync(configuration, ct);
        await _services.Out.FlushAsync(CancellationToken.None);
        return exit;
    }

    private int Usage(string message)
    {
        _services.Console.MarkupLineInterpolated($"[red]error:[/] {message}");
        return UsageError;
    }

    // --cache-dir works through PGEMBED_CACHE_DIR, which the library and the workspace read
    private static void ApplyCacheDir(CommandSettings settings)
    {
        if (settings is GlobalSettings { CacheDir: { Length: > 0 } dir })
        {
            Environment.SetEnvironmentVariable(MysticMind.PostgresEmbed.PgCache.DirectoryVariable, Path.GetFullPath(dir));
        }
    }

    private static Action<ParseResult, TSettings> AddArgument<TSettings>(Command command, PropertyInfo property, CommandArgumentAttribute attribute)
    {
        var required = attribute.Template.StartsWith('<');
        var helpName = attribute.Template.Trim('<', '>', '[', ']');
        var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;

        if (property.PropertyType == typeof(string[]))
        {
            var argument = new Argument<string[]>(helpName.ToLowerInvariant())
            {
                Description = description, HelpName = helpName.ToLowerInvariant(),
                Arity = required ? ArgumentArity.OneOrMore : ArgumentArity.ZeroOrMore
            };
            command.Arguments.Add(argument);
            return (parse, settings) =>
            {
                if (parse.GetResult(argument) is { Tokens.Count: > 0 }) property.SetValue(settings, parse.GetValue(argument));
            };
        }
        else
        {
            var argument = new Argument<string>(helpName.ToLowerInvariant())
            {
                Description = description, HelpName = helpName.ToLowerInvariant(),
                Arity = required ? ArgumentArity.ExactlyOne : ArgumentArity.ZeroOrOne
            };
            command.Arguments.Add(argument);
            return (parse, settings) =>
            {
                if (parse.GetResult(argument) is { Tokens.Count: > 0 }) property.SetValue(settings, parse.GetValue(argument));
            };
        }
    }

    private static Action<ParseResult, TSettings> AddOption<TSettings>(Command command, PropertyInfo property, CommandOptionAttribute attribute, TSettings defaults)
    {
        var parts = attribute.Template.Split(' ', 2);
        var names = parts[0].Split('|');
        var name = names[^1];
        var aliases = names[..^1];
        var helpName = parts.Length > 1 ? parts[1].Trim('<', '>') : null;
        var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
        var initial = property.GetValue(defaults);
        var type = property.PropertyType;

        Option option;
        Func<ParseResult, object?> read;
        if (type == typeof(bool))
        {
            var o = new Option<bool>(name, aliases);
            (option, read) = (o, p => p.GetValue(o));
        }
        else if (type == typeof(int))
        {
            var o = new Option<int>(name, aliases);
            if (initial is int i && i != 0) o.DefaultValueFactory = _ => i;
            (option, read) = (o, p => p.GetValue(o));
        }
        else if (type == typeof(int?))
        {
            var o = new Option<int?>(name, aliases);
            (option, read) = (o, p => p.GetValue(o));
        }
        else if (type == typeof(string[]))
        {
            var o = new Option<string[]>(name, aliases) { AllowMultipleArgumentsPerToken = false };
            (option, read) = (o, p => p.GetValue(o));
        }
        else if (type == typeof(string))
        {
            var o = new Option<string>(name, aliases);
            if (initial is string { Length: > 0 } s) o.DefaultValueFactory = _ => s;
            (option, read) = (o, p => p.GetValue(o));
        }
        else
        {
            throw new InvalidOperationException($"{property.DeclaringType?.Name}.{property.Name}: options of type {type.Name} are not supported");
        }

        option.Description = description;
        if (helpName is not null) option.HelpName = helpName;
        command.Options.Add(option);

        // a value given on the command line replaces the property's initial value; otherwise it stays
        return (parse, settings) =>
        {
            if (parse.GetResult(option) is { Implicit: false }) property.SetValue(settings, read(parse));
        };
    }

    private int ShowHelp(ParseResult parse)
    {
        WriteHelp(parse.CommandResult.Command, parse.InvocationConfiguration.Output);
        return 0;
    }

    /// <summary>
    /// A command's help: description (with examples), usage, arguments, options and, for a group, its commands,
    /// each listed with its one-line summary.
    /// </summary>
    private void WriteHelp(Command command, TextWriter output)
    {
        var width = HelpWidth ?? TerminalWidth();
        var w = new StringWriter();

        if (!string.IsNullOrEmpty(command.Description))
        {
            w.WriteLine("Description:");
            foreach (var line in command.Description.Split('\n')) w.WriteLine(line.Length == 0 ? "" : "  " + line);
            w.WriteLine();
        }

        var path = CommandPath(command);
        var usage = path;
        foreach (var argument in command.Arguments)
        {
            var label = argument.Arity.MaximumNumberOfValues > 1 ? $"<{argument.HelpName}>..." : $"<{argument.HelpName}>";
            usage += argument.Arity.MinimumNumberOfValues > 0 ? $" {label}" : $" [{label}]";
        }

        if (command.Subcommands.Count > 0) usage += " [command]";
        usage += " [options]";
        w.WriteLine("Usage:");
        w.WriteLine("  " + usage);

        var arguments = command.Arguments.Select(a => (Left: $"<{a.HelpName}>", Right: a.Description ?? "")).ToList();
        WriteSection(w, "Arguments:", arguments, width);

        var options = command.Options.Concat(Root.Options.Where(o => o is HelpOption))
            .Distinct()
            .Where(o => !o.Hidden)
            .Select(o => (Left: OptionLabel(o), Right: OptionDescription(o)))
            .ToList();
        WriteSection(w, "Options:", options, width);

        var commands = command.Subcommands.Where(c => !c.Hidden)
            .Select(c => (Left: c.Name + string.Concat(c.Arguments.Select(a => a.Arity.MinimumNumberOfValues > 0 ? $" <{a.HelpName}>" : $" [<{a.HelpName}>]"))
                                + (c.Aliases.Count > 0 ? $" ({string.Join(", ", c.Aliases)})" : ""),
                          Right: _summaries.TryGetValue(c, out var summary) ? summary : c.Description ?? ""))
            .ToList();
        WriteSection(w, "Commands:", commands, width);

        output.Write(w.ToString());
        output.Flush();
    }

    private static string OptionLabel(Option option)
    {
        // short names first; the Windows-style help aliases (/h, -?, /?) still work but are not listed
        var names = option.Aliases.Where(a => a.StartsWith('-') && a != "-?").OrderBy(a => a.Length).Append(option.Name);
        var label = string.Join(", ", names);
        var takesValue = option.ValueType != typeof(bool) && option is not HelpOption and not VersionOption;
        return takesValue ? $"{label} <{option.HelpName ?? option.Name.TrimStart('-').ToUpperInvariant()}>" : label;
    }

    private static string OptionDescription(Option option)
    {
        // descriptions say their defaults in words, so none is appended
        return option is HelpOption ? "Show help and usage information" : option.Description ?? "";
    }

    // two columns: the left padded to the widest entry, the right wrapped at the width
    private static void WriteSection(TextWriter w, string title, List<(string Left, string Right)> rows, int width)
    {
        if (rows.Count == 0) return;
        w.WriteLine();
        w.WriteLine(title);
        var left = Math.Min(rows.Max(r => r.Left.Length), Math.Max(20, width / 2 - 4));
        var indent = 2 + left + 2;
        var rightWidth = Math.Max(20, width - indent);
        foreach (var (l, r) in rows)
        {
            var lines = Wrap(r, rightWidth);
            if (l.Length > left)
            {
                w.WriteLine("  " + l);
                foreach (var line in lines) w.WriteLine(new string(' ', indent) + line);
                continue;
            }

            w.WriteLine(("  " + l.PadRight(left) + "  " + lines[0]).TrimEnd());
            foreach (var line in lines.Skip(1)) w.WriteLine(new string(' ', indent) + line);
        }
    }

    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > width)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = current.Length == 0 ? word : current + " " + word;
            }
        }

        lines.Add(current);
        return lines;
    }

    private static int TerminalWidth()
    {
        try
        {
            return Console.IsOutputRedirected ? 100 : Math.Max(60, Console.WindowWidth);
        }
        catch (IOException)
        {
            return 100;
        }
    }

    private sealed class PrintHelp(CliApp app) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            app.WriteHelp(parseResult.CommandResult.Command, parseResult.InvocationConfiguration.Output);
            return 0;
        }
    }

    private static string CommandPath(Command command)
    {
        var names = new List<string>();
        for (Symbol? s = command; s is Command c; s = c.Parents.FirstOrDefault()) names.Insert(0, c is RootCommand ? "pgembed" : c.Name);
        return string.Join(' ', names);
    }

    private static string Quote(string arg) => arg.Contains(' ') || arg.Length == 0 ? $"\"{arg}\"" : arg;

    private sealed class PrintVersion(string version) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            parseResult.InvocationConfiguration.Output.WriteLine(version);
            return 0;
        }
    }
}
