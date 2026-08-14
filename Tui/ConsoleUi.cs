using System.Text;

namespace ZombiesVsPlants2.SaveEditor.Tui;

internal static class ConsoleUi
{
    public static void Clear()
    {
        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            Console.WriteLine();
        }
    }

    public static void WriteTitle(string title)
    {
        int availableWidth = Math.Max(1, GetWidth() - 1);
        WriteColored(Truncate(title, availableWidth), ConsoleColor.Cyan);
        Console.WriteLine();
        WriteColored(new string('─', Math.Min(availableWidth, Math.Max(1, title.Length + 8))), ConsoleColor.DarkGray);
        Console.WriteLine();
    }

    public static int Select(
        string title,
        IReadOnlyList<string> items,
        IReadOnlyList<string>? headerLines = null,
        string? footer = null,
        int initialSelection = 0)
    {
        if (items.Count == 0)
        {
            return -1;
        }

        int selected = Math.Clamp(initialSelection, 0, items.Count - 1);
        int scroll = 0;

        while (true)
        {
            int height = GetHeight();
            int headerCount = headerLines?.Count ?? 0;
            int visibleCount = Math.Max(1, height - headerCount - 8);
            if (selected < scroll)
            {
                scroll = selected;
            }
            else if (selected >= scroll + visibleCount)
            {
                scroll = selected - visibleCount + 1;
            }

            scroll = Math.Clamp(scroll, 0, Math.Max(0, items.Count - visibleCount));
            Clear();
            WriteTitle(title);
            if (headerLines is not null)
            {
                foreach (string line in headerLines)
                {
                    WriteTruncated(line, ConsoleColor.DarkGray);
                    Console.WriteLine();
                }

                Console.WriteLine();
            }

            int end = Math.Min(items.Count, scroll + visibleCount);
            for (int index = scroll; index < end; index++)
            {
                bool active = index == selected;
                WriteColored(active ? "  > " : "    ", active ? ConsoleColor.Yellow : ConsoleColor.DarkGray);
                WriteTruncated(items[index], active ? ConsoleColor.White : ConsoleColor.Gray, reservedColumns: 5);
                Console.WriteLine();
            }

            if (items.Count > visibleCount)
            {
                Console.WriteLine();
                WriteColored($"  {selected + 1}/{items.Count}  (PgUp/PgDn to page)", ConsoleColor.DarkGray);
                Console.WriteLine();
            }

            Console.WriteLine();
            WriteColored(footer ?? "Up/Down: select | Enter: confirm | Esc: back", ConsoleColor.DarkGray);

            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    selected = (selected - 1 + items.Count) % items.Count;
                    break;
                case ConsoleKey.DownArrow:
                    selected = (selected + 1) % items.Count;
                    break;
                case ConsoleKey.PageUp:
                    selected = Math.Max(0, selected - visibleCount);
                    break;
                case ConsoleKey.PageDown:
                    selected = Math.Min(items.Count - 1, selected + visibleCount);
                    break;
                case ConsoleKey.Home:
                    selected = 0;
                    break;
                case ConsoleKey.End:
                    selected = items.Count - 1;
                    break;
                case ConsoleKey.Enter:
                    return selected;
                case ConsoleKey.Escape:
                    return -1;
            }
        }
    }

    public static string? PromptOptional(string prompt, string? current = null)
    {
        Console.WriteLine();
        string suffix = current is null ? "" : $" (current: {current})";
        WriteColored($"{prompt}{suffix}", ConsoleColor.Cyan);
        Console.WriteLine();
        WriteColored("Press Enter without typing to cancel: ", ConsoleColor.DarkGray);
        string? value = Console.ReadLine();
        string? trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    public static string? PromptStringEdit(string prompt, string current)
    {
        Console.WriteLine();
        WriteColored($"{prompt} (current: {current})", ConsoleColor.Cyan);
        Console.WriteLine();
        WriteColored("Press Enter without typing to cancel; enter \"\" to use an empty string: ", ConsoleColor.DarkGray);
        string? value = Console.ReadLine();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value == "\"\"" ? string.Empty : value;
    }

    public static string? PromptWithDefault(string prompt, string defaultValue)
    {
        Console.WriteLine();
        WriteColored(prompt, ConsoleColor.Cyan);
        Console.WriteLine();
        WriteColored($"Default: {defaultValue}", ConsoleColor.DarkGray);
        Console.WriteLine();
        WriteColored("Press Enter to use the default; type /cancel to cancel: ", ConsoleColor.DarkGray);
        string? value = Console.ReadLine();
        if (value is null)
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed.Length == 0 ? defaultValue : trimmed;
    }

    public static bool Confirm(string question, bool defaultAnswer = false)
    {
        WriteColored($"{question} {(defaultAnswer ? "[Y/n]" : "[y/N]")} ", ConsoleColor.Yellow);
        string? answer = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(answer))
        {
            return defaultAnswer;
        }

        return answer.Equals("y", StringComparison.OrdinalIgnoreCase)
            || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static void Success(string message)
    {
        Console.WriteLine();
        WriteColored("Success: ", ConsoleColor.Green);
        Console.WriteLine(message);
        Pause();
    }

    public static void Error(string message)
    {
        Console.WriteLine();
        WriteColored("Error: ", ConsoleColor.Red);
        Console.WriteLine(message);
        Pause();
    }

    public static void Notice(string message)
    {
        Console.WriteLine();
        WriteColored("Notice: ", ConsoleColor.Yellow);
        Console.WriteLine(message);
        Pause();
    }

    public static void Pause()
    {
        Console.WriteLine();
        WriteColored("Press any key to continue...", ConsoleColor.DarkGray);
        _ = Console.ReadKey(intercept: true);
    }

    public static string FormatNumber(object? value) => value?.ToString() ?? "—";

    public static string Truncate(string value, int maximumLength)
    {
        if (maximumLength <= 0)
        {
            return string.Empty;
        }

        List<Rune> runes = value.EnumerateRunes().ToList();
        if (runes.Count <= maximumLength)
        {
            return value;
        }

        if (maximumLength == 1)
        {
            return "…";
        }

        return string.Concat(runes.Take(maximumLength - 1).Select(rune => rune.ToString())) + "…";
    }

    private static void WriteTruncated(string value, ConsoleColor color, int reservedColumns = 0) =>
        WriteColored(Truncate(value, Math.Max(1, GetWidth() - reservedColumns - 1)), color);

    private static void WriteColored(string value, ConsoleColor color)
    {
        ConsoleColor original = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.Write(value);
        }
        finally
        {
            Console.ForegroundColor = original;
        }
    }

    private static int GetWidth()
    {
        try
        {
            return Math.Max(1, Console.WindowWidth);
        }
        catch (IOException)
        {
            return 100;
        }
    }

    private static int GetHeight()
    {
        try
        {
            return Math.Max(1, Console.WindowHeight);
        }
        catch (IOException)
        {
            return 30;
        }
    }
}
