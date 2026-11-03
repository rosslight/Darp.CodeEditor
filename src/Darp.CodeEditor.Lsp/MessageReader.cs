using System.Globalization;
using System.Text;

namespace Darp.CodeEditor.Lsp;

/// <summary>Reads message bodies framed by <c>Content-Length</c> headers from a stream.</summary>
internal sealed class MessageReader
{
    private readonly Stream _input;
    private readonly JsonRpcLimits _limits;
    private readonly Action<string> _log;
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;

    public MessageReader(Stream input, JsonRpcLimits limits, Action<string> log)
    {
        _input = input;
        _limits = limits;
        _log = log;
    }

    private static ReadOnlySpan<byte> HeaderEnd => "\r\n\r\n"u8;

    private static ReadOnlySpan<byte> ContentLength => "Content-Length:"u8;

    private ReadOnlySpan<byte> Unread => _buffer.AsSpan(_start, _end - _start);

    /// <summary>Returns the next message body, valid until the next call, or null when the input ended.</summary>
    /// <remarks>
    /// A read that the stream does not cancel, such as from a console stream, is abandoned when
    /// <paramref name="cancellationToken"/> fires; this reader must not be used afterwards.
    /// </remarks>
    /// <exception cref="InvalidDataException">A message is longer than <see cref="JsonRpcLimits.MaxMessageLength"/>.</exception>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int headerLength;
            while ((headerLength = Unread.IndexOf(HeaderEnd)) < 0)
            {
                if (_end - _start > _limits.MaxHeaderLength)
                    SkipGarbage();
                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                    return null;
            }
            if (ParseContentLength(Unread[..headerLength]) is not int length)
            {
                _start += Resynchronize(Unread[..headerLength]);
                continue;
            }
            if (length > _limits.MaxMessageLength)
            {
                throw new InvalidDataException(
                    $"A message of {length} bytes is longer than the limit of {_limits.MaxMessageLength} bytes."
                );
            }
            _start += headerLength + HeaderEnd.Length;
            while (_end - _start < length)
            {
                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                    return null;
            }
            var body = new ReadOnlyMemory<byte>(_buffer, _start, length);
            _start += length;
            return body;
        }
    }

    /// <returns>The value of the <c>Content-Length</c> header, or null when the header is malformed.</returns>
    private static int? ParseContentLength(ReadOnlySpan<byte> header)
    {
        int? length = null;
        foreach (Range lineRange in header.Split("\r\n"u8))
        {
            ReadOnlySpan<byte> line = header[lineRange];
            if (line.IsEmpty)
                continue;
            int colon = line.IndexOf((byte)':');
            if (colon < 0)
                return null;
            ReadOnlySpan<byte> name = line[..colon];
            if (!Ascii.EqualsIgnoreCase(name[Ascii.Trim(name)], "Content-Length"u8))
                continue;
            ReadOnlySpan<byte> value = line[(colon + 1)..];
            if (
                !int.TryParse(value[Ascii.Trim(value)], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            )
                return null;
            length = parsed;
        }
        return length;
    }

    /// <summary>Skips a header that has no valid length, since its body cannot be found.</summary>
    /// <returns>
    /// The offset of a later <c>Content-Length</c> in the header, which is where the next message starts when the
    /// peer sent a body without a header. Otherwise the offset after the header.
    /// </returns>
    private int Resynchronize(ReadOnlySpan<byte> header)
    {
        _log($"Skipped a malformed message header: {Encoding.ASCII.GetString(header[..Math.Min(header.Length, 200)])}");
        int next = header.Length > 0 ? header[1..].IndexOf(ContentLength) : -1;
        return next >= 0 ? next + 1 : header.Length + HeaderEnd.Length;
    }

    /// <summary>Drops input that is too long for a header, up to its last <c>Content-Length</c> if it has one.</summary>
    private void SkipGarbage()
    {
        int next = Unread.LastIndexOf(ContentLength);
        // Without a header start, keeps the bytes that may begin the end of a header.
        int skipped = next > 0 ? next : _end - _start - (HeaderEnd.Length - 1);
        _log($"Skipped {skipped} bytes of input without a message header.");
        _start += skipped;
    }

    /// <returns>False when the input ended.</returns>
    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_end == _buffer.Length)
        {
            int unread = _end - _start;
            byte[] target =
                unread > _buffer.Length / 2 ? new byte[(int)Math.Min(2L * _buffer.Length, Array.MaxLength)] : _buffer;
            _buffer.AsSpan(_start, unread).CopyTo(target);
            _buffer = target;
            _start = 0;
            _end = unread;
        }
        int read = await _input
            .ReadAsync(_buffer.AsMemory(_end), cancellationToken)
            .AsTask()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        _end += read;
        return read > 0;
    }
}
