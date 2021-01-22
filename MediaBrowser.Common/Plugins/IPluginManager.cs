#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBrowser.Common.Plugins
{
    /// <summary>
    /// Defines the <see cref="IPluginManager" />.
    /// </summary>
    public interface IPluginManager
    {
        /// <summary>
        /// Gets the Plugins.
        /// </summary>
        IList<LocalPlugin> Plugins { get; }

        /// <summary>
        /// Creates the plugins.
        /// </summary>
        void CreatePlugins();

        /// <summary>
        /// Returns all the assemblies.
        /// </summary>
        /// <returns>An IEnumerable{Assembly}.</returns>
        IEnumerable<Assembly> LoadAssemblies();

        /// <summary>
        /// Registers the plugin's services with the DI.
        /// Note: DI is not yet instantiated yet.
        /// </summary>
        /// <param name="serviceCollection">A <see cref="ServiceCollection"/> instance.</param>
        void RegisterServices(IServiceCollection serviceCollection);

        /// <summary>
        /// Saves the manifest back to disk.
        /// </summary>
        /// <param name="manifest">The <see cref="PluginManifest"/> to save.</param>
        /// <param name="path">The path where to save the manifest.</param>
        /// <returns>True if successful.</returns>
        bool SaveManifest(PluginManifest manifest, string path);

        /// <summary>
        /// Imports plugin details from a folder.
        /// </summary>
        /// <param name="folder">Folder of the plugin.</param>
        void ImportPluginFrom(string folder);

        /// <summary>
        /// Disable the plugin.
        /// </summary>
        /// <param name="assembly">The <see cref="Assembly"/> of the plug to disable.</param>
        void FailPlugin(Assembly assembly);

        /// <summary>
        /// Disable the plugin.
        /// </summary>
        /// <param name="plugin">The <see cref="LocalPlugin"/> of the plug to disable.</param>
        void DisablePlugin(LocalPlugin plugin);

        /// <summary>
        /// Enables the plugin, disabling all other versions.
        /// </summary>
        /// <param name="plugin">The <see cref="LocalPlugin"/> of the plug to disable.</param>
        void EnablePlugin(LocalPlugin plugin);

        /// <summary>
        /// Attempts to find the plugin with and id of <paramref name="id"/>.
        /// </summary>
        /// <param name="id">Id of plugin.</param>
        /// <param name="version">The version of the plugin to locate.</param>
        /// <returns>A <see cref="LocalPlugin"/> if located, or null if not.</returns>
        LocalPlugin? GetPlugin(Guid id, Version? version = null);

        /// <summary>
        /// Removes the plugin.
        /// </summary>
        /// <param name="plugin">The plugin.</param>
        /// <returns>Outcome of the operation.</returns>
        bool RemovePlugin(LocalPlugin plugin);
    }
}
