using Akka.Serialization.V2;

namespace Akka.Serialization.V2.Tests.Messages;

/// <summary>
/// SerializerV2 for RemoteEnvelope messages demonstrating zero-copy nesting.
/// Writes envelope metadata, then delegates to the inner message's serializer using the SAME writer.
/// </summary>
public sealed class RemoteEnvelopeSerializer : SerializerV2
{
    private readonly SerializerRegistry _registry;

    public RemoteEnvelopeSerializer(SerializerRegistry registry)
    {
        _registry = registry;
    }

    public override int Identifier => 9999;

    public override string? Manifest(object obj) => obj switch
    {
        RemoteEnvelope => "remote-envelope-v1",
        _ => throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj))
    };

    public override void Write(ICodecWriter writer, object obj)
    {
        if (obj is not RemoteEnvelope envelope)
            throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj));

        // Look up the serializer for the inner message
        var innerSerializer = _registry.FindFor(envelope.Message)
            ?? throw new InvalidOperationException($"No serializer found for type {envelope.Message.GetType()}");

        var innerManifest = innerSerializer.Manifest(envelope.Message)
            ?? throw new InvalidOperationException($"Serializer {innerSerializer.GetType()} returned null manifest for {envelope.Message.GetType()}");

        // Write envelope fields + metadata about the inner message
        // Field 0: recipientPath
        // Field 1: senderPath
        // Field 2: innerSerializerId
        // Field 3: innerManifest
        // Field 4: inner message (as nested array via innerSerializer.Write)
        writer.BeginObject(5);
        writer.WriteString(envelope.RecipientPath);
        writer.WriteString(envelope.SenderPath);
        writer.WriteInt32(innerSerializer.Identifier);
        writer.WriteString(innerManifest);

        // CRITICAL: Delegate to inner serializer using SAME writer
        // The inner serializer's BeginObject creates a nested array at field index 4
        innerSerializer.Write(writer, envelope.Message);
    }

    public override object Read(ICodecReader reader, string manifest)
    {
        if (manifest != "remote-envelope-v1")
            throw new ArgumentException($"Unknown manifest: {manifest}", nameof(manifest));

        return ReadRemoteEnvelope(reader);
    }

    private RemoteEnvelope ReadRemoteEnvelope(ICodecReader reader)
    {
        var fieldCount = reader.BeginReadObject();

        // Read envelope fields
        var recipientPath = reader.ReadString() ?? string.Empty;
        var senderPath = reader.ReadString() ?? string.Empty;
        var innerSerializerId = reader.ReadInt32();
        var innerManifest = reader.ReadString() ?? string.Empty;

        // Look up the inner message's serializer
        var innerSerializer = _registry.GetById(innerSerializerId);

        // Deserialize the inner message using SAME reader
        // The inner serializer will read its nested array from field index 4
        var innerMessage = innerSerializer.Read(reader, innerManifest);

        // Skip any unknown trailing fields for forward compatibility
        for (int i = 5; i < fieldCount; i++)
        {
            reader.SkipField();
        }

        return new RemoteEnvelope(recipientPath, senderPath, innerMessage);
    }

    public override int SizeHint(object obj) => 512;
}
