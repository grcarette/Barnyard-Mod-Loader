using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace UCHModLoader.Core.Services;

public sealed record PluginInfo(string Guid, string Name, string Version);

/// <summary>
/// Reads the [BepInPlugin(guid, name, version)] attribute from a DLL
/// without loading or executing it.
/// </summary>
public static class DllInspector
{
    public static PluginInfo? Inspect(Stream dllStream)
    {
        try
        {
            using var pe = new PEReader(dllStream, PEStreamOptions.PrefetchEntireImage);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();

            foreach (var typeHandle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(typeHandle);
                foreach (var caHandle in type.GetCustomAttributes())
                {
                    var ca = md.GetCustomAttribute(caHandle);
                    if (!IsBepInPlugin(md, ca)) continue;

                    var blob = md.GetBlobReader(ca.Value);
                    if (blob.ReadUInt16() != 0x0001) continue;
                    var guid = blob.ReadSerializedString() ?? "";
                    var name = blob.ReadSerializedString() ?? "";
                    var version = blob.ReadSerializedString() ?? "";
                    if (guid.Length > 0 && version.Length > 0)
                        return new PluginInfo(guid, name, version);
                }
            }
        }
        catch
        {
            // Not a valid .NET assembly — caller treats null as rejection.
        }
        return null;
    }

    private static bool IsBepInPlugin(MetadataReader md, CustomAttribute ca)
    {
        StringHandle nameHandle;
        if (ca.Constructor.Kind == HandleKind.MemberReference)
        {
            var memberRef = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
            if (memberRef.Parent.Kind != HandleKind.TypeReference) return false;
            nameHandle = md.GetTypeReference((TypeReferenceHandle)memberRef.Parent).Name;
        }
        else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
        {
            var methodDef = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor);
            nameHandle = md.GetTypeDefinition(methodDef.GetDeclaringType()).Name;
        }
        else return false;

        return md.GetString(nameHandle) is "BepInPlugin" or "BepInPluginAttribute";
    }
}