using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using fhir.candle.Services;
using FhirCandle.Models;
using Newtonsoft.Json;

#nullable enable

namespace fhir.candle._ForPackages
{
    /// <summary>A package entry resolved from the CI server QA listings.</summary>
    /// <param name="Name">         The package id.</param>
    /// <param name="RepositoryUrl">The partial repository URL (e.g., HL7/fhir-us-core/branches/main/qa.json).</param>
    /// <param name="FhirVersion">  The FHIR version of the package.</param>
    public sealed record CiPackageCatalogEntry(string? Name, string? RepositoryUrl, string? FhirVersion);

    /// <summary>
    /// Represents a client for interacting with a FHIR Continuous Integration (CI) server.
    /// </summary>
    public class FhirCiClient : IDisposable
    {
        /// <summary>(Immutable) The default time to refresh the CI build listings, 0 for every request (no caching), -1 to never automatically refresh.</summary>
        private const int _defaultInvalidationSeconds = -1;

        /// <summary>(Immutable) Literal used to designate the branch name in the version string.</summary>
        private const string _ciBranchDelimiter = ".b-";

        /// <summary>(Immutable) The CI version date format.</summary>
        private const string _ciVersionDateFormat = "yyyyMMdd-HHmmssZ";

        /// <summary>(Immutable) The manifest date format used in package.json 'date' properties.</summary>
        private const string _manifestDateFormat = "yyyyMMddHHmmss";

        /// <summary>(Immutable) The default branch names.</summary>
        private static readonly HashSet<string> _defaultBranchNames = new() { "main", "master" };

        /// <summary>(Immutable) The ci URI using HTTPS.</summary>
        private static readonly Uri _ciUriS = new("https://build.fhir.org/");

        private static readonly Uri _ciBranchUri = new("https://build.fhir.org/branches/");

        /// <summary>(Immutable) URI of the qas.</summary>
        private static readonly Uri _qasUri = new("https://build.fhir.org/ig/qas.json");

        /// <summary>(Immutable) The HTTP client.</summary>
        private readonly HttpClient _httpClient;

        /// <summary>The QAS records for the CI server, grouped by package id.</summary>
        private Dictionary<string, List<FhirCiQaRecord>> _qasByPackageId = new();

        /// <summary>The QAS records for the CI server.</summary>
        private FhirCiQaRecord[] _qas = Array.Empty<FhirCiQaRecord>();

        /// <summary>When the local copy of the CI QAS was last updated.</summary>
        private DateTimeOffset _qasLastUpdated = DateTimeOffset.MinValue;

        /// <summary>The listing invalidation in seconds.</summary>
        private int _listingInvalidationSeconds;

        /// <summary>Initializes a new instance of the <see cref="FhirCiClient"/> class.</summary>
        /// <param name="listingInvalidationSeconds">(Optional) The listing invalidation in seconds, 0 for
        ///  every request (no caching), -1 to never automatically refresh.</param>
        /// <param name="client">                    (Optional) The <see cref="HttpClient"/> instance to
        ///  use. If null, a new instance will be created.</param>
        public FhirCiClient(int listingInvalidationSeconds = _defaultInvalidationSeconds, HttpClient? client = null)
        {
            _listingInvalidationSeconds = listingInvalidationSeconds;
            _httpClient = client ?? new HttpClient();
        }

        /// <inheritdoc/>
        public override string? ToString() => _ciUriS.ToString();

        /// <summary>Get the QA records and update the cache if necessary.</summary>
        /// <param name="forceRefresh">(Optional) True to force a refresh.</param>
        /// <returns>An asynchronous result that yields a list of.</returns>
        private async Task<(FhirCiQaRecord[] qas, Dictionary<string, List<FhirCiQaRecord>> qasByPackageId)> getQAs(bool forceRefresh = false)
        {
            // check for having a cached copy and configuration to never refresh the cache
            if (!forceRefresh && (_listingInvalidationSeconds == -1) && (_qas.Length != 0))
            {
                // return what we have
                return (_qas, _qasByPackageId);
            }

            // check for having a cached copy and not needing to refresh
            if (!forceRefresh &&
                (_listingInvalidationSeconds > 0) &&
                (_qasLastUpdated.AddSeconds(_listingInvalidationSeconds) >= DateTimeOffset.Now))
            {
                // return what we have
                return (_qas, _qasByPackageId);
            }

            List<FhirCiQaRecord>? updatedGuideQas = await downloadGuideQAs();
            List<FhirCiQaRecord>? updatedCoreQas = await downloadCoreQAs();

            // join our sets together
            FhirCiQaRecord[] updatedQAs = (updatedCoreQas ?? Enumerable.Empty<FhirCiQaRecord>()).Concat(updatedGuideQas ?? Enumerable.Empty<FhirCiQaRecord>()).ToArray();
            if (updatedQAs.Length == 0)
            {
                return (_qas, _qasByPackageId);
            }

            Dictionary<string, List<FhirCiQaRecord>> qasByPackageId = new();

            // iterate over the QAS records and add them to a dictionary
            foreach (FhirCiQaRecord qas in updatedQAs)
            {
                if (qas.PackageId is null)
                {
                    continue;
                }

                if (!qasByPackageId.TryGetValue(qas.PackageId, out List<FhirCiQaRecord>? qasRecs))
                {
                    qasRecs = new List<FhirCiQaRecord>();
                    qasByPackageId.Add(qas.PackageId, qasRecs);
                }

                qasRecs.Add(qas);
            }

            // update our cache if necessary
            if (forceRefresh || (_listingInvalidationSeconds != 0))
            {
                _qas = updatedQAs;
                _qasByPackageId = qasByPackageId;
                _qasLastUpdated = DateTimeOffset.Now;

                // return our updated version
                return (_qas, _qasByPackageId);
            }

            // return what we downloaded
            return (updatedQAs, qasByPackageId);
        }

        /// <summary>
        /// Downloads the current qas.json file from the build server and deserializes into an array of <see cref="FhirCiQaRecord"/>.
        /// </summary>
        /// <returns>An array of FhirCiQaRecord objects representing the current FhirCiQaRecords.</returns>
        private async Task<List<FhirCiQaRecord>?> downloadGuideQAs()
        {
            // download the QA records from the build server
            HttpRequestMessage request = new HttpRequestMessage()
            {
                Method = HttpMethod.Get,
                RequestUri = _qasUri,
                Headers =
                {
                    Accept =
                    {
                        new MediaTypeWithQualityHeaderValue("application/json"),
                    },
                },
            };

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            System.Net.HttpStatusCode statusCode = response.StatusCode;

            if (statusCode != System.Net.HttpStatusCode.OK)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            List<FhirCiQaRecord>? qas = JsonConvert.DeserializeObject<List<FhirCiQaRecord>>(json);

            // if we do not have records, we are done
            if (qas is null)
            {
                return null;
            }

            return qas;
        }

        private async Task<List<FhirCiQaRecord>> downloadCoreQAs()
        {
            List<FhirCiQaRecord> qas = new();

            // download the branch list from the core build
            HttpRequestMessage request = new HttpRequestMessage()
            {
                Method = HttpMethod.Get,
                RequestUri = _ciBranchUri,
                Headers =
                {
                    Accept =
                    {
                        new MediaTypeWithQualityHeaderValue("application/json"),
                    },
                },
            };

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            System.Net.HttpStatusCode statusCode = response.StatusCode;

            if (statusCode != System.Net.HttpStatusCode.OK)
            {
                return qas;
            }

            string json = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(json))
            {
                return qas;
            }

            CiBranchRecord[]? coreBranches = JsonConvert.DeserializeObject<CiBranchRecord[]>(json);

            if (coreBranches is null)
            {
                return qas;
            }

            // traverse the core branches to build their records
            foreach (CiBranchRecord ciBranchRec in coreBranches)
            {
                if ((ciBranchRec.Name is null) ||
                    string.IsNullOrEmpty(ciBranchRec.Name) ||
                    string.IsNullOrEmpty(ciBranchRec.Url))
                {
                    continue;
                }

                // download the version.info for this branch
                request = new HttpRequestMessage()
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri(_ciBranchUri, ciBranchRec.Url + "version.info"),
                    Headers =
                    {
                        Accept =
                        {
                            new MediaTypeWithQualityHeaderValue("text/plain"),
                        },
                    },
                };

                response = await _httpClient.SendAsync(request);
                statusCode = response.StatusCode;

                if (statusCode != System.Net.HttpStatusCode.OK)
                {
                    continue;
                }

                string contents = await response.Content.ReadAsStringAsync();

                // grab the contents we can out of the version.info file
                parseVersionInfoIni(
                    contents,
                    out string ciFhirVersion,
                    out string ciVersion,
                    out string _,
                    out DateTimeOffset? ciBuildDate);

                string packageId = $"hl7.fhir.r{ciFhirVersion.Split('.').First()}.core";

                if (ciBranchRec.Name == "master/")
                {
                    // build a QA record for the main branch
                    qas.Add(new()
                    {
                        Url = "https://build.fhir.org",
                        Name = "FHIR Core " + ciFhirVersion,
                        Title = "FHIR Core build",
                        Status = "draft",
                        PackageId = packageId,
                        PackageVersion = ciVersion,
                        BuildDate = ciBuildDate,
                        BuildDateIso = ciBuildDate,
                        FhirVersion = ciFhirVersion,
                        RepositoryUrl = "HL7/fhir/branches/master/qa.json"
                    });
                }
                else
                {
                    // build a QA record for this branch
                    qas.Add(new()
                    {
                        Url = "https://build.fhir.org/branches/" + ciBranchRec.Name.Substring(0, ciBranchRec.Name.Length - 1),
                        Name = "FHIR Core " + ciFhirVersion + " branch: " + ciBranchRec.Name,
                        Title = "FHIR Core build",
                        Status = "draft",
                        PackageId = packageId,
                        PackageVersion = ciVersion,
                        BuildDate = ciBuildDate,
                        BuildDateIso = ciBuildDate,
                        FhirVersion = ciFhirVersion,
                        RepositoryUrl = $"HL7/fhir/branches/{ciBranchRec.Name}/qa.json"
                    });
                }
            }

            return qas;
        }

        /// <summary>Gets local version information.</summary>
        /// <param name="contents">   The contents.</param>
        /// <param name="fhirVersion">[out] The FHIR version.</param>
        /// <param name="version">    [out] The version string (e.g., 4.0.1).</param>
        /// <param name="buildId">    [out] Identifier for the build.</param>
        /// <param name="buildDate">  [out] The build date.</param>
        private static void parseVersionInfoIni(
            string contents,
            out string fhirVersion,
            out string version,
            out string buildId,
            out DateTimeOffset? buildDate)
        {
            IEnumerable<string> lines = contents.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            fhirVersion = string.Empty;
            version = string.Empty;
            buildId = string.Empty;
            buildDate = null;

            foreach (string line in lines)
            {
                if (!line.Contains('='))
                {
                    continue;
                }

                string[] kvp = line.Split('=');

                if (kvp.Length != 2)
                {
                    continue;
                }

                switch (kvp[0])
                {
                    case "FhirVersion":
                        fhirVersion = kvp[1];
                        break;

                    case "version":
                        version = kvp[1];
                        break;

                    case "buildId":
                        buildId = kvp[1];
                        break;

                    case "date":
                        {
                            if (DateTimeOffset.TryParseExact(
                                kvp[1],
                                _manifestDateFormat,
                                CultureInfo.InvariantCulture.DateTimeFormat,
                                DateTimeStyles.None, out DateTimeOffset dto))
                            {
                                buildDate = dto;
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Builds the version string for a FhirCiQaRecord.
        /// </summary>
        /// <param name="qa">The FhirCiQaRecord.</param>
        /// <returns>The version string.</returns>
        private static string buildVersionString(FhirCiQaRecord qa)
        {
            string versionPrerelease = qa.PackageVersion?.Contains('-') ?? false
                ? string.Empty
                : "-cibuild";

            // prefer the date of the build as the build metadata
            string? buildMeta = qa.BuildDateIso?.ToUniversalTime().ToString(_ciVersionDateFormat)
                ?? qa.BuildDate?.ToUniversalTime().ToString(_ciVersionDateFormat);

            // if we do not have a date, mangle the branch info
            if (buildMeta is null)
            {
                (string? branchName, bool isDefaultBranch) = GetBranchNameRepoLiteral(qa.RepositoryUrl);

                versionPrerelease = isDefaultBranch || string.IsNullOrEmpty(branchName)
                    ? versionPrerelease
                    : (versionPrerelease + _ciBranchDelimiter + cleanForSemVer(branchName!));

                // add the repo portions to the version
                string[] repoComponents = qa.RepositoryUrl?.Split('/') ?? Array.Empty<string>();
                if (repoComponents.Length > 2)
                {
                    buildMeta = repoComponents[0] + "." + repoComponents[1];
                }
                else
                {
                    buildMeta = "ci";
                }
            }

            string packageVersion = qa.PackageVersion ?? "0.0.0";

            return $"{packageVersion}{versionPrerelease}+{cleanForSemVer(buildMeta)}";
        }

        /// <summary>
        /// Builds the dist-tag map (current / current$branch -&gt; version literal) for a set of QA records.
        /// </summary>
        /// <param name="qaRecs">The QA records for a single package id.</param>
        /// <returns>The tag to version-literal map.</returns>
        private static Dictionary<string, string> buildDistTags(List<FhirCiQaRecord> qaRecs)
        {
            Dictionary<string, string> distTags = new();

            foreach (FhirCiQaRecord qa in qaRecs.OrderBy(qa => qa.BuildDateIso ?? qa.BuildDate))
            {
                (string? branchName, bool isDefaultBranch) = GetBranchNameRepoLiteral(qa.RepositoryUrl);

                string tag = branchName is null
                    ? "current"
                    : "current$" + branchName;

                string versionLiteral = buildVersionString(qa);

                // add the full tag if we have it
                if (!distTags.ContainsKey(tag))
                {
                    distTags.Add(tag, versionLiteral);
                }

                // check for default branches to add them as well
                if (isDefaultBranch &&
                    (!distTags.ContainsKey("current")))
                {
                    distTags.Add("current", versionLiteral);
                }
            }

            return distTags;
        }

        /// <summary>
        /// Extracts the branch name from a repository URL.
        /// </summary>
        /// <param name="partialRepoLiteral">The partial repository URL.</param>
        /// <returns>The branch name extracted from the repository URL, or null if the branch name cannot be determined.</returns>
        public static (string? branchName, bool isDefaultBranch) GetBranchNameRepoLiteral(string? partialRepoLiteral)
        {
            if ((partialRepoLiteral is null) ||
                string.IsNullOrEmpty(partialRepoLiteral))
            {
                return (null, false);
            }

            int branchStart = partialRepoLiteral.IndexOf("branches/") + 9;

            if (branchStart == -1)
            {
                branchStart = partialRepoLiteral.IndexOf("tree/") + 5;
            }

            if (branchStart == -1)
            {
                return (null, false);
            }

            int branchEnd = partialRepoLiteral.IndexOf('/', branchStart);

            string branchName = branchEnd == -1
                ? partialRepoLiteral.Substring(branchStart)
                : partialRepoLiteral.Substring(branchStart, branchEnd - branchStart);

            return (branchName, _defaultBranchNames.Contains(branchName));
        }

        /// <summary>Get a list of package catalogs, based on optional parameters.</summary>
        /// <param name="pkgname">    (Optional) Name of the package.</param>
        /// <param name="fhirversion">(Optional) The FHIR version of a package.</param>
        /// <param name="site">       (Optional) URL of the site.</param>
        /// <param name="repo">       (Optional) The repository.</param>
        /// <param name="branch">     (Optional) The branch.</param>
        /// <returns>A list of package catalogs that conform to the parameters.</returns>
        public async ValueTask<List<CiPackageCatalogEntry>> CatalogPackagesAsync(
            string? pkgname = null,
            string? fhirversion = null,
            string? site = null,
            string? repo = null,
            string? branch = null)
        {
            List<CiPackageCatalogEntry> entries = new();

            (FhirCiQaRecord[] qas, Dictionary<string, List<FhirCiQaRecord>> qasByPackageId) = await getQAs();

            HashSet<string> usedIds = new();

            // remove any trailing slashes from the site URL - QAs.json does not have them
            if ((site is not null) && site.EndsWith("/"))
            {
                site = site.Substring(0, site.Length - 1);
            }

            // sanitize any repository URL - QAs.json does not repeat the GitHub URL portion
            if ((repo is not null) && repo.StartsWith("http://github.com/"))
            {
                repo = repo.Substring(18);
            }
            else if ((repo is not null) && repo.StartsWith("https://github.com/"))
            {
                repo = repo.Substring(19);
            }

            if ((branch is not null) && (!branch.Contains('/')))
            {
                branch = "/branches/" + branch + "/qa.json";
            }

            // if there was a package name provided, we can use our dictionary for lookup
            IEnumerable<FhirCiQaRecord> candidates;

            if (pkgname is not null)
            {
                if (!qasByPackageId.TryGetValue(pkgname, out List<FhirCiQaRecord>? qasRecs))
                {
                    return entries;
                }

                candidates = qasRecs;
            }
            else
            {
                candidates = qas;
            }

            // iterate over the QA records
            foreach (FhirCiQaRecord qa in candidates)
            {
                // skip anything we have already added - duplicates are likely forks but we lose that granularity here
                if (usedIds.Contains(qa.PackageId ?? string.Empty))
                {
                    continue;
                }

                if ((fhirversion is not null) && (qa.FhirVersion != fhirversion))
                {
                    continue;
                }

                if ((site is not null) && (qa.Url != site))
                {
                    continue;
                }

                if ((repo is not null) && (!qa.RepositoryUrl?.StartsWith(repo) ?? false))
                {
                    continue;
                }

                if ((branch is not null) && (!qa.RepositoryUrl?.EndsWith(branch) ?? false))
                {
                    continue;
                }

                // only use this package if it passes all the other filters
                usedIds.Add(qa.PackageId ?? string.Empty);
                entries.Add(new CiPackageCatalogEntry(qa.PackageId, qa.RepositoryUrl, qa.FhirVersion));
            }

            return entries;
        }

        /// <summary>
        /// Cleans the input string to be usable in a semantic versioning component.
        /// </summary>
        /// <param name="value">The input string to be cleaned.</param>
        /// <returns>The cleaned string for semantic versioning.</returns>
        private static string cleanForSemVer(string value)
        {
            List<char> clean = new(value.Length);

            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    clean.Add(c);
                    continue;
                }

                clean.Add('-');
            }

            return new string(clean.ToArray());
        }

        /// <summary>
        /// Retrieves the FhirCiQaRecord for the specified package name and version or tag.
        /// </summary>
        /// <param name="name">The name of the package.</param>
        /// <param name="versionDiscriminator">The version, tag, or branch of the package.</param>
        /// <returns>The FhirCiQaRecord for the specified package name and version or tag, or null if not found.</returns>
        private async ValueTask<FhirCiQaRecord?> getQaRecord(string? name, string? versionDiscriminator)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            (FhirCiQaRecord[] _, Dictionary<string, List<FhirCiQaRecord>> qasByPackageId) = await getQAs();

            if (!qasByPackageId.TryGetValue(name!, out List<FhirCiQaRecord>? qaRecs))
            {
                return null;
            }

            // no version resolves to the 'current' tag by default
            string requestedVersion = versionDiscriminator switch
            {
                null => "current",
                _ => versionDiscriminator,
            };

            // check for using a tag or branch name and resolve it to a version
            if (!requestedVersion.Contains('+'))
            {
                Dictionary<string, string> distTags = buildDistTags(qaRecs);

                if (distTags.TryGetValue(requestedVersion, out string? tagVersion) ||
                    distTags.TryGetValue("current$" + requestedVersion, out tagVersion))
                {
                    requestedVersion = tagVersion;
                }
            }

            string[] rvComponents = requestedVersion.Split('+');

            if ((rvComponents.Length > 1) &&
                DateTimeOffset.TryParseExact(
                    rvComponents.Last(),
                    _ciVersionDateFormat,
                    CultureInfo.InvariantCulture.DateTimeFormat,
                    DateTimeStyles.None, out DateTimeOffset requestedDto))
            {
                // traverse the records in the package looking for a match
                foreach (FhirCiQaRecord qa in qaRecs)
                {
                    if ((qa.BuildDateIso == requestedDto) ||
                        (qa.BuildDate == requestedDto))
                    {
                        return qa;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Downloads a package tarball from the build server.
        /// </summary>
        /// <param name="qa">The QA record of the build to download.</param>
        /// <returns>Package content as a byte array.</returns>
        private async ValueTask<byte[]> downloadPackage(FhirCiQaRecord qa)
        {
            // build the URL
            int igUrlIndex = qa.RepositoryUrl?.IndexOf("/qa.json", StringComparison.Ordinal) ?? -1;
            string url = igUrlIndex == -1 ? qa.RepositoryUrl! : qa.RepositoryUrl!.Substring(0, igUrlIndex);

            url += url.EndsWith('/')
                ? "package.tgz"
                : "/package.tgz";

            if (!url.StartsWith("http"))
            {
                if (url.StartsWith("HL7/fhir/", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://build.fhir.org/" + url.Substring(9);
                }
                else
                {
                    url = "https://build.fhir.org/ig/" + url;
                }
            }

            // download data
            return await _httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
        }

        /// <summary>
        /// Determines the tag (current / current$branch) a QA record installs under.
        /// </summary>
        /// <param name="qa">                  The QA record.</param>
        /// <param name="versionDiscriminator">The version, tag, or branch that was requested.</param>
        /// <returns>The install tag.</returns>
        private static string getTagForQaRecord(FhirCiQaRecord qa, string? versionDiscriminator)
        {
            (string? branchName, bool isDefaultBranch) = GetBranchNameRepoLiteral(qa.RepositoryUrl);

            if ((branchName is null) ||
                (isDefaultBranch && (!versionDiscriminator?.Contains(branchName) ?? true)))
            {
                return "current";
            }

            return "current$" + branchName;
        }

        /// <summary>
        /// Installs or updates a package from the FHIR CI server into the expanded package cache.
        /// </summary>
        /// <exception cref="Exception">Thrown when the package cannot be resolved on the CI server.</exception>
        /// <param name="name">                 The package name.</param>
        /// <param name="versionDiscriminator"> The version, tag, or branch of the package (e.g., current, current$branch).</param>
        /// <param name="packagesRootDirectory">The root directory containing expanded packages.</param>
        /// <returns>The installed package.</returns>
        public async Task<InstalledPackage> InstallOrUpdateAsync(string name, string? versionDiscriminator, string packagesRootDirectory)
        {
            // find our package in the QA listings
            FhirCiQaRecord? qa = await getQaRecord(name, versionDiscriminator)
                ?? throw new Exception($"Could not resolve {name}#{versionDiscriminator ?? "current"} on the CI server");

            string packageId = qa.PackageId ?? name;
            string tag = getTagForQaRecord(qa, versionDiscriminator);

            string packageRoot = Path.Combine(packagesRootDirectory, $"{packageId}#{tag}");
            string contentDirectory = Path.Combine(packageRoot, "package");

            // compare the build date from QA and the installed manifest
            DateTimeOffset installedBuildDate = getInstalledBuildDate(contentDirectory);
            bool shouldDownload = installedBuildDate < (qa.BuildDateIso ?? qa.BuildDate ?? DateTimeOffset.MaxValue);

            if (shouldDownload)
            {
                if (Directory.Exists(packageRoot))
                {
                    // need to delete the existing content
                    Directory.Delete(packageRoot, true);
                }

                byte[] data = await downloadPackage(qa);

                using MemoryStream ms = new(data);
                _ = FhirPackageService.ExtractPackageTarball(ms, packageRoot);
            }

            return new InstalledPackage(packageId, tag, $"{packageId}#{tag}", contentDirectory);
        }

        /// <summary>Reads the build date of an installed package manifest, MinValue when unavailable.</summary>
        private static DateTimeOffset getInstalledBuildDate(string contentDirectory)
        {
            if (!Directory.Exists(contentDirectory))
            {
                return DateTimeOffset.MinValue;
            }

            try
            {
                FhirNpmPackageDetails manifest = FhirNpmPackageDetails.Load(contentDirectory);

                if (DateTimeOffset.TryParseExact(
                        manifest.BuildDate,
                        _manifestDateFormat,
                        CultureInfo.InvariantCulture.DateTimeFormat,
                        DateTimeStyles.None,
                        out DateTimeOffset buildDate))
                {
                    return buildDate;
                }
            }
            catch (Exception)
            {
                // treat unreadable manifests as never-installed so the package is refreshed
            }

            return DateTimeOffset.MinValue;
        }

        #region IDisposable

        private bool _disposed;

        /// <inheritdoc/>
        void IDisposable.Dispose() => Dispose(true);

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="FhirCiClient"/> and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _httpClient?.Dispose();
                }

                _disposed = true;
            }
        }

        #endregion
    }
}

#nullable restore
