using System.Buffers;
using Akka.Actor;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2;
using Akka.Serialization.V2.Tests.Messages;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Akka.Serialization.Benchmarks;

/// <summary>
/// Benchmarks comparing the legacy Newtonsoft.Json multi-layer serialization pattern
/// vs V2 single-buffer zero-copy pattern for envelope messages.
///
/// Legacy pattern (what real Akka.NET users have today):
///   Inner message -> Newtonsoft.Json ToBinary() -> byte[]
///   Outer envelope embeds byte[] as binary blob -> ToBinary() -> byte[]
///   Each layer allocates a new byte[]
///
/// V2 pattern:
///   Single IBufferWriter shared across all layers -> zero intermediate allocations
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

    // Pre-serialized data for deserialization benchmarks
    private byte[] _v2OneLayerBytes = null!;
    private byte[] _v2ThreeLayerBytes = null!;

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

        // Legacy setup
        _actorSystem = ActorSystem.Create("envelope-benchmark-system");
        _newtonsoftSerializer = ((ExtendedActorSystem)_actorSystem).Serialization
            .FindSerializerForType(typeof(UserCreated));

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
    /// Two allocations: inner byte[] + outer byte[].
    /// </summary>
    [Benchmark(Baseline = true)]
    public byte[] NewtonsoftJson_1Layer_Serialize()
    {
        // Step 1: Serialize inner UserCreated to byte[] via Newtonsoft.Json
        var innerBytes = _newtonsoftSerializer.ToBinary(_innerMessage);

        // Step 2: Serialize outer envelope embedding innerBytes as binary blob
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
    /// Multiple byte[] allocations per layer.
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
        layer3Writer.WriteInt32(_remoteSerializer.Identifier);
        layer3Writer.WriteString("remote-envelope-v1");
        layer3Writer.WriteBytes(layer2Bytes);
        var layer3Bytes = layer3Buffer.WrittenSpan.ToArray();

        // Layer 4 (outermost): RemoteEnvelope embedding layer3Bytes
        _buffer.Clear();
        var outerWriter = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        outerWriter.BeginObject(5);
        outerWriter.WriteString(_threeLayerEnvelope.RecipientPath);
        outerWriter.WriteString(_threeLayerEnvelope.SenderPath);
        outerWriter.WriteInt32(_ddataSerializer.Identifier);
        outerWriter.WriteString("ddata-envelope-v1");
        outerWriter.WriteBytes(layer3Bytes);

        return _buffer.WrittenSpan.ToArray();
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
    /// V2 deserialization: 1-layer envelope from pre-serialized bytes.
    /// </summary>
    [Benchmark]
    public RemoteEnvelope V2_1Layer_Deserialize()
    {
        var reader = MessagePackCodecProvider.Instance.CreateReader(_v2OneLayerBytes);
        return (RemoteEnvelope)_remoteSerializer.Read(reader, "remote-envelope-v1");
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
