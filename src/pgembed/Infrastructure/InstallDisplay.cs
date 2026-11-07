using MysticMind.PostgresEmbed;
using Spectre.Console;

namespace PgEmbed.Cli.Infrastructure;

/// <summary>
/// Work that may download or install Postgres. In a terminal it shows a spinner with the message, which becomes
/// a progress bar (bytes, speed) once a download starts, then the verify and extract steps. In JSON mode and
/// when output is redirected, nothing is shown. When the version is cached, only the spinner appears, briefly.
/// </summary>
internal static class InstallDisplay
{
    public static async Task RunAsync(CliContext cli, string message, Func<IProgress<PgInstallProgress>?, Task> work)
    {
        if (cli.Settings.Json || !cli.Console.Profile.Capabilities.Interactive)
        {
            await work(null);
            return;
        }

        await cli.Console.Progress()
            .AutoClear(true)
            .Columns(new SpinnerColumn(), new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn(),
                new WhileDownloading(new DownloadedColumn()), new WhileDownloading(new TransferSpeedColumn()))
            .StartAsync(async context =>
            {
                var task = context.AddTask(Markup.Escape(message), maxValue: 1);
                task.IsIndeterminate = true;
                await work(new TaskProgress(task));
                task.Value = task.MaxValue;
                task.StopTask();
            });
    }

    /// <summary>A column shown only while bytes are counted: blank while starting, verifying and extracting.</summary>
    private sealed class WhileDownloading(ProgressColumn inner) : ProgressColumn
    {
        public override Spectre.Console.Rendering.IRenderable Render(Spectre.Console.Rendering.RenderOptions options, ProgressTask task, TimeSpan deltaTime) =>
            task.IsIndeterminate ? Text.Empty : inner.Render(options, task, deltaTime);
    }

    /// <summary>Reports arrive on the download's thread; Spectre's tasks take updates from any thread.</summary>
    private sealed class TaskProgress(ProgressTask task) : IProgress<PgInstallProgress>
    {
        public void Report(PgInstallProgress value)
        {
            switch (value.Step)
            {
                case PgInstallStep.Download:
                    task.Description = Markup.Escape($"Downloading {value.Item}");
                    if (value.BytesTotal is > 0 and var total)
                    {
                        task.MaxValue = total;
                        task.IsIndeterminate = false;
                    }

                    task.Value = value.BytesDone ?? 0;
                    break;
                case PgInstallStep.Verify:
                    task.Description = Markup.Escape($"Verifying {value.Item}");
                    task.IsIndeterminate = true;
                    break;
                case PgInstallStep.Extract:
                    task.Description = Markup.Escape($"Extracting {value.Item}");
                    task.IsIndeterminate = true;
                    break;
            }
        }
    }
}
