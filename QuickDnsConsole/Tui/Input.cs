using System.Text;
using Spectre.Console;

namespace QuickDNSConsole.Tui;

/// <summary>
/// Single-line console input with Enter/Esc semantics, inline default ("keep current")
/// and validation — the pieces Spectre's TextPrompt does not offer (Esc-to-cancel).
/// </summary>
internal static class Input
{
    /// <returns>The accepted value, or null when the user cancelled with Esc.</returns>
    public static string? Ask(string label, string? defaultValue = null, Func<string?, string?>? validator = null)
    {
        while (true)
        {
            var hint = !string.IsNullOrEmpty(defaultValue)
                ? $"[grey] · current:[/] [grey]{Markup.Escape(defaultValue)}[/] [grey]· Enter keeps · Esc cancels[/]"
                : "[grey] · Esc cancels[/]";

            AnsiConsole.Markup($" [bold]{Markup.Escape(label)}:[/] {hint}\n > ");

            var buffer = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape)
                {
                    AnsiConsole.WriteLine();
                    return null;
                }

                if (key.Key == ConsoleKey.Enter)
                    break;

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }

            AnsiConsole.WriteLine();

            var typed = buffer.ToString();
            var effective = string.IsNullOrWhiteSpace(typed) ? (defaultValue ?? typed) : typed;

            var error = validator?.Invoke(effective);
            if (error is not null)
            {
                AnsiConsole.MarkupLine($" [red]error:[/] {Markup.Escape(error)}");
                continue;
            }

            return effective;
        }
    }
}
