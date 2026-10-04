using System.Globalization;
using System.Text;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.CLI;

internal sealed record ProgressFrame(string Line, bool Completed);

internal sealed class ProgressDisplay(TimeProvider clock)
{
    private static readonly string[] Units = ["Bytes", "KB", "MB", "GB", "TB", "PB", "EB"];
    private ProgressRecord? _last;
    private long _started;
    private long _rendered;
    private bool _completed;

    public ProgressFrame? TryRender(ProgressRecord record, bool redirected, int terminalWidth)
    {
        if (_completed && record == _last && record.Phase != ProgressPhase.Started) return null;
        long now = clock.GetTimestamp();
        bool reset = _last is null || _completed || record.Phase == ProgressPhase.Started ||
            record.Unit != _last.Unit || record.Current < _last.Current ||
            record.Unit == ProgressUnit.Steps && record.Label != _last.Label;
        if (reset) _started = now;
        bool completed = record.Phase == ProgressPhase.Completed ||
            record.Unit == ProgressUnit.Steps && record.Total > 0 && record.Current >= record.Total;
        _last = record;
        if (!reset && !completed && clock.GetElapsedTime(_rendered, now).TotalMilliseconds < (redirected ? 1000 : 100))
            return null;
        _rendered = now;
        _completed = completed;

        long current = Math.Max(0, record.Current);
        bool knownTotal = record.Total > 0 || completed && record.Total == 0;
        if (knownTotal) current = Math.Min(current, record.Total);
        double ratio = completed ? 1 : record.Total > 0 ? Math.Clamp((double)current / record.Total, 0, 1) : 0;
        string percent = knownTotal ? (ratio * 100).ToString("F1", CultureInfo.InvariantCulture) + "%" : "--%";
        double seconds = Math.Max(0, clock.GetElapsedTime(_started, now).TotalSeconds);
        string elapsed = Duration(seconds);
        string quantity = record.Unit == ProgressUnit.Bytes
            ? completed ? Size(current) : $"{Size(current)} / {(knownTotal ? Size(record.Total) : "?")}"
            : $"{current} / {(knownTotal ? record.Total.ToString(CultureInfo.InvariantCulture) : "?")}";
        string time = completed ? Strings.FormatCli_ProgressDuration(elapsed) : Strings.FormatCli_ProgressElapsed(elapsed);
        string details = $"{percent} {quantity}";
        if (record.Unit == ProgressUnit.Bytes)
        {
            double speed = seconds > 0 ? current / seconds : 0;
            string rate = seconds > 0 ? Size((decimal)speed) + "/s" : "--";
            details += " | " + Strings.FormatCli_ProgressSpeed(rate);
            if (!completed)
            {
                string eta = knownTotal && speed > 0 ? Duration((record.Total - current) / speed) : "--:--:--";
                time += " | " + Strings.FormatCli_ProgressRemaining(eta);
            }
        }
        details += " | " + time;
        string label = record.Label.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        int width = Math.Min(24, terminalWidth - Cells(label) - Cells(details) - 5);
        string bar = width >= 8 ? " [" + new string('#', (int)(ratio * width)) + new string('-', width - (int)(ratio * width)) + "]" : "";
        return new ProgressFrame($"{label}{bar} {details}", completed);
    }

    internal static string Size(decimal bytes)
    {
        int unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString("0.##", CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    private static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds) return "--:--:--";
        long whole = (long)seconds;
        return $"{whole / 3600:00}:{whole / 60 % 60:00}:{whole % 60:00}";
    }

    internal static int Cells(string text)
    {
        int cells = 0;
        foreach (Rune rune in text.EnumerateRunes())
            cells += RuneCells(rune);
        return cells;
    }

    private static int RuneCells(Rune rune)
    {
        if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        int value = rune.Value;
        return value is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or
            >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE6F or >= 0xFF01 and <= 0xFF60 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFF ? 2 : 1;
    }

    internal static (string Text, int Rows) Wrap(string line, int terminalWidth)
    {
        // Reserve the final column to avoid terminal-dependent deferred wrapping.
        int available = Math.Max(2, terminalWidth - 1);
        if (Cells(line) <= available) return (line, 1);
        var output = new StringBuilder(line.Length + 16);
        int column = 0, rows = 1;
        Span<char> characters = stackalloc char[2];
        foreach (Rune rune in line.EnumerateRunes())
        {
            int cells = RuneCells(rune);
            if (column + cells > available) { output.Append("\r\n"); column = 0; rows++; }
            int count = rune.EncodeToUtf16(characters);
            output.Append(characters[..count]);
            column += cells;
        }
        return (output.ToString(), rows);
    }
}
