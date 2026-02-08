using Akka.Actor;

namespace Akka.Serialization.V2;

/// <summary>
/// Adapter that wraps a legacy Akka.NET Serializer or SerializerWithStringManifest
/// to work within the V2 serialization system.
/// </summary>
/// <remarks>
/// This adapter enables backwards compatibility:
/// - Legacy serializers can coexist with V2 serializers in the same registry
/// - Envelope messages can wrap legacy-serialized payloads
/// - Migration can happen incrementally (convert serializers one at a time)
///
/// The adapter calls the legacy serializer's ToBinary() method and writes the resulting
/// byte[] as a binary blob. On read, it reads the blob and calls FromBinary().
///
/// This means the legacy bytes are embedded directly in the V2 payload.
/// For nested messages, the nesting point becomes a binary field containing the legacy bytes.
/// </remarks>
public sealed class SerializerV2Adapter : SerializerV2
{
    private readonly Serializer _legacy;

    /// <summary>
    /// Creates a new adapter wrapping the specified legacy serializer.
    /// </summary>
    /// <param name="legacy">The legacy serializer to wrap</param>
    public SerializerV2Adapter(Serializer legacy)
    {
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
    }

    /// <summary>
    /// Returns the identifier of the wrapped legacy serializer.
    /// </summary>
    public override int Identifier => _legacy.Identifier;

    /// <summary>
    /// Returns the manifest for the object using the legacy serializer's manifest logic.
    /// For SerializerWithStringManifest, uses Manifest(obj).
    /// For plain Serializer, returns the type's full name.
    /// </summary>
    public override string? Manifest(object obj)
    {
        return _legacy switch
        {
            SerializerWithStringManifest sm => sm.Manifest(obj),
            _ => obj.GetType().FullName
        };
    }

    /// <summary>
    /// Serializes the object using the legacy ToBinary() method and writes the bytes as a blob.
    /// </summary>
    public override void Write(ICodecWriter writer, object obj)
    {
        byte[] bytes = _legacy.ToBinary(obj);
        writer.WriteBytes(bytes);
    }

    /// <summary>
    /// Reads a byte blob and deserializes it using the legacy FromBinary() method.
    /// For SerializerWithStringManifest, passes the manifest string.
    /// For plain Serializer, uses typeof(object) as the type hint.
    /// </summary>
    public override object Read(ICodecReader reader, string manifest)
    {
        byte[]? bytes = reader.ReadBytes();

        if (bytes == null)
        {
            throw new InvalidOperationException("Cannot deserialize null bytes from legacy serializer");
        }

        return _legacy switch
        {
            SerializerWithStringManifest sm => sm.FromBinary(bytes, manifest),
            _ => _legacy.FromBinary(bytes, typeof(object))
        };
    }

    /// <summary>
    /// Provides a size hint based on the legacy serializer's behavior.
    /// Returns a conservative estimate since legacy serializers don't expose size hints.
    /// </summary>
    public override int SizeHint(object obj) => 512;
}
