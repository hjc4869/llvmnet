// jit-all needs the JIT and loads arbitrary assemblies, so it is compiled only into the JIT-hosted
// Checks build; the NativeAOT build (LLVMNET_AOT_PROBE) exists solely for the SIMD128 probe.
#if !LLVMNET_AOT_PROBE
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

/// <summary>
/// Forces the JIT to compile every method body of llvmnet-emitted assemblies, so structurally invalid IL surfaces as
/// InvalidProgramException even in methods no test executes. Translated code is unverifiable by design (raw pointers,
/// localloc, calli), which leaves the JIT as the structural check.
/// </summary>
internal static class JitValidation
{
    internal static int Run(IEnumerable<string> roots)
    {
        int assemblies = 0, methods = 0, failures = 0;
        foreach (string path in roots.SelectMany(Expand).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!EmittedByLlvmnet(path)) continue;
            assemblies++;
            string directory = Path.GetDirectoryName(path)!;
            var context = new AssemblyLoadContext($"jit-all:{path}", isCollectible: true);
            context.Resolving += (loader, name) =>
            {
                string candidate = Path.Combine(directory, name.Name + ".dll");
                return File.Exists(candidate) ? loader.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(path);
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException error) { types = error.Types.OfType<Type>().ToArray(); failures++; Console.Error.WriteLine($"{path}: {error.LoaderExceptions.FirstOrDefault()?.Message}"); }
                foreach (Type type in types)
                    foreach (MethodBase method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)))
                    {
                        if (method.IsAbstract || method.ContainsGenericParameters || method.Attributes.HasFlag(MethodAttributes.PinvokeImpl) ||
                            method.MethodImplementationFlags.HasFlag(MethodImplAttributes.InternalCall)) continue;
                        try
                        {
                            RuntimeHelpers.PrepareMethod(method.MethodHandle);
                            methods++;
                        }
                        catch (Exception error)
                        {
                            failures++;
                            Console.Error.WriteLine($"{path}: {type.FullName}.{method.Name}: {error.GetType().Name}: {error.Message}");
                        }
                    }
            }
            finally { context.Unload(); }
        }
        Console.WriteLine($"JIT-compiled {methods} methods in {assemblies} llvmnet assemblies; {failures} failures.");
        return failures == 0 && assemblies > 0 ? 0 : 1;
    }

    private static IEnumerable<string> Expand(string root) =>
        Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories) : [Path.GetFullPath(root)];

    private static bool EmittedByLlvmnet(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var image = new PEReader(stream);
            if (!image.HasMetadata) return false;
            MetadataReader metadata = image.GetMetadataReader();
            foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                CustomAttribute attribute = metadata.GetCustomAttribute(handle);
                BlobReader value = metadata.GetBlobReader(attribute.Value);
                if (value.Length > 2 && value.ReadUInt16() == 1 && value.ReadSerializedString() == "llvmnet.abi") return true;
            }
        }
        catch (BadImageFormatException) { }
        return false;
    }
}
#endif
