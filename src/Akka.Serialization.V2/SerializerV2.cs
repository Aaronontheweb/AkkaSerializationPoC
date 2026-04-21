using System.Buffers;

namespace Akka.Serialization.V2;

/// <summary>
/// Base class for all V2 serializers.
/// Replaces Akka.NET's Serializer and SerializerWithStringManifest with a codec-based API.
/// </summary>
/// <remarks>
/// Key differences from legacy serializers:
/// - Write takes AkkaWriter instead of returning byte[]
/// - Read takes AkkaReader instead of byte[]
/// - Multiple serializers can share the same writer/reader for zero-copy nesting
///
/// Wire format contract: (SerializerId, Manifest, Payload)
/// - SerializerId: Unique integer identifying this serializer (same as legacy)
/// - Manifest: String type hint for polymorphic deserialization
/// - Payload: MessagePack-encoded fields (array format, may contain nested arrays)
///
/// For zero-copy nested serialization:
/// An envelope serializer writes its fields, then calls innerSerializer.Write(writer, innerMessage)
/// using the SAME writer. The inner serializer's BeginObject becomes a nested array in the payload.
/// All layers write to a single shared IBufferWriter&lt;byte&gt;.
///
/// V1 bridge: ToBinary/FromBinary methods enable V2 serializers to work with Akka.NET's
/// existing byte[]-based transport. When the transport is upgraded to pass IBufferWriter&lt;byte&gt;
/// directly, these methods are bypassed and the byte[] allocation disappears.
///
/// Integration path: In Akka.NET proper, this class would extend SerializerWithStringManifest
/// so V2 serializers plug directly into HOCON config, the existing transport, and
/// Akka.Serialization.Serialization — no separate registry needed.
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
    /// Optional reference to the ActorSystem. Set by the SerializerRegistry
    /// when the serializer is registered with a system-aware registry.
    /// Serializers that need to resolve ActorRefs (e.g. by path) can use this.
    /// </summary>
    public Akka.Actor.ExtendedActorSystem? System { get; internal set; }

    /// <summary>
    /// Returns the manifest string for the given object.
    /// The manifest provides a type hint for polymorphic deserialization.
    /// </summary>
    public abstract string? Manifest(object obj);

    /// <summary>
    /// Writes the object to the writer.
    /// The writer may be shared with other serializers for zero-copy nested serialization.
    /// </summary>
    public abstract void Write(AkkaWriter writer, object obj);

    /// <summary>
    /// Reads an object from the reader using the manifest string.
    /// The reader may be shared with other serializers for nested deserialization.
    /// </summary>
    public abstract object Read(AkkaReader reader, string manifest);

    /// <summary>
    /// Provides a size hint for buffer pre-allocation.
    /// </summary>
    public virtual int SizeHint(object obj) => 256;

    /// <summary>
    /// V1 bridge: Serializes to byte[] for compatibility with Akka.NET's existing transport.
    /// When the transport is upgraded to pass IBufferWriter&lt;byte&gt; directly, this allocation goes away.
    /// </summary>
    public byte[] ToBinary(object obj)
    {
        var buffer = new ArrayBufferWriter<byte>(SizeHint(obj));
        var writer = new AkkaWriter(buffer);
        Write(writer, obj);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// V1 bridge: Deserializes from byte[] for compatibility with Akka.NET's existing transport.
    /// </summary>
    public object FromBinary(byte[] bytes, string manifest)
    {
        var reader = new AkkaReader(bytes);
        return Read(reader, manifest);
    }
}

/// <summary>
/// Generic base class for protocol-scoped serializers.
/// The type parameter defines a marker interface that scopes which message types belong to this serializer.
/// </summary>
public abstract class SerializerV2<TProtocol> : SerializerV2
{
}
