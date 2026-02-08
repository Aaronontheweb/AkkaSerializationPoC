using System.Buffers;
using System.Text.Json;
using Akka.Actor;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2.Tests.Messages;
using FluentAssertions;
using Xunit;

namespace Akka.Serialization.V2.Tests;

/// <summary>
/// Legacy serializer that uses System.Text.Json to serialize UserCreated messages.
/// Extends Akka.NET's SerializerWithStringManifest to simulate a pre-V2 serializer.
/// </summary>
public sealed class LegacyUserSerializer : SerializerWithStringManifest
{
    private const string UserCreatedManifest = "legacy-user-created";

    public LegacyUserSerializer(ExtendedActorSystem system) : base(system)
    {
    }

    public override int Identifier => 1001;

    public override string Manifest(object o) => o switch
    {
        UserCreated => UserCreatedManifest,
        _ => throw new ArgumentException($"Unsupported type: {o.GetType()}", nameof(o))
    };

    public override byte[] ToBinary(object obj)
    {
        if (obj is not UserCreated msg)
            throw new ArgumentException($"Unsupported type: {obj.GetType()}", nameof(obj));

        return JsonSerializer.SerializeToUtf8Bytes(msg);
    }

    public override object FromBinary(byte[] bytes, string manifest)
    {
        return manifest switch
        {
            UserCreatedManifest => JsonSerializer.Deserialize<UserCreated>(bytes)
                ?? throw new InvalidOperationException("Deserialized UserCreated was null"),
            _ => throw new ArgumentException($"Unknown manifest: {manifest}", nameof(manifest))
        };
    }
}

/// <summary>
/// Tests for backwards compatibility via SerializerV2Adapter.
/// Validates that legacy Akka.NET serializers can be wrapped and used within the V2 system,
/// including coexistence with native V2 serializers and nesting inside V2 envelopes.
/// </summary>
public class BackwardsCompatTests : IDisposable
{
    private readonly ActorSystem _actorSystem;
    private readonly MessagePackCodecProvider _codecProvider = MessagePackCodecProvider.Instance;

    public BackwardsCompatTests()
    {
        _actorSystem = ActorSystem.Create("backwards-compat-test");
    }

    public void Dispose()
    {
        _actorSystem.Terminate().Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies that a legacy serializer wrapped in SerializerV2Adapter can round-trip
    /// a UserCreated message through the V2 codec pipeline.
    /// </summary>
    [Fact]
    public void LegacyAdapter_RoundTrip()
    {
        // Arrange
        var legacySerializer = new LegacyUserSerializer((ExtendedActorSystem)_actorSystem);
        var adapter = new SerializerV2Adapter(legacySerializer);

        var original = new UserCreated(
            UserId: "legacy-user-001",
            Email: "legacy@example.com",
            CreatedAt: new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc));

        // Act - Serialize through adapter via V2 codec
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = _codecProvider.CreateWriter(buffer))
        {
            adapter.Write(writer, original);
            writer.Flush();
        }

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize through adapter via V2 codec
        var manifest = adapter.Manifest(original);
        UserCreated deserialized;
        using (var reader = _codecProvider.CreateReader(bytes))
        {
            deserialized = (UserCreated)adapter.Read(reader, manifest!);
        }

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.UserId.Should().Be(original.UserId);
        deserialized.Email.Should().Be(original.Email);
        deserialized.CreatedAt.Should().Be(original.CreatedAt);

        // Verify adapter preserves the legacy serializer's identifier and manifest
        adapter.Identifier.Should().Be(1001);
        manifest.Should().Be("legacy-user-created");
    }

    /// <summary>
    /// Verifies that a legacy-serialized message can be embedded inside a V2 RemoteEnvelope.
    /// The adapter writes the legacy bytes as a binary blob at the nesting point,
    /// and on read the adapter extracts and deserializes the blob using the legacy serializer.
    /// </summary>
    [Fact]
    public void LegacyAdapter_InEnvelope()
    {
        // Arrange - Set up registry with envelope serializer + legacy adapter
        var registry = new SerializerRegistry();
        var legacySerializer = new LegacyUserSerializer((ExtendedActorSystem)_actorSystem);
        var adapter = new SerializerV2Adapter(legacySerializer);
        var remoteSerializer = new RemoteEnvelopeSerializer(registry);

        registry.Register(adapter, typeof(UserCreated));
        registry.Register(remoteSerializer, typeof(RemoteEnvelope));

        var innerMessage = new UserCreated(
            UserId: "envelope-legacy-001",
            Email: "envelope-legacy@example.com",
            CreatedAt: new DateTime(2024, 7, 20, 14, 15, 0, DateTimeKind.Utc));

        var envelope = new RemoteEnvelope(
            RecipientPath: "akka://system/user/target",
            SenderPath: "akka://system/user/source",
            Message: innerMessage);

        // Act - Serialize the entire envelope (V2 envelope wrapping legacy-adapted inner message)
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = _codecProvider.CreateWriter(buffer))
        {
            remoteSerializer.Write(writer, envelope);
            writer.Flush();
        }

        var bytes = buffer.WrittenMemory;

        // Act - Deserialize
        RemoteEnvelope deserialized;
        using (var reader = _codecProvider.CreateReader(bytes))
        {
            var manifest = remoteSerializer.Manifest(envelope);
            deserialized = (RemoteEnvelope)remoteSerializer.Read(reader, manifest!);
        }

        // Assert - Envelope metadata preserved
        deserialized.Should().NotBeNull();
        deserialized.RecipientPath.Should().Be(envelope.RecipientPath);
        deserialized.SenderPath.Should().Be(envelope.SenderPath);

        // Assert - Inner message deserialized correctly through legacy adapter
        deserialized.Message.Should().BeOfType<UserCreated>();
        var deserializedInner = (UserCreated)deserialized.Message;
        deserializedInner.UserId.Should().Be(innerMessage.UserId);
        deserializedInner.Email.Should().Be(innerMessage.Email);
        deserializedInner.CreatedAt.Should().Be(innerMessage.CreatedAt);
    }

    /// <summary>
    /// Verifies that native V2 serializers and adapted legacy serializers can coexist
    /// in the same SerializerRegistry. Both types resolve correctly for their registered types,
    /// and the registry routes serialization/deserialization to the correct serializer.
    /// </summary>
    [Fact]
    public void MixedRegistry()
    {
        // Arrange - Registry with both V2 native and legacy adapted serializers
        var registry = new SerializerRegistry();

        // Native V2 serializer for UserUpdated
        var nativeSerializer = new UserMessageSerializer();

        // Legacy adapter for UserCreated
        var legacySerializer = new LegacyUserSerializer((ExtendedActorSystem)_actorSystem);
        var adapter = new SerializerV2Adapter(legacySerializer);

        // Register: UserCreated -> legacy adapter, UserUpdated -> native V2
        registry.Register(adapter, typeof(UserCreated));
        registry.Register(nativeSerializer, typeof(UserUpdated));

        // Arrange - Create test messages
        var userCreated = new UserCreated(
            UserId: "mixed-user-001",
            Email: "mixed@example.com",
            CreatedAt: new DateTime(2024, 8, 1, 12, 0, 0, DateTimeKind.Utc));

        var userUpdated = new UserUpdated(
            UserId: "mixed-user-001",
            NewEmail: "updated-mixed@example.com",
            NewName: "Mixed User",
            UpdatedAt: new DateTime(2024, 8, 2, 14, 30, 0, DateTimeKind.Utc));

        // Act & Assert - UserCreated routes to legacy adapter
        var createdSerializer = registry.FindFor(userCreated);
        createdSerializer.Should().NotBeNull();
        createdSerializer.Should().BeOfType<SerializerV2Adapter>();
        createdSerializer!.Identifier.Should().Be(1001);

        // Act & Assert - UserUpdated routes to native V2 serializer
        var updatedSerializer = registry.FindFor(userUpdated);
        updatedSerializer.Should().NotBeNull();
        updatedSerializer.Should().BeOfType<UserMessageSerializer>();
        updatedSerializer!.Identifier.Should().Be(5001);

        // Act & Assert - Round-trip UserCreated through legacy adapter
        var createdBuffer = new ArrayBufferWriter<byte>();
        using (var writer = _codecProvider.CreateWriter(createdBuffer))
        {
            createdSerializer.Write(writer, userCreated);
            writer.Flush();
        }

        UserCreated deserializedCreated;
        using (var reader = _codecProvider.CreateReader(createdBuffer.WrittenMemory))
        {
            var manifest = createdSerializer.Manifest(userCreated);
            deserializedCreated = (UserCreated)createdSerializer.Read(reader, manifest!);
        }

        deserializedCreated.UserId.Should().Be(userCreated.UserId);
        deserializedCreated.Email.Should().Be(userCreated.Email);
        deserializedCreated.CreatedAt.Should().Be(userCreated.CreatedAt);

        // Act & Assert - Round-trip UserUpdated through native V2 serializer
        var updatedBuffer = new ArrayBufferWriter<byte>();
        using (var writer = _codecProvider.CreateWriter(updatedBuffer))
        {
            updatedSerializer.Write(writer, userUpdated);
            writer.Flush();
        }

        UserUpdated deserializedUpdated;
        using (var reader = _codecProvider.CreateReader(updatedBuffer.WrittenMemory))
        {
            var manifest = updatedSerializer.Manifest(userUpdated);
            deserializedUpdated = (UserUpdated)updatedSerializer.Read(reader, manifest!);
        }

        deserializedUpdated.UserId.Should().Be(userUpdated.UserId);
        deserializedUpdated.NewEmail.Should().Be(userUpdated.NewEmail);
        deserializedUpdated.NewName.Should().Be(userUpdated.NewName);
        deserializedUpdated.UpdatedAt.Should().Be(userUpdated.UpdatedAt);

        // Verify lookup by serializer ID works for both
        registry.GetById(1001).Should().BeSameAs(adapter);
        registry.GetById(5001).Should().BeSameAs(nativeSerializer);
    }
}
