using Akka.Serialization.V2;

namespace Akka.Serialization.V2.Tests.Messages;

/// <summary>
/// Hand-written SerializerV2 for UserCreated and UserUpdated messages.
/// Demonstrates manifest routing, field-by-field serialization, and version tolerance.
/// </summary>
public sealed class UserMessageSerializer : SerializerV2
{
    public override int Identifier => 5001;

    public override string? Manifest(object obj) => obj switch
    {
        UserCreated => "user-created-v1",
        UserUpdated => "user-updated-v1",
        _ => throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj))
    };

    public override void Write(AkkaWriter writer, object obj)
    {
        switch (obj)
        {
            case UserCreated msg:
                WriteUserCreated(writer, msg);
                break;
            case UserUpdated msg:
                WriteUserUpdated(writer, msg);
                break;
            default:
                throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj));
        }
    }

    public override object Read(AkkaReader reader, string manifest)
    {
        return manifest switch
        {
            "user-created-v1" => ReadUserCreated(reader),
            "user-updated-v1" => ReadUserUpdated(reader),
            _ => throw new ArgumentException($"Unknown manifest: {manifest}", nameof(manifest))
        };
    }

    private void WriteUserCreated(AkkaWriter writer, UserCreated msg)
    {
        // BeginObject with 3 fields: UserId, Email, CreatedAt
        writer.BeginObject(3);
        writer.WriteString(msg.UserId);
        writer.WriteString(msg.Email);
        writer.WriteDateTime(msg.CreatedAt);
    }

    private UserCreated ReadUserCreated(AkkaReader reader)
    {
        var fieldCount = reader.BeginReadObject();

        // Read known fields
        var userId = reader.ReadString() ?? string.Empty;
        var email = reader.ReadString() ?? string.Empty;
        var createdAt = reader.ReadDateTime();

        // Skip unknown trailing fields for forward compatibility
        for (int i = 3; i < fieldCount; i++)
        {
            reader.SkipField();
        }

        return new UserCreated(userId, email, createdAt);
    }

    private void WriteUserUpdated(AkkaWriter writer, UserUpdated msg)
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

    private UserUpdated ReadUserUpdated(AkkaReader reader)
    {
        var fieldCount = reader.BeginReadObject();

        // Read known fields
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

        return new UserUpdated(userId, newEmail, newName, updatedAt);
    }

    public override int SizeHint(object obj) => 128;
}
