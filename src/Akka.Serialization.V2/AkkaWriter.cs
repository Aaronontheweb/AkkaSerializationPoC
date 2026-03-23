using System.Buffers;
using MessagePack;

namespace Akka.Serialization.V2;

/// <summary>
/// Concrete MessagePack-based writer for Akka.NET serialization.
/// Wraps an <see cref="IBufferWriter{T}"/> and provides typed write methods.
/// </summary>
/// <remarks>
/// Multiple serializers can share the same AkkaWriter instance for zero-copy nested serialization.
/// Each BeginObject(N) writes a MessagePack array header; nested serializer calls create nested arrays.
///
/// DateTime is serialized as [ticks (long), Kind (int)].
/// DateTimeOffset is serialized as [ticks (long), offset minutes (int)].
/// Guid is serialized as 16-byte binary.
/// Decimal is serialized as [lo, mid, hi, flags] for lossless round-trip.
/// </remarks>
public sealed class AkkaWriter
{
    private readonly IBufferWriter<byte> _buffer;

    public AkkaWriter(IBufferWriter<byte> buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    /// <summary>
    /// Exposes the underlying buffer for advanced scenarios (e.g. custom MessagePack extensions).
    /// </summary>
    public IBufferWriter<byte> RawBuffer => _buffer;

    public void BeginObject(int fieldCount)
    {
        var writer = new MessagePackWriter(_buffer);
        writer.WriteArrayHeader(fieldCount);
        writer.Flush();
    }

    public void WriteInt32(int value)
    {
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteInt64(long value)
    {
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteString(string? value)
    {
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteBool(bool value)
    {
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteDouble(double value)
    {
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteDateTime(DateTime value)
    {
        var writer = new MessagePackWriter(_buffer);
        writer.WriteArrayHeader(2);
        writer.Write(value.Ticks);
        writer.Write((int)value.Kind);
        writer.Flush();
    }

    public void WriteDateTimeOffset(DateTimeOffset value)
    {
        var writer = new MessagePackWriter(_buffer);
        writer.WriteArrayHeader(2);
        writer.Write(value.Ticks);
        writer.Write((int)value.Offset.TotalMinutes);
        writer.Flush();
    }

    public void WriteGuid(Guid value)
    {
        // Write Guid as 16-byte binary directly into the buffer (zero-copy)
        var writer = new MessagePackWriter(_buffer);
        writer.WriteBinHeader(16);
        value.TryWriteBytes(writer.GetSpan(16));
        writer.Advance(16);
        writer.Flush();
    }

    public void WriteDecimal(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        var writer = new MessagePackWriter(_buffer);
        writer.WriteArrayHeader(4);
        decimal.GetBits(value, bits);
        writer.Write(bits[0]);
        writer.Write(bits[1]);
        writer.Write(bits[2]);
        writer.Write(bits[3]);
        writer.Flush();
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        var writer = new MessagePackWriter(_buffer);
        writer.Write(value);
        writer.Flush();
    }

    public void WriteNull()
    {
        var writer = new MessagePackWriter(_buffer);
        writer.WriteNil();
        writer.Flush();
    }
}
