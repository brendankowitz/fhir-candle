using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using FhirCandle.Models;
using FhirCandle.Schema;
using FhirCandle.Search;
using FhirCandle.Serialization;
using FhirCandle.Strict;
using FhirCandle.Utils;
using Ignixa.Abstractions;
using Ignixa.Search.Models;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using SearchParameterHandling = FhirCandle.Client.CandleClientSettings.SearchParameterHandling;

namespace FhirCandle.Storage;

/// <summary>
/// A FHIR store spanning every resource type for a single tenant/FHIR-version combination.
/// </summary>
/// <remarks>
/// This is the Ignixa-model port of the core CRUD/search/dispatch surface of the old (Firely-based)
/// <c>VersionedFhirStore</c>. Bundle processing (<see cref="ProcessBundle"/>) and CapabilityStatement
/// generation (<see cref="GetMetadata"/>) are ported in later tasks (9 and 10 respectively) and are
/// stubbed here with a <see cref="HttpStatusCode.NotImplemented"/> response. Subscription execution,
/// compartment membership, and terminology (ValueSet) services do not exist yet anywhere in the new
/// engine and are deferred to future tasks - see the per-member remarks below for exact extension
/// points.
/// </remarks>
public sealed class VersionedFhirStore : IFhirStore
{
    /// <summary>FHIR id datatype regex per spec (R4/R4B/R5 datatypes.html#id): <c>[A-Za-z0-9\-\.]{1,64}</c>.
    /// Used by strict-mode pre-checks on POST/PUT to reject ill-formed resource ids.</summary>
    private static readonly Regex _fhirIdRegex = new("^[A-Za-z0-9\\-\\.]{1,64}$", RegexOptions.Compiled);

    private readonly Dictionary<string, ResourceStore> _store = [];
    private readonly HashSet<string> _protectedResources = [];
    private readonly HashSet<string> _loadedDirectives = [];
    private readonly HashSet<string> _loadedPackageIds = [];
    private readonly HashSet<string> _loadedSupplements = [];
    private readonly ConcurrentQueue<string> _resourceQ = [];

    /// <summary>
    /// Always-false ValueSet membership stub threaded through every <see cref="ResourceStore"/>'s
    /// <c>:in</c>/<c>:not-in</c> search modifier support. Task 12 will replace this field (and add a
    /// real <c>Terminology</c> property backed by <c>StoreTerminologyService</c>) once that service
    /// exists; until then, ValueSet-membership search modifiers always report "no match".
    /// </summary>
    private readonly Func<string?, string?, string?, bool> _vsContains = (_, _, _) => false;

    private TenantConfiguration _config = null!;
    private IFhirSchemaProvider _schema = null!;
    private CandleSearchService _search = null!;
    private int _maxResourceCount;
    private bool _hasDisposed;

    /// <inheritdoc/>
    public event EventHandler<StoreInstanceEventArgs>? OnInstanceCreated;

    /// <inheritdoc/>
    public event EventHandler<StoreInstanceEventArgs>? OnInstanceUpdated;

    /// <inheritdoc/>
    public event EventHandler<StoreInstanceEventArgs>? OnInstanceDeleted;

    /// <summary>Declared to satisfy <see cref="IFhirStore"/>; subscription execution is not ported yet
    /// (no topic/subscription converter exists in the new engine), so this is never raised.</summary>
    public event EventHandler<SubscriptionChangedEventArgs>? OnSubscriptionsChanged;

    /// <summary>Declared to satisfy <see cref="IFhirStore"/>; never raised (see <see cref="OnSubscriptionsChanged"/>).</summary>
    public event EventHandler<SubscriptionSendEventArgs>? OnSubscriptionSendEvent;

    /// <summary>Declared to satisfy <see cref="IFhirStore"/>; never raised (see <see cref="OnSubscriptionsChanged"/>).</summary>
    public event EventHandler<ReceivedSubscriptionChangedEventArgs>? OnReceivedSubscriptionChanged;

    /// <summary>Declared to satisfy <see cref="IFhirStore"/>; never raised (see <see cref="OnSubscriptionsChanged"/>).</summary>
    public event EventHandler<ReceivedSubscriptionEventArgs>? OnReceivedSubscriptionEvent;

    /// <summary>Gets the FHIR schema provider for this store's FHIR version.</summary>
    public IFhirSchemaProvider Schema => _schema;

    /// <summary>Gets the search service shared by every per-resource-type store.</summary>
    public CandleSearchService Search => _search;

    /// <inheritdoc/>
    public TenantConfiguration Config => _config;

    /// <inheritdoc/>
    public HashSet<string> LoadedPackageDirectives => _loadedDirectives;

    /// <inheritdoc/>
    public HashSet<string> LoadedPackageIds => _loadedPackageIds;

    /// <inheritdoc/>
    public HashSet<string> LoadedSupplements => _loadedSupplements;

    /// <inheritdoc/>
    public IEnumerable<string> SupportedResources => _store.Keys;

    /// <summary>Gets the per-resource-type store for <paramref name="resourceType"/>, or null if the
    /// resource type is not supported by this FHIR version.</summary>
    internal ResourceStore? GetStore(string resourceType) =>
        _store.TryGetValue(resourceType, out ResourceStore? rs) ? rs : null;

    /// <inheritdoc/>
    public void Init(TenantConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrEmpty(config.ControllerName))
        {
            throw new ArgumentNullException(nameof(config.ControllerName));
        }

        if (string.IsNullOrEmpty(config.BaseUrl))
        {
            throw new ArgumentNullException(nameof(config.BaseUrl));
        }

        // Compose --strict policy into the per-feature flags BEFORE assignment so every
        // store-creation path (CLI-launched, programmatic, test) gets the same composition.
        config.ResolveStrict();

        _config = config;
        _schema = FhirSchemas.Get(config.FhirVersion);
        _search = new CandleSearchService(_schema, NullLoggerFactory.Instance);

        foreach (string resourceType in _schema.ResourceTypeNames)
        {
            switch (resourceType)
            {
                case "Parameters":
                case "OperationOutcome":
                case "SubscriptionStatus":
                    continue;
            }

            var rs = new ResourceStore(resourceType, _schema, _search, _vsContains);

            rs.OnInstanceCreated += (_, e) => RegisterInstanceCreated(e.ResourceType, e.ResourceId);
            rs.OnInstanceUpdated += (_, e) => RegisterInstanceUpdated(e.ResourceType, e.ResourceId);
            rs.OnInstanceDeleted += (_, e) => RegisterInstanceDeleted(e.ResourceType, e.ResourceId);

            // Deferred: no compartment engine (Task 11) or terminology service (Task 12) exists yet
            // to register these against. Subscribing here (rather than leaving the events unobserved)
            // documents the exact extension point those tasks should use.
            rs.OnCompartmentDefinitionChanged += (_, _) => { };
            rs.OnCompartmentDefinitionRemoved += (_, _) => { };
            rs.OnValueSetChanged += (_, _) => { };
            rs.OnValueSetRemoved += (_, _) => { };

            _store.Add(resourceType, rs);
        }

        if (config.LoadDirectory is not null)
        {
            foreach (FileInfo file in config.LoadDirectory.GetFiles("*.*", SearchOption.AllDirectories))
            {
                bool loaded = TryLoadAndStoreResourceFile(file);
                Console.WriteLine(loaded
                    ? $"{config.ControllerName} <<<      loaded: {file.FullName}"
                    : $"{config.ControllerName} <<< load FAILED: {file.FullName}");
            }
        }

        _maxResourceCount = config.MaxResourceCount;
    }

    /// <inheritdoc/>
    public void LoadPackage(
        string directive,
        string directory,
        string packageSupplements,
        bool includeExamples)
    {
        // `includeExamples` is not honored in this port - every resource file under the package
        // directory is loaded regardless of lib/example placement. Simplification vs. the old file's
        // package.json "lib" directory probing; revisit if a package-loading task needs that nuance.
        if (!string.IsNullOrEmpty(directive) && !string.IsNullOrEmpty(directory))
        {
            _loadedDirectives.Add(directive);
            _loadedPackageIds.Add(directive.Split('#', '@')[0]);

            Console.WriteLine($"Store[{_config.ControllerName}] loading {directive}");

            DirectoryInfo di = new(directory);
            IEnumerable<FileInfo> files = di.GetFiles("*.*", SearchOption.AllDirectories)
                .Where(f => !f.Name.Equals(".index.json", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Name.EndsWith(".openapi.json", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Name.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase));

            foreach (FileInfo file in files)
            {
                bool loaded = TryLoadAndStoreResourceFile(file);
                Console.WriteLine(loaded
                    ? $"{_config.ControllerName}:{directive} <<<      loaded: {file.FullName}"
                    : $"{_config.ControllerName}:{directive} <<< load FAILED: {file.FullName}");
            }
        }

        if (!string.IsNullOrEmpty(packageSupplements) &&
            Directory.Exists(packageSupplements) &&
            _loadedSupplements.Add(packageSupplements))
        {
            Console.WriteLine($"Store[{_config.ControllerName}] loading contents from {packageSupplements}");

            DirectoryInfo di = new(packageSupplements);
            foreach (FileInfo file in di.GetFiles("*.*", SearchOption.AllDirectories))
            {
                TryLoadAndStoreResourceFile(file);
            }
        }
    }

    /// <summary>Parses a single resource file and stores it (update-as-create), honoring
    /// <see cref="TenantConfiguration.ProtectLoadedContent"/>. Used by both <see cref="Init"/>'s
    /// load-directory processing and <see cref="LoadPackage"/>.</summary>
    private bool TryLoadAndStoreResourceFile(FileInfo file)
    {
        string format = file.Extension.ToLowerInvariant() switch
        {
            ".json" => "application/fhir+json",
            ".xml" => "application/fhir+xml",
            _ => string.Empty,
        };

        if (string.IsNullOrEmpty(format))
        {
            return false;
        }

        HttpStatusCode sc = SerializationUtils.TryDeserializeFhir(
            File.ReadAllText(file.FullName), format, out ResourceJsonNode? resource, out _, _schema);

        if (sc != HttpStatusCode.OK ||
            resource is null ||
            !_store.TryGetValue(resource.ResourceType, out ResourceStore? rs))
        {
            return false;
        }

        if (string.IsNullOrEmpty(resource.Id))
        {
            resource.Id = Guid.NewGuid().ToString();
        }

        ResourceJsonNode? stored = rs.InstanceUpdate(
            resource,
            allowCreate: true,
            ifMatch: string.Empty,
            ifNoneMatch: string.Empty,
            protectedResources: _protectedResources,
            out _,
            out _);

        if (stored is null)
        {
            return false;
        }

        if (_config.ProtectLoadedContent)
        {
            _protectedResources.Add($"{resource.ResourceType}/{resource.Id}");
        }

        return true;
    }

    /// <summary>Enforces <see cref="TenantConfiguration.MaxResourceCount"/> by evicting the oldest
    /// created resources. Simplified vs. the old file's 30-second background timer: eviction runs
    /// synchronously right after each create, which is functionally equivalent (bounds storage) but
    /// without the periodic-timer machinery.</summary>
    private void EnforceCapacity(string resourceType, string id)
    {
        _resourceQ.Enqueue($"{resourceType}/{id}");

        while (_resourceQ.Count > _maxResourceCount && _resourceQ.TryDequeue(out string? evictId))
        {
            string[] parts = evictId.Split('/');
            if (parts.Length == 2 && _store.TryGetValue(parts[0], out ResourceStore? rs))
            {
                rs.InstanceDelete(parts[1], _protectedResources);
            }
        }
    }

    /// <inheritdoc/>
    public void RegisterInstanceCreated(string resourceType, string resourceId) =>
        OnInstanceCreated?.Invoke(this, new StoreInstanceEventArgs { ResourceType = resourceType, ResourceId = resourceId });

    /// <inheritdoc/>
    public void RegisterInstanceUpdated(string resourceType, string resourceId) =>
        OnInstanceUpdated?.Invoke(this, new StoreInstanceEventArgs { ResourceType = resourceType, ResourceId = resourceId });

    /// <inheritdoc/>
    public void RegisterInstanceDeleted(string resourceType, string resourceId) =>
        OnInstanceDeleted?.Invoke(this, new StoreInstanceEventArgs { ResourceType = resourceType, ResourceId = resourceId });

    /// <inheritdoc/>
    public bool PerformInteraction(
        FhirRequestContext ctx,
        out FhirResponseContext response,
        bool serializeReturn = true,
        bool forceAllowExistingId = false)
    {
        // `serializeReturn` is currently always honored (every path below always serializes) - there
        // is no unserialized/transaction-internal caller yet. Task 9 (Bundle processing) may need to
        // extend this once it wants to avoid double-serializing entries inside a transaction.
        switch (ctx.Interaction)
        {
            case Common.StoreInteractionCodes.InstanceDelete:
                return InstanceDelete(ctx, out response);

            case Common.StoreInteractionCodes.InstanceRead:
                return InstanceRead(ctx, out response);

            case Common.StoreInteractionCodes.InstanceUpdate:
            case Common.StoreInteractionCodes.InstanceUpdateConditional:
                return InstanceUpdate(ctx, out response);

            case Common.StoreInteractionCodes.TypeCreate:
            case Common.StoreInteractionCodes.TypeCreateConditional:
                return InstanceCreate(ctx, out response, forceAllowExistingId);

            case Common.StoreInteractionCodes.TypeSearch:
                return TypeSearch(ctx, out response);

            case Common.StoreInteractionCodes.SystemCapabilities:
                return GetMetadata(ctx, out response);

            case Common.StoreInteractionCodes.SystemBundle:
                return ProcessBundle(ctx, out response);

            default:
                response = NotImplementedResponse($"Interaction not implemented: {ctx.Interaction}");
                return false;
        }
    }

    /// <inheritdoc/>
    public bool InstanceCreate(
        FhirRequestContext ctx,
        out FhirResponseContext response,
        bool forceAllowExistingId = false)
    {
        if (!TryDeserializeSource(ctx, out ResourceJsonNode? content, out response))
        {
            return false;
        }

        bool success = DoInstanceCreate(ctx, content!, out response, forceAllowExistingId);
        response = SerializeResponse(ctx, response);
        return success;
    }

    /// <summary>Executes create, applying strict/id rules and conditional-create (If-None-Exist)
    /// resolution, without serializing the response.</summary>
    private bool DoInstanceCreate(
        FhirRequestContext ctx,
        ResourceJsonNode content,
        out FhirResponseContext response,
        bool forceExistingId = false)
    {
        string resourceType = string.IsNullOrEmpty(ctx.ResourceType) ? content.ResourceType : ctx.ResourceType;

        if (content.ResourceType != resourceType)
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(
                    HttpStatusCode.UnprocessableEntity,
                    $"Resource type: {content.ResourceType} does not match request: {resourceType}",
                    OperationOutcomeJsonNode.IssueType.Invalid),
                StatusCode = HttpStatusCode.UnprocessableEntity,
            };
            return false;
        }

        if (!_store.TryGetValue(resourceType, out ResourceStore? rs))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(
                    HttpStatusCode.NotFound,
                    $"Resource type: {resourceType} is not supported",
                    OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        string conditionalQuery = !string.IsNullOrEmpty(ctx.IfNoneExist) ? ctx.IfNoneExist : ctx.UrlQuery;
        bool isConditionalCreate =
            ((ctx.Interaction == Common.StoreInteractionCodes.TypeCreateConditional) || !string.IsNullOrEmpty(ctx.IfNoneExist)) &&
            FhirCandle.Search.Common.QueryContainsSearchParameters(conditionalQuery);

        if (isConditionalCreate)
        {
            List<ResourceJsonNode> matches = rs.TypeSearch(_search.ParseQuery(resourceType, conditionalQuery)).ToList();

            switch (matches.Count)
            {
                case 0:
                    break;

                case 1:
                    {
                        ResourceJsonNode r = matches[0];
                        response = new()
                        {
                            Resource = r,
                            ResourceType = r.ResourceType,
                            Id = r.Id,
                            ETag = string.IsNullOrEmpty(r.Meta.VersionId) ? string.Empty : $"W/\"{r.Meta.VersionId}\"",
                            LastModified = r.Meta.LastUpdated is null ? string.Empty : r.Meta.LastUpdated.Value.UtcDateTime.ToString("r"),
                            Location = $"{GetBaseUrl(ctx)}/{resourceType}/{r.Id}",
                            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, $"Created {resourceType}/{r.Id}"),
                            StatusCode = HttpStatusCode.OK,
                        };
                        return true;
                    }

                default:
                    response = new()
                    {
                        Outcome = SerializationUtils.BuildOutcomeForRequest(
                            HttpStatusCode.PreconditionFailed,
                            $"If-None-Exist query returned too many matches: {matches.Count}"),
                        StatusCode = HttpStatusCode.PreconditionFailed,
                    };
                    return false;
            }
        }

        // FHIR REST §2.42: on POST (create), the server SHALL ignore the client-supplied Resource.id
        // and assign a server-side id. Gated on HTTP method, not the dispatcher interaction enum, so
        // non-POST callers (load-from-disk, package self-registration) can keep AllowExistingId.
        bool isPostCreate = string.Equals(ctx.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);

        if (_config.Strict &&
            isPostCreate &&
            !forceExistingId &&
            !string.IsNullOrEmpty(content.Id))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForStrictRule(
                    HttpStatusCode.BadRequest,
                    "POST/create must not include Resource.id; the server assigns ids on create.",
                    StrictRuleCode.PostClientSuppliedId,
                    _config.FhirVersion,
                    OperationOutcomeJsonNode.IssueType.Invalid),
                StatusCode = HttpStatusCode.BadRequest,
            };
            return false;
        }

        bool allowExistingIdForThisCall = forceExistingId || (!isPostCreate && _config.AllowExistingId);

        ResourceJsonNode? stored = rs.InstanceCreate(
            ctx, content, allowExistingIdForThisCall, out HttpStatusCode createStatusCode, out OperationOutcomeJsonNode createOutcome);

        if (stored is null)
        {
            response = new() { Outcome = createOutcome, StatusCode = createStatusCode };
            return false;
        }

        if (_maxResourceCount > 0)
        {
            EnforceCapacity(resourceType, stored.Id);
        }

        response = new()
        {
            Resource = stored,
            ResourceType = stored.ResourceType,
            Id = stored.Id,
            ETag = string.IsNullOrEmpty(stored.Meta.VersionId) ? string.Empty : $"W/\"{stored.Meta.VersionId}\"",
            LastModified = stored.Meta.LastUpdated is null ? string.Empty : stored.Meta.LastUpdated.Value.UtcDateTime.ToString("r"),
            Location = $"{GetBaseUrl(ctx)}/{resourceType}/{stored.Id}",
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.Created, $"Created {resourceType}/{stored.Id}"),
            StatusCode = HttpStatusCode.Created,
        };
        return true;
    }

    /// <inheritdoc/>
    public bool InstanceRead(
        FhirRequestContext ctx,
        out FhirResponseContext response)
    {
        bool success = DoInstanceRead(ctx, out response);
        response = SerializeResponse(ctx, response);
        return success;
    }

    private bool DoInstanceRead(FhirRequestContext ctx, out FhirResponseContext response)
    {
        if (string.IsNullOrEmpty(ctx.ResourceType))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.BadRequest, "Resource type is required", OperationOutcomeJsonNode.IssueType.Structure),
                StatusCode = HttpStatusCode.BadRequest,
            };
            return false;
        }

        if (!_store.TryGetValue(ctx.ResourceType, out ResourceStore? rs))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Resource type: {ctx.ResourceType} is not supported", OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        if (string.IsNullOrEmpty(ctx.Id))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.BadRequest, "ID required for instance level read.", OperationOutcomeJsonNode.IssueType.Structure),
                StatusCode = HttpStatusCode.BadRequest,
            };
            return false;
        }

        ResourceJsonNode? r = rs.InstanceRead(ctx.Id);

        if (r is null)
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Resource: {ctx.ResourceType}/{ctx.Id} not found", OperationOutcomeJsonNode.IssueType.Exception),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        string eTag = string.IsNullOrEmpty(r.Meta.VersionId) ? string.Empty : $"W/\"{r.Meta.VersionId}\"";

        if (!string.IsNullOrEmpty(ctx.IfMatch) && !eTag.Equals(ctx.IfMatch, StringComparison.Ordinal))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.PreconditionFailed, $"If-Match: {ctx.IfMatch} does not equal found eTag: {eTag}", OperationOutcomeJsonNode.IssueType.BusinessRule),
                StatusCode = HttpStatusCode.PreconditionFailed,
            };
            return false;
        }

        string lastModified = r.Meta.LastUpdated is null ? string.Empty : r.Meta.LastUpdated.Value.UtcDateTime.ToString("r");

        if (!string.IsNullOrEmpty(ctx.IfModifiedSince) && string.Compare(lastModified, ctx.IfModifiedSince, StringComparison.Ordinal) < 0)
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotModified, $"Last modified: {lastModified} is prior to If-Modified-Since: {ctx.IfModifiedSince}", OperationOutcomeJsonNode.IssueType.Informational),
                ETag = eTag,
                LastModified = lastModified,
                StatusCode = HttpStatusCode.NotModified,
            };
            return true;
        }

        if (ctx.IfNoneMatch.Equals("*", StringComparison.Ordinal))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.PreconditionFailed, "Prior version exists, but If-None-Match is *"),
                StatusCode = HttpStatusCode.PreconditionFailed,
            };
            return false;
        }

        if (!string.IsNullOrEmpty(ctx.IfNoneMatch) &&
            _config.SupportNotChanged &&
            ctx.IfNoneMatch.Equals(eTag, StringComparison.Ordinal))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotModified, $"Read {ctx.ResourceType}/{ctx.Id} found version: {eTag}, equals If-None-Match: {ctx.IfNoneMatch}"),
                StatusCode = HttpStatusCode.NotModified,
            };
            return false;
        }

        response = new()
        {
            Resource = r,
            ResourceType = r.ResourceType,
            Id = r.Id,
            ETag = eTag,
            LastModified = lastModified,
            Location = string.IsNullOrEmpty(r.Id) ? string.Empty : $"{GetBaseUrl(ctx)}/{r.ResourceType}/{r.Id}",
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, $"Read {r.ResourceType}/{r.Id}"),
            StatusCode = HttpStatusCode.OK,
        };
        return true;
    }

    /// <inheritdoc/>
    public bool InstanceUpdate(
        FhirRequestContext ctx,
        out FhirResponseContext response)
    {
        if (!TryDeserializeSource(ctx, out ResourceJsonNode? content, out response))
        {
            return false;
        }

        bool success = DoInstanceUpdate(ctx, content!, out response);
        response = SerializeResponse(ctx, response);
        return success;
    }

    private bool DoInstanceUpdate(FhirRequestContext ctx, ResourceJsonNode content, out FhirResponseContext response)
    {
        string resourceType = string.IsNullOrEmpty(ctx.ResourceType) ? content.ResourceType : ctx.ResourceType;
        string id = ctx.Id;

        if (content.ResourceType != resourceType)
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.UnprocessableEntity, $"Resource type: {content.ResourceType} does not match request: {resourceType}", OperationOutcomeJsonNode.IssueType.Invalid),
                StatusCode = HttpStatusCode.UnprocessableEntity,
            };
            return false;
        }

        if (!_store.TryGetValue(resourceType, out ResourceStore? rs))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Resource type: {resourceType} is not supported", OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        // FHIR REST: when both URL id and body id are present, they must agree. 422 - well-formed FHIR,
        // semantically inconsistent (spec permits either 400 or 422). Always-on, strict-independent.
        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(content.Id) && !id.Equals(content.Id, StringComparison.Ordinal))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForStrictRule(HttpStatusCode.UnprocessableEntity, $"URL id '{id}' does not match resource id '{content.Id}'", StrictRuleCode.PutBodyIdMismatch, _config.FhirVersion, OperationOutcomeJsonNode.IssueType.Invalid),
                StatusCode = HttpStatusCode.UnprocessableEntity,
            };
            return false;
        }

        if (!string.IsNullOrEmpty(id) && string.IsNullOrEmpty(content.Id))
        {
            if (_config.Strict)
            {
                response = new()
                {
                    Outcome = SerializationUtils.BuildOutcomeForStrictRule(HttpStatusCode.UnprocessableEntity, $"Resource.id is required on PUT and must equal the URL id '{id}'.", StrictRuleCode.PutEmptyBodyId, _config.FhirVersion, OperationOutcomeJsonNode.IssueType.Required),
                    StatusCode = HttpStatusCode.UnprocessableEntity,
                };
                return false;
            }

            content.Id = id;
        }

        if (_config.Strict)
        {
            if (ctx.Interaction != Common.StoreInteractionCodes.InstanceUpdateConditional &&
                !string.IsNullOrEmpty(id) &&
                !_fhirIdRegex.IsMatch(id))
            {
                response = new()
                {
                    Outcome = SerializationUtils.BuildOutcomeForStrictRule(HttpStatusCode.BadRequest, $"URL id '{id}' does not match the FHIR id datatype regex [A-Za-z0-9\\-\\.]{{1,64}}.", StrictRuleCode.ResourceIdRegex, _config.FhirVersion, OperationOutcomeJsonNode.IssueType.Invalid),
                    StatusCode = HttpStatusCode.BadRequest,
                };
                return false;
            }

            if (!string.IsNullOrEmpty(content.Id) && !_fhirIdRegex.IsMatch(content.Id))
            {
                response = new()
                {
                    Outcome = SerializationUtils.BuildOutcomeForStrictRule(HttpStatusCode.BadRequest, $"Resource.id '{content.Id}' does not match the FHIR id datatype regex [A-Za-z0-9\\-\\.]{{1,64}}.", StrictRuleCode.ResourceIdRegex, _config.FhirVersion, OperationOutcomeJsonNode.IssueType.Invalid),
                    StatusCode = HttpStatusCode.BadRequest,
                };
                return false;
            }

            if (ctx.Interaction != Common.StoreInteractionCodes.InstanceUpdateConditional &&
                !string.IsNullOrEmpty(id) &&
                !rs.ContainsKey(id))
            {
                response = new()
                {
                    Outcome = SerializationUtils.BuildOutcomeForStrictRule(HttpStatusCode.NotFound, $"Resource {resourceType}/{id} does not exist; PUT cannot create resources under strict mode.", StrictRuleCode.PutCreateAsUpdateDisallowed, _config.FhirVersion, OperationOutcomeJsonNode.IssueType.NotFound),
                    StatusCode = HttpStatusCode.NotFound,
                };
                return false;
            }
        }

        if (ctx.Interaction == Common.StoreInteractionCodes.InstanceUpdateConditional)
        {
            if (!FhirCandle.Search.Common.QueryContainsSearchParameters(ctx.UrlQuery))
            {
                response = new()
                {
                    Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.BadRequest, "Conditional update requires search criteria", OperationOutcomeJsonNode.IssueType.Required),
                    StatusCode = HttpStatusCode.BadRequest,
                };
                return false;
            }

            List<ResourceJsonNode> matches = rs.TypeSearch(_search.ParseQuery(resourceType, ctx.UrlQuery)).ToList();

            switch (matches.Count)
            {
                case 0:
                    if (string.IsNullOrEmpty(content.Id))
                    {
                        content.Id = Guid.NewGuid().ToString();
                    }
                    break;

                case 1:
                    content.Id = matches[0].Id;
                    break;

                default:
                    response = new()
                    {
                        Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.PreconditionFailed, $"Conditional update query returned too many matches: {matches.Count}"),
                        StatusCode = HttpStatusCode.PreconditionFailed,
                    };
                    return false;
            }
        }

        ResourceJsonNode? stored = rs.InstanceUpdate(
            content, _config.AllowCreateAsUpdate, ctx.IfMatch, ctx.IfNoneMatch, _protectedResources, out HttpStatusCode sc, out OperationOutcomeJsonNode outcome);

        if (stored is null)
        {
            response = new() { Outcome = outcome, StatusCode = sc };
            return false;
        }

        response = new()
        {
            Resource = stored,
            ResourceType = stored.ResourceType,
            Id = stored.Id,
            ETag = string.IsNullOrEmpty(stored.Meta.VersionId) ? string.Empty : $"W/\"{stored.Meta.VersionId}\"",
            LastModified = stored.Meta.LastUpdated is null ? string.Empty : stored.Meta.LastUpdated.Value.UtcDateTime.ToString("r"),
            Location = $"{GetBaseUrl(ctx)}/{resourceType}/{stored.Id}",
            Outcome = outcome,
            StatusCode = sc,
        };
        return true;
    }

    /// <inheritdoc/>
    public bool InstanceDelete(
        FhirRequestContext ctx,
        out FhirResponseContext response)
    {
        bool success = DoInstanceDelete(ctx, out response);
        response = SerializeResponse(ctx, response);
        return success;
    }

    private bool DoInstanceDelete(FhirRequestContext ctx, out FhirResponseContext response)
    {
        if (!_store.TryGetValue(ctx.ResourceType, out ResourceStore? rs))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Resource type: {ctx.ResourceType} is not supported", OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        ResourceJsonNode? resource = rs.InstanceDelete(ctx.Id, _protectedResources);

        if (resource is null)
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Resource {ctx.ResourceType}/{ctx.Id} not found"),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        response = new()
        {
            Resource = resource,
            ResourceType = resource.ResourceType,
            Id = resource.Id,
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, $"Deleted {ctx.ResourceType}/{ctx.Id}"),
            StatusCode = HttpStatusCode.OK,
        };
        return true;
    }

    /// <inheritdoc/>
    public bool TypeSearch(
        FhirRequestContext ctx,
        out FhirResponseContext response)
    {
        bool success = DoTypeSearch(ctx, out response);
        response = SerializeResponse(ctx, response);
        return success;
    }

    private bool DoTypeSearch(FhirRequestContext ctx, out FhirResponseContext response)
    {
        string searchQueryParams = string.IsNullOrEmpty(ctx.SourceContent) || (ctx.SourceFormat != "application/x-www-form-urlencoded")
            ? ctx.UrlQuery
            : ctx.SourceContent;

        if (string.IsNullOrEmpty(ctx.ResourceType))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.BadRequest, "Resource type is required for type search interactions", OperationOutcomeJsonNode.IssueType.Structure),
                StatusCode = HttpStatusCode.BadRequest,
            };
            return false;
        }

        if (!_store.TryGetValue(ctx.ResourceType, out ResourceStore? rs))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Resource type: {ctx.ResourceType} is not supported", OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        ParsedQuery query = _search.ParseQuery(ctx.ResourceType, searchQueryParams);

        if (query.UnknownParameters.Count > 0 && EffectiveSearchHandling(ctx) == SearchParameterHandling.Strict)
        {
            var issues = query.UnknownParameters
                .Select(p => (StrictRuleCode.SearchUnknownParameter, $"Unknown search parameter '{p}' for this resource type.", OperationOutcomeJsonNode.IssueType.NotSupported))
                .ToList();

            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForStrictRules(HttpStatusCode.BadRequest, issues, _config.FhirVersion),
                StatusCode = HttpStatusCode.BadRequest,
            };
            return false;
        }

        List<ResourceJsonNode> matches = rs.TypeSearch(query).ToList();

        if (ctx.Authorization is not null)
        {
            matches = FilterSearchResultsForAuth(matches);
        }

        if (query.Options.Sort.Count > 0)
        {
            var comparer = new FhirSortComparer(_schema, query.Options.Sort, _search.Definitions);
            matches = matches.OrderBy(m => m, comparer).ToList();
        }

        string selfLink = $"{GetBaseUrl(ctx)}/{ctx.ResourceType}";
        if (!string.IsNullOrEmpty(searchQueryParams))
        {
            selfLink = selfLink + "?" + searchQueryParams.TrimStart('?');
        }

        var bundle = new BundleJsonNode
        {
            Id = Guid.NewGuid().ToString(),
            Type = BundleJsonNode.BundleType.Searchset,
            Total = matches.Count,
        };
        bundle.Link.Add(new BundleLinkJsonNode { Relation = "self", Url = selfLink });

        if (query.Options.Summary != SummaryType.Count)
        {
            var addedIds = new HashSet<string>();
            int maxItemCount = query.Options.MaxItemCount;
            int resultCount = 0;

            foreach (ResourceJsonNode resource in matches)
            {
                if (resultCount >= maxItemCount)
                {
                    break;
                }

                string relativeUrl = $"{resource.ResourceType}/{resource.Id}";
                if (!addedIds.Add(relativeUrl))
                {
                    continue;
                }

                resultCount++;
                bundle.Entry.Add(new BundleComponentJsonNode
                {
                    FullUrl = $"{GetBaseUrl(ctx)}/{relativeUrl}",
                    Resource = resource,
                    Search = new BundleComponentSearchJsonNode { Mode = "match" },
                });
            }

            if (query.Options.Include.Count > 0)
            {
                foreach (ResourceJsonNode included in SearchExecutor.ResolveIncludes(matches, query.Options.Include, GetStore, _search, _schema))
                {
                    AddIncludeEntry(bundle, ctx, included, addedIds);
                }
            }

            if (query.Options.RevInclude.Count > 0)
            {
                foreach (ResourceJsonNode included in SearchExecutor.ResolveRevIncludes(matches, query.Options.RevInclude, GetStore, _search, _schema))
                {
                    AddIncludeEntry(bundle, ctx, included, addedIds);
                }
            }
        }

        response = new()
        {
            Resource = bundle,
            ResourceType = "Bundle",
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, "Type search successful"),
            StatusCode = HttpStatusCode.OK,
        };
        return true;
    }

    private static void AddIncludeEntry(BundleJsonNode bundle, FhirRequestContext ctx, ResourceJsonNode included, HashSet<string> addedIds)
    {
        string relativeUrl = $"{included.ResourceType}/{included.Id}";
        if (!addedIds.Add(relativeUrl))
        {
            return;
        }

        bundle.Entry.Add(new BundleComponentJsonNode
        {
            Resource = included,
            Search = new BundleComponentSearchJsonNode { Mode = "include" },
        });
    }

    /// <summary>
    /// SMART-scope compartment filtering, deferred to Task 11: a real compartment engine does not
    /// exist yet. Every required test in this task runs with no <see cref="AuthorizationInfo"/> on the
    /// request, so this is never invoked by them; when it is invoked (an authorized request), results
    /// pass through unfiltered rather than half-implementing compartment membership checks.
    /// </summary>
    private List<ResourceJsonNode> FilterSearchResultsForAuth(List<ResourceJsonNode> resources) => resources;

    /// <summary>
    /// Resolves the effective <see cref="SearchParameterHandling"/> for a request: an explicit
    /// <c>Prefer: handling=…</c> header wins (last directive of the last header value), otherwise the
    /// tenant default applies (<see cref="SearchParameterHandling.Strict"/> under <c>--strict</c>).
    /// </summary>
    private SearchParameterHandling EffectiveSearchHandling(FhirRequestContext ctx)
    {
        if (ctx.RequestHeaders.TryGetValue("Prefer", out StringValues prefer))
        {
            for (int v = prefer.Count - 1; v >= 0; v--)
            {
                string? value = prefer[v];
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                string[] directives = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                for (int d = directives.Length - 1; d >= 0; d--)
                {
                    string directive = directives[d];
                    if (!directive.StartsWith("handling=", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string handlingValue = directive["handling=".Length..].Trim();
                    if (handlingValue.Equals("strict", StringComparison.OrdinalIgnoreCase))
                    {
                        return SearchParameterHandling.Strict;
                    }

                    if (handlingValue.Equals("lenient", StringComparison.OrdinalIgnoreCase))
                    {
                        return SearchParameterHandling.Lenient;
                    }
                }
            }
        }

        return _config.Strict ? SearchParameterHandling.Strict : SearchParameterHandling.Lenient;
    }

    /// <summary>Deserializes <see cref="FhirRequestContext.SourceObject"/> (if a <see cref="ResourceJsonNode"/>
    /// was already attached) or <see cref="FhirRequestContext.SourceContent"/>/<see cref="FhirRequestContext.SourceFormat"/>
    /// into a <see cref="ResourceJsonNode"/> for a create/update request.</summary>
    private bool TryDeserializeSource(FhirRequestContext ctx, out ResourceJsonNode? content, out FhirResponseContext response)
    {
        if (ctx.SourceObject is ResourceJsonNode r)
        {
            content = r;
            response = new();
            return true;
        }

        if (string.IsNullOrEmpty(ctx.SourceContent) || string.IsNullOrEmpty(ctx.SourceFormat))
        {
            content = null;
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.BadRequest, "Resource is required", OperationOutcomeJsonNode.IssueType.Structure),
                StatusCode = HttpStatusCode.BadRequest,
            };
            return false;
        }

        HttpStatusCode sc = SerializationUtils.TryDeserializeFhir(ctx.SourceContent, ctx.SourceFormat, out content, out string exMessage, _schema);

        if (sc != HttpStatusCode.OK || content is null)
        {
            OperationOutcomeJsonNode outcome = SerializationUtils.BuildOutcomeForRequest(
                sc, $"Failed to deserialize resource, format: {ctx.SourceFormat}, error: {exMessage}", OperationOutcomeJsonNode.IssueType.Structure);

            response = new()
            {
                Outcome = outcome,
                SerializedOutcome = SerializationUtils.SerializeFhir(outcome, _schema, ctx.DestinationFormat, ctx.SerializePretty),
                StatusCode = sc,
            };
            return false;
        }

        response = new();
        return true;
    }

    /// <summary>Serializes <see cref="FhirResponseContext.Resource"/>/<see cref="FhirResponseContext.Outcome"/>
    /// into <see cref="FhirResponseContext.SerializedResource"/>/<see cref="FhirResponseContext.SerializedOutcome"/>.</summary>
    private FhirResponseContext SerializeResponse(FhirRequestContext ctx, FhirResponseContext response)
    {
        string sr = response.Resource is null
            ? string.Empty
            : SerializationUtils.SerializeFhir((ResourceJsonNode)response.Resource, _schema, ctx.DestinationFormat, ctx.SerializePretty);

        string so = response.Outcome is null
            ? string.Empty
            : SerializationUtils.SerializeFhir((ResourceJsonNode)response.Outcome, _schema, ctx.DestinationFormat, ctx.SerializePretty);

        return response with
        {
            MimeType = ctx.DestinationFormat,
            SerializedResource = sr,
            SerializedOutcome = so,
        };
    }

    private string GetBaseUrl(FhirRequestContext? ctx) => ctx?.RequestBaseUrl(_config.BaseUrl) ?? _config.BaseUrl;

    private static FhirResponseContext NotImplementedResponse(string message) => new()
    {
        Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotImplemented, message, OperationOutcomeJsonNode.IssueType.NotSupported),
        StatusCode = HttpStatusCode.NotImplemented,
    };

    /// <summary>Not yet implemented - Bundle batch/transaction processing is Task 9.</summary>
    public bool ProcessBundle(FhirRequestContext ctx, out FhirResponseContext response)
    {
        response = NotImplementedResponse("ProcessBundle: Bundle batch/transaction processing is implemented in a later task.");
        return false;
    }

    /// <summary>Not yet implemented - CapabilityStatement generation is Task 10.</summary>
    public bool GetMetadata(FhirRequestContext ctx, out FhirResponseContext response)
    {
        response = NotImplementedResponse("GetMetadata: CapabilityStatement generation is implemented in a later task.");
        return false;
    }

    /// <summary>Not yet implemented in this task.</summary>
    public bool SystemDelete(FhirRequestContext ctx, out FhirResponseContext response)
    {
        response = NotImplementedResponse("SystemDelete is not yet implemented.");
        return false;
    }

    /// <summary>Not yet implemented in this task.</summary>
    public bool TypeDelete(FhirRequestContext ctx, out FhirResponseContext response)
    {
        response = NotImplementedResponse("TypeDelete is not yet implemented.");
        return false;
    }

    /// <summary>Not yet implemented in this task.</summary>
    public bool SystemSearch(FhirRequestContext ctx, out FhirResponseContext response)
    {
        response = NotImplementedResponse("SystemSearch is not yet implemented.");
        return false;
    }

    /// <summary>Not yet implemented - the Operations subsystem has not been ported.</summary>
    public bool SystemOperation(FhirRequestContext ctx, out FhirResponseContext response)
    {
        response = NotImplementedResponse("SystemOperation is not yet implemented.");
        return false;
    }

    /// <summary>Not yet implemented - the Operations subsystem has not been ported.</summary>
    public bool TypeOperation(FhirRequestContext ctx, out FhirResponseContext response)
    {
        response = NotImplementedResponse("TypeOperation is not yet implemented.");
        return false;
    }

    /// <summary>Not yet implemented - the Operations subsystem has not been ported.</summary>
    public bool InstanceOperation(FhirRequestContext ctx, out FhirResponseContext response)
    {
        response = NotImplementedResponse("InstanceOperation is not yet implemented.");
        return false;
    }

    /// <summary>Not yet implemented - compartment membership is Task 11.</summary>
    public bool CompartmentSearch(FhirRequestContext ctx, out FhirResponseContext response)
    {
        response = NotImplementedResponse("CompartmentSearch: compartment engine is implemented in a later task.");
        return false;
    }

    /// <summary>Not yet implemented - compartment membership is Task 11.</summary>
    public bool CompartmentTypeSearch(FhirRequestContext ctx, out FhirResponseContext response)
    {
        response = NotImplementedResponse("CompartmentTypeSearch: compartment engine is implemented in a later task.");
        return false;
    }

    /// <summary>Registers a compartment definition. Deferred to Task 11: there is no compartment
    /// engine yet to register against, so this only validates resource shape.</summary>
    public bool RegisterCompartmentDefinition(object compartmentDefinition) =>
        compartmentDefinition is ResourceJsonNode { ResourceType: "CompartmentDefinition" };

    /// <summary>Deferred to Task 11 (see <see cref="RegisterCompartmentDefinition"/>).</summary>
    public void RemoveCompartmentDefinition(string compartmentType)
    {
        // No compartment engine exists yet to remove a registration from.
    }

    /// <inheritdoc/>
    public bool SupportsResource(string resourceName) => _store.ContainsKey(resourceName);

    /// <inheritdoc/>
    public bool TryGetResourceInfo(object resource, out string resourceName, out string id)
    {
        if (resource is not ResourceJsonNode r)
        {
            resourceName = string.Empty;
            id = string.Empty;
            return false;
        }

        resourceName = r.ResourceType;
        id = r.Id;
        return true;
    }

    /// <summary>Subscriptions are not ported yet (no topic/subscription converter exists); always empty.</summary>
    public IEnumerable<ParsedSubscriptionTopic> CurrentTopics => [];

    /// <summary>Subscriptions are not ported yet; always empty.</summary>
    public IEnumerable<ParsedSubscription> CurrentSubscriptions => [];

    /// <summary>Subscriptions are not ported yet; always empty.</summary>
    public ConcurrentDictionary<string, List<ParsedSubscriptionStatus>> ReceivedNotifications { get; } = new();

    /// <summary>Subscriptions are not ported yet; a no-op.</summary>
    public void ChangeSubscriptionStatus(string id, string status)
    {
    }

    /// <summary>Subscriptions are not ported yet; always fails.</summary>
    public bool TryGetSubscription(ParsedSubscription parsed, out object? subscription)
    {
        subscription = null;
        return false;
    }

    /// <summary>Subscriptions are not ported yet; always fails.</summary>
    public bool TrySerializeToSubscription(ParsedSubscription subscriptionInfo, out string serialized, bool pretty, string destFormat = "")
    {
        serialized = string.Empty;
        return false;
    }

    /// <summary>Subscriptions are not ported yet; always empty.</summary>
    public string SerializeSubscriptionEvents(
        string subscriptionId, IEnumerable<long> eventNumbers, string notificationType, bool pretty, string contentType = "", string contentLevel = "") =>
        string.Empty;

    /// <inheritdoc/>
    public List<(string ResourceName, string? Name, string? Code, string? Description, string? SearchType)> GetSearchParameters(string? resourceName)
    {
        string rn = string.IsNullOrEmpty(resourceName) ? "Resource" : resourceName;

        if (!_search.Definitions.TryGetSearchParameters(rn, out IEnumerable<SearchParameterInfo> parameters))
        {
            return [];
        }

        return parameters
            .Select(p => (rn, (string?)p.Name, (string?)p.Code, (string?)p.Description, (string?)p.Type.ToString().ToLowerInvariant()))
            .ToList();
    }

    /// <inheritdoc/>
    public (string Message, List<(string SpName, string SpValue, bool IsOk, string Message)> Results) ValidateTypeSearchRequest(
        string resourceType,
        string searchString)
    {
        if (!_store.ContainsKey(resourceType))
        {
            return ($"Resource type: {resourceType} is not supported", []);
        }

        ParsedQuery query = _search.ParseQuery(resourceType, searchString);
        var unknown = new HashSet<string>(query.UnknownParameters, StringComparer.Ordinal);

        var results = new List<(string SpName, string SpValue, bool IsOk, string Message)>();
        int ok = 0;
        int bad = 0;

        System.Collections.Specialized.NameValueCollection parsed = HttpUtility.ParseQueryString(searchString);
        foreach (string? key in parsed.AllKeys ?? [])
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            string value = parsed[key] ?? string.Empty;
            string code = key.Split(':')[0];
            bool isOk = !unknown.Contains(code) && !unknown.Contains(key);

            results.Add((key, value, isOk, isOk
                ? "Processed successfully"
                : $"'{key}={value}' failed to process. E.g., unknown search parameter or invalid value"));

            if (isOk) { ok++; } else { bad++; }
        }

        return ($"Valid: {ok}, Invalid: {bad}", results);
    }

    /// <summary>The FHIRPath <c>resolve()</c> hook: resolves a "ResourceType/id" (or absolute URL ending
    /// in one) reference to its stored <see cref="IElement"/>.</summary>
    public bool TryResolveAsElement(string reference, out IElement? element)
    {
        element = null;

        if (string.IsNullOrEmpty(reference))
        {
            return false;
        }

        string[] parts = reference.Split('/');
        if (parts.Length < 2)
        {
            return false;
        }

        string resourceType = parts[^2];
        string id = parts[^1];

        ResourceJsonNode? resource = GetStore(resourceType)?.InstanceRead(id);
        if (resource is null)
        {
            return false;
        }

        element = resource.ToElement(_schema);
        return true;
    }

    // IReadOnlyDictionary<string, IResourceStore> - delegates to the per-resource-type store dictionary.

    IEnumerable<string> IReadOnlyDictionary<string, IResourceStore>.Keys => _store.Keys;

    IEnumerable<IResourceStore> IReadOnlyDictionary<string, IResourceStore>.Values => _store.Values;

    int IReadOnlyCollection<KeyValuePair<string, IResourceStore>>.Count => _store.Count;

    IResourceStore IReadOnlyDictionary<string, IResourceStore>.this[string key] => _store[key];

    bool IReadOnlyDictionary<string, IResourceStore>.ContainsKey(string key) => _store.ContainsKey(key);

    bool IReadOnlyDictionary<string, IResourceStore>.TryGetValue(string key, out IResourceStore value)
    {
        bool result = _store.TryGetValue(key, out ResourceStore? rs);
        value = rs!;
        return result;
    }

    IEnumerator<KeyValuePair<string, IResourceStore>> IEnumerable<KeyValuePair<string, IResourceStore>>.GetEnumerator() =>
        _store.Select(kvp => new KeyValuePair<string, IResourceStore>(kvp.Key, kvp.Value)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() =>
        _store.Select(kvp => new KeyValuePair<string, IResourceStore>(kvp.Key, kvp.Value)).GetEnumerator();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_hasDisposed)
        {
            return;
        }

        foreach (ResourceStore rs in _store.Values)
        {
            rs.Dispose();
        }

        _hasDisposed = true;
        GC.SuppressFinalize(this);
    }
}
