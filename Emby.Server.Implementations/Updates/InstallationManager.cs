#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Json;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Updates;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Updates
{
    /// <summary>
    /// Manages all install, uninstall, and update operations for the system and individual plugins.
    /// </summary>
    public class InstallationManager : IInstallationManager
    {
        /// <summary>
        /// The logger.
        /// </summary>
        private readonly ILogger<InstallationManager> _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly IEventManager _eventManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerConfigurationManager _config;
        private readonly JsonSerializerOptions _jsonSerializerOptions;
        private readonly IPluginManager _pluginManager;

        /// <summary>
        /// Gets the application host.
        /// </summary>
        /// <value>The application host.</value>
        private readonly IServerApplicationHost _applicationHost;
        private readonly IZipClient _zipClient;
        private readonly object _currentInstallationsLock = new object();

        /// <summary>
        /// The current installations.
        /// </summary>
        private readonly List<(InstallationInfo info, CancellationTokenSource token)> _currentInstallations;

        /// <summary>
        /// The completed installations.
        /// </summary>
        private readonly ConcurrentBag<InstallationInfo> _completedInstallationsInternal;

        /// <summary>
        /// Initializes a new instance of the <see cref="InstallationManager"/> class.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger{InstallationManager}"/>.</param>
        /// <param name="appHost">The <see cref="IServerApplicationHost"/>.</param>
        /// <param name="appPaths">The <see cref="IApplicationPaths"/>.</param>
        /// <param name="eventManager">The <see cref="IEventManager"/>.</param>
        /// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/>.</param>
        /// <param name="config">The <see cref="IServerConfigurationManager"/>.</param>
        /// <param name="zipClient">The <see cref="IZipClient"/>.</param>
        /// <param name="pluginManager">The <see cref="IPluginManager"/>.</param>
        public InstallationManager(
            ILogger<InstallationManager> logger,
            IServerApplicationHost appHost,
            IApplicationPaths appPaths,
            IEventManager eventManager,
            IHttpClientFactory httpClientFactory,
            IServerConfigurationManager config,
            IZipClient zipClient,
            IPluginManager pluginManager)
        {
            _currentInstallations = new List<(InstallationInfo, CancellationTokenSource)>();
            _completedInstallationsInternal = new ConcurrentBag<InstallationInfo>();

            _logger = logger;
            _applicationHost = appHost;
            _appPaths = appPaths;
            _eventManager = eventManager;
            _httpClientFactory = httpClientFactory;
            _config = config;
            _zipClient = zipClient;
            _jsonSerializerOptions = JsonDefaults.GetOptions();
            _pluginManager = pluginManager;
        }

        /// <inheritdoc />
        public IEnumerable<InstallationInfo> CompletedInstallations => _completedInstallationsInternal;

        /// <inheritdoc />
        public async Task<IList<PackageInfo>> GetPackages(string manifestName, string manifest, bool filterIncompatible, CancellationToken cancellationToken = default)
        {
            try
            {
                List<PackageInfo>? packages = await _httpClientFactory.CreateClient(NamedClient.Default)
                        .GetFromJsonAsync<List<PackageInfo>>(new Uri(manifest), _jsonSerializerOptions, cancellationToken).ConfigureAwait(false);

                if (packages == null)
                {
                    return Array.Empty<PackageInfo>();
                }

                var minimumVersion = new Version(0, 0, 0, 1);
                // Store the repository and repository url with each version, as they may be spread apart.
                foreach (var entry in packages)
                {
                    for (int a = entry.Versions.Count - 1; a >= 0; a--)
                    {
                        var ver = entry.Versions[a];
                        ver.RepositoryName = manifestName;
                        ver.RepositoryUrl = manifest;

                        if (!filterIncompatible)
                        {
                            continue;
                        }

                        if (!Version.TryParse(ver.TargetAbi, out var targetAbi))
                        {
                            targetAbi = minimumVersion;
                        }

                        // Only show plugins that are greater than or equal to targetAbi.
                        if (_applicationHost.ApplicationVersion >= targetAbi)
                        {
                            continue;
                        }

                        // Not compatible with this version so remove it.
                        entry.Versions.Remove(ver);
                    }
                }

                return packages;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Cannot locate the plugin manifest {Manifest}", manifest);
                return Array.Empty<PackageInfo>();
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to deserialize the plugin manifest retrieved from {Manifest}", manifest);
                return Array.Empty<PackageInfo>();
            }
            catch (UriFormatException ex)
            {
                _logger.LogError(ex, "The URL configured for the plugin repository manifest URL is not valid: {Manifest}", manifest);
                return Array.Empty<PackageInfo>();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "An error occurred while accessing the plugin manifest: {Manifest}", manifest);
                return Array.Empty<PackageInfo>();
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<PackageInfo>> GetAvailablePackages(CancellationToken cancellationToken = default)
        {
            var result = new List<PackageInfo>();
            foreach (RepositoryInfo repository in _config.Configuration.PluginRepositories)
            {
                if (repository.Enabled && repository.Url != null)
                {
                    // Where repositories have the same content, the details from the first is taken.
                    foreach (var package in await GetPackages(repository.Name ?? "Unnamed Repo", repository.Url, true, cancellationToken).ConfigureAwait(true))
                    {
                        if (!Guid.TryParse(package.Id, out var packageGuid))
                        {
                            // Package doesn't have a valid GUID, skip.
                            continue;
                        }

                        var existing = FilterPackages(result, package.Name, packageGuid).FirstOrDefault();

                        // Remove invalid versions from the valid package.
                        for (var i = package.Versions.Count - 1; i >= 0; i--)
                        {
                            var version = package.Versions[i];

                            var plugin = _pluginManager.GetPlugin(packageGuid, version.VersionNumber);
                            // Update the manifests, if anything changes.
                            if (plugin != null)
                            {
                                if (!string.Equals(plugin.Manifest.TargetAbi, version.TargetAbi, StringComparison.Ordinal))
                                {
                                    plugin.Manifest.TargetAbi = version.TargetAbi ?? string.Empty;
                                    _pluginManager.SaveManifest(plugin.Manifest, plugin.Path);
                                }
                            }

                            // Remove versions with a target abi that is greater then the current application version.
                            if (Version.TryParse(version.TargetAbi, out var targetAbi) && _applicationHost.ApplicationVersion < targetAbi)
                            {
                                package.Versions.RemoveAt(i);
                            }
                        }

                        // Don't add a package that doesn't have any compatible versions.
                        if (package.Versions.Count == 0)
                        {
                            continue;
                        }

                        if (existing != null)
                        {
                            // Assumption is both lists are ordered, so slot these into the correct place.
                            MergeSortedList(existing.Versions, package.Versions);
                        }
                        else
                        {
                            result.Add(package);
                        }
                    }
                }
            }

            return result;
        }

        /// <inheritdoc />
        public IEnumerable<PackageInfo> FilterPackages(
            IEnumerable<PackageInfo> availablePackages,
            string? name = null,
            Guid? id = default,
            Version? specificVersion = null)
        {
            if (name != null)
            {
                availablePackages = availablePackages.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }

            if (id != Guid.Empty)
            {
                availablePackages = availablePackages.Where(x => Guid.Parse(x.Id) == id);
            }

            if (specificVersion != null)
            {
                availablePackages = availablePackages.Where(x => x.Versions.Any(y => y.VersionNumber.Equals(specificVersion)));
            }

            return availablePackages;
        }

        /// <inheritdoc />
        public IEnumerable<InstallationInfo> GetCompatibleVersions(
            IEnumerable<PackageInfo> availablePackages,
            string? name = null,
            Guid? id = default,
            Version? minVersion = null,
            Version? specificVersion = null)
        {
            var package = FilterPackages(availablePackages, name, id, specificVersion).FirstOrDefault();

            // Package not found in repository
            if (package == null)
            {
                yield break;
            }

            var appVer = _applicationHost.ApplicationVersion;
            var availableVersions = package.Versions
                .Where(x => string.IsNullOrEmpty(x.TargetAbi) || Version.Parse(x.TargetAbi) <= appVer);

            if (specificVersion != null)
            {
                availableVersions = availableVersions.Where(x => x.VersionNumber.Equals(specificVersion));
            }
            else if (minVersion != null)
            {
                availableVersions = availableVersions.Where(x => x.VersionNumber >= minVersion);
            }

            foreach (var v in availableVersions.OrderByDescending(x => x.VersionNumber))
            {
                yield return new InstallationInfo
                {
                    Changelog = v.Changelog,
                    Id = new Guid(package.Id),
                    Name = package.Name,
                    Version = v.VersionNumber,
                    SourceUrl = v.SourceUrl,
                    Checksum = v.Checksum
                };
            }
        }

        /// <inheritdoc />
        public async Task<IEnumerable<InstallationInfo>> GetAvailablePluginUpdates(CancellationToken cancellationToken = default)
        {
            var catalog = await GetAvailablePackages(cancellationToken).ConfigureAwait(false);
            return GetAvailablePluginUpdates(catalog);
        }

        /// <inheritdoc />
        public async Task InstallPackage(InstallationInfo package, CancellationToken cancellationToken)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            var innerCancellationTokenSource = new CancellationTokenSource();

            var tuple = (package, innerCancellationTokenSource);

            // Add it to the in-progress list
            lock (_currentInstallationsLock)
            {
                _currentInstallations.Add(tuple);
            }

            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, innerCancellationTokenSource.Token);
            var linkedToken = linkedTokenSource.Token;

            await _eventManager.PublishAsync(new PluginInstallingEventArgs(package)).ConfigureAwait(false);

            try
            {
                var isUpdate = await InstallPackageInternal(package, linkedToken).ConfigureAwait(false);

                lock (_currentInstallationsLock)
                {
                    _currentInstallations.Remove(tuple);
                }

                _completedInstallationsInternal.Add(package);
                await _eventManager.PublishAsync(isUpdate
                    ? (GenericEventArgs<InstallationInfo>)new PluginUpdatedEventArgs(package)
                    : new PluginInstalledEventArgs(package)).ConfigureAwait(false);

                _applicationHost.NotifyPendingRestart();
            }
            catch (OperationCanceledException)
            {
                lock (_currentInstallationsLock)
                {
                    _currentInstallations.Remove(tuple);
                }

                _logger.LogInformation("Package installation cancelled: {0} {1}", package.Name, package.Version);

                await _eventManager.PublishAsync(new PluginInstallationCancelledEventArgs(package)).ConfigureAwait(false);

                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Package installation failed");

                lock (_currentInstallationsLock)
                {
                    _currentInstallations.Remove(tuple);
                }

                await _eventManager.PublishAsync(new InstallationFailedEventArgs
                {
                    InstallationInfo = package,
                    Exception = ex
                }).ConfigureAwait(false);

                throw;
            }
            finally
            {
                // Dispose the progress object and remove the installation from the in-progress list
                tuple.innerCancellationTokenSource.Dispose();
            }
        }

        /// <summary>
        /// Uninstalls a plugin.
        /// </summary>
        /// <param name="plugin">The <see cref="LocalPlugin"/> to uninstall.</param>
        public void UninstallPlugin(LocalPlugin plugin)
        {
            if (plugin == null)
            {
                return;
            }

            if (plugin.Instance?.CanUninstall == false)
            {
                _logger.LogWarning("Attempt to delete non removable plugin {PluginName}, ignoring request", plugin.Name);
                return;
            }

            plugin.Instance?.OnUninstalling();

            // Remove it the quick way for now
            _pluginManager.RemovePlugin(plugin);

            _eventManager.Publish(new PluginUninstalledEventArgs(plugin.GetPluginInfo()));

            _applicationHost.NotifyPendingRestart();
        }

        /// <inheritdoc/>
        public bool CancelInstallation(Guid id)
        {
            lock (_currentInstallationsLock)
            {
                var install = _currentInstallations.Find(x => x.info.Id == id);
                if (install == default((InstallationInfo, CancellationTokenSource)))
                {
                    return false;
                }

                install.token.Cancel();
                _currentInstallations.Remove(install);
                return true;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and optionally managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources or <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                lock (_currentInstallationsLock)
                {
                    foreach (var (info, token) in _currentInstallations)
                    {
                        token.Dispose();
                    }

                    _currentInstallations.Clear();
                }
            }
        }

        /// <summary>
        /// Merges two sorted lists.
        /// </summary>
        /// <param name="source">The source <see cref="IList{VersionInfo}"/> instance to merge.</param>
        /// <param name="dest">The destination <see cref="IList{VersionInfo}"/> instance to merge with.</param>
        private static void MergeSortedList(IList<VersionInfo> source, IList<VersionInfo> dest)
        {
            int sLength = source.Count - 1;
            int dLength = dest.Count;
            int s = 0, d = 0;
            var sourceVersion = source[0].VersionNumber;
            var destVersion = dest[0].VersionNumber;

            while (d < dLength)
            {
                if (sourceVersion.CompareTo(destVersion) >= 0)
                {
                    if (s < sLength)
                    {
                        sourceVersion = source[++s].VersionNumber;
                    }
                    else
                    {
                        // Append all of destination to the end of source.
                        while (d < dLength)
                        {
                            source.Add(dest[d++]);
                        }

                        break;
                    }
                }
                else
                {
                    source.Insert(s++, dest[d++]);
                    if (d >= dLength)
                    {
                        break;
                    }

                    sLength++;
                    destVersion = dest[d].VersionNumber;
                }
            }
        }

        private IEnumerable<InstallationInfo> GetAvailablePluginUpdates(IReadOnlyList<PackageInfo> pluginCatalog)
        {
            var plugins = _pluginManager.Plugins;
            foreach (var plugin in plugins)
            {
                if (plugin.Manifest?.AutoUpdate == false)
                {
                    continue;
                }

                var compatibleVersions = GetCompatibleVersions(pluginCatalog, plugin.Name, plugin.Id, minVersion: plugin.Version);
                var version = compatibleVersions.FirstOrDefault(y => y.Version > plugin.Version);

                if (version != null && CompletedInstallations.All(x => x.Id != version.Id))
                {
                    yield return version;
                }
            }
        }

        private async Task PerformPackageInstallation(InstallationInfo package, CancellationToken cancellationToken)
        {
            var extension = Path.GetExtension(package.SourceUrl);
            if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Only zip packages are supported. {SourceUrl} is not a zip archive.", package.SourceUrl);
                return;
            }

            // Always override the passed-in target (which is a file) and figure it out again
            string targetDir = Path.Combine(_appPaths.PluginsPath, package.Name);

            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .GetAsync(new Uri(package.SourceUrl), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // CA5351: Do Not Use Broken Cryptographic Algorithms
#pragma warning disable CA5351
            using var md5 = MD5.Create();
            cancellationToken.ThrowIfCancellationRequested();

            var hash = Convert.ToHexString(md5.ComputeHash(stream));
            if (!string.Equals(package.Checksum, hash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "The checksums didn't match while installing {Package}, expected: {Expected}, got: {Received}",
                    package.Name,
                    package.Checksum,
                    hash);
                throw new InvalidDataException("The checksum of the received data doesn't match.");
            }

            // Version folder as they cannot be overwritten in Windows.
            targetDir += "_" + package.Version;

            if (Directory.Exists(targetDir))
            {
                try
                {
                    Directory.Delete(targetDir, true);
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch
#pragma warning restore CA1031 // Do not catch general exception types
                {
                    // Ignore any exceptions.
                }
            }

            stream.Position = 0;
            _zipClient.ExtractAllFromZip(stream, targetDir, true);
            _pluginManager.ImportPluginFrom(targetDir);
        }

        private async Task<bool> InstallPackageInternal(InstallationInfo package, CancellationToken cancellationToken)
        {
            // Set last update time if we were installed before
            LocalPlugin? plugin = _pluginManager.Plugins.FirstOrDefault(p => p.Id.Equals(package.Id) && p.Version.Equals(package.Version))
                  ?? _pluginManager.Plugins.FirstOrDefault(p => p.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase) && p.Version.Equals(package.Version));
            if (plugin != null)
            {
                plugin.Manifest.Timestamp = DateTime.UtcNow;
                _pluginManager.SaveManifest(plugin.Manifest, plugin.Path);
            }

            // Do the install
            await PerformPackageInstallation(package, cancellationToken).ConfigureAwait(false);

            // Do plugin-specific processing
            _logger.LogInformation(plugin == null ? "New plugin installed: {PluginName} {PluginVersion}" : "Plugin updated: {PluginName} {PluginVersion}", package.Name, package.Version);

            return plugin != null;
        }
    }
}
