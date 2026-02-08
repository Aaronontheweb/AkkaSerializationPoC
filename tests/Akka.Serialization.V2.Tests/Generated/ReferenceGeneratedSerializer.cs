#if REFERENCE_IMPL
using Akka.Serialization.V2;

namespace Akka.Serialization.V2.Tests.Messages;

/// <summary>
/// This file represents what the source generator WILL produce for AnnotatedMessageSerializer.
/// It serves as a reference implementation and specification for the generator to be built in Phase 5.
///
/// This completes the partial class defined in AnnotatedMessages.cs, making it a full SerializerV2.
/// The serialization logic is IDENTICAL to the hand-written serializers to ensure byte-for-byte compatibility.
/// </summary>
public partial class AnnotatedMessageSerializer : SerializerV2
{
    public override int Identifier => 6001;

    public override string? Manifest(object obj) => obj switch
    {
        UserCreatedAnnotated => "user-created-v1",
        UserUpdatedAnnotated => "user-updated-v1",
        OrderPlacedAnnotated => "order-placed-v1",
        _ => throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj))
    };

    public override void Write(ICodecWriter writer, object obj)
    {
        switch (obj)
        {
            case UserCreatedAnnotated msg:
                WriteUserCreatedAnnotated(writer, msg);
                break;
            case UserUpdatedAnnotated msg:
                WriteUserUpdatedAnnotated(writer, msg);
                break;
            case OrderPlacedAnnotated msg:
                WriteOrderPlacedAnnotated(writer, msg);
                break;
            default:
                throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj));
        }
    }

    public override object Read(ICodecReader reader, string manifest)
    {
        return manifest switch
        {
            "user-created-v1" => ReadUserCreatedAnnotated(reader),
            "user-updated-v1" => ReadUserUpdatedAnnotated(reader),
            "order-placed-v1" => ReadOrderPlacedAnnotated(reader),
            _ => throw new ArgumentException($"Unknown manifest: {manifest}", nameof(manifest))
        };
    }

    public override int SizeHint(object obj) => 128;

    // =====================================================================
    // Per-type serialization methods
    // Generated based on [AkkaField] attributes in index order
    // =====================================================================

    private void WriteUserCreatedAnnotated(ICodecWriter writer, UserCreatedAnnotated msg)
    {
        // BeginObject with 3 fields: UserId, Email, CreatedAt
        writer.BeginObject(3);
        writer.WriteString(msg.UserId);
        writer.WriteString(msg.Email);
        writer.WriteDateTime(msg.CreatedAt);
    }

    private UserCreatedAnnotated ReadUserCreatedAnnotated(ICodecReader reader)
    {
        var fieldCount = reader.BeginReadObject();

        // Read known fields in [AkkaField] index order
        var userId = reader.ReadString() ?? string.Empty;
        var email = reader.ReadString() ?? string.Empty;
        var createdAt = reader.ReadDateTime();

        // Skip unknown trailing fields for forward compatibility
        for (int i = 3; i < fieldCount; i++)
        {
            reader.SkipField();
        }

        return new UserCreatedAnnotated(userId, email, createdAt);
    }

    private void WriteUserUpdatedAnnotated(ICodecWriter writer, UserUpdatedAnnotated msg)
    {
        // BeginObject with 4 fields: UserId, NewEmail, NewName, UpdatedAt
        writer.BeginObject(4);
        writer.WriteString(msg.UserId);

        // Write nullable fields - use WriteNull for null values
        if (msg.NewEmail is null)
            writer.WriteNull();
        else
            writer.WriteString(msg.NewEmail);

        if (msg.NewName is null)
            writer.WriteNull();
        else
            writer.WriteString(msg.NewName);

        writer.WriteDateTime(msg.UpdatedAt);
    }

    private UserUpdatedAnnotated ReadUserUpdatedAnnotated(ICodecReader reader)
    {
        var fieldCount = reader.BeginReadObject();

        // Read known fields in [AkkaField] index order
        var userId = reader.ReadString() ?? string.Empty;

        // Read nullable fields
        var newEmail = reader.TryReadNull() ? null : reader.ReadString();
        var newName = reader.TryReadNull() ? null : reader.ReadString();
        var updatedAt = reader.ReadDateTime();

        // Skip unknown trailing fields for forward compatibility
        for (int i = 4; i < fieldCount; i++)
        {
            reader.SkipField();
        }

        return new UserUpdatedAnnotated(userId, newEmail, newName, updatedAt);
    }

    private void WriteOrderPlacedAnnotated(ICodecWriter writer, OrderPlacedAnnotated msg)
    {
        // BeginObject with 4 fields: OrderId, CustomerId, Amount, PlacedAt
        writer.BeginObject(4);
        writer.WriteGuid(msg.OrderId);
        writer.WriteString(msg.CustomerId);
        // Handle decimal as double for simplicity
        writer.WriteDouble((double)msg.Amount);
        writer.WriteDateTimeOffset(msg.PlacedAt);
    }

    private OrderPlacedAnnotated ReadOrderPlacedAnnotated(ICodecReader reader)
    {
        var fieldCount = reader.BeginReadObject();

        // Read known fields in [AkkaField] index order
        var orderId = reader.ReadGuid();
        var customerId = reader.ReadString() ?? string.Empty;
        var amount = (decimal)reader.ReadDouble();
        var placedAt = reader.ReadDateTimeOffset();

        // Skip unknown trailing fields for forward compatibility
        for (int i = 4; i < fieldCount; i++)
        {
            reader.SkipField();
        }

        return new OrderPlacedAnnotated(orderId, customerId, amount, placedAt);
    }
}
#endif
