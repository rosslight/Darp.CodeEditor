using System.Text;

namespace Darp.CodeEditor.Lsp.Hosting;

/// <summary>
/// Splits a child's console output into lines of bounded length, so a child that writes without line breaks cannot
/// grow this process's memory. A longer line is cut, ends with "…", and the rest of it is dropped.
/// </summary>
internal sealed class OutputLines(Action<string> line)
{
    /// <summary>The most characters of a line that are kept; documented on <see cref="ChildProcess.StartAsync"/>.</summary>
    public const int MaxLength = 4096;

    private readonly Action<string> _line = line;
    private readonly StringBuilder _text = new();
    private bool _cut;

    public void Write(ReadOnlySpan<char> output)
    {
        while (!output.IsEmpty)
        {
            int newline = output.IndexOf('\n');
            Append(newline < 0 ? output : output[..newline]);
            if (newline < 0)
                return;
            EndLine();
            output = output[(newline + 1)..];
        }
    }

    /// <summary>Passes on the last line when the output ends without a line break.</summary>
    public void Complete()
    {
        if (_text.Length > 0)
            EndLine();
    }

    private void Append(ReadOnlySpan<char> part)
    {
        if (_cut || part.IsEmpty)
            return;
        int room = MaxLength - _text.Length;
        if (part.Length <= room)
        {
            _text.Append(part);
            return;
        }
        _text.Append(part[..room]).Append('…');
        _line(_text.ToString());
        _text.Clear();
        _cut = true;
    }

    private void EndLine()
    {
        if (!_cut)
        {
            if (_text.Length > 0 && _text[^1] == '\r')
                _text.Length--;
            _line(_text.ToString());
        }
        _text.Clear();
        _cut = false;
    }
}
