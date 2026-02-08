using System.Buffers;
using Akka.Serialization.MessagePack;
using Akka.Serialization.V2;
using Akka.Serialization.V2.Tests.Messages;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Akka.Serialization.Benchmarks;

/// <summary>
/// Benchmarks comparing legacy multi-layer byte[] allocation pattern vs V2 single-buffer zero-copy pattern
/// for envelope messages (nested serialization).
///
/// Legacy pattern: Inner -> byte[] -> Outer envelope -> byte[] (multiple allocations per layer)
/// V2 pattern: Single buffer shared across all layers (zero-copy)
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class EnvelopeBenchmarks
{
    private UserCreated _innerMessage = null!;
    private RemoteEnvelope _oneLayerEnvelope = null!;
    private RemoteEnvelope _threeLayerEnvelope = null!;

    private SerializerRegistry _registry = null!;
    private UserMessageSerializer _userSerializer = null!;
    private RemoteEnvelopeSerializer _remoteSerializer = null!;
    private DDataEnvelopeSerializer _ddataSerializer = null!;

    private ArrayBufferWriter<byte> _buffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _innerMessage = new UserCreated(
            "user-12345",
            "test@example.com",
            new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc));

        // Setup registry with all serializers
        _registry = new SerializerRegistry();
        _userSerializer = new UserMessageSerializer();
        _remoteSerializer = new RemoteEnvelopeSerializer(_registry);
        _ddataSerializer = new DDataEnvelopeSerializer(_registry);

        _registry.Register(_userSerializer, typeof(UserCreated), typeof(UserUpdated));
        _registry.Register(_remoteSerializer, typeof(RemoteEnvelope));
        _registry.Register(_ddataSerializer, typeof(DDataEnvelope));

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
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _buffer.Clear();
    }

    /// <summary>
    /// Legacy pattern: 1-layer envelope.
    /// Serialize inner to byte[], then wrap in outer envelope and serialize to byte[].
    /// Two allocations: one for inner, one for outer.
    /// </summary>
    [Benchmark(Baseline = true)]
    public byte[] Legacy_1Layer_Serialize()
    {
        // Step 1: Serialize inner UserCreated to byte[]
        var innerBuffer = new ArrayBufferWriter<byte>(128);
        using (var innerWriter = MessagePackCodecProvider.Instance.CreateWriter(innerBuffer))
        {
            _userSerializer.Write(innerWriter, _innerMessage);
            innerWriter.Flush();
        }
        var innerBytes = innerBuffer.WrittenSpan.ToArray();

        // Step 2: Serialize outer RemoteEnvelope (embedding innerBytes as binary blob) to byte[]
        _buffer.Clear();
        using (var outerWriter = MessagePackCodecProvider.Instance.CreateWriter(_buffer))
        {
            // Manually write envelope fields + inner bytes as a blob
            outerWriter.BeginObject(5);
            outerWriter.WriteString(_oneLayerEnvelope.RecipientPath);
            outerWriter.WriteString(_oneLayerEnvelope.SenderPath);
            outerWriter.WriteInt32(_userSerializer.Identifier);
            outerWriter.WriteString("user-created-v1");
            outerWriter.WriteBytes(innerBytes); // Inner message as binary blob
            outerWriter.Flush();
        }

        return _buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// V2 pattern: 1-layer envelope.
    /// Single buffer, both envelope and inner write directly via V2.
    /// Zero allocations, zero copies.
    /// </summary>
    [Benchmark]
    public ArrayBufferWriter<byte> V2_1Layer_Serialize()
    {
        using var writer = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        _remoteSerializer.Write(writer, _oneLayerEnvelope);
        writer.Flush();

        return _buffer;
    }

    /// <summary>
    /// Legacy pattern: 3-layer envelope.
    /// Serialize innermost to byte[], wrap in 2nd layer and serialize to byte[],
    /// wrap in 3rd layer and serialize to byte[].
    /// Six allocations total: 3 buffers + 3 ToArray() calls.
    /// </summary>
    [Benchmark]
    public byte[] Legacy_3Layer_Serialize()
    {
        // Layer 1: Serialize innermost UserCreated to byte[]
        var layer1Buffer = new ArrayBufferWriter<byte>(128);
        using (var layer1Writer = MessagePackCodecProvider.Instance.CreateWriter(layer1Buffer))
        {
            _userSerializer.Write(layer1Writer, _innerMessage);
            layer1Writer.Flush();
        }
        var layer1Bytes = layer1Buffer.WrittenSpan.ToArray();

        // Layer 2: Serialize inner RemoteEnvelope (embedding layer1Bytes) to byte[]
        var layer2Buffer = new ArrayBufferWriter<byte>(256);
        using (var layer2Writer = MessagePackCodecProvider.Instance.CreateWriter(layer2Buffer))
        {
            var innerRemote = (RemoteEnvelope)_threeLayerEnvelope.Message;
            var innerDData = (DDataEnvelope)innerRemote.Message;

            // Write inner RemoteEnvelope fields + layer1Bytes
            layer2Writer.BeginObject(5);
            layer2Writer.WriteString("/user/inner-recipient");
            layer2Writer.WriteString("/user/inner-sender");
            layer2Writer.WriteInt32(_userSerializer.Identifier);
            layer2Writer.WriteString("user-created-v1");
            layer2Writer.WriteBytes(layer1Bytes);
            layer2Writer.Flush();
        }
        var layer2Bytes = layer2Buffer.WrittenSpan.ToArray();

        // Layer 3: Serialize DDataEnvelope (embedding layer2Bytes) to byte[]
        var layer3Buffer = new ArrayBufferWriter<byte>(512);
        using (var layer3Writer = MessagePackCodecProvider.Instance.CreateWriter(layer3Buffer))
        {
            var innerRemote = (RemoteEnvelope)_threeLayerEnvelope.Message;
            var ddataEnvelope = (DDataEnvelope)innerRemote.Message;

            // Write DDataEnvelope fields + layer2Bytes
            layer3Writer.BeginObject(5);
            layer3Writer.WriteString(ddataEnvelope.Key);
            layer3Writer.WriteInt64(ddataEnvelope.Version);
            layer3Writer.WriteInt32(_remoteSerializer.Identifier);
            layer3Writer.WriteString("remote-envelope-v1");
            layer3Writer.WriteBytes(layer2Bytes);
            layer3Writer.Flush();
        }
        var layer3Bytes = layer3Buffer.WrittenSpan.ToArray();

        // Layer 4 (outermost): Serialize outer RemoteEnvelope (embedding layer3Bytes) to byte[]
        _buffer.Clear();
        using (var outerWriter = MessagePackCodecProvider.Instance.CreateWriter(_buffer))
        {
            // Write outer RemoteEnvelope fields + layer3Bytes
            outerWriter.BeginObject(5);
            outerWriter.WriteString(_threeLayerEnvelope.RecipientPath);
            outerWriter.WriteString(_threeLayerEnvelope.SenderPath);
            outerWriter.WriteInt32(_ddataSerializer.Identifier);
            outerWriter.WriteString("ddata-envelope-v1");
            outerWriter.WriteBytes(layer3Bytes);
            outerWriter.Flush();
        }

        return _buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// V2 pattern: 3-layer envelope.
    /// Single buffer for all 3 layers via V2.
    /// Zero allocations, zero copies - all layers write to the same buffer.
    /// </summary>
    [Benchmark]
    public ArrayBufferWriter<byte> V2_3Layer_Serialize()
    {
        using var writer = MessagePackCodecProvider.Instance.CreateWriter(_buffer);
        _remoteSerializer.Write(writer, _threeLayerEnvelope);
        writer.Flush();

        return _buffer;
    }
}
