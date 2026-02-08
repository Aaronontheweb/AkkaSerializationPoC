using System.Buffers;
using System.Text;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2;
using FluentAssertions;
using Xunit;

namespace Akka.Serialization.V2.Tests.Generated;

// =====================================================================
// Protocol A — "inventory" domain
// =====================================================================

public interface IInventoryProtocol { }

[AkkaSerializable(Manifest = "item-added-v1")]
public sealed record ItemAdded(
    [property: AkkaField(0)] string Sku,
    [property: AkkaField(1)] int Quantity) : IInventoryProtocol;

[AkkaSerializable(Manifest = "item-removed-v1")]
public sealed record ItemRemoved(
    [property: AkkaField(0)] string Sku,
    [property: AkkaField(1)] int Quantity) : IInventoryProtocol;

[AkkaSerializer(Name = "inventory-protocol")]
public partial class InventorySerializer : SerializerV2<IInventoryProtocol> { }

// =====================================================================
// Protocol B — "shipping" domain
// =====================================================================

public interface IShippingProtocol { }

[AkkaSerializable(Manifest = "shipment-created-v1")]
public sealed record ShipmentCreated(
    [property: AkkaField(0)] string ShipmentId,
    [property: AkkaField(1)] string Address,
    [property: AkkaField(2)] DateTime CreatedAt) : IShippingProtocol;

[AkkaSerializable(Manifest = "shipment-delivered-v1")]
public sealed record ShipmentDelivered(
    [property: AkkaField(0)] string ShipmentId,
    [property: AkkaField(1)] DateTime DeliveredAt) : IShippingProtocol;

[AkkaSerializer(Name = "shipping-protocol")]
public partial class ShippingSerializer : SerializerV2<IShippingProtocol> { }

// =====================================================================
// Tests
// =====================================================================

/// <summary>
/// Tests verifying that multiple serializer modules can coexist in the same compilation,
/// each scoped to its own protocol interface. This is the key scenario that the old
/// [AkkaSerializerModule] approach could NOT handle.
/// </summary>
public class MultiModuleTests
{
    /// <summary>
    /// FNV-1a hash implementation matching the source generator's ComputeFnv1aHash.
    /// Duplicated here since the generator assembly isn't directly referenceable.
    /// </summary>
    private static int ComputeFnv1aHash(string input)
    {
        const uint FnvOffsetBasis = 2166136261u;
        const uint FnvPrime = 16777619u;
        uint hash = FnvOffsetBasis;
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= FnvPrime;
        }
        return (int)(hash & 0x7FFFFFFF);
    }

    private readonly InventorySerializer _inventorySerializer = new();
    private readonly ShippingSerializer _shippingSerializer = new();
    private readonly MessagePackCodecProvider _codec = MessagePackCodecProvider.Instance;

    // =====================================================================
    // Inventory serializer tests
    // =====================================================================

    [Fact]
    public void Inventory_ItemAdded_RoundTrip()
    {
        var original = new ItemAdded("SKU-001", 42);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        _inventorySerializer.Write(writer, original);

        var reader = _codec.CreateReader(buffer.WrittenMemory);
        var deserialized = (ItemAdded)_inventorySerializer.Read(reader, "item-added-v1");

        deserialized.Sku.Should().Be("SKU-001");
        deserialized.Quantity.Should().Be(42);
    }

    [Fact]
    public void Inventory_ItemRemoved_RoundTrip()
    {
        var original = new ItemRemoved("SKU-002", 10);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        _inventorySerializer.Write(writer, original);

        var reader = _codec.CreateReader(buffer.WrittenMemory);
        var deserialized = (ItemRemoved)_inventorySerializer.Read(reader, "item-removed-v1");

        deserialized.Sku.Should().Be("SKU-002");
        deserialized.Quantity.Should().Be(10);
    }

    [Fact]
    public void Inventory_Manifest_ReturnsCorrectValues()
    {
        _inventorySerializer.Manifest(new ItemAdded("x", 1)).Should().Be("item-added-v1");
        _inventorySerializer.Manifest(new ItemRemoved("x", 1)).Should().Be("item-removed-v1");
    }

    // =====================================================================
    // Shipping serializer tests
    // =====================================================================

    [Fact]
    public void Shipping_ShipmentCreated_RoundTrip()
    {
        var original = new ShipmentCreated("SHIP-001", "123 Main St",
            new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc));

        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        _shippingSerializer.Write(writer, original);

        var reader = _codec.CreateReader(buffer.WrittenMemory);
        var deserialized = (ShipmentCreated)_shippingSerializer.Read(reader, "shipment-created-v1");

        deserialized.ShipmentId.Should().Be("SHIP-001");
        deserialized.Address.Should().Be("123 Main St");
        deserialized.CreatedAt.Should().Be(new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Shipping_ShipmentDelivered_RoundTrip()
    {
        var original = new ShipmentDelivered("SHIP-002",
            new DateTime(2024, 7, 1, 14, 30, 0, DateTimeKind.Utc));

        var buffer = new ArrayBufferWriter<byte>();
        var writer = _codec.CreateWriter(buffer);
        _shippingSerializer.Write(writer, original);

        var reader = _codec.CreateReader(buffer.WrittenMemory);
        var deserialized = (ShipmentDelivered)_shippingSerializer.Read(reader, "shipment-delivered-v1");

        deserialized.ShipmentId.Should().Be("SHIP-002");
        deserialized.DeliveredAt.Should().Be(new DateTime(2024, 7, 1, 14, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Shipping_Manifest_ReturnsCorrectValues()
    {
        _shippingSerializer.Manifest(new ShipmentCreated("x", "y", DateTime.UtcNow)).Should().Be("shipment-created-v1");
        _shippingSerializer.Manifest(new ShipmentDelivered("x", DateTime.UtcNow)).Should().Be("shipment-delivered-v1");
    }

    // =====================================================================
    // Cross-module isolation tests
    // =====================================================================

    [Fact]
    public void Inventory_DoesNotHandle_ShippingTypes()
    {
        var shippingMsg = new ShipmentCreated("SHIP-001", "addr", DateTime.UtcNow);
        var act = () => _inventorySerializer.Manifest(shippingMsg);
        act.Should().Throw<ArgumentException>().WithMessage("*Unsupported type*");
    }

    [Fact]
    public void Shipping_DoesNotHandle_InventoryTypes()
    {
        var inventoryMsg = new ItemAdded("SKU-001", 5);
        var act = () => _shippingSerializer.Manifest(inventoryMsg);
        act.Should().Throw<ArgumentException>().WithMessage("*Unsupported type*");
    }

    // =====================================================================
    // Registry coexistence
    // =====================================================================

    [Fact]
    public void BothSerializers_CoexistInRegistry()
    {
        var registry = new SerializerRegistry();
        registry.Register(_inventorySerializer, typeof(IInventoryProtocol));
        registry.Register(_shippingSerializer, typeof(IShippingProtocol));

        // Inventory types resolve to inventory serializer
        registry.FindFor(new ItemAdded("x", 1)).Should().BeSameAs(_inventorySerializer);
        registry.FindFor(new ItemRemoved("x", 1)).Should().BeSameAs(_inventorySerializer);

        // Shipping types resolve to shipping serializer
        registry.FindFor(new ShipmentCreated("x", "y", DateTime.UtcNow)).Should().BeSameAs(_shippingSerializer);
        registry.FindFor(new ShipmentDelivered("x", DateTime.UtcNow)).Should().BeSameAs(_shippingSerializer);
    }

    // =====================================================================
    // BoundTypes
    // =====================================================================

    [Fact]
    public void BoundTypes_ContainsProtocolInterface()
    {
        InventorySerializerSetup.BoundTypes.Should().ContainSingle()
            .Which.Should().Be(typeof(IInventoryProtocol));

        ShippingSerializerSetup.BoundTypes.Should().ContainSingle()
            .Which.Should().Be(typeof(IShippingProtocol));
    }

    // =====================================================================
    // FNV-1a hash tests
    // =====================================================================

    [Fact]
    public void FnvHash_IsDeterministic()
    {
        var hash1 = ComputeFnv1aHash("inventory-protocol");
        var hash2 = ComputeFnv1aHash("inventory-protocol");
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void FnvHash_IsPositive()
    {
        var hash = ComputeFnv1aHash("inventory-protocol");
        hash.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FnvHash_DifferentNames_ProduceDifferentIds()
    {
        var hashA = ComputeFnv1aHash("inventory-protocol");
        var hashB = ComputeFnv1aHash("shipping-protocol");
        hashA.Should().NotBe(hashB);
    }

    [Fact]
    public void Inventory_Identifier_MatchesFnvHash()
    {
        // InventorySerializer has Name = "inventory-protocol" with no explicit SerializerId
        var expectedId = ComputeFnv1aHash("inventory-protocol");
        _inventorySerializer.Identifier.Should().Be(expectedId);
    }

    [Fact]
    public void Shipping_Identifier_MatchesFnvHash()
    {
        // ShippingSerializer has Name = "shipping-protocol" with no explicit SerializerId
        var expectedId = ComputeFnv1aHash("shipping-protocol");
        _shippingSerializer.Identifier.Should().Be(expectedId);
    }

    [Fact]
    public void DifferentSerializers_HaveDifferentIds()
    {
        _inventorySerializer.Identifier.Should().NotBe(_shippingSerializer.Identifier);
    }
}
