using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace GateKit.Symbols.Managed;

/// <summary>The string literals the code of one method uses.</summary>
public sealed record MethodStrings(string Type, string Method, IReadOnlyList<string> Strings);

/// <summary>Reads a managed (.NET) assembly without loading or running it: its types, the members
/// of each type, and per method the string literals its code uses (ldstr) - the fastest way to
/// find command-line switches, config keys and the like in the Toolkit's editor assemblies.
/// Works on any platform; nothing the assembly references needs to be present.</summary>
public sealed class ManagedStrings : IDisposable
{
    readonly PEReader pe;
    readonly MetadataReader md;

    public ManagedStrings(string assembly)
    {
        if (!File.Exists(assembly))
            throw new SymbolsException($"{assembly} not found");
        pe = new PEReader(File.OpenRead(assembly));
        if (!pe.HasMetadata)
        {
            pe.Dispose();
            throw new SymbolsException($"{assembly} is not a .NET assembly (no CLR metadata)");
        }
        md = pe.GetMetadataReader();
    }

    static Regex Pattern(string? text) => new(string.IsNullOrEmpty(text) ? "." : text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Full name of a type definition as reflection prints it (Outer+Inner for nested types).</summary>
    string FullName(TypeDefinitionHandle h)
    {
        var t = md.GetTypeDefinition(h);
        string name = Escape(md.GetString(t.Name));
        var outer = t.GetDeclaringType();
        if (!outer.IsNil)
            return FullName(outer) + "+" + name;
        string ns = md.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    string FullName(TypeReferenceHandle h)
    {
        var t = md.GetTypeReference(h);
        string name = Escape(md.GetString(t.Name));
        if (t.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullName((TypeReferenceHandle)t.ResolutionScope) + "+" + name;
        string ns = md.GetString(t.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    /// <summary>A name part with the characters reflection escapes in full names (\ , + &amp; * [ ]).</summary>
    static string Escape(string name) =>
        name.IndexOfAny(['\\', ',', '+', '&', '*', '[', ']']) < 0 ? name
            : string.Concat(name.Select(c => c is '\\' or ',' or '+' or '&' or '*' or '[' or ']' ? "\\" + c : c.ToString()));

    /// <summary>Every type, `&lt;Module&gt;` left out, in metadata order.</summary>
    IEnumerable<(TypeDefinitionHandle Handle, string Name)> AllTypes() =>
        md.TypeDefinitions.Select(h => (h, FullName(h))).Where(t => t.Item2 != "<Module>");

    /// <summary>Full names of the types whose name matches the expression, sorted.</summary>
    public List<string> Types(string? type = null)
    {
        var re = Pattern(type);
        return AllTypes().Select(t => t.Name).Where(n => re.IsMatch(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsConstructor(string name) => name is ".ctor" or ".cctor";

    /// <summary>Per method (methods first, then constructors, as reflection lists them) the distinct
    /// string literals matching <paramref name="match"/>, for the types matching <paramref name="type"/>.</summary>
    public List<MethodStrings> Strings(string? type = null, string? match = null)
    {
        var typeRe = Pattern(type);
        var matchRe = Pattern(match);
        var result = new List<MethodStrings>();
        foreach (var (handle, typeName) in AllTypes())
        {
            if (!typeRe.IsMatch(typeName))
                continue;
            var methods = md.GetTypeDefinition(handle).GetMethods().Select(md.GetMethodDefinition).ToList();
            foreach (var m in methods.Where(m => !IsConstructor(md.GetString(m.Name))).Concat(methods.Where(m => IsConstructor(md.GetString(m.Name)))))
            {
                if (m.RelativeVirtualAddress == 0)
                    continue;
                byte[] il;
                try
                {
                    il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes() ?? [];
                }
                catch (BadImageFormatException)
                {
                    continue;
                }
                var found = new List<string>();
                // the opcode byte of ldstr followed by a user string token, as the old script read it
                for (int i = 0; i < il.Length - 4; i++)
                {
                    if (il[i] != 0x72)
                        continue;
                    int token = BitConverter.ToInt32(il, i + 1);
                    if ((token >>> 24) != 0x70)
                        continue;
                    try
                    {
                        string s = md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF));
                        if (matchRe.IsMatch(s) && !found.Contains(s))
                            found.Add(s);
                    }
                    catch (BadImageFormatException)
                    {
                    }
                    i += 4;
                }
                if (found.Count > 0)
                    result.Add(new MethodStrings(typeName, md.GetString(m.Name), found));
            }
        }
        return result;
    }

    /// <summary>"== Type : Base" and one line per method whose name matches: "[static ]Return Name(Type name, ...)".</summary>
    public List<string> Members(string? type = null, string? match = null)
    {
        var typeRe = Pattern(type);
        var matchRe = Pattern(match);
        var shortNames = new NameProvider(this, full: false);
        var lines = new List<string>();
        foreach (var (handle, typeName) in AllTypes())
        {
            if (!typeRe.IsMatch(typeName))
                continue;
            var t = md.GetTypeDefinition(handle);
            lines.Add($"== {typeName} : {BaseName(t.BaseType, new GenericScope(t.GetGenericParameters(), default))}");
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                string name = md.GetString(m.Name);
                if (IsConstructor(name) || !matchRe.IsMatch(name))
                    continue;
                MethodSignature<string> sig;
                try
                {
                    sig = m.DecodeSignature(shortNames, new GenericScope(t.GetGenericParameters(), m.GetGenericParameters()));
                }
                catch (BadImageFormatException)
                {
                    continue;
                }
                var names = new Dictionary<int, string>();
                foreach (var ph in m.GetParameters())
                {
                    var p = md.GetParameter(ph);
                    names[p.SequenceNumber] = md.GetString(p.Name);
                }
                var ps = sig.ParameterTypes.Select((pt, i) => $"{pt} {names.GetValueOrDefault(i + 1, "")}");
                bool isStatic = (m.Attributes & MethodAttributes.Static) != 0;
                lines.Add($"   {(isStatic ? "static " : "")}{sig.ReturnType} {name}({string.Join(", ", ps)})");
            }
        }
        return lines;
    }

    string BaseName(EntityHandle h, GenericScope scope)
    {
        if (h.IsNil)
            return "";
        var full = new NameProvider(this, full: true);
        return h.Kind switch
        {
            HandleKind.TypeDefinition => FullName((TypeDefinitionHandle)h),
            HandleKind.TypeReference => FullName((TypeReferenceHandle)h),
            HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(full, scope),
            _ => "",
        };
    }

    sealed record GenericScope(GenericParameterHandleCollection Type, GenericParameterHandleCollection Method);

    /// <summary>Type names as reflection's Type.Name (short) or Type.ToString() (full) print them.</summary>
    sealed class NameProvider(ManagedStrings owner, bool full) : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            string name = typeCode switch
            {
                PrimitiveTypeCode.Boolean => "Boolean", PrimitiveTypeCode.Char => "Char", PrimitiveTypeCode.SByte => "SByte",
                PrimitiveTypeCode.Byte => "Byte", PrimitiveTypeCode.Int16 => "Int16", PrimitiveTypeCode.UInt16 => "UInt16",
                PrimitiveTypeCode.Int32 => "Int32", PrimitiveTypeCode.UInt32 => "UInt32", PrimitiveTypeCode.Int64 => "Int64",
                PrimitiveTypeCode.UInt64 => "UInt64", PrimitiveTypeCode.Single => "Single", PrimitiveTypeCode.Double => "Double",
                PrimitiveTypeCode.String => "String", PrimitiveTypeCode.Object => "Object", PrimitiveTypeCode.IntPtr => "IntPtr",
                PrimitiveTypeCode.UIntPtr => "UIntPtr", PrimitiveTypeCode.Void => "Void", PrimitiveTypeCode.TypedReference => "TypedReference",
                _ => typeCode.ToString(),
            };
            return full ? "System." + name : name;
        }

        string Short(string fullName)
        {
            int plus = fullName.LastIndexOf('+');
            string last = plus >= 0 ? fullName[(plus + 1)..] : fullName;
            int dot = plus >= 0 ? -1 : last.LastIndexOf('.');
            return dot >= 0 ? last[(dot + 1)..] : last;
        }

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            string name = owner.FullName(handle);
            return full ? name : Short(name);
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            string name = owner.FullName(handle);
            return full ? name : Short(name);
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            full ? StripArity(genericType) + "[" + string.Join(",", typeArguments) + "]" : genericType;

        /// <summary>A generic type as PowerShell printed it inside a base type name: without `N.</summary>
        static string StripArity(string name)
        {
            int tick = name.LastIndexOf('`');
            return tick > 0 && name[(tick + 1)..].All(char.IsAsciiDigit) && tick + 1 < name.Length ? name[..tick] : name;
        }
        public string GetGenericTypeParameter(object? genericContext, int index) => genericContext is GenericScope s && index < s.Type.Count ? owner.md.GetString(owner.md.GetGenericParameter(s.Type[index]).Name) : "T" + index;
        public string GetGenericMethodParameter(object? genericContext, int index) => genericContext is GenericScope s && index < s.Method.Count ? owner.md.GetString(owner.md.GetGenericParameter(s.Method[index]).Name) : "M" + index;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "IntPtr";
    }

    public void Dispose() => pe.Dispose();
}
