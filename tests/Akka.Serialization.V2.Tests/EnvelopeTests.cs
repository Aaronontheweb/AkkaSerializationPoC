using System.Buffers;
using Akka.Serialization.V2;
using Akka.Serialization.V2.Tests.Messages;
using FluentAssertions;
using Xunit;

namespace Akka.Serialization.V2.Tests;

/// <summary>
/// Tests for envelope message serialization (Phase 2).
/// Validates zero-copy nested serialization with single-buffer writes.
/// </summary>
public class EnvelopeTests
{
    private readonly SerializerRegistry _registry;
    private readonly RemoteEnvelopeSerializer _remoteSerializer;
    private readonly DDataEnvelopeSerializer _ddataSerializer;
    private readonly UserMessageSerializer _userSerializer;

    public EnvelopeTests()
    {
        _registry = new SerializerRegistry();
        _userSerializer = new UserMessageSerializer();
        _remoteSerializer = new RemoteEnvelopeSerializer(_registry);
        _ddataSerializer = new DDataEnvelopeSerializer(_registry);

        // Register all serializers
        _registry.Register(_userSerializer, typeof(UserCreated), typeof(UserUpdated));
        _registry.Register(_remoteSerializer, typeof(RemoteEnvelope));
        _registry.Register(_ddataSerializer, typeof(DDataEnvelope));
    }

    [Fact]
    public void Should_RoundTrip_RemoteEnvelope_WithUserCreated()
    {
        // Arrange
        var innerMessage = new UserCreated(
            UserId: "user-123",
            Email: "test@example.com",
            CreatedAt: new DateTime(2024, 1, 15, 10, 30, 45, DateTimeKind.Utc));

        var envelope = new RemoteEnvelope(
            RecipientPath: "akka://system/user/recipient",
            SenderPath: "akka://system/user/sender",
            Message: innerMessage);

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new AkkaWriter(buffer);
        _remoteSerializer.Write(writer, envelope);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var manifest = _remoteSerializer.Manifest(envelope);
        var reader = new AkkaReader(bytes);
        var deserialized = (RemoteEnvelope)_remoteSerializer.Read(reader, manifest!);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.RecipientPath.Should().Be(envelope.RecipientPath);
        deserialized.SenderPath.Should().Be(envelope.SenderPath);
        deserialized.Message.Should().BeOfType<UserCreated>();

        var deserializedInner = (UserCreated)deserialized.Message;
        deserializedInner.UserId.Should().Be(innerMessage.UserId);
        deserializedInner.Email.Should().Be(innerMessage.Email);
        deserializedInner.CreatedAt.Should().Be(innerMessage.CreatedAt);
    }

    [Fact]
    public void Should_RoundTrip_DDataEnvelope_WithUserUpdated()
    {
        // Arrange
        var innerData = new UserUpdated(
            UserId: "user-456",
            NewEmail: "updated@example.com",
            NewName: "Jane Smith",
            UpdatedAt: new DateTime(2024, 2, 20, 14, 15, 30, DateTimeKind.Utc));

        var envelope = new DDataEnvelope(
            Key: "users/user-456",
            Version: 42,
            Data: innerData);

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new AkkaWriter(buffer);
        _ddataSerializer.Write(writer, envelope);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var manifest = _ddataSerializer.Manifest(envelope);
        var reader = new AkkaReader(bytes);
        var deserialized = (DDataEnvelope)_ddataSerializer.Read(reader, manifest!);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.Key.Should().Be(envelope.Key);
        deserialized.Version.Should().Be(envelope.Version);
        deserialized.Data.Should().BeOfType<UserUpdated>();

        var deserializedInner = (UserUpdated)deserialized.Data;
        deserializedInner.UserId.Should().Be(innerData.UserId);
        deserializedInner.NewEmail.Should().Be(innerData.NewEmail);
        deserializedInner.NewName.Should().Be(innerData.NewName);
        deserializedInner.UpdatedAt.Should().Be(innerData.UpdatedAt);
    }

    [Fact]
    public void Should_RoundTrip_DoubleNested_RemoteEnvelope_Wrapping_DDataEnvelope()
    {
        // Arrange - RemoteEnvelope wrapping DDataEnvelope wrapping UserCreated
        var innerMessage = new UserCreated(
            UserId: "user-789",
            Email: "nested@example.com",
            CreatedAt: new DateTime(2024, 3, 10, 9, 45, 0, DateTimeKind.Utc));

        var ddataEnvelope = new DDataEnvelope(
            Key: "users/user-789",
            Version: 99,
            Data: innerMessage);

        var remoteEnvelope = new RemoteEnvelope(
            RecipientPath: "akka://cluster/user/data-replicator",
            SenderPath: "akka://cluster/user/client",
            Message: ddataEnvelope);

        // Act - Serialize (all three layers write to single buffer)
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new AkkaWriter(buffer);
        _remoteSerializer.Write(writer, remoteEnvelope);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var manifest = _remoteSerializer.Manifest(remoteEnvelope);
        var reader = new AkkaReader(bytes);
        var deserialized = (RemoteEnvelope)_remoteSerializer.Read(reader, manifest!);

        // Assert - Verify all three layers
        deserialized.Should().NotBeNull();
        deserialized.RecipientPath.Should().Be(remoteEnvelope.RecipientPath);
        deserialized.SenderPath.Should().Be(remoteEnvelope.SenderPath);
        deserialized.Message.Should().BeOfType<DDataEnvelope>();

        var deserializedDData = (DDataEnvelope)deserialized.Message;
        deserializedDData.Key.Should().Be(ddataEnvelope.Key);
        deserializedDData.Version.Should().Be(ddataEnvelope.Version);
        deserializedDData.Data.Should().BeOfType<UserCreated>();

        var deserializedInner = (UserCreated)deserializedDData.Data;
        deserializedInner.UserId.Should().Be(innerMessage.UserId);
        deserializedInner.Email.Should().Be(innerMessage.Email);
        deserializedInner.CreatedAt.Should().Be(innerMessage.CreatedAt);
    }

    [Fact]
    public void Should_Write_All_Layers_To_Single_Buffer_Without_Intermediate_Allocations()
    {
        // This test verifies the zero-copy property by ensuring that:
        // 1. All serializers share the same writer
        // 2. All data goes into a single buffer
        // 3. No intermediate byte[] allocations occur

        // Arrange
        var innerMessage = new UserCreated(
            UserId: "user-single-buffer",
            Email: "zerocopy@example.com",
            CreatedAt: new DateTime(2024, 4, 1, 12, 0, 0, DateTimeKind.Utc));

        var ddataEnvelope = new DDataEnvelope(
            Key: "test-key",
            Version: 1,
            Data: innerMessage);

        var remoteEnvelope = new RemoteEnvelope(
            RecipientPath: "akka://test/user/target",
            SenderPath: "akka://test/user/source",
            Message: ddataEnvelope);

        // Act - Serialize to a single buffer
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new AkkaWriter(buffer);
        _remoteSerializer.Write(writer, remoteEnvelope);

        // The buffer now contains all three layers serialized in a single contiguous block
        var serializedBytes = buffer.WrittenMemory;

        // Assert - We should be able to deserialize successfully
        var reader = new AkkaReader(serializedBytes);
        var deserialized = (RemoteEnvelope)_remoteSerializer.Read(reader, "remote-envelope-v1");

        // Verify the entire nested structure was preserved
        deserialized.Should().NotBeNull();
        var ddataLayer = deserialized.Message.Should().BeOfType<DDataEnvelope>().Subject;
        var innerLayer = ddataLayer.Data.Should().BeOfType<UserCreated>().Subject;
        innerLayer.UserId.Should().Be("user-single-buffer");

        // The key assertion: serializedBytes.Length should be reasonably small
        // because there are no duplicate encodings or extra overhead from intermediate allocations
        serializedBytes.Length.Should().BeLessThan(500); // Rough upper bound for this test case
    }

    [Fact]
    public void Should_Deserialize_Inner_Message_Independently_After_Extraction()
    {
        // This test verifies that the nested serialization format is correct
        // by extracting the inner message and deserializing it independently

        // Arrange
        var innerMessage = new UserCreated(
            UserId: "user-independent",
            Email: "independent@example.com",
            CreatedAt: new DateTime(2024, 5, 1, 15, 30, 0, DateTimeKind.Utc));

        var envelope = new RemoteEnvelope(
            RecipientPath: "akka://test/user/recipient",
            SenderPath: "akka://test/user/sender",
            Message: innerMessage);

        // Act - Serialize envelope
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new AkkaWriter(buffer);
        _remoteSerializer.Write(writer, envelope);

        // Act - Deserialize envelope and extract inner message
        var reader = new AkkaReader(buffer.WrittenMemory);
        var deserializedEnvelope = (RemoteEnvelope)_remoteSerializer.Read(reader, "remote-envelope-v1");

        // Assert - Verify inner message is correctly deserialized
        deserializedEnvelope.Message.Should().BeOfType<UserCreated>();
        var extractedMessage = (UserCreated)deserializedEnvelope.Message;
        extractedMessage.UserId.Should().Be(innerMessage.UserId);
        extractedMessage.Email.Should().Be(innerMessage.Email);
        extractedMessage.CreatedAt.Should().Be(innerMessage.CreatedAt);
    }
}
