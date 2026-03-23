namespace Akka.Serialization.V2.Tests.Messages;

/// <summary>
/// Serializer for ActorRef-containing messages.
/// Demonstrates the pattern: serialize ActorRef as path string,
/// resolve on deserialization using the System property.
/// </summary>
public sealed class ActorRefMessageSerializer : SerializerV2
{
    public override int Identifier => 7001;

    public override string? Manifest(object obj) => obj switch
    {
        SubscribeToEvents => "subscribe-to-events-v1",
        _ => throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj))
    };

    public override void Write(AkkaWriter writer, object obj)
    {
        switch (obj)
        {
            case SubscribeToEvents msg:
                writer.BeginObject(2);
                writer.WriteString(msg.SubscriberPath);
                writer.WriteString(msg.Topic);
                break;
            default:
                throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj));
        }
    }

    public override object Read(AkkaReader reader, string manifest)
    {
        return manifest switch
        {
            "subscribe-to-events-v1" => ReadSubscribeToEvents(reader),
            _ => throw new ArgumentException($"Unknown manifest: {manifest}", nameof(manifest))
        };
    }

    private SubscribeToEvents ReadSubscribeToEvents(AkkaReader reader)
    {
        var fieldCount = reader.BeginReadObject();
        var subscriberPath = reader.ReadString() ?? string.Empty;
        var topic = reader.ReadString() ?? string.Empty;

        for (int i = 2; i < fieldCount; i++)
            reader.SkipField();

        return new SubscribeToEvents(subscriberPath, topic);
    }

    public override int SizeHint(object obj) => 128;
}
