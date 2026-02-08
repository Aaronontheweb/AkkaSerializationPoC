# Source Generation Guide

The Akka.Serialization.Generators package provides a Roslyn incremental source generator that produces `SerializerV2` implementations from annotated types — eliminating the boilerplate of hand-writing `Write`/`Read`/`Manifest` methods.

## Quick Start

1. Add attributes to your message types:

```csharp
using Akka.Serialization.V2;

[AkkaSerializable(Manifest = "user-created-v1")]
public sealed record UserCreated(
    [property: AkkaField(0)] string UserId,
    [property: AkkaField(1)] string Email,
    [property: AkkaField(2)] DateTime CreatedAt);
```

2. Create a serializer module (partial class):

```csharp
[AkkaSerializerModule(SerializerId = 5001)]
public partial class MySerializer { }
```

3. The generator produces the full `SerializerV2` implementation — `Write`, `Read`, `Manifest`, and `SizeHint` — plus a `MySerializerSetup` metadata class.

## Attributes

### `[AkkaSerializable(Manifest = "...")]`

Marks a type for serialization. Applied to `record`, `class`, or `struct` types.

- **Manifest** (required): A stable string identifier for this type on the wire. Once published, never change it — changing a manifest is a breaking wire format change.
- Manifests should be versioned (e.g., `"user-created-v1"`) to allow future schema evolution.

### `[AkkaField(int index)]`

Marks a property for serialization and assigns its positional index.

- **Index** (required): Zero-based field position in the serialized array. Fields are written/read in index order.
- Applied via `[property: AkkaField(N)]` on record constructor parameters, or directly on properties.

### `[AkkaSerializerModule(SerializerId = N)]`

Marks a partial class as the target for the generated serializer implementation.

- **SerializerId** (required): Unique integer identifier, matching the Akka.NET serializer configuration.
- The class must be `partial` — the generator fills in the implementation.
- All `[AkkaSerializable]` types in the same compilation are included in this serializer.

## Field Ordering Rules

Fields are serialized **by their `[AkkaField(N)]` index**, not by declaration order. This is critical for wire compatibility:

```csharp
// These produce IDENTICAL wire format:

// Declaration order matches index order
public sealed record Msg(
    [property: AkkaField(0)] string A,
    [property: AkkaField(1)] string B);

// Declaration order differs from index order — still writes A first, then B
public sealed record Msg(
    [property: AkkaField(1)] string B,
    [property: AkkaField(0)] string A);
```

### Rules:
- Indices must start at 0 and be contiguous (0, 1, 2, ...). Gaps produce a warning (AKKA002).
- Duplicate indices on the same type produce an error (AKKA006).
- Once a field index is assigned and data is published, **never change it**.

## Adding and Removing Fields

The generated deserializer uses `fieldCount`-guarded reads, enabling forward and backward compatibility:

### Adding a new field (backward compatible)

Add the field at the next available index. Old data with fewer fields will use the type's default value:

```csharp
// V1: 2 fields
[AkkaSerializable(Manifest = "user-created-v1")]
public sealed record UserCreated(
    [property: AkkaField(0)] string UserId,
    [property: AkkaField(1)] string Email);

// V2: 3 fields — old data still deserializes correctly
[AkkaSerializable(Manifest = "user-created-v1")]
public sealed record UserCreated(
    [property: AkkaField(0)] string UserId,
    [property: AkkaField(1)] string Email,
    [property: AkkaField(2)] DateTime CreatedAt);  // defaults to default(DateTime) for old data
```

**Keep the same manifest** — the version suffix is for documentation, not automatic version routing.

### Removing a field

Never remove a field from the middle. Instead, stop writing it but keep it in the schema to maintain index stability. Or simply leave the field in place — unused fields have minimal overhead.

### Forward compatibility

If a newer serializer writes more fields than the reader knows about, the extra fields are automatically skipped. This means:
- V2 data (4 fields) can be read by V1 reader (expects 3 fields) — the 4th field is skipped.

## Supported Types

| C# Type | Writer/Reader Method | Notes |
|---------|---------------------|-------|
| `string` | `WriteString`/`ReadString` | Non-nullable defaults to `string.Empty` |
| `string?` | `WriteString`/`ReadString` + null check | Uses `TryReadNull()` pattern |
| `int` | `WriteInt32`/`ReadInt32` | |
| `long` | `WriteInt64`/`ReadInt64` | |
| `bool` | `WriteBool`/`ReadBool` | |
| `double` | `WriteDouble`/`ReadDouble` | |
| `decimal` | `WriteDecimal`/`ReadDecimal` | Lossless via `decimal.GetBits()` 4-int encoding |
| `DateTime` | `WriteDateTime`/`ReadDateTime` | |
| `DateTimeOffset` | `WriteDateTimeOffset`/`ReadDateTimeOffset` | Preserves offset |
| `Guid` | `WriteGuid`/`ReadGuid` | |
| `byte[]` | `WriteBytes`/`ReadBytes` | |

Unsupported types (collections, custom classes, etc.) produce a compile-time error (AKKA003).

## Diagnostics

The generator reports compile-time diagnostics:

| ID | Severity | Description |
|----|----------|-------------|
| AKKA001 | Error | `[AkkaSerializable]` type has zero `[AkkaField]` properties |
| AKKA002 | Warning | Field indices have gaps (e.g., 0, 2, 5 — missing 1) |
| AKKA003 | Error | Property has an unsupported type |
| AKKA004 | Warning | `[AkkaSerializable]` types exist but no `[AkkaSerializerModule]` was found |
| AKKA006 | Error | Duplicate field indices on the same type |
| AKKA007 | Error | Duplicate manifests across types in the same module |

## Generated Code

For a module `MySerializer` with types `UserCreated` and `OrderPlaced`, the generator produces:

### Serializer class

```csharp
public partial class MySerializer : SerializerV2
{
    public override int Identifier => 5001;

    public override string? Manifest(object obj) => obj switch
    {
        UserCreated => "user-created-v1",
        OrderPlaced => "order-placed-v1",
        _ => throw new ArgumentException(...)
    };

    public override void Write(ICodecWriter writer, object obj) { /* switch dispatch */ }
    public override object Read(ICodecReader reader, string manifest) { /* manifest dispatch */ }

    // Per-type methods with fieldCount guards for backward compat
    private void WriteUserCreated(ICodecWriter writer, UserCreated msg) { ... }
    private UserCreated ReadUserCreated(ICodecReader reader) { ... }
}
```

### Setup class

Always generated alongside the serializer:

```csharp
public sealed class MySerializerSetup
{
    public static readonly MySerializerSetup Instance = new();
    public Type SerializerType => typeof(MySerializer);
    public static Type[] BoundTypes => new Type[] { typeof(UserCreated), typeof(OrderPlaced) };
}
```

## Registration

### Manual registration with SerializerRegistry

```csharp
var registry = new SerializerRegistry();
var serializer = new MySerializer();
registry.Register(serializer, MySerializerSetup.BoundTypes);
```

### With ActorSystem (for ActorRef resolution)

```csharp
var system = ActorSystem.Create("my-system");
var registry = new SerializerRegistry((ExtendedActorSystem)system);
var serializer = new MySerializer();
registry.Register(serializer, MySerializerSetup.BoundTypes);
// serializer.System is now set, enabling ActorRef path resolution
```

## Migration Strategy (Legacy to V2)

1. **Wrap existing serializers**: Use `SerializerV2Adapter` to wrap legacy `Serializer`/`SerializerWithStringManifest` implementations. This lets legacy-serialized messages live inside V2 envelopes immediately.

2. **Add V2 serializers alongside legacy**: Register new V2 serializers for new message types. Old messages continue using the adapter.

3. **Migrate incrementally**: Convert message types one at a time from legacy to V2. The `SerializerRegistry` routes each type to the correct serializer.

4. **Byte-identity verification**: The generator produces identical wire format to hand-written serializers. Use byte-identity tests to verify migration correctness.

## Nested Types

The generator handles types nested inside other classes:

```csharp
public class MyActor
{
    [AkkaSerializable(Manifest = "create-user-v1")]
    public sealed record CreateUser(
        [property: AkkaField(0)] string Name);
}
```

The generated code uses the fully qualified name `MyActor.CreateUser` in all references.
