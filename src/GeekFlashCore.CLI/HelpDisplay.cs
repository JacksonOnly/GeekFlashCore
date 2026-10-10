using System.Text;

namespace GeekFlashCore.CLI;

internal static class HelpDisplay
{
    internal static string Format(string text, int terminalWidth)
    {
        int width = Math.Clamp(terminalWidth - 1, 8, 119);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int column = lines.Where(line => line.Contains('\t'))
            .Select(line => ProgressDisplay.Cells(line.Split('\t')[0])).DefaultIfEmpty(0).Max();
        column = Math.Min(column, Math.Max(0, width / 2 - 4));
        var output = new StringBuilder(text.Length + 64);
        foreach (string source in lines)
        {
            string line = source;
            string continuation = line.StartsWith("  ", StringComparison.Ordinal) ? "    " : "  ";
            int tab = line.IndexOf('\t');
            if (tab >= 0)
            {
                string name = line[..tab], description = line[(tab + 1)..];
                if (ProgressDisplay.Cells(name) <= column)
                {
                    line = ProgressDisplay.Pad(name, column) + "  " + description;
                    continuation = new string(' ', column + 2);
                }
                else
                {
                    AppendWrapped(output, name, width, "    ");
                    line = "    " + description;
                    continuation = "    ";
                }
            }
            AppendWrapped(output, line, width, continuation);
        }
        return output.ToString().TrimEnd('\r', '\n');
    }

    private static void AppendWrapped(StringBuilder output, string line, int width, string continuation)
    {
        while (ProgressDisplay.Cells(line) > width)
        {
            int count = 0, cells = 0;
            foreach (Rune rune in line.EnumerateRunes())
            {
                int next = ProgressDisplay.Cells(rune.ToString());
                if (cells + next > width) break;
                count += rune.Utf16SequenceLength; cells += next;
            }
            int space = line.LastIndexOf(' ', count - 1, count);
            if (space > line.Length - line.TrimStart().Length) count = space;
            output.AppendLine(line[..count].TrimEnd());
            line = continuation + line[count..].TrimStart();
        }
        output.AppendLine(line);
    }
}
