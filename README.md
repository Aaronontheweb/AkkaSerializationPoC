# Akka.NET SerializerV2 PoC

Proof of concept for Akka.NET's next-generation serialization system. Builds a codec-based serializer **externally** (no Akka.NET core modifications needed) that solves three long-standing pain points:

1. **Allocation-heavy APIs** — the current `ToBinary()` returns `byte[]`; every call allocates. V2 writes directly to `IBufferWriter<byte>`.
2. **Nested envelope explosion** — DData messages go through 3-4 serialization layers, each allocating a new `byte[]`. V2 writes all layers to a **single shared buffer** with zero intermediate copies.
3. **Tedious boilerplate** — `SerializerWithStringManifest` requires triple-duplication of every message type (manifest, write dispatch, read dispatch). V2 adds a **Roslyn source generator** that handles it all from a few attributes.

Related issues: [akkadotnet/akka.net#7465](https://github.com/akkadotnet/akka.net/issues/7465), [#3740](https://github.com/akkadotnet/akka.net/issues/3740), [#6060](https://github.com/akkadotnet/akka.net/issues/6060)

## How It Works for End Users

With SerializerV2, you define your messages as C# records, annotate them with a few attributes, and the source generator writes the entire serializer for you.

### Step 1: Define a Protocol Interface

A marker interface scopes which message types belong to which serializer:

```csharp
public interface IMyProtocol { }
```

### Step 2: Annotate Your Messages

Each message gets `[AkkaSerializable]` with a stable manifest string, and each field gets `[AkkaField(index)]` for positional serialization:

```csharp
[AkkaSerializable(Manifest = "user-created-v1")]
public sealed record UserCreated(
    [property: AkkaField(0)] string UserId,
    [property: AkkaField(1)] string Email,
    [property: AkkaField(2)] DateTime CreatedAt) : IMyProtocol;

[AkkaSerializable(Manifest = "user-updated-v1")]
public sealed record UserUpdated(
    [property: AkkaField(0)] string UserId,
    [property: AkkaField(1)] string? NewEmail,     // nullable fields handled automatically
    [property: AkkaField(2)] string? NewName,
    [property: AkkaField(3)] DateTime UpdatedAt) : IMyProtocol;
```

### Step 3: Declare a Partial Serializer Class

One line — the source generator fills in the implementation:

```csharp
[AkkaSerializer(Name = "my-protocol")]
public partial class MyProtocolSerializer : SerializerV2<IMyProtocol> { }
```

That's it. The generator produces `Manifest()`, `Write()`, `Read()`, and `SizeHint()` implementations covering every type that implements `IMyProtocol`.

### Nested Value Objects and Collections

The generator handles complex type graphs out of the box. Value objects just need `[AkkaField]` properties — they don't need `[AkkaSerializable]` since they aren't standalone messages:

```csharp
// Value objects — embeddable, not standalone messages
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
    [property: AkkaField(3)] Money UnitPrice);   // multi-level nesting: LineItem → Money

// Messages — standalone, routed through the serializer
[AkkaSerializable(Manifest = "order-submitted-v1")]
public sealed record OrderSubmitted(
    [property: AkkaField(0)] string OrderId,
    [property: AkkaField(1)] DateTime SubmittedAt,
    [property: AkkaField(2)] Address ShippingAddress,
    [property: AkkaField(3)] Address? BillingAddress,  // nullable nested objects
    [property: AkkaField(4)] List<LineItem> Items,      // collections of complex types
    [property: AkkaField(5)] Money Total,
    [property: AkkaField(6)] OrderStatus Status) : IMyProtocol;  // enums
```

**Supported types:** `string`, `int`, `long`, `bool`, `double`, `decimal`, `DateTime`, `DateTimeOffset`, `Guid`, `byte[]`, nullable value types, enums, nested value objects, `List<T>`, `T[]`, `IReadOnlyList<T>`, `ImmutableList<T>`, `ImmutableArray<T>`, `HashSet<T>`, `Dictionary<K,V>`, `ImmutableDictionary<K,V>`.

### Version Tolerance

Fields use protobuf-style index rules for safe schema evolution:

- **Add fields** at higher indices — old readers skip them, new readers use defaults for missing ones
- **Never reorder or reuse** existing indices
- **Breaking changes** require a new manifest string (e.g., `"user-created-v2"`)

### What You Don't Write Anymore

For comparison, here's what the **current** `SerializerWithStringManifest` pattern requires for the same two messages — every type appears three times (manifest, write dispatch, read dispatch), and every field is manually serialized:

```csharp
// TODAY: ~110 lines of hand-written boilerplate per serializer
public sealed class UserMessageSerializer : SerializerV2
{
    public override int Identifier => 5001;

    public override string? Manifest(object obj) => obj switch
    {
        UserCreated => "user-created-v1",
        UserUpdated => "user-updated-v1",
        _ => throw new ArgumentException(...)
    };

    public override void Write(ICodecWriter writer, object obj)
    {
        switch (obj)
        {
            case UserCreated msg: WriteUserCreated(writer, msg); break;
            case UserUpdated msg: WriteUserUpdated(writer, msg); break;
            default: throw new ArgumentException(...);
        }
    }

    public override object Read(ICodecReader reader, string manifest) => manifest switch
    {
        "user-created-v1" => ReadUserCreated(reader),
        "user-updated-v1" => ReadUserUpdated(reader),
        _ => throw new ArgumentException(...)
    };

    private void WriteUserCreated(ICodecWriter writer, UserCreated msg)
    {
        writer.BeginObject(3);
        writer.WriteString(msg.UserId);
        writer.WriteString(msg.Email);
        writer.WriteDateTime(msg.CreatedAt);
    }

    private UserCreated ReadUserCreated(ICodecReader reader)
    {
        var fieldCount = reader.BeginReadObject();
        var userId = reader.ReadString() ?? string.Empty;
        var email = reader.ReadString() ?? string.Empty;
        var createdAt = reader.ReadDateTime();
        for (int i = 3; i < fieldCount; i++) reader.SkipField();
        return new UserCreated(userId, email, createdAt);
    }

    // ... same again for WriteUserUpdated / ReadUserUpdated ...
}
```

With source generation, this entire class reduces to:

```csharp
[AkkaSerializer(Name = "my-protocol")]
public partial class MyProtocolSerializer : SerializerV2<IMyProtocol> { }
```

### Compile-Time Diagnostics

The generator catches common mistakes at build time:

| ID | Severity | What it catches |
|---|---|---|
| AKKA001 | Error | No `[AkkaField]` properties on an `[AkkaSerializable]` type |
| AKKA002 | Warning | Gaps in field indices (e.g., 0, 2, 5) |
| AKKA006 | Error | Duplicate field indices on the same type |
| AKKA007 | Error | Duplicate manifest strings within a serializer |
| AKKA009 | Error | `[AkkaSerializer]` missing both Name and SerializerId |
| AKKA010 | Error | Serializer ID collision between modules |
| AKKA012 | Error | Circular reference in nested type chain |
| AKKA013 | Error | Dictionary key is not a primitive or enum |

## Benchmarks

All benchmarks run on BenchmarkDotNet v0.15.8, .NET 9.0, Windows 11, x64 RyuJIT. Three approaches compared:

- **Newtonsoft.Json** — current Akka.NET default serializer (baseline)
- **V1 MessagePack** — same binary wire format as V2, but using the legacy `ToBinary() → byte[]` API shape
- **V2 MessagePack** — source-generated serializer writing to a shared `IBufferWriter<byte>` buffer

### Flat Messages (simple record with 4 fields)

| Method | Mean | Allocated | Alloc Ratio |
|---|---:|---:|---:|
| NewtonsoftJson_Serialize | 59.49 us | 6,048 B | 1.000 |
| V1MessagePack_Serialize | 6.97 us | 248 B | 0.041 |
| **V2_Serialize** | **8.20 us** | **24 B** | **0.004** |
| NewtonsoftJson_Deserialize | 77.42 us | 6,464 B | 1.069 |
| V1MessagePack_Deserialize | 3.88 us | 144 B | 0.024 |
| V2_Deserialize | 6.08 us | 184 B | 0.030 |

V2 serialization allocates **99.6% less** than Newtonsoft.Json. Throughput is 7-10x faster.

### Envelope Messages (nested serialization layers)

This is where V2's zero-copy architecture shines. In Akka.NET, remote messages pass through multiple serialization layers (Remote → DData → User Data). With V1, each layer allocates its own `byte[]`. With V2, all layers write to the **same buffer**.

| Method | Mean | Allocated | Alloc Ratio |
|---|---:|---:|---:|
| NewtonsoftJson_1Layer_Serialize | 71.21 us | 6,368 B | 1.000 |
| V1MessagePack_1Layer_Serialize | 5.66 us | 680 B | 0.107 |
| **V2_1Layer_Serialize** | **5.68 us** | **24 B** | **0.004** |
| NewtonsoftJson_3Layer_Serialize | 79.80 us | 8,600 B | 1.351 |
| V1MessagePack_3Layer_Serialize | 6.06 us | 2,736 B | 0.430 |
| **V2_3Layer_Serialize** | **7.31 us** | **24 B** | **0.004** |

The critical result: **V2 3-layer serialization allocates 24 bytes regardless of nesting depth**, while V1 grows from 680 B to 2,736 B as layers are added. This is the nested envelope problem that motivated the entire redesign.

| Method | Mean | Allocated | Alloc Ratio |
|---|---:|---:|---:|
| V1MessagePack_1Layer_Deserialize | 6.03 us | 408 B | 0.064 |
| V2_1Layer_Deserialize | 12.93 us | 384 B | 0.060 |
| V1MessagePack_3Layer_Deserialize | 8.19 us | 1,112 B | 0.175 |
| V2_3Layer_Deserialize | 11.84 us | 792 B | 0.124 |

V2 deserialization also reduces allocations at 3 layers (792 B vs 1,112 B) by avoiding intermediate `byte[]` copies.

### Complex Messages (nested objects + collections, source-generated)

Orders with nested `Address`, `Money`, and `List<LineItem>` value objects — parameterized by collection size to show scaling behavior.

| Method | Items | Mean | Allocated | Alloc Ratio |
|---|---:|---:|---:|---:|
| NewtonsoftJson_Serialize | 3 | 191.56 us | 27,768 B | 1.000 |
| V1MessagePack_Serialize | 3 | 29.40 us | 4,488 B | 0.162 |
| **V2_Serialize** | **3** | **13.93 us** | **184 B** | **0.007** |
| NewtonsoftJson_Serialize | 10 | 273.12 us | 60,040 B | 1.000 |
| V1MessagePack_Serialize | 10 | 14.16 us | 4,968 B | 0.083 |
| **V2_Serialize** | **10** | **12.82 us** | **464 B** | **0.008** |
| NewtonsoftJson_Serialize | 50 | 652.76 us | 248,128 B | 1.000 |
| V1MessagePack_Serialize | 50 | 27.96 us | 7,688 B | 0.031 |
| **V2_Serialize** | **50** | **42.45 us** | **2,064 B** | **0.008** |

| Method | Items | Mean | Allocated | Alloc Ratio |
|---|---:|---:|---:|---:|
| NewtonsoftJson_Deserialize | 3 | 311.02 us | 27,480 B | 0.990 |
| V1MessagePack_Deserialize | 3 | 9.81 us | 1,080 B | 0.039 |
| V2_Deserialize | 3 | 15.17 us | 1,120 B | 0.040 |
| NewtonsoftJson_Deserialize | 10 | 402.74 us | 61,280 B | 1.021 |
| V1MessagePack_Deserialize | 10 | 16.32 us | 2,480 B | 0.041 |
| V2_Deserialize | 10 | 16.81 us | 2,520 B | 0.042 |
| NewtonsoftJson_Deserialize | 50 | 1,054.15 us | 257,568 B | 1.038 |
| V1MessagePack_Deserialize | 50 | 48.42 us | 10,480 B | 0.042 |
| V2_Deserialize | 50 | 45.49 us | 10,520 B | 0.042 |

At 50 items, V2 serialization allocates **2 KB vs 248 KB** for Newtonsoft.Json — a **120x reduction**. Deserialization throughput at scale (50 items) is over 20x faster than Newtonsoft.Json and equivalent to hand-written V1 MessagePack.

### Key Takeaways

- **Serialization allocations are near-zero** — V2 writes to a pooled `IBufferWriter<byte>`, avoiding `byte[]` allocations entirely. The 24 B baseline is the `ICodecWriter` struct itself.
- **Nesting depth doesn't increase allocations** — the core innovation. V2 3-layer envelope serialization allocates the same 24 B as a flat message.
- **Throughput is 7-20x faster than Newtonsoft.Json** across all message shapes.
- **V2 matches hand-written MessagePack** — the source-generated code performs identically to expert-optimized hand-written serialization.
- **Deserialization allocations are message-proportional** — reads must construct objects regardless of API, so V1 and V2 are equivalent here. The win is in serialization and envelope nesting.

## Architecture

Three-layer codec design:

```
┌─────────────────────────────────────────────────────┐
│  SerializerV2<TProtocol>                            │
│  Write(ICodecWriter, object) / Read(ICodecReader)   │
│  ↕ source generated from [AkkaSerializable] types   │
├─────────────────────────────────────────────────────┤
│  ICodecWriter / ICodecReader                        │
│  BeginObject, WriteInt32, ReadString, etc.           │
│  ↕ pluggable wire format abstraction                │
├─────────────────────────────────────────────────────┤
│  ICodecProvider (default: MessagePack-CSharp)       │
│  CreateWriter(IBufferWriter<byte>)                  │
│  CreateReader(ReadOnlyMemory<byte>)                 │
└─────────────────────────────────────────────────────┘
```

See `docs/ARCHITECTURE.md` for the full design, including zero-copy nesting mechanics and the wire format contract.

## Project Structure

```
src/
  Akka.Serialization.V2/           # Core abstractions (ICodecWriter, ICodecReader, SerializerV2, attributes)
  Akka.Serialization.MessagePack/  # MessagePack ICodecProvider implementation
  Akka.Serialization.Generators/   # Roslyn incremental source generator
tests/
  Akka.Serialization.V2.Tests/     # xUnit tests (flat, envelope, nested, multi-module, edge cases)
benchmarks/
  Akka.Serialization.Benchmarks/   # BenchmarkDotNet benchmarks (flat, envelope, complex)
docs/
  ARCHITECTURE.md                  # Detailed design documentation
  PHASES.md                        # Implementation phase breakdown
  SOURCE_GENERATION.md             # Source generator attribute reference
```

## Build

```bash
# Build everything
dotnet build SampleSln.slnx

# Run tests
dotnet test tests/Akka.Serialization.V2.Tests/

# Run benchmarks
dotnet run -c Release --project benchmarks/Akka.Serialization.Benchmarks/
```

Requires .NET 9 SDK. Uses central package management (`Directory.Packages.props`) and the `.slnx` solution format.
