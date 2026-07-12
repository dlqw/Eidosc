using System.CommandLine;
using System.CommandLine.Invocation;
using Eidosup.Installation;

namespace Eidosup.Commands;

internal static class DoctorCommand
{
    public static Command Create()
    {
        var command = new Command("doctor", "Inspect the local Eidos environment and print readiness diagnostics.");
        var installRootOption = new Option<string?>("--install-root", "Override the install root to inspect.");
        command.AddOption(installRootOption);

        command.SetHandler(async (InvocationContext context) =>
        {
            var installRoot = context.ParseResult.GetValueForOption(installRootOption);
            var doctor = new DoctorReporter();
            var json = context.ParseResult.GetValueForOption(GlobalOptions.Json);
            context.ExitCode = await doctor.RunAsync(
                installRoot,
                json,
                cancellationToken: context.GetCancellationToken());
        });

        return command;
    }
}
