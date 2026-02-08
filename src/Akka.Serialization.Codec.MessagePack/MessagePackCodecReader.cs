using System.Buffers;
using Akka.Serialization.V2;
using MessagePack;

namespace Akka.Serialization.MessagePack;

/// <summary>
/// MessagePack-based implementation of ICodecReader.
/// Stores ReadOnlyMemory&lt;byte&gt; and tracks consumed offset.
/// Creates a MessagePackReader ref struct per operation from _buffer[_consumed..], advancing _consumed by reader.Consumed.
/// </summary>
/// <remarks>
/// The MessagePackReader is a lightweight ref struct - creating one per operation is cheap.
/// Each read operation creates a reader from the remaining buffer, reads one value, and advances the consumed offset.
///
/// DateTime is deserialized from an array: [ticks (long), Kind (int)]
/// DateTimeOffset is deserialized from an array: [ticks (long), offset minutes (int)]
/// Guid is deserialized from 16-byte binary.
/// </remarks>
internal sealed class MessagePackCodecReader : ICodecReader
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private int _consumed;

    public MessagePackCodecReader(ReadOnlyMemory<byte> buffer)
    {
        _buffer = buffer;
        _consumed = 0;
    }

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

        // Deserialize from [ticks (long), Kind (int)]
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

        // Deserialize from [ticks (long), offset minutes (int)]
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

        // Read 16-byte binary
        var bytes = reader.ReadBytes();
        if (bytes == null || bytes.Value.Length != 16)
            throw new MessagePackSerializationException($"Expected 16 bytes for Guid, got {bytes?.Length ?? 0}");

        _consumed += (int)reader.Consumed;

        // Convert ReadOnlySequence<byte> to Guid
        if (bytes.Value.IsSingleSegment)
        {
            return new Guid(bytes.Value.FirstSpan);
        }
        else
        {
            // Multi-segment sequence - need to copy to contiguous memory
            Span<byte> span = stackalloc byte[16];
            bytes.Value.CopyTo(span);
            return new Guid(span);
        }
    }

    public decimal ReadDecimal()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);

        // Deserialize from [lo, mid, hi, flags]
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

        // Convert ReadOnlySequence<byte> to byte[]
        if (bytes.Value.IsSingleSegment)
        {
            return bytes.Value.FirstSpan.ToArray();
        }
        else
        {
            byte[] result = new byte[bytes.Value.Length];
            CopySequenceToArray(bytes.Value, result);
            return result;
        }
    }

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

    public void SkipField()
    {
        var reader = new MessagePackReader(_buffer[_consumed..]);
        reader.Skip();
        _consumed += (int)reader.Consumed;
    }

    private static void CopySequenceToArray(in ReadOnlySequence<byte> sequence, byte[] destination)
    {
        var position = 0;
        foreach (var segment in sequence)
        {
            segment.Span.CopyTo(destination.AsSpan(position));
            position += segment.Length;
        }
    }
}
