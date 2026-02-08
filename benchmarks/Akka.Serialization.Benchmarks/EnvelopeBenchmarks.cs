using System.Buffers;
using Akka.Actor;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2;
using Akka.Serialization.V2.Tests.Messages;
using Akka.Util.Internal;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using MessagePack;

namespace Akka.Serialization.Benchmarks;

/// <summary>
/// Benchmarks comparing four envelope serialization approaches:
///
/// 1. Newtonsoft.Json (Akka.NET default) — inner message via ToBinary() -> byte[], embedded as blob
/// 2. V1 MessagePack (ToBinary() -> byte[]) — same wire format as V2, but each layer allocates byte[]
/// 3. MsgPackSerializer (Akka.NET's built-in MessagePack) — existing Akka.NET MessagePack serializer
/// 4. V2 MessagePack (ICodecWriter on shared buffer) — single buffer, zero-copy nesting
///
/// V2 vs Newtonsoft.Json shows the full migration benefit (format + API).
/// V2 vs V1 MessagePack isolates the zero-copy nesting improvement (API shape only).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class EnvelopeBenchmarks
{
    private UserCreated _innerMessage = null!;
    private RemoteEnvelope _oneLayerEnvelope = null!;
    private RemoteEnvelope _threeLayerEnvelope = null!;

    // V2 infrastructure
    private SerializerRegistry _registry = null!;
    private UserMessageSerializer _userSerializer = null!;
    private RemoteEnvelopeSerializer _remoteSerializer = null!;
    private DDataEnvelopeSerializer _ddataSerializer = null!;
    private ArrayBufferWriter<byte> _buffer = null!;

    // Legacy Newtonsoft.Json infrastructure
    private ActorSystem _actorSystem = null!;
    private Akka.Serialization.Serializer _newtonsoftSerializer = null!;

    // V1 MessagePack infrastructure (ToBinary() -> byte[] pattern)
    private V1MessagePackEnvelopeSerializer _v1Serializer = null!;

    // Akka.NET MsgPackSerializer (for comparison with V1/V2)
    private MsgPackSerializer _msgPackSerializer = null!;

    // Pre-serialized data for deserialization benchmarks
    private byte[] _v2OneLayerBytes = null!;
    private byte[] _v2ThreeLayerBytes = null!;
    private byte[] _v1OneLayerBytes = null!;
    private byte[] _v1ThreeLayerBytes = null!;
    private byte[] _msgPackOneLayerBytes = null!;
    private byte[] _msgPackThreeLayerBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _innerMessage = new UserCreated(
            "user-12345",
            "test@example.com",
            new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc));

        // V2 registry setup
        _registry = new SerializerRegistry();
        _userSerializer = new UserMessageSerializer();
        _remoteSerializer = new RemoteEnvelopeSerializer(_registry);
        _ddataSerializer = new DDataEnvelopeSerializer(_registry);

        _registry.Register(_userSerializer, typeof(UserCreated), typeof(UserUpdated));
        _registry.Register(_remoteSerializer, typeof(RemoteEnvelope));
        _registry.Register(_ddataSerializer, typeof(DDataEnvelope));

        // Legacy Newtonsoft.Json setup
        _actorSystem = ActorSystem.Create("envelope-benchmark-system");
        _newtonsoftSerializer = ((ExtendedActorSystem)_actorSystem).Serialization
            .FindSerializerForType(typeof(UserCreated));

        // V1 MessagePack setup
        _v1Serializer = new V1MessagePackEnvelopeSerializer();

        // MsgPackSerializer setup (Akka.NET's built-in MessagePack serializer)
        _msgPackSerializer = new MsgPackSerializer(_actorSystem.AsInstanceOf<ExtendedActorSystem>());

        // One-layer envelope: Remote -> UserCreated
        _oneLayerEnvelope = new RemoteEnvelope(
            "/user/recipient",
            "/user/sender",
            _innerMessage);

        // Three-layer envelope: Remote -> DData -> Remote -> UserCreated
        var innerRemote = new RemoteEnvelope(
            "/user/inner-recipient",
            "/user/inner-sender",
            _innerMessage);

        var ddataEnvelope = new DDataEnvelope(
            "replicated-key",
            42L,
            innerRemote);

        _threeLayerEnvelope = new RemoteEnvelope(
            "/user/outer-recipient",
            "/user/outer-sender",
            ddataEnvelope);

        _buffer = new ArrayBufferWriter<byte>(1024);

        // Pre-serialize V2 data for deserialization benchmarks
        var tempBuffer = new ArrayBufferWriter<byte>(1024);
        var writer = MessagePackCodecProvider.Instance.CreateWriter(tempBuffer);
        _remoteSerializer.Write(writer, _oneLayerEnvelope);
        _v2OneLayerBytes = tempBuffer.WrittenSpan.ToArray();

        tempBuffer = new ArrayBufferWriter<byte>(1024);
        writer = MessagePackCodecProvider.Instance.CreateWriter(tempBuffer);
        _remoteSerializer.Write(writer, _threeLayerEnvelope);
        _v2ThreeLayerBytes = tempBuffer.WrittenSpan.ToArray();

        // Pre-serialize V1 data for deserialization benchmarks
        _v1OneLayerBytes = _v1Serializer.SerializeOneLayer(_oneLayerEnvelope, _innerMessage);
        _v1ThreeLayerBytes = _v1Serializer.SerializeThreeLayer(
            _threeLayerEnvelope, ddataEnvelope, innerRemote, _innerMessage);

        // Pre-serialize MsgPackSerializer data for deserialization benchmarks
        _msgPackOneLayerBytes = _msgPackSerializer.ToBinary(_oneLayerEnvelope);
        _msgPackThreeLayerBytes = _msgPackSerializer.ToBinary(_threeLayerEnvelope);
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
    // 1-Layer Envelope: Remote -> UserCreated
    // =====================================================================

    /// <summary>
    /// Legacy Newtonsoft.Json pattern: 1-layer envelope.
    /// Serialize inner via Newtonsoft.Json ToBinary() -> byte[],
    /// then embed as binary blob in outer envelope -> byte[].
    /// </summary>
    [Benchmark(Baseline = true)]
    public byte[] NewtonsoftJson_1Layer_Serialize()
    {
        var innerBytes = _newtonsoftSerializer.ToBinary(_innerMessage);

        _buffer.Clear();
        var outerWriter = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        outerWriter.BeginObject(5);
        outerWriter.WriteString(_oneLayerEnvelope.RecipientPath);
        outerWriter.WriteString(_oneLayerEnvelope.SenderPath);
        outerWriter.WriteInt32(_newtonsoftSerializer.Identifier);
        outerWriter.WriteString(_innerMessage.GetType().FullName!);
        outerWriter.WriteBytes(innerBytes);

        return _buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// V1-style MessagePack pattern: 1-layer envelope.
    /// Same wire format as V2, but each layer serializes to byte[] then embeds.
    /// </summary>
    [Benchmark]
    public byte[] V1MessagePack_1Layer_Serialize()
    {
        return _v1Serializer.SerializeOneLayer(_oneLayerEnvelope, _innerMessage);
    }

    /// <summary>
    /// MsgPackSerializer: Akka.NET's built-in MessagePack serializer for 1-layer envelope.
    /// </summary>
    [Benchmark]
    public byte[] MsgPackSerializer_1Layer_Serialize()
    {
        return _msgPackSerializer.ToBinary(_oneLayerEnvelope);
    }

    /// <summary>
    /// V2 MessagePack pattern: 1-layer envelope.
    /// Single buffer, zero-copy nesting. Both envelope and inner write directly.
    /// </summary>
    [Benchmark]
    public ArrayBufferWriter<byte> V2_1Layer_Serialize()
    {
        var writer = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        _remoteSerializer.Write(writer, _oneLayerEnvelope);
        return _buffer;
    }

    // =====================================================================
    // 3-Layer Envelope: Remote -> DData -> Remote -> UserCreated
    // =====================================================================

    /// <summary>
    /// Legacy Newtonsoft.Json pattern: 3-layer envelope.
    /// Each layer serializes inner to byte[] via Newtonsoft.Json, wraps in next layer.
    /// </summary>
    [Benchmark]
    public byte[] NewtonsoftJson_3Layer_Serialize()
    {
        // Layer 1: Serialize innermost UserCreated via Newtonsoft.Json
        var layer1Bytes = _newtonsoftSerializer.ToBinary(_innerMessage);

        // Layer 2: Inner RemoteEnvelope embedding layer1Bytes
        var layer2Buffer = new ArrayBufferWriter<byte>(256);
        var layer2Writer = MessagePackCodecProvider.Instance.CreateWriter(layer2Buffer);
        layer2Writer.BeginObject(5);
        layer2Writer.WriteString("/user/inner-recipient");
        layer2Writer.WriteString("/user/inner-sender");
        layer2Writer.WriteInt32(_newtonsoftSerializer.Identifier);
        layer2Writer.WriteString(_innerMessage.GetType().FullName!);
        layer2Writer.WriteBytes(layer1Bytes);
        var layer2Bytes = layer2Buffer.WrittenSpan.ToArray();

        // Layer 3: DDataEnvelope embedding layer2Bytes
        var layer3Buffer = new ArrayBufferWriter<byte>(512);
        var layer3Writer = MessagePackCodecProvider.Instance.CreateWriter(layer3Buffer);
        layer3Writer.BeginObject(5);
        layer3Writer.WriteString("replicated-key");
        layer3Writer.WriteInt64(42L);
        layer3Writer.WriteInt32(5001); // inner serializer id
        layer3Writer.WriteString("remote-envelope-v1");
        layer3Writer.WriteBytes(layer2Bytes);
        var layer3Bytes = layer3Buffer.WrittenSpan.ToArray();

        // Layer 4 (outermost): RemoteEnvelope embedding layer3Bytes
        _buffer.Clear();
        var outerWriter = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        outerWriter.BeginObject(5);
        outerWriter.WriteString(_threeLayerEnvelope.RecipientPath);
        outerWriter.WriteString(_threeLayerEnvelope.SenderPath);
        outerWriter.WriteInt32(6001); // ddata serializer id
        outerWriter.WriteString("ddata-envelope-v1");
        outerWriter.WriteBytes(layer3Bytes);

        return _buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// V1-style MessagePack pattern: 3-layer envelope.
    /// Same wire format as V2, but each layer allocates a byte[].
    /// </summary>
    [Benchmark]
    public byte[] V1MessagePack_3Layer_Serialize()
    {
        return _v1Serializer.SerializeThreeLayer(
            _threeLayerEnvelope,
            (DDataEnvelope)_threeLayerEnvelope.Message,
            (RemoteEnvelope)((DDataEnvelope)_threeLayerEnvelope.Message).Data,
            _innerMessage);
    }

    /// <summary>
    /// MsgPackSerializer: Akka.NET's built-in MessagePack serializer for 3-layer envelope.
    /// </summary>
    [Benchmark]
    public byte[] MsgPackSerializer_3Layer_Serialize()
    {
        return _msgPackSerializer.ToBinary(_threeLayerEnvelope);
    }

    /// <summary>
    /// V2 MessagePack pattern: 3-layer envelope.
    /// Single buffer for all layers via zero-copy nesting.
    /// </summary>
    [Benchmark]
    public ArrayBufferWriter<byte> V2_3Layer_Serialize()
    {
        var writer = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        _remoteSerializer.Write(writer, _threeLayerEnvelope);
        return _buffer;
    }

    // =====================================================================
    // Deserialization benchmarks
    // =====================================================================

    /// <summary>
    /// V1-style MessagePack deserialization: 1-layer envelope from byte[].
    /// </summary>
    [Benchmark]
    public RemoteEnvelope V1MessagePack_1Layer_Deserialize()
    {
        return _v1Serializer.DeserializeOneLayer(_v1OneLayerBytes);
    }

    /// <summary>
    /// MsgPackSerializer: Akka.NET's built-in MessagePack deserializer for 1-layer envelope.
    /// </summary>
    [Benchmark]
    public object MsgPackSerializer_1Layer_Deserialize()
    {
        return _msgPackSerializer.FromBinary(_msgPackOneLayerBytes, typeof(RemoteEnvelope));
    }

    /// <summary>
    /// V2 deserialization: 1-layer envelope from pre-serialized bytes.
    /// </summary>
    [Benchmark]
    public RemoteEnvelope V2_1Layer_Deserialize()
    {
        var reader = MessagePackCodecProvider.Instance.CreateReader(_v2OneLayerBytes);
        return (RemoteEnvelope)_remoteSerializer.Read(reader, "remote-envelope-v1");
    }

    /// <summary>
    /// V1-style MessagePack deserialization: 3-layer envelope from byte[].
    /// </summary>
    [Benchmark]
    public RemoteEnvelope V1MessagePack_3Layer_Deserialize()
    {
        return _v1Serializer.DeserializeThreeLayer(_v1ThreeLayerBytes);
    }

    /// <summary>
    /// MsgPackSerializer: Akka.NET's built-in MessagePack deserializer for 3-layer envelope.
    /// </summary>
    [Benchmark]
    public object MsgPackSerializer_3Layer_Deserialize()
    {
        return _msgPackSerializer.FromBinary(_msgPackThreeLayerBytes, typeof(RemoteEnvelope));
    }

    /// <summary>
    /// V2 deserialization: 3-layer envelope from pre-serialized bytes.
    /// </summary>
    [Benchmark]
    public RemoteEnvelope V2_3Layer_Deserialize()
    {
        var reader = MessagePackCodecProvider.Instance.CreateReader(_v2ThreeLayerBytes);
        return (RemoteEnvelope)_remoteSerializer.Read(reader, "remote-envelope-v1");
    }
}

/// <summary>
/// V1-style envelope serializer: uses MessagePack but follows the legacy ToBinary() -> byte[] pattern.
/// Each layer serializes its inner content to byte[], then embeds it as a binary blob.
/// This is what a "best possible" V1 serializer looks like — same wire format, but
/// paying the byte[] allocation tax at every nesting layer.
/// </summary>
internal sealed class V1MessagePackEnvelopeSerializer
{
    public byte[] SerializeOneLayer(RemoteEnvelope envelope, UserCreated inner)
    {
        // Layer 1: inner message -> byte[]
        var innerBytes = SerializeUserCreated(inner);

        // Layer 2: envelope wrapping inner byte[]
        var buffer = new ArrayBufferWriter<byte>(256);
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(5);
        writer.Write(envelope.RecipientPath);
        writer.Write(envelope.SenderPath);
        writer.Write(5001); // serializer id
        writer.Write("user-created-v1");
        writer.Write(innerBytes);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public byte[] SerializeThreeLayer(
        RemoteEnvelope outer, DDataEnvelope ddata,
        RemoteEnvelope innerRemote, UserCreated innerMsg)
    {
        // Layer 1: innermost message -> byte[]
        var layer1 = SerializeUserCreated(innerMsg);

        // Layer 2: inner RemoteEnvelope -> byte[]
        var l2Buf = new ArrayBufferWriter<byte>(256);
        var l2W = new MessagePackWriter(l2Buf);
        l2W.WriteArrayHeader(5);
        l2W.Write(innerRemote.RecipientPath);
        l2W.Write(innerRemote.SenderPath);
        l2W.Write(5001);
        l2W.Write("user-created-v1");
        l2W.Write(layer1);
        l2W.Flush();
        var layer2 = l2Buf.WrittenSpan.ToArray();

        // Layer 3: DDataEnvelope -> byte[]
        var l3Buf = new ArrayBufferWriter<byte>(512);
        var l3W = new MessagePackWriter(l3Buf);
        l3W.WriteArrayHeader(5);
        l3W.Write(ddata.Key);
        l3W.Write(ddata.Version);
        l3W.Write(5002); // remote serializer id
        l3W.Write("remote-envelope-v1");
        l3W.Write(layer2);
        l3W.Flush();
        var layer3 = l3Buf.WrittenSpan.ToArray();

        // Layer 4: outer RemoteEnvelope -> byte[]
        var l4Buf = new ArrayBufferWriter<byte>(1024);
        var l4W = new MessagePackWriter(l4Buf);
        l4W.WriteArrayHeader(5);
        l4W.Write(outer.RecipientPath);
        l4W.Write(outer.SenderPath);
        l4W.Write(6001); // ddata serializer id
        l4W.Write("ddata-envelope-v1");
        l4W.Write(layer3);
        l4W.Flush();
        return l4Buf.WrittenSpan.ToArray();
    }

    public RemoteEnvelope DeserializeOneLayer(byte[] bytes)
    {
        var reader = new MessagePackReader(bytes);
        reader.ReadArrayHeader();
        var recipientPath = reader.ReadString()!;
        var senderPath = reader.ReadString()!;
        reader.ReadInt32(); // serializer id
        reader.ReadString(); // manifest
        var innerBytes = reader.ReadBytes()!.Value.ToArray();

        var inner = DeserializeUserCreated(innerBytes);
        return new RemoteEnvelope(recipientPath, senderPath, inner);
    }

    public RemoteEnvelope DeserializeThreeLayer(byte[] bytes)
    {
        // Outer RemoteEnvelope
        var r = new MessagePackReader(bytes);
        r.ReadArrayHeader();
        var outerRecipient = r.ReadString()!;
        var outerSender = r.ReadString()!;
        r.ReadInt32(); r.ReadString();
        var ddataBytes = r.ReadBytes()!.Value.ToArray();

        // DDataEnvelope
        var r2 = new MessagePackReader(ddataBytes);
        r2.ReadArrayHeader();
        var key = r2.ReadString()!;
        var version = r2.ReadInt64();
        r2.ReadInt32(); r2.ReadString();
        var innerRemoteBytes = r2.ReadBytes()!.Value.ToArray();

        // Inner RemoteEnvelope
        var r3 = new MessagePackReader(innerRemoteBytes);
        r3.ReadArrayHeader();
        var innerRecipient = r3.ReadString()!;
        var innerSender = r3.ReadString()!;
        r3.ReadInt32(); r3.ReadString();
        var userBytes = r3.ReadBytes()!.Value.ToArray();

        var user = DeserializeUserCreated(userBytes);
        var innerRemote = new RemoteEnvelope(innerRecipient, innerSender, user);
        var ddata = new DDataEnvelope(key, version, innerRemote);
        return new RemoteEnvelope(outerRecipient, outerSender, ddata);
    }

    private static byte[] SerializeUserCreated(UserCreated msg)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(3);
        writer.Write(msg.UserId);
        writer.Write(msg.Email);
        writer.Write(msg.CreatedAt.ToBinary());
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static UserCreated DeserializeUserCreated(byte[] bytes)
    {
        var reader = new MessagePackReader(bytes);
        reader.ReadArrayHeader();
        var userId = reader.ReadString()!;
        var email = reader.ReadString()!;
        var createdAt = DateTime.FromBinary(reader.ReadInt64());
        return new UserCreated(userId, email, createdAt);
    }
}
