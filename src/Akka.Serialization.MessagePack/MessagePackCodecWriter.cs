using System.Buffers;
using Akka.Serialization.V2;
using MessagePack;

namespace Akka.Serialization.MessagePack;

/// <summary>
/// MessagePack-based implementation of ICodecWriter.
/// Creates a new MessagePackWriter ref struct per operation, writing to a shared IBufferWriter&lt;byte&gt;.
/// </summary>
/// <remarks>
/// The MessagePackWriter is a lightweight ref struct - creating one per operation is cheap.
/// Each write operation creates a writer, writes one value, and flushes to the buffer.
/// The buffer tracks position across calls - each new MessagePackWriter picks up where the previous left off.
///
/// DateTime is serialized as an array: [ticks (long), Kind (int)]
/// DateTimeOffset is serialized as an array: [ticks (long), offset minutes (int)]
/// Guid is serialized as 16-byte binary using MessagePack extension type.
/// </remarks>
internal sealed class MessagePackCodecWriter : ICodecWriter
{
    private readonly IBufferWriter<byte> _buffer;
    private bool _disposed;

    public MessagePackCodecWriter(IBufferWriter<byte> buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    public void BeginObject(int fieldCount)
    {
        ThrowIfDisposed();
        var writer = new MessagePackWriter(_buffer);
        writer.WriteArrayHeader(fieldCount);
        writer.Flush();
    }

    public void WriteInt32(int value)
    {
        ThrowIfDisposed();
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteInt64(long value)
    {
        ThrowIfDisposed();
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteString(string? value)
    {
        ThrowIfDisposed();
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteBool(bool value)
    {
        ThrowIfDisposed();
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteDouble(double value)
    {
        ThrowIfDisposed();
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteDateTime(DateTime value)
    {
        ThrowIfDisposed();
        var writer = new MessagePackWriter(_buffer);
        // Serialize as [ticks (long), Kind (int)]
        writer.WriteArrayHeader(2);
        writer.Write(value.Ticks);
        writer.Write((int)value.Kind);
        writer.Flush();
    }

    public void WriteDateTimeOffset(DateTimeOffset value)
    {
        ThrowIfDisposed();
        var writer = new MessagePackWriter(_buffer);
        // Serialize as [ticks (long), offset minutes (int)]
        writer.WriteArrayHeader(2);
        writer.Write(value.Ticks);
        writer.Write((int)value.Offset.TotalMinutes);
        writer.Flush();
    }

    public void WriteGuid(Guid value)
    {
        ThrowIfDisposed();
        // Write Guid as 16-byte binary
        // Use ToByteArray() instead of stackalloc to avoid ref struct scope issues
        var guidBytes = value.ToByteArray();
        var writer = new MessagePackWriter(_buffer);
        writer.Write(guidBytes);
        writer.Flush();
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        ThrowIfDisposed();
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteNull()
    {
        ThrowIfDisposed();
        var writer = new MessagePackWriter(_buffer);
        writer.WriteNil();
        writer.Flush();
    }

    public void Flush()
    {
        ThrowIfDisposed();
        // MessagePackWriter.Flush() already writes to the IBufferWriter
        // Nothing additional needed here
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MessagePackCodecWriter));
    }
}
