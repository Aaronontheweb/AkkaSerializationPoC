using System.Buffers;
using System.Collections.Immutable;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2;
using FluentAssertions;
using Xunit;

namespace Akka.Serialization.V2.Tests.Generated;

// =====================================================================
// Edge case protocol + serializer
// =====================================================================

public interface IEdgeCaseProtocol { }

[AkkaSerializer(Name = "edge-case-protocol")]
public partial class EdgeCaseSerializer : SerializerV2<IEdgeCaseProtocol> { }

// =====================================================================
// 2a. Nested Collections
// =====================================================================

[AkkaSerializable(Manifest = "nested-collections-v1")]
public sealed record NestedCollections(
    [property: AkkaField(0)] List<List<string>> StringGrid,
    [property: AkkaField(1)] Dictionary<string, List<int>> TaggedScores
) : IEdgeCaseProtocol;

// =====================================================================
// 2b. Nullable Value Types + Nullable Enums
// =====================================================================

[AkkaSerializable(Manifest = "nullable-values-v1")]
public sealed record NullableValues(
    [property: AkkaField(0)] int? OptionalCount,
    [property: AkkaField(1)] DateTime? OptionalDate,
    [property: AkkaField(2)] decimal? OptionalAmount,
    [property: AkkaField(3)] OrderStatus? OptionalStatus
) : IEdgeCaseProtocol;

// =====================================================================
// 2c. Collections of Enums
// =====================================================================

[AkkaSerializable(Manifest = "enum-collections-v1")]
public sealed record EnumCollections(
    [property: AkkaField(0)] List<OrderStatus> StatusHistory,
    [property: AkkaField(1)] HashSet<Country> AllowedCountries,
    [property: AkkaField(2)] ImmutableArray<Country> CountryRanking
) : IEdgeCaseProtocol;

// =====================================================================
// 2d. Record Struct Value Objects
// =====================================================================

public record struct Coordinates(
    [property: AkkaField(0)] double Latitude,
    [property: AkkaField(1)] double Longitude);

[AkkaSerializable(Manifest = "location-event-v1")]
public sealed record LocationEvent(
    [property: AkkaField(0)] string DeviceId,
    [property: AkkaField(1)] Coordinates Position,
    [property: AkkaField(2)] DateTime Timestamp
) : IEdgeCaseProtocol;

// =====================================================================
// 2e. Nullable Struct Value Object
// =====================================================================

[AkkaSerializable(Manifest = "optional-location-v1")]
public sealed record OptionalLocation(
    [property: AkkaField(0)] string DeviceId,
    [property: AkkaField(1)] Coordinates? LastKnown
) : IEdgeCaseProtocol;

// =====================================================================
// 2f. Default/Empty Values
// =====================================================================

[AkkaSerializable(Manifest = "default-values-v1")]
public sealed record DefaultValues(
    [property: AkkaField(0)] int Count,
    [property: AkkaField(1)] string Name,
    [property: AkkaField(2)] Money Amount,
    [property: AkkaField(3)] bool IsActive
) : IEdgeCaseProtocol;

// =====================================================================
// Tests
// =====================================================================

public class EdgeCaseTests
{
    private readonly EdgeCaseSerializer _serializer = new();
    private readonly MessagePackCodecProvider _codec = MessagePackCodecProvider.Instance;

    private T RoundTrip<T>(T obj, string manifest) where T : class
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        _serializer.Write(writer, obj);

        var reader = _codec.CreateReader(buffer.WrittenMemory);
        return (T)_serializer.Read(reader, manifest);
    }

    // =====================================================================
    // 2a. Nested Collections
    // =====================================================================

    [Fact]
    public void NestedList_RoundTrip()
    {
        var original = new NestedCollections(
            new List<List<string>>
            {
                new() { "a", "b", "c" },
                new() { "d", "e" }
            },
            new Dictionary<string, List<int>>
            {
                ["scores"] = new List<int> { 100, 200 }
            });

        var deserialized = RoundTrip(original, "nested-collections-v1");

        deserialized.StringGrid.Should().HaveCount(2);
        deserialized.StringGrid[0].Should().BeEquivalentTo(new[] { "a", "b", "c" });
        deserialized.StringGrid[1].Should().BeEquivalentTo(new[] { "d", "e" });
    }

    [Fact]
    public void NestedList_EmptyInner_RoundTrip()
    {
        var original = new NestedCollections(
            new List<List<string>>
            {
                new(),
                new() { "x" },
                new()
            },
            new Dictionary<string, List<int>>());

        var deserialized = RoundTrip(original, "nested-collections-v1");

        deserialized.StringGrid.Should().HaveCount(3);
        deserialized.StringGrid[0].Should().BeEmpty();
        deserialized.StringGrid[1].Should().ContainSingle().Which.Should().Be("x");
        deserialized.StringGrid[2].Should().BeEmpty();
    }

    [Fact]
    public void DictOfList_RoundTrip()
    {
        var original = new NestedCollections(
            new List<List<string>>(),
            new Dictionary<string, List<int>>
            {
                ["math"] = new List<int> { 95, 87, 92 },
                ["science"] = new List<int> { 88, 91 }
            });

        var deserialized = RoundTrip(original, "nested-collections-v1");

        deserialized.TaggedScores.Should().HaveCount(2);
        deserialized.TaggedScores["math"].Should().BeEquivalentTo(new[] { 95, 87, 92 });
        deserialized.TaggedScores["science"].Should().BeEquivalentTo(new[] { 88, 91 });
    }

    // =====================================================================
    // 2b. Nullable Value Types
    // =====================================================================

    [Fact]
    public void NullableInt_WithValue_RoundTrip()
    {
        var original = new NullableValues(42, null, null, null);
        var deserialized = RoundTrip(original, "nullable-values-v1");
        deserialized.OptionalCount.Should().Be(42);
    }

    [Fact]
    public void NullableInt_Null_RoundTrip()
    {
        var original = new NullableValues(null, null, null, null);
        var deserialized = RoundTrip(original, "nullable-values-v1");
        deserialized.OptionalCount.Should().BeNull();
    }

    [Fact]
    public void NullableDateTime_WithValue_RoundTrip()
    {
        var dt = new DateTime(2024, 12, 25, 10, 0, 0, DateTimeKind.Utc);
        var original = new NullableValues(null, dt, null, null);
        var deserialized = RoundTrip(original, "nullable-values-v1");
        deserialized.OptionalDate.Should().Be(dt);
    }

    [Fact]
    public void NullableDateTime_Null_RoundTrip()
    {
        var original = new NullableValues(null, null, null, null);
        var deserialized = RoundTrip(original, "nullable-values-v1");
        deserialized.OptionalDate.Should().BeNull();
    }

    [Fact]
    public void NullableDecimal_WithValue_RoundTrip()
    {
        var original = new NullableValues(null, null, 99.95m, null);
        var deserialized = RoundTrip(original, "nullable-values-v1");
        deserialized.OptionalAmount.Should().Be(99.95m);
    }

    [Fact]
    public void NullableEnum_WithValue_RoundTrip()
    {
        var original = new NullableValues(null, null, null, OrderStatus.Shipped);
        var deserialized = RoundTrip(original, "nullable-values-v1");
        deserialized.OptionalStatus.Should().Be(OrderStatus.Shipped);
    }

    [Fact]
    public void NullableEnum_Null_RoundTrip()
    {
        var original = new NullableValues(null, null, null, null);
        var deserialized = RoundTrip(original, "nullable-values-v1");
        deserialized.OptionalStatus.Should().BeNull();
    }

    // =====================================================================
    // 2c. Collections of Enums
    // =====================================================================

    [Fact]
    public void ListOfEnums_RoundTrip()
    {
        var original = new EnumCollections(
            new List<OrderStatus> { OrderStatus.Pending, OrderStatus.Confirmed, OrderStatus.Shipped },
            new HashSet<Country>(),
            ImmutableArray<Country>.Empty);

        var deserialized = RoundTrip(original, "enum-collections-v1");

        deserialized.StatusHistory.Should().HaveCount(3);
        deserialized.StatusHistory[0].Should().Be(OrderStatus.Pending);
        deserialized.StatusHistory[1].Should().Be(OrderStatus.Confirmed);
        deserialized.StatusHistory[2].Should().Be(OrderStatus.Shipped);
    }

    [Fact]
    public void HashSetOfEnums_RoundTrip()
    {
        var original = new EnumCollections(
            new List<OrderStatus>(),
            new HashSet<Country> { Country.US, Country.UK, Country.DE },
            ImmutableArray<Country>.Empty);

        var deserialized = RoundTrip(original, "enum-collections-v1");

        deserialized.AllowedCountries.Should().HaveCount(3);
        deserialized.AllowedCountries.Should().Contain(Country.US);
        deserialized.AllowedCountries.Should().Contain(Country.UK);
        deserialized.AllowedCountries.Should().Contain(Country.DE);
    }

    [Fact]
    public void ImmutableArrayOfEnums_RoundTrip()
    {
        var original = new EnumCollections(
            new List<OrderStatus>(),
            new HashSet<Country>(),
            ImmutableArray.Create(Country.JP, Country.FR, Country.CA));

        var deserialized = RoundTrip(original, "enum-collections-v1");

        deserialized.CountryRanking.Should().HaveCount(3);
        deserialized.CountryRanking[0].Should().Be(Country.JP);
        deserialized.CountryRanking[1].Should().Be(Country.FR);
        deserialized.CountryRanking[2].Should().Be(Country.CA);
    }

    // =====================================================================
    // 2d. Record Struct Value Objects
    // =====================================================================

    [Fact]
    public void RecordStruct_ValueObject_RoundTrip()
    {
        var original = new LocationEvent(
            "device-001",
            new Coordinates(47.6062, -122.3321),
            new DateTime(2024, 8, 15, 14, 30, 0, DateTimeKind.Utc));

        var deserialized = RoundTrip(original, "location-event-v1");

        deserialized.DeviceId.Should().Be("device-001");
        deserialized.Position.Latitude.Should().Be(47.6062);
        deserialized.Position.Longitude.Should().Be(-122.3321);
        deserialized.Timestamp.Should().Be(new DateTime(2024, 8, 15, 14, 30, 0, DateTimeKind.Utc));
    }

    // =====================================================================
    // 2e. Nullable Struct Value Object
    // =====================================================================

    [Fact]
    public void NullableStructValueObject_WithValue()
    {
        var original = new OptionalLocation(
            "device-002",
            new Coordinates(40.7128, -74.0060));

        var deserialized = RoundTrip(original, "optional-location-v1");

        deserialized.DeviceId.Should().Be("device-002");
        deserialized.LastKnown.Should().NotBeNull();
        deserialized.LastKnown!.Value.Latitude.Should().Be(40.7128);
        deserialized.LastKnown.Value.Longitude.Should().Be(-74.0060);
    }

    [Fact]
    public void NullableStructValueObject_Null()
    {
        var original = new OptionalLocation("device-003", null);

        var deserialized = RoundTrip(original, "optional-location-v1");

        deserialized.DeviceId.Should().Be("device-003");
        deserialized.LastKnown.Should().BeNull();
    }

    // =====================================================================
    // 2f. Default/Empty Values
    // =====================================================================

    [Fact]
    public void DefaultValues_RoundTrip()
    {
        var original = new DefaultValues(0, "", new Money(0m, ""), false);

        var deserialized = RoundTrip(original, "default-values-v1");

        deserialized.Count.Should().Be(0);
        deserialized.Name.Should().BeEmpty();
        deserialized.Amount.Amount.Should().Be(0m);
        deserialized.Amount.Currency.Should().BeEmpty();
        deserialized.IsActive.Should().BeFalse();
    }

    // =====================================================================
    // Combined scenarios
    // =====================================================================

    [Fact]
    public void AllNullableValues_AllNull_RoundTrip()
    {
        var original = new NullableValues(null, null, null, null);

        var deserialized = RoundTrip(original, "nullable-values-v1");

        deserialized.OptionalCount.Should().BeNull();
        deserialized.OptionalDate.Should().BeNull();
        deserialized.OptionalAmount.Should().BeNull();
        deserialized.OptionalStatus.Should().BeNull();
    }

    [Fact]
    public void AllNullableValues_AllPopulated_RoundTrip()
    {
        var dt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var original = new NullableValues(100, dt, 55.55m, OrderStatus.Delivered);

        var deserialized = RoundTrip(original, "nullable-values-v1");

        deserialized.OptionalCount.Should().Be(100);
        deserialized.OptionalDate.Should().Be(dt);
        deserialized.OptionalAmount.Should().Be(55.55m);
        deserialized.OptionalStatus.Should().Be(OrderStatus.Delivered);
    }
}
