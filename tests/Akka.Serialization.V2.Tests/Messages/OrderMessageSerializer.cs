using Akka.Serialization.V2;

namespace Akka.Serialization.V2.Tests.Messages;

/// <summary>
/// Hand-written SerializerV2 for OrderPlaced messages.
/// Demonstrates Guid and DateTimeOffset handling.
/// Handles decimal as double for simplicity (good enough for PoC).
/// </summary>
public sealed class OrderMessageSerializer : SerializerV2
{
    public override int Identifier => 5002;

    public override string? Manifest(object obj) => obj switch
    {
        OrderPlaced => "order-placed-v1",
        _ => throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj))
    };

    public override void Write(ICodecWriter writer, object obj)
    {
        if (obj is not OrderPlaced msg)
            throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj));

        WriteOrderPlaced(writer, msg);
    }

    public override object Read(ICodecReader reader, string manifest)
    {
        return manifest switch
        {
            "order-placed-v1" => ReadOrderPlaced(reader),
            _ => throw new ArgumentException($"Unknown manifest: {manifest}", nameof(manifest))
        };
    }

    private void WriteOrderPlaced(ICodecWriter writer, OrderPlaced msg)
    {
        // BeginObject with 4 fields: OrderId, CustomerId, Amount, PlacedAt
        writer.BeginObject(4);
        writer.WriteGuid(msg.OrderId);
        writer.WriteString(msg.CustomerId);
        writer.WriteDecimal(msg.Amount);
        writer.WriteDateTimeOffset(msg.PlacedAt);
    }

    private OrderPlaced ReadOrderPlaced(ICodecReader reader)
    {
        var fieldCount = reader.BeginReadObject();

        // Read known fields
        var orderId = reader.ReadGuid();
        var customerId = reader.ReadString() ?? string.Empty;
        var amount = reader.ReadDecimal();
        var placedAt = reader.ReadDateTimeOffset();

        // Skip unknown trailing fields for forward compatibility
        for (int i = 4; i < fieldCount; i++)
        {
            reader.SkipField();
        }

        return new OrderPlaced(orderId, customerId, amount, placedAt);
    }

    public override int SizeHint(object obj) => 128;
}
