using Akka.Serialization.V2;

namespace Akka.Serialization.V2.Tests.Messages;

/// <summary>
/// SerializerV2 for DDataEnvelope messages demonstrating zero-copy nesting.
/// Writes envelope metadata, then delegates to the inner data's serializer using the SAME writer.
/// </summary>
public sealed class DDataEnvelopeSerializer : SerializerV2
{
    private readonly SerializerRegistry _registry;

    public DDataEnvelopeSerializer(SerializerRegistry registry)
    {
        _registry = registry;
    }

    public override int Identifier => 9998;

    public override string? Manifest(object obj) => obj switch
    {
        DDataEnvelope => "ddata-envelope-v1",
        _ => throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj))
    };

    public override void Write(ICodecWriter writer, object obj)
    {
        if (obj is not DDataEnvelope envelope)
            throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj));

        // Look up the serializer for the inner data
        var innerSerializer = _registry.FindFor(envelope.Data)
            ?? throw new InvalidOperationException($"No serializer found for type {envelope.Data.GetType()}");

        var innerManifest = innerSerializer.Manifest(envelope.Data)
            ?? throw new InvalidOperationException($"Serializer {innerSerializer.GetType()} returned null manifest for {envelope.Data.GetType()}");

        // Write envelope fields + metadata about the inner data
        // Field 0: key
        // Field 1: version
        // Field 2: innerSerializerId
        // Field 3: innerManifest
        // Field 4: inner data (as nested array via innerSerializer.Write)
        writer.BeginObject(5);
        writer.WriteString(envelope.Key);
        writer.WriteInt64(envelope.Version);
        writer.WriteInt32(innerSerializer.Identifier);
        writer.WriteString(innerManifest);

        // CRITICAL: Delegate to inner serializer using SAME writer
        // The inner serializer's BeginObject creates a nested array at field index 4
        innerSerializer.Write(writer, envelope.Data);
    }

    public override object Read(ICodecReader reader, string manifest)
    {
        if (manifest != "ddata-envelope-v1")
            throw new ArgumentException($"Unknown manifest: {manifest}", nameof(manifest));

        return ReadDDataEnvelope(reader);
    }

    private DDataEnvelope ReadDDataEnvelope(ICodecReader reader)
    {
        var fieldCount = reader.BeginReadObject();

        // Read envelope fields
        var key = reader.ReadString() ?? string.Empty;
        var version = reader.ReadInt64();
        var innerSerializerId = reader.ReadInt32();
        var innerManifest = reader.ReadString() ?? string.Empty;

        // Look up the inner data's serializer
        var innerSerializer = _registry.GetById(innerSerializerId);

        // Deserialize the inner data using SAME reader
        // The inner serializer will read its nested array from field index 4
        var innerData = innerSerializer.Read(reader, innerManifest);

        // Skip any unknown trailing fields for forward compatibility
        for (int i = 5; i < fieldCount; i++)
        {
            reader.SkipField();
        }

        return new DDataEnvelope(key, version, innerData);
    }

    public override int SizeHint(object obj) => 512;
}
