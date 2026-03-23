using System.Buffers;
using System.Collections.Immutable;
using Akka.Serialization.V2;
using FluentAssertions;
using Xunit;

namespace Akka.Serialization.V2.Tests.Generated;

// =====================================================================
// Enums
// =====================================================================

public enum Country { US, CA, UK, DE, FR, JP }
public enum OrderStatus { Pending, Confirmed, Shipped, Delivered, Cancelled }

// =====================================================================
// Value objects — no [AkkaSerializable], just [AkkaField]
// These are embeddable as fields but are NOT standalone messages.
// =====================================================================

public sealed record Address(
    [property: AkkaField(0)] string Street,
    [property: AkkaField(1)] string City,
    [property: AkkaField(2)] string State,
    [property: AkkaField(3)] string ZipCode,
    [property: AkkaField(4)] Country Country);

public sealed record Money(
    [property: AkkaField(0)] decimal Amount,
    [property: AkkaField(1)] string Currency);

public sealed record LineItem(
    [property: AkkaField(0)] string ProductId,
    [property: AkkaField(1)] string ProductName,
    [property: AkkaField(2)] int Quantity,
    [property: AkkaField(3)] Money UnitPrice);

public sealed record ContactInfo(
    [property: AkkaField(0)] string Email,
    [property: AkkaField(1)] string? Phone);

// =====================================================================
// Protocol interface
// =====================================================================

public interface IComplexProtocol { }

// =====================================================================
// Protocol messages
// =====================================================================

[AkkaSerializable(Manifest = "order-submitted-v1")]
public sealed record OrderSubmitted(
    [property: AkkaField(0)] string OrderId,
    [property: AkkaField(1)] DateTime SubmittedAt,
    [property: AkkaField(2)] Address ShippingAddress,
    [property: AkkaField(3)] Address? BillingAddress,
    [property: AkkaField(4)] List<LineItem> Items,
    [property: AkkaField(5)] Money Total,
    [property: AkkaField(6)] OrderStatus Status) : IComplexProtocol;

[AkkaSerializable(Manifest = "customer-created-v1")]
public sealed record CustomerCreated(
    [property: AkkaField(0)] string CustomerId,
    [property: AkkaField(1)] string Name,
    [property: AkkaField(2)] ContactInfo Contact,
    [property: AkkaField(3)] Address PrimaryAddress) : IComplexProtocol;

// [AkkaSerializable] type used as a nested field
[AkkaSerializable(Manifest = "order-with-customer-v1")]
public sealed record OrderWithCustomer(
    [property: AkkaField(0)] string OrderId,
    [property: AkkaField(1)] CustomerCreated Customer,
    [property: AkkaField(2)] List<LineItem> Items) : IComplexProtocol;

// All collection types exercised in a single message
[AkkaSerializable(Manifest = "catalog-snapshot-v1")]
public sealed record CatalogSnapshot(
    [property: AkkaField(0)] string CatalogId,
    [property: AkkaField(1)] IReadOnlyList<string> Categories,
    [property: AkkaField(2)] ImmutableList<LineItem> FeaturedItems,
    [property: AkkaField(3)] Dictionary<string, Money> PriceOverrides,
    [property: AkkaField(4)] HashSet<string> Tags,
    [property: AkkaField(5)] ImmutableArray<int> PopularItemRanks,
    [property: AkkaField(6)] ImmutableDictionary<string, string> Metadata) : IComplexProtocol;

[AkkaSerializer(Name = "complex-protocol", SerializerId = 8001)]
public partial class ComplexProtocolSerializer : SerializerV2<IComplexProtocol> { }

// =====================================================================
// Tests
// =====================================================================

public class NestedTypeTests
{
    private readonly ComplexProtocolSerializer _serializer = new();

    private T RoundTrip<T>(T obj, string manifest) where T : class
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new AkkaWriter(buffer);
        _serializer.Write(writer, obj);

        var reader = new AkkaReader(buffer.WrittenMemory);
        return (T)_serializer.Read(reader, manifest);
    }

    // =====================================================================
    // Enum round-trip
    // =====================================================================

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled)]
    public void Enum_RoundTrip(OrderStatus status)
    {
        var original = new OrderSubmitted(
            "ORD-001", new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            new Address("123 Main St", "Springfield", "IL", "62701", Country.US),
            null,
            new List<LineItem>
            {
                new("PROD-1", "Widget", 2, new Money(9.99m, "USD"))
            },
            new Money(19.98m, "USD"),
            status);

        var deserialized = RoundTrip(original, "order-submitted-v1");

        deserialized.Status.Should().Be(status);
    }

    // =====================================================================
    // Nested value object round-trip
    // =====================================================================

    [Fact]
    public void NestedValueObject_RoundTrip()
    {
        var original = new OrderSubmitted(
            "ORD-002", new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            new Address("456 Oak Ave", "Portland", "OR", "97201", Country.US),
            new Address("789 Pine St", "Seattle", "WA", "98101", Country.US),
            new List<LineItem>
            {
                new("PROD-1", "Widget", 1, new Money(25.50m, "USD"))
            },
            new Money(25.50m, "USD"),
            OrderStatus.Confirmed);

        var deserialized = RoundTrip(original, "order-submitted-v1");

        deserialized.OrderId.Should().Be("ORD-002");
        deserialized.ShippingAddress.Street.Should().Be("456 Oak Ave");
        deserialized.ShippingAddress.City.Should().Be("Portland");
        deserialized.ShippingAddress.Country.Should().Be(Country.US);
        deserialized.BillingAddress.Should().NotBeNull();
        deserialized.BillingAddress!.Street.Should().Be("789 Pine St");
        deserialized.BillingAddress.City.Should().Be("Seattle");
    }

    // =====================================================================
    // Multi-level nesting: OrderSubmitted → LineItem → Money
    // =====================================================================

    [Fact]
    public void MultiLevelNesting_RoundTrip()
    {
        var original = new OrderSubmitted(
            "ORD-003", new DateTime(2024, 7, 15, 8, 30, 0, DateTimeKind.Utc),
            new Address("10 Downing St", "London", "Westminster", "SW1A 2AA", Country.UK),
            null,
            new List<LineItem>
            {
                new("PROD-A", "Gadget Pro", 3, new Money(49.99m, "GBP")),
                new("PROD-B", "Accessory Kit", 1, new Money(12.50m, "GBP")),
                new("PROD-C", "Extended Warranty", 1, new Money(9.99m, "GBP"))
            },
            new Money(172.46m, "GBP"),
            OrderStatus.Shipped);

        var deserialized = RoundTrip(original, "order-submitted-v1");

        deserialized.Items.Should().HaveCount(3);
        deserialized.Items[0].ProductId.Should().Be("PROD-A");
        deserialized.Items[0].Quantity.Should().Be(3);
        deserialized.Items[0].UnitPrice.Amount.Should().Be(49.99m);
        deserialized.Items[0].UnitPrice.Currency.Should().Be("GBP");
        deserialized.Items[2].ProductName.Should().Be("Extended Warranty");
        deserialized.Total.Amount.Should().Be(172.46m);
    }

    // =====================================================================
    // Nullable nested object (BillingAddress null vs non-null)
    // =====================================================================

    [Fact]
    public void NullableNestedObject_Null_RoundTrip()
    {
        var original = new OrderSubmitted(
            "ORD-004", new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            new Address("1 Main St", "Anytown", "CA", "90210", Country.US),
            null, // BillingAddress is null
            new List<LineItem>(),
            new Money(0m, "USD"),
            OrderStatus.Pending);

        var deserialized = RoundTrip(original, "order-submitted-v1");

        deserialized.BillingAddress.Should().BeNull();
    }

    [Fact]
    public void NullableNestedObject_NonNull_RoundTrip()
    {
        var billing = new Address("2 Billing Rd", "Paytown", "NY", "10001", Country.US);
        var original = new OrderSubmitted(
            "ORD-005", new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            new Address("1 Main St", "Anytown", "CA", "90210", Country.US),
            billing,
            new List<LineItem>(),
            new Money(0m, "USD"),
            OrderStatus.Pending);

        var deserialized = RoundTrip(original, "order-submitted-v1");

        deserialized.BillingAddress.Should().NotBeNull();
        deserialized.BillingAddress!.Street.Should().Be("2 Billing Rd");
    }

    // =====================================================================
    // Nullable string in value object
    // =====================================================================

    [Fact]
    public void NullableString_InValueObject_RoundTrip()
    {
        var original = new CustomerCreated(
            "CUST-001", "Jane Doe",
            new ContactInfo("jane@example.com", "+1-555-0100"),
            new Address("100 First St", "Boston", "MA", "02101", Country.US));

        var deserialized = RoundTrip(original, "customer-created-v1");

        deserialized.Contact.Email.Should().Be("jane@example.com");
        deserialized.Contact.Phone.Should().Be("+1-555-0100");
    }

    [Fact]
    public void NullableString_Null_InValueObject_RoundTrip()
    {
        var original = new CustomerCreated(
            "CUST-002", "John Smith",
            new ContactInfo("john@example.com", null), // Phone is null
            new Address("200 Second Ave", "Chicago", "IL", "60601", Country.US));

        var deserialized = RoundTrip(original, "customer-created-v1");

        deserialized.Contact.Phone.Should().BeNull();
    }

    // =====================================================================
    // List of complex types
    // =====================================================================

    [Fact]
    public void ListOfComplexTypes_RoundTrip()
    {
        var original = new OrderSubmitted(
            "ORD-006", new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            new Address("1 Main St", "Anytown", "CA", "90210", Country.US),
            null,
            new List<LineItem>
            {
                new("P1", "Product One", 1, new Money(10m, "USD")),
                new("P2", "Product Two", 5, new Money(20m, "USD"))
            },
            new Money(110m, "USD"),
            OrderStatus.Confirmed);

        var deserialized = RoundTrip(original, "order-submitted-v1");

        deserialized.Items.Should().HaveCount(2);
        deserialized.Items[1].ProductId.Should().Be("P2");
        deserialized.Items[1].Quantity.Should().Be(5);
    }

    // =====================================================================
    // Empty list
    // =====================================================================

    [Fact]
    public void EmptyList_RoundTrip()
    {
        var original = new OrderSubmitted(
            "ORD-007", new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            new Address("1 Main St", "Anytown", "CA", "90210", Country.US),
            null,
            new List<LineItem>(), // empty
            new Money(0m, "USD"),
            OrderStatus.Pending);

        var deserialized = RoundTrip(original, "order-submitted-v1");

        deserialized.Items.Should().BeEmpty();
    }

    // =====================================================================
    // [AkkaSerializable] type as nested field
    // =====================================================================

    [Fact]
    public void AkkaSerializableAsNestedField_RoundTrip()
    {
        var customer = new CustomerCreated(
            "CUST-010", "Alice Wonder",
            new ContactInfo("alice@example.com", null),
            new Address("300 Third Blvd", "Austin", "TX", "73301", Country.US));

        var original = new OrderWithCustomer(
            "ORD-010",
            customer,
            new List<LineItem>
            {
                new("PROD-X", "Deluxe Widget", 2, new Money(99.99m, "USD"))
            });

        var deserialized = RoundTrip(original, "order-with-customer-v1");

        deserialized.OrderId.Should().Be("ORD-010");
        deserialized.Customer.CustomerId.Should().Be("CUST-010");
        deserialized.Customer.Name.Should().Be("Alice Wonder");
        deserialized.Customer.Contact.Email.Should().Be("alice@example.com");
        deserialized.Customer.PrimaryAddress.City.Should().Be("Austin");
        deserialized.Items.Should().HaveCount(1);
        deserialized.Items[0].UnitPrice.Amount.Should().Be(99.99m);
    }

    // =====================================================================
    // All collection types (CatalogSnapshot)
    // =====================================================================

    [Fact]
    public void AllCollectionTypes_RoundTrip()
    {
        var original = new CatalogSnapshot(
            "CAT-001",
            new[] { "Electronics", "Home & Garden", "Sports" },
            ImmutableList.Create(
                new LineItem("FEAT-1", "Featured Widget", 10, new Money(29.99m, "USD")),
                new LineItem("FEAT-2", "Featured Gadget", 5, new Money(49.99m, "USD"))),
            new Dictionary<string, Money>
            {
                ["PROMO-1"] = new Money(19.99m, "USD"),
                ["PROMO-2"] = new Money(39.99m, "EUR")
            },
            new HashSet<string> { "new-arrivals", "best-sellers", "clearance" },
            ImmutableArray.Create(1, 5, 3, 7, 2),
            ImmutableDictionary.CreateRange(new[]
            {
                new KeyValuePair<string, string>("version", "2024.1"),
                new KeyValuePair<string, string>("region", "NA")
            }));

        var deserialized = RoundTrip(original, "catalog-snapshot-v1");

        deserialized.CatalogId.Should().Be("CAT-001");

        // IReadOnlyList<string>
        deserialized.Categories.Should().HaveCount(3);
        deserialized.Categories.Should().Contain("Electronics");

        // ImmutableList<LineItem>
        deserialized.FeaturedItems.Should().HaveCount(2);
        deserialized.FeaturedItems[0].ProductName.Should().Be("Featured Widget");
        deserialized.FeaturedItems[0].UnitPrice.Amount.Should().Be(29.99m);

        // Dictionary<string, Money>
        deserialized.PriceOverrides.Should().HaveCount(2);
        deserialized.PriceOverrides["PROMO-1"].Amount.Should().Be(19.99m);
        deserialized.PriceOverrides["PROMO-2"].Currency.Should().Be("EUR");

        // HashSet<string>
        deserialized.Tags.Should().HaveCount(3);
        deserialized.Tags.Should().Contain("best-sellers");

        // ImmutableArray<int>
        deserialized.PopularItemRanks.Should().HaveCount(5);
        deserialized.PopularItemRanks[0].Should().Be(1);
        deserialized.PopularItemRanks[4].Should().Be(2);

        // ImmutableDictionary<string, string>
        deserialized.Metadata.Should().HaveCount(2);
        deserialized.Metadata["version"].Should().Be("2024.1");
        deserialized.Metadata["region"].Should().Be("NA");
    }

    // =====================================================================
    // Empty collections in CatalogSnapshot
    // =====================================================================

    [Fact]
    public void EmptyCollections_RoundTrip()
    {
        var original = new CatalogSnapshot(
            "CAT-EMPTY",
            Array.Empty<string>(),
            ImmutableList<LineItem>.Empty,
            new Dictionary<string, Money>(),
            new HashSet<string>(),
            ImmutableArray<int>.Empty,
            ImmutableDictionary<string, string>.Empty);

        var deserialized = RoundTrip(original, "catalog-snapshot-v1");

        deserialized.CatalogId.Should().Be("CAT-EMPTY");
        deserialized.Categories.Should().BeEmpty();
        deserialized.FeaturedItems.Should().BeEmpty();
        deserialized.PriceOverrides.Should().BeEmpty();
        deserialized.Tags.Should().BeEmpty();
        deserialized.PopularItemRanks.Should().BeEmpty();
        deserialized.Metadata.Should().BeEmpty();
    }

    // =====================================================================
    // Registry coexistence
    // =====================================================================

    [Fact]
    public void ComplexSerializer_CoexistsWithOtherSerializers()
    {
        var registry = new SerializerRegistry();
        var complexSerializer = new ComplexProtocolSerializer();
        var inventorySerializer = new InventorySerializer();

        registry.Register(complexSerializer, typeof(IComplexProtocol));
        registry.Register(inventorySerializer, typeof(IInventoryProtocol));

        registry.FindFor(new OrderSubmitted("x", DateTime.UtcNow,
                new Address("a", "b", "c", "d", Country.US), null,
                new List<LineItem>(), new Money(0m, "USD"), OrderStatus.Pending))
            .Should().BeSameAs(complexSerializer);

        registry.FindFor(new ItemAdded("SKU", 1))
            .Should().BeSameAs(inventorySerializer);
    }

    // =====================================================================
    // Manifest correctness
    // =====================================================================

    [Fact]
    public void Manifests_AreCorrect()
    {
        _serializer.Manifest(new OrderSubmitted("x", DateTime.UtcNow,
                new Address("a", "b", "c", "d", Country.US), null,
                new List<LineItem>(), new Money(0m, "USD"), OrderStatus.Pending))
            .Should().Be("order-submitted-v1");

        _serializer.Manifest(new CustomerCreated("x", "y",
                new ContactInfo("e", null),
                new Address("a", "b", "c", "d", Country.US)))
            .Should().Be("customer-created-v1");

        _serializer.Manifest(new OrderWithCustomer("x",
                new CustomerCreated("x", "y", new ContactInfo("e", null),
                    new Address("a", "b", "c", "d", Country.US)),
                new List<LineItem>()))
            .Should().Be("order-with-customer-v1");

        _serializer.Manifest(new CatalogSnapshot("x",
                Array.Empty<string>(), ImmutableList<LineItem>.Empty,
                new Dictionary<string, Money>(), new HashSet<string>(),
                ImmutableArray<int>.Empty, ImmutableDictionary<string, string>.Empty))
            .Should().Be("catalog-snapshot-v1");
    }

    // =====================================================================
    // Identifier
    // =====================================================================

    [Fact]
    public void Identifier_MatchesExplicitValue()
    {
        _serializer.Identifier.Should().Be(8001);
    }

    // =====================================================================
    // BoundTypes
    // =====================================================================

    [Fact]
    public void BoundTypes_ContainsProtocolInterface()
    {
        ComplexProtocolSerializerSetup.BoundTypes.Should().ContainSingle()
            .Which.Should().Be(typeof(IComplexProtocol));
    }
}
