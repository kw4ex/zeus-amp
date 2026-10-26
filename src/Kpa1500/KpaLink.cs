// SPDX-License-Identifier: GPL-2.0-or-later
//
// One open connection to the amplifier, over any byte stream (TCP socket or
// USB serial). A background reader splits incoming bytes into "^...;" frames;
// QueryAsync sends one GET and waits for the frame that answers it, dropping
// anything stale. Only one command is ever in flight because the KPA1500 has
// no flow control and a small input buffer.

using System.Text;
using System.Threading.Channels;

namespace Zeus.Community.Kpa1500;

public sealed class KpaLink : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<string> _frames =
        Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private volatile bool _faulted;

    /// <param name="stream">Open duplex stream to the amp.</param>
    /// <param name="owner">Object that owns the stream (TcpClient, SerialPort); disposed with the link.</param>
    public KpaLink(Stream stream, IDisposable? owner)
    {
        _stream = stream;
        _owner = owner;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>False once the stream has closed or errored.</summary>
    public bool IsAlive => !_faulted && !_cts.IsCancellationRequested;

    /// <summary>Raised (on the reader thread) for every frame received, for the traffic log.</summary>
    public event Action<string>? FrameReceived;

    private async Task ReadLoopAsync()
    {
        var buf = new byte[512];
        var acc = new StringBuilder();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = await _stream.ReadAsync(buf, _cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                acc.Append(Encoding.ASCII.GetString(buf, 0, n));
                foreach (var f in KpaProtocol.ExtractFrames(acc))
                {
                    FrameReceived?.Invoke(f);
                    _frames.Writer.TryWrite(f);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* closed underneath us */ }
        finally
        {
            _faulted = true;
            _frames.Writer.TryComplete();
        }
    }

    /// <summary>Write raw text (SET commands have no reply).</summary>
    public async Task SendAsync(string text, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await WriteAsync(text, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Send GET "^LETTERS;" and return the answering frame's payload, or null on
    /// timeout. Never throws for a timeout; throws only if the link is dead.
    /// </summary>
    public async Task<string?> QueryAsync(string letters, TimeSpan timeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (_frames.Reader.TryRead(out _)) { } // discard stale / unsolicited
            await WriteAsync(KpaProtocol.Get(letters), ct).ConfigureAwait(false);

            using var tcs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tcs.CancelAfter(timeout);
            try
            {
                while (true)
                {
                    var frame = await _frames.Reader.ReadAsync(tcs.Token).ConfigureAwait(false);
                    if (KpaProtocol.Answers(frame, letters))
                        return KpaProtocol.Payload(frame, letters);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // timed out
            }
            catch (ChannelClosedException)
            {
                throw new IOException("Connection to the amplifier closed.");
            }
        }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        if (_faulted) throw new IOException("Connection to the amplifier closed.");
        var bytes = Encoding.ASCII.GetBytes(text);
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _owner?.Dispose(); } catch { /* best effort */ }
        try { _stream.Dispose(); } catch { /* best effort */ }
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _cts.Dispose();
        _gate.Dispose();
    }
}
