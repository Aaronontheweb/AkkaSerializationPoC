using System.Buffers;
using Akka.Actor;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MessagePack;
using Newtonsoft.Json;

namespace Akka.Serialization.Benchmarks;

// =====================================================================
// Benchmark protocol types (self-contained, no test project dependency)
// =====================================================================

public interface IBenchmarkProtocol { }

public sealed record BenchAddress(
    [property: AkkaField(0)] string Street,
    [property: AkkaField(1)] string City,
    [property: AkkaField(2)] string State,
    [property: AkkaField(3)] string ZipCode);

public sealed record BenchMoney(
    [property: AkkaField(0)] decimal Amount,
    [property: AkkaField(1)] string Currency);

public sealed record BenchLineItem(
    [property: AkkaField(0)] string ProductId,
    [property: AkkaField(1)] int Quantity,
    [property: AkkaField(2)] BenchMoney Price);

[AkkaSerializable(Manifest = "bench-order-v1")]
public sealed record BenchOrder(
    [property: AkkaField(0)] string OrderId,
    [property: AkkaField(1)] BenchAddress ShippingAddress,
    [property: AkkaField(2)] List<BenchLineItem> Items,
    [property: AkkaField(3)] BenchMoney Total,
    [property: AkkaField(4)] int Status) : IBenchmarkProtocol;

[AkkaSerializer(Name = "bench-protocol")]
public partial class BenchProtocolSerializer : SerializerV2<IBenchmarkProtocol> { }

// =====================================================================
// Benchmark class
// =====================================================================

/// <summary>
/// Benchmarks comparing three serialization approaches for complex messages
/// with nested value objects, collections, and multi-level nesting:
///
/// 1. Newtonsoft.Json (Akka.NET default) — what most users have today
/// 2. V1 MessagePack (ToBinary() -> byte[]) — same wire format as V2, legacy API shape
/// 3. V2 MessagePack (source-generated, ICodecWriter on shared buffer) — new API
///
/// Parameterized by collection size to show scaling behavior.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class ComplexMessageBenchmarks
{
    [Params(3, 10, 50)]
    public int ItemCount { get; set; }

    private BenchOrder _message = null!;
    private BenchProtocolSerializer _v2Serializer = null!;
    private ArrayBufferWriter<byte> _buffer = null!;

    // Legacy Newtonsoft.Json
    private ActorSystem _actorSystem = null!;
    private Akka.Serialization.Serializer _newtonsoftSerializer = null!;
    private byte[] _newtonsoftBytes = null!;

    // V1-style MessagePack
    private V1MessagePackComplexSerializer _v1Serializer = null!;
    private byte[] _v1Bytes = null!;

    // V2 pre-serialized
    private byte[] _v2Bytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        var items = new List<BenchLineItem>(ItemCount);
        for (int i = 0; i < ItemCount; i++)
        {
            items.Add(new BenchLineItem(
                $"PROD-{i:D4}",
                (i % 10) + 1,
                new BenchMoney(9.99m + i, "USD")));
        }

        _message = new BenchOrder(
            "ORD-BENCH-001",
            new BenchAddress("123 Main St", "Springfield", "IL", "62701"),
            items,
            new BenchMoney(items.Sum(x => x.Price.Amount * x.Quantity), "USD"),
            1); // Confirmed

        // V2 setup
        _v2Serializer = new BenchProtocolSerializer();
        _buffer = new ArrayBufferWriter<byte>(4096);

        // V1 setup
        _v1Serializer = new V1MessagePackComplexSerializer();

        // Legacy Newtonsoft.Json setup
        _actorSystem = ActorSystem.Create("complex-benchmark-system");
        _newtonsoftSerializer = ((ExtendedActorSystem)_actorSystem).Serialization
            .FindSerializerForType(typeof(BenchOrder));

        // Pre-serialize for deserialization benchmarks
        _newtonsoftBytes = _newtonsoftSerializer.ToBinary(_message);
        _v1Bytes = _v1Serializer.ToBinary(_message);

        var v2Buffer = new ArrayBufferWriter<byte>(4096);
        var writer = MessagePackCodecProvider.Instance.CreateWriter(v2Buffer);
        _v2Serializer.Write(writer, _message);
        _v2Bytes = v2Buffer.WrittenSpan.ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _actorSystem?.Terminate().Wait(TimeSpan.FromSeconds(5));
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _buffer.Clear();
    }

    // =====================================================================
    // Serialization
    // =====================================================================

    [Benchmark(Baseline = true)]
    public byte[] NewtonsoftJson_Serialize()
    {
        return _newtonsoftSerializer.ToBinary(_message);
    }

    [Benchmark]
    public byte[] V1MessagePack_Serialize()
    {
        return _v1Serializer.ToBinary(_message);
    }

    [Benchmark]
    public ArrayBufferWriter<byte> V2_Serialize()
    {
        var writer = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        _v2Serializer.Write(writer, _message);
        return _buffer;
    }

    // =====================================================================
    // Deserialization
    // =====================================================================

    [Benchmark]
    public object NewtonsoftJson_Deserialize()
    {
        return _newtonsoftSerializer.FromBinary(_newtonsoftBytes, typeof(BenchOrder));
    }

    [Benchmark]
    public BenchOrder V1MessagePack_Deserialize()
    {
        return _v1Serializer.FromBinary(_v1Bytes);
    }

    [Benchmark]
    public BenchOrder V2_Deserialize()
    {
        var reader = MessagePackCodecProvider.Instance.CreateReader(_v2Bytes);
        return (BenchOrder)_v2Serializer.Read(reader, "bench-order-v1");
    }
}

/// <summary>
/// V1-style hand-written MessagePack serializer for BenchOrder.
/// Produces the same wire format as V2 (nested MessagePack arrays), but
/// uses the legacy ToBinary() -> byte[] API shape to isolate the allocation overhead.
/// </summary>
internal sealed class V1MessagePackComplexSerializer
{
    public byte[] ToBinary(BenchOrder msg)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        var writer = new MessagePackWriter(buffer);

        // BenchOrder: 5 fields
        writer.WriteArrayHeader(5);
        writer.Write(msg.OrderId);

        // BenchAddress: 4 fields
        writer.WriteArrayHeader(4);
        writer.Write(msg.ShippingAddress.Street);
        writer.Write(msg.ShippingAddress.City);
        writer.Write(msg.ShippingAddress.State);
        writer.Write(msg.ShippingAddress.ZipCode);

        // List<BenchLineItem>
        writer.WriteArrayHeader(msg.Items.Count);
        foreach (var item in msg.Items)
        {
            // BenchLineItem: 3 fields
            writer.WriteArrayHeader(3);
            writer.Write(item.ProductId);
            writer.Write(item.Quantity);

            // BenchMoney: 2 fields (decimal as 4 ints)
            writer.WriteArrayHeader(2);
            WriteDecimal(ref writer, item.Price.Amount);
            writer.Write(item.Price.Currency);
        }

        // BenchMoney Total: 2 fields
        writer.WriteArrayHeader(2);
        WriteDecimal(ref writer, msg.Total.Amount);
        writer.Write(msg.Total.Currency);

        writer.Write(msg.Status);

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public BenchOrder FromBinary(byte[] bytes)
    {
        var reader = new MessagePackReader(bytes);

        reader.ReadArrayHeader(); // BenchOrder: 5
        var orderId = reader.ReadString()!;

        // BenchAddress
        reader.ReadArrayHeader(); // 4
        var street = reader.ReadString()!;
        var city = reader.ReadString()!;
        var state = reader.ReadString()!;
        var zip = reader.ReadString()!;
        var address = new BenchAddress(street, city, state, zip);

        // List<BenchLineItem>
        var itemCount = reader.ReadArrayHeader();
        var items = new List<BenchLineItem>(itemCount);
        for (int i = 0; i < itemCount; i++)
        {
            reader.ReadArrayHeader(); // BenchLineItem: 3
            var productId = reader.ReadString()!;
            var qty = reader.ReadInt32();

            reader.ReadArrayHeader(); // BenchMoney: 2
            var priceAmount = ReadDecimal(ref reader);
            var priceCurrency = reader.ReadString()!;

            items.Add(new BenchLineItem(productId, qty, new BenchMoney(priceAmount, priceCurrency)));
        }

        // BenchMoney Total
        reader.ReadArrayHeader(); // 2
        var totalAmount = ReadDecimal(ref reader);
        var totalCurrency = reader.ReadString()!;
        var total = new BenchMoney(totalAmount, totalCurrency);

        var status = reader.ReadInt32();

        return new BenchOrder(orderId, address, items, total, status);
    }

    private static void WriteDecimal(ref MessagePackWriter writer, decimal value)
    {
        var bits = decimal.GetBits(value);
        writer.WriteArrayHeader(4);
        writer.Write(bits[0]);
        writer.Write(bits[1]);
        writer.Write(bits[2]);
        writer.Write(bits[3]);
    }

    private static decimal ReadDecimal(ref MessagePackReader reader)
    {
        reader.ReadArrayHeader(); // 4
        var lo = reader.ReadInt32();
        var mid = reader.ReadInt32();
        var hi = reader.ReadInt32();
        var flags = reader.ReadInt32();
        return new decimal(new[] { lo, mid, hi, flags });
    }
}
