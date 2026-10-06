using GeekFlashCore.CLI.Localization;
using System.Runtime.InteropServices;

namespace GeekFlashCore.CLI;

internal sealed class InteractiveLineEditor(IReadOnlyList<string> history, Func<string, IReadOnlyList<string>> complete)
{
    private int _historyIndex = history.Count;
    private string _draft = "";
    private IReadOnlyList<string>? _matches;
    private string _suffix = "";
    private int _matchIndex = -1;
    internal string Text { get; private set; } = "";
    internal int Cursor { get; private set; }

    internal void Handle(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Tab)
        {
            if (_matches is null)
            {
                _matches = complete(Text[..Cursor]); _suffix = Text[Cursor..];
                _matchIndex = key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 0 : -1;
            }
            if (_matches.Count == 0) return;
            int direction = key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1;
            _matchIndex = (_matchIndex + direction + _matches.Count) % _matches.Count;
            string match = _matches[_matchIndex];
            if (match.Length + _suffix.Length > 65536) throw new InvalidOperationException(Strings.Cli_InputTooLong);
            Text = match + _suffix; Cursor = match.Length; return;
        }
        _matches = null;
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (_historyIndex == 0) return;
                if (_historyIndex == history.Count) _draft = Text;
                Set(history[--_historyIndex]); return;
            case ConsoleKey.DownArrow:
                if (_historyIndex >= history.Count) return;
                _historyIndex++; Set(_historyIndex == history.Count ? _draft : history[_historyIndex]); return;
            case ConsoleKey.LeftArrow: if (Cursor > 0) Cursor = Previous(Cursor); return;
            case ConsoleKey.RightArrow: if (Cursor < Text.Length) Cursor = Next(Cursor); return;
            case ConsoleKey.Home: Cursor = 0; return;
            case ConsoleKey.End: Cursor = Text.Length; return;
            case ConsoleKey.Backspace:
                if (Cursor > 0) { int start = Previous(Cursor); Text = Text.Remove(start, Cursor - start); Cursor = start; } return;
            case ConsoleKey.Delete:
                if (Cursor < Text.Length) Text = Text.Remove(Cursor, Next(Cursor) - Cursor); return;
            case ConsoleKey.Escape: Set(""); _historyIndex = history.Count; return;
        }
        if (char.IsControl(key.KeyChar) || key.Modifiers.HasFlag(ConsoleModifiers.Control) || key.Modifiers.HasFlag(ConsoleModifiers.Alt)) return;
        if (Text.Length >= 65536) throw new InvalidOperationException(Strings.Cli_InputTooLong);
        Text = Text.Insert(Cursor, key.KeyChar.ToString()); Cursor++;
    }

    private void Set(string text) { Text = text; Cursor = text.Length; }
    private int Previous(int index) => index > 1 && char.IsLowSurrogate(Text[index - 1]) && char.IsHighSurrogate(Text[index - 2]) ? index - 2 : index - 1;
    private int Next(int index) => index + 1 < Text.Length && char.IsHighSurrogate(Text[index]) && char.IsLowSurrogate(Text[index + 1]) ? index + 2 : index + 1;
}

internal sealed class ConsoleLineRenderer(string prompt)
{
    private readonly uint? _originalMode = EnableVirtualTerminal();
    private int _rows = 1;
    private int _cursorRow;

    internal void Render(InteractiveLineEditor editor)
    {
        Console.Write("\r");
        if (_cursorRow > 0) Console.Write($"\u001b[{_cursorRow}A");
        for (int row = 0; row < _rows; row++)
        {
            Console.Write("\u001b[2K");
            if (row + 1 < _rows) Console.Write("\u001b[1B\r");
        }
        if (_rows > 1) Console.Write($"\u001b[{_rows - 1}A");
        int width;
        try { width = Console.WindowWidth; } catch (IOException) { width = 80; }
        width = width > 1 ? width : 80;
        var full = ProgressDisplay.Wrap(prompt + editor.Text, width);
        var prefix = ProgressDisplay.Wrap(prompt + editor.Text[..editor.Cursor], width);
        Console.Write(full.Text);
        int column = ProgressDisplay.Cells(prefix.Text[(prefix.Text.LastIndexOf('\n') + 1)..]);
        int up = full.Rows - prefix.Rows;
        if (up > 0) Console.Write($"\u001b[{up}A");
        Console.Write("\r"); if (column > 0) Console.Write($"\u001b[{column}C");
        _rows = full.Rows; _cursorRow = prefix.Rows - 1;
    }

    internal void Finish()
    {
        if (_rows - 1 > _cursorRow) Console.Write($"\u001b[{_rows - 1 - _cursorRow}B");
        Console.WriteLine();
        if (_originalMode is { } mode) SetConsoleMode(GetStdHandle(-11), mode);
    }

    private static uint? EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return null;
        nint handle = GetStdHandle(-11);
        return GetConsoleMode(handle, out uint mode) && SetConsoleMode(handle, mode | 0x0004) ? mode : null;
    }

    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint handle, uint mode);
}
