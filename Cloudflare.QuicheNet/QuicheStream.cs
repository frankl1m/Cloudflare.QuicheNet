using System.Buffers;
using System.IO.Pipelines;

namespace Cloudflare.QuicheNet
{
    public class QuicheStream : Stream
    {
        public enum Shutdown
        {
            Read = 0,
            Write = 1,
        }

        public enum Direction
        {
            Bidirectional = 0x0,
            Unidirectional = 0x2,
        }

        private readonly Pipe? recvPipe, sendPipe;

        private readonly QuicheConnection conn;

        private readonly ulong streamId;

        private bool disposedValue;

        public override bool CanRead => recvPipe is not null;

        public override bool CanWrite => sendPipe is not null;

        public override bool CanSeek => false;

        public override long Position
        {
            get => throw new NotSupportedException("This stream cannot be seeked.");
            set => throw new NotSupportedException("This stream cannot be seeked.");
        }

        public override long Length => throw new NotSupportedException("This stream cannot have its length set or changed.");

        internal bool IsShuttingDown => disposedValue;

        // Set once all written data was handed to the connection after disposal,
        // so the FIN can't be sent before the last bytes.
        internal bool IsWriteCompleted { get; private set; }

        internal QuicheStream(QuicheConnection conn, ulong streamId)
        {
            this.conn = conn;
            this.streamId = streamId;

            bool isPeerInitiated = ((streamId & 1) == 0) ^ conn.IsServer;
            bool isBidirectional = (streamId & 2) == 0;

            if (!isPeerInitiated || isBidirectional)
            {
                recvPipe = new Pipe();
            }

            if (isPeerInitiated || isBidirectional)
            {
                sendPipe = new Pipe();
            }
        }

        internal async Task ReceiveDataAsync(ReadOnlyMemory<byte> bufIn, bool finished, CancellationToken cancellationToken)
        {
            if (recvPipe is null)
            {
                throw new NotSupportedException();
            }
            else
            {
                Memory<byte> memory = recvPipe.Writer.GetMemory(bufIn.Length);
                bufIn.CopyTo(memory); recvPipe.Writer.Advance(bufIn.Length);

                await recvPipe.Writer.FlushAsync(cancellationToken);

                if (finished)
                {
                    await recvPipe.Writer.CompleteAsync();
                }
            }
        }

        public override void Flush()
        {
            if (sendPipe is null)
            {
                return;
            }

            // Called both by the application and by the connection's send loop;
            // PipeReader doesn't support concurrent readers.
            lock (sendPipe)
            {
                while (sendPipe.Reader.TryRead(out ReadResult result))
                {
                    if (!result.Buffer.IsEmpty)
                    {
                        conn.sendQueue.AddOrUpdate(streamId,
                            key => result.Buffer.ToArray(),
                            (key, buf) => [.. buf, .. result.Buffer.ToArray()]
                            );
                    }
                    sendPipe.Reader.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted || result.Buffer.IsEmpty) break;
                }
            }
        }

        // Blocks until data is available; returns 0 only at the end of the stream.
        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (recvPipe is null)
            {
                throw new NotSupportedException("This stream is not readable.");
            }

            if (buffer.IsEmpty)
            {
                return 0;
            }

            ReadResult result = await recvPipe.Reader.ReadAsync(cancellationToken);

            int bytesRead = (int)Math.Min(result.Buffer.Length, buffer.Length);
            result.Buffer.Slice(0, bytesRead).CopyTo(buffer.Span);
            recvPipe.Reader.AdvanceTo(result.Buffer.GetPosition(bytesRead));

            return bytesRead;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (sendPipe is null)
            {
                throw new NotSupportedException("This stream is not writable.");
            }

            await sendPipe.Writer.WriteAsync(buffer, cancellationToken);
            conn.NotifyStreamWrite();
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("This stream cannot be seeked.");

        public override void SetLength(long value) =>
            throw new NotSupportedException("This stream cannot have its length set or changed.");

        protected override void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                disposedValue = true;

                if (disposing)
                {
                    if (recvPipe is not null)
                    {
                        recvPipe.Writer.Complete();
                    }

                    if (sendPipe is not null)
                    {
                        sendPipe.Writer.Complete();
                    }

                    Flush();
                    IsWriteCompleted = true;
                    conn.NotifyStreamWrite();
                }
            }

            base.Dispose(disposing);
        }
    }
}
