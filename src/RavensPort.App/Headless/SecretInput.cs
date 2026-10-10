using System.Text;

namespace RavensPort.Headless;

/// <summary>
/// Reads a secret from standard input: one line when it is piped or redirected, otherwise a prompt
/// on the terminal that does not echo what is typed.
///
/// Stdin and nothing else. Never a command-line argument, because a command line is readable by
/// every process on the machine and lands in shell history; never an environment variable read
/// behind the user's back, because one survives restarts in a place every process they run can
/// read. Piping keeps the secret out of both: <c>systemd-creds cat … | ravensport serve</c>,
/// or systemd's own <c>LoadCredentialEncrypted=</c> redirected onto stdin.
/// </summary>
internal static class SecretInput
{
    /// <summary>Overridable so tests can feed a value without a terminal.</summary>
    internal static Func<string, string?> Reader { get; set; } = ReadFromConsole;

    public static string? Read(string prompt) => Reader(prompt);

    private static string? ReadFromConsole(string prompt)
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine()?.Trim();

        Console.Error.Write(prompt);

        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter) break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }

        Console.Error.WriteLine();
        return value.ToString().Trim();
    }
}
