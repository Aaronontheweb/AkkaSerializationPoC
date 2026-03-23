using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Akka.Serialization.Generators;

/// <summary>
/// Roslyn incremental source generator that produces SerializerV2 implementations
/// for types annotated with [AkkaSerializer] that extend SerializerV2&lt;TProtocol&gt;.
/// Only [AkkaSerializable] types that implement the module's TProtocol interface are included.
/// </summary>
[Generator]
public class AkkaSerializerGenerator : IIncrementalGenerator
{
    private const string SerializerAttributeFullName = "Akka.Serialization.V2.AkkaSerializerAttribute";
    private const string SerializableAttributeFullName = "Akka.Serialization.V2.AkkaSerializableAttribute";
    private const string FieldAttributeFullName = "Akka.Serialization.V2.AkkaFieldAttribute";

    // Diagnostic descriptors
    private static readonly DiagnosticDescriptor NoFieldsDiagnostic = new DiagnosticDescriptor(
        "AKKA001", "No fields defined",
        "[AkkaSerializable] type '{0}' has zero [AkkaField] properties",
        "Akka.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GappedIndicesDiagnostic = new DiagnosticDescriptor(
        "AKKA002", "Field index gap",
        "[AkkaSerializable] type '{0}' has gaps in field indices (found: {1})",
        "Akka.Serialization", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedTypeDiagnostic = new DiagnosticDescriptor(
        "AKKA003", "Unsupported property type",
        "Property '{0}' on type '{1}' has unsupported type '{2}'",
        "Akka.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoModuleDiagnostic = new DiagnosticDescriptor(
        "AKKA004", "No serializer module",
        "[AkkaSerializable] types exist but no [AkkaSerializer] class was found",
        "Akka.Serialization", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateFieldIndexDiagnostic = new DiagnosticDescriptor(
        "AKKA006", "Duplicate field index",
        "[AkkaSerializable] type '{0}' has duplicate field index {1}",
        "Akka.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateManifestDiagnostic = new DiagnosticDescriptor(
        "AKKA007", "Duplicate manifest",
        "Multiple [AkkaSerializable] types share manifest '{0}': {1}",
        "Akka.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor OrphanedTypeDiagnostic = new DiagnosticDescriptor(
        "AKKA008", "Orphaned serializable type",
        "[AkkaSerializable] type '{0}' does not implement any protocol interface used by an [AkkaSerializer] module",
        "Akka.Serialization", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoNameOrIdDiagnostic = new DiagnosticDescriptor(
        "AKKA009", "No Name or SerializerId",
        "[AkkaSerializer] class '{0}' must specify either Name or SerializerId",
        "Akka.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor IdCollisionDiagnostic = new DiagnosticDescriptor(
        "AKKA010", "Serializer ID collision",
        "Serializer ID {0} is used by multiple [AkkaSerializer] classes: {1}",
        "Akka.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ComputedIdInfoDiagnostic = new DiagnosticDescriptor(
        "AKKA011", "Computed serializer ID",
        "[AkkaSerializer] class '{0}' with Name '{1}' has computed serializer ID {2}",
        "Akka.Serialization", DiagnosticSeverity.Info, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor CircularReferenceDiagnostic = new DiagnosticDescriptor(
        "AKKA012", "Circular reference in nested types",
        "Type '{0}' has a circular reference through nested type chain",
        "Akka.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DictKeyUnsupportedDiagnostic = new DiagnosticDescriptor(
        "AKKA013", "Dictionary key must be primitive or enum",
        "Dictionary key type '{0}' on property '{1}' of type '{2}' must be a primitive type or enum",
        "Akka.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingAkkaFieldsDiagnostic = new DiagnosticDescriptor(
        "AKKA014", "Nested type missing [AkkaField]",
        "Type '{0}' is used as a field on '{1}' but has no [AkkaField] properties. Add [AkkaField] attributes to its properties to enable nested serialization.",
        "Akka.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Step 1: Find all types with [AkkaSerializer]
        var moduleProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SerializerAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => ExtractModuleInfo(ctx, ct))
            .Where(static m => m != null);

        // Step 2: Find all types with [AkkaSerializable]
        var serializableProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SerializableAttributeFullName,
                predicate: static (node, _) => node is ClassDeclarationSyntax
                    || node is StructDeclarationSyntax
                    || node is RecordDeclarationSyntax,
                transform: static (ctx, ct) => ExtractSerializableInfo(ctx, ct))
            .Where(static s => s != null);

        // Step 3: Collect all serializable types into an array
        var serializableCollection = serializableProvider.Collect();

        // Step 4: Combine each module with all serializable types
        var combined = moduleProvider.Combine(serializableCollection);

        // Step 5: Register source output — one per module
        context.RegisterSourceOutput(combined, static (spc, pair) =>
        {
            var module = pair.Left;
            var allSerializables = pair.Right;

            if (module == null)
                return;

            // AKKA009: Must have Name or SerializerId
            if (string.IsNullOrEmpty(module.Name) && module.ExplicitSerializerId == 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NoNameOrIdDiagnostic, Location.None, module.ClassName));
                return;
            }

            // AKKA011: Report computed ID for Name-only modules
            if (!string.IsNullOrEmpty(module.Name) && module.ExplicitSerializerId == 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(ComputedIdInfoDiagnostic, Location.None,
                    module.ClassName, module.Name, module.SerializerId));
            }

            // Filter serializable types to only those implementing this module's protocol interface
            var filteredSerializables = FilterByProtocol(allSerializables, module.ProtocolTypeFullName);

            // Validate and report diagnostics on filtered types
            ValidateSerializables(spc, filteredSerializables);
            ValidateManifestUniqueness(spc, filteredSerializables);

            var source = GenerateSerializerCode(module, filteredSerializables);
            var hintName = module.ClassName + ".g.cs";
            spc.AddSource(hintName, source);
        });

        // Step 6: Cross-module validation (AKKA004, AKKA008, AKKA010)
        var moduleCollection = moduleProvider.Collect();
        var crossModuleCheck = serializableCollection.Combine(moduleCollection);
        context.RegisterSourceOutput(crossModuleCheck, static (spc, pair) =>
        {
            var serializables = pair.Left;
            var modules = pair.Right;

            // AKKA004: No modules found
            if (serializables.Length > 0 && modules.Length == 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NoModuleDiagnostic, Location.None));
            }

            // AKKA008: Orphaned types — not implemented by any module's protocol
            if (modules.Length > 0)
            {
                var protocolTypes = new HashSet<string>();
                for (int i = 0; i < modules.Length; i++)
                {
                    if (modules[i] != null && !string.IsNullOrEmpty(modules[i].ProtocolTypeFullName))
                        protocolTypes.Add(modules[i].ProtocolTypeFullName);
                }

                for (int i = 0; i < serializables.Length; i++)
                {
                    var s = serializables[i];
                    if (s == null) continue;

                    bool matched = false;
                    foreach (var proto in protocolTypes)
                    {
                        if (s.ImplementsInterface(proto))
                        {
                            matched = true;
                            break;
                        }
                    }

                    if (!matched)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(OrphanedTypeDiagnostic, Location.None, s.SimpleName));
                    }
                }
            }

            // AKKA010: ID collisions across modules
            if (modules.Length > 1)
            {
                var idMap = new Dictionary<int, List<string>>();
                for (int i = 0; i < modules.Length; i++)
                {
                    var m = modules[i];
                    if (m == null) continue;
                    if (!idMap.TryGetValue(m.SerializerId, out var list))
                    {
                        list = new List<string>();
                        idMap[m.SerializerId] = list;
                    }
                    list.Add(m.ClassName);
                }

                foreach (var kvp in idMap)
                {
                    if (kvp.Value.Count > 1)
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(IdCollisionDiagnostic, Location.None,
                            kvp.Key, string.Join(", ", kvp.Value)));
                    }
                }
            }
        });
    }

    private static ImmutableArray<SerializableTypeInfo> FilterByProtocol(
        ImmutableArray<SerializableTypeInfo> allSerializables,
        string protocolTypeFullName)
    {
        if (string.IsNullOrEmpty(protocolTypeFullName))
            return ImmutableArray<SerializableTypeInfo>.Empty;

        var builder = ImmutableArray.CreateBuilder<SerializableTypeInfo>();
        for (int i = 0; i < allSerializables.Length; i++)
        {
            var s = allSerializables[i];
            if (s != null && s.ImplementsInterface(protocolTypeFullName))
            {
                builder.Add(s);
            }
        }
        return builder.ToImmutable();
    }

    private static void ValidateSerializables(
        SourceProductionContext spc,
        ImmutableArray<SerializableTypeInfo> serializables)
    {
        for (int i = 0; i < serializables.Length; i++)
        {
            var s = serializables[i];
            if (s == null) continue;

            // AKKA001: No fields
            if (s.Fields.Length == 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NoFieldsDiagnostic, Location.None, s.SimpleName));
            }

            // AKKA006: Duplicate field indices
            var indexSet = new HashSet<int>();
            foreach (var field in s.Fields)
            {
                if (!indexSet.Add(field.Index))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(DuplicateFieldIndexDiagnostic, Location.None,
                        s.SimpleName, field.Index));
                }
            }

            // AKKA002: Gapped indices
            if (s.Fields.Length > 0)
            {
                var indices = new List<int>();
                foreach (var f in s.Fields)
                    indices.Add(f.Index);
                indices.Sort();
                bool hasGap = false;
                for (int j = 0; j < indices.Count; j++)
                {
                    if (indices[j] != j)
                    {
                        hasGap = true;
                        break;
                    }
                }
                if (hasGap)
                {
                    var indicesStr = string.Join(", ", indices);
                    spc.ReportDiagnostic(Diagnostic.Create(GappedIndicesDiagnostic, Location.None,
                        s.SimpleName, indicesStr));
                }
            }

            // AKKA003: Unsupported types
            foreach (var field in s.Fields)
            {
                if (field.TypeMapping.WriteMethod == "/* UNSUPPORTED */")
                {
                    spc.ReportDiagnostic(Diagnostic.Create(UnsupportedTypeDiagnostic, Location.None,
                        field.PropertyName, s.SimpleName, field.TypeMapping.CSharpType));
                }
            }

            // AKKA012: Circular references
            ValidateFieldsDeeply(spc, s.SimpleName, s.Fields);
        }
    }

    private static void ValidateFieldsDeeply(SourceProductionContext spc, string parentTypeName, FieldInfo[] fields)
    {
        foreach (var field in fields)
        {
            ValidateTypeMappingDeeply(spc, parentTypeName, field.PropertyName, field.TypeMapping);
        }
    }

    private static void ValidateTypeMappingDeeply(SourceProductionContext spc, string parentTypeName, string propertyName, TypeMapping mapping)
    {
        if (mapping.WriteMethod == "/* CIRCULAR */")
        {
            spc.ReportDiagnostic(Diagnostic.Create(CircularReferenceDiagnostic, Location.None, mapping.CSharpType));
        }
        else if (mapping.WriteMethod == "/* MISSING_AKKA_FIELDS */")
        {
            spc.ReportDiagnostic(Diagnostic.Create(MissingAkkaFieldsDiagnostic, Location.None,
                mapping.CSharpType, parentTypeName));
        }

        // Check collection element/key/value types
        if (mapping.Kind == TypeMappingKind.Collection && mapping.Collection != null)
        {
            var coll = mapping.Collection;
            if (coll.Kind == CollectionKind.Dictionary || coll.Kind == CollectionKind.ImmutableDictionary)
            {
                // AKKA013: Dictionary key must be primitive or enum
                if (coll.KeyMapping != null
                    && coll.KeyMapping.Kind != TypeMappingKind.Primitive
                    && coll.KeyMapping.Kind != TypeMappingKind.Enum)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(DictKeyUnsupportedDiagnostic, Location.None,
                        coll.KeyTypeFullName, propertyName, parentTypeName));
                }
                if (coll.ValueMapping != null)
                {
                    ValidateTypeMappingDeeply(spc, parentTypeName, propertyName, coll.ValueMapping);
                }
            }
            else
            {
                if (coll.ElementMapping != null)
                {
                    ValidateTypeMappingDeeply(spc, parentTypeName, propertyName, coll.ElementMapping);
                }
            }
        }

        // Check nullable value inner type
        if (mapping.Kind == TypeMappingKind.NullableValue && mapping.InnerMapping != null)
        {
            ValidateTypeMappingDeeply(spc, parentTypeName, propertyName, mapping.InnerMapping);
        }

        // Check nested object fields
        if (mapping.Kind == TypeMappingKind.NestedObject && mapping.NestedType != null)
        {
            ValidateFieldsDeeply(spc, mapping.NestedType.SimpleName, mapping.NestedType.Fields);
        }
    }

    private static void ValidateManifestUniqueness(
        SourceProductionContext spc,
        ImmutableArray<SerializableTypeInfo> serializables)
    {
        // AKKA007: Duplicate manifests within this module's scope
        var manifestMap = new Dictionary<string, List<string>>();
        for (int i = 0; i < serializables.Length; i++)
        {
            var s = serializables[i];
            if (s == null || string.IsNullOrEmpty(s.Manifest)) continue;

            if (!manifestMap.TryGetValue(s.Manifest, out var list))
            {
                list = new List<string>();
                manifestMap[s.Manifest] = list;
            }
            list.Add(s.SimpleName);
        }

        foreach (var kvp in manifestMap)
        {
            if (kvp.Value.Count > 1)
            {
                spc.ReportDiagnostic(Diagnostic.Create(DuplicateManifestDiagnostic, Location.None,
                    kvp.Key, string.Join(", ", kvp.Value)));
            }
        }
    }

    private static ModuleInfo ExtractModuleInfo(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;

        // Extract Name and SerializerId from the attribute
        var attr = context.Attributes[0];
        string name = null;
        int explicitSerializerId = 0;

        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "Name" && named.Value.Value is string n)
            {
                name = n;
            }
            else if (named.Key == "SerializerId" && named.Value.Value is int id)
            {
                explicitSerializerId = id;
            }
        }

        // Walk BaseType chain to find SerializerV2<T>
        string protocolTypeFullName = null;
        string protocolTypeSimpleName = null;
        var baseType = symbol.BaseType;
        while (baseType != null)
        {
            var originalDef = baseType.OriginalDefinition;
            if (originalDef.Name == "SerializerV2" && originalDef.Arity == 1
                && GetFullNamespace(originalDef) == "Akka.Serialization.V2")
            {
                var typeArg = baseType.TypeArguments[0];
                protocolTypeFullName = typeArg.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                protocolTypeSimpleName = typeArg.Name;
                break;
            }
            baseType = baseType.BaseType;
        }

        // Get the fully qualified namespace
        var namespaceName = GetFullNamespace(symbol);
        var className = symbol.Name;

        return new ModuleInfo(namespaceName, className, name, protocolTypeFullName, protocolTypeSimpleName, explicitSerializerId);
    }

    private static SerializableTypeInfo ExtractSerializableInfo(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;

        // Extract Manifest from the attribute
        var attr = context.Attributes[0];
        string manifest = "";

        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "Manifest" && named.Value.Value is string m)
            {
                manifest = m;
            }
        }

        // Get the fully qualified type name, handling nested types
        var fullyQualifiedName = GetFullyQualifiedTypeName(symbol);
        // Also get the simple name for method naming
        var simpleName = symbol.Name;

        // Collect all implemented interfaces and base types (fully qualified)
        var implementedTypes = CollectImplementedTypes(symbol);

        // Extract [AkkaField] properties
        var fields = new List<FieldInfo>();
        foreach (var member in symbol.GetMembers())
        {
            if (member is IPropertySymbol prop)
            {
                foreach (var propAttr in prop.GetAttributes())
                {
                    var attrClass = propAttr.AttributeClass;
                    if (attrClass != null && GetFullMetadataName(attrClass) == FieldAttributeFullName)
                    {
                        int index = 0;
                        if (propAttr.ConstructorArguments.Length > 0 &&
                            propAttr.ConstructorArguments[0].Value is int idx)
                        {
                            index = idx;
                        }

                        var typeInfo = GetTypeMapping(prop.Type);
                        fields.Add(new FieldInfo(
                            prop.Name,
                            index,
                            typeInfo));
                    }
                }
            }
        }

        // Sort fields by index
        fields.Sort((a, b) => a.Index.CompareTo(b.Index));

        return new SerializableTypeInfo(fullyQualifiedName, simpleName, manifest, fields.ToArray(), implementedTypes);
    }

    private static string[] CollectImplementedTypes(INamedTypeSymbol symbol)
    {
        var result = new List<string>();

        // All interfaces
        foreach (var iface in symbol.AllInterfaces)
        {
            result.Add(iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }

        // Walk base types
        var current = symbol.BaseType;
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            result.Add(current.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            current = current.BaseType;
        }

        return result.ToArray();
    }

    private static string GetFullyQualifiedTypeName(INamedTypeSymbol symbol)
    {
        // Walk ContainingType chain for nested types (e.g., MyActor.Commands.CreateUser)
        var parts = new List<string>();
        parts.Add(symbol.Name);

        var containingType = symbol.ContainingType;
        while (containingType != null)
        {
            parts.Insert(0, containingType.Name);
            containingType = containingType.ContainingType;
        }

        // Build the namespace prefix
        var nsParts = new List<string>();
        var ns = symbol.ContainingNamespace;
        while (ns != null && !ns.IsGlobalNamespace)
        {
            nsParts.Insert(0, ns.Name);
            ns = ns.ContainingNamespace;
        }

        var nsPrefix = nsParts.Count > 0 ? "global::" + string.Join(".", nsParts) + "." : "global::";
        return nsPrefix + string.Join(".", parts);
    }

    private static TypeMapping GetTypeMapping(ITypeSymbol typeSymbol)
    {
        return GetTypeMapping(typeSymbol, new HashSet<string>());
    }

    private static TypeMapping GetTypeMapping(ITypeSymbol typeSymbol, HashSet<string> visitedTypes)
    {
        // Check for nullable reference type (e.g. string?)
        bool isNullableReference = typeSymbol.NullableAnnotation == NullableAnnotation.Annotated
            && !typeSymbol.IsValueType;

        // Get the underlying type for nullable reference types
        var effectiveType = typeSymbol;
        if (isNullableReference && typeSymbol is INamedTypeSymbol)
        {
            effectiveType = typeSymbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        }

        var specialType = effectiveType.SpecialType;
        var fullName = effectiveType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // === Primitives (unchanged) ===
        if (specialType == SpecialType.System_String || fullName == "global::System.String" || fullName == "string")
        {
            if (isNullableReference)
            {
                return new TypeMapping("string?", "WriteString", "ReadString",
                    isNullableReference: true, nullDefault: "null");
            }
            return new TypeMapping("string", "WriteString", "ReadString() ?? string.Empty",
                isNullableReference: false, nullDefault: "string.Empty",
                readExpression: "ReadString() ?? string.Empty");
        }

        if (specialType == SpecialType.System_Int32)
        {
            return new TypeMapping("int", "WriteInt32", "ReadInt32",
                isNullableReference: false, nullDefault: "0");
        }

        if (specialType == SpecialType.System_Int64)
        {
            return new TypeMapping("long", "WriteInt64", "ReadInt64",
                isNullableReference: false, nullDefault: "0L");
        }

        if (specialType == SpecialType.System_Boolean)
        {
            return new TypeMapping("bool", "WriteBool", "ReadBool",
                isNullableReference: false, nullDefault: "false");
        }

        if (specialType == SpecialType.System_Double)
        {
            return new TypeMapping("double", "WriteDouble", "ReadDouble",
                isNullableReference: false, nullDefault: "0.0");
        }

        if (specialType == SpecialType.System_Decimal)
        {
            return new TypeMapping("decimal", "WriteDecimal", "ReadDecimal",
                isNullableReference: false, nullDefault: "0m");
        }

        if (specialType == SpecialType.System_DateTime || fullName == "global::System.DateTime")
        {
            return new TypeMapping("DateTime", "WriteDateTime", "ReadDateTime",
                isNullableReference: false, nullDefault: "default");
        }

        if (fullName == "global::System.DateTimeOffset")
        {
            return new TypeMapping("DateTimeOffset", "WriteDateTimeOffset", "ReadDateTimeOffset",
                isNullableReference: false, nullDefault: "default");
        }

        if (fullName == "global::System.Guid")
        {
            return new TypeMapping("Guid", "WriteGuid", "ReadGuid",
                isNullableReference: false, nullDefault: "default");
        }

        // byte[] check
        if (typeSymbol is IArrayTypeSymbol byteArrayType &&
            byteArrayType.ElementType.SpecialType == SpecialType.System_Byte)
        {
            return new TypeMapping("byte[]", "WriteBytes", "ReadBytes() ?? global::System.Array.Empty<byte>()",
                isNullableReference: false, nullDefault: "global::System.Array.Empty<byte>()",
                readExpression: "ReadBytes() ?? global::System.Array.Empty<byte>()");
        }

        // === Enums ===
        if (effectiveType.TypeKind == TypeKind.Enum)
        {
            var enumFullName = fullName;
            // Map underlying type to write/read methods
            var namedType = (INamedTypeSymbol)effectiveType;
            var underlyingType = namedType.EnumUnderlyingType;
            string writeMethod = "WriteInt32";
            string readMethod = "ReadInt32";
            if (underlyingType != null)
            {
                switch (underlyingType.SpecialType)
                {
                    case SpecialType.System_Int64:
                        writeMethod = "WriteInt64";
                        readMethod = "ReadInt64";
                        break;
                    // byte, sbyte, short, ushort, uint all fit in Int32
                }
            }
            return new TypeMapping(
                TypeMappingKind.Enum,
                enumFullName, writeMethod, readMethod,
                isNullableReference: isNullableReference,
                nullDefault: "default",
                enumFullName: enumFullName);
        }

        // === Nullable<T> value type ===
        if (effectiveType is INamedTypeSymbol nullableNamedType
            && nullableNamedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            var innerType = nullableNamedType.TypeArguments[0];
            var innerMapping = GetTypeMapping(innerType, visitedTypes);
            var innerFullName = innerType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return new TypeMapping(
                TypeMappingKind.NullableValue,
                fullName, "/* NULLABLE_VALUE */", "/* NULLABLE_VALUE */",
                isNullableReference: false,
                nullDefault: "null",
                innerMapping: innerMapping);
        }

        // === Collections ===
        // T[] (non-byte arrays)
        if (typeSymbol is IArrayTypeSymbol arrayType)
        {
            var elementMapping = GetTypeMapping(arrayType.ElementType, visitedTypes);
            var elementFullName = arrayType.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return new TypeMapping(
                TypeMappingKind.Collection,
                fullName, "/* COLLECTION */", "/* COLLECTION */",
                isNullableReference: isNullableReference,
                nullDefault: isNullableReference ? "null" : "global::System.Array.Empty<" + elementFullName + ">()",
                collection: new CollectionInfo(CollectionKind.Array, elementMapping, elementFullName));
        }

        // Generic collections
        if (effectiveType is INamedTypeSymbol namedTypeForCollection && namedTypeForCollection.IsGenericType)
        {
            var originalDef = namedTypeForCollection.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var collectionResult = TryMapCollection(originalDef, namedTypeForCollection, isNullableReference, fullName, visitedTypes);
            if (collectionResult != null)
                return collectionResult;
        }

        // === Nested objects ===
        if (effectiveType is INamedTypeSymbol namedTypeForNested
            && (namedTypeForNested.TypeKind == TypeKind.Class || namedTypeForNested.TypeKind == TypeKind.Struct))
        {
            // Check for circular reference
            if (visitedTypes.Contains(fullName))
            {
                return new TypeMapping(fullName, "/* CIRCULAR */", "/* CIRCULAR */",
                    isNullableReference: isNullableReference, nullDefault: "default");
            }

            // Check if type has [AkkaField] properties
            var nestedFields = ExtractAkkaFields(namedTypeForNested, visitedTypes);
            if (nestedFields.Count > 0)
            {
                // Check if type also has [AkkaSerializable]
                bool isAkkaSerializable = HasAttribute(namedTypeForNested, SerializableAttributeFullName);
                var nestedSimpleName = namedTypeForNested.Name;
                var nestedInfo = new NestedTypeInfo(fullName, nestedSimpleName, nestedFields.ToArray(), isAkkaSerializable);

                return new TypeMapping(
                    TypeMappingKind.NestedObject,
                    fullName, "/* NESTED */", "/* NESTED */",
                    isNullableReference: isNullableReference,
                    nullDefault: isNullableReference ? "null" : "default",
                    nestedType: nestedInfo);
            }

            // Type is a non-primitive, non-enum, non-collection class/struct with no [AkkaField] properties
            if (!effectiveType.IsAbstract && effectiveType.SpecialType == SpecialType.None)
            {
                return new TypeMapping(fullName, "/* MISSING_AKKA_FIELDS */", "/* MISSING_AKKA_FIELDS */",
                    isNullableReference: isNullableReference, nullDefault: "default");
            }
        }

        // Fallback - unsupported type
        return new TypeMapping(fullName, "/* UNSUPPORTED */", "/* UNSUPPORTED */",
            isNullableReference: false, nullDefault: "default");
    }

    private static TypeMapping TryMapCollection(
        string originalDef,
        INamedTypeSymbol namedType,
        bool isNullableReference,
        string fullName,
        HashSet<string> visitedTypes)
    {
        CollectionKind? kind = null;
        bool isDictionary = false;

        switch (originalDef)
        {
            case "global::System.Collections.Generic.List<T>":
                kind = CollectionKind.List;
                break;
            case "global::System.Collections.Generic.IReadOnlyList<T>":
                kind = CollectionKind.IReadOnlyList;
                break;
            case "global::System.Collections.Immutable.ImmutableList<T>":
                kind = CollectionKind.ImmutableList;
                break;
            case "global::System.Collections.Immutable.ImmutableArray<T>":
                kind = CollectionKind.ImmutableArray;
                break;
            case "global::System.Collections.Generic.HashSet<T>":
                kind = CollectionKind.HashSet;
                break;
            case "global::System.Collections.Generic.Dictionary<TKey, TValue>":
                kind = CollectionKind.Dictionary;
                isDictionary = true;
                break;
            case "global::System.Collections.Immutable.ImmutableDictionary<TKey, TValue>":
                kind = CollectionKind.ImmutableDictionary;
                isDictionary = true;
                break;
        }

        if (kind == null) return null;

        if (isDictionary)
        {
            var keyType = namedType.TypeArguments[0];
            var valueType = namedType.TypeArguments[1];
            var keyMapping = GetTypeMapping(keyType, visitedTypes);
            var valueMapping = GetTypeMapping(valueType, visitedTypes);
            var keyFullName = keyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var valueFullName = valueType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            return new TypeMapping(
                TypeMappingKind.Collection,
                fullName, "/* COLLECTION */", "/* COLLECTION */",
                isNullableReference: isNullableReference,
                nullDefault: isNullableReference ? "null" : "default!",
                collection: new CollectionInfo(kind.Value, keyMapping, keyFullName, valueMapping, valueFullName));
        }
        else
        {
            var elementType = namedType.TypeArguments[0];
            var elementMapping = GetTypeMapping(elementType, visitedTypes);
            var elementFullName = elementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            return new TypeMapping(
                TypeMappingKind.Collection,
                fullName, "/* COLLECTION */", "/* COLLECTION */",
                isNullableReference: isNullableReference,
                nullDefault: isNullableReference ? "null" : "default!",
                collection: new CollectionInfo(kind.Value, elementMapping, elementFullName));
        }
    }

    private static List<FieldInfo> ExtractAkkaFields(INamedTypeSymbol typeSymbol, HashSet<string> visitedTypes)
    {
        var fullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        visitedTypes.Add(fullName);

        var fields = new List<FieldInfo>();
        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is IPropertySymbol prop)
            {
                foreach (var propAttr in prop.GetAttributes())
                {
                    var attrClass = propAttr.AttributeClass;
                    if (attrClass != null && GetFullMetadataName(attrClass) == FieldAttributeFullName)
                    {
                        int index = 0;
                        if (propAttr.ConstructorArguments.Length > 0 &&
                            propAttr.ConstructorArguments[0].Value is int idx)
                        {
                            index = idx;
                        }

                        var typeInfo = GetTypeMapping(prop.Type, visitedTypes);
                        fields.Add(new FieldInfo(prop.Name, index, typeInfo));
                    }
                }
            }
        }

        visitedTypes.Remove(fullName);
        fields.Sort((a, b) => a.Index.CompareTo(b.Index));
        return fields;
    }

    private static bool HasAttribute(INamedTypeSymbol symbol, string attributeFullName)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass != null && GetFullMetadataName(attr.AttributeClass) == attributeFullName)
                return true;
        }
        return false;
    }

    private static string GenerateSerializerCode(
        ModuleInfo module,
        ImmutableArray<SerializableTypeInfo> serializables)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(module.Namespace))
        {
            sb.AppendLine("namespace " + module.Namespace + ";");
            sb.AppendLine();
        }

        // Emit partial class WITHOUT base class (user's partial already has : SerializerV2<T>)
        sb.AppendLine("public partial class " + module.ClassName);
        sb.AppendLine("{");

        // Identifier property
        sb.AppendLine("    public override int Identifier => " + module.SerializerId + ";");
        sb.AppendLine();

        // Manifest method
        GenerateManifestMethod(sb, serializables);
        sb.AppendLine();

        // Write method
        GenerateWriteMethod(sb, serializables);
        sb.AppendLine();

        // Read method
        GenerateReadMethod(sb, serializables);
        sb.AppendLine();

        // SizeHint method
        sb.AppendLine("    public override int SizeHint(object obj) => 128;");
        sb.AppendLine();

        // Per-type Write/Read methods
        for (int i = 0; i < serializables.Length; i++)
        {
            var s = serializables[i];
            if (s == null) continue;

            GenerateWriteTypeMethod(sb, s);
            sb.AppendLine();
            GenerateReadTypeMethod(sb, s);

            if (i < serializables.Length - 1)
            {
                sb.AppendLine();
            }
        }

        // Collect and generate nested type helper methods
        var nestedTypes = CollectAllNestedTypes(serializables);
        foreach (var nested in nestedTypes)
        {
            sb.AppendLine();
            GenerateNestedWriteHelper(sb, nested);
            sb.AppendLine();
            GenerateNestedReadHelper(sb, nested);
        }

        sb.AppendLine("}");
        sb.AppendLine();

        // Generate SerializerSetup class
        GenerateSerializerSetup(sb, module, serializables);

        return sb.ToString();
    }

    /// <summary>
    /// Walks all fields recursively and collects unique nested types that need Write/Read helper methods.
    /// Skips types that are [AkkaSerializable] (they already get top-level Write/Read methods).
    /// </summary>
    /// <remarks>
    /// Design: Value object helpers (WriteAddress/ReadAddress) are generated as private methods
    /// per serializer. Cross-serializer sharing was considered but rejected because:
    /// 1. Generated code duplication has zero maintenance cost — it's generated, not hand-maintained.
    /// 2. Each generated file is self-contained (no cross-file dependencies).
    /// 3. JIT inlines these small methods, eliminating runtime overhead of duplication.
    /// 4. Roslyn generators run per-compilation; cross-assembly sharing would need
    ///    a new trigger mechanism for value-object-only assemblies.
    ///
    /// Wire format note: All sequence collections (List, Array, HashSet, ImmutableList, etc.)
    /// share the same wire format on the write side (count + elements). Only the read/construction
    /// side differs (List.Add vs array indexing vs builder pattern). This uniformity is intentional
    /// and requires no special handling.
    /// </remarks>
    private static List<NestedTypeInfo> CollectAllNestedTypes(ImmutableArray<SerializableTypeInfo> serializables)
    {
        var result = new List<NestedTypeInfo>();
        var seen = new HashSet<string>();

        for (int i = 0; i < serializables.Length; i++)
        {
            if (serializables[i] == null) continue;
            CollectNestedTypesFromFields(serializables[i].Fields, result, seen);
        }

        return result;
    }

    private static void CollectNestedTypesFromFields(FieldInfo[] fields, List<NestedTypeInfo> result, HashSet<string> seen)
    {
        foreach (var field in fields)
        {
            CollectNestedTypesFromMapping(field.TypeMapping, result, seen);
        }
    }

    private static void CollectNestedTypesFromMapping(TypeMapping mapping, List<NestedTypeInfo> result, HashSet<string> seen)
    {
        if (mapping.Kind == TypeMappingKind.NestedObject && mapping.NestedType != null)
        {
            if (!mapping.NestedType.IsAkkaSerializable && seen.Add(mapping.NestedType.FullyQualifiedName))
            {
                result.Add(mapping.NestedType);
                // Recurse into nested type's fields
                CollectNestedTypesFromFields(mapping.NestedType.Fields, result, seen);
            }
        }
        else if (mapping.Kind == TypeMappingKind.Collection && mapping.Collection != null)
        {
            var coll = mapping.Collection;
            if (coll.Kind == CollectionKind.Dictionary || coll.Kind == CollectionKind.ImmutableDictionary)
            {
                if (coll.KeyMapping != null) CollectNestedTypesFromMapping(coll.KeyMapping, result, seen);
                if (coll.ValueMapping != null) CollectNestedTypesFromMapping(coll.ValueMapping, result, seen);
            }
            else
            {
                if (coll.ElementMapping != null) CollectNestedTypesFromMapping(coll.ElementMapping, result, seen);
            }
        }
        else if (mapping.Kind == TypeMappingKind.NullableValue && mapping.InnerMapping != null)
        {
            CollectNestedTypesFromMapping(mapping.InnerMapping, result, seen);
        }
    }

    private static void GenerateSerializerSetup(
        StringBuilder sb,
        ModuleInfo module,
        ImmutableArray<SerializableTypeInfo> serializables)
    {
        var setupClassName = module.ClassName + "Setup";

        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// SerializerSetup for " + module.ClassName + ". Use with Akka.NET configuration.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public sealed class " + setupClassName);
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly " + setupClassName + " Instance = new();");
        sb.AppendLine();

        sb.AppendLine("    public global::System.Type SerializerType => typeof(" + module.ClassName + ");");
        sb.AppendLine();

        // BoundTypes — emit typeof(TProtocol) (the interface) instead of individual concrete types
        sb.AppendLine("    public static global::System.Type[] BoundTypes => new global::System.Type[]");
        sb.AppendLine("    {");
        if (!string.IsNullOrEmpty(module.ProtocolTypeFullName))
        {
            sb.AppendLine("        typeof(" + module.ProtocolTypeFullName + ")");
        }
        sb.AppendLine("    };");

        sb.AppendLine("}");
    }

    private static void GenerateManifestMethod(
        StringBuilder sb,
        ImmutableArray<SerializableTypeInfo> serializables)
    {
        sb.AppendLine("    public override string? Manifest(object obj) => obj switch");
        sb.AppendLine("    {");

        for (int i = 0; i < serializables.Length; i++)
        {
            var s = serializables[i];
            if (s == null) continue;
            sb.AppendLine("        " + s.FullyQualifiedName + " => \"" + s.Manifest + "\",");
        }

        sb.AppendLine("        _ => throw new global::System.ArgumentException($\"Unsupported type: {obj.GetType()}\", nameof(obj))");
        sb.AppendLine("    };");
    }

    private static void GenerateWriteMethod(
        StringBuilder sb,
        ImmutableArray<SerializableTypeInfo> serializables)
    {
        sb.AppendLine("    public override void Write(Akka.Serialization.V2.AkkaWriter writer, object obj)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (obj)");
        sb.AppendLine("        {");

        for (int i = 0; i < serializables.Length; i++)
        {
            var s = serializables[i];
            if (s == null) continue;
            sb.AppendLine("            case " + s.FullyQualifiedName + " msg:");
            sb.AppendLine("                Write" + s.SimpleName + "(writer, msg);");
            sb.AppendLine("                break;");
        }

        sb.AppendLine("            default:");
        sb.AppendLine("                throw new global::System.ArgumentException($\"Unsupported type: {obj.GetType()}\", nameof(obj));");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void GenerateReadMethod(
        StringBuilder sb,
        ImmutableArray<SerializableTypeInfo> serializables)
    {
        sb.AppendLine("    public override object Read(Akka.Serialization.V2.AkkaReader reader, string manifest)");
        sb.AppendLine("    {");
        sb.AppendLine("        return manifest switch");
        sb.AppendLine("        {");

        for (int i = 0; i < serializables.Length; i++)
        {
            var s = serializables[i];
            if (s == null) continue;
            sb.AppendLine("            \"" + s.Manifest + "\" => Read" + s.SimpleName + "(reader),");
        }

        sb.AppendLine("            _ => throw new global::System.ArgumentException($\"Unknown manifest: {manifest}\", nameof(manifest))");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
    }

    private static void GenerateWriteTypeMethod(
        StringBuilder sb,
        SerializableTypeInfo type)
    {
        sb.AppendLine("    private void Write" + type.SimpleName + "(Akka.Serialization.V2.AkkaWriter writer, " + type.FullyQualifiedName + " msg)");
        sb.AppendLine("    {");
        sb.AppendLine("        writer.BeginObject(" + type.Fields.Length + ");");

        for (int i = 0; i < type.Fields.Length; i++)
        {
            var field = type.Fields[i];
            GenerateFieldWrite(sb, field.TypeMapping, "msg." + field.PropertyName, "        ", 0);
        }

        sb.AppendLine("    }");
    }

    private static void GenerateReadTypeMethod(
        StringBuilder sb,
        SerializableTypeInfo type)
    {
        sb.AppendLine("    private " + type.FullyQualifiedName + " Read" + type.SimpleName + "(Akka.Serialization.V2.AkkaReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("        var fieldCount = reader.BeginReadObject();");

        for (int i = 0; i < type.Fields.Length; i++)
        {
            var field = type.Fields[i];
            var mapping = field.TypeMapping;
            var varName = ToCamelCase(field.PropertyName);

            GenerateFieldRead(sb, mapping, varName, i, "fieldCount", "        ", 0);
        }

        // Skip unknown trailing fields
        sb.AppendLine("        for (int __skip = " + type.Fields.Length + "; __skip < fieldCount; __skip++)");
        sb.AppendLine("        {");
        sb.AppendLine("            reader.SkipField();");
        sb.AppendLine("        }");

        // Construct the object
        sb.Append("        return new " + type.FullyQualifiedName + "(");
        for (int i = 0; i < type.Fields.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ToCamelCase(type.Fields[i].PropertyName));
        }
        sb.AppendLine(");");

        sb.AppendLine("    }");
    }

    private static void GenerateNestedWriteHelper(StringBuilder sb, NestedTypeInfo nested)
    {
        sb.AppendLine("    private void Write" + nested.SimpleName + "(Akka.Serialization.V2.AkkaWriter writer, " + nested.FullyQualifiedName + " msg)");
        sb.AppendLine("    {");
        sb.AppendLine("        writer.BeginObject(" + nested.Fields.Length + ");");

        for (int i = 0; i < nested.Fields.Length; i++)
        {
            var field = nested.Fields[i];
            GenerateFieldWrite(sb, field.TypeMapping, "msg." + field.PropertyName, "        ", 0);
        }

        sb.AppendLine("    }");
    }

    private static void GenerateNestedReadHelper(StringBuilder sb, NestedTypeInfo nested)
    {
        sb.AppendLine("    private " + nested.FullyQualifiedName + " Read" + nested.SimpleName + "(Akka.Serialization.V2.AkkaReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("        var fieldCount = reader.BeginReadObject();");

        for (int i = 0; i < nested.Fields.Length; i++)
        {
            var field = nested.Fields[i];
            var varName = ToCamelCase(field.PropertyName);
            GenerateFieldRead(sb, field.TypeMapping, varName, i, "fieldCount", "        ", 0);
        }

        // Skip unknown trailing fields
        sb.AppendLine("        for (int __skip = " + nested.Fields.Length + "; __skip < fieldCount; __skip++)");
        sb.AppendLine("        {");
        sb.AppendLine("            reader.SkipField();");
        sb.AppendLine("        }");

        // Construct the object
        sb.Append("        return new " + nested.FullyQualifiedName + "(");
        for (int i = 0; i < nested.Fields.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ToCamelCase(nested.Fields[i].PropertyName));
        }
        sb.AppendLine(");");

        sb.AppendLine("    }");
    }

    // =====================================================================
    // Field-level write/read code generation — dispatches on TypeMappingKind
    // =====================================================================

    private static void GenerateFieldWrite(StringBuilder sb, TypeMapping mapping, string accessor, string indent, int depth)
    {
        switch (mapping.Kind)
        {
            case TypeMappingKind.Primitive:
                GeneratePrimitiveWrite(sb, mapping, accessor, indent);
                break;

            case TypeMappingKind.Enum:
                GenerateEnumWrite(sb, mapping, accessor, indent);
                break;

            case TypeMappingKind.NullableValue:
                GenerateNullableValueWrite(sb, mapping, accessor, indent, depth);
                break;

            case TypeMappingKind.NestedObject:
                GenerateNestedObjectWrite(sb, mapping, accessor, indent);
                break;

            case TypeMappingKind.Collection:
                GenerateCollectionWrite(sb, mapping, accessor, indent, depth);
                break;
        }
    }

    private static void GeneratePrimitiveWrite(StringBuilder sb, TypeMapping mapping, string accessor, string indent)
    {
        if (mapping.IsNullableReference)
        {
            sb.AppendLine(indent + "if (" + accessor + " is null)");
            sb.AppendLine(indent + "    writer.WriteNull();");
            sb.AppendLine(indent + "else");
            sb.AppendLine(indent + "    writer." + mapping.WriteMethod + "(" + accessor + ");");
        }
        else if (mapping.WritePrefix != null)
        {
            sb.AppendLine(indent + "writer." + mapping.WriteMethod + "(" + mapping.WritePrefix + accessor + ");");
        }
        else
        {
            sb.AppendLine(indent + "writer." + mapping.WriteMethod + "(" + accessor + ");");
        }
    }

    private static void GenerateEnumWrite(StringBuilder sb, TypeMapping mapping, string accessor, string indent)
    {
        if (mapping.IsNullableReference)
        {
            sb.AppendLine(indent + "if (" + accessor + " is null)");
            sb.AppendLine(indent + "    writer.WriteNull();");
            sb.AppendLine(indent + "else");
            sb.AppendLine(indent + "    writer." + mapping.WriteMethod + "((int)" + accessor + ");");
        }
        else
        {
            sb.AppendLine(indent + "writer." + mapping.WriteMethod + "((int)" + accessor + ");");
        }
    }

    private static void GenerateNullableValueWrite(StringBuilder sb, TypeMapping mapping, string accessor, string indent, int depth)
    {
        sb.AppendLine(indent + "if (" + accessor + ".HasValue)");
        sb.AppendLine(indent + "{");
        // Write the inner value
        var innerAccessor = accessor + ".Value";
        GenerateFieldWrite(sb, mapping.InnerMapping, innerAccessor, indent + "    ", depth);
        sb.AppendLine(indent + "}");
        sb.AppendLine(indent + "else");
        sb.AppendLine(indent + "{");
        sb.AppendLine(indent + "    writer.WriteNull();");
        sb.AppendLine(indent + "}");
    }

    private static void GenerateNestedObjectWrite(StringBuilder sb, TypeMapping mapping, string accessor, string indent)
    {
        if (mapping.IsNullableReference)
        {
            sb.AppendLine(indent + "if (" + accessor + " is null)");
            sb.AppendLine(indent + "    writer.WriteNull();");
            sb.AppendLine(indent + "else");
            sb.AppendLine(indent + "    Write" + mapping.NestedType.SimpleName + "(writer, " + accessor + ");");
        }
        else
        {
            sb.AppendLine(indent + "Write" + mapping.NestedType.SimpleName + "(writer, " + accessor + ");");
        }
    }

    private static void GenerateCollectionWrite(StringBuilder sb, TypeMapping mapping, string accessor, string indent, int depth)
    {
        var coll = mapping.Collection;
        var depthStr = depth.ToString();

        if (mapping.IsNullableReference)
        {
            sb.AppendLine(indent + "if (" + accessor + " is null)");
            sb.AppendLine(indent + "{");
            sb.AppendLine(indent + "    writer.WriteNull();");
            sb.AppendLine(indent + "}");
            sb.AppendLine(indent + "else");
            sb.AppendLine(indent + "{");
            GenerateCollectionWriteInner(sb, coll, accessor, indent + "    ", depth);
            sb.AppendLine(indent + "}");
        }
        else
        {
            GenerateCollectionWriteInner(sb, coll, accessor, indent, depth);
        }
    }

    private static void GenerateCollectionWriteInner(StringBuilder sb, CollectionInfo coll, string accessor, string indent, int depth)
    {
        var depthStr = depth.ToString();
        bool isDictionary = coll.Kind == CollectionKind.Dictionary || coll.Kind == CollectionKind.ImmutableDictionary;

        if (isDictionary)
        {
            sb.AppendLine(indent + "writer.BeginObject(" + accessor + ".Count);");
            sb.AppendLine(indent + "foreach (var __kvp" + depthStr + " in " + accessor + ")");
            sb.AppendLine(indent + "{");
            sb.AppendLine(indent + "    writer.BeginObject(2);");
            GenerateFieldWrite(sb, coll.KeyMapping, "__kvp" + depthStr + ".Key", indent + "    ", depth + 1);
            GenerateFieldWrite(sb, coll.ValueMapping, "__kvp" + depthStr + ".Value", indent + "    ", depth + 1);
            sb.AppendLine(indent + "}");
        }
        else
        {
            // For arrays and ImmutableArray use .Length, for everything else use .Count
            string countAccessor;
            if (coll.Kind == CollectionKind.Array || coll.Kind == CollectionKind.ImmutableArray)
                countAccessor = accessor + ".Length";
            else
                countAccessor = accessor + ".Count";

            sb.AppendLine(indent + "writer.BeginObject(" + countAccessor + ");");

            if (coll.Kind == CollectionKind.Array)
            {
                sb.AppendLine(indent + "for (int __i" + depthStr + " = 0; __i" + depthStr + " < " + accessor + ".Length; __i" + depthStr + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldWrite(sb, coll.ElementMapping, accessor + "[__i" + depthStr + "]", indent + "    ", depth + 1);
                sb.AppendLine(indent + "}");
            }
            else
            {
                sb.AppendLine(indent + "foreach (var __item" + depthStr + " in " + accessor + ")");
                sb.AppendLine(indent + "{");
                GenerateFieldWrite(sb, coll.ElementMapping, "__item" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "}");
            }
        }
    }

    private static void GenerateFieldRead(StringBuilder sb, TypeMapping mapping, string varName, int fieldIndex, string fieldCountVar, string indent, int depth)
    {
        switch (mapping.Kind)
        {
            case TypeMappingKind.Primitive:
                GeneratePrimitiveRead(sb, mapping, varName, fieldIndex, fieldCountVar, indent);
                break;

            case TypeMappingKind.Enum:
                GenerateEnumRead(sb, mapping, varName, fieldIndex, fieldCountVar, indent);
                break;

            case TypeMappingKind.NullableValue:
                GenerateNullableValueRead(sb, mapping, varName, fieldIndex, fieldCountVar, indent, depth);
                break;

            case TypeMappingKind.NestedObject:
                GenerateNestedObjectRead(sb, mapping, varName, fieldIndex, fieldCountVar, indent);
                break;

            case TypeMappingKind.Collection:
                GenerateCollectionRead(sb, mapping, varName, fieldIndex, fieldCountVar, indent, depth);
                break;

            default:
                // Unsupported — emit default
                sb.AppendLine(indent + "var " + varName + " = " + mapping.NullDefault + ";");
                break;
        }
    }

    private static void GeneratePrimitiveRead(StringBuilder sb, TypeMapping mapping, string varName, int fieldIndex, string fieldCountVar, string indent)
    {
        if (mapping.IsNullableReference)
        {
            sb.AppendLine(indent + "var " + varName + " = " + fieldCountVar + " > " + fieldIndex + " ? (reader.TryReadNull() ? null : reader." + mapping.ReadMethod + "()) : " + mapping.NullDefault + ";");
        }
        else if (mapping.ReadExpression != null)
        {
            sb.AppendLine(indent + "var " + varName + " = " + fieldCountVar + " > " + fieldIndex + " ? reader." + mapping.ReadExpression + " : " + mapping.NullDefault + ";");
        }
        else if (mapping.ReadPrefix != null)
        {
            sb.AppendLine(indent + "var " + varName + " = " + fieldCountVar + " > " + fieldIndex + " ? " + mapping.ReadPrefix + "reader." + mapping.ReadMethod + "() : " + mapping.NullDefault + ";");
        }
        else
        {
            sb.AppendLine(indent + "var " + varName + " = " + fieldCountVar + " > " + fieldIndex + " ? reader." + mapping.ReadMethod + "() : " + mapping.NullDefault + ";");
        }
    }

    private static void GenerateEnumRead(StringBuilder sb, TypeMapping mapping, string varName, int fieldIndex, string fieldCountVar, string indent)
    {
        sb.AppendLine(indent + "var " + varName + " = " + fieldCountVar + " > " + fieldIndex + " ? (" + mapping.EnumFullName + ")reader." + mapping.ReadMethod + "() : default;");
    }

    private static void GenerateNullableValueRead(StringBuilder sb, TypeMapping mapping, string varName, int fieldIndex, string fieldCountVar, string indent, int depth)
    {
        var innerMapping = mapping.InnerMapping;
        sb.AppendLine(indent + mapping.CSharpType + " " + varName + " = null;");
        sb.AppendLine(indent + "if (" + fieldCountVar + " > " + fieldIndex + ")");
        sb.AppendLine(indent + "{");
        sb.AppendLine(indent + "    if (!reader.TryReadNull())");
        sb.AppendLine(indent + "    {");

        // Read the inner value into a temp variable then assign
        var tempVar = "__inner" + depth;
        GenerateFieldReadInline(sb, innerMapping, tempVar, indent + "        ", depth + 1);
        sb.AppendLine(indent + "        " + varName + " = " + tempVar + ";");

        sb.AppendLine(indent + "    }");
        sb.AppendLine(indent + "}");
    }

    private static void GenerateNestedObjectRead(StringBuilder sb, TypeMapping mapping, string varName, int fieldIndex, string fieldCountVar, string indent)
    {
        if (mapping.IsNullableReference)
        {
            sb.AppendLine(indent + "var " + varName + " = " + fieldCountVar + " > " + fieldIndex + " ? (reader.TryReadNull() ? null : Read" + mapping.NestedType.SimpleName + "(reader)) : " + mapping.NullDefault + ";");
        }
        else
        {
            sb.AppendLine(indent + "var " + varName + " = " + fieldCountVar + " > " + fieldIndex + " ? Read" + mapping.NestedType.SimpleName + "(reader) : " + mapping.NullDefault + "!;");
        }
    }

    private static void GenerateCollectionRead(StringBuilder sb, TypeMapping mapping, string varName, int fieldIndex, string fieldCountVar, string indent, int depth)
    {
        var coll = mapping.Collection;
        bool isDictionary = coll.Kind == CollectionKind.Dictionary || coll.Kind == CollectionKind.ImmutableDictionary;

        if (mapping.IsNullableReference)
        {
            // Declare variable, then check
            sb.AppendLine(indent + mapping.CSharpType + "? " + varName + " = " + mapping.NullDefault + ";");
            sb.AppendLine(indent + "if (" + fieldCountVar + " > " + fieldIndex + ")");
            sb.AppendLine(indent + "{");
            sb.AppendLine(indent + "    if (!reader.TryReadNull())");
            sb.AppendLine(indent + "    {");
            GenerateCollectionReadInner(sb, coll, varName, indent + "        ", depth);
            sb.AppendLine(indent + "    }");
            sb.AppendLine(indent + "}");
        }
        else
        {
            sb.AppendLine(indent + GenerateCollectionTypeDecl(coll) + " " + varName + " = " + mapping.NullDefault + ";");
            sb.AppendLine(indent + "if (" + fieldCountVar + " > " + fieldIndex + ")");
            sb.AppendLine(indent + "{");
            GenerateCollectionReadInner(sb, coll, varName, indent + "    ", depth);
            sb.AppendLine(indent + "}");
        }
    }

    private static string GenerateCollectionTypeDecl(CollectionInfo coll)
    {
        bool isDictionary = coll.Kind == CollectionKind.Dictionary || coll.Kind == CollectionKind.ImmutableDictionary;
        if (isDictionary)
        {
            switch (coll.Kind)
            {
                case CollectionKind.Dictionary:
                    return "global::System.Collections.Generic.Dictionary<" + coll.KeyTypeFullName + ", " + coll.ValueTypeFullName + ">";
                case CollectionKind.ImmutableDictionary:
                    return "global::System.Collections.Immutable.ImmutableDictionary<" + coll.KeyTypeFullName + ", " + coll.ValueTypeFullName + ">";
                default: return "object";
            }
        }
        switch (coll.Kind)
        {
            case CollectionKind.Array:
                return coll.ElementTypeFullName + "[]";
            case CollectionKind.List:
                return "global::System.Collections.Generic.List<" + coll.ElementTypeFullName + ">";
            case CollectionKind.IReadOnlyList:
                return "global::System.Collections.Generic.IReadOnlyList<" + coll.ElementTypeFullName + ">";
            case CollectionKind.ImmutableList:
                return "global::System.Collections.Immutable.ImmutableList<" + coll.ElementTypeFullName + ">";
            case CollectionKind.ImmutableArray:
                return "global::System.Collections.Immutable.ImmutableArray<" + coll.ElementTypeFullName + ">";
            case CollectionKind.HashSet:
                return "global::System.Collections.Generic.HashSet<" + coll.ElementTypeFullName + ">";
            default: return "object";
        }
    }

    private static void GenerateCollectionReadInner(StringBuilder sb, CollectionInfo coll, string varName, string indent, int depth)
    {
        var depthStr = depth.ToString();
        bool isDictionary = coll.Kind == CollectionKind.Dictionary || coll.Kind == CollectionKind.ImmutableDictionary;

        sb.AppendLine(indent + "var __count" + depthStr + " = reader.BeginReadObject();");

        if (isDictionary)
        {
            GenerateDictionaryReadInner(sb, coll, varName, indent, depth);
        }
        else
        {
            GenerateSequenceReadInner(sb, coll, varName, indent, depth);
        }
    }

    private static void GenerateDictionaryReadInner(StringBuilder sb, CollectionInfo coll, string varName, string indent, int depth)
    {
        var depthStr = depth.ToString();
        var countVar = "__count" + depthStr;
        var iVar = "__i" + depthStr;

        switch (coll.Kind)
        {
            case CollectionKind.Dictionary:
                sb.AppendLine(indent + "var __builder" + depthStr + " = new global::System.Collections.Generic.Dictionary<" + coll.KeyTypeFullName + ", " + coll.ValueTypeFullName + ">(" + countVar + ");");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                sb.AppendLine(indent + "    reader.BeginReadObject();");
                GenerateFieldReadInline(sb, coll.KeyMapping, "__key" + depthStr, indent + "    ", depth + 1);
                GenerateFieldReadInline(sb, coll.ValueMapping, "__val" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __builder" + depthStr + ".Add(__key" + depthStr + ", __val" + depthStr + ");");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + varName + " = __builder" + depthStr + ";");
                break;

            case CollectionKind.ImmutableDictionary:
                sb.AppendLine(indent + "var __builder" + depthStr + " = global::System.Collections.Immutable.ImmutableDictionary.CreateBuilder<" + coll.KeyTypeFullName + ", " + coll.ValueTypeFullName + ">();");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                sb.AppendLine(indent + "    reader.BeginReadObject();");
                GenerateFieldReadInline(sb, coll.KeyMapping, "__key" + depthStr, indent + "    ", depth + 1);
                GenerateFieldReadInline(sb, coll.ValueMapping, "__val" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __builder" + depthStr + ".Add(__key" + depthStr + ", __val" + depthStr + ");");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + varName + " = __builder" + depthStr + ".ToImmutable();");
                break;
        }
    }

    private static void GenerateSequenceReadInner(StringBuilder sb, CollectionInfo coll, string varName, string indent, int depth)
    {
        var depthStr = depth.ToString();
        var countVar = "__count" + depthStr;
        var iVar = "__i" + depthStr;

        switch (coll.Kind)
        {
            case CollectionKind.Array:
                sb.AppendLine(indent + "var __arr" + depthStr + " = new " + coll.ElementTypeFullName + "[" + countVar + "];");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldReadInline(sb, coll.ElementMapping, "__elem" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __arr" + depthStr + "[" + iVar + "] = __elem" + depthStr + ";");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + varName + " = __arr" + depthStr + ";");
                break;

            case CollectionKind.List:
                sb.AppendLine(indent + "var __list" + depthStr + " = new global::System.Collections.Generic.List<" + coll.ElementTypeFullName + ">(" + countVar + ");");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldReadInline(sb, coll.ElementMapping, "__elem" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __list" + depthStr + ".Add(__elem" + depthStr + ");");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + varName + " = __list" + depthStr + ";");
                break;

            case CollectionKind.IReadOnlyList:
                // Use array (implements IReadOnlyList<T>)
                sb.AppendLine(indent + "var __arr" + depthStr + " = new " + coll.ElementTypeFullName + "[" + countVar + "];");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldReadInline(sb, coll.ElementMapping, "__elem" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __arr" + depthStr + "[" + iVar + "] = __elem" + depthStr + ";");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + varName + " = __arr" + depthStr + ";");
                break;

            case CollectionKind.ImmutableList:
                sb.AppendLine(indent + "var __builder" + depthStr + " = global::System.Collections.Immutable.ImmutableList.CreateBuilder<" + coll.ElementTypeFullName + ">();");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldReadInline(sb, coll.ElementMapping, "__elem" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __builder" + depthStr + ".Add(__elem" + depthStr + ");");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + varName + " = __builder" + depthStr + ".ToImmutable();");
                break;

            case CollectionKind.ImmutableArray:
                sb.AppendLine(indent + "var __builder" + depthStr + " = global::System.Collections.Immutable.ImmutableArray.CreateBuilder<" + coll.ElementTypeFullName + ">(" + countVar + ");");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldReadInline(sb, coll.ElementMapping, "__elem" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __builder" + depthStr + ".Add(__elem" + depthStr + ");");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + varName + " = __builder" + depthStr + ".MoveToImmutable();");
                break;

            case CollectionKind.HashSet:
                sb.AppendLine(indent + "var __set" + depthStr + " = new global::System.Collections.Generic.HashSet<" + coll.ElementTypeFullName + ">(" + countVar + ");");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldReadInline(sb, coll.ElementMapping, "__elem" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __set" + depthStr + ".Add(__elem" + depthStr + ");");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + varName + " = __set" + depthStr + ";");
                break;
        }
    }

    /// <summary>
    /// Generates an inline read expression for use inside collection loops and nullable value reads.
    /// This creates a variable declaration directly (no fieldCount guard needed).
    /// </summary>
    private static void GenerateFieldReadInline(StringBuilder sb, TypeMapping mapping, string varName, string indent, int depth)
    {
        switch (mapping.Kind)
        {
            case TypeMappingKind.Primitive:
                if (mapping.IsNullableReference)
                {
                    sb.AppendLine(indent + "var " + varName + " = reader.TryReadNull() ? null : reader." + mapping.ReadMethod + "();");
                }
                else if (mapping.ReadExpression != null)
                {
                    sb.AppendLine(indent + "var " + varName + " = reader." + mapping.ReadExpression + ";");
                }
                else
                {
                    sb.AppendLine(indent + "var " + varName + " = reader." + mapping.ReadMethod + "();");
                }
                break;

            case TypeMappingKind.Enum:
                sb.AppendLine(indent + "var " + varName + " = (" + mapping.EnumFullName + ")reader." + mapping.ReadMethod + "();");
                break;

            case TypeMappingKind.NullableValue:
                sb.AppendLine(indent + mapping.CSharpType + " " + varName + " = null;");
                sb.AppendLine(indent + "if (!reader.TryReadNull())");
                sb.AppendLine(indent + "{");
                var innerTemp = "__nv" + depth;
                GenerateFieldReadInline(sb, mapping.InnerMapping, innerTemp, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    " + varName + " = " + innerTemp + ";");
                sb.AppendLine(indent + "}");
                break;

            case TypeMappingKind.NestedObject:
                if (mapping.IsNullableReference)
                {
                    sb.AppendLine(indent + "var " + varName + " = reader.TryReadNull() ? null : Read" + mapping.NestedType.SimpleName + "(reader);");
                }
                else
                {
                    sb.AppendLine(indent + "var " + varName + " = Read" + mapping.NestedType.SimpleName + "(reader);");
                }
                break;

            case TypeMappingKind.Collection:
                // For inline collection reads, build into a temp then assign
                GenerateCollectionReadInline(sb, mapping, varName, indent, depth);
                break;

            default:
                sb.AppendLine(indent + "var " + varName + " = default;");
                break;
        }
    }

    private static void GenerateCollectionReadInline(StringBuilder sb, TypeMapping mapping, string varName, string indent, int depth)
    {
        var coll = mapping.Collection;
        var depthStr = depth.ToString();

        sb.AppendLine(indent + "var __count" + depthStr + " = reader.BeginReadObject();");

        bool isDictionary = coll.Kind == CollectionKind.Dictionary || coll.Kind == CollectionKind.ImmutableDictionary;
        if (isDictionary)
        {
            // Inline dictionary read
            var iVar = "__i" + depthStr;
            switch (coll.Kind)
            {
                case CollectionKind.Dictionary:
                    sb.AppendLine(indent + "var __builder" + depthStr + " = new global::System.Collections.Generic.Dictionary<" + coll.KeyTypeFullName + ", " + coll.ValueTypeFullName + ">(__count" + depthStr + ");");
                    break;
                case CollectionKind.ImmutableDictionary:
                    sb.AppendLine(indent + "var __builder" + depthStr + " = global::System.Collections.Immutable.ImmutableDictionary.CreateBuilder<" + coll.KeyTypeFullName + ", " + coll.ValueTypeFullName + ">();");
                    break;
            }
            sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < __count" + depthStr + "; " + iVar + "++)");
            sb.AppendLine(indent + "{");
            sb.AppendLine(indent + "    reader.BeginReadObject();");
            GenerateFieldReadInline(sb, coll.KeyMapping, "__key" + depthStr, indent + "    ", depth + 1);
            GenerateFieldReadInline(sb, coll.ValueMapping, "__val" + depthStr, indent + "    ", depth + 1);
            sb.AppendLine(indent + "    __builder" + depthStr + ".Add(__key" + depthStr + ", __val" + depthStr + ");");
            sb.AppendLine(indent + "}");

            if (coll.Kind == CollectionKind.ImmutableDictionary)
                sb.AppendLine(indent + "var " + varName + " = __builder" + depthStr + ".ToImmutable();");
            else
                sb.AppendLine(indent + "var " + varName + " = __builder" + depthStr + ";");
        }
        else
        {
            GenerateSequenceReadInline(sb, coll, varName, indent, depth);
        }
    }

    private static void GenerateSequenceReadInline(StringBuilder sb, CollectionInfo coll, string varName, string indent, int depth)
    {
        var depthStr = depth.ToString();
        var countVar = "__count" + depthStr;
        var iVar = "__i" + depthStr;

        switch (coll.Kind)
        {
            case CollectionKind.Array:
            case CollectionKind.IReadOnlyList:
                sb.AppendLine(indent + "var __arr" + depthStr + " = new " + coll.ElementTypeFullName + "[" + countVar + "];");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldReadInline(sb, coll.ElementMapping, "__elem" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __arr" + depthStr + "[" + iVar + "] = __elem" + depthStr + ";");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + "var " + varName + " = __arr" + depthStr + ";");
                break;

            case CollectionKind.List:
                sb.AppendLine(indent + "var __list" + depthStr + " = new global::System.Collections.Generic.List<" + coll.ElementTypeFullName + ">(" + countVar + ");");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldReadInline(sb, coll.ElementMapping, "__elem" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __list" + depthStr + ".Add(__elem" + depthStr + ");");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + "var " + varName + " = __list" + depthStr + ";");
                break;

            case CollectionKind.ImmutableList:
                sb.AppendLine(indent + "var __builder" + depthStr + " = global::System.Collections.Immutable.ImmutableList.CreateBuilder<" + coll.ElementTypeFullName + ">();");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldReadInline(sb, coll.ElementMapping, "__elem" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __builder" + depthStr + ".Add(__elem" + depthStr + ");");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + "var " + varName + " = __builder" + depthStr + ".ToImmutable();");
                break;

            case CollectionKind.ImmutableArray:
                sb.AppendLine(indent + "var __builder" + depthStr + " = global::System.Collections.Immutable.ImmutableArray.CreateBuilder<" + coll.ElementTypeFullName + ">(" + countVar + ");");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldReadInline(sb, coll.ElementMapping, "__elem" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __builder" + depthStr + ".Add(__elem" + depthStr + ");");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + "var " + varName + " = __builder" + depthStr + ".MoveToImmutable();");
                break;

            case CollectionKind.HashSet:
                sb.AppendLine(indent + "var __set" + depthStr + " = new global::System.Collections.Generic.HashSet<" + coll.ElementTypeFullName + ">(" + countVar + ");");
                sb.AppendLine(indent + "for (int " + iVar + " = 0; " + iVar + " < " + countVar + "; " + iVar + "++)");
                sb.AppendLine(indent + "{");
                GenerateFieldReadInline(sb, coll.ElementMapping, "__elem" + depthStr, indent + "    ", depth + 1);
                sb.AppendLine(indent + "    __set" + depthStr + ".Add(__elem" + depthStr + ");");
                sb.AppendLine(indent + "}");
                sb.AppendLine(indent + "var " + varName + " = __set" + depthStr + ";");
                break;
        }
    }

    /// <summary>
    /// Computes an FNV-1a hash of the input string, returning a positive 31-bit int.
    /// </summary>
    internal static int ComputeFnv1aHash(string input)
    {
        const uint FnvOffsetBasis = 2166136261u;
        const uint FnvPrime = 16777619u;
        uint hash = FnvOffsetBasis;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= FnvPrime;
        }
        return (int)(hash & 0x7FFFFFFF); // mask to positive int32
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private static string GetFullNamespace(ISymbol symbol)
    {
        var parts = new List<string>();
        var ns = symbol.ContainingNamespace;
        while (ns != null && !ns.IsGlobalNamespace)
        {
            parts.Insert(0, ns.Name);
            ns = ns.ContainingNamespace;
        }
        return string.Join(".", parts);
    }

    private static string GetFullMetadataName(INamedTypeSymbol symbol)
    {
        var parts = new List<string>();
        parts.Add(symbol.Name);

        var ns = symbol.ContainingNamespace;
        while (ns != null && !ns.IsGlobalNamespace)
        {
            parts.Insert(0, ns.Name);
            ns = ns.ContainingNamespace;
        }

        return string.Join(".", parts);
    }
}

// =====================================================================
// Data models for the generator pipeline
// These are simple immutable types used to pass data between pipeline stages.
// They must be equatable for the incremental generator caching to work.
// =====================================================================

internal sealed class ModuleInfo : IEquatable<ModuleInfo>
{
    public string Namespace { get; }
    public string ClassName { get; }
    public string Name { get; }
    public string ProtocolTypeFullName { get; }
    public string ProtocolTypeSimpleName { get; }
    public int ExplicitSerializerId { get; }

    /// <summary>
    /// Returns the effective serializer ID: explicit if set, otherwise FNV-1a hash of Name.
    /// </summary>
    public int SerializerId
    {
        get
        {
            if (ExplicitSerializerId != 0)
                return ExplicitSerializerId;
            if (!string.IsNullOrEmpty(Name))
                return AkkaSerializerGenerator.ComputeFnv1aHash(Name);
            return 0;
        }
    }

    public ModuleInfo(string ns, string className, string name, string protocolTypeFullName, string protocolTypeSimpleName, int explicitSerializerId)
    {
        Namespace = ns;
        ClassName = className;
        Name = name;
        ProtocolTypeFullName = protocolTypeFullName;
        ProtocolTypeSimpleName = protocolTypeSimpleName;
        ExplicitSerializerId = explicitSerializerId;
    }

    public bool Equals(ModuleInfo other)
    {
        if (other == null) return false;
        return Namespace == other.Namespace
            && ClassName == other.ClassName
            && Name == other.Name
            && ProtocolTypeFullName == other.ProtocolTypeFullName
            && ProtocolTypeSimpleName == other.ProtocolTypeSimpleName
            && ExplicitSerializerId == other.ExplicitSerializerId;
    }

    public override bool Equals(object obj) => Equals(obj as ModuleInfo);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (Namespace != null ? Namespace.GetHashCode() : 0);
            hash = hash * 31 + (ClassName != null ? ClassName.GetHashCode() : 0);
            hash = hash * 31 + (Name != null ? Name.GetHashCode() : 0);
            hash = hash * 31 + (ProtocolTypeFullName != null ? ProtocolTypeFullName.GetHashCode() : 0);
            hash = hash * 31 + ExplicitSerializerId.GetHashCode();
            return hash;
        }
    }
}

internal sealed class SerializableTypeInfo : IEquatable<SerializableTypeInfo>
{
    public string FullyQualifiedName { get; }
    public string SimpleName { get; }
    public string Manifest { get; }
    public FieldInfo[] Fields { get; }
    public string[] ImplementedTypes { get; }

    public SerializableTypeInfo(string fullyQualifiedName, string simpleName, string manifest, FieldInfo[] fields, string[] implementedTypes)
    {
        FullyQualifiedName = fullyQualifiedName;
        SimpleName = simpleName;
        Manifest = manifest;
        Fields = fields;
        ImplementedTypes = implementedTypes ?? Array.Empty<string>();
    }

    /// <summary>
    /// Checks if this type implements the given interface (by fully qualified name).
    /// </summary>
    public bool ImplementsInterface(string fullName)
    {
        for (int i = 0; i < ImplementedTypes.Length; i++)
        {
            if (ImplementedTypes[i] == fullName)
                return true;
        }
        return false;
    }

    public bool Equals(SerializableTypeInfo other)
    {
        if (other == null) return false;
        if (FullyQualifiedName != other.FullyQualifiedName
            || SimpleName != other.SimpleName
            || Manifest != other.Manifest
            || Fields.Length != other.Fields.Length
            || ImplementedTypes.Length != other.ImplementedTypes.Length)
            return false;

        for (int i = 0; i < Fields.Length; i++)
        {
            if (!Fields[i].Equals(other.Fields[i]))
                return false;
        }
        for (int i = 0; i < ImplementedTypes.Length; i++)
        {
            if (ImplementedTypes[i] != other.ImplementedTypes[i])
                return false;
        }
        return true;
    }

    public override bool Equals(object obj) => Equals(obj as SerializableTypeInfo);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (FullyQualifiedName != null ? FullyQualifiedName.GetHashCode() : 0);
            hash = hash * 31 + (SimpleName != null ? SimpleName.GetHashCode() : 0);
            hash = hash * 31 + (Manifest != null ? Manifest.GetHashCode() : 0);
            hash = hash * 31 + Fields.Length.GetHashCode();
            hash = hash * 31 + ImplementedTypes.Length.GetHashCode();
            return hash;
        }
    }
}

internal sealed class FieldInfo : IEquatable<FieldInfo>
{
    public string PropertyName { get; }
    public int Index { get; }
    public TypeMapping TypeMapping { get; }

    public FieldInfo(string propertyName, int index, TypeMapping typeMapping)
    {
        PropertyName = propertyName;
        Index = index;
        TypeMapping = typeMapping;
    }

    public bool Equals(FieldInfo other)
    {
        if (other == null) return false;
        return PropertyName == other.PropertyName
            && Index == other.Index
            && TypeMapping.Equals(other.TypeMapping);
    }

    public override bool Equals(object obj) => Equals(obj as FieldInfo);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (PropertyName != null ? PropertyName.GetHashCode() : 0);
            hash = hash * 31 + Index.GetHashCode();
            hash = hash * 31 + TypeMapping.GetHashCode();
            return hash;
        }
    }
}

internal enum TypeMappingKind { Primitive, Enum, NestedObject, Collection, NullableValue, Unsupported }
internal enum CollectionKind { Array, List, IReadOnlyList, ImmutableList, ImmutableArray, HashSet, Dictionary, ImmutableDictionary }

internal sealed class NestedTypeInfo : IEquatable<NestedTypeInfo>
{
    public string FullyQualifiedName { get; }
    public string SimpleName { get; }
    public FieldInfo[] Fields { get; }
    public bool IsAkkaSerializable { get; }

    public NestedTypeInfo(string fullyQualifiedName, string simpleName, FieldInfo[] fields, bool isAkkaSerializable)
    {
        FullyQualifiedName = fullyQualifiedName;
        SimpleName = simpleName;
        Fields = fields;
        IsAkkaSerializable = isAkkaSerializable;
    }

    public bool Equals(NestedTypeInfo other)
    {
        if (other == null) return false;
        if (FullyQualifiedName != other.FullyQualifiedName
            || SimpleName != other.SimpleName
            || IsAkkaSerializable != other.IsAkkaSerializable
            || Fields.Length != other.Fields.Length)
            return false;
        for (int i = 0; i < Fields.Length; i++)
        {
            if (!Fields[i].Equals(other.Fields[i]))
                return false;
        }
        return true;
    }

    public override bool Equals(object obj) => Equals(obj as NestedTypeInfo);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (FullyQualifiedName != null ? FullyQualifiedName.GetHashCode() : 0);
            hash = hash * 31 + (SimpleName != null ? SimpleName.GetHashCode() : 0);
            hash = hash * 31 + IsAkkaSerializable.GetHashCode();
            hash = hash * 31 + Fields.Length.GetHashCode();
            return hash;
        }
    }
}

internal sealed class CollectionInfo : IEquatable<CollectionInfo>
{
    public CollectionKind Kind { get; }
    public TypeMapping ElementMapping { get; }
    public string ElementTypeFullName { get; }
    // For dictionaries
    public TypeMapping KeyMapping { get; }
    public string KeyTypeFullName { get; }
    public TypeMapping ValueMapping { get; }
    public string ValueTypeFullName { get; }

    public CollectionInfo(CollectionKind kind, TypeMapping elementMapping, string elementTypeFullName)
    {
        Kind = kind;
        ElementMapping = elementMapping;
        ElementTypeFullName = elementTypeFullName;
    }

    public CollectionInfo(CollectionKind kind, TypeMapping keyMapping, string keyTypeFullName,
        TypeMapping valueMapping, string valueTypeFullName)
    {
        Kind = kind;
        KeyMapping = keyMapping;
        KeyTypeFullName = keyTypeFullName;
        ValueMapping = valueMapping;
        ValueTypeFullName = valueTypeFullName;
    }

    public bool Equals(CollectionInfo other)
    {
        if (other == null) return false;
        if (Kind != other.Kind) return false;
        if (!Equals(ElementMapping, other.ElementMapping)) return false;
        if (ElementTypeFullName != other.ElementTypeFullName) return false;
        if (!Equals(KeyMapping, other.KeyMapping)) return false;
        if (KeyTypeFullName != other.KeyTypeFullName) return false;
        if (!Equals(ValueMapping, other.ValueMapping)) return false;
        if (ValueTypeFullName != other.ValueTypeFullName) return false;
        return true;
    }

    public override bool Equals(object obj) => Equals(obj as CollectionInfo);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + Kind.GetHashCode();
            hash = hash * 31 + (ElementTypeFullName != null ? ElementTypeFullName.GetHashCode() : 0);
            hash = hash * 31 + (KeyTypeFullName != null ? KeyTypeFullName.GetHashCode() : 0);
            hash = hash * 31 + (ValueTypeFullName != null ? ValueTypeFullName.GetHashCode() : 0);
            return hash;
        }
    }
}

internal sealed class TypeMapping : IEquatable<TypeMapping>
{
    public string CSharpType { get; }
    public string WriteMethod { get; }
    public string ReadMethod { get; }
    public bool IsNullableReference { get; }
    public string NullDefault { get; }
    public string WritePrefix { get; }
    public string ReadPrefix { get; }
    public string ReadExpression { get; }
    public TypeMappingKind Kind { get; }
    public NestedTypeInfo NestedType { get; }
    public CollectionInfo Collection { get; }
    public TypeMapping InnerMapping { get; }
    public string EnumFullName { get; }

    // Primitive constructor (backward compat)
    public TypeMapping(
        string csharpType,
        string writeMethod,
        string readMethod,
        bool isNullableReference,
        string nullDefault,
        string writePrefix = null,
        string readPrefix = null,
        string readExpression = null)
    {
        CSharpType = csharpType;
        WriteMethod = writeMethod;
        ReadMethod = readMethod;
        IsNullableReference = isNullableReference;
        NullDefault = nullDefault;
        WritePrefix = writePrefix;
        ReadPrefix = readPrefix;
        ReadExpression = readExpression;
        Kind = TypeMappingKind.Primitive;
    }

    // Full constructor for all kinds
    public TypeMapping(
        TypeMappingKind kind,
        string csharpType,
        string writeMethod,
        string readMethod,
        bool isNullableReference,
        string nullDefault,
        string writePrefix = null,
        string readPrefix = null,
        string readExpression = null,
        NestedTypeInfo nestedType = null,
        CollectionInfo collection = null,
        TypeMapping innerMapping = null,
        string enumFullName = null)
    {
        Kind = kind;
        CSharpType = csharpType;
        WriteMethod = writeMethod;
        ReadMethod = readMethod;
        IsNullableReference = isNullableReference;
        NullDefault = nullDefault;
        WritePrefix = writePrefix;
        ReadPrefix = readPrefix;
        ReadExpression = readExpression;
        NestedType = nestedType;
        Collection = collection;
        InnerMapping = innerMapping;
        EnumFullName = enumFullName;
    }

    public bool Equals(TypeMapping other)
    {
        if (other == null) return false;
        return CSharpType == other.CSharpType
            && WriteMethod == other.WriteMethod
            && ReadMethod == other.ReadMethod
            && IsNullableReference == other.IsNullableReference
            && NullDefault == other.NullDefault
            && WritePrefix == other.WritePrefix
            && ReadPrefix == other.ReadPrefix
            && ReadExpression == other.ReadExpression
            && Kind == other.Kind
            && Equals(NestedType, other.NestedType)
            && Equals(Collection, other.Collection)
            && Equals(InnerMapping, other.InnerMapping)
            && EnumFullName == other.EnumFullName;
    }

    public override bool Equals(object obj) => Equals(obj as TypeMapping);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (CSharpType != null ? CSharpType.GetHashCode() : 0);
            hash = hash * 31 + (WriteMethod != null ? WriteMethod.GetHashCode() : 0);
            hash = hash * 31 + (ReadMethod != null ? ReadMethod.GetHashCode() : 0);
            hash = hash * 31 + IsNullableReference.GetHashCode();
            hash = hash * 31 + Kind.GetHashCode();
            return hash;
        }
    }
}
