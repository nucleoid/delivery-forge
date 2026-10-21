using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.CodeAnalysis;

namespace DeliveryForge.Evidence;

// Exact CLI identity DATA in the SAME target-5.9 island.
// This table does not discharge bodies, initializers, nested regions or coverage.
internal static class CompilerNamedMethodBindings
{
    internal sealed record Binding(int? MethodToken, string Disposition, IReadOnlyList<int> PdbStateMachineCarriers);
    internal sealed record FieldBinding(int? FieldToken, string Disposition);
    internal sealed class Table(Dictionary<IMethodSymbol, Binding> bindings, HashSet<int> namedCarriers,
        Func<IFieldSymbol, FieldBinding> fieldBinding, Func<IMethodSymbol, Binding> constructorTarget)
    {
        private static readonly Binding Unmapped = new(null, "not-in-source-named-member-table", []);
        internal Binding For(IMethodSymbol? method) => method is not null && bindings.TryGetValue(Normalize(method), out var binding)
            ? binding : Unmapped;
        internal bool IsNamedCarrier(int token) => namedCarriers.Contains(token);
        internal FieldBinding ForField(IFieldSymbol field) => fieldBinding(field.OriginalDefinition);
        internal Binding ConstructorTarget(IMethodSymbol method) => constructorTarget(method);
    }

    private sealed class UnsupportedSignature(string reason) : Exception(reason);
    private static IMethodSymbol Normalize(IMethodSymbol method) => (method.PartialImplementationPart ?? method).OriginalDefinition;

    // step/admit are the caller's EXISTING shared 1M-step/128MiB preadmission
    // accounting. No independent clock, expanded budget or new file authority.
    internal static Table Capture(Compilation compilation, MetadataReader metadata, MetadataReader pdb,
        Action step, Action<long, string?[]> admit)
    {
        const int maximumRows = 10000;
        var grammar = new Grammar(metadata,
            compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.IsExternalInit"), step, admit);
        var sourceMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var sourceKeys = new Dictionary<IMethodSymbol, string?>(SymbolEqualityComparer.Default);
        var sourceReasons = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
        var sourceByKey = new Dictionary<string, List<IMethodSymbol>>(StringComparer.Ordinal);
        var metadataByKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var stateMachinesByKickoff = new Dictionary<int, List<int>>();

        void AddSource(IMethodSymbol method)
        {
            step();
            method = Normalize(method);
            if (sourceMethods.Contains(method)) return;
            if (sourceMethods.Count >= maximumRows) throw new IOException("Named source membership exceeds the existing bounded method profile.");
            admit(512, []);
            sourceMethods.Add(method);
            string? key = null;
            var reason = "exact-signature-candidate";
            try { key = grammar.SourceMethod(method); }
            catch (UnsupportedSignature unsupported) { reason = unsupported.Message; }
            sourceKeys.Add(method, key);
            sourceReasons.Add(method, reason);
            if (key is null) return;
            if (!sourceByKey.TryGetValue(key, out var members))
            {
                admit(128, []);
                sourceByKey.Add(key, members = []);
            }
            admit(32, []);
            members.Add(method);
        }

        void VisitType(INamedTypeSymbol type)
        {
            step();
            foreach (var member in type.GetMembers())
            {
                step();
                switch (member)
                {
                    case IMethodSymbol method: AddSource(method); break;
                    case IPropertySymbol property:
                        if (property.GetMethod is { } getter) AddSource(getter);
                        if (property.SetMethod is { } setter) AddSource(setter);
                        break;
                    case IEventSymbol @event:
                        if (@event.AddMethod is { } add) AddSource(add);
                        if (@event.RemoveMethod is { } remove) AddSource(remove);
                        if (@event.RaiseMethod is { } raise) AddSource(raise);
                        break;
                    case INamedTypeSymbol nested: VisitType(nested); break;
                }
            }
        }

        void VisitNamespace(INamespaceSymbol space)
        {
            step();
            foreach (var type in space.GetTypeMembers()) { step(); VisitType(type); }
            foreach (var nested in space.GetNamespaceMembers()) { step(); VisitNamespace(nested); }
        }
        VisitNamespace(compilation.SourceModule.GlobalNamespace);

        if (metadata.MethodDefinitions.Count > maximumRows)
            throw new IOException("Named emitted membership exceeds the existing bounded method profile.");
        foreach (var handle in metadata.MethodDefinitions)
        {
            step();
            var methodToken = MetadataTokens.GetToken(handle);
            string? key = null;
            try { key = grammar.MetadataMethod(handle); }
            catch (UnsupportedSignature) { } // An unsupported signature cannot contribute any lookup key.
            if (key is not null)
            {
                if (!metadataByKey.TryGetValue(key, out var members))
                {
                    admit(128, []);
                    metadataByKey.Add(key, members = []);
                }
                admit(32, []);
                members.Add(methodToken);
            }
            // Actual portable PDB StateMachineMethod relation; no generated-name
            // or attribute heuristic and no claim about other helper methods.
            var kickoff = pdb.GetMethodDebugInformation(handle).GetStateMachineKickoffMethod();
            if (kickoff.IsNil) continue;
            if (MetadataTokens.GetRowNumber(kickoff) > metadata.MethodDefinitions.Count)
                throw new BadImageFormatException("PDB state-machine kickoff is outside the exact method table.");
            var kickoffToken = MetadataTokens.GetToken(kickoff);
            admit(256, []);
            if (!stateMachinesByKickoff.TryGetValue(kickoffToken, out var carriers))
            {
                admit(128, []);
                stateMachinesByKickoff.Add(kickoffToken, carriers = []);
            }
            carriers.Add(methodToken);
        }

        var bindings = new Dictionary<IMethodSymbol, Binding>(SymbolEqualityComparer.Default);
        foreach (var method in sourceMethods)
        {
            step();
            var key = sourceKeys[method];
            int? methodToken = null;
            var reason = sourceReasons[method];
            IReadOnlyList<int> carriers = [];
            if (key is not null)
            {
                if (sourceByKey[key].Count != 1) reason = "ambiguous-source-cli-signature";
                else if (!metadataByKey.TryGetValue(key, out var matches)) reason = "no-exact-emitted-cli-signature";
                else if (matches.Count != 1) reason = "ambiguous-emitted-cli-signature";
                else
                {
                    methodToken = matches[0];
                    reason = "unique-exact-cli-signature-data-only";
                    if (stateMachinesByKickoff.TryGetValue(methodToken.Value, out var linked))
                    {
                        admit(64L + 32L * linked.Count, []);
                        carriers = linked.ToArray();
                    }
                }
            }
            admit(256, [reason]);
            var binding = new Binding(methodToken, reason, carriers);
            bindings.Add(method, binding);
        }
        var namedCarriers = new HashSet<int>();
        foreach (var binding in bindings.Values)
        {
            step();
            if (binding.MethodToken is not { } methodToken) continue;
            if (!namedCarriers.Contains(methodToken)) { admit(64, []); namedCarriers.Add(methodToken); }
            foreach (var carrier in binding.PdbStateMachineCarriers)
            {
                step();
                if (!namedCarriers.Contains(carrier)) { admit(64, []); namedCarriers.Add(carrier); }
            }
        }
        var fields = new Dictionary<IFieldSymbol, FieldBinding>(SymbolEqualityComparer.Default);
        var emittedFields = new Dictionary<FieldDefinitionHandle, string?>();
        FieldBinding Field(IFieldSymbol field)
        {
            step();
            if (fields.TryGetValue(field, out var previous)) return previous;
            if (fields.Count >= maximumRows || metadata.FieldDefinitions.Count > maximumRows)
                throw new IOException("Initializer field membership exceeds the existing bounded profile.");
            int? found = null;
            var count = 0;
            var reason = "no-exact-emitted-field-signature";
            try
            {
                var key = grammar.SourceField(field);
                foreach (var handle in metadata.FieldDefinitions)
                {
                    step();
                    var row = metadata.GetFieldDefinition(handle);
                    if (!metadata.StringComparer.Equals(row.Name, field.MetadataName) ||
                        ((row.Attributes & FieldAttributes.Static) != 0) != field.IsStatic) continue;
                    if (!emittedFields.TryGetValue(handle, out var emitted))
                    {
                        try { emitted = grammar.MetadataField(handle); }
                        catch (UnsupportedSignature) { emitted = null; }
                        admit(128, []); emittedFields.Add(handle, emitted);
                    }
                    if (key != emitted) continue;
                    found = MetadataTokens.GetToken(handle); count++;
                }
                reason = count switch
                {
                    1 => "unique-exact-cli-field-signature",
                    > 1 => "ambiguous-emitted-field-signature",
                    _ => reason
                };
            }
            catch (UnsupportedSignature unsupported) { reason = unsupported.Message; }
            admit(256, [reason]);
            var result = new FieldBinding(count == 1 ? found : null, reason);
            fields.Add(field, result);
            return result;
        }
        Dictionary<string, List<int>>? constructorReferences = null;
        var targets = new Dictionary<IMethodSymbol, Binding>(SymbolEqualityComparer.Default);
        Binding ConstructorTarget(IMethodSymbol method)
        {
            step();
            if (method.MethodKind != MethodKind.Constructor || method.IsStatic)
            {
                admit(256, ["not-instance-constructor-target"]);
                return new(null, "not-instance-constructor-target", []);
            }
            // A constructed external declaring type requires TypeSpec association,
            // which is deliberately outside this first exact-reference slice.
            if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, method.ContainingType.OriginalDefinition))
            {
                admit(256, ["constructed-constructor-target-unsupported"]);
                return new(null, "constructed-constructor-target-unsupported", []);
            }
            method = Normalize(method);
            if (bindings.TryGetValue(method, out var own)) return own;
            if (targets.TryGetValue(method, out var previous)) return previous;
            if (targets.Count >= maximumRows || metadata.MemberReferences.Count > maximumRows)
                throw new IOException("Constructor target membership exceeds the existing bounded profile.");
            int? selected = null;
            var reason = "no-exact-emitted-constructor-reference";
            try
            {
                if (constructorReferences is null)
                {
                    admit(128, []); constructorReferences = new(StringComparer.Ordinal);
                    foreach (var handle in metadata.MemberReferences)
                    {
                        step();
                        var member = metadata.GetMemberReference(handle);
                        if (member.GetKind() != MemberReferenceKind.Method || !metadata.StringComparer.Equals(member.Name, ".ctor")) continue;
                        string key;
                        try { key = grammar.MetadataConstructorReference(handle); }
                        catch (UnsupportedSignature) { continue; }
                        if (!constructorReferences.TryGetValue(key, out var matches))
                        {
                            admit(128, []); constructorReferences.Add(key, matches = []);
                        }
                        admit(32, []); matches.Add(MetadataTokens.GetToken(handle));
                    }
                }
                if (constructorReferences.TryGetValue(grammar.SourceMethod(method), out var selectedMatches))
                {
                    reason = selectedMatches.Count == 1 ? "unique-exact-constructor-reference" : "ambiguous-constructor-reference";
                    if (selectedMatches.Count == 1) selected = selectedMatches[0];
                }
            }
            catch (UnsupportedSignature unsupported) { reason = unsupported.Message; }
            admit(256, [reason]);
            var result = new Binding(selected, reason, []);
            targets.Add(method, result); return result;
        }
        return new(bindings, namedCarriers, Field, ConstructorTarget);
    }

    private sealed class Grammar(MetadataReader reader, INamedTypeSymbol? initOnlyMarker, Action step,
        Action<long, string?[]> admit) : ISignatureTypeProvider<string, object?>
    {
        private readonly Dictionary<ITypeSymbol, string> sourceTypes = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<INamedTypeSymbol, string> sourceNames = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<EntityHandle, string> metadataNames = [];
        private readonly Dictionary<AssemblyReferenceHandle, string> referenceAssemblies = [];
        private readonly Dictionary<string, string> modifierIdentities = new(StringComparer.Ordinal);
        private readonly HashSet<EntityHandle> namesInProgress = [];
        private string? ownAssembly;

        internal void CheckText(string? value)
        {
            step();
            if (value?.Length > 16384) throw new UnsupportedSignature("signature-identity-outside-existing-identity-profile");
        }

        private string Key(string tag, params string[] parts)
        {
            step();
            long length = tag.Length + 2;
            foreach (var part in parts)
            {
                step(); CheckText(part);
                length = checked(length + 12L + part.Length);
            }
            if (length > 16384) throw new UnsupportedSignature("signature-identity-outside-existing-identity-profile");
            admit(64L + 8L * length, []);
            // Length framing preserves identifiers containing punctuation.
            return tag + "[" + string.Concat(parts.Select(part => part.Length.ToString(CultureInfo.InvariantCulture) + ":" + part)) + "]";
        }

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        internal string SourceField(IFieldSymbol field)
        {
            step();
            if (field.IsConst || field.RefKind != RefKind.None || !field.CustomModifiers.IsEmpty || !field.RefCustomModifiers.IsEmpty)
                throw new UnsupportedSignature("initializer-field-modifiers-unsupported");
            return Key("field", SourceNamedIdentity(field.ContainingType.OriginalDefinition), field.MetadataName,
                field.IsStatic ? "static" : "instance", SourceType(field.Type));
        }

        internal string MetadataField(FieldDefinitionHandle handle)
        {
            step();
            var field = reader.GetFieldDefinition(handle);
            return Key("field", MetadataNamedIdentity(field.GetDeclaringType()), reader.GetString(field.Name),
                (field.Attributes & FieldAttributes.Static) != 0 ? "static" : "instance", field.DecodeSignature(this, null));
        }

        internal string MetadataConstructorReference(MemberReferenceHandle handle)
        {
            step();
            var member = reader.GetMemberReference(handle);
            var signature = member.DecodeMethodSignature(this, genericContext: null);
            if (!reader.StringComparer.Equals(member.Name, ".ctor") || !signature.Header.IsInstance || signature.Header.HasExplicitThis ||
                signature.Header.CallingConvention != SignatureCallingConvention.Default || signature.GenericParameterCount != 0 ||
                signature.RequiredParameterCount != signature.ParameterTypes.Length ||
                signature.ReturnType != GetPrimitiveType(PrimitiveTypeCode.Void))
                throw new UnsupportedSignature("constructor-reference-signature-unsupported");
            admit(64L + 32L * signature.ParameterTypes.Length, []);
            return Method(MetadataNamedIdentity(member.Parent), ".ctor", 0, false,
                signature.ReturnType, signature.ParameterTypes.ToArray());
        }


        internal string SourceMethod(IMethodSymbol method)
        {
            step();
            if (method.IsVararg) throw new UnsupportedSignature("vararg-signature-unsupported");
            var initReturn = method.IsInitOnly && method.MethodKind == MethodKind.PropertySet && method.ReturnsVoid &&
                !method.ReturnsByRef && !method.ReturnsByRefReadonly && method.RefCustomModifiers.IsEmpty &&
                method.ReturnTypeCustomModifiers.Length == 1 && !method.ReturnTypeCustomModifiers[0].IsOptional &&
                initOnlyMarker is not null &&
                SymbolEqualityComparer.Default.Equals(method.ReturnTypeCustomModifiers[0].Modifier, initOnlyMarker);
            if (!method.RefCustomModifiers.IsEmpty ||
                (!method.ReturnTypeCustomModifiers.IsEmpty && !initReturn) || (method.IsInitOnly && !initReturn))
                throw new UnsupportedSignature("custom-modifier-signature-unsupported");
            admit(64L + 32L * method.Parameters.Length, []);
            var parameters = new string[method.Parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                step(); var parameter = method.Parameters[i];
                if (!parameter.CustomModifiers.IsEmpty || !parameter.RefCustomModifiers.IsEmpty)
                    throw new UnsupportedSignature("custom-modifier-signature-unsupported");
                var type = SourceType(parameter.Type);
                parameters[i] = parameter.RefKind == RefKind.None ? type : Key("byref", type);
            }
            var returns = SourceType(method.ReturnType);
            if (method.ReturnsByRef || method.ReturnsByRefReadonly) returns = Key("byref", returns);
            if (initReturn)
                returns = Key("required-modifier", Key("modifier-identity",
                    SourceNamedIdentity(method.ReturnTypeCustomModifiers[0].Modifier.OriginalDefinition)), returns);
            return Method(SourceNamedIdentity(method.ContainingType.OriginalDefinition), method.MetadataName,
                method.Arity, method.IsStatic, returns, parameters);
        }

        internal string MetadataMethod(MethodDefinitionHandle handle)
        {
            step(); var method = reader.GetMethodDefinition(handle);
            var signature = method.DecodeSignature(this, genericContext: null);
            if (signature.Header.CallingConvention != SignatureCallingConvention.Default || signature.Header.HasExplicitThis ||
                signature.RequiredParameterCount != signature.ParameterTypes.Length ||
                signature.Header.IsGeneric != (signature.GenericParameterCount != 0) ||
                signature.GenericParameterCount != method.GetGenericParameters().Count)
                throw new UnsupportedSignature("calling-convention-or-vararg-signature-unsupported");
            var isStatic = (method.Attributes & MethodAttributes.Static) != 0;
            if (signature.Header.IsInstance == isStatic)
                throw new BadImageFormatException("Method flags contradict the exact CLI signature header.");
            admit(64L + 32L * signature.ParameterTypes.Length, []);
            return Method(MetadataNamedIdentity(method.GetDeclaringType()), reader.GetString(method.Name),
                signature.GenericParameterCount, isStatic, signature.ReturnType, signature.ParameterTypes.ToArray());
        }

        private string Method(string declaringType, string name, int arity, bool isStatic, string returns, string[] parameters) =>
            Key("method", declaringType, name, Number(arity), isStatic ? "static" : "instance", "default-no-explicitthis",
                returns, Key("parameters", parameters));

        private string SourceType(ITypeSymbol type)
        {
            step();
            if (sourceTypes.TryGetValue(type, out var found)) return found;
            string key;
            if (type is IDynamicTypeSymbol) key = GetPrimitiveType(PrimitiveTypeCode.Object);
            else if (Primitive(type.SpecialType) is { } primitive) key = GetPrimitiveType(primitive);
            else switch (type)
            {
                case ITypeParameterSymbol parameter:
                    if (parameter.TypeParameterKind is not (TypeParameterKind.Type or TypeParameterKind.Method))
                        throw new UnsupportedSignature("generic-parameter-owner-signature-unsupported");
                    var ordinal = parameter.Ordinal;
                    if (parameter.TypeParameterKind == TypeParameterKind.Type)
                        for (var outer = parameter.ContainingType?.ContainingType; outer is not null; outer = outer.ContainingType)
                        { step(); ordinal = checked(ordinal + outer.Arity); }
                    key = Key(parameter.TypeParameterKind == TypeParameterKind.Method ? "method-parameter" : "type-parameter", Number(ordinal));
                    break;
                case IArrayTypeSymbol array:
                    if (!array.CustomModifiers.IsEmpty) throw new UnsupportedSignature("custom-modifier-signature-unsupported");
                    if (!array.Sizes.IsEmpty || !array.LowerBounds.IsDefaultOrEmpty)
                        throw new UnsupportedSignature("explicit-array-shape-signature-unsupported");
                    key = array.IsSZArray ? Key("szarray", SourceType(array.ElementType))
                        : Key("array", SourceType(array.ElementType), Number(array.Rank), "sizes=", "lowerbounds=");
                    break;
                case IPointerTypeSymbol pointer:
                    if (!pointer.CustomModifiers.IsEmpty) throw new UnsupportedSignature("custom-modifier-signature-unsupported");
                    key = Key("pointer", SourceType(pointer.PointedAtType));
                    break;
                case INamedTypeSymbol named when named.TypeKind != TypeKind.Error && !named.IsUnboundGenericType:
                    named = named.TupleUnderlyingType ?? named;
                    key = Key(named.IsValueType ? "valuetype" : "class", SourceNamedIdentity(named.OriginalDefinition));
                    var arguments = new List<string>();
                    void Arguments(INamedTypeSymbol current)
                    {
                        step();
                        if (current.ContainingType is { } containing) Arguments(containing);
                        for (var i = 0; i < current.TypeArguments.Length; i++)
                        {
                            step();
                            if (!current.GetTypeArgumentCustomModifiers(i).IsEmpty)
                                throw new UnsupportedSignature("custom-modifier-signature-unsupported");
                            admit(32, []); arguments.Add(SourceType(current.TypeArguments[i]));
                        }
                    }
                    Arguments(named);
                    if (arguments.Count != 0)
                    {
                        admit(64L + 32L * arguments.Count, []);
                        key = Key("generic-instance", key, Key("arguments", arguments.ToArray()));
                    }
                    break;
                default: throw new UnsupportedSignature("source-type-signature-form-unsupported");
            }
            admit(128, []);
            sourceTypes.Add(type, key);
            return key;
        }

        private string SourceNamedIdentity(INamedTypeSymbol type)
        {
            step();
            if (sourceNames.TryGetValue(type, out var found)) return found;
            var owner = type.ContainingType is { } enclosing ? SourceNamedIdentity(enclosing.OriginalDefinition)
                : Key("namespace", type.ContainingAssembly.Identity.GetDisplayName(),
                    SourceNamespace(type.ContainingNamespace));
            var key = Key("named-identity", owner, type.MetadataName);
            admit(128, []); sourceNames.Add(type, key); return key;
        }

        private string SourceNamespace(INamespaceSymbol space)
        {
            step();
            if (space.IsGlobalNamespace) return string.Empty;
            var parent = SourceNamespace(space.ContainingNamespace);
            CheckText(space.MetadataName);
            var length = checked(parent.Length + space.MetadataName.Length + (parent.Length == 0 ? 0 : 1));
            if (length > 16384) throw new UnsupportedSignature("signature-identity-outside-existing-identity-profile");
            admit(64L + 8L * length, []);
            return parent.Length == 0 ? space.MetadataName : parent + "." + space.MetadataName;
        }

        private string MetadataNamedIdentity(EntityHandle handle)
        {
            step();
            if (metadataNames.TryGetValue(handle, out var found)) return found;
            if (!namesInProgress.Add(handle)) throw new BadImageFormatException("Cyclic metadata type identity.");
            try
            {
                string owner, name;
                if (handle.Kind == HandleKind.TypeDefinition)
                {
                    var type = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                    name = reader.GetString(type.Name);
                    owner = type.GetDeclaringType().IsNil
                        ? Key("namespace", OwnAssembly(), reader.GetString(type.Namespace))
                        : MetadataNamedIdentity(type.GetDeclaringType());
                }
                else if (handle.Kind == HandleKind.TypeReference)
                {
                    var type = reader.GetTypeReference((TypeReferenceHandle)handle);
                    name = reader.GetString(type.Name);
                    owner = type.ResolutionScope.Kind switch
                    {
                        HandleKind.TypeReference => MetadataNamedIdentity(type.ResolutionScope),
                        HandleKind.AssemblyReference => Key("namespace", ReferencedAssembly((AssemblyReferenceHandle)type.ResolutionScope), reader.GetString(type.Namespace)),
                        HandleKind.ModuleDefinition => Key("namespace", OwnAssembly(), reader.GetString(type.Namespace)),
                        _ => throw new UnsupportedSignature("module-or-unresolved-type-scope-unsupported")
                    };
                }
                else throw new UnsupportedSignature("metadata-type-identity-form-unsupported");
                var key = Key("named-identity", owner, name);
                admit(128, []); metadataNames.Add(handle, key); return key;
            }
            finally { namesInProgress.Remove(handle); }
        }

        private string OwnAssembly()
        {
            if (ownAssembly is not null) return ownAssembly;
            step(); var definition = reader.GetAssemblyDefinition();
            ownAssembly = Assembly(reader.GetString(definition.Name), definition.Version, reader.GetString(definition.Culture),
                reader.GetBlobBytes(definition.PublicKey), definition.Flags);
            return ownAssembly;
        }

        private string ReferencedAssembly(AssemblyReferenceHandle handle)
        {
            if (referenceAssemblies.TryGetValue(handle, out var found)) return found;
            step(); var reference = reader.GetAssemblyReference(handle);
            var key = Assembly(reader.GetString(reference.Name), reference.Version, reader.GetString(reference.Culture),
                reader.GetBlobBytes(reference.PublicKeyOrToken), reference.Flags);
            admit(128, []); referenceAssemblies.Add(handle, key); return key;
        }

        private string Assembly(string name, Version version, string culture, byte[] key, AssemblyFlags flags)
        {
            step(); CheckText(name); CheckText(culture); admit(128L + key.Length, [name, culture]);
            var contentType = flags & AssemblyFlags.ContentTypeMask;
            if (contentType != 0 && contentType != AssemblyFlags.WindowsRuntime)
                throw new UnsupportedSignature("unknown-assembly-content-type-unsupported");
            return new AssemblyIdentity(name, version, culture, ImmutableArray.Create(key),
                (flags & AssemblyFlags.PublicKey) != 0, (flags & AssemblyFlags.Retargetable) != 0,
                contentType == AssemblyFlags.WindowsRuntime ? AssemblyContentType.WindowsRuntime : AssemblyContentType.Default).GetDisplayName();
        }

        private static PrimitiveTypeCode? Primitive(SpecialType type) => type switch
        {
            SpecialType.System_Void => PrimitiveTypeCode.Void,
            SpecialType.System_Boolean => PrimitiveTypeCode.Boolean,
            SpecialType.System_Char => PrimitiveTypeCode.Char,
            SpecialType.System_SByte => PrimitiveTypeCode.SByte,
            SpecialType.System_Byte => PrimitiveTypeCode.Byte,
            SpecialType.System_Int16 => PrimitiveTypeCode.Int16,
            SpecialType.System_UInt16 => PrimitiveTypeCode.UInt16,
            SpecialType.System_Int32 => PrimitiveTypeCode.Int32,
            SpecialType.System_UInt32 => PrimitiveTypeCode.UInt32,
            SpecialType.System_Int64 => PrimitiveTypeCode.Int64,
            SpecialType.System_UInt64 => PrimitiveTypeCode.UInt64,
            SpecialType.System_Single => PrimitiveTypeCode.Single,
            SpecialType.System_Double => PrimitiveTypeCode.Double,
            SpecialType.System_String => PrimitiveTypeCode.String,
            SpecialType.System_Object => PrimitiveTypeCode.Object,
            SpecialType.System_IntPtr => PrimitiveTypeCode.IntPtr,
            SpecialType.System_UIntPtr => PrimitiveTypeCode.UIntPtr,
            SpecialType.System_TypedReference => PrimitiveTypeCode.TypedReference,
            _ => null
        };

        public string GetArrayType(string elementType, ArrayShape shape)
        {
            step();
            // C# source arrays have no signature sizes/lower bounds. A metadata
            // shape with either is outside this explicit supported association.
            if (!shape.Sizes.IsEmpty || !shape.LowerBounds.IsEmpty)
                throw new UnsupportedSignature("explicit-array-shape-signature-unsupported");
            return Key("array", elementType, Number(shape.Rank), "sizes=", "lowerbounds=");
        }
        public string GetByReferenceType(string elementType) => Key("byref", elementType);
        public string GetPointerType(string elementType) => Key("pointer", elementType);
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => Key("primitive", typeCode.ToString());
        public string GetSZArrayType(string elementType) => Key("szarray", elementType);
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            step(); admit(64L + 32L * typeArguments.Length, []);
            return Key("generic-instance", genericType, Key("arguments", typeArguments.ToArray()));
        }
        public string GetGenericMethodParameter(object? context, int index) => Key("method-parameter", Number(index));
        public string GetGenericTypeParameter(object? context, int index) => Key("type-parameter", Number(index));
        public string GetTypeFromDefinition(MetadataReader metadata, TypeDefinitionHandle handle, byte rawTypeKind) => Named(handle, rawTypeKind);
        public string GetTypeFromReference(MetadataReader metadata, TypeReferenceHandle handle, byte rawTypeKind) => Named(handle, rawTypeKind);
        private string Named(EntityHandle handle, byte rawTypeKind)
        {
            if (rawTypeKind == (byte)SignatureTypeKind.Unknown)
            {
                // A modifier TypeDefOrRef does not assert class/value kind.
                // Keep its exact scoped identity in a disjoint key namespace;
                // this key can never match an ordinary source type signature.
                var identity = MetadataNamedIdentity(handle);
                var modifier = Key("modifier-identity", identity);
                if (!modifierIdentities.ContainsKey(modifier))
                {
                    admit(128, []);
                    modifierIdentities.Add(modifier, identity);
                }
                return modifier;
            }
            return rawTypeKind switch
            {
                (byte)SignatureTypeKind.Class => Key("class", MetadataNamedIdentity(handle)),
                (byte)SignatureTypeKind.ValueType => Key("valuetype", MetadataNamedIdentity(handle)),
                _ => throw new UnsupportedSignature("unknown-named-type-signature-kind")
            };
        }
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        {
            step();
            // Singleton modreq on plain void only. A second modifier, modopt,
            // modified byref or a different marker/scope cannot take this route.
            if (!isRequired || initOnlyMarker is null ||
                !modifierIdentities.TryGetValue(modifier, out var identity) ||
                identity != SourceNamedIdentity(initOnlyMarker.OriginalDefinition) ||
                unmodifiedType != GetPrimitiveType(PrimitiveTypeCode.Void))
                throw new UnsupportedSignature("custom-modifier-signature-unsupported");
            return Key("required-modifier", modifier, unmodifiedType);
        }
        public string GetFunctionPointerType(MethodSignature<string> signature) =>
            throw new UnsupportedSignature("function-pointer-signature-unsupported");
        public string GetPinnedType(string elementType) => throw new UnsupportedSignature("pinned-method-signature-unsupported");
        public string GetTypeFromSpecification(MetadataReader metadata, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            throw new UnsupportedSignature("type-specification-handle-signature-unsupported");
    }
}
