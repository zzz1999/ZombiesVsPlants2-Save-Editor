using System.Reflection;
using System.Text;
using ZombiesVsPlants2.SaveEditor.Diagnostics;
using ZombiesVsPlants2.SaveEditor.Tui;

namespace ZombiesVsPlants2.SaveEditor;

internal static class Program
{
    private static readonly Assembly ApplicationAssembly = typeof(Program).Assembly;
    private static readonly string ApplicationName =
        ApplicationAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
        ?? ApplicationAssembly.GetName().Name
        ?? "Save Editor";
    private static readonly string ApplicationVersion = ResolveApplicationVersion();

    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            Console.Title = "Zombies vs Plants 2 Save Editor";
        }
        catch (IOException)
        {
            // Some non-interactive terminals do not support setting the title.
        }

        try
        {
            if (args.Length > 0)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        PrintHelp();
                        return 0;
                    case "--version":
                        Console.WriteLine($"{ApplicationName} {ApplicationVersion}");
                        return 0;
                    case "--fixture-test" when args.Length == 1:
                        return DiagnosticsRunner.FixtureTest();
                    case "--inspect" when args.Length == 2:
                        return DiagnosticsRunner.Inspect(args[1]);
                    case "--self-test" when args.Length == 2:
                        return DiagnosticsRunner.SelfTest(args[1]);
                    case "--roundtrip" when args.Length == 3:
                        return DiagnosticsRunner.RoundTrip(args[1], args[2]);
                }
            }

            string? initialPath = args.Length == 0 ? null : args[0];
            return RunTui(initialPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or OverflowException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static int RunTui(string? initialPath)
    {
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            // Keep Ctrl+C from bypassing the TUI's unsaved-change confirmation.
            eventArgs.Cancel = true;
        };
        Console.CancelKeyPress += handler;
        try
        {
            return TuiApp.Run(initialPath);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"{ApplicationName} {ApplicationVersion}");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ZombiesVsPlants2.SaveEditor.exe [pp.dat]");
        Console.WriteLine("  ZombiesVsPlants2.SaveEditor.exe --inspect <pp.dat>");
        Console.WriteLine("  ZombiesVsPlants2.SaveEditor.exe --self-test <pp.dat>");
        Console.WriteLine("  ZombiesVsPlants2.SaveEditor.exe --roundtrip <input> <output>");
        Console.WriteLine();
        Console.WriteLine("Interactive mode supports profile selection, resource editing, bulk and individual plant editing, field search, undo, automatic backups, and atomic saves.");
    }

    private static string ResolveApplicationVersion()
    {
        string? informationalVersion = ApplicationAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        Version? version = ApplicationAssembly.GetName().Version;
        return version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}
