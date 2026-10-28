using System.Collections.Frozen;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;

namespace Emby.Server.Implementations.Plugins;

/// <summary>
/// A custom <see cref="AssemblyLoadContext"/> for loading Jellyfin plugins.
/// </summary>
public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly IReadOnlyDictionary<string, string> _assemblyPaths;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLoadContext"/> class.
    /// </summary>
    /// <param name="path">The path of the plugin assembly.</param>
    public PluginLoadContext(string path)
        : this(path, FrozenDictionary<string, string>.Empty)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLoadContext"/> class.
    /// </summary>
    /// <param name="path">The path of the plugin assembly.</param>
    /// <param name="assemblyPaths">
    /// The DLLs the plugin ships, by assembly name: each is loaded into this context the first time
    /// something in it refers to that name, instead of all of them up front.
    /// </param>
    public PluginLoadContext(string path, IReadOnlyDictionary<string, string> assemblyPaths)
        : base(true)
    {
        _resolver = new AssemblyDependencyResolver(path);
        _assemblyPaths = assemblyPaths;
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is not null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        // The plugin's own copy wins over the server's, as it did when every DLL in the folder was
        // loaded ahead of time; an assembly the plugin does not ship falls through to the server's.
        if (assemblyName.Name is not null && _assemblyPaths.TryGetValue(assemblyName.Name, out assemblyPath))
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null;
    }
}
