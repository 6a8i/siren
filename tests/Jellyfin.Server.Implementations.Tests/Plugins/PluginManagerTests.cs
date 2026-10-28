using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using AutoFixture;
using Emby.Server.Implementations.Library;
using Emby.Server.Implementations.Plugins;
using Jellyfin.Extensions;
using Jellyfin.Extensions.Json;
using Jellyfin.Extensions.Json.Converters;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Plugins
{
    public class PluginManagerTests
    {
        private static readonly string _testPathRoot = Path.Combine(Path.GetTempPath(), "jellyfin-test-data");

        private string _tempPath = string.Empty;

        private string _pluginPath = string.Empty;

        private JsonSerializerOptions _options;

        public PluginManagerTests()
        {
            (_tempPath, _pluginPath) = GetTestPaths("plugin-" + Path.GetRandomFileName());

            Directory.CreateDirectory(_pluginPath);

            _options = GetTestSerializerOptions();
        }

        [Fact]
        public void SaveManifest_RoundTrip_Success()
        {
            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, null!, new Version(1, 0));
            var manifest = new PluginManifest()
            {
                Version = "1.0"
            };

            Assert.True(pluginManager.SaveManifest(manifest, _pluginPath));

            var res = pluginManager.LoadManifest(_pluginPath);

            Assert.Equal(manifest.Category, res.Manifest.Category);
            Assert.Equal(manifest.Changelog, res.Manifest.Changelog);
            Assert.Equal(manifest.Description, res.Manifest.Description);
            Assert.Equal(manifest.Id, res.Manifest.Id);
            Assert.Equal(manifest.Name, res.Manifest.Name);
            Assert.Equal(manifest.Overview, res.Manifest.Overview);
            Assert.Equal(manifest.Owner, res.Manifest.Owner);
            Assert.Equal(manifest.TargetAbi, res.Manifest.TargetAbi);
            Assert.Equal(manifest.Timestamp, res.Manifest.Timestamp);
            Assert.Equal(manifest.Version, res.Manifest.Version);
            Assert.Equal(manifest.Status, res.Manifest.Status);
            Assert.Equal(manifest.AutoUpdate, res.Manifest.AutoUpdate);
            Assert.Equal(manifest.ImagePath, res.Manifest.ImagePath);
            Assert.Equal(manifest.Assemblies, res.Manifest.Assemblies);
        }

        /// <summary>
        ///  Tests safe traversal within the plugin directory.
        /// </summary>
        /// <param name="dllFile">The safe path to evaluate.</param>
        [Theory]
        [InlineData("./some.dll")]
        [InlineData("some.dll")]
        [InlineData("sub/path/some.dll")]
        public void Constructor_DiscoversSafePluginAssembly_Status_Active(string dllFile)
        {
            var manifest = new PluginManifest
            {
                Id = Guid.NewGuid(),
                Name = "Safe Assembly",
                Assemblies = new string[] { dllFile }
            };

            var filename = Path.GetFileName(dllFile)!;
            var dllPath = Path.GetDirectoryName(Path.Combine(_pluginPath, dllFile))!;

            Directory.CreateDirectory(dllPath);
            FileHelper.CreateEmpty(Path.Combine(dllPath, filename));
            var metafilePath = Path.Combine(_pluginPath, "meta.json");

            File.WriteAllText(metafilePath, JsonSerializer.Serialize(manifest, _options));

            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, _tempPath, new Version(1, 0));

            var res = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(metafilePath), _options);

            var expectedFullPath = Path.Combine(_pluginPath, dllFile).Canonicalize();

            Assert.NotNull(res);
            Assert.NotEmpty(pluginManager.Plugins);
            Assert.Equal(PluginStatus.Active, res!.Status);
            Assert.Equal(expectedFullPath, pluginManager.Plugins[0].DllFiles[0]);
            Assert.StartsWith(_pluginPath, expectedFullPath, StringComparison.InvariantCulture);
        }

        /// <summary>
        ///  Tests unsafe attempts to traverse to higher directories.
        /// </summary>
        /// <remarks>
        ///  Attempts to load directories outside of the plugin should be
        ///  constrained. Path traversal, shell expansion, and double encoding
        ///  can be used to load unintended files.
        ///  See <see href="https://owasp.org/www-community/attacks/Path_Traversal"/> for more.
        /// </remarks>
        /// <param name="unsafePath">The unsafe path to evaluate.</param>
        [Theory]
        [InlineData("/some.dll")] // Root path.
        [InlineData("../some.dll")] // Simple traversal.
        [InlineData("C:\\some.dll")] // Windows root path.
        [InlineData("test.txt")] // Not a DLL
        [InlineData(".././.././../some.dll")] // Traversal with current and parent
        [InlineData(@"..\.\..\.\..\some.dll")] // Windows traversal with current and parent
        [InlineData(@"\\network\resource.dll")] // UNC Path
        [InlineData("https://jellyfin.org/some.dll")] // URL
        [InlineData("~/some.dll")] // Tilde poses a shell expansion risk, but is a valid path character.
        public void Constructor_DiscoversUnsafePluginAssembly_Status_Malfunctioned(string unsafePath)
        {
            var manifest = new PluginManifest
            {
                Id = Guid.NewGuid(),
                Name = "Unsafe Assembly",
                Assemblies = new string[] { unsafePath }
            };

            // Only create very specific files. Otherwise the test will be exploiting path traversal.
            var files = new string[]
            {
                "../other.dll",
                "some.dll"
            };

            foreach (var file in files)
            {
                FileHelper.CreateEmpty(Path.Combine(_pluginPath, file));
            }

            var metafilePath = Path.Combine(_pluginPath, "meta.json");

            File.WriteAllText(metafilePath, JsonSerializer.Serialize(manifest, _options));

            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, _tempPath, new Version(1, 0));

            var res = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(metafilePath), _options);

            Assert.NotNull(res);
            Assert.Empty(pluginManager.Plugins);
            Assert.Equal(PluginStatus.Malfunctioned, res!.Status);
        }

        [Fact]
        public async Task PopulateManifest_ExistingMetafilePlugin_PopulatesMissingFields()
        {
            var packageInfo = GenerateTestPackage();

            // Partial plugin without a name, but matching version and package ID
            var partial = new PluginManifest
            {
                Id = packageInfo.Id,
                AutoUpdate = false, // Turn off AutoUpdate
                Status = PluginStatus.Restart,
                Version = new Version(1, 0, 0).ToString(),
                Assemblies = new[] { "Jellyfin.Test.dll" }
            };

            var expectedManifest = new PluginManifest
            {
                Id = partial.Id,
                Name = packageInfo.Name,
                AutoUpdate = partial.AutoUpdate,
                Status = PluginStatus.Active,
                Owner = packageInfo.Owner,
                Assemblies = partial.Assemblies,
                Category = packageInfo.Category,
                Description = packageInfo.Description,
                Overview = packageInfo.Overview,
                TargetAbi = packageInfo.Versions[0].TargetAbi!,
                Timestamp = DateTimeOffset.Parse(packageInfo.Versions[0].Timestamp!, CultureInfo.InvariantCulture).UtcDateTime,
                Changelog = packageInfo.Versions[0].Changelog!,
                Version = new Version(1, 0).ToString(),
                ImagePath = string.Empty
            };

            var metafilePath = Path.Combine(_pluginPath, "meta.json");
            await File.WriteAllTextAsync(metafilePath, JsonSerializer.Serialize(partial, _options), TestContext.Current.CancellationToken);

            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, _tempPath, new Version(1, 0));

            await pluginManager.PopulateManifest(packageInfo, new Version(1, 0), _pluginPath, PluginStatus.Active);

            var resultBytes = await File.ReadAllBytesAsync(metafilePath, TestContext.Current.CancellationToken);
            var result = JsonSerializer.Deserialize<PluginManifest>(resultBytes, _options);

            Assert.NotNull(result);
            Assert.Equivalent(expectedManifest, result);
        }

        [Fact]
        public async Task PopulateManifest_NoMetafile_PreservesManifest()
        {
            var packageInfo = GenerateTestPackage();
            var expectedManifest = new PluginManifest
            {
                Id = packageInfo.Id,
                Name = packageInfo.Name,
                AutoUpdate = true,
                Status = PluginStatus.Active,
                Owner = packageInfo.Owner,
                Assemblies = Array.Empty<string>(),
                Category = packageInfo.Category,
                Description = packageInfo.Description,
                Overview = packageInfo.Overview,
                TargetAbi = packageInfo.Versions[0].TargetAbi!,
                Timestamp = DateTimeOffset.Parse(packageInfo.Versions[0].Timestamp!, CultureInfo.InvariantCulture).UtcDateTime,
                Changelog = packageInfo.Versions[0].Changelog!,
                Version = packageInfo.Versions[0].Version,
                ImagePath = string.Empty
            };

            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, null!, new Version(1, 0));

            await pluginManager.PopulateManifest(packageInfo, new Version(1, 0), _pluginPath, PluginStatus.Active);

            var metafilePath = Path.Combine(_pluginPath, "meta.json");
            var resultBytes = await File.ReadAllBytesAsync(metafilePath, TestContext.Current.CancellationToken);
            var result = JsonSerializer.Deserialize<PluginManifest>(resultBytes, _options);

            Assert.NotNull(result);
            Assert.Equivalent(expectedManifest, result);
        }

        [Fact]
        public async Task PopulateManifest_ExistingImage_IsNotDownloaded()
        {
            const string ImageContent = "not really a png";

            var packageInfo = GenerateTestPackage();
            packageInfo.ImageUrl = "https://example.org/some-plugin.png";

            var imagePath = Path.Combine(_pluginPath, "some-plugin.png");
            await File.WriteAllTextAsync(imagePath, ImageContent, TestContext.Current.CancellationToken);

            // The application host is null, so attempting to download the image would throw.
            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, null!, new Version(1, 0));

            Assert.True(await pluginManager.PopulateManifest(packageInfo, new Version(1, 0), _pluginPath, PluginStatus.Active));

            var result = pluginManager.LoadManifest(_pluginPath).Manifest;

            Assert.Equal(imagePath, result.ImagePath);
            Assert.Equal(ImageContent, await File.ReadAllTextAsync(imagePath, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task PopulateManifest_ExistingMetafileMismatchedIds_Status_Malfunctioned()
        {
            var packageInfo = GenerateTestPackage();

            // Partial plugin without a name, but matching version and package ID
            var partial = new PluginManifest
            {
                Id = Guid.NewGuid(),
                Version = new Version(1, 0, 0).ToString()
            };

            var metafilePath = Path.Combine(_pluginPath, "meta.json");
            await File.WriteAllTextAsync(metafilePath, JsonSerializer.Serialize(partial, _options), TestContext.Current.CancellationToken);

            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, _tempPath, new Version(1, 0));

            await pluginManager.PopulateManifest(packageInfo, new Version(1, 0), _pluginPath, PluginStatus.Active);

            var resultBytes = await File.ReadAllBytesAsync(metafilePath, TestContext.Current.CancellationToken);
            var result = JsonSerializer.Deserialize<PluginManifest>(resultBytes, _options);

            Assert.NotNull(result);
            Assert.Equal(packageInfo.Name, result.Name);
            Assert.Equal(PluginStatus.Malfunctioned, result.Status);
        }

        [Fact]
        public async Task PopulateManifest_ExistingMetafileMismatchedVersions_Updates_Version()
        {
            var packageInfo = GenerateTestPackage();

            var partial = new PluginManifest
            {
                Id = packageInfo.Id,
                Version = new Version(2, 0, 0).ToString()
            };

            var metafilePath = Path.Combine(_pluginPath, "meta.json");
            await File.WriteAllTextAsync(metafilePath, JsonSerializer.Serialize(partial, _options), TestContext.Current.CancellationToken);

            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, _tempPath, new Version(1, 0));

            await pluginManager.PopulateManifest(packageInfo, new Version(1, 0), _pluginPath, PluginStatus.Active);

            var resultBytes = await File.ReadAllBytesAsync(metafilePath, TestContext.Current.CancellationToken);
            var result = JsonSerializer.Deserialize<PluginManifest>(resultBytes, _options);

            Assert.NotNull(result);
            Assert.Equal(packageInfo.Name, result.Name);
            Assert.Equal(PluginStatus.Active, result.Status);
            Assert.Equal(packageInfo.Versions[0].Version, result.Version);
        }

        [Fact]
        public async Task DisablePlugin_CatalogRefresh_StaysDisabled()
        {
            var pluginRoot = Path.Combine(_tempPath, "plugins");
            var pluginDir = CreateTestPlugin(pluginRoot, "Disable Me", PluginStatus.Active);

            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, pluginRoot, new Version(1, 0));
            var plugin = Assert.Single(pluginManager.Plugins);

            pluginManager.DisablePlugin(plugin);

            Assert.Equal(PluginStatus.Disabled, pluginManager.LoadManifest(pluginDir).Manifest.Status);

            // The web shows that a restart is required, but the persisted state must not change.
            Assert.Equal(PluginStatus.Restart, plugin.GetPluginInfo().Status);
            Assert.Equal(PluginStatus.Disabled, plugin.Manifest.Status);
            Assert.True(plugin.Manifest.AutoUpdate);

            // Every catalog fetch rewrites the manifests of installed plugins from the in-memory status.
            var packageInfo = GenerateTestPackage(plugin.Id);
            await pluginManager.PopulateManifest(packageInfo, new Version(1, 0), pluginDir, plugin.Manifest.Status);

            Assert.Equal(PluginStatus.Disabled, pluginManager.LoadManifest(pluginDir).Manifest.Status);
        }

        [Fact]
        public void Constructor_DisabledPluginSortingBeforeEnabledPlugin_IsNotDeleted()
        {
            var pluginRoot = Path.Combine(_tempPath, "plugins");
            var disabledDir = CreateTestPlugin(pluginRoot, "AAA Disabled", PluginStatus.Disabled);
            CreateTestPlugin(pluginRoot, "ZZZ Active", PluginStatus.Active);

            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, pluginRoot, new Version(1, 0));

            Assert.True(Directory.Exists(disabledDir));
            Assert.Contains(pluginManager.Plugins, p => string.Equals(p.Name, "AAA Disabled", StringComparison.Ordinal));
        }

        [Fact]
        public void LoadAssemblies_DisabledPluginWithSupersededVersion_DoesNotRevertToOldVersion()
        {
            var pluginRoot = Path.Combine(_tempPath, "plugins");
            var id = Guid.NewGuid();
            var oldDir = CreateTestPlugin(pluginRoot, "Two Versions", PluginStatus.Superseded, new Version(1, 0), id);
            var newDir = CreateTestPlugin(pluginRoot, "Two Versions_2.0", PluginStatus.Disabled, new Version(2, 0), id, "Two Versions");

            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, pluginRoot, new Version(1, 0));

            Assert.Empty(pluginManager.LoadAssemblies());

            // Neither version may be touched: the old one stays superseded instead of being loaded
            // as a stand-in for the version the user disabled.
            Assert.Equal(PluginStatus.Superseded, pluginManager.LoadManifest(oldDir).Manifest.Status);
            Assert.Equal(PluginStatus.Disabled, pluginManager.LoadManifest(newDir).Manifest.Status);
        }

        [Fact]
        public void Inspect_AssemblyReferencingTheServer_IsAPluginAssembly()
        {
            var kind = PluginAssemblyInspector.Inspect(typeof(PluginManagerTests).Assembly.Location, out var name);

            Assert.Equal(PluginAssemblyInspector.Kind.Plugin, kind);
            Assert.Equal("Jellyfin.Server.Implementations.Tests", name);
        }

        [Fact]
        public void Inspect_AssemblyWithoutServerReference_IsADependency()
        {
            var kind = PluginAssemblyInspector.Inspect(typeof(Mock).Assembly.Location, out var name);

            Assert.Equal(PluginAssemblyInspector.Kind.Dependency, kind);
            Assert.Equal("Moq", name);
        }

        [Fact]
        public void Inspect_FileThatIsNoAssembly_IsNotManaged()
        {
            var path = Path.Combine(_pluginPath, "native.dll");
            File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 });

            Assert.Equal(PluginAssemblyInspector.Kind.NotManaged, PluginAssemblyInspector.Inspect(path, out var name));
            Assert.Null(name);

            FileHelper.CreateEmpty(path);
            Assert.Equal(PluginAssemblyInspector.Kind.NotManaged, PluginAssemblyInspector.Inspect(path, out _));
        }

        [Fact]
        public void LoadAssemblies_LoadsOnlyAssembliesReferencingTheServer_AndResolvesTheRestOnDemand()
        {
            var pluginRoot = Path.Combine(_tempPath, "plugins");
            var pluginDir = CreateTestPlugin(pluginRoot, "Fake", PluginStatus.Active);
            File.Delete(Path.Combine(pluginDir, "some.dll"));

            // Three files, as a real plugin folder has them: the plugin itself (it references the server),
            // a library it ships and a native library. Only the first is a plugin assembly.
            var helperPath = Path.Combine(pluginDir, "Fake.Helper.dll");
            var pluginPath = Path.Combine(pluginDir, "Jellyfin.Plugin.Fake.dll");
            EmitHelperAssembly(helperPath);
            EmitPluginAssembly(pluginPath, helperPath);
            FileHelper.CreateEmpty(Path.Combine(pluginDir, "native.dll"));

            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, pluginRoot, new Version(1, 0));
            var plugin = Assert.Single(pluginManager.Plugins);
            Assert.Equal(3, plugin.DllFiles.Count);

            var assembly = Assert.Single(pluginManager.LoadAssemblies());

            Assert.Equal("Jellyfin.Plugin.Fake", assembly.GetName().Name);
            Assert.Equal(PluginStatus.Active, plugin.Manifest.Status);

            // The types were walked once at load time and are handed out from there.
            var types = pluginManager.GetLoadedTypes(assembly);
            Assert.NotNull(types);
            var marker = Assert.Single(types, t => t.FullName == "Jellyfin.Plugin.Fake.Marker");
            Assert.Null(pluginManager.GetLoadedTypes(typeof(PluginManagerTests).Assembly));

            // The helper was not loaded up front...
            var context = AssemblyLoadContext.GetLoadContext(assembly);
            Assert.NotNull(context);
            Assert.DoesNotContain(context.Assemblies, a => string.Equals(a.GetName().Name, "Fake.Helper", StringComparison.Ordinal));

            // ...but the first reference to it resolves the plugin's own copy into the plugin's context.
            var helperType = marker.GetMethod("Use")!.GetParameters()[0].ParameterType;
            Assert.Equal("Fake.Helper.Util", helperType.FullName);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(helperType.Assembly));
            Assert.Equal(helperPath, helperType.Assembly.Location);
            Assert.Contains(context.Assemblies, a => string.Equals(a.GetName().Name, "Fake.Helper", StringComparison.Ordinal));
        }

        [Fact]
        public void LoadAssemblies_PluginWithoutAnyManagedAssembly_IsMalfunctioned()
        {
            var pluginRoot = Path.Combine(_tempPath, "plugins");
            var pluginDir = CreateTestPlugin(pluginRoot, "Broken", PluginStatus.Active);

            var pluginManager = new PluginManager(new NullLogger<PluginManager>(), null!, null!, pluginRoot, new Version(1, 0));

            // some.dll is an empty file: nothing to load, and the plugin is disabled as it was before.
            Assert.Empty(pluginManager.LoadAssemblies());
            Assert.Equal(PluginStatus.Malfunctioned, pluginManager.LoadManifest(pluginDir).Manifest.Status);
        }

        /// <summary>
        /// Writes an assembly with one public type and no reference to the server: what a library a
        /// plugin ships looks like.
        /// </summary>
        private static void EmitHelperAssembly(string path)
        {
            var builder = new PersistedAssemblyBuilder(new AssemblyName("Fake.Helper"), typeof(object).Assembly);
            var module = builder.DefineDynamicModule("Fake.Helper");
            module.DefineType("Fake.Helper.Util", TypeAttributes.Public | TypeAttributes.Class).CreateType();
            builder.Save(path);
        }

        /// <summary>
        /// Writes an assembly that references the server (a field typed with a server interface) and the
        /// helper (a method parameter, which nothing resolves until the method is looked at).
        /// </summary>
        private static void EmitPluginAssembly(string path, string helperPath)
        {
            var scratch = new AssemblyLoadContext(null, isCollectible: true);
            try
            {
                var helperType = scratch.LoadFromAssemblyPath(helperPath).GetType("Fake.Helper.Util", throwOnError: true)!;

                var builder = new PersistedAssemblyBuilder(new AssemblyName("Jellyfin.Plugin.Fake"), typeof(object).Assembly);
                var module = builder.DefineDynamicModule("Jellyfin.Plugin.Fake");
                var marker = module.DefineType("Jellyfin.Plugin.Fake.Marker", TypeAttributes.Public | TypeAttributes.Class);
                marker.DefineField("Plugin", typeof(IPlugin), FieldAttributes.Public);
                var use = marker.DefineMethod("Use", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [helperType]);
                use.GetILGenerator().Emit(OpCodes.Ret);
                marker.CreateType();
                builder.Save(path);
            }
            finally
            {
                scratch.Unload();
            }
        }

        private string CreateTestPlugin(string root, string folderName, PluginStatus status, Version? version = null, Guid? id = null, string? name = null)
        {
            var dir = Path.Combine(root, folderName);
            Directory.CreateDirectory(dir);
            FileHelper.CreateEmpty(Path.Combine(dir, "some.dll"));

            var manifest = new PluginManifest
            {
                Id = id ?? Guid.NewGuid(),
                Name = name ?? folderName,
                Status = status,
                AutoUpdate = true,
                TargetAbi = "1.0",
                Version = (version ?? new Version(1, 0)).ToString()
            };

            File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(manifest, _options));

            return dir;
        }

        private PackageInfo GenerateTestPackage(Guid? id = null)
        {
            var fixture = new Fixture();
            fixture.Customize<PackageInfo>(c => c.Without(x => x.Versions).Without(x => x.ImageUrl));
            fixture.Customize<VersionInfo>(c => c.Without(x => x.Version).Without(x => x.Timestamp));

            var versionInfo = fixture.Create<VersionInfo>();
            versionInfo.Version = new Version(1, 0).ToString();
            versionInfo.Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            var packageInfo = fixture.Create<PackageInfo>();
            packageInfo.Versions = new[] { versionInfo };
            if (id.HasValue)
            {
                packageInfo.Id = id.Value;
            }

            return packageInfo;
        }

        private JsonSerializerOptions GetTestSerializerOptions()
        {
            var options = new JsonSerializerOptions(JsonDefaults.Options)
            {
                WriteIndented = true
            };

            for (var i = 0; i < options.Converters.Count; i++)
            {
                // Remove the Guid converter for parity with plugin manager.
                if (options.Converters[i] is JsonGuidConverter converter)
                {
                    options.Converters.Remove(converter);
                }
            }

            return options;
        }

        private (string TempPath, string PluginPath) GetTestPaths(string pluginFolderName)
        {
            var tempPath = Path.Combine(_testPathRoot, "plugin-manager" + Path.GetRandomFileName());
            var pluginPath = Path.Combine(tempPath, pluginFolderName);

            return (tempPath, pluginPath);
        }
    }
}
