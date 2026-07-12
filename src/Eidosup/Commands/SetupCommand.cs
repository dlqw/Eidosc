using System.CommandLine;
using System.CommandLine.Invocation;
using Eidosup.Installation;

namespace Eidosup.Commands;

internal static class SetupCommand
{
    public static Command Create()
    {
        var command = new Command("setup", "Install or update eidosc, install clang/LLVM, and configure environment variables.");

        var versionOption = new Option<string?>("--version", "Install a specific Eidosc release. Accepts 0.4.0-alpha.1, v0.4.0-alpha.1, or eidosc-v0.4.0-alpha.1.");
        var repositoryOption = new Option<string>("--repo", () => "dlqw/Eidosc", "GitHub repository that hosts Eidos release assets.");
        var installRootOption = new Option<string?>("--install-root", "Override the install root directory.");
        var downloadRootOption = new Option<string?>("--download-root", "Override the download cache directory.");
        var skipEidoscOption = new Option<bool>("--skip-eidosc", "Skip installing or updating eidosc.");
        var skipClangOption = new Option<bool>("--skip-clang", "Skip installing or updating clang/LLVM.");
        var skipEnvOption = new Option<bool>("--skip-env", "Skip writing environment variables and PATH changes.");
        var includePreReleaseOption = new Option<bool>("--include-prerelease", () => true, "Allow prerelease versions when resolving the latest release.");
        var dryRunOption = new Option<bool>("--dry-run", "Print planned actions without mutating the machine.");
        var forceOption = new Option<bool>("--force", "Reinstall even if the requested version is already present.");

        command.AddOption(versionOption);
        command.AddOption(repositoryOption);
        command.AddOption(installRootOption);
        command.AddOption(downloadRootOption);
        command.AddOption(skipEidoscOption);
        command.AddOption(skipClangOption);
        command.AddOption(skipEnvOption);
        command.AddOption(includePreReleaseOption);
        command.AddOption(dryRunOption);
        command.AddOption(forceOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var parseResult = context.ParseResult;
            var options = new SetupOptions
            {
                Version = parseResult.GetValueForOption(versionOption),
                Repository = parseResult.GetValueForOption(repositoryOption) ?? "dlqw/Eidosc",
                InstallRoot = parseResult.GetValueForOption(installRootOption),
                DownloadRoot = parseResult.GetValueForOption(downloadRootOption),
                SkipEidosc = parseResult.GetValueForOption(skipEidoscOption),
                SkipClang = parseResult.GetValueForOption(skipClangOption),
                SkipEnvironmentConfiguration = parseResult.GetValueForOption(skipEnvOption),
                IncludePreRelease = parseResult.GetValueForOption(includePreReleaseOption),
                DryRun = parseResult.GetValueForOption(dryRunOption),
                Force = parseResult.GetValueForOption(forceOption)
            };

            var installer = new SetupOrchestrator();
            context.ExitCode = await installer.RunAsync(options, context.GetCancellationToken());
        });

        return command;
    }
}
