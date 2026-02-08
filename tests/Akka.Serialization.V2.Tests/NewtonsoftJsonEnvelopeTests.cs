using System.Buffers;
using Akka.Actor;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2.Tests.Messages;
using FluentAssertions;
using Xunit;

namespace Akka.Serialization.V2.Tests;

/// <summary>
/// Tests that a message serialized by Akka.NET's built-in NewtonsoftJsonSerializer
/// (wrapped in SerializerV2Adapter) can live inside a V2 envelope.
/// This validates the real migration path: existing Akka.NET users with default
/// Newtonsoft.Json serialization can wrap their messages in V2 envelopes.
/// </summary>
public class NewtonsoftJsonEnvelopeTests : IDisposable
{
    private readonly ActorSystem _actorSystem;
    private readonly MessagePackCodecProvider _codecProvider = MessagePackCodecProvider.Instance;

    public NewtonsoftJsonEnvelopeTests()
    {
        _actorSystem = ActorSystem.Create("newtonsoft-json-test");
    }

    public void Dispose()
    {
        _actorSystem.Terminate().Wait(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void NewtonsoftJson_WrappedInAdapter_RoundTrip()
    {
        // Arrange - Get Akka.NET's built-in Newtonsoft.Json serializer
        var system = (ExtendedActorSystem)_actorSystem;
        var newtonsoftSerializer = system.Serialization.FindSerializerForType(typeof(object));

        // Wrap it in the V2 adapter
        var adapter = new SerializerV2Adapter(newtonsoftSerializer);

        var original = new UserCreated(
            UserId: "json-user-001",
            Email: "json@example.com",
            CreatedAt: new DateTime(2024, 9, 15, 10, 30, 0, DateTimeKind.Utc));

        // Act - Serialize through adapter
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codecProvider.CreateWriter(buffer);
        adapter.Write(writer, original);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize through adapter
        var manifest = adapter.Manifest(original);
        var reader = _codecProvider.CreateReader(bytes);
        var deserialized = (UserCreated)adapter.Read(reader, manifest!);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be(original.UserId);
        deserialized.Email.Should().Be(original.Email);
        deserialized.CreatedAt.Should().Be(original.CreatedAt);
    }

    [Fact]
    public void NewtonsoftJson_InsideV2RemoteEnvelope()
    {
        // Arrange - Wrap Newtonsoft.Json serializer and put inside V2 envelope
        var system = (ExtendedActorSystem)_actorSystem;
        var newtonsoftSerializer = system.Serialization.FindSerializerForType(typeof(object));
        var adapter = new SerializerV2Adapter(newtonsoftSerializer);

        var registry = new SerializerRegistry();
        registry.Register(adapter, typeof(UserCreated));

        var remoteSerializer = new RemoteEnvelopeSerializer(registry);
        registry.Register(remoteSerializer, typeof(RemoteEnvelope));

        var innerMessage = new UserCreated(
            UserId: "json-envelope-user",
            Email: "json-envelope@example.com",
            CreatedAt: new DateTime(2024, 10, 1, 12, 0, 0, DateTimeKind.Utc));

        var envelope = new RemoteEnvelope(
            RecipientPath: "akka://system/user/target",
            SenderPath: "akka://system/user/source",
            Message: innerMessage);

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codecProvider.CreateWriter(buffer);
        remoteSerializer.Write(writer, envelope);

        // Act - Deserialize
        var reader = _codecProvider.CreateReader(buffer.WrittenMemory);
        var deserialized = (RemoteEnvelope)remoteSerializer.Read(reader, "remote-envelope-v1");

        // Assert - Envelope metadata preserved
        deserialized.RecipientPath.Should().Be(envelope.RecipientPath);
        deserialized.SenderPath.Should().Be(envelope.SenderPath);

        // Assert - Inner message deserialized correctly through JSON adapter
        deserialized.Message.Should().BeOfType<UserCreated>();
        var inner = (UserCreated)deserialized.Message;
        inner.UserId.Should().Be(innerMessage.UserId);
        inner.Email.Should().Be(innerMessage.Email);
        inner.CreatedAt.Should().Be(innerMessage.CreatedAt);
    }

    [Fact]
    public void NewtonsoftJson_DoubleNested()
    {
        // V2 Remote -> V2 DData -> JSON message
        var system = (ExtendedActorSystem)_actorSystem;
        var newtonsoftSerializer = system.Serialization.FindSerializerForType(typeof(object));
        var adapter = new SerializerV2Adapter(newtonsoftSerializer);

        var registry = new SerializerRegistry();
        registry.Register(adapter, typeof(UserCreated));

        var remoteSerializer = new RemoteEnvelopeSerializer(registry);
        var ddataSerializer = new DDataEnvelopeSerializer(registry);
        registry.Register(remoteSerializer, typeof(RemoteEnvelope));
        registry.Register(ddataSerializer, typeof(DDataEnvelope));

        var innerMessage = new UserCreated(
            UserId: "json-double-nested",
            Email: "nested-json@example.com",
            CreatedAt: new DateTime(2024, 11, 15, 8, 0, 0, DateTimeKind.Utc));

        var ddataEnvelope = new DDataEnvelope(
            Key: "users/json-nested",
            Version: 7,
            Data: innerMessage);

        var remoteEnvelope = new RemoteEnvelope(
            RecipientPath: "akka://cluster/user/replicator",
            SenderPath: "akka://cluster/user/client",
            Message: ddataEnvelope);

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codecProvider.CreateWriter(buffer);
        remoteSerializer.Write(writer, remoteEnvelope);

        // Act - Deserialize
        var reader = _codecProvider.CreateReader(buffer.WrittenMemory);
        var deserialized = (RemoteEnvelope)remoteSerializer.Read(reader, "remote-envelope-v1");

        // Assert - All layers preserved
        deserialized.RecipientPath.Should().Be(remoteEnvelope.RecipientPath);
        var ddataLayer = deserialized.Message.Should().BeOfType<DDataEnvelope>().Subject;
        ddataLayer.Key.Should().Be(ddataEnvelope.Key);
        ddataLayer.Version.Should().Be(ddataEnvelope.Version);

        var innerLayer = ddataLayer.Data.Should().BeOfType<UserCreated>().Subject;
        innerLayer.UserId.Should().Be(innerMessage.UserId);
        innerLayer.Email.Should().Be(innerMessage.Email);
        innerLayer.CreatedAt.Should().Be(innerMessage.CreatedAt);
    }
}
