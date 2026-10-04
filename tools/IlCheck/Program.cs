// IlCheck: checks a built plugin DLL for two IL2CPP problems the compiler can't see.
//
//  1. Stripped game methods. The IL2CPP build only kept the methods the game uses. BepInEx's interop assemblies still
//     list every method and rebuild ("unstrip") the simple ones, but a method it couldn't rebuild gets a body that
//     throws "Method unstripping failed": the plugin compiles and then throws in game (GUI.DrawTexture,
//     GUI.Label(Rect, string, GUIStyle)). Every game / Unity method the plugin calls is looked up in the interop
//     assembly and its body checked for that.
//  2. Injected types that can't be registered. A local function in a MonoBehaviour that uses both locals and `this`
//     compiles to an instance method taking `ref <>c__DisplayClass`, and ClassInjector.RegisterTypeInIl2Cpp throws a
//     NullReferenceException in ConvertMethodInfo: the plugin fails to load.
//
// Usage: IlCheck --interop <GameDir\BepInEx\interop> <plugin.dll>...   (run by tools/il2cpp-check.ps1)
// Prints FAIL / WARN / info lines and a final RESULT line; exit code 1 when anything FAILs.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

static class Program
{
    static int fails, warns;
    static void Fail(string m) { fails++; Console.WriteLine("  FAIL  " + m); }
    static void Warn(string m) { warns++; Console.WriteLine("  WARN  " + m); }
    static void Info(string m) => Console.WriteLine("  info  " + m);

    static int Main(string[] args)
    {
        string interop = null;
        var dlls = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--interop" && i + 1 < args.Length) interop = args[++i];
            else dlls.Add(args[i]);
        }
        if (interop == null || !Directory.Exists(interop) || dlls.Count == 0)
        {
            Console.WriteLine(@"usage: IlCheck --interop <GameDir\BepInEx\interop> <plugin.dll>...");
            return 2;
        }
        foreach (var dll in dlls)
        {
            if (!File.Exists(dll)) { Fail($"{dll}: not found (build it first)"); continue; }
            Console.WriteLine(Path.GetFileName(dll));
            using var fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            var r = pe.GetMetadataReader();
            CheckInjected(r);
            CheckStripped(r, interop);
        }
        Console.WriteLine(fails == 0 ? $"RESULT: OK ({warns} warning(s))" : $"RESULT: {fails} problem(s)");
        return fails == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ type names
    static string Simple(string name)
    {
        int tick = name.IndexOf('`');
        return tick >= 0 ? name.Substring(0, tick) : name;
    }

    /// <summary>Top-level type reference of a (possibly nested) type reference, and the nested name path.</summary>
    static TypeReferenceHandle Outer(MetadataReader r, TypeReferenceHandle h, out string path)
    {
        var t = r.GetTypeReference(h);
        path = Simple(r.GetString(t.Name));
        while (t.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            h = (TypeReferenceHandle)t.ResolutionScope;
            t = r.GetTypeReference(h);
            path = Simple(r.GetString(t.Name)) + "." + path;
        }
        return h;
    }

    static string AssemblyOf(MetadataReader r, TypeReferenceHandle h)
    {
        var top = r.GetTypeReference(Outer(r, h, out _));
        if (top.ResolutionScope.Kind != HandleKind.AssemblyReference) return null;
        return r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)top.ResolutionScope).Name);
    }

    sealed class Names : ISignatureTypeProvider<string, object>
    {
        public string GetPrimitiveType(PrimitiveTypeCode c) => c switch
        {
            PrimitiveTypeCode.Boolean => "bool", PrimitiveTypeCode.Byte => "byte", PrimitiveTypeCode.SByte => "sbyte",
            PrimitiveTypeCode.Char => "char", PrimitiveTypeCode.Int16 => "short", PrimitiveTypeCode.UInt16 => "ushort",
            PrimitiveTypeCode.Int32 => "int", PrimitiveTypeCode.UInt32 => "uint", PrimitiveTypeCode.Int64 => "long",
            PrimitiveTypeCode.UInt64 => "ulong", PrimitiveTypeCode.Single => "float", PrimitiveTypeCode.Double => "double",
            PrimitiveTypeCode.String => "string", PrimitiveTypeCode.Object => "object", PrimitiveTypeCode.Void => "void",
            PrimitiveTypeCode.IntPtr => "IntPtr", PrimitiveTypeCode.UIntPtr => "UIntPtr", _ => c.ToString(),
        };
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k)
        {
            var t = r.GetTypeDefinition(h);
            return Map(r.GetString(t.Namespace), Simple(r.GetString(t.Name)));
        }
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k)
        {
            var t = r.GetTypeReference(h);
            return Map(r.GetString(t.Namespace), Simple(r.GetString(t.Name)));
        }
        /// <summary>The same name whichever side (plugin or interop assembly) the type is read from.</summary>
        static string Map(string ns, string name)
        {
            if (ns == "Il2CppSystem" && name == "Object") return "object";
            if (ns == "Il2CppSystem" && name == "String") return "string";
            if (ns == "Il2CppInterop.Runtime.InteropTypes.Arrays" && name == "Il2CppStringArray") return "string[]";
            return name;
        }
        public string GetTypeFromSpecification(MetadataReader r, object ctx, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, ctx);
        public string GetSZArrayType(string e) => e + "[]";
        public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', Math.Max(0, s.Rank - 1)) + "]";
        public string GetByReferenceType(string e) => "ref " + e;
        public string GetPointerType(string e) => e + "*";
        public string GetPinnedType(string e) => e;
        public string GetModifiedType(string m, string u, bool req) => u;
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetGenericMethodParameter(object ctx, int i) => "?";
        public string GetGenericTypeParameter(object ctx, int i) => "?";
        public string GetGenericInstantiation(string g, ImmutableArray<string> a)
        {
            if ((g == "Il2CppStructArray" || g == "Il2CppReferenceArray") && a.Length == 1) return a[0] + "[]";
            return g;   // both sides are read the same way, so the outer generic type is enough
        }
    }
    static readonly Names names = new Names();

    // ------------------------------------------------------------------ 2. injected types
    static void CheckInjected(MetadataReader r)
    {
        int injected = 0;
        foreach (var th in r.TypeDefinitions)
        {
            var td = r.GetTypeDefinition(th);
            if (!DerivesFromGame(r, td, 0)) continue;
            injected++;
            string typeName = r.GetString(td.Name);
            foreach (var mh in td.GetMethods())
            {
                var md = r.GetMethodDefinition(mh);
                if ((md.Attributes & System.Reflection.MethodAttributes.Static) != 0) continue;
                if (HasAttribute(r, md.GetCustomAttributes(), "HideFromIl2CppAttribute")) continue;
                var sig = md.DecodeSignature(names, null);
                var bad = sig.ParameterTypes.FirstOrDefault(p => p.StartsWith("ref <", StringComparison.Ordinal));
                if (bad == null) continue;
                string name = r.GetString(md.Name);
                var lf = Regex.Match(name, @"^<(\w+)>g__(\w+)\|");
                string what = lf.Success ? $"local function '{lf.Groups[2].Value}' inside {typeName}.{lf.Groups[1].Value}()" : $"{typeName}.{name}";
                Fail($"{what} uses both locals and `this` (compiles to an instance method taking '{bad}'): RegisterTypeInIl2Cpp throws and the plugin won't load. Make it static, move it to a helper class or mark it [HideFromIl2Cpp].");
            }
        }
        Info($"{injected} type(s) derived from game / Unity classes checked for injection problems");
    }

    static bool DerivesFromGame(MetadataReader r, TypeDefinition td, int depth)
    {
        if (depth > 20 || td.BaseType.IsNil) return false;
        switch (td.BaseType.Kind)
        {
            case HandleKind.TypeDefinition: return DerivesFromGame(r, r.GetTypeDefinition((TypeDefinitionHandle)td.BaseType), depth + 1);
            case HandleKind.TypeReference:
                var asm = AssemblyOf(r, (TypeReferenceHandle)td.BaseType);
                if (asm == null) return false;
                // BasePlugin (BepInEx) and plain managed bases aren't injected; interop classes are
                return asm.StartsWith("UnityEngine", StringComparison.Ordinal) || asm.StartsWith("Unity.", StringComparison.Ordinal)
                    || asm == "Assembly-CSharp" || asm.StartsWith("Il2Cpp", StringComparison.Ordinal) && !asm.StartsWith("Il2CppInterop", StringComparison.Ordinal);
            default: return false;
        }
    }

    static bool HasAttribute(MetadataReader r, CustomAttributeHandleCollection attrs, string name)
    {
        foreach (var ah in attrs)
        {
            var ctor = r.GetCustomAttribute(ah).Constructor;
            if (ctor.Kind != HandleKind.MemberReference) continue;
            var parent = r.GetMemberReference((MemberReferenceHandle)ctor).Parent;
            if (parent.Kind == HandleKind.TypeReference && r.GetString(r.GetTypeReference((TypeReferenceHandle)parent).Name) == name) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ 1. stripped methods
    /// <summary>An interop assembly, opened once: its types by namespace + nested path.</summary>
    sealed class Interop
    {
        public MetadataReader R;
        public PEReader Pe;
        public string Dir;
        public Dictionary<string, List<TypeDefinitionHandle>> Types = new Dictionary<string, List<TypeDefinitionHandle>>();   // Nullable and Nullable`1 share a key
    }
    static readonly Dictionary<string, Interop> opened = new Dictionary<string, Interop>(StringComparer.OrdinalIgnoreCase);

    static Interop Open(string dir, string asm)
    {
        if (opened.TryGetValue(asm, out var io)) return io;
        var path = Path.Combine(dir, asm + ".dll");
        if (!File.Exists(path)) return opened[asm] = null;
        var pe = new PEReader(File.OpenRead(path), PEStreamOptions.PrefetchEntireImage);
        io = new Interop { Pe = pe, R = pe.GetMetadataReader(), Dir = dir };
        foreach (var th in io.R.TypeDefinitions)
        {
            var k = Key(io.R, th);
            if (!io.Types.TryGetValue(k, out var list)) io.Types[k] = list = new List<TypeDefinitionHandle>();
            list.Add(th);
        }
        return opened[asm] = io;
    }

    static string Key(MetadataReader r, TypeDefinitionHandle th)
    {
        var td = r.GetTypeDefinition(th);
        string path = Simple(r.GetString(td.Name));
        for (int depth = 0; td.IsNested && depth < 20; depth++)
        {
            var outer = td.GetDeclaringType();
            if (outer.IsNil) break;   // nested flag without a NestedClass row (seen in generated interop)
            td = r.GetTypeDefinition(outer);
            path = Simple(r.GetString(td.Name)) + "." + path;
        }
        return r.GetString(td.Namespace) + "|" + path;
    }

    static void CheckStripped(MetadataReader r, string interopDir)
    {
        int checkedCount = 0;
        var notFound = new SortedSet<string>();
        var seen = new HashSet<string>();
        foreach (var mh in r.MemberReferences)
        {
            if (r.GetMemberReference(mh).GetKind() != MemberReferenceKind.Method) continue;
            int res = Resolve(r, null, mh, interopDir, out var io, out var def, out var display);
            if (res < 0) continue;   // not a game / Unity method (BepInEx, System, Il2CppInterop)
            if (!seen.Add(display)) continue;
            checkedCount++;
            if (res == 0) { notFound.Add(display); continue; }
            var chain = Broken(io, def, 0);
            if (chain == null) continue;
            Fail(chain.Count == 0
                ? $"{display}: stripped from the game build and BepInEx couldn't rebuild it; it compiles but throws 'Method unstripping failed' in game. Use another overload or API."
                : $"{display}: BepInEx rebuilt it as a call to {string.Join(" -> ", chain)}, which was stripped and couldn't be rebuilt; it throws 'Method unstripping failed' in game. Use another overload or API.");
        }
        Info($"{checkedCount} game / Unity method(s) checked against the interop");
        if (notFound.Count > 0) Warn($"not found in the interop (not checked): {string.Join("; ", notFound.Take(8))}{(notFound.Count > 8 ? $" ... (+{notFound.Count - 8})" : "")}");
    }

    /// <summary>
    /// A member reference to its method in the interop: -1 = not a game / Unity method, 0 = the type or method wasn't
    /// found, 1 = found (io, def set). `self` = the interop assembly `r` belongs to (null for the plugin): its
    /// unstripped IL refers to its own types too.
    /// </summary>
    static int Resolve(MetadataReader r, Interop self, MemberReferenceHandle mh, string interopDir, out Interop io, out MethodDefinitionHandle def, out string display)
    {
        io = null; def = default; display = null;
        var mr = r.GetMemberReference(mh);
        string key;
        if (mr.Parent.Kind == HandleKind.TypeDefinition)
        {
            if (self == null) return -1;   // the plugin's own type
            io = self;
            key = Key(r, (TypeDefinitionHandle)mr.Parent);
        }
        else
        {
            if (!ParentType(r, mr.Parent, out var typeRef)) return -1;
            var top = r.GetTypeReference(Outer(r, typeRef, out string path));
            key = r.GetString(top.Namespace) + "|" + path;
            if (top.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                string asm = r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)top.ResolutionScope).Name);
                if (asm.StartsWith("Il2CppInterop", StringComparison.Ordinal)) return -1;
                io = Open(interopDir, asm);
            }
            else io = self;   // module-scoped: a type of the same assembly
            if (io == null) return -1;   // not a game assembly (BepInEx, System, ...)
        }

        string name = r.GetString(mr.Name);
        var ps = mr.DecodeMethodSignature(names, null).ParameterTypes;
        display = $"{key.Replace('|', '.').TrimStart('.')}.{name}({string.Join(", ", ps)})";
        if (!io.Types.TryGetValue(key, out var types)) return 0;
        var ior = io.R;
        foreach (var dh in types.SelectMany(t => ior.GetTypeDefinition(t).GetMethods()))
        {
            var md = io.R.GetMethodDefinition(dh);
            if (io.R.GetString(md.Name) != name) continue;
            var dps = md.DecodeSignature(names, null).ParameterTypes;
            if (dps.Length == ps.Length && dps.SequenceEqual(ps)) { def = dh; return 1; }
        }
        return 0;
    }

    static string Describe(Interop io, MethodDefinitionHandle h)
    {
        var md = io.R.GetMethodDefinition(h);
        var ps = md.DecodeSignature(names, null).ParameterTypes;
        return $"{io.R.GetString(io.R.GetTypeDefinition(md.GetDeclaringType()).Name)}.{io.R.GetString(md.Name)}({string.Join(", ", ps)})";
    }

    /// <summary>
    /// Il2CppInterop gives a method it could not unstrip a body that throws "Method unstripping failed", and a method
    /// it did unstrip is plain IL that may call one that failed (GUI.DrawTexture(Rect, Texture) -> the full overload).
    /// Returns null when the method is fine, else the chain of calls down to the failing one (empty = this one).
    /// </summary>
    static readonly Dictionary<string, List<string>> verdicts = new Dictionary<string, List<string>>();   // null = fine (or being checked)

    static List<string> Broken(Interop io, MethodDefinitionHandle h, int depth)
    {
        string key = io.Dir + "|" + io.R.GetString(io.R.GetAssemblyDefinition().Name) + "|" + MetadataTokens.GetToken(h);
        if (verdicts.TryGetValue(key, out var known)) return known == null ? null : new List<string>(known);
        verdicts[key] = null;   // a call cycle counts as fine
        var result = BrokenBody(io, h, depth);
        verdicts[key] = result;
        return result == null ? null : new List<string>(result);
    }

    static List<string> BrokenBody(Interop io, MethodDefinitionHandle h, int depth)
    {
        var md = io.R.GetMethodDefinition(h);
        if (md.RelativeVirtualAddress == 0 || depth > 40) return null;
        var il = io.Pe.GetMethodBody(md.RelativeVirtualAddress).GetILBytes();
        var calls = new List<EntityHandle>();
        foreach (var (op, token) in Tokens(il))
        {
            if (op == 0x72)
            {
                try { if (io.R.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF)).IndexOf("unstripping", StringComparison.OrdinalIgnoreCase) >= 0) return new List<string>(); }
                catch (BadImageFormatException) { }
            }
            else if (op == 0x28 || op == 0x6F || op == 0x73)
            {
                try { calls.Add(MetadataTokens.EntityHandle(token)); } catch (ArgumentException) { }
            }
        }
        foreach (var c in calls)
        {
            var target = c;
            if (target.Kind == HandleKind.MethodSpecification) target = io.R.GetMethodSpecification((MethodSpecificationHandle)target).Method;
            List<string> sub = null; string step = null;
            if (target.Kind == HandleKind.MethodDefinition)
            {
                var dh = (MethodDefinitionHandle)target;
                sub = Broken(io, dh, depth + 1);
                if (sub != null) step = Describe(io, dh);
            }
            else if (target.Kind == HandleKind.MemberReference)
            {
                if (io.R.GetMemberReference((MemberReferenceHandle)target).GetKind() != MemberReferenceKind.Method) continue;
                if (Resolve(io.R, io, (MemberReferenceHandle)target, io.Dir, out var io2, out var dh2, out var disp) != 1) continue;
                sub = Broken(io2, dh2, depth + 1);
                if (sub != null) step = disp;
            }
            if (sub != null) { sub.Insert(0, step); return sub; }
        }
        return null;
    }

    /// <summary>The token operands of an IL body, walked opcode by opcode: (opcode, token) for every 4-byte token operand.</summary>
    static IEnumerable<(int op, int token)> Tokens(byte[] il)
    {
        int i = 0;
        while (i < il.Length)
        {
            int op = il[i++];
            if (op == 0xFE)
            {
                if (i >= il.Length) yield break;
                int op2 = il[i++];
                switch (op2)
                {
                    case 0x06: case 0x07: case 0x15: case 0x16: case 0x1C:   // ldftn ldvirtftn initobj constrained sizeof
                        if (i + 4 <= il.Length) yield return (0xFE00 | op2, BitConverter.ToInt32(il, i));
                        i += 4; break;
                    case 0x09: case 0x0A: case 0x0B: case 0x0C: case 0x0D: case 0x0E: i += 2; break;   // long-form arg / local
                    case 0x12: case 0x19: i += 1; break;                                               // unaligned. no.
                }
                continue;
            }
            switch (op)
            {
                case 0x0E: case 0x0F: case 0x10: case 0x11: case 0x12: case 0x13: case 0x1F: case 0xDE:   // short arg/local, ldc.i4.s, leave.s
                    i += 1; break;
                case 0x20: case 0x22: case 0xDD: i += 4; break;   // ldc.i4, ldc.r4, leave
                case 0x21: case 0x23: i += 8; break;               // ldc.i8, ldc.r8
                case 0x45:                                          // switch
                    if (i + 4 > il.Length) yield break;
                    int n = BitConverter.ToInt32(il, i); i += 4 + 4 * n; break;
                default:
                    if (op >= 0x2B && op <= 0x37) { i += 1; break; }   // short branches
                    if (op >= 0x38 && op <= 0x44) { i += 4; break; }   // long branches
                    bool token = op == 0x27 || op == 0x28 || op == 0x29 || op == 0x6F || op == 0x70 || op == 0x71 || op == 0x72
                        || op == 0x73 || op == 0x74 || op == 0x75 || op == 0x79 || (op >= 0x7B && op <= 0x81) || op == 0x8C
                        || op == 0x8D || op == 0x8F || op == 0xA3 || op == 0xA4 || op == 0xA5 || op == 0xC2 || op == 0xC6 || op == 0xD0;
                    if (token)
                    {
                        if (i + 4 <= il.Length) yield return (op, BitConverter.ToInt32(il, i));
                        i += 4;
                    }
                    break;
            }
        }
    }

    static bool ParentType(MetadataReader r, EntityHandle parent, out TypeReferenceHandle typeRef)
    {
        typeRef = default;
        if (parent.Kind == HandleKind.TypeReference) { typeRef = (TypeReferenceHandle)parent; return true; }
        if (parent.Kind != HandleKind.TypeSpecification) return false;
        // generic instantiation (List<int>.Add): the generic type itself
        var blob = r.GetBlobReader(r.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return false;
        blob.ReadSignatureTypeCode();   // class / valuetype
        var h = blob.ReadTypeHandle();
        if (h.Kind != HandleKind.TypeReference) return false;
        typeRef = (TypeReferenceHandle)h;
        return true;
    }

}
