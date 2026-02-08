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
/// for types annotated with [AkkaSerializerModule] and [AkkaSerializable].
/// </summary>
[Generator]
public class AkkaSerializerGenerator : IIncrementalGenerator
{
    private const string SerializerModuleAttributeFullName = "Akka.Serialization.V2.AkkaSerializerModuleAttribute";
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
        "[AkkaSerializable] types exist but no [AkkaSerializerModule] class was found",
        "Akka.Serialization", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateFieldIndexDiagnostic = new DiagnosticDescriptor(
        "AKKA006", "Duplicate field index",
        "[AkkaSerializable] type '{0}' has duplicate field index {1}",
        "Akka.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateManifestDiagnostic = new DiagnosticDescriptor(
        "AKKA007", "Duplicate manifest",
        "Multiple [AkkaSerializable] types share manifest '{0}': {1}",
        "Akka.Serialization", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Step 1: Find all types with [AkkaSerializerModule]
        var moduleProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                SerializerModuleAttributeFullName,
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

        // Step 4: Combine each module with all serializable types and compilation
        var combined = moduleProvider.Combine(serializableCollection);

        // Step 5: Register source output
        context.RegisterSourceOutput(combined, static (spc, pair) =>
        {
            var module = pair.Left;
            var serializables = pair.Right;

            if (module == null)
                return;

            // Validate and report diagnostics
            ValidateSerializables(spc, serializables);
            ValidateManifestUniqueness(spc, serializables);

            var source = GenerateSerializerCode(module, serializables);
            var hintName = module.ClassName + ".g.cs";
            spc.AddSource(hintName, source);
        });

        // Step 6: AKKA004 - Warn if [AkkaSerializable] types exist but no module found
        var moduleCollection = moduleProvider.Collect();
        var noModuleCheck = serializableCollection.Combine(moduleCollection);
        context.RegisterSourceOutput(noModuleCheck, static (spc, pair) =>
        {
            var serializables = pair.Left;
            var modules = pair.Right;
            if (serializables.Length > 0 && modules.Length == 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NoModuleDiagnostic, Location.None));
            }
        });
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
        }
    }

    private static void ValidateManifestUniqueness(
        SourceProductionContext spc,
        ImmutableArray<SerializableTypeInfo> serializables)
    {
        // AKKA007: Duplicate manifests
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

        // Extract SerializerId from the attribute
        var attr = context.Attributes[0];
        int serializerId = 0;

        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "SerializerId" && named.Value.Value is int id)
            {
                serializerId = id;
            }
        }

        // Get the fully qualified namespace
        var namespaceName = GetFullNamespace(symbol);
        var className = symbol.Name;

        return new ModuleInfo(namespaceName, className, serializerId);
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

        return new SerializableTypeInfo(fullyQualifiedName, simpleName, manifest, fields.ToArray());
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

        // Map types to write/read methods
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
        if (typeSymbol is IArrayTypeSymbol arrayType &&
            arrayType.ElementType.SpecialType == SpecialType.System_Byte)
        {
            return new TypeMapping("byte[]", "WriteBytes", "ReadBytes() ?? System.Array.Empty<byte>()",
                isNullableReference: false, nullDefault: "System.Array.Empty<byte>()",
                readExpression: "ReadBytes() ?? System.Array.Empty<byte>()");
        }

        // Fallback - unsupported type
        return new TypeMapping(fullName, "/* UNSUPPORTED */", "/* UNSUPPORTED */",
            isNullableReference: false, nullDefault: "default");
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

        sb.AppendLine("public partial class " + module.ClassName + " : Akka.Serialization.V2.SerializerV2");
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

        sb.AppendLine("}");
        sb.AppendLine();

        // Generate SerializerSetup class (always)
        GenerateSerializerSetup(sb, module, serializables);

        // Note: Akka.Hosting extension method is NOT generated because
        // SerializerV2 is not a subclass of Akka.Serialization.Serializer.
        // V2 serializers need their own Akka.Hosting registration API.

        return sb.ToString();
    }

    private static void GenerateSerializerSetup(
        StringBuilder sb,
        ModuleInfo module,
        ImmutableArray<SerializableTypeInfo> serializables)
    {
        var setupClassName = module.ClassName + "Setup";

        if (!string.IsNullOrEmpty(module.Namespace))
        {
            // Already in same namespace from file-scoped namespace
        }

        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// SerializerSetup for " + module.ClassName + ". Use with Akka.NET configuration.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public sealed class " + setupClassName);
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly " + setupClassName + " Instance = new();");
        sb.AppendLine();

        sb.AppendLine("    public System.Type SerializerType => typeof(" + module.ClassName + ");");
        sb.AppendLine();

        // Bound types
        sb.AppendLine("    public static System.Type[] BoundTypes => new System.Type[]");
        sb.AppendLine("    {");
        for (int i = 0; i < serializables.Length; i++)
        {
            var s = serializables[i];
            if (s == null) continue;
            var comma = i < serializables.Length - 1 ? "," : "";
            sb.AppendLine("        typeof(" + s.FullyQualifiedName + ")" + comma);
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

        sb.AppendLine("        _ => throw new System.ArgumentException($\"Unsupported type: {obj.GetType()}\", nameof(obj))");
        sb.AppendLine("    };");
    }

    private static void GenerateWriteMethod(
        StringBuilder sb,
        ImmutableArray<SerializableTypeInfo> serializables)
    {
        sb.AppendLine("    public override void Write(Akka.Serialization.V2.ICodecWriter writer, object obj)");
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
        sb.AppendLine("                throw new System.ArgumentException($\"Unsupported type: {obj.GetType()}\", nameof(obj));");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void GenerateReadMethod(
        StringBuilder sb,
        ImmutableArray<SerializableTypeInfo> serializables)
    {
        sb.AppendLine("    public override object Read(Akka.Serialization.V2.ICodecReader reader, string manifest)");
        sb.AppendLine("    {");
        sb.AppendLine("        return manifest switch");
        sb.AppendLine("        {");

        for (int i = 0; i < serializables.Length; i++)
        {
            var s = serializables[i];
            if (s == null) continue;
            sb.AppendLine("            \"" + s.Manifest + "\" => Read" + s.SimpleName + "(reader),");
        }

        sb.AppendLine("            _ => throw new System.ArgumentException($\"Unknown manifest: {manifest}\", nameof(manifest))");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
    }

    private static void GenerateWriteTypeMethod(
        StringBuilder sb,
        SerializableTypeInfo type)
    {
        sb.AppendLine("    private void Write" + type.SimpleName + "(Akka.Serialization.V2.ICodecWriter writer, " + type.FullyQualifiedName + " msg)");
        sb.AppendLine("    {");
        sb.AppendLine("        writer.BeginObject(" + type.Fields.Length + ");");

        for (int i = 0; i < type.Fields.Length; i++)
        {
            var field = type.Fields[i];
            var mapping = field.TypeMapping;

            if (mapping.IsNullableReference)
            {
                // Nullable reference type - check for null
                sb.AppendLine("        if (msg." + field.PropertyName + " is null)");
                sb.AppendLine("            writer.WriteNull();");
                sb.AppendLine("        else");
                sb.AppendLine("            writer." + mapping.WriteMethod + "(msg." + field.PropertyName + ");");
            }
            else if (mapping.WritePrefix != null)
            {
                // Types needing a cast (e.g., decimal -> double)
                sb.AppendLine("        writer." + mapping.WriteMethod + "(" + mapping.WritePrefix + "msg." + field.PropertyName + ");");
            }
            else
            {
                sb.AppendLine("        writer." + mapping.WriteMethod + "(msg." + field.PropertyName + ");");
            }
        }

        sb.AppendLine("    }");
    }

    private static void GenerateReadTypeMethod(
        StringBuilder sb,
        SerializableTypeInfo type)
    {
        sb.AppendLine("    private " + type.FullyQualifiedName + " Read" + type.SimpleName + "(Akka.Serialization.V2.ICodecReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("        var fieldCount = reader.BeginReadObject();");

        // Generate local variables for each field with fieldCount guards for backward compat
        for (int i = 0; i < type.Fields.Length; i++)
        {
            var field = type.Fields[i];
            var mapping = field.TypeMapping;
            var varName = ToCamelCase(field.PropertyName);

            if (mapping.IsNullableReference)
            {
                // Nullable reference - use TryReadNull, default to null if field missing
                sb.AppendLine("        var " + varName + " = fieldCount > " + i + " ? (reader.TryReadNull() ? null : reader." + mapping.ReadMethod + "()) : " + mapping.NullDefault + ";");
            }
            else if (mapping.ReadExpression != null)
            {
                // Custom read expression (e.g., ReadString() ?? string.Empty)
                sb.AppendLine("        var " + varName + " = fieldCount > " + i + " ? reader." + mapping.ReadExpression + " : " + mapping.NullDefault + ";");
            }
            else if (mapping.ReadPrefix != null)
            {
                // Types needing a cast on read
                sb.AppendLine("        var " + varName + " = fieldCount > " + i + " ? " + mapping.ReadPrefix + "reader." + mapping.ReadMethod + "() : " + mapping.NullDefault + ";");
            }
            else
            {
                sb.AppendLine("        var " + varName + " = fieldCount > " + i + " ? reader." + mapping.ReadMethod + "() : " + mapping.NullDefault + ";");
            }
        }

        // Skip unknown trailing fields
        sb.AppendLine("        for (int i = " + type.Fields.Length + "; i < fieldCount; i++)");
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

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private static string ToKebabCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
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
    public int SerializerId { get; }

    public ModuleInfo(string ns, string className, int serializerId)
    {
        Namespace = ns;
        ClassName = className;
        SerializerId = serializerId;
    }

    public bool Equals(ModuleInfo other)
    {
        if (other == null) return false;
        return Namespace == other.Namespace
            && ClassName == other.ClassName
            && SerializerId == other.SerializerId;
    }

    public override bool Equals(object obj) => Equals(obj as ModuleInfo);
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (Namespace != null ? Namespace.GetHashCode() : 0);
            hash = hash * 31 + (ClassName != null ? ClassName.GetHashCode() : 0);
            hash = hash * 31 + SerializerId.GetHashCode();
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

    public SerializableTypeInfo(string fullyQualifiedName, string simpleName, string manifest, FieldInfo[] fields)
    {
        FullyQualifiedName = fullyQualifiedName;
        SimpleName = simpleName;
        Manifest = manifest;
        Fields = fields;
    }

    public bool Equals(SerializableTypeInfo other)
    {
        if (other == null) return false;
        if (FullyQualifiedName != other.FullyQualifiedName
            || SimpleName != other.SimpleName
            || Manifest != other.Manifest
            || Fields.Length != other.Fields.Length)
            return false;

        for (int i = 0; i < Fields.Length; i++)
        {
            if (!Fields[i].Equals(other.Fields[i]))
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
            && ReadExpression == other.ReadExpression;
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
            return hash;
        }
    }
}
