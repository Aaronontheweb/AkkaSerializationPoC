using Akka.Actor;

namespace Akka.Serialization.V2;

/// <summary>
/// Adapter that wraps a legacy Akka.NET Serializer or SerializerWithStringManifest
/// to work within the V2 serialization system.
/// </summary>
/// <remarks>
/// Enables backwards compatibility:
/// - Legacy serializers can coexist with V2 serializers in the same registry
/// - Envelope messages can wrap legacy-serialized payloads
/// - Migration can happen incrementally (convert serializers one at a time)
///
/// The adapter calls the legacy ToBinary() and writes the resulting byte[] as a binary blob.
/// On read, it reads the blob and calls FromBinary().
/// </remarks>
public sealed class SerializerV2Adapter : SerializerV2
{
    private readonly Serializer _legacy;

    public SerializerV2Adapter(Serializer legacy)
    {
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
    }

    public override int Identifier => _legacy.Identifier;

    public override string? Manifest(object obj)
    {
        return _legacy switch
        {
            SerializerWithStringManifest sm => sm.Manifest(obj),
            _ => obj.GetType().FullName
        };
    }

    public override void Write(AkkaWriter writer, object obj)
    {
        byte[] bytes = _legacy.ToBinary(obj);
        writer.WriteBytes(bytes);
    }

    public override object Read(AkkaReader reader, string manifest)
    {
        byte[]? bytes = reader.ReadBytes();

        if (bytes == null)
            throw new InvalidOperationException("Cannot deserialize null bytes from legacy serializer");

        return _legacy switch
        {
            SerializerWithStringManifest sm => sm.FromBinary(bytes, manifest),
            _ => _legacy.FromBinary(bytes, typeof(object))
        };
    }

    public override int SizeHint(object obj) => 512;
}
