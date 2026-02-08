using System.Buffers;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2.Tests.Messages;
using FluentAssertions;
using Xunit;

namespace Akka.Serialization.V2.Tests.Generated;

/// <summary>
/// Tests for the reference generated serializer implementation.
/// These tests verify:
/// 1. Round-trip serialization correctness
/// 2. Byte-for-byte identity with hand-written serializers
/// 3. Version tolerance (forward/backward compatibility)
///
/// The byte-identity tests are critical: they prove that the generated serializer
/// produces EXACTLY the same binary output as the hand-written serializers,
/// ensuring no behavior regression when we switch to source generation.
/// </summary>
public class ReferenceGeneratedTests
{
    private readonly AnnotatedMessageSerializer _generatedSerializer = new();
    private readonly UserMessageSerializer _handWrittenUserSerializer = new();
    private readonly OrderMessageSerializer _handWrittenOrderSerializer = new();
    private readonly MessagePackCodecProvider _codec = MessagePackCodecProvider.Instance;

    // =====================================================================
    // Round-trip tests
    // =====================================================================

    [Fact]
    public void Generated_UserCreated_RoundTrip()
    {
        // Arrange
        var original = new UserCreatedAnnotated(
            UserId: "user-123",
            Email: "test@example.com",
            CreatedAt: new DateTime(2024, 1, 15, 10, 30, 45, DateTimeKind.Utc));

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        _generatedSerializer.Write(writer, original);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var manifest = _generatedSerializer.Manifest(original);
        var reader = _codec.CreateReader(bytes);
        var deserialized = (UserCreatedAnnotated)_generatedSerializer.Read(reader, manifest!);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be(original.UserId);
        deserialized.Email.Should().Be(original.Email);
        deserialized.CreatedAt.Should().Be(original.CreatedAt);
    }

    [Fact]
    public void Generated_UserUpdated_RoundTrip_AllFields()
    {
        // Arrange
        var original = new UserUpdatedAnnotated(
            UserId: "user-456",
            NewEmail: "updated@example.com",
            NewName: "Jane Smith",
            UpdatedAt: new DateTime(2024, 2, 20, 14, 15, 30, DateTimeKind.Utc));

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        _generatedSerializer.Write(writer, original);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var manifest = _generatedSerializer.Manifest(original);
        var reader = _codec.CreateReader(bytes);
        var deserialized = (UserUpdatedAnnotated)_generatedSerializer.Read(reader, manifest!);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be(original.UserId);
        deserialized.NewEmail.Should().Be(original.NewEmail);
        deserialized.NewName.Should().Be(original.NewName);
        deserialized.UpdatedAt.Should().Be(original.UpdatedAt);
    }

    [Fact]
    public void Generated_UserUpdated_RoundTrip_WithNulls()
    {
        // Arrange
        var original = new UserUpdatedAnnotated(
            UserId: "user-789",
            NewEmail: null,
            NewName: null,
            UpdatedAt: new DateTime(2024, 3, 10, 9, 45, 0, DateTimeKind.Utc));

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        _generatedSerializer.Write(writer, original);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var manifest = _generatedSerializer.Manifest(original);
        var reader = _codec.CreateReader(bytes);
        var deserialized = (UserUpdatedAnnotated)_generatedSerializer.Read(reader, manifest!);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be(original.UserId);
        deserialized.NewEmail.Should().BeNull();
        deserialized.NewName.Should().BeNull();
        deserialized.UpdatedAt.Should().Be(original.UpdatedAt);
    }

    [Fact]
    public void Generated_OrderPlaced_RoundTrip()
    {
        // Arrange
        var orderId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        var placedAt = new DateTimeOffset(2024, 4, 5, 16, 20, 0, TimeSpan.FromHours(-5));

        var original = new OrderPlacedAnnotated(
            OrderId: orderId,
            CustomerId: "customer-001",
            Amount: 149.99m,
            PlacedAt: placedAt);

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        _generatedSerializer.Write(writer, original);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var manifest = _generatedSerializer.Manifest(original);
        var reader = _codec.CreateReader(bytes);
        var deserialized = (OrderPlacedAnnotated)_generatedSerializer.Read(reader, manifest!);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.OrderId.Should().Be(original.OrderId);
        deserialized.CustomerId.Should().Be(original.CustomerId);
        deserialized.Amount.Should().Be(original.Amount);
        deserialized.PlacedAt.Should().Be(original.PlacedAt);
        deserialized.PlacedAt.Offset.Should().Be(original.PlacedAt.Offset);
    }

    // =====================================================================
    // Byte-identity tests
    // These are CRITICAL: they prove the generated serializer produces
    // exactly the same binary output as the hand-written serializers
    // =====================================================================

    [Fact]
    public void Generated_UserCreated_ByteIdentical_ToHandWritten()
    {
        // Arrange - Same data in both message types
        var createdAt = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var handWrittenMsg = new UserCreated("user-123", "test@example.com", createdAt);
        var generatedMsg = new UserCreatedAnnotated("user-123", "test@example.com", createdAt);

        // Act - Serialize with hand-written serializer
        var handWrittenBuffer = new ArrayBufferWriter<byte>();
        var handWriter = _codec.CreateWriter(handWrittenBuffer);
        _handWrittenUserSerializer.Write(handWriter, handWrittenMsg);

        // Act - Serialize with generated serializer
        var generatedBuffer = new ArrayBufferWriter<byte>();
        var genWriter = _codec.CreateWriter(generatedBuffer);
        _generatedSerializer.Write(genWriter, generatedMsg);

        // Assert - Byte arrays must be identical
        var handWrittenBytes = handWrittenBuffer.WrittenSpan.ToArray();
        var generatedBytes = generatedBuffer.WrittenSpan.ToArray();

        generatedBytes.Should().Equal(handWrittenBytes,
            "Generated serializer must produce byte-for-byte identical output to hand-written serializer");
    }

    [Fact]
    public void Generated_UserUpdated_ByteIdentical_ToHandWritten_AllFields()
    {
        // Arrange - Same data in both message types
        var updatedAt = new DateTime(2024, 2, 20, 14, 15, 30, DateTimeKind.Utc);
        var handWrittenMsg = new UserUpdated("user-456", "updated@example.com", "Jane Smith", updatedAt);
        var generatedMsg = new UserUpdatedAnnotated("user-456", "updated@example.com", "Jane Smith", updatedAt);

        // Act - Serialize with hand-written serializer
        var handWrittenBuffer = new ArrayBufferWriter<byte>();
        var handWriter = _codec.CreateWriter(handWrittenBuffer);
        _handWrittenUserSerializer.Write(handWriter, handWrittenMsg);

        // Act - Serialize with generated serializer
        var generatedBuffer = new ArrayBufferWriter<byte>();
        var genWriter = _codec.CreateWriter(generatedBuffer);
        _generatedSerializer.Write(genWriter, generatedMsg);

        // Assert - Byte arrays must be identical
        var handWrittenBytes = handWrittenBuffer.WrittenSpan.ToArray();
        var generatedBytes = generatedBuffer.WrittenSpan.ToArray();

        generatedBytes.Should().Equal(handWrittenBytes,
            "Generated serializer must produce byte-for-byte identical output to hand-written serializer");
    }

    [Fact]
    public void Generated_UserUpdated_ByteIdentical_ToHandWritten_WithNulls()
    {
        // Arrange - Same data in both message types, with null fields
        var updatedAt = new DateTime(2024, 3, 10, 9, 45, 0, DateTimeKind.Utc);
        var handWrittenMsg = new UserUpdated("user-789", null, null, updatedAt);
        var generatedMsg = new UserUpdatedAnnotated("user-789", null, null, updatedAt);

        // Act - Serialize with hand-written serializer
        var handWrittenBuffer = new ArrayBufferWriter<byte>();
        var handWriter = _codec.CreateWriter(handWrittenBuffer);
        _handWrittenUserSerializer.Write(handWriter, handWrittenMsg);

        // Act - Serialize with generated serializer
        var generatedBuffer = new ArrayBufferWriter<byte>();
        var genWriter = _codec.CreateWriter(generatedBuffer);
        _generatedSerializer.Write(genWriter, generatedMsg);

        // Assert - Byte arrays must be identical
        var handWrittenBytes = handWrittenBuffer.WrittenSpan.ToArray();
        var generatedBytes = generatedBuffer.WrittenSpan.ToArray();

        generatedBytes.Should().Equal(handWrittenBytes,
            "Generated serializer must produce byte-for-byte identical output to hand-written serializer");
    }

    [Fact]
    public void Generated_OrderPlaced_ByteIdentical_ToHandWritten()
    {
        // Arrange - Same data in both message types
        var orderId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        var placedAt = new DateTimeOffset(2024, 4, 5, 16, 20, 0, TimeSpan.FromHours(-5));
        var handWrittenMsg = new OrderPlaced(orderId, "customer-001", 149.99m, placedAt);
        var generatedMsg = new OrderPlacedAnnotated(orderId, "customer-001", 149.99m, placedAt);

        // Act - Serialize with hand-written serializer
        var handWrittenBuffer = new ArrayBufferWriter<byte>();
        var handWriter = _codec.CreateWriter(handWrittenBuffer);
        _handWrittenOrderSerializer.Write(handWriter, handWrittenMsg);

        // Act - Serialize with generated serializer
        var generatedBuffer = new ArrayBufferWriter<byte>();
        var genWriter = _codec.CreateWriter(generatedBuffer);
        _generatedSerializer.Write(genWriter, generatedMsg);

        // Assert - Byte arrays must be identical
        var handWrittenBytes = handWrittenBuffer.WrittenSpan.ToArray();
        var generatedBytes = generatedBuffer.WrittenSpan.ToArray();

        generatedBytes.Should().Equal(handWrittenBytes,
            "Generated serializer must produce byte-for-byte identical output to hand-written serializer");
    }

    // =====================================================================
    // Version tolerance test
    // =====================================================================

    [Fact]
    public void Generated_BackwardCompat_DeserializesOlderDataWithFewerFields()
    {
        // Simulate V1 data that only had 2 fields (UserId, Email)
        // being deserialized by V2 reader expecting 3 fields (UserId, Email, CreatedAt)
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        writer.BeginObject(2); // Only 2 fields instead of 3
        writer.WriteString("user-old");
        writer.WriteString("old@example.com");

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize with current deserializer (expects 3 fields)
        var reader = _codec.CreateReader(bytes);
        var deserialized = (UserCreatedAnnotated)_generatedSerializer.Read(reader, "user-created-v1");

        // Assert - Should read available fields and use defaults for missing ones
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be("user-old");
        deserialized.Email.Should().Be("old@example.com");
        deserialized.CreatedAt.Should().Be(default(DateTime)); // Missing field defaults to default
    }

    [Fact]
    public void Generated_VersionTolerance_SkipsUnknownFields()
    {
        // This test simulates a V2 serializer (with 4 fields) serializing data
        // that is then read by a V1 deserializer (expecting only 3 fields).
        // The V1 deserializer should skip the extra field.

        // Arrange - Manually create a UserCreatedAnnotated message with 4 fields (simulating V2)
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        // Write a UserCreatedAnnotated-like message with an extra field
        writer.BeginObject(4); // 4 fields instead of 3
        writer.WriteString("user-v2");
        writer.WriteString("v2@example.com");
        writer.WriteDateTime(new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        writer.WriteString("extra-field-data"); // Extra field added in V2

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize with V1 deserializer (expects 3 fields)
        var reader = _codec.CreateReader(bytes);
        var deserialized = (UserCreatedAnnotated)_generatedSerializer.Read(reader, "user-created-v1");

        // Assert - Should successfully read the first 3 fields and skip the 4th
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be("user-v2");
        deserialized.Email.Should().Be("v2@example.com");
        deserialized.CreatedAt.Should().Be(new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc));
    }
}
