using System.Buffers;
using Akka.Actor;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2.Tests.Messages;
using FluentAssertions;
using Xunit;

namespace Akka.Serialization.V2.Tests;

/// <summary>
/// Integration tests for ActorRef serialization via V2 serializers.
/// Demonstrates the pattern: serialize ActorRef as path string,
/// resolve on deserialization using System.Provider.ResolveActorRef(path).
/// </summary>
public class ActorRefSerializationTests : IDisposable
{
    private readonly ActorSystem _actorSystem;
    private readonly SerializerRegistry _registry;
    private readonly ActorRefMessageSerializer _serializer;
    private readonly MessagePackCodecProvider _codec = MessagePackCodecProvider.Instance;

    public ActorRefSerializationTests()
    {
        _actorSystem = ActorSystem.Create("actorref-serialization-test");
        _registry = new SerializerRegistry((ExtendedActorSystem)_actorSystem);
        _serializer = new ActorRefMessageSerializer();
        _registry.Register(_serializer, typeof(SubscribeToEvents));
    }

    public void Dispose()
    {
        _actorSystem.Terminate().Wait(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Serializer_HasSystemReference_WhenRegisteredWithSystemAwareRegistry()
    {
        // The serializer should have a System reference set by the registry
        _serializer.System.Should().NotBeNull();
        _serializer.System.Should().Be(_actorSystem);
    }

    [Fact]
    public void SubscribeToEvents_RoundTrip_WithActorPath()
    {
        // Arrange - Create a real actor and get its path
        var actor = _actorSystem.ActorOf(Props.Create(() => new BlackholeActor()), "subscriber");
        var actorPath = actor.Path.ToSerializationFormat();

        var original = new SubscribeToEvents(actorPath, "user-events");

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        _serializer.Write(writer, original);

        // Act - Deserialize
        var reader = _codec.CreateReader(buffer.WrittenMemory);
        var deserialized = (SubscribeToEvents)_serializer.Read(reader, "subscribe-to-events-v1");

        // Assert - Path preserved
        deserialized.SubscriberPath.Should().Be(actorPath);
        deserialized.Topic.Should().Be(original.Topic);

        // Assert - Can resolve the actor from the path
        var resolvedRef = ((ExtendedActorSystem)_actorSystem)
            .Provider.ResolveActorRef(deserialized.SubscriberPath);
        resolvedRef.Should().NotBeNull();
        resolvedRef.Path.Should().Be(actor.Path);
    }

    [Fact]
    public void SubscribeToEvents_InsideRemoteEnvelope()
    {
        // Arrange - ActorRef message inside a V2 envelope
        var actor = _actorSystem.ActorOf(Props.Create(() => new BlackholeActor()), "envelope-subscriber");
        var actorPath = actor.Path.ToSerializationFormat();

        var innerMessage = new SubscribeToEvents(actorPath, "order-events");

        var remoteSerializer = new RemoteEnvelopeSerializer(_registry);
        _registry.Register(remoteSerializer, typeof(RemoteEnvelope));

        var envelope = new RemoteEnvelope(
            RecipientPath: "akka://system/user/handler",
            SenderPath: "akka://system/user/client",
            Message: innerMessage);

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        remoteSerializer.Write(writer, envelope);

        // Act - Deserialize
        var reader = _codec.CreateReader(buffer.WrittenMemory);
        var deserialized = (RemoteEnvelope)remoteSerializer.Read(reader, "remote-envelope-v1");

        // Assert
        deserialized.RecipientPath.Should().Be(envelope.RecipientPath);
        deserialized.SenderPath.Should().Be(envelope.SenderPath);

        var innerDeserialized = deserialized.Message.Should().BeOfType<SubscribeToEvents>().Subject;
        innerDeserialized.SubscriberPath.Should().Be(actorPath);
        innerDeserialized.Topic.Should().Be(innerMessage.Topic);
    }

    [Fact]
    public void SerializerRegistry_WithoutSystem_LeavesSystemNull()
    {
        // Arrange - Registry without system
        var registry = new SerializerRegistry();
        var serializer = new ActorRefMessageSerializer();
        registry.Register(serializer, typeof(SubscribeToEvents));

        // Assert
        serializer.System.Should().BeNull();
    }

    /// <summary>Simple actor that ignores all messages.</summary>
    private class BlackholeActor : ReceiveActor
    {
        public BlackholeActor()
        {
            ReceiveAny(_ => { });
        }
    }
}
