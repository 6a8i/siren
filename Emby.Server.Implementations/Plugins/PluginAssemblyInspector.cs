using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Emby.Server.Implementations.Plugins;

/// <summary>
/// Tells what a DLL in a plugin folder is by reading its metadata, without loading it: the plugin's
/// own assembly (it references the server), a library it ships for itself, or not a .NET assembly at
/// all (a native library, or a broken file).
/// </summary>
internal static class PluginAssemblyInspector
{
    /// <summary>
    /// What a file in a plugin folder turned out to be.
    /// </summary>
    internal enum Kind
    {
        /// <summary>
        /// Not a managed assembly: a native library, a module, or a file that cannot be read.
        /// </summary>
        NotManaged,

        /// <summary>
        /// A managed assembly that does not reference the server: a dependency the plugin ships.
        /// </summary>
        Dependency,

        /// <summary>
        /// A managed assembly that references the server, so it can carry plugin parts.
        /// </summary>
        Plugin
    }

    /// <summary>
    /// Inspects a file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="assemblyName">The simple name of the assembly, when it is one.</param>
    /// <returns>What the file is.</returns>
    public static Kind Inspect(string path, out string? assemblyName)
    {
        assemblyName = null;
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return Kind.NotManaged;
            }

            var metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                return Kind.NotManaged;
            }

            assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            foreach (var handle in metadata.AssemblyReferences)
            {
                if (IsServerAssembly(metadata.GetString(metadata.GetAssemblyReference(handle).Name)))
                {
                    return Kind.Plugin;
                }
            }

            return Kind.Dependency;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return Kind.NotManaged;
        }
    }

    /// <summary>
    /// Whether an assembly name belongs to the server: the assemblies that define the interfaces a
    /// plugin part implements, so a plugin assembly must reference at least one of them.
    /// </summary>
    private static bool IsServerAssembly(string name)
        => name.StartsWith("MediaBrowser.", StringComparison.Ordinal)
            || name.StartsWith("Jellyfin.", StringComparison.Ordinal)
            || name.StartsWith("Emby.", StringComparison.Ordinal);
}
