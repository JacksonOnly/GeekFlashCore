using System.Text;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal sealed class ConsoleInputReader(TextReader? input = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<string?>? _pendingLine;
    private readonly List<string> _history = [];

    internal bool CanPrompt => input is not null || !Console.IsInputRedirected;

    internal async Task<string?> ReadCommandAsync(string prompt, Func<string, IReadOnlyList<string>> complete, CancellationToken ct)
    {
        if (input is not null || Console.IsInputRedirected || Console.IsOutputRedirected)
        { Console.Write(prompt); return await ReadAsync(false, ct).ConfigureAwait(false); }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var renderer = new ConsoleLineRenderer(prompt);
        try
        {
            var editor = new InteractiveLineEditor(_history, complete);
            renderer.Render(editor);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!Console.KeyAvailable) { await Task.Delay(25, ct).ConfigureAwait(false); continue; }
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    string line = editor.Text;
                    if (!string.IsNullOrWhiteSpace(line) && (_history.Count == 0 || _history[^1] != line))
                    { if (_history.Count == 200) _history.RemoveAt(0); _history.Add(line); }
                    return line;
                }
                editor.Handle(key); renderer.Render(editor);
            }
        }
        finally { try { renderer.Finish(); } finally { _gate.Release(); } }
    }

    internal async Task<string?> ReadAsync(bool secret, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input is not null || Console.IsInputRedirected)
            {
                // Console's TextReader may block before returning a Task. Retain one read
                // across cancelled waits so a retry cannot start a competing stdin reader.
                _pendingLine ??= Task.Run(() => (input ?? Console.In).ReadLineAsync());
                string? line = await _pendingLine.WaitAsync(cancellationToken).ConfigureAwait(false);
                _pendingLine = null;
                if (line?.Length > 65536) throw new InvalidOperationException(Strings.Cli_InputTooLong);
                return line;
            }

            var buffer = new StringBuilder();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return buffer.ToString(); }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0) { buffer.Length--; if (!secret) Console.Write("\b \b"); }
                    continue;
                }
                if (char.IsControl(key.KeyChar)) continue;
                if (buffer.Length >= 65536) throw new InvalidOperationException(Strings.Cli_InputTooLong);
                buffer.Append(key.KeyChar);
                if (!secret) Console.Write(key.KeyChar);
            }
        }
        finally { _gate.Release(); }
    }
}
