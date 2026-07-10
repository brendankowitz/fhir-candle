using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;
using FhirCandle.Compartments;
using FhirCandle.Models;
using FhirCandle.Operations;
using FhirCandle.Schema;
using FhirCandle.Search;
using FhirCandle.Serialization;
using FhirCandle.Strict;
using FhirCandle.Subscriptions;
using FhirCandle.Utils;
using Ignixa.Abstractions;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.Models;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Ignixa.Specification.ValueSets.Normative;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using SearchParameterHandling = FhirCandle.Client.CandleClientSettings.SearchParameterHandling;

namespace FhirCandle.Storage;

/// <summary>
/// A FHIR store spanning every resource type for a single tenant/FHIR-version combination.
/// </summary>
/// <remarks>
/// This is the Ignixa-model port of the old (Firely-based) <c>VersionedFhirStore</c>: the core
/// CRUD/search/dispatch surface plus terminology (<see cref="StoreTerminologyService"/>), compartments
/// (<see cref="ParsedCompartment"/>), operations, and subscription/topic execution
/// (<see cref="FhirCandle.Subscriptions.TopicConverter"/>/<see cref="FhirCandle.Subscriptions.SubscriptionConverter"/>).
/// </remarks>
public sealed class VersionedFhirStore : IFhirStore
{
    /// <summary>FHIR id datatype regex per spec (R4/R4B/R5 datatypes.html#id): <c>[A-Za-z0-9\-\.]{1,64}</c>.
    /// Used by strict-mode pre-checks on POST/PUT to reject ill-formed resource ids.</summary>
    private static readonly Regex _fhirIdRegex = new("^[A-Za-z0-9\\-\\.]{1,64}$", RegexOptions.Compiled);

    private readonly Dictionary<string, ResourceStore> _store = [];
    private readonly Dictionary<string, ParsedCompartment> _compartments = [];
    private readonly ConcurrentDictionary<string, ParsedSubscriptionTopic> _topics = new();
    private readonly ConcurrentDictionary<string, ParsedSubscription> _subscriptions = new();
    private readonly Dictionary<string, IFhirOperation> _operations = [];
    private readonly HashSet<string> _protectedResources = [];
    private readonly HashSet<string> _loadedDirectives = [];
    private readonly HashSet<string> _loadedPackageIds = [];
    private readonly HashSet<string> _loadedSupplements = [];
    private readonly ConcurrentQueue<string> _resourceQ = [];

    private TenantConfiguration _config = null!;
    private IFhirSchemaProvider _schema = null!;
    private CandleSearchService _search = null!;
    private StoreTerminologyService _terminology = null!;
    private TopicConverter _topicConverter = null!;
    private SubscriptionConverter _subscriptionConverter = null!;
    private int _maxResourceCount;
    private bool _hasDisposed;

    /// <summary>Cached, already-serialized-shape CapabilityStatement (see <see cref="GetCapabilities"/>),
    /// and whether it needs to be rebuilt. Starts stale so the first <see cref="GetMetadata"/> call
    /// always builds it. Nothing in this port currently invalidates the cache after the first build - no
    /// runtime SearchParameter registration exists yet against this store (unlike the old file's
    /// <c>TrySetExecutableSearchParameter</c>/<c>TryRemoveExecutableSearchParameter</c>), so once built the
    /// cache remains valid for the life of the store.</summary>
    private bool _capabilitiesAreStale = true;
    private ResourceJsonNode? _cachedCapabilityStatement;
    private const string _capabilityStatementId = "metadata";

    /// <inheritdoc/>
    public event EventHandler<StoreInstanceEventArgs>? OnInstanceCreated;

    /// <inheritdoc/>
    public event EventHandler<StoreInstanceEventArgs>? OnInstanceUpdated;

    /// <inheritdoc/>
    public event EventHandler<StoreInstanceEventArgs>? OnInstanceDeleted;

    /// <summary>Occurs when a Subscription is registered, updated, or removed.</summary>
    public event EventHandler<SubscriptionChangedEventArgs>? OnSubscriptionsChanged;

    /// <summary>Occurs when a resource change matched a subscription and a notification event was generated.</summary>
    public event EventHandler<SubscriptionSendEventArgs>? OnSubscriptionSendEvent;

    /// <summary>Occurs when the set of received (inbound) subscription notifications changes.</summary>
    public event EventHandler<ReceivedSubscriptionChangedEventArgs>? OnReceivedSubscriptionChanged;

    /// <summary>Occurs when an inbound subscription notification is received via <c>$subscription-hook</c>.</summary>
    public event EventHandler<ReceivedSubscriptionEventArgs>? OnReceivedSubscriptionEvent;

    /// <summary>Gets the FHIR schema provider for this store's FHIR version.</summary>
    public IFhirSchemaProvider Schema => _schema;

    /// <summary>Gets the search service shared by every per-resource-type store.</summary>
    public CandleSearchService Search => _search;

    /// <summary>Gets the terminology service backing every per-resource-type store's <c>:in</c>/
    /// <c>:not-in</c> search modifier support.</summary>
    public StoreTerminologyService Terminology => _terminology;

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
        _terminology = new StoreTerminologyService(_schema);
        _topicConverter = new TopicConverter(config.FhirVersion);
        _subscriptionConverter = new SubscriptionConverter(config.FhirVersion, config.MaxSubscriptionExpirationMinutes);

        foreach (string resourceType in _schema.ResourceTypeNames)
        {
            switch (resourceType)
            {
                case "Parameters":
                case "OperationOutcome":
                case "SubscriptionStatus":
                    continue;
            }

            var rs = new ResourceStore(resourceType, _schema, _search, _terminology.VsContains, ResolveElement);

            rs.OnInstanceCreated += (_, e) => RegisterInstanceCreated(e.ResourceType, e.ResourceId);
            rs.OnInstanceUpdated += (_, e) => RegisterInstanceUpdated(e.ResourceType, e.ResourceId);
            rs.OnInstanceDeleted += (_, e) => RegisterInstanceDeleted(e.ResourceType, e.ResourceId);

            rs.OnCompartmentDefinitionChanged += (_, resource) => RegisterCompartmentDefinition(resource);
            rs.OnCompartmentDefinitionRemoved += (_, code) => RemoveCompartmentDefinition(code);

            rs.OnValueSetChanged += (_, resource) => _terminology.StoreProcessValueSet(resource.ToElement(_schema));
            rs.OnValueSetRemoved += (_, resource) => _terminology.StoreProcessValueSet(resource.ToElement(_schema), remove: true);

            rs.OnSubscriptionTopicChanged += (_, resource) => ProcessSubscriptionTopicResource(resource, remove: false);
            rs.OnSubscriptionTopicRemoved += (_, resource) => ProcessSubscriptionTopicResource(resource, remove: true);
            rs.OnSubscriptionChanged += (_, resource) => ProcessSubscriptionResource(resource, remove: false);
            rs.OnSubscriptionRemoved += (_, resource) => ProcessSubscriptionResource(resource, remove: true);
            rs.OnSubscriptionEventMatched += (_, e) => RegisterSendEvent(e);
            rs.OnSubscriptionTriggerError += (_, e) => RegisterError(e.SubscriptionId, e.Message);

            switch (resourceType)
            {
                case "Basic":
                case "SubscriptionTopic":
                    rs.SubscriptionTopicValidator = ValidateSubscriptionTopicResource;
                    break;

                case "Subscription":
                    rs.SubscriptionValidator = ValidateSubscriptionResource;
                    break;
            }

            _store.Add(resourceType, rs);
        }

        foreach (ParsedCompartment compartment in CoreCompartmentSource.GetCompartments(_schema.Version))
        {
            if (_store.ContainsKey(compartment.CompartmentType))
            {
                _compartments[compartment.CompartmentType] = compartment;
            }
        }

        RegisterEligibleOperations();

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

    /// <summary>Every <see cref="IFhirOperation"/> implementation in this assembly. Mirrors the old
    /// (Firely-based) file's <c>CheckLoadedOperations</c> reflection scan, but as an explicit list:
    /// there are exactly 8 known, sealed, same-assembly operations and no plugin story, so reflection
    /// buys nothing here - it is also trimming/AOT-hostile, and a constructor-throws failure would
    /// surface as a runtime reflection stack trace at store construction instead of a compile error.</summary>
    private static readonly IFhirOperation[] _knownOperations =
    [
        new OpValidate(),
        new OpConvert(),
        new OpResetStore(),
        new OpTestIfFhir(),
        new OpFeatureQuery(),
        new OpSubscriptionHook(),
        new OpSubscriptionEvents(),
        new OpSubscriptionStatus(),
    ];

    /// <summary>(Re-)registers every <see cref="_knownOperations"/> entry applicable to this store's FHIR
    /// version into <see cref="_operations"/>, keyed by <see cref="IFhirOperation.OperationName"/>.
    /// Idempotent and safe to call more than once: re-checking an already-registered operation's
    /// eligibility and re-adding it to the dictionary is a no-op. Called once from <see cref="Init"/> and
    /// again at the end of <see cref="LoadPackage"/>, because an operation's <see
    /// cref="IFhirOperation.RequiresPackage"/> gate only becomes satisfiable once a package has loaded -
    /// registering only in <see cref="Init"/> would leave any future <c>RequiresPackage</c>-gated
    /// operation permanently unregistered no matter what packages load afterward. This port does not
    /// self-register each operation's <see cref="IFhirOperation.GetDefinition"/> result as a stored
    /// OperationDefinition resource - nothing in this engine's CapabilityStatement generation consumes
    /// stored OperationDefinitions yet, so doing so would have no observable effect. Revisit if a future
    /// task wires OperationDefinition discovery into the CapabilityStatement.</summary>
    private void RegisterEligibleOperations()
    {
        foreach (IFhirOperation fhirOp in _knownOperations)
        {
            if (!fhirOp.CanonicalByFhirVersion.ContainsKey(_config.FhirVersion))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(fhirOp.RequiresPackage) &&
                !_loadedDirectives.Contains(fhirOp.RequiresPackage) &&
                !_loadedPackageIds.Contains(fhirOp.RequiresPackage))
            {
                continue;
            }

            _operations[fhirOp.OperationName] = fhirOp;
        }
    }

    /// <summary>Deletes every non-protected resource; when <paramref name="keepConformance"/> is true,
    /// resources of a conformance-bearing type (<see cref="ResourceStore.ResourcesAreConformance"/>,
    /// e.g. StructureDefinition, SearchParameter) are preserved. Semantics match the old (Firely-based)
    /// port's <c>$reset-store</c> introduced in commit f4675d0. Protected-resource filtering is handled
    /// by <see cref="ResourceStore.InstanceDelete"/> itself.</summary>
    public void ResetStore(bool keepConformance)
    {
        foreach (ResourceStore rs in _store.Values)
        {
            if (keepConformance && rs.ResourcesAreConformance)
            {
                continue;
            }

            foreach (string id in rs.Keys.ToList())
            {
                _ = rs.InstanceDelete(id, _protectedResources);
            }
        }
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

        RegisterEligibleOperations();
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

            case Common.StoreInteractionCodes.SystemOperation:
                return SystemOperation(ctx, out response);

            case Common.StoreInteractionCodes.TypeOperation:
                return TypeOperation(ctx, out response);

            case Common.StoreInteractionCodes.InstanceOperation:
                return InstanceOperation(ctx, out response);

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

        List<ResourceJsonNode> matches = ApplyChainedExpressions(rs.TypeSearch(query).ToList(), query);

        if (ctx.Authorization is not null)
        {
            matches = FilterSearchResultsForAuth(ctx, matches);
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

        BundleJsonNode bundle = BuildSearchBundle(ctx, query, matches, selfLink);

        response = new()
        {
            Resource = bundle,
            ResourceType = "Bundle",
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, "Type search successful"),
            StatusCode = HttpStatusCode.OK,
        };
        return true;
    }

    /// <summary>Filters <paramref name="matches"/> down to those satisfying every one of
    /// <paramref name="query"/>'s <see cref="ParsedQuery.ChainedExpressions"/> (chained/<c>_has</c>
    /// parameters) - these are parsed by <see cref="CandleSearchService.ParseQuery"/> but not evaluated
    /// by <see cref="ResourceStore.TypeSearch"/> itself, since testing them requires resolving other
    /// resource types' stores via <see cref="GetStore"/>, which only this store (not <see cref="ResourceStore"/>)
    /// has access to.</summary>
    private List<ResourceJsonNode> ApplyChainedExpressions(List<ResourceJsonNode> matches, ParsedQuery query) =>
        query.ChainedExpressions.Count == 0
            ? matches
            : matches.Where(candidate => query.ChainedExpressions.All(
                expr => SearchExecutor.EvaluateChained(expr, candidate, GetStore, _search, _schema))).ToList();

    /// <summary>Builds a searchset <see cref="BundleJsonNode"/> from already-filtered/sorted
    /// <paramref name="matches"/>, resolving <c>_include</c>/<c>_revinclude</c> per <paramref name="query"/>.
    /// Shared by <see cref="DoTypeSearch"/> and the compartment search paths.</summary>
    private BundleJsonNode BuildSearchBundle(FhirRequestContext ctx, ParsedQuery query, List<ResourceJsonNode> matches, string selfLink)
    {
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

        return bundle;
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
    /// SMART-scope compartment filtering: authorized requests pass unfiltered when the user scope grants
    /// blanket access, or when this is a Patient-compartment search whose id matches the launch patient
    /// (already scoped by the compartment filter itself). Otherwise each resource must both belong to the
    /// Patient compartment and satisfy a granted patient scope, checked via <see cref="IsInCompartment"/>
    /// against the launch patient.
    /// </summary>
    private List<ResourceJsonNode> FilterSearchResultsForAuth(FhirRequestContext ctx, List<ResourceJsonNode> resources)
    {
        if (ctx.Authorization is null)
        {
            return resources;
        }

        if (ctx.Authorization.UserScopes.Contains("*.*") || ctx.Authorization.UserScopes.Contains("*.s"))
        {
            return resources;
        }

        if ((ctx.Interaction is Common.StoreInteractionCodes.CompartmentSearch or Common.StoreInteractionCodes.CompartmentTypeSearch) &&
            (ctx.CompartmentType == "Patient") &&
            ($"Patient/{ctx.Id}" == ctx.Authorization.LaunchPatient))
        {
            return resources;
        }

        return resources.Where(r => IsAuthorizedAsSearchMatch(ctx, r)).ToList();
    }

    private bool IsAuthorizedAsSearchMatch(FhirRequestContext ctx, ResourceJsonNode resource)
    {
        AuthorizationInfo authorization = ctx.Authorization!;

        if (authorization.UserScopes.Contains("*.*") ||
            authorization.UserScopes.Contains("*.s") ||
            authorization.UserScopes.Contains(resource.ResourceType + ".*") ||
            authorization.UserScopes.Contains(resource.ResourceType + ".s"))
        {
            return true;
        }

        if ((ctx.Interaction is Common.StoreInteractionCodes.CompartmentSearch or Common.StoreInteractionCodes.CompartmentTypeSearch) &&
            (ctx.CompartmentType == "Patient") &&
            ($"Patient/{ctx.Id}" == authorization.LaunchPatient))
        {
            return true;
        }

        if (!_compartments.TryGetValue("Patient", out ParsedCompartment? patientCompartment) ||
            !patientCompartment.IncludedResources.TryGetValue(resource.ResourceType, out ParsedCompartment.IncludedResource? ir))
        {
            return false;
        }

        if (!authorization.PatientScopes.Contains("*.*") &&
            !authorization.PatientScopes.Contains("*.s") &&
            !authorization.PatientScopes.Contains(resource.ResourceType + ".*") &&
            !authorization.PatientScopes.Contains(resource.ResourceType + ".s"))
        {
            return false;
        }

        if ((resource.ResourceType == "Patient") && ($"Patient/{resource.Id}" == authorization.LaunchPatient))
        {
            return true;
        }

        if (authorization.LaunchPatient.Split('/') is not [_, string launchPatientId])
        {
            return false;
        }

        return IsInCompartment(resource, ir, "Patient", launchPatientId);
    }

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

    /// <inheritdoc/>
    public bool ProcessBundle(FhirRequestContext ctx, out FhirResponseContext response)
    {
        if (!TryDeserializeSource(ctx, out ResourceJsonNode? content, out response))
        {
            return false;
        }

        if (content!.ResourceType != "Bundle")
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(
                    HttpStatusCode.UnprocessableEntity,
                    $"Cannot process non-Bundle resource type ({content.ResourceType}) as a Bundle",
                    OperationOutcomeJsonNode.IssueType.Invalid),
                StatusCode = HttpStatusCode.UnprocessableEntity,
            };
            response = SerializeResponse(ctx, response);
            return false;
        }

        BundleJsonNode requestBundle = content is BundleJsonNode typed
            ? typed
            : new BundleJsonNode(content.MutableNode, content.FhirVersion);

        bool success = DoProcessBundle(ctx, requestBundle, out response);
        response = SerializeResponse(ctx, response);
        return success;
    }

    /// <summary>Executes the process-bundle operation for an already-typed request bundle, without
    /// serializing the response.</summary>
    internal bool DoProcessBundle(FhirRequestContext ctx, BundleJsonNode requestBundle, out FhirResponseContext response)
    {
        var responseBundle = new BundleJsonNode { Id = Guid.NewGuid().ToString() };

        switch (requestBundle.Type)
        {
            case BundleJsonNode.BundleType.Transaction:
                responseBundle.Type = BundleJsonNode.BundleType.TransactionResponse;
                ProcessTransaction(ctx, requestBundle, responseBundle);
                break;

            case BundleJsonNode.BundleType.Batch:
                responseBundle.Type = BundleJsonNode.BundleType.BatchResponse;
                ProcessBatch(ctx, requestBundle, responseBundle);
                break;

            default:
                response = new()
                {
                    Outcome = SerializationUtils.BuildOutcomeForRequest(
                        HttpStatusCode.UnprocessableEntity,
                        $"Unsupported Bundle process request! Type: {requestBundle.Type}",
                        OperationOutcomeJsonNode.IssueType.NotSupported),
                    StatusCode = HttpStatusCode.UnprocessableEntity,
                };
                return false;
        }

        response = new()
        {
            Resource = responseBundle,
            ResourceType = "Bundle",
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, $"Processed {requestBundle.Type} bundle"),
            StatusCode = HttpStatusCode.OK,
        };
        return true;
    }

    /// <summary>Processes a transaction bundle: reassigns POST-entry ids and rewrites same-bundle
    /// references (see <see cref="ApplyTransactionIdReassignment"/>), then dispatches entries in
    /// FHIR's required order (DELETE, POST, PUT/PATCH, GET/HEAD, then anything unrecognized).</summary>
    private void ProcessTransaction(FhirRequestContext ctx, BundleJsonNode transaction, BundleJsonNode responseBundle)
    {
        List<BundleComponentJsonNode> entries = transaction.Entry.ToList();

        ApplyTransactionIdReassignment(entries);

        foreach (BundleComponentJsonNode entry in entries.OrderBy(TransactionMethodPriority))
        {
            ProcessEntry(ctx, entry, responseBundle, forceAllowExistingId: true);
        }
    }

    /// <summary>FHIR transaction processing order: DELETE, POST, PUT/PATCH, GET/HEAD, then anything
    /// with a missing or unrecognized request method (never dropped, unlike the old file's literal
    /// port would have done - each still reaches <see cref="ProcessEntry"/> and gets a proper error
    /// entry rather than being silently skipped).</summary>
    private static int TransactionMethodPriority(BundleComponentJsonNode entry) =>
        entry.Request?.Method?.ToUpperInvariant() switch
        {
            "DELETE" => 0,
            "POST" => 1,
            "PUT" or "PATCH" => 2,
            "GET" or "HEAD" => 3,
            _ => 4,
        };

    /// <summary>Processes a batch bundle: every entry is dispatched independently, in bundle order,
    /// with no id reassignment or cross-entry reference rewriting.</summary>
    private void ProcessBatch(FhirRequestContext ctx, BundleJsonNode batch, BundleJsonNode responseBundle)
    {
        foreach (BundleComponentJsonNode entry in batch.Entry)
        {
            ProcessEntry(ctx, entry, responseBundle, forceAllowExistingId: false);
        }
    }

    /// <summary>Reassigns server-side ids to every POST entry carrying a resource (FHIR transactions
    /// ignore client-supplied ids on POST, exactly like a top-level create), then rewrites every
    /// entry's resource so that literal <c>reference</c> strings pointing at a reassigned entry's
    /// <c>fullUrl</c> or original <c>ResourceType/id</c> are updated to the new <c>ResourceType/newId</c>,
    /// and fixes any other entry's request URL whose last path segment names a reassigned original id.
    /// Batch bundles never call this - only transactions get id reassignment/reference rewriting.</summary>
    private static void ApplyTransactionIdReassignment(List<BundleComponentJsonNode> entries)
    {
        var recs = new List<(string? FullUrl, string? OriginalId, string ResourceType, string NewId)>();

        foreach (BundleComponentJsonNode entry in entries)
        {
            BundleComponentRequestJsonNode? request = entry.Request;
            ResourceJsonNode? resource = entry.Resource;

            if (request is null || resource is null ||
                !string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string newId = Guid.NewGuid().ToString();
            recs.Add((
                string.IsNullOrEmpty(entry.FullUrl) ? null : entry.FullUrl,
                string.IsNullOrEmpty(resource.Id) ? null : resource.Id,
                resource.ResourceType,
                newId));

            // FHIR REST: the server always assigns a new id to a POST'd resource inside a
            // transaction, ignoring whatever id the client supplied.
            resource.Id = newId;
        }

        if (recs.Count == 0)
        {
            return;
        }

        var referenceMap = new Dictionary<string, string>();
        var originalIdSegmentMap = new Dictionary<string, string>();

        foreach (var rec in recs)
        {
            string newReference = $"{rec.ResourceType}/{rec.NewId}";

            if (rec.FullUrl is not null)
            {
                referenceMap[rec.FullUrl] = newReference;
            }

            if (rec.OriginalId is not null)
            {
                referenceMap[$"{rec.ResourceType}/{rec.OriginalId}"] = newReference;
                originalIdSegmentMap[rec.OriginalId] = rec.NewId;
            }
        }

        foreach (BundleComponentJsonNode entry in entries)
        {
            BundleComponentRequestJsonNode? request = entry.Request;
            if (request is not null && !string.IsNullOrEmpty(request.Url))
            {
                string[] urlParts = request.Url.Split('?');
                string[] segments = urlParts[0].Split('/');
                string idSegment = segments[^1];

                if (originalIdSegmentMap.TryGetValue(idSegment, out string? newIdForUrl))
                {
                    segments[^1] = newIdForUrl;
                    string newPath = string.Join('/', segments);
                    request.Url = urlParts.Length > 1 ? $"{newPath}?{urlParts[1]}" : newPath;
                }
            }

            ResourceJsonNode? resource = entry.Resource;
            if (resource is not null)
            {
                RewriteReferences(resource.MutableNode, referenceMap);
            }
        }
    }

    /// <summary>Recursively rewrites literal <c>reference</c> string properties anywhere in
    /// <paramref name="node"/>'s tree that exactly match a key in <paramref name="map"/>.</summary>
    private static void RewriteReferences(JsonNode? node, IReadOnlyDictionary<string, string> map)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.TryGetPropertyValue("reference", out JsonNode? rv)
                    && rv is JsonValue v && v.TryGetValue(out string? r)
                    && r is not null && map.TryGetValue(r, out string? replacement))
                {
                    obj["reference"] = replacement;
                }
                foreach (var kv in obj.ToList()) RewriteReferences(kv.Value, map);
                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr) RewriteReferences(item, map);
                break;
        }
    }

    /// <summary>Dispatches a single bundle entry's request through <see cref="PerformInteraction"/> and
    /// appends the corresponding response entry to <paramref name="responseBundle"/>. Shared by both
    /// <see cref="ProcessTransaction"/> (<paramref name="forceAllowExistingId"/> always true, since ids
    /// were already reassigned/agreed upon in the pre-pass) and <see cref="ProcessBatch"/> (always false,
    /// ordinary per-entry semantics).</summary>
    private void ProcessEntry(FhirRequestContext ctx, BundleComponentJsonNode entry, BundleJsonNode responseBundle, bool forceAllowExistingId)
    {
        BundleComponentRequestJsonNode? request = entry.Request;

        if (request is null)
        {
            responseBundle.Entry.Add(new BundleComponentJsonNode
            {
                FullUrl = entry.FullUrl,
                Response = new BundleComponentResponseJsonNode
                {
                    Status = GetResponseStatus(HttpStatusCode.BadRequest),
                    Outcome = SerializationUtils.BuildOutcomeForRequest(
                        HttpStatusCode.UnprocessableEntity,
                        "Entry is missing a request",
                        OperationOutcomeJsonNode.IssueType.Required),
                },
            });
            return;
        }

        JsonObject requestObj = request.MutableNode;

        // `entry.Resource` (if present) is a child of the incoming request bundle's own JsonNode tree.
        // Cloning it before it can be stored keeps the stored resource an independent tree - otherwise
        // it would carry a permanent parent-link to this (otherwise transient) request bundle, and
        // System.Text.Json.Nodes.JsonNode's single-parent invariant would then reject ever embedding it
        // in a future response (search results, other transactions, etc.).
        ResourceJsonNode? entryResource = entry.Resource;
        object? sourceForDispatch = entryResource is null
            ? null
            : JsonSourceNodeFactory.Parse((JsonNode)entryResource.MutableNode.DeepClone());

        FhirRequestContext entryCtx = new()
        {
            TenantName = ctx.TenantName,
            Store = ctx.Store,
            Authorization = ctx.Authorization,
            RequestHeaders = ctx.RequestHeaders,
            Forwarded = ctx.Forwarded,
            HttpMethod = request.Method ?? string.Empty,
            Url = request.Url ?? string.Empty,
            IfMatch = GetOptionalString(requestObj, "ifMatch"),
            IfModifiedSince = GetOptionalString(requestObj, "ifModifiedSince"),
            IfNoneMatch = GetOptionalString(requestObj, "ifNoneMatch"),
            IfNoneExist = GetOptionalString(requestObj, "ifNoneExist"),
            SourceObject = sourceForDispatch,
        };

        if (entryCtx.Interaction is null)
        {
            responseBundle.Entry.Add(new BundleComponentJsonNode
            {
                FullUrl = entry.FullUrl,
                Response = new BundleComponentResponseJsonNode
                {
                    Status = GetResponseStatus(HttpStatusCode.InternalServerError),
                    Outcome = SerializationUtils.BuildOutcomeForRequest(
                        HttpStatusCode.NotImplemented,
                        $"Request could not be parsed to known interaction: {request.Method} {request.Url}",
                        OperationOutcomeJsonNode.IssueType.NotSupported),
                },
            });
            return;
        }

        // No bulk-load-suppression flag exists yet in this port (unlike the old file's `_loadState`
        // gate) - every entry's authorization is checked unconditionally.
        if (!ctx.IsAuthorized())
        {
            responseBundle.Entry.Add(new BundleComponentJsonNode
            {
                FullUrl = entry.FullUrl,
                Response = new BundleComponentResponseJsonNode
                {
                    Status = GetResponseStatus(HttpStatusCode.Unauthorized),
                    Outcome = SerializationUtils.BuildOutcomeForRequest(
                        HttpStatusCode.Unauthorized,
                        $"Unauthorized request: {request.Method} {request.Url}, parsed interaction: {entryCtx.Interaction}",
                        OperationOutcomeJsonNode.IssueType.Forbidden),
                },
            });
            return;
        }

        bool opSuccess = PerformInteraction(entryCtx, out FhirResponseContext opResponse, serializeReturn: false, forceAllowExistingId: forceAllowExistingId);

        ResourceJsonNode? responseResource = opSuccess ? opResponse.Resource as ResourceJsonNode : null;

        var responseComponent = new BundleComponentResponseJsonNode
        {
            Status = GetResponseStatus(opResponse.StatusCode ?? (opSuccess ? HttpStatusCode.OK : HttpStatusCode.InternalServerError)),
        };

        if (opSuccess)
        {
            if (opResponse.Outcome is ResourceJsonNode successOutcome)
            {
                responseComponent.Outcome = successOutcome;
            }

            if (!string.IsNullOrEmpty(opResponse.ETag))
            {
                responseComponent.Etag = opResponse.ETag;
            }

            if (responseResource?.Meta.LastUpdated is { } lastUpdated)
            {
                responseComponent.LastModified = lastUpdated;
            }

            if (!string.IsNullOrEmpty(opResponse.Location))
            {
                responseComponent.Location = opResponse.Location;
            }
        }
        else if (opResponse.Outcome is OperationOutcomeJsonNode failureOutcome)
        {
            failureOutcome.Issue.Add(new OperationOutcomeJsonNode.IssueComponent
            {
                Severity = OperationOutcomeJsonNode.IssueSeverity.Error,
                Code = OperationOutcomeJsonNode.IssueType.NotSupported,
                Diagnostics = $"Unsupported request: {request.Method} {request.Url}, parsed interaction: {entryCtx.Interaction}",
            });
            responseComponent.Outcome = failureOutcome;
        }
        else
        {
            responseComponent.Outcome = SerializationUtils.BuildOutcomeForRequest(
                HttpStatusCode.NotImplemented,
                $"Unsupported request: {request.Method} {request.Url}, parsed interaction: {entryCtx.Interaction}",
                OperationOutcomeJsonNode.IssueType.NotSupported);
        }

        var responseEntry = new BundleComponentJsonNode
        {
            FullUrl = entry.FullUrl,
            Response = responseComponent,
        };

        if (responseResource is not null)
        {
            // System.Text.Json.Nodes.JsonNode enforces a single-parent invariant: `responseResource`
            // may be the exact instance held long-term in `_resourceStore` (e.g. an existing match
            // returned by a conditional create, or the just-stored resource itself), so attaching it
            // directly here would permanently tie it to this transient response bundle and break any
            // later attempt to embed it elsewhere (another search, another transaction). Clone it.
            responseEntry.Resource = JsonSourceNodeFactory.Parse((JsonNode)responseResource.MutableNode.DeepClone());
        }

        responseBundle.Entry.Add(responseEntry);
    }

    /// <summary>Reads an optional string property (e.g. <c>ifMatch</c>, <c>ifNoneExist</c>) directly off
    /// a raw request <see cref="JsonObject"/> - these are not exposed as typed properties on
    /// <see cref="BundleComponentRequestJsonNode"/>, which only surfaces <c>method</c>/<c>url</c>.</summary>
    private static string GetOptionalString(JsonObject obj, string propertyName) =>
        obj.TryGetPropertyValue(propertyName, out JsonNode? node) &&
        node is JsonValue value &&
        value.TryGetValue(out string? s) &&
        s is not null
            ? s
            : string.Empty;

    private static string GetResponseStatus(HttpStatusCode sc) => $"{(int)sc} {sc}";

    /// <inheritdoc/>
    public bool GetMetadata(FhirRequestContext ctx, out FhirResponseContext response)
    {
        ResourceJsonNode cs = GetCapabilities(ctx);

        // No `meta` is ever set on the generated document (there is no backing store entry to version
        // it against - see the deviation note on GetCapabilities), so ETag/LastModified stay empty.
        // Deliberately avoid touching `cs.Meta` here: that property getter creates an empty `meta: {}`
        // object on first access if none exists, which would otherwise leak into the serialized output.
        response = new()
        {
            Resource = cs,
            ResourceType = "CapabilityStatement",
            Id = _capabilityStatementId,
            Location = $"{GetBaseUrl(ctx)}/CapabilityStatement/{_capabilityStatementId}",
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, "Retrieved current CapabilityStatement"),
            StatusCode = HttpStatusCode.OK,
        };

        response = SerializeResponse(ctx, response);
        return true;
    }

    /// <summary>Returns the current CapabilityStatement, rebuilding it if stale or if <paramref name="ctx"/>
    /// carries reverse-proxy <see cref="FhirRequestContext.Forwarded"/> info (the generated document embeds
    /// the request's base URL, so a forwarded request can never safely reuse the cache built for the
    /// canonical base URL). Mirrors the old file's <c>GetCapabilities</c>/<c>generateCapabilities</c> split.</summary>
    private ResourceJsonNode GetCapabilities(FhirRequestContext? ctx)
    {
        if (!_capabilitiesAreStale && (ctx?.Forwarded is null) && _cachedCapabilityStatement is not null)
        {
            return JsonSourceNodeFactory.Parse((JsonNode)_cachedCapabilityStatement.MutableNode.DeepClone());
        }

        return BuildCapabilityStatement(ctx);
    }

    /// <summary>Builds the CapabilityStatement as a raw <see cref="JsonObject"/> tree (there is no typed
    /// <c>CapabilityStatementJsonNode</c> wrapper in Ignixa), then wraps it via
    /// <see cref="JsonSourceNodeFactory"/>. Ports the old file's <c>generateCapabilities</c>: every
    /// <c>CapabilityStatement.XComponent</c> becomes a <see cref="JsonObject"/> literal, and every Firely
    /// enum (<c>TypeRestfulInteraction.Read</c>, <c>RestfulCapabilityMode.Server</c>, etc.) becomes its
    /// FHIR wire-format string literal directly.</summary>
    private ResourceJsonNode BuildCapabilityStatement(FhirRequestContext? ctx)
    {
        string root = GetBaseUrl(ctx);
        string smartRoot = FhirUrlToSmart(root);

        var restResource = new JsonArray();

        // Precompute reverse-include targets once: for every resource type's Reference-typed search
        // parameters, record "ResourceType:code" against each of its target resource types. Mirrors the
        // old file's per-store `_supportedRevIncludes`, computed here instead since that concept no
        // longer lives on ResourceStore (Task 6 centralized search parameter definitions).
        var revIncludesByTarget = new Dictionary<string, List<string>>();
        foreach (string otherType in _store.Keys)
        {
            if (!_search.Definitions.TryGetSearchParameters(otherType, out IEnumerable<SearchParameterInfo> otherParams))
            {
                continue;
            }

            foreach (SearchParameterInfo sp in otherParams.Where(p => p.Type == SearchParamType.Reference))
            {
                foreach (string target in sp.TargetResourceTypes)
                {
                    if (!revIncludesByTarget.TryGetValue(target, out List<string>? list))
                    {
                        list = [];
                        revIncludesByTarget[target] = list;
                    }

                    list.Add($"{otherType}:{sp.Name}");
                }
            }
        }

        foreach (string resourceName in _store.Keys)
        {
            _search.Definitions.TryGetSearchParameters(resourceName, out IEnumerable<SearchParameterInfo> parameters);
            List<SearchParameterInfo> paramList = (parameters ?? []).ToList();

            var searchParamArray = new JsonArray();
            foreach (SearchParameterInfo sp in paramList)
            {
                var spObj = new JsonObject
                {
                    ["name"] = sp.Name,
                    ["type"] = sp.Type.ToString().ToLowerInvariant(),
                };

                if (sp.Url is not null)
                {
                    spObj["definition"] = sp.Url.ToString();
                }

                if (!string.IsNullOrEmpty(sp.Description))
                {
                    spObj["documentation"] = sp.Description;
                }

                searchParamArray.Add(spObj);
            }

            List<string> searchIncludes = paramList
                .Where(p => p.Type == SearchParamType.Reference)
                .Select(p => $"{resourceName}:{p.Name}")
                .Order(StringComparer.Ordinal)
                .ToList();

            List<string> searchRevIncludes = revIncludesByTarget.TryGetValue(resourceName, out List<string>? revList)
                ? revList.Order(StringComparer.Ordinal).ToList()
                : [];

            var rc = new JsonObject
            {
                ["type"] = resourceName,
                ["interaction"] = new JsonArray(
                    Interaction("create"),
                    Interaction("delete"),
                    Interaction("read"),
                    Interaction("search-type"),
                    Interaction("update")),
                ["versioning"] = "no-version",
                ["updateCreate"] = true,
                ["conditionalCreate"] = true,
                ["conditionalRead"] = "full-support",
                ["conditionalUpdate"] = true,
                ["conditionalDelete"] = "not-supported",
                ["referencePolicy"] = new JsonArray("literal", "logical", "local"),
                ["searchInclude"] = new JsonArray([.. searchIncludes.Select(s => (JsonNode)s)]),
                ["searchRevInclude"] = new JsonArray([.. searchRevIncludes.Select(s => (JsonNode)s)]),
                ["searchParam"] = searchParamArray,
            };

            restResource.Add(rc);
        }

        var restComponent = new JsonObject
        {
            ["mode"] = "server",
            ["interaction"] = new JsonArray(
                Interaction("batch"),
                Interaction("search-system"),
                Interaction("transaction")),
            ["resource"] = restResource,
        };

        if (_config.SmartRequired || _config.SmartAllowed)
        {
            string securityCodeSystemUrl = _config.FhirVersion switch
            {
                FhirReleases.FhirSequenceCodes.R4 => "http://terminology.hl7.org/CodeSystem/restful-security-service",
                FhirReleases.FhirSequenceCodes.R4B => "http://terminology.hl7.org/CodeSystem/restful-security-service",
                FhirReleases.FhirSequenceCodes.R5 => "http://hl7.org/fhir/restful-security-service",
                _ => "http://hl7.org/fhir/restful-security-service",
            };

            restComponent["security"] = new JsonObject
            {
                ["cors"] = true,
                ["service"] = new JsonArray(new JsonObject
                {
                    ["coding"] = new JsonArray(new JsonObject
                    {
                        ["system"] = securityCodeSystemUrl,
                        ["code"] = "SMART-on-FHIR",
                    }),
                }),
                ["extension"] = new JsonArray(new JsonObject
                {
                    ["url"] = "http://fhir-registry.smarthealthit.org/StructureDefinition/oauth-uris",
                    ["extension"] = new JsonArray(
                        new JsonObject { ["url"] = "token", ["valueUri"] = $"{smartRoot}/token" },
                        new JsonObject { ["url"] = "authorize", ["valueUri"] = $"{smartRoot}/authorize" },
                        new JsonObject { ["url"] = "register", ["valueUri"] = $"{smartRoot}/register" },
                        new JsonObject { ["url"] = "manage", ["valueUri"] = $"{smartRoot}/clients" }),
                }),
            };
        }

        var cs = new JsonObject
        {
            ["resourceType"] = "CapabilityStatement",
            ["id"] = _capabilityStatementId,
            ["url"] = $"{root}/CapabilityStatement/{_capabilityStatementId}",
            ["name"] = "Capabilities" + _config.FhirVersion,
            ["status"] = "active",
            ["date"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ["kind"] = "instance",
            ["software"] = new JsonObject
            {
                ["name"] = "fhir-candle",
                ["version"] = GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            },
            ["implementation"] = new JsonObject
            {
                ["description"] = "fhir-candle: A FHIR Server for testing and development",
                ["url"] = "https://github.com/FHIR/fhir-candle",
            },
            ["fhirVersion"] = _schema.FullVersion,
            ["format"] = new JsonArray([.. _config.SupportedFormats.Select(f => (JsonNode)f)]),
            ["rest"] = new JsonArray(restComponent),
        };

        ResourceJsonNode resource = JsonSourceNodeFactory.Parse((JsonNode)cs);

        if (root == _config.BaseUrl)
        {
            _cachedCapabilityStatement = resource;
            _capabilitiesAreStale = false;
        }

        return resource;

        static JsonObject Interaction(string code) => new() { ["code"] = code };
    }

    /// <summary>Rewrites a FHIR base URL to the corresponding SMART discovery root
    /// (<c>.../fhir/{tenant}</c> to <c>.../_smart/{tenant}</c>).</summary>
    private static string FhirUrlToSmart(string url)
    {
        if (url.Contains("/fhir/", StringComparison.Ordinal))
        {
            return url.Replace("/fhir/", "/_smart/");
        }

        if (url.EndsWith("/fhir", StringComparison.Ordinal))
        {
            return url[..^5] + "/_smart";
        }

        return url.EndsWith('/') ? url + "_smart" : url + "/_smart";
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

    /// <inheritdoc/>
    public bool SystemOperation(FhirRequestContext ctx, out FhirResponseContext response)
    {
        bool success = DoSystemOperation(ctx, out FhirResponseContext resp);
        response = SerializeResponse(ctx, resp);
        return success;
    }

    private bool DoSystemOperation(FhirRequestContext ctx, out FhirResponseContext response)
    {
        if (!TryGetOperationForLevel(ctx.OperationName, op => op.AllowSystemLevel, "system-level", out IFhirOperation? op, out response))
        {
            return false;
        }

        if (!TryDeserializeOperationBody(ctx, op, out ResourceJsonNode? body, out response))
        {
            return false;
        }

        bool success = op.DoOperation(ctx, this, null, null, body, out FhirResponseContext opResponse);
        response = BuildOperationResponse(opResponse, success, $"System-Level Operation {ctx.OperationName}");
        return success;
    }

    /// <inheritdoc/>
    public bool TypeOperation(FhirRequestContext ctx, out FhirResponseContext response)
    {
        bool success = DoTypeOperation(ctx, out FhirResponseContext resp);
        response = SerializeResponse(ctx, resp);
        return success;
    }

    private bool DoTypeOperation(FhirRequestContext ctx, out FhirResponseContext response)
    {
        if (!_store.TryGetValue(ctx.ResourceType, out ResourceStore? rs))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(
                    HttpStatusCode.NotFound,
                    $"Resource type {ctx.ResourceType} does not exist on this server.",
                    OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        if (!TryGetOperationForLevel(ctx.OperationName, op => op.AllowResourceLevel, "type-level", out IFhirOperation? op, out response))
        {
            return false;
        }

        if (!TryCheckSupportedResource(op, ctx.ResourceType, out response))
        {
            return false;
        }

        if (!TryDeserializeOperationBody(ctx, op, out ResourceJsonNode? body, out response))
        {
            return false;
        }

        bool success = op.DoOperation(ctx, this, rs, null, body, out FhirResponseContext opResponse);
        response = BuildOperationResponse(opResponse, success, $"Type-Level Operation {ctx.ResourceType}/{ctx.OperationName}");
        return success;
    }

    /// <inheritdoc/>
    public bool InstanceOperation(FhirRequestContext ctx, out FhirResponseContext response)
    {
        bool success = DoInstanceOperation(ctx, out FhirResponseContext resp);
        response = SerializeResponse(ctx, resp);
        return success;
    }

    private bool DoInstanceOperation(FhirRequestContext ctx, out FhirResponseContext response)
    {
        if (!_store.TryGetValue(ctx.ResourceType, out ResourceStore? rs))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(
                    HttpStatusCode.NotFound,
                    $"Resource type {ctx.ResourceType} does not exist on this server.",
                    OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        ResourceJsonNode? focus = string.IsNullOrEmpty(ctx.Id) ? null : rs.InstanceRead(ctx.Id);

        if (focus is null)
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(
                    HttpStatusCode.NotFound,
                    $"Instance {ctx.ResourceType}/{ctx.Id} does not exist on this server."),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        if (!TryGetOperationForLevel(ctx.OperationName, op => op.AllowInstanceLevel, "instance-level", out IFhirOperation? op, out response))
        {
            return false;
        }

        if (!TryCheckSupportedResource(op, ctx.ResourceType, out response))
        {
            return false;
        }

        if (!TryDeserializeOperationBody(ctx, op, out ResourceJsonNode? body, out response))
        {
            return false;
        }

        bool success = op.DoOperation(ctx, this, rs, focus, body, out FhirResponseContext opResponse);
        response = BuildOperationResponse(opResponse, success, $"Instance-Level Operation {ctx.ResourceType}/{ctx.Id}/{ctx.OperationName}");
        return success;
    }

    /// <summary>Looks up <paramref name="operationName"/> in <see cref="_operations"/> and checks
    /// <paramref name="levelAllowed"/> against it, building the shared NotFound/UnprocessableEntity
    /// <see cref="FhirResponseContext"/> for either failure. Shared by <see cref="DoSystemOperation"/>,
    /// <see cref="DoTypeOperation"/>, and <see cref="DoInstanceOperation"/>, which otherwise differ only
    /// in which <see cref="IFhirOperation"/> level flag they check and what to call it in the error
    /// message.</summary>
    private bool TryGetOperationForLevel(
        string operationName,
        Func<IFhirOperation, bool> levelAllowed,
        string levelDescription,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IFhirOperation? op,
        out FhirResponseContext response)
    {
        if (!_operations.TryGetValue(operationName, out op))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(
                    HttpStatusCode.NotFound,
                    $"Operation {operationName} does not have an executable implementation on this server."),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        if (!levelAllowed(op))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(
                    HttpStatusCode.UnprocessableEntity,
                    $"Operation {operationName} does not allow {levelDescription} execution.",
                    OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.UnprocessableEntity,
            };
            op = null;
            return false;
        }

        response = new();
        return true;
    }

    /// <summary>Checks <paramref name="op"/>'s <see cref="IFhirOperation.SupportedResources"/> against
    /// <paramref name="resourceType"/>, building the shared UnprocessableEntity response on mismatch.
    /// Shared by <see cref="DoTypeOperation"/> and <see cref="DoInstanceOperation"/> (system-level
    /// operations have no resource type to check against).</summary>
    private static bool TryCheckSupportedResource(IFhirOperation op, string resourceType, out FhirResponseContext response)
    {
        if (op.SupportedResources.Count > 0 && !op.SupportedResources.Contains(resourceType))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(
                    HttpStatusCode.UnprocessableEntity,
                    $"Operation {op.OperationName} is not defined for resource: {resourceType}.",
                    OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.UnprocessableEntity,
            };
            return false;
        }

        response = new();
        return true;
    }

    /// <summary>Projects an operation's own <paramref name="opResponse"/> into the final <see
    /// cref="FhirResponseContext"/>, applying the <c>StatusCode ?? (success ? OK : InternalServerError)</c>
    /// defaulting exactly once instead of once per dispatch level. Shared by <see
    /// cref="DoSystemOperation"/>, <see cref="DoTypeOperation"/>, and <see cref="DoInstanceOperation"/>.</summary>
    private static FhirResponseContext BuildOperationResponse(FhirResponseContext opResponse, bool success, string label)
    {
        HttpStatusCode statusCode = opResponse.StatusCode ?? (success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);

        return new()
        {
            Resource = opResponse.Resource,
            ResourceType = opResponse.ResourceType,
            Id = opResponse.Id,
            Outcome = opResponse.Outcome ?? SerializationUtils.BuildOutcomeForRequest(
                statusCode,
                $"{label} {(success ? "succeeded" : "failed")}"),
            StatusCode = statusCode,
        };
    }

    /// <summary>Resolves the body content for an operation dispatch. Unlike <see
    /// cref="TryDeserializeSource"/>, an empty body is not an error here - many operations (GET
    /// requests, no-arg POSTs like <c>$reset-store</c>) have none. When content is present but not
    /// parseable as FHIR, the operation's <see cref="IFhirOperation.AcceptsNonFhir"/> flag decides
    /// whether that is tolerated.</summary>
    private bool TryDeserializeOperationBody(
        FhirRequestContext ctx,
        IFhirOperation op,
        out ResourceJsonNode? body,
        out FhirResponseContext response)
    {
        if (ctx.SourceObject is ResourceJsonNode direct)
        {
            body = direct;
            response = new();
            return true;
        }

        if (string.IsNullOrEmpty(ctx.SourceContent))
        {
            body = null;
            response = new();
            return true;
        }

        HttpStatusCode sc = SerializationUtils.TryDeserializeFhir(ctx.SourceContent, ctx.SourceFormat, out body, out string exMessage, _schema);

        if (sc == HttpStatusCode.OK && body is not null)
        {
            response = new();
            return true;
        }

        if (op.AcceptsNonFhir)
        {
            body = null;
            response = new();
            return true;
        }

        response = new()
        {
            Outcome = SerializationUtils.BuildOutcomeForRequest(
                HttpStatusCode.UnsupportedMediaType,
                string.IsNullOrEmpty(exMessage)
                    ? $"Operation {ctx.OperationName} does not consume non-FHIR content."
                    : $"Operation {ctx.OperationName} does not consume non-FHIR content.\n\nError:\n{exMessage}",
                OperationOutcomeJsonNode.IssueType.Invalid),
            StatusCode = HttpStatusCode.UnsupportedMediaType,
        };
        return false;
    }

    /// <inheritdoc/>
    public bool CompartmentSearch(FhirRequestContext ctx, out FhirResponseContext response)
    {
        bool success = DoCompartmentSearch(ctx, out response);
        response = SerializeResponse(ctx, response);
        return success;
    }

    private bool DoCompartmentSearch(FhirRequestContext ctx, out FhirResponseContext response)
    {
        string searchQueryParams = string.IsNullOrEmpty(ctx.SourceContent) || (ctx.SourceFormat != "application/x-www-form-urlencoded")
            ? ctx.UrlQuery
            : ctx.SourceContent;

        if (string.IsNullOrEmpty(ctx.CompartmentType))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.BadRequest, "Compartment type is required for compartment search interactions", OperationOutcomeJsonNode.IssueType.Structure),
                StatusCode = HttpStatusCode.BadRequest,
            };
            return false;
        }

        if (!_store.ContainsKey(ctx.CompartmentType))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Compartment Resource type: {ctx.CompartmentType} is not supported", OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        if (!_compartments.TryGetValue(ctx.CompartmentType, out ParsedCompartment? compartment))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Compartment type: {ctx.CompartmentType} is not supported", OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        var matches = new List<ResourceJsonNode>();

        foreach ((string resourceType, ParsedCompartment.IncludedResource ir) in compartment.IncludedResources)
        {
            if (!_store.TryGetValue(resourceType, out ResourceStore? rs))
            {
                continue;
            }

            ParsedQuery typeQuery = _search.ParseQuery(resourceType, searchQueryParams);
            List<ResourceJsonNode> typeMatches = rs.TypeSearch(typeQuery)
                .Where(r => IsInCompartment(r, ir, ctx.CompartmentType, ctx.Id))
                .ToList();
            matches.AddRange(ApplyChainedExpressions(typeMatches, typeQuery));
        }

        if (ctx.Authorization is not null)
        {
            matches = FilterSearchResultsForAuth(ctx, matches);
        }

        // parsed once more against the compartment type itself for aggregate-level result options
        // (sort/include/revinclude/summary/max-count), matching how the old file scoped these to the
        // compartment resource's own store rather than any single matched type.
        ParsedQuery aggregateQuery = _search.ParseQuery(ctx.CompartmentType, searchQueryParams);

        if (aggregateQuery.Options.Sort.Count > 0)
        {
            var comparer = new FhirSortComparer(_schema, aggregateQuery.Options.Sort, _search.Definitions);
            matches = matches.OrderBy(m => m, comparer).ToList();
        }

        string selfLink = $"{GetBaseUrl(ctx)}/{ctx.CompartmentType}/{ctx.Id}/*";
        if (!string.IsNullOrEmpty(searchQueryParams))
        {
            selfLink = selfLink + "?" + searchQueryParams.TrimStart('?');
        }

        BundleJsonNode bundle = BuildSearchBundle(ctx, aggregateQuery, matches, selfLink);

        response = new()
        {
            Resource = bundle,
            ResourceType = "Bundle",
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, "Compartment search successful"),
            StatusCode = HttpStatusCode.OK,
        };
        return true;
    }

    /// <inheritdoc/>
    public bool CompartmentTypeSearch(FhirRequestContext ctx, out FhirResponseContext response)
    {
        bool success = DoCompartmentTypeSearch(ctx, out response);
        response = SerializeResponse(ctx, response);
        return success;
    }

    private bool DoCompartmentTypeSearch(FhirRequestContext ctx, out FhirResponseContext response)
    {
        string searchQueryParams = string.IsNullOrEmpty(ctx.SourceContent) || (ctx.SourceFormat != "application/x-www-form-urlencoded")
            ? ctx.UrlQuery
            : ctx.SourceContent;

        if (string.IsNullOrEmpty(ctx.CompartmentType))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.BadRequest, "Compartment type is required for compartment type search interactions", OperationOutcomeJsonNode.IssueType.Structure),
                StatusCode = HttpStatusCode.BadRequest,
            };
            return false;
        }

        if (!_store.ContainsKey(ctx.CompartmentType))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Compartment Resource type: {ctx.CompartmentType} is not supported", OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        if (!_compartments.TryGetValue(ctx.CompartmentType, out ParsedCompartment? compartment))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Compartment type: {ctx.CompartmentType} is not supported", OperationOutcomeJsonNode.IssueType.NotSupported),
                StatusCode = HttpStatusCode.NotFound,
            };
            return false;
        }

        if (string.IsNullOrEmpty(ctx.ResourceType))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.BadRequest, "Resource type is required for compartment type search interactions", OperationOutcomeJsonNode.IssueType.Structure),
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

        if (!compartment.IncludedResources.TryGetValue(ctx.ResourceType, out ParsedCompartment.IncludedResource? ir))
        {
            response = new()
            {
                Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.NotFound, $"Resource type: {ctx.ResourceType} is not supported in compartment {ctx.CompartmentType}", OperationOutcomeJsonNode.IssueType.NotSupported),
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

        List<ResourceJsonNode> matches = ApplyChainedExpressions(
            rs.TypeSearch(query).Where(r => IsInCompartment(r, ir, ctx.CompartmentType, ctx.Id)).ToList(),
            query);

        if (ctx.Authorization is not null)
        {
            matches = FilterSearchResultsForAuth(ctx, matches);
        }

        if (query.Options.Sort.Count > 0)
        {
            var comparer = new FhirSortComparer(_schema, query.Options.Sort, _search.Definitions);
            matches = matches.OrderBy(m => m, comparer).ToList();
        }

        string selfLink = $"{GetBaseUrl(ctx)}/{ctx.CompartmentType}/{ctx.Id}/{ctx.ResourceType}";
        if (!string.IsNullOrEmpty(searchQueryParams))
        {
            selfLink = selfLink + "?" + searchQueryParams.TrimStart('?');
        }

        BundleJsonNode bundle = BuildSearchBundle(ctx, query, matches, selfLink);

        response = new()
        {
            Resource = bundle,
            ResourceType = "Bundle",
            Outcome = SerializationUtils.BuildOutcomeForRequest(HttpStatusCode.OK, "Compartment type search successful"),
            StatusCode = HttpStatusCode.OK,
        };
        return true;
    }

    /// <summary>Tests whether <paramref name="resource"/> is a member of the compartment identified by
    /// <paramref name="compartmentType"/>/<paramref name="compartmentId"/>: true if the resource's own
    /// identity matches (the special <c>_id</c> membership code, from a CompartmentDefinition's
    /// <c>{def}</c> parameter), or if any of <paramref name="includedResource"/>'s other search param
    /// codes has a <see cref="ReferenceSearchValue"/> index entry pointing at the compartment instance
    /// (the same index-scan approach <see cref="SearchExecutor"/> uses for <c>_revinclude</c>/<c>_has</c>).</summary>
    private bool IsInCompartment(
        ResourceJsonNode resource,
        ParsedCompartment.IncludedResource includedResource,
        string compartmentType,
        string compartmentId)
    {
        if (includedResource.SearchParamCodes.Contains("_id") &&
            string.Equals(resource.ResourceType, compartmentType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(resource.Id, compartmentId, StringComparison.Ordinal))
        {
            return true;
        }

        IReadOnlyCollection<SearchIndexEntry> index = _search.Index(resource.ToElement(_schema));

        return includedResource.SearchParamCodes.Any(code =>
            code != "_id" &&
            index.Any(entry =>
                entry.Value is ReferenceSearchValue reference &&
                string.Equals(entry.SearchParameter.Code, code, StringComparison.Ordinal) &&
                string.Equals(reference.ResourceType, compartmentType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(reference.ResourceId, compartmentId, StringComparison.Ordinal)));
    }

    /// <inheritdoc/>
    public bool RegisterCompartmentDefinition(object compartmentDefinition)
    {
        if (compartmentDefinition is not ResourceJsonNode { ResourceType: "CompartmentDefinition" } node)
        {
            return false;
        }

        ParsedCompartment parsed;
        try
        {
            parsed = new ParsedCompartment(node.ToElement(_schema));
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!_store.ContainsKey(parsed.CompartmentType))
        {
            return false;
        }

        _compartments[parsed.CompartmentType] = parsed;
        return true;
    }

    /// <inheritdoc/>
    public void RemoveCompartmentDefinition(string compartmentType) => _compartments.Remove(compartmentType);

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

    /// <inheritdoc/>
    public IEnumerable<ParsedSubscriptionTopic> CurrentTopics => _topics.Values;

    /// <inheritdoc/>
    public IEnumerable<ParsedSubscription> CurrentSubscriptions => _subscriptions.Values;

    /// <inheritdoc/>
    public ConcurrentDictionary<string, List<ParsedSubscriptionStatus>> ReceivedNotifications { get; } = new();

    /// <summary>Attempts to get the parsed (tracked) subscription for an id.</summary>
    internal bool TryGetParsedSubscription(string subscriptionId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ParsedSubscription? subscription) =>
        _subscriptions.TryGetValue(subscriptionId, out subscription);

    private bool ValidateSubscriptionTopicResource(ResourceJsonNode source, out string? errorMessage)
    {
        if (_topicConverter.TryParse(source, out _))
        {
            errorMessage = null;
            return true;
        }

        errorMessage = source.ResourceType == "Basic"
            ? "Basic-wrapped SubscriptionTopic could not be parsed!"
            : "SubscriptionTopic could not be parsed!";
        return false;
    }

    private bool ValidateSubscriptionResource(ResourceJsonNode source, out string? errorMessage)
    {
        if (_subscriptionConverter.TryParse(source, out ParsedSubscription _))
        {
            errorMessage = null;
            return true;
        }

        errorMessage = "Subscription could not be parsed!";
        return false;
    }

    private void ProcessSubscriptionTopicResource(ResourceJsonNode resource, bool remove)
    {
        if (_topicConverter.TryParse(resource, out ParsedSubscriptionTopic topic))
        {
            _ = StoreProcessSubscriptionTopic(topic, remove);
        }
    }

    private void ProcessSubscriptionResource(ResourceJsonNode resource, bool remove)
    {
        if (_subscriptionConverter.TryParse(resource, out ParsedSubscription subscription))
        {
            _ = StoreProcessSubscription(subscription, remove);
        }
    }

    /// <summary>Registers (or removes) a subscription topic and rebuilds the executable trigger
    /// definitions on every per-resource-type store it applies to. Returns true if the topic has at
    /// least one executable trigger.</summary>
    public bool StoreProcessSubscriptionTopic(ParsedSubscriptionTopic topic, bool remove = false)
    {
        if (remove)
        {
            if (!_topics.TryRemove(topic.Url, out _))
            {
                return false;
            }

            foreach (ResourceStore rs in _store.Values)
            {
                rs.RemoveExecutableSubscriptionTopic(topic.Url);
            }

            return true;
        }

        bool priorExisted = _topics.ContainsKey(topic.Url);
        _topics[topic.Url] = topic;

        if (topic.ResourceTriggers.Count == 0)
        {
            foreach (ResourceStore rs in _store.Values)
            {
                rs.RemoveExecutableSubscriptionTopic(topic.Url);
            }

            return false;
        }

        bool canExecute = false;

        foreach ((string resourceName, ResourceStore rs) in _store)
        {
            if (!topic.ResourceTriggers.ContainsKey(resourceName))
            {
                if (priorExisted)
                {
                    rs.RemoveExecutableSubscriptionTopic(topic.Url);
                }

                continue;
            }

            var interactionTriggers = new List<ExecutableSubscriptionInfo.InteractionOnlyTrigger>();
            var fhirPathTriggers = new List<ExecutableSubscriptionInfo.FhirPathTrigger>();
            var queryTriggers = new List<ExecutableSubscriptionInfo.QueryTrigger>();
            ParsedQuery? additionalContext = null;

            foreach (string key in (string[])[resourceName, "*", "Resource"])
            {
                if (topic.ResourceTriggers.TryGetValue(key, out List<ParsedSubscriptionTopic.ResourceTrigger>? triggers))
                {
                    foreach (ParsedSubscriptionTopic.ResourceTrigger trigger in triggers)
                    {
                        bool onCreate = trigger.OnCreate;
                        bool onUpdate = trigger.OnUpdate;
                        bool onDelete = trigger.OnDelete;

                        // not filled out means trigger on any interaction
                        if (!onCreate && !onUpdate && !onDelete)
                        {
                            onCreate = true;
                            onUpdate = true;
                            onDelete = true;
                        }

                        // prefer FHIRPath if present
                        if (!string.IsNullOrEmpty(trigger.FhirPathCriteria))
                        {
                            fhirPathTriggers.Add(new(onCreate, onUpdate, onDelete, trigger.FhirPathCriteria));
                            continue;
                        }

                        if (!string.IsNullOrEmpty(trigger.QueryPrevious) || !string.IsNullOrEmpty(trigger.QueryCurrent))
                        {
                            queryTriggers.Add(new(
                                onCreate,
                                onUpdate,
                                onDelete,
                                string.IsNullOrEmpty(trigger.QueryPrevious) ? null : _search.ParseQuery(resourceName, trigger.QueryPrevious),
                                trigger.CreateAutoFail,
                                trigger.CreateAutoPass,
                                string.IsNullOrEmpty(trigger.QueryCurrent) ? null : _search.ParseQuery(resourceName, trigger.QueryCurrent),
                                trigger.DeleteAutoFail,
                                trigger.DeleteAutoPass,
                                trigger.RequireBothQueries));
                            continue;
                        }

                        interactionTriggers.Add(new(onCreate, onUpdate, onDelete));
                    }
                }

                if (additionalContext is null &&
                    topic.NotificationShapes.TryGetValue(key, out List<ParsedSubscriptionTopic.NotificationShape>? shapes) &&
                    shapes.Count != 0)
                {
                    // use the first matching shape, mirroring the old engine
                    ParsedSubscriptionTopic.NotificationShape shape = shapes[0];
                    string includeQuery = string.Join('&', (shape.Includes ?? []).Concat(shape.ReverseIncludes ?? []));

                    if (!string.IsNullOrEmpty(includeQuery))
                    {
                        additionalContext = TryParseNotificationShapeQuery(resourceName, includeQuery);
                    }
                }
            }

            if (interactionTriggers.Count != 0 || fhirPathTriggers.Count != 0 || queryTriggers.Count != 0)
            {
                rs.SetExecutableSubscriptionTopic(topic.Url, interactionTriggers, fhirPathTriggers, queryTriggers, additionalContext);
                canExecute = true;
            }
            else
            {
                rs.RemoveExecutableSubscriptionTopic(topic.Url);
            }
        }

        // wire up any subscriptions that arrived before their topic (replaces the old engine's
        // load-state reprocess queue)
        foreach (ParsedSubscription pending in _subscriptions.Values.Where(s => s.TopicUrl == topic.Url))
        {
            _ = StoreProcessSubscription(pending);
        }

        return canExecute;
    }

    /// <summary>Parses a notification-shape include/revinclude query, tolerating segments the search
    /// engine rejects (e.g. dotted <c>iterate=Patient.link</c> continuations, which the subscriptions
    /// samples use but Ignixa's include parser does not accept) by dropping just those segments.</summary>
    private ParsedQuery? TryParseNotificationShapeQuery(string resourceName, string queryString)
    {
        try
        {
            return _search.ParseQuery(resourceName, queryString);
        }
        catch (Exception)
        {
            var parseable = queryString.Split('&').Where(segment =>
            {
                try
                {
                    _ = _search.ParseQuery(resourceName, segment);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }).ToList();

            return parseable.Count == 0 ? null : _search.ParseQuery(resourceName, string.Join('&', parseable));
        }
    }

    /// <summary>Registers (or removes) a subscription: records it in the tracked set and pushes its
    /// per-resource-type filters into every store its topic triggers on. Returns false when the topic
    /// is not (yet) known - the subscription stays tracked and is wired up if the topic arrives.</summary>
    public bool StoreProcessSubscription(ParsedSubscription subscription, bool remove = false)
    {
        if (remove)
        {
            if (!_subscriptions.ContainsKey(subscription.Id))
            {
                return false;
            }

            foreach (ResourceStore rs in _store.Values)
            {
                rs.RemoveExecutableSubscription(subscription.TopicUrl, subscription.Id);
            }

            _ = _subscriptions.TryRemove(subscription.Id, out _);

            RegisterSubscriptionsChanged(subscription, removed: true);
            return true;
        }

        bool priorExisted = _subscriptions.TryGetValue(subscription.Id, out ParsedSubscription? prior);
        string priorState = priorExisted ? prior!.CurrentStatus : "off";

        _subscriptions[subscription.Id] = subscription;

        if (!_topics.TryGetValue(subscription.TopicUrl, out ParsedSubscriptionTopic? topic))
        {
            return false;
        }

        foreach ((string resourceName, ResourceStore rs) in _store)
        {
            if (!topic.ResourceTriggers.ContainsKey(resourceName))
            {
                continue;
            }

            var filterSegments = new List<string>();

            foreach (string key in (string[])[resourceName, "*", "Resource"])
            {
                if (!subscription.Filters.TryGetValue(key, out List<ParsedSubscription.SubscriptionFilter>? filters))
                {
                    continue;
                }

                foreach (ParsedSubscription.SubscriptionFilter filter in filters)
                {
                    string modifier = string.IsNullOrEmpty(filter.Modifier) ? string.Empty : ":" + filter.Modifier;
                    filterSegments.Add($"{filter.Name}{modifier}={filter.Comparator}{filter.Value}");
                }
            }

            rs.SetExecutableSubscription(
                subscription.TopicUrl,
                subscription.Id,
                filterSegments.Count == 0 ? null : _search.ParseQuery(resourceName, string.Join('&', filterSegments)));
        }

        RegisterSubscriptionsChanged(subscription, removed: false, sendHandshake: priorState.Equals("off", StringComparison.Ordinal));
        return true;
    }

    /// <summary>Raises <see cref="OnSubscriptionsChanged"/>.</summary>
    public void RegisterSubscriptionsChanged(ParsedSubscription? subscription, bool removed = false, bool sendHandshake = false) =>
        OnSubscriptionsChanged?.Invoke(this, new()
        {
            Tenant = _config,
            ChangedSubscription = subscription,
            RemovedSubscriptionId = removed ? subscription?.Id : null,
            SendHandshake = sendHandshake,
        });

    /// <summary>Allocates the event number, resolves additional-context resources per the topic's
    /// notification shape, records the event on the subscription, and raises
    /// <see cref="OnSubscriptionSendEvent"/>.</summary>
    private void RegisterSendEvent(SubscriptionMatchedEventArgs matched)
    {
        if (!_subscriptions.TryGetValue(matched.SubscriptionId, out ParsedSubscription? subscription))
        {
            return;
        }

        var additionalContext = new List<object>();

        if (matched.AdditionalContext is not null)
        {
            var focusList = new List<ResourceJsonNode> { matched.Focus };
            var addedIds = new HashSet<string> { $"{matched.Focus.ResourceType}/{matched.Focus.Id}" };

            IEnumerable<ResourceJsonNode> inclusions = SearchExecutor
                .ResolveIncludes(focusList, matched.AdditionalContext.Options.Include, GetStore, _search, _schema)
                .Concat(SearchExecutor.ResolveRevIncludes(focusList, matched.AdditionalContext.Options.RevInclude, GetStore, _search, _schema));

            additionalContext.AddRange(inclusions.Where(r => addedIds.Add($"{r.ResourceType}/{r.Id}")));
        }

        var subscriptionEvent = new SubscriptionEvent
        {
            SubscriptionId = matched.SubscriptionId,
            TopicUrl = matched.TopicUrl,
            EventNumber = subscription.IncrementEventCount(),
            Focus = matched.Focus,
            AdditionalContext = additionalContext,
        };

        subscription.RegisterEvent(subscriptionEvent);

        OnSubscriptionSendEvent?.Invoke(this, new()
        {
            Tenant = _config,
            Subscription = subscription,
            NotificationEvents = [subscriptionEvent],
            NotificationType = ParsedSubscription.NotificationTypeCodes.EventNotification,
        });
    }

    /// <summary>Records a subscription error message.</summary>
    public void RegisterError(string subscriptionId, string errorMessage)
    {
        if (_subscriptions.TryGetValue(subscriptionId, out ParsedSubscription? subscription))
        {
            subscription.RegisterError(errorMessage);
        }
    }

    /// <summary>Gets (optionally incrementing) the event count for a subscription.</summary>
    public long GetSubscriptionEventCount(string subscriptionId, bool increment)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out ParsedSubscription? subscription))
        {
            return 0;
        }

        return increment ? subscription.IncrementEventCount() : subscription.CurrentEventCount;
    }

    /// <inheritdoc/>
    public void ChangeSubscriptionStatus(string id, string status)
    {
        if (!_subscriptions.TryGetValue(id, out ParsedSubscription? parsed))
        {
            return;
        }

        if (!_store.TryGetValue("Subscription", out ResourceStore? rs) ||
            rs.InstanceRead(id) is not { } stored)
        {
            return;
        }

        _subscriptionConverter.UpdateResourceStatus(stored, status);
        parsed.CurrentStatus = status;

        RegisterSubscriptionsChanged(parsed);
    }

    /// <inheritdoc/>
    public bool TryGetSubscription(ParsedSubscription parsed, out object? subscription)
    {
        if (_subscriptionConverter.TryParse(parsed, out ResourceJsonNode resource))
        {
            subscription = resource;
            return true;
        }

        subscription = null;
        return false;
    }

    /// <inheritdoc/>
    public bool TrySerializeToSubscription(ParsedSubscription subscriptionInfo, out string serialized, bool pretty, string destFormat = "application/fhir+json")
    {
        if (!_subscriptionConverter.TryParse(subscriptionInfo, out ResourceJsonNode resource))
        {
            serialized = string.Empty;
            return false;
        }

        if (string.IsNullOrEmpty(destFormat))
        {
            destFormat = "application/fhir+json";
        }

        serialized = SerializationUtils.SerializeFhir(resource, _schema, destFormat, pretty);
        return true;
    }

    /// <inheritdoc/>
    public string SerializeSubscriptionEvents(
        string subscriptionId, IEnumerable<long> eventNumbers, string notificationType, bool pretty, string contentType = "", string contentLevel = "")
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out ParsedSubscription? subscription))
        {
            return string.Empty;
        }

        BundleJsonNode? bundle = _subscriptionConverter.BundleForSubscriptionEvents(
            subscription, eventNumbers, notificationType, _config.BaseUrl, contentLevel);

        return bundle is null
            ? string.Empty
            : SerializationUtils.SerializeFhir(
                bundle,
                _schema,
                string.IsNullOrEmpty(contentType) ? subscription.ContentType : contentType,
                pretty);
    }

    /// <summary>Builds the notification bundle for one or more of a subscription's events.</summary>
    public BundleJsonNode? BundleForSubscriptionEvents(
        string subscriptionId, IEnumerable<long> eventNumbers, string notificationType, string contentLevel = "") =>
        _subscriptions.TryGetValue(subscriptionId, out ParsedSubscription? subscription)
            ? _subscriptionConverter.BundleForSubscriptionEvents(subscription, eventNumbers, notificationType, _config.BaseUrl, contentLevel)
            : null;

    /// <summary>Builds the notification status resource for a subscription (Parameters on R4,
    /// SubscriptionStatus on R4B/R5).</summary>
    public ResourceJsonNode? StatusForSubscription(string subscriptionId, string notificationType) =>
        _subscriptions.TryGetValue(subscriptionId, out ParsedSubscription? subscription)
            ? _subscriptionConverter.StatusForSubscription(subscription, notificationType, _config.BaseUrl)
            : null;

    /// <summary>Parses an inbound notification bundle's first-entry status resource.</summary>
    public ParsedSubscriptionStatus? ParseNotificationBundle(BundleJsonNode bundle) =>
        bundle.Entry.FirstOrDefault()?.Resource is { } statusResource &&
        _subscriptionConverter.TryParse(statusResource, bundle.Id, out ParsedSubscriptionStatus status)
            ? status
            : null;

    /// <summary>Records an inbound notification and raises <see cref="OnReceivedSubscriptionEvent"/>.</summary>
    public void RegisterReceivedNotification(string bundleId, ParsedSubscriptionStatus status)
    {
        List<ParsedSubscriptionStatus> notifications = ReceivedNotifications.GetOrAdd(status.SubscriptionReference, _ => []);
        notifications.Add(status);

        OnReceivedSubscriptionEvent?.Invoke(this, new()
        {
            Tenant = _config,
            BundleId = bundleId,
            Status = status,
        });
    }

    /// <summary>Raises <see cref="OnReceivedSubscriptionChanged"/>.</summary>
    public void RegisterReceivedSubscriptionChanged(string subscriptionReference, int cachedNotificationCount, bool removed) =>
        OnReceivedSubscriptionChanged?.Invoke(this, new()
        {
            Tenant = _config,
            SubscriptionReference = subscriptionReference,
            CurrentBundleCount = cachedNotificationCount,
            Removed = removed,
        });

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

    /// <summary>Func-shaped adapter over <see cref="TryResolveAsElement"/> for FHIRPath
    /// <c>resolve()</c> hooks (<see cref="Ignixa.FhirPath.Evaluation.FhirEvaluationContext.ElementResolver"/>).</summary>
    private IElement? ResolveElement(string reference) =>
        TryResolveAsElement(reference, out IElement? element) ? element : null;

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
