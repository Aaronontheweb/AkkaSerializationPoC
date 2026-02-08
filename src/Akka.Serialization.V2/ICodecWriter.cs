namespace Akka.Serialization.V2;

/// <summary>
/// Write-side abstraction for wire format encoding.
/// Abstracts the underlying codec (MessagePack, Protobuf, etc.) to allow format swapping.
/// </summary>
/// <remarks>
/// The writer is designed for sequential field writes in index order (array format).
/// Each BeginObject(N) writes an array header, and subsequent Write* calls write field values.
/// For nested objects, call BeginObject again - this creates nested arrays in the underlying format.
///
/// The writer maintains a reference to an IBufferWriter&lt;byte&gt; internally and writes directly to it.
/// Multiple serializers can share the same writer instance to achieve zero-copy nested serialization.
/// </remarks>
public interface ICodecWriter : IDisposable
{
    /// <summary>
    /// Begins writing an object with the specified number of fields.
    /// In MessagePack format, this writes an array header.
    /// </summary>
    /// <param name="fieldCount">Number of fields in the object</param>
    void BeginObject(int fieldCount);

    /// <summary>
    /// Writes a 32-bit integer value.
    /// </summary>
    void WriteInt32(int value);

    /// <summary>
    /// Writes a 64-bit integer value.
    /// </summary>
    void WriteInt64(long value);

    /// <summary>
    /// Writes a string value. Null strings are written as null.
    /// </summary>
    void WriteString(string? value);

    /// <summary>
    /// Writes a boolean value.
    /// </summary>
    void WriteBool(bool value);

    /// <summary>
    /// Writes a double-precision floating point value.
    /// </summary>
    void WriteDouble(double value);

    /// <summary>
    /// Writes a DateTime value.
    /// </summary>
    void WriteDateTime(DateTime value);

    /// <summary>
    /// Writes a DateTimeOffset value.
    /// </summary>
    void WriteDateTimeOffset(DateTimeOffset value);

    /// <summary>
    /// Writes a Guid value.
    /// </summary>
    void WriteGuid(Guid value);

    /// <summary>
    /// Writes a byte array value.
    /// </summary>
    void WriteBytes(ReadOnlySpan<byte> value);

    /// <summary>
    /// Writes a null value.
    /// </summary>
    void WriteNull();

    /// <summary>
    /// Flushes any buffered data to the underlying buffer.
    /// Must be called when writing is complete.
    /// </summary>
    void Flush();
}
