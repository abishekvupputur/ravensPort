using System.CommandLine;
using System.Reflection;

namespace RavensPort.Headless;

/// <summary>
/// RavensPort's command line: what the same executable does when it is given a command instead of
/// being launched to open its window. Built by a function so the tests can parse arguments against
/// exactly what ships, with their own output writers.
/// </summary>
internal static class Cli
{
    /// <summary>
    /// Whether these arguments are for the command line rather than the window. Any argument at
    /// all: the desktop app takes none, so there is nothing to mistake one for, and an unknown
    /// command is better answered with the command line's own error than by opening a window.
    /// </summary>
    public static bool Wants(string[] args) => args.Length > 0;

    public static int Run(string[] args)
    {
        WindowsConsole.Attach();

        // The messages carry em dashes and the like; a Windows console defaults to a legacy code
        // page that renders them as boxes. UTF-8 is what Windows Terminal, a pipe and Linux all expect.
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Synchronously, on Main's thread: there is no UI and no synchronization context here,
        // so nothing can try to post back onto a blocked thread.
        return Build(Console.Out, Console.Error).Parse(args).InvokeAsync().GetAwaiter().GetResult();
    }

    public static RootCommand Build(TextWriter output, TextWriter error)
    {
        var json = new Option<bool>("--json") { Description = "Print the server's JSON reply instead of a table.", Recursive = true };
        var socket = new Option<string?>("--socket")
        {
            Description = "Admin socket of the RavensPort to manage. Defaults to this user's.",
            Recursive = true,
        };

        var root = new RootCommand(
            "RavensPort. With no command it opens the desktop app. `serve` runs the proxy headless, with no "
            + "window; every other command manages the RavensPort that is running — the desktop app or "
            + "`serve` — through its admin socket, using the vault it has already unlocked.")
        {
            json,
            socket,
        };

        var context = new CommandContext(json, socket, output, error);

        root.Subcommands.Add(BuildServe(output, error));
        root.Subcommands.Add(ManagementCommands.Status(context));
        root.Subcommands.Add(ManagementCommands.Reload(context));
        root.Subcommands.Add(ManagementCommands.Credentials(context));
        root.Subcommands.Add(ManagementCommands.Upstreams(context));
        root.Subcommands.Add(ManagementCommands.Routes(context));
        root.Subcommands.Add(ManagementCommands.Sources(context));
        root.Subcommands.Add(ManagementCommands.Funnels(context));
        root.Subcommands.Add(ManagementCommands.Bridges(context));
        root.Subcommands.Add(ManagementCommands.Settings(context));

        return root;
    }

    private static Command BuildServe(TextWriter output, TextWriter error)
    {
        var backend = new Option<Backend>("--backend")
        {
            Description = "Password manager to read the configuration from. protonpass is always read-only.",
            DefaultValueFactory = _ => Backend.OnePassword,
            CustomParser = result => result.Tokens.Single().Value.ToLowerInvariant().Replace("-", "") switch
            {
                "onepassword" or "1password" or "op" => Backend.OnePassword,
                "protonpass" or "proton" or "pass" => Backend.ProtonPass,
                var other => Fail<Backend>(result, $"Unknown backend '{other}'. Use onepassword or protonpass."),
            },
        };
        var vault = new Option<string?>("--vault") { Description = "1Password: the vault to use when none holds a RavensPort configuration yet." };
        var createVault = new Option<string?>("--create-vault") { Description = "1Password: create a new vault with this name and use it." };
        var readOnly = new Option<bool>("--read-only") { Description = "Never write to the vault; changes are discarded on exit. Always on for protonpass." };

        var serve = new Command("serve",
            "Run the proxy headless. The secret is read from stdin — a 1Password service account token, "
            + "or a Proton Pass personal access token — and never from an argument or the environment.")
        {
            backend, vault, createVault, readOnly,
        };

        serve.SetAction((parse, ct) => ServeCommand.RunAsync(
            new ServeOptions(parse.GetValue(backend), parse.GetValue(vault), parse.GetValue(createVault), parse.GetValue(readOnly)),
            output, error, ct));

        return serve;
    }

    private static T Fail<T>(System.CommandLine.Parsing.ArgumentResult result, string message)
    {
        result.AddError(message);
        return default!;
    }

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}

/// <summary>What every management command needs: the shared options and where to write.</summary>
internal sealed record CommandContext(Option<bool> Json, Option<string?> Socket, TextWriter Output, TextWriter Error)
{
    public AdminClient Client(ParseResult parse) => new(parse.GetValue(Socket), parse.GetValue(Json), Output, Error);
}
