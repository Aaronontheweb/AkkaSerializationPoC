namespace Akka.Serialization.V2;

/// <summary>
/// Base class for all V2 serializers.
/// Replaces Akka.NET's Serializer and SerializerWithStringManifest with a codec-based API.
/// </summary>
/// <remarks>
/// Key differences from legacy serializers:
/// - Write takes ICodecWriter instead of returning byte[]
/// - Read takes ICodecReader instead of byte[]
/// - Multiple serializers can share the same writer/reader for zero-copy nesting
///
/// Wire format contract: (SerializerId, Manifest, Payload)
/// - SerializerId: Unique integer identifying this serializer (same as legacy)
/// - Manifest: String type hint for polymorphic deserialization
/// - Payload: Codec-encoded message data (typically MessagePack array)
///
/// For zero-copy nested serialization:
/// An envelope serializer writes its fields, then calls innerSerializer.Write(writer, innerMessage)
/// using the SAME writer. The inner serializer's BeginObject becomes a nested array in the payload.
/// All layers write to a single shared IBufferWriter&lt;byte&gt;.
/// </remarks>
public abstract class SerializerV2
{
    /// <summary>
    /// Unique identifier for this serializer.
    /// Must match the SerializerId in the Akka.NET configuration.
    /// Must be unique across all serializers in the ActorSystem.
    /// </summary>
    public abstract int Identifier { get; }

    /// <summary>
    /// Returns the manifest string for the given object.
    /// The manifest provides a type hint for polymorphic deserialization.
    /// </summary>
    /// <param name="obj">The object to get a manifest for</param>
    /// <returns>A manifest string identifying the object's type, or null for unambiguous types</returns>
    public abstract string? Manifest(object obj);

    /// <summary>
    /// Writes the object to the codec writer.
    /// The writer may be shared with other serializers for zero-copy nested serialization.
    /// </summary>
    /// <param name="writer">The codec writer to write to</param>
    /// <param name="obj">The object to serialize</param>
    public abstract void Write(ICodecWriter writer, object obj);

    /// <summary>
    /// Reads an object from the codec reader using the manifest string.
    /// The reader may be shared with other serializers for nested deserialization.
    /// </summary>
    /// <param name="reader">The codec reader to read from</param>
    /// <param name="manifest">The manifest string identifying the object's type</param>
    /// <returns>The deserialized object</returns>
    public abstract object Read(ICodecReader reader, string manifest);

    /// <summary>
    /// Provides a size hint for buffer pre-allocation.
    /// Implementations should return a reasonable estimate of the serialized size.
    /// The default implementation returns 256 bytes.
    /// </summary>
    /// <param name="obj">The object to estimate size for</param>
    /// <returns>Estimated serialized size in bytes</returns>
    public virtual int SizeHint(object obj) => 256;
}
