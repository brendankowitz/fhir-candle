// <copyright file="FhirPackageService.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using fhir.candle._ForPackages;
using FhirCandle.Configuration;
using FhirCandle.Models;
using FhirCandle.Utils;
using Ignixa.PackageManagement.Abstractions;
using Ignixa.PackageManagement.DTOs;
using Ignixa.PackageManagement.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Formats.Tar;
using System.IO.Compression;

namespace fhir.candle.Services;

/// <summary>A service for accessing FHIR packages.</summary>
public partial class FhirPackageService : IFhirPackageService, IDisposable
{
    internal enum VersionHandlingTypes
    {
        /// <summary>Unprocessed / unknown / SemVer / ranges / etc (pass through).</summary>
        Passthrough,

        /// <summary>Latest release.</summary>
        Latest,

        /// <summary>Local build.</summary>
        Local,

        /// <summary>CI Build.</summary>
        ContinuousIntegration,
    }

    /// <summary>Values that represent package load state enums.</summary>
    public enum PackageLoadStateEnum
    {
        /// <summary>The package is in an unknown state.</summary>
        Unknown,

        /// <summary>The package has not been loaded.</summary>
        NotLoaded,

        /// <summary>The package is queued for loading.</summary>
        Queued,

        /// <summary>The package is currently being loaded.</summary>
        InProgress,

        /// <summary>The package is currently loaded into memory.</summary>
        Loaded,

        /// <summary>The package has failed to load and cannot be used.</summary>
        Failed,

        /// <summary>The package has been parsed but not loaded into memory.</summary>
        Parsed,
    }

    /// <summary>Information about a package in the cache.</summary>
    public readonly record struct PackageCacheRecord(
        string CacheDirective,
        PackageLoadStateEnum PackageState,
        string PackageName,
        string Version,
        FhirReleases.FhirSequenceCodes FhirVersion,
        string DownloadDateTime,
        long PackageSize,
        FhirNpmPackageDetails Details);

    /// <summary>(Immutable) The package registry URIs.</summary>
    private static readonly string[] _officialRegistryUrls =
    [
        "https://packages.fhir.org/",
        "https://packages2.fhir.org/packages/",
    ];

    /// <summary>The logger.</summary>
    private readonly ILogger _logger;

    /// <summary>Optional logger factory for Ignixa package management components.</summary>
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>Server configuration.</summary>
    private readonly CandleConfig _config;

    /// <summary>(Immutable) The HTTP client shared by registry loaders and search services.</summary>
    private readonly HttpClient _httpClient = new();

    /// <summary>(Immutable) The FHIR CI client (build.fhir.org).</summary>
    private readonly FhirCiClient _ciClient = new();

    /// <summary>Per-registry package search services, in resolution priority order.</summary>
    private readonly List<NpmPackageSearchService> _registrySearchServices = [];

    /// <summary>Composite loader over all configured registries, null until configured.</summary>
    private IPackageLoader? _packageLoader = null;

    /// <summary>Pathname of the cache package directory.</summary>
    private string _cachePackageDirectory = string.Empty;

    /// <summary>True if is initialized, false if not.</summary>
    private bool _isInitialized = false;

    /// <summary>True to disposed value.</summary>
    private bool _disposedValue = false;

    private readonly HashSet<string> _processedMonikers = [];

    /// <summary>The package records, by directive.</summary>
    private readonly Dictionary<string, PackageCacheRecord> _packagesByDirective = new();

    /// <summary>Occurs when On Changed.</summary>
    public event EventHandler<EventArgs>? OnChanged = null;

    /// <summary>Initializes a new instance of the <see cref="FhirPackageService"/> class.</summary>
    /// <param name="logger">             The logger.</param>
    /// <param name="serverConfiguration">The server configuration.</param>
    /// <param name="loggerFactory">      (Optional) The logger factory, used for package-management components.</param>
    public FhirPackageService(
        ILogger<FhirPackageService> logger,
        CandleConfig serverConfiguration,
        ILoggerFactory? loggerFactory = null)
    {
        _logger = logger;
        _config = serverConfiguration;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Gets the packages by directive.</summary>
    public Dictionary<string, PackageCacheRecord> PackagesByDirective => _packagesByDirective;

    /// <summary>Gets a value indicating whether this object is available.</summary>
    public bool IsConfigured => _packageLoader is not null;

    /// <summary>Gets a value indicating whether the package service is ready.</summary>
    public bool IsReady => _isInitialized;

    /// <summary>Initializes this object.</summary>
    public void Init()
    {
        if (_isInitialized)
        {
            return;
        }

        if (_config.FhirCacheDirectory == string.Empty)
        {
            _logger.LogInformation("Disabling FhirPackageService, --fhir-package-cache set to empty.");
            return;
        }

        if (_config.FhirCacheDirectory is null)
        {
            // default to the standard FHIR cache root (~/.fhir), matching other FHIR tooling
            _config.FhirCacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".fhir");
        }

        _logger.LogInformation($"Initializing FhirPackageService with cache: {_config.FhirCacheDirectory}");
        _isInitialized = true;

        if (!Directory.Exists(_config.FhirCacheDirectory))
        {
            Directory.CreateDirectory(_config.FhirCacheDirectory);
            Directory.CreateDirectory(Path.Combine(_config.FhirCacheDirectory, "packages"));
        }

        if (Directory.Exists(Path.Combine(_config.FhirCacheDirectory, "packages")))
        {
            _cachePackageDirectory = Path.Combine(_config.FhirCacheDirectory, "packages");
        }
        else
        {
            _cachePackageDirectory = _config.FhirCacheDirectory;
        }

        List<string> registryUrls = [];

        if (_config.UseOfficialRegistries == true)
        {
            registryUrls.AddRange(_officialRegistryUrls);
        }

        registryUrls.AddRange(_config.AdditionalFhirRegistryUrls);
        registryUrls.AddRange(_config.AdditionalNpmRegistryUrls);

        PackageCacheManager tarballCache = new(
            Path.Combine(_config.FhirCacheDirectory, "tarballs"),
            CreateLogger<PackageCacheManager>());

        List<IPackageLoader> loaders = [];

        foreach (string url in registryUrls)
        {
            NpmPackageLoaderOptions options = new() { RegistryUrl = url.TrimEnd('/') };

            loaders.Add(new NpmPackageLoader(_httpClient, tarballCache, options, CreateLogger<NpmPackageLoader>()));
            _registrySearchServices.Add(new NpmPackageSearchService(_httpClient, options, CreateLogger<NpmPackageSearchService>()));
        }

        if (loaders.Count != 0)
        {
            _packageLoader = new CompositePackageLoader(CreateLogger<CompositePackageLoader>(), [.. loaders]);
        }
    }

    private ILogger<T> CreateLogger<T>() => _loggerFactory?.CreateLogger<T>() ?? NullLogger<T>.Instance;

    /// <summary>Triggered when the application host is ready to start the service.</summary>
    /// <param name="cancellationToken">Indicates that the start process has been aborted.</param>
    /// <returns>An asynchronous result.</returns>
    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        if (_packageLoader is null)
        {
            _logger.LogInformation("Disabling FhirPackageService, --fhir-package-cache set to empty.");
            return Task.CompletedTask;
        }

        _logger.LogInformation($"Starting FhirPackageService...");

        Init();

        return Task.CompletedTask;
    }

    /// <summary>Triggered when the application host is performing a graceful shutdown.</summary>
    /// <param name="cancellationToken">Indicates that the shutdown process should no longer be
    ///  graceful.</param>
    /// <returns>An asynchronous result.</returns>
    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>Converts a FHIR-style directive (name#version) to the npm style (name@version).</summary>
    /// <param name="directive">The input directive.</param>
    /// <returns>The normalized directive; unchanged when it already contains an '@'.</returns>
    internal static string NormalizeDirective(string directive) =>
        directive.Contains('@')
            ? directive
            : directive.Replace('#', '@');

    /// <summary>
    /// Gets the version handling type based on the provided version string.
    /// </summary>
    /// <param name="version">The version string.</param>
    /// <returns>The version handling type.</returns>
    internal static VersionHandlingTypes GetVersionHandlingType(string? version)
    {
        // handle simple literals
        switch (version)
        {
            case null:
            case "":
            case "latest":
                return VersionHandlingTypes.Latest;

            case "current":
                return VersionHandlingTypes.ContinuousIntegration;

            case "dev":
                return VersionHandlingTypes.Local;
        }

        // check for local or current with branch names
        if (version.StartsWith("current$", StringComparison.Ordinal))
        {
            return VersionHandlingTypes.ContinuousIntegration;
        }

        if (version.StartsWith("dev$", StringComparison.Ordinal))
        {
            return VersionHandlingTypes.Local;
        }

        return VersionHandlingTypes.Passthrough;
    }

    /// <summary>
    /// Extracts a FHIR npm package tarball to disk, preserving the archive layout (package/...).
    /// </summary>
    /// <param name="tgzStream">           Stream containing the gzipped tarball.</param>
    /// <param name="packageRootDirectory">Directory the archive is expanded into; the package
    ///  content lands in its package/ subdirectory.</param>
    /// <returns>The parsed package manifest (package.json).</returns>
    internal static FhirNpmPackageDetails ExtractPackageTarball(Stream tgzStream, string packageRootDirectory)
    {
        Directory.CreateDirectory(packageRootDirectory);
        string fullRoot = Path.GetFullPath(packageRootDirectory);

        using GZipStream gz = new(tgzStream, CompressionMode.Decompress, leaveOpen: true);
        using TarReader reader = new(gz);

        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            string name = entry.Name.Replace('\\', '/');
            if (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];
            }

            if ((name.Length == 0) ||
                name.Split('/').Any(segment => (segment == "..") || (segment == string.Empty)))
            {
                continue;
            }

            string destination = Path.GetFullPath(Path.Combine(fullRoot, name));
            if (!destination.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        return FhirNpmPackageDetails.Load(fullRoot);
    }

    /// <summary>Installs packages based on directives or CI literals.</summary>
    /// <exception cref="Exception">Thrown when a package cannot be resolved or installed.</exception>
    /// <param name="packageDirectives">The package directives.</param>
    /// <param name="ciLiterals">       The ci literals.</param>
    /// <param name="fhirVersions">     The FHIR versions.</param>
    /// <returns>An asynchronous result that yields a List&lt;InstalledPackage&gt;</returns>
    public async Task<List<InstalledPackage>> InstallPackages(
        string[]? packageDirectives,
        string[]? ciLiterals,
        List<FhirReleases.FhirSequenceCodes>? fhirVersions)
    {
        List<InstalledPackage> localPackages = [];

        List<string> directives = packageDirectives?.ToList() ?? new();

        directives.AddRange(await ResolveCiLiterals(ciLiterals));

        if (directives.Count == 0)
        {
            return [];
        }

        if (_packageLoader is null)
        {
            _logger.LogError("InstallPackages <<< Packages have been requested, but no cache has been configured!");
            return [];
        }

        // traverse our package directives
        foreach (string inputDirective in directives)
        {
            string directive = NormalizeDirective(inputDirective);

            int separatorIndex = directive.IndexOf('@');
            string name = separatorIndex == -1 ? directive : directive[..separatorIndex];
            string? version = separatorIndex == -1 ? null : directive[(separatorIndex + 1)..];

            if (string.IsNullOrEmpty(name))
            {
                _logger.LogWarning($"InstallPackages <<< Failed to parse package reference: {directive}");
                continue;
            }

            bool needsInstall = true;
            bool isCiPackage = false;

            VersionHandlingTypes vht = GetVersionHandlingType(version);

            // do special handling for versions if necessary
            switch (vht)
            {
                case VersionHandlingTypes.Latest:
                    {
                        // resolve the version via the registries so that we have access to the actual version number
                        version = await ResolveLatestVersion(name)
                            ?? throw new Exception($"Failed to resolve latest version of {name} ({directive})");

                        needsInstall = !IsPackageInstalled(name, version);
                    }
                    break;

                case VersionHandlingTypes.Local:
                    // ensure there is a local build, there is no other source
                    {
                        if (!IsPackageInstalled(name, version!))
                        {
                            throw new Exception($"Local build of {name} is not installed ({directive})");
                        }

                        needsInstall = false;
                    }
                    break;

                case VersionHandlingTypes.ContinuousIntegration:
                    // always trigger install/update for CI builds
                    needsInstall = true;
                    isCiPackage = true;
                    break;

                default:
                    needsInstall = !IsPackageInstalled(name, version!);
                    break;
            }

            string moniker = $"{name}@{version}";

            // skip if we have already loaded this package
            if (_processedMonikers.Contains(moniker))
            {
                _logger.LogInformation($"Skipping already loaded dependency: {moniker}");
                continue;
            }
            _processedMonikers.Add(moniker);

            _logger.LogInformation($"Processing {moniker}...");

            InstalledPackage installedPackage;

            if (isCiPackage)
            {
                try
                {
                    installedPackage = await _ciClient.InstallOrUpdateAsync(name, version, _cachePackageDirectory);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to install package {moniker} as requested by {inputDirective}", ex);
                }
            }
            else
            {
                string packageRoot = Path.Combine(_cachePackageDirectory, $"{name}#{version}");

                if (needsInstall &&
                    !await InstallPackage(name, version!, packageRoot))
                {
                    // failed to install
                    throw new Exception($"Failed to install package {moniker} as requested by {inputDirective}");
                }

                installedPackage = new InstalledPackage(name, version!, $"{name}#{version}", Path.Combine(packageRoot, "package"));
            }

            // add this package
            localPackages.Add(installedPackage);

            // check to see if we have a specified FHIR versions and need to filter
            if (fhirVersions?.Count > 0)
            {
                // read the manifest to pull the FHIR version of the package
                FhirNpmPackageDetails manifest = LoadManifest(installedPackage.ContentDirectory)
                    ?? throw new Exception("Failed to load package manifest");

                if (GetManifestFhirVersions(manifest).FirstOrDefault() is not string manifestFhirVersion)
                {
                    _logger.LogInformation($"InstallPackages <<< Package {moniker} does not report a FHIR version!");
                    continue;
                }

                // get the FHIR version of the package
                FhirReleases.FhirSequenceCodes packageFhirSequence = FhirReleases.FhirVersionToSequence(manifestFhirVersion);

                // iterate over our requested FHIR versions
                foreach (FhirReleases.FhirSequenceCodes fhirSequence in fhirVersions)
                {
                    if (packageFhirSequence == fhirSequence)
                    {
                        continue;
                    }

                    _logger.LogInformation($"InstallPackages <<< {moniker} ({manifestFhirVersion}) does not match requested FHIR version {fhirSequence}!");

                    string packageIdSuffix = name.Split('.')[^1];
                    FhirReleases.FhirSequenceCodes packageIdSuffixCode = FhirReleases.FhirVersionToSequence(packageIdSuffix);

                    string requiredRLiteral = fhirSequence.ToRLiteral().ToLowerInvariant();
                    string desiredName = (packageIdSuffixCode == FhirReleases.FhirSequenceCodes.Unknown)
                        ? $"{name}.{requiredRLiteral}"
                        : $"{string.Join('.', name.Split('.')[..^1])}.{requiredRLiteral}";
                    string desiredMoniker = $"{desiredName}@{version}";

                    // check to see if this package exists anywhere
                    if (!await PackageExists(desiredName))
                    {
                        continue;
                    }

                    // install this package
                    List<InstalledPackage> deps = await InstallPackages([desiredMoniker], null, fhirVersions);

                    if (_processedMonikers.Contains(desiredMoniker))
                    {
                        _logger.LogInformation($"Package {desiredMoniker} loaded for {moniker}!");
                    }
                    else
                    {
                        _logger.LogInformation($"Could not find substitute for {moniker} - please specify manually if this is required!");
                    }

                    localPackages.AddRange(deps);
                }
            }
        }

        return localPackages;
    }

    /// <summary>Resolves the latest published version of a package across the configured registries.</summary>
    /// <param name="name">The package name.</param>
    /// <returns>The latest version, or null if the package cannot be found on any registry.</returns>
    private async Task<string?> ResolveLatestVersion(string name)
    {
        List<string> candidates = [];

        foreach (NpmPackageSearchService searchService in _registrySearchServices)
        {
            try
            {
                PackageDetails? details = await searchService.GetPackageDetailsAsync(name);

                string? latest = details?.LatestVersion ?? details?.Versions.FirstOrDefault()?.Version;

                if (!string.IsNullOrEmpty(latest))
                {
                    candidates.Add(latest!);
                }
            }
            catch (Exception)
            {
                // ignore - registries that do not know the package throw
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.OrderByDescending(v => v, StringComparer.Ordinal).First();
    }

    /// <summary>
    /// Installs a package by downloading it from the configured registries and expanding it on disk.
    /// </summary>
    /// <param name="name">       The package name.</param>
    /// <param name="version">    The package version.</param>
    /// <param name="packageRoot">The directory the package is expanded into.</param>
    /// <returns>A task representing the asynchronous operation. The task result contains a boolean value indicating whether the package was installed successfully.</returns>
    private async Task<bool> InstallPackage(string name, string version, string packageRoot)
    {
        if (_packageLoader is null)
        {
            return false;
        }

        try
        {
            await using Stream tgz = await _packageLoader.DownloadPackageAsync(name, version, CancellationToken.None);

            if (Directory.Exists(packageRoot))
            {
                Directory.Delete(packageRoot, true);
            }

            _ = ExtractPackageTarball(tgz, packageRoot);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"InstallPackage <<< failed to install {name}@{version}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Checks whether a package version exists on any configured registry.</summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>True if the package exists, false if not.</returns>
    private async Task<bool> PackageExists(string packageId)
    {
        foreach (NpmPackageSearchService searchService in _registrySearchServices)
        {
            try
            {
                PackageDetails? details = await searchService.GetPackageDetailsAsync(packageId);

                if ((details?.Versions.Count > 0) ||
                    (details?.LatestVersion is not null))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // ignore
            }
        }

        return false;
    }

    /// <summary>Checks whether a package has already been expanded into the local cache.</summary>
    private bool IsPackageInstalled(string name, string version) =>
        Directory.Exists(Path.Combine(_cachePackageDirectory, $"{name}#{version}", "package"));

    /// <summary>Loads the package.json manifest from an expanded content directory.</summary>
    private static FhirNpmPackageDetails? LoadManifest(string contentDirectory)
    {
        try
        {
            return FhirNpmPackageDetails.Load(contentDirectory);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the FHIR versions declared by a manifest: fhirVersions / fhir-version-list / fhirVersion,
    /// falling back to versions derived from core-package dependencies.
    /// </summary>
    private static List<string> GetManifestFhirVersions(FhirNpmPackageDetails manifest) =>
        manifest.FhirVersions.Any()
            ? manifest.FhirVersions.ToList()
            : VersionExtensions.FhirVersionsFromPackages(manifest.Dependencies);

    /// <summary>
    /// Retrieves the FHIR versions supported by a package.
    /// </summary>
    /// <param name="package">The installed package.</param>
    /// <returns>A list of FHIR sequence codes representing the supported versions.</returns>
    public Task<List<FhirReleases.FhirSequenceCodes>?> InstalledPackageFhirVersions(InstalledPackage package)
    {
        if (_packageLoader is null)
        {
            return Task.FromResult<List<FhirReleases.FhirSequenceCodes>?>(null);
        }

        if (!Directory.Exists(package.ContentDirectory))
        {
            return Task.FromResult<List<FhirReleases.FhirSequenceCodes>?>(null);
        }

        FhirNpmPackageDetails manifest = LoadManifest(package.ContentDirectory)
            ?? throw new Exception("Failed to load package manifest");

        return Task.FromResult<List<FhirReleases.FhirSequenceCodes>?>(
            GetManifestFhirVersions(manifest).Select(FhirReleases.FhirVersionToSequence).ToList());
    }

    /// <summary>
    /// Gets the content directory for a specific package.
    /// </summary>
    /// <param name="package">The installed package.</param>
    /// <returns>The content directory for the package, or null if the cache is not configured.</returns>
    public string? GetPackageContentDirectory(InstalledPackage package)
    {
        if (_packageLoader is null)
        {
            return null;
        }

        return Directory.Exists(package.ContentDirectory)
            ? package.ContentDirectory
            : null;
    }

    /// <summary>
    /// Deletes a package based on the provided package directive.
    /// </summary>
    /// <param name="packageDirective">The package directive specifying the package to delete.</param>
    public void DeletePackage(string packageDirective)
    {
        if (string.IsNullOrEmpty(_cachePackageDirectory))
        {
            return;
        }

        string[] components = packageDirective.Split('@', '#');

        if (components.Length != 2)
        {
            _logger.LogWarning($"DeletePackage <<< invalid package directive: {packageDirective}");
            return;
        }

        string packageRoot = Path.Combine(_cachePackageDirectory, $"{components[0]}#{components[1]}");

        if (Directory.Exists(packageRoot))
        {
            Directory.Delete(packageRoot, true);
        }
    }

    /// <summary>
    /// Resolves the CI literals into standard directives.
    /// </summary>
    /// <param name="ciLiterals">The CI literals to resolve.</param>
    /// <returns>A list of resolved directives.</returns>
    private async Task<List<string>> ResolveCiLiterals(string[]? ciLiterals)
    {
        List<string> directives = [];

        // iterate over CI directives to resolve them into standard directives
        foreach (string literal in ciLiterals ?? Array.Empty<string>())
        {
            // check to see if this is a tagged package literal
            if (literal.EndsWith("current") ||
                literal.Contains("current$"))
            {
                directives.Add(literal);
                continue;
            }

            // try the repository reference first
            List<CiPackageCatalogEntry> entries = await _ciClient.CatalogPackagesAsync(repo: literal);
            if (entries.Count == 0)
            {
                // check for a publication URL
                entries = await _ciClient.CatalogPackagesAsync(site: literal);
            }

            if (entries.Count == 0)
            {
                // check for a package name
                entries = await _ciClient.CatalogPackagesAsync(pkgname: literal);
            }

            if (entries.Count == 0)
            {
                _logger.LogWarning($"ResolveCiLiterals <<< cannot resolve CI directive: {literal}!");
                continue;
            }

            CiPackageCatalogEntry entry = entries.First();

            // check to see if we have a package name and repository URL
            if (string.IsNullOrEmpty(entry.Name) ||
                string.IsNullOrEmpty(entry.RepositoryUrl) ||
                !entry.RepositoryUrl!.Contains('/'))
            {
                _logger.LogWarning($"ResolveCiLiterals <<< invalid resolution for CI directive: {literal}! Name: {entry.Name}, Repository: {entry.RepositoryUrl}");
                continue;
            }

            // get the branch name from the repo url
            (string? branchName, bool isDefaultBranch) = FhirCiClient.GetBranchNameRepoLiteral(entry.RepositoryUrl);

            if (isDefaultBranch)
            {
                directives.Add(entry.Name + "#current");
                continue;
            }

            if (string.IsNullOrEmpty(branchName))
            {
                _logger.LogWarning($"ResolveCiLiterals <<< invalid resolution for CI directive: {literal} - no branch name and not default branch!");
                continue;
            }

            directives.Add(entry.Name + "#current$" + branchName);
        }

        return directives;
    }

    /// <summary>Updates the package state.</summary>
    /// <param name="directive">      The directive.</param>
    /// <param name="resolvedName">   Name of the resolved.</param>
    /// <param name="resolvedVersion">The resolved version.</param>
    /// <param name="toState">        State of to.</param>
    public void UpdatePackageState(
        string directive,
        string resolvedName,
        string resolvedVersion,
        PackageLoadStateEnum toState)
    {
        if (!_packagesByDirective.ContainsKey(directive))
        {
            _packagesByDirective.Add(directive, new()
            {
                CacheDirective = directive,
                PackageState = toState,
            });
        }

        _packagesByDirective[directive] = _packagesByDirective[directive] with
        {
            PackageState = toState,
            PackageName = string.IsNullOrEmpty(resolvedName) ? _packagesByDirective[directive].PackageName : resolvedName,
            Version = string.IsNullOrEmpty(resolvedVersion) ? _packagesByDirective[directive].Version : resolvedVersion,
        };

        StateHasChanged();
    }

    /// <summary>
    /// Attempts to get a package state, returning a default value rather than throwing an exception
    /// if it fails.
    /// </summary>
    /// <param name="directive">The directive.</param>
    /// <param name="state">    [out] The state.</param>
    /// <returns>True if it succeeds, false if it fails.</returns>
    public bool TryGetPackageState(string directive, out PackageLoadStateEnum state)
    {
        if (!_packagesByDirective.TryGetValue(directive, out PackageCacheRecord cacheRecord))
        {
            state = PackageLoadStateEnum.Unknown;
            return false;
        }

        state = cacheRecord.PackageState;
        return true;
    }

    /// <summary>
    /// Releases the unmanaged resources used by the <see cref="FhirPackageService"/>
    /// and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to
    ///  release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _httpClient.Dispose();
                ((IDisposable)_ciClient).Dispose();
            }

            _disposedValue = true;
        }
    }

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged
    /// resources.
    /// </summary>
    void IDisposable.Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>State has changed.</summary>
    public void StateHasChanged()
    {
        OnChanged?.Invoke(this, new());
    }
}
