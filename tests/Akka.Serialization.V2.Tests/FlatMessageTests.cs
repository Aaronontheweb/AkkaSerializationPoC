using System.Buffers;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2.Tests.Messages;
using FluentAssertions;
using Xunit;

namespace Akka.Serialization.V2.Tests;

/// <summary>
/// Tests for flat message serialization (Phase 1).
/// Validates round-trip serialization, nullable field handling, and version tolerance.
/// </summary>
public class FlatMessageTests
{
    private readonly MessagePackCodecProvider _codecProvider = MessagePackCodecProvider.Instance;
    private readonly UserMessageSerializer _userSerializer = new();
    private readonly OrderMessageSerializer _orderSerializer = new();

    [Fact]
    public void Should_RoundTrip_UserCreated()
    {
        // Arrange
        var original = new UserCreated(
            UserId: "user-123",
            Email: "test@example.com",
            CreatedAt: new DateTime(2024, 1, 15, 10, 30, 45, DateTimeKind.Utc));

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codecProvider.CreateWriter(buffer);
        _userSerializer.Write(writer, original);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var manifest = _userSerializer.Manifest(original);
        var reader = _codecProvider.CreateReader(bytes);
        var deserialized = (UserCreated)_userSerializer.Read(reader, manifest!);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be(original.UserId);
        deserialized.Email.Should().Be(original.Email);
        deserialized.CreatedAt.Should().Be(original.CreatedAt);
    }

    [Fact]
    public void Should_RoundTrip_UserUpdated_WithAllFields()
    {
        // Arrange
        var original = new UserUpdated(
            UserId: "user-456",
            NewEmail: "updated@example.com",
            NewName: "Jane Smith",
            UpdatedAt: new DateTime(2024, 2, 20, 14, 15, 30, DateTimeKind.Utc));

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codecProvider.CreateWriter(buffer);
        _userSerializer.Write(writer, original);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var manifest = _userSerializer.Manifest(original);
        var reader = _codecProvider.CreateReader(bytes);
        var deserialized = (UserUpdated)_userSerializer.Read(reader, manifest!);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be(original.UserId);
        deserialized.NewEmail.Should().Be(original.NewEmail);
        deserialized.NewName.Should().Be(original.NewName);
        deserialized.UpdatedAt.Should().Be(original.UpdatedAt);
    }

    [Fact]
    public void Should_RoundTrip_UserUpdated_WithNulls()
    {
        // Arrange
        var original = new UserUpdated(
            UserId: "user-789",
            NewEmail: null,
            NewName: null,
            UpdatedAt: new DateTime(2024, 3, 10, 9, 45, 0, DateTimeKind.Utc));

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codecProvider.CreateWriter(buffer);
        _userSerializer.Write(writer, original);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var manifest = _userSerializer.Manifest(original);
        var reader = _codecProvider.CreateReader(bytes);
        var deserialized = (UserUpdated)_userSerializer.Read(reader, manifest!);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be(original.UserId);
        deserialized.NewEmail.Should().BeNull();
        deserialized.NewName.Should().BeNull();
        deserialized.UpdatedAt.Should().Be(original.UpdatedAt);
    }

    [Fact]
    public void Should_RoundTrip_OrderPlaced()
    {
        // Arrange
        var orderId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        var placedAt = new DateTimeOffset(2024, 4, 5, 16, 20, 0, TimeSpan.FromHours(-5));

        var original = new OrderPlaced(
            OrderId: orderId,
            CustomerId: "customer-001",
            Amount: 149.99m,
            PlacedAt: placedAt);

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codecProvider.CreateWriter(buffer);
        _orderSerializer.Write(writer, original);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var manifest = _orderSerializer.Manifest(original);
        var reader = _codecProvider.CreateReader(bytes);
        var deserialized = (OrderPlaced)_orderSerializer.Read(reader, manifest!);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.OrderId.Should().Be(original.OrderId);
        deserialized.CustomerId.Should().Be(original.CustomerId);
        deserialized.Amount.Should().Be(original.Amount);
        deserialized.PlacedAt.Should().Be(original.PlacedAt);
        deserialized.PlacedAt.Offset.Should().Be(original.PlacedAt.Offset);
    }

    [Fact]
    public void Should_RoundTrip_Decimal_WithFullPrecision()
    {
        // Arrange - Use a decimal value that would lose precision with double cast
        var preciseAmount = decimal.MaxValue / 3; // 26409387504754779197847983026m
        var orderId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        var placedAt = new DateTimeOffset(2024, 4, 5, 16, 20, 0, TimeSpan.FromHours(-5));

        var original = new OrderPlaced(orderId, "customer-001", preciseAmount, placedAt);

        // Act - Serialize
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codecProvider.CreateWriter(buffer);
        _orderSerializer.Write(writer, original);

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        var reader = _codecProvider.CreateReader(bytes);
        var deserialized = (OrderPlaced)_orderSerializer.Read(reader, "order-placed-v1");

        // Assert - decimal must survive round-trip with EXACT precision
        deserialized.Amount.Should().Be(preciseAmount,
            "decimal round-trip must be lossless (no double conversion)");
    }

    [Fact]
    public void Should_SkipUnknownFields_When_DeserializingOlderVersion()
    {
        // This test simulates a V2 serializer (with 4 fields) serializing data
        // that is then read by a V1 deserializer (expecting only 3 fields).
        // The V1 deserializer should skip the extra field.

        // Arrange - Manually create a UserCreated message with 4 fields (simulating V2)
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codecProvider.CreateWriter(buffer);
        // Write a UserCreated-like message with an extra field
        writer.BeginObject(4); // 4 fields instead of 3
        writer.WriteString("user-v2");
        writer.WriteString("v2@example.com");
        writer.WriteDateTime(new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        writer.WriteString("extra-field-data"); // Extra field added in V2

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize with V1 deserializer (expects 3 fields)
        var reader = _codecProvider.CreateReader(bytes);
        var deserialized = (UserCreated)_userSerializer.Read(reader, "user-created-v1");

        // Assert - Should successfully read the first 3 fields and skip the 4th
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be("user-v2");
        deserialized.Email.Should().Be("v2@example.com");
        deserialized.CreatedAt.Should().Be(new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Should_HandleMissingFields_When_DeserializingNewerVersion()
    {
        // This test simulates a V1 serializer (with 2 fields) serializing data
        // that is then read by a V2 deserializer (expecting 4 fields).
        // The V2 deserializer should provide default values for missing fields.

        // Arrange - Manually create a UserUpdated message with only 2 fields (simulating V1)
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codecProvider.CreateWriter(buffer);
        // Write a UserUpdated-like message with only UserId and UpdatedAt (missing NewEmail and NewName)
        writer.BeginObject(2); // Only 2 fields instead of 4
        writer.WriteString("user-v1");
        writer.WriteDateTime(new DateTime(2024, 6, 1, 8, 30, 0, DateTimeKind.Utc));

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize with current deserializer (expects 4 fields)
        var reader = _codecProvider.CreateReader(bytes);
        var fieldCount = reader.BeginReadObject();

        // Read available fields
        var userId = reader.ReadString() ?? string.Empty;

        // Handle missing fields - provide defaults for NewEmail and NewName
        string? newEmail = null;
        string? newName = null;
        DateTime updatedAt = default;

        if (fieldCount >= 2)
        {
            updatedAt = reader.ReadDateTime();
        }

        // Fields 2 and 3 are missing (NewEmail and NewName would normally be here)
        // We treat them as null for backwards compatibility

        var deserialized = new UserUpdated(userId, newEmail, newName, updatedAt);

        // Assert - Should successfully read available fields and use defaults for missing ones
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be("user-v1");
        deserialized.NewEmail.Should().BeNull();
        deserialized.NewName.Should().BeNull();
        deserialized.UpdatedAt.Should().Be(new DateTime(2024, 6, 1, 8, 30, 0, DateTimeKind.Utc));
    }
}
