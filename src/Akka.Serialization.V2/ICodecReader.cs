namespace Akka.Serialization.V2;

/// <summary>
/// Read-side abstraction for wire format decoding.
/// Abstracts the underlying codec (MessagePack, Protobuf, etc.) to allow format swapping.
/// </summary>
/// <remarks>
/// The reader is designed for sequential field reads in index order (array format).
/// Call BeginReadObject() to read the array header and get the field count.
/// Then read each field in order using the appropriate Read* method.
///
/// For version tolerance, use the field count to determine how many fields to read.
/// If reading old data (fewer fields), use default values for missing fields.
/// If reading new data (more fields), call SkipField() for each unknown field.
///
/// The reader maintains an internal position offset and advances it as data is consumed.
/// Multiple deserializers can share the same reader instance for nested deserialization.
/// </remarks>
public interface ICodecReader
{
    /// <summary>
    /// Begins reading an object and returns the number of fields.
    /// In MessagePack format, this reads an array header.
    /// </summary>
    /// <returns>The number of fields in the object</returns>
    int BeginReadObject();

    /// <summary>
    /// Reads a 32-bit integer value.
    /// </summary>
    int ReadInt32();

    /// <summary>
    /// Reads a 64-bit integer value.
    /// </summary>
    long ReadInt64();

    /// <summary>
    /// Reads a string value. Returns null if the value is null.
    /// </summary>
    string? ReadString();

    /// <summary>
    /// Reads a boolean value.
    /// </summary>
    bool ReadBool();

    /// <summary>
    /// Reads a double-precision floating point value.
    /// </summary>
    double ReadDouble();

    /// <summary>
    /// Reads a DateTime value.
    /// </summary>
    DateTime ReadDateTime();

    /// <summary>
    /// Reads a DateTimeOffset value.
    /// </summary>
    DateTimeOffset ReadDateTimeOffset();

    /// <summary>
    /// Reads a Guid value.
    /// </summary>
    Guid ReadGuid();

    /// <summary>
    /// Reads a decimal value with full precision.
    /// </summary>
    decimal ReadDecimal();

    /// <summary>
    /// Reads a byte array value. Returns null if the value is null.
    /// </summary>
    byte[]? ReadBytes();

    /// <summary>
    /// Attempts to read a null value. If the next value is null, consumes it and returns true.
    /// Otherwise, leaves the position unchanged and returns false.
    /// </summary>
    bool TryReadNull();

    /// <summary>
    /// Skips the next field without reading its value.
    /// Used for forward compatibility when encountering unknown fields from newer versions.
    /// </summary>
    void SkipField();

    /// <summary>
    /// Gets the number of bytes consumed from the buffer so far.
    /// </summary>
    int Consumed { get; }
}
