using System.Buffers;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2;
using Akka.Serialization.V2.Tests.Messages;
using FluentAssertions;
using Xunit;

namespace Akka.Serialization.V2.Tests.Generated;

/// <summary>
/// Tests verifying the SOURCE-GENERATED serializer works correctly
/// in integration scenarios (envelopes, registry routing).
/// </summary>
public class GeneratedSerializerTests
{
    private readonly AnnotatedMessageSerializer _generatedSerializer = new();
    private readonly MessagePackCodecProvider _codec = MessagePackCodecProvider.Instance;

    [Fact]
    public void Generated_InEnvelope_RoundTrip()
    {
        // The generated serializer should work inside a RemoteEnvelope
        // via the SerializerRegistry, registered by protocol interface
        var registry = new SerializerRegistry();
        registry.Register(_generatedSerializer, typeof(IAnnotatedProtocol));

        var remoteSerializer = new RemoteEnvelopeSerializer(registry);
        registry.Register(remoteSerializer, typeof(RemoteEnvelope));

        var inner = new UserCreatedAnnotated("gen-user-1", "gen@example.com",
            new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        var envelope = new RemoteEnvelope("/user/target", "/user/sender", inner);

        // Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        remoteSerializer.Write(writer, envelope);

        // Deserialize
        var reader = _codec.CreateReader(buffer.WrittenMemory);
        var deserialized = (RemoteEnvelope)remoteSerializer.Read(reader, "remote-envelope-v1");

        // Assert
        deserialized.RecipientPath.Should().Be("/user/target");
        deserialized.SenderPath.Should().Be("/user/sender");
        var innerMsg = deserialized.Message.Should().BeOfType<UserCreatedAnnotated>().Subject;
        innerMsg.UserId.Should().Be("gen-user-1");
        innerMsg.Email.Should().Be("gen@example.com");
    }

    [Fact]
    public void Generated_Manifest_ReturnsCorrectValues()
    {
        _generatedSerializer.Manifest(new UserCreatedAnnotated("a", "b", DateTime.UtcNow))
            .Should().Be("user-created-v1");
        _generatedSerializer.Manifest(new UserUpdatedAnnotated("a", null, null, DateTime.UtcNow))
            .Should().Be("user-updated-v1");
        _generatedSerializer.Manifest(new OrderPlacedAnnotated(Guid.Empty, "c", 0m, DateTimeOffset.UtcNow))
            .Should().Be("order-placed-v1");
    }

    [Fact]
    public void Generated_Identifier_MatchesAttribute()
    {
        _generatedSerializer.Identifier.Should().Be(6001);
    }

    [Fact]
    public void Generated_BoundTypes_ContainsProtocolInterface()
    {
        // BoundTypes should contain the protocol interface, not individual concrete types
        AnnotatedMessageSerializerSetup.BoundTypes.Should().ContainSingle()
            .Which.Should().Be(typeof(IAnnotatedProtocol));
    }

    [Fact]
    public void Generated_Registry_ResolvesViaInterface()
    {
        // Registering with the protocol interface should resolve for concrete types
        var registry = new SerializerRegistry();
        registry.Register(_generatedSerializer, typeof(IAnnotatedProtocol));

        registry.FindFor(new UserCreatedAnnotated("x", "y", DateTime.UtcNow))
            .Should().BeSameAs(_generatedSerializer);
        registry.FindFor(new UserUpdatedAnnotated("x", null, null, DateTime.UtcNow))
            .Should().BeSameAs(_generatedSerializer);
        registry.FindFor(new OrderPlacedAnnotated(Guid.Empty, "c", 0m, DateTimeOffset.UtcNow))
            .Should().BeSameAs(_generatedSerializer);
    }
}
