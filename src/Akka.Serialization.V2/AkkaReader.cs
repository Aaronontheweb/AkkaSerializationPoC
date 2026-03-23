using System.Buffers;
using MessagePack;

namespace Akka.Serialization.V2;

/// <summary>
/// Concrete MessagePack-based reader for Akka.NET serialization.
/// Tracks consumed byte offset so multiple serializers can share the same reader for nested deserialization.
/// </summary>
/// <remarks>
/// DateTime is deserialized from [ticks (long), Kind (int)].
/// DateTimeOffset is deserialized from [ticks (long), offset minutes (int)].
/// Guid is deserialized from 16-byte binary.
/// Decimal is deserialized from [lo, mid, hi, flags].
/// </remarks>
public sealed class AkkaReader
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private int _consumed;

    public AkkaReader(ReadOnlyMemory<byte> buffer)
    {
        _buffer = buffer;
        _consumed = 0;
    }

    /// <summary>
    /// Gets the number of bytes consumed from the buffer so far.
    /// </summary>
    public int Consumed => _consumed;

    public int BeginReadObject()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        var fieldCount = reader.ReadArrayHeader();
        _consumed += (int)reader.Consumed;
        return fieldCount;
    }

    public int ReadInt32()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        var value = reader.ReadInt32();
        _consumed += (int)reader.Consumed;
        return value;
    }

    public long ReadInt64()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        var value = reader.ReadInt64();
        _consumed += (int)reader.Consumed;
        return value;
    }

    public string? ReadString()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        var value = reader.ReadString();
        _consumed += (int)reader.Consumed;
        return value;
    }

    public bool ReadBool()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        var value = reader.ReadBoolean();
        _consumed += (int)reader.Consumed;
        return value;
    }

    public double ReadDouble()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        var value = reader.ReadDouble();
        _consumed += (int)reader.Consumed;
        return value;
    }

    public DateTime ReadDateTime()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        var arrayLength = reader.ReadArrayHeader();
        if (arrayLength != 2)
            throw new MessagePackSerializationException($"Expected DateTime array with 2 elements, got {arrayLength}");
        var ticks = reader.ReadInt64();
        var kind = (DateTimeKind)reader.ReadInt32();
        _consumed += (int)reader.Consumed;
        return new DateTime(ticks, kind);
    }

    public DateTimeOffset ReadDateTimeOffset()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        var arrayLength = reader.ReadArrayHeader();
        if (arrayLength != 2)
            throw new MessagePackSerializationException($"Expected DateTimeOffset array with 2 elements, got {arrayLength}");
        var ticks = reader.ReadInt64();
        var offsetMinutes = reader.ReadInt32();
        _consumed += (int)reader.Consumed;
        return new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMinutes));
    }

    public Guid ReadGuid()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        var bytes = reader.ReadBytes();
        if (bytes == null || bytes.Value.Length != 16)
            throw new MessagePackSerializationException($"Expected 16 bytes for Guid, got {bytes?.Length ?? 0}");
        _consumed += (int)reader.Consumed;

        if (bytes.Value.IsSingleSegment)
        {
            return new Guid(bytes.Value.FirstSpan);
        }
        else
        {
            Span<byte> span = stackalloc byte[16];
            bytes.Value.CopyTo(span);
            return new Guid(span);
        }
    }

    public decimal ReadDecimal()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        var arrayLength = reader.ReadArrayHeader();
        if (arrayLength != 4)
            throw new MessagePackSerializationException($"Expected decimal array with 4 elements, got {arrayLength}");
        var lo = reader.ReadInt32();
        var mid = reader.ReadInt32();
        var hi = reader.ReadInt32();
        var flags = reader.ReadInt32();
        _consumed += (int)reader.Consumed;
        return new decimal(new[] { lo, mid, hi, flags });
    }

    public byte[]? ReadBytes()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        var bytes = reader.ReadBytes();
        _consumed += (int)reader.Consumed;

        if (bytes == null)
            return null;

        if (bytes.Value.IsSingleSegment)
        {
            return bytes.Value.FirstSpan.ToArray();
        }
        else
        {
            byte[] result = new byte[bytes.Value.Length];
            var position = 0;
            foreach (var segment in bytes.Value)
            {
                segment.Span.CopyTo(result.AsSpan(position));
                position += segment.Length;
            }
            return result;
        }
    }

    /// <summary>
    /// Attempts to read a null value. If the next value is null, consumes it and returns true.
    /// Otherwise, leaves the position unchanged and returns false.
    /// </summary>
    public bool TryReadNull()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        if (reader.TryReadNil())
        {
            _consumed += (int)reader.Consumed;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Skips the next field without reading its value.
    /// Used for forward compatibility when encountering unknown fields from newer versions.
    /// </summary>
    public void SkipField()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        reader.Skip();
        _consumed += (int)reader.Consumed;
    }
}
