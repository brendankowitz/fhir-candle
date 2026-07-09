# Ignixa SDK Migration — Gaps Analysis & Implementation Plan

Repo: `E:\data\src\fhir-candle` (branch `feature/ignixa-sdk-migration`). Reference SDK source: `brendankowitz/ignixa-fhir@main` (local clone read in full where cited). Every claim below was verified against actual source in both repos.

---

# Part 1 — Gaps Analysis

## The crux finding: Ignixa has no POCOs — the resource model itself changes

**Ignixa does not use Firely POCOs and does not generate per-resource POCO classes.** Its resource model is a schemaless, mutable JSON-document model: every resource is an `Ignixa.Serialization.SourceNodes.ResourceJsonNode` — a thin wrapper over a `System.Text.Json.Nodes.JsonObject` (`MutableNode` is the single source of truth). There is no `Patient` or `Observation` class; only a few infrastructure wrappers exist (`BundleJsonNode`, `ParametersJsonNode`, `OperationOutcomeJsonNode`, `MetaJsonNode`, etc. in `src/Core/Ignixa.Serialization/Models/`). Typed navigation for FHIRPath/search/validation comes from wrapping (not converting) the JSON into read-only `Ignixa.Abstractions.IElement` trees via a per-version schema provider: `resource.ToSourceNavigator().ToElement(schema)`.

The `Ignixa.Extensions.FirelySdk5/6` packages were read file-by-file: they are **element-level adapters only** (`ITypedElement`/`ISourceNode` ⇄ `IElement`); there is **no POCO ⇄ ResourceJsonNode conversion in either direction**. So there is no shim that lets fhir-candle keep `Hl7.Fhir.Model.*` types while swapping engines — a "full swap" means the entire in-memory resource model changes: `ConcurrentDictionary<string, Resource>` becomes `ConcurrentDictionary<string, ResourceJsonNode>`, and every typed property access becomes JsonNode manipulation, FHIRPath `Select()`, or `IElement.Children()`.

**The compensating win**: Ignixa is single-assembly multi-version. One `FhirVersion` enum (`Stu3=30, R4=40, R4B=43, R5=50, R6=60` — `src/Core/Ignixa.Abstractions/FhirVersion.cs`) selects a generated `IFhirSchemaProvider` at runtime (`R4CoreSchemaProvider`, `R4BCoreSchemaProvider`, `R5CoreSchemaProvider`, ... in `src/Core/Ignixa.Specification/Generated/`, via `FhirVersion.GetSchemaProvider()`). fhir-candle's entire raison-d'être for the `FhirStore.CommonVersioned` shared-project trick — compiling the same C# three times against three Firely assemblies — **disappears**. The three `FhirCandle.R4/R4B/R5` assemblies, the `.projitems` import, the MSBuild `AddPackageAliases` ReferencePath hack, and every `extern alias candleR4/coreR4/...` collapse into one ordinary project with a `FhirVersion` field per tenant.

**Version-specific divergence survives as runtime branching**, not separate assemblies: the R4-backport vs R4B vs R5-native subscription machinery (today `SubscriptionConverter.cs`/`TopicConverter.cs` duplicated per project) becomes `switch (fhirVersion)` over JsonNode shapes in one file set.

## Explicit risk: Ignixa's project status

Ignixa's root `README.md` states: *"Project Status: Advanced Research / Reference Implementation … not a supported production product."* This is a decided migration, but the plan must treat the SDK as unstable: pin exact package versions via a single `$(IgnixaVersion)` MSBuild property, keep the source clone available for reference, and budget for upstreaming fixes or vendoring patched builds if a needed API is broken (most likely candidates, found during this analysis: no XML serializer, no `_summary` filter, incomplete chained/include evaluation in the in-memory interpreter, FHIRPath `memberOf()` terminology hook declared but "not yet implemented"). Each of these has an explicit build-custom task in Part 2. Also: all Ignixa packages target `net9.0;net10.0` only — fhir-candle must drop its `net8.0` TFM.

## Capability-by-capability gaps table

| # | Capability | Current (Firely) | Ignixa equivalent (exact package/type) | Gap or risk | Verdict |
|---|---|---|---|---|---|
| 1 | **Multi-version model / POCO shape** | Per-version `Hl7.Fhir.R4/R4B/R5` 6.2.1 POCO assemblies; `FhirStore.CommonVersioned.projitems` compiled 3×; `extern alias` + `AddPackageAliases` MSBuild hack (fhir-candle.csproj:140-161) | `Ignixa.Serialization` `ResourceJsonNode`/`BaseJsonNode`/`MetaJsonNode`; `Ignixa.Abstractions` `IElement`/`ISourceNavigator`/`FhirVersion`; `Ignixa.Specification` `{R4,R4B,R5,R6,STU3}CoreSchemaProvider : IFhirSchemaProvider` | Whole model swap; no compile-time typing; `SchemaAwareElement.Value` returns dates as strings (SchemaAwareElement.cs:173-190), so date handling stays string/DateTimeOffset-parse based (candle already hand-parses dates — `ParsedSearchParameter.TryParseFhirDate`) | **Partial — needs adapter code** (architecture rewrite, single-assembly consolidation) |
| 2 | **Serialization — JSON** | `FhirJsonDeserializer.BACKWARDSCOMPATIBLE/.OSTRICH`, `FhirJsonSerializer.Default` (SerializationUtils.cs:26-29) | `Ignixa.Serialization.JsonSourceNodeFactory`: `Parse(string/Stream/ReadOnlyMemory<byte>/JsonNode)`, `SerializeToString/Stream/Bytes(pretty)` — the SDK's core fast path | Ignixa parser is lenient by design (schemaless); candle's strict-vs-OSTRICH distinction maps to validating post-parse against schema when strict mode is on | **Direct replacement** |
| 3 | **Serialization — XML** | `FhirXmlDeserializer.BACKWARDSCOMPATIBLE/.OSTRICH`, `FhirXmlSerializer` (SerializationUtils.cs:63-69) | **None.** grep of `src/Core` for XML APIs hits only `Ignixa.NarrativeGenerator/Security/XhtmlSanitizer.cs` — JSON only | fhir-candle content-negotiates `application/fhir+xml` and its test suite asserts XML round-trips (`FhirStoreTests` via `XDocument`) | **No equivalent — build custom** (schema-guided XML⇄JsonObject converter; Task 3) |
| 4 | **Serialization — `_summary`/`_elements`** | `SerializationFilter.ForSummary/ForText/ForData/ForCount` filterFactory (SerializationUtils.cs:407-474) | None in serializer (no settings class; only `pretty` flag). Schema layer exposes `IType.InSummary` per element (`Ignixa.Abstractions/Structure/IType.cs`) | Must implement filtering as a JsonObject pruning pass driven by `IType` metadata | **No equivalent — build custom** (Task 4) |
| 5 | **FHIRPath** | `FhirPathCompiler` + `SymbolTable().AddStandardFP().AddFhirExtensions()`, `CompiledExpression.Invoke(PocoNode, FhirEvaluationContext)`, `%current/%previous` env vars, `ElementResolver`, `TerminologyService` (VersionedFhirStore.cs:62,235-236,888-904,6198-6221; ResourceStore.cs:1523-1627) | `Ignixa.FhirPath.Evaluation.TypedElementExtensions`: `Select/Scalar/Predicate/IsTrue(this IElement, string, EvaluationContext?)` with built-in AST + compiled-delegate caches; `EvaluationContext.WithEnvironmentVariable(name, element)` covers `%current/%previous`; `FhirEvaluationContext.ElementResolver` (`Func<string, IElement?>`) covers `resolve()` | `FhirEvaluationContext.TerminologyService` is `object?` and **"not yet implemented"** in the evaluator → FHIRPath `memberOf()` is a functional regression (used only via the terminology hook candle wires into every context; no core test depends on it) | **Direct replacement** (with documented `memberOf()` regression) |
| 6 | **Search evaluation** | Hand-written: `ParsedSearchParameter` (1,869 lines, hand-built `SearchParamDefinition` registry), `SearchTester.TestNode` ~250-case `"{type}-{modifier}-{pocoType}"` dispatch, 7 `Eval*Search.cs` evaluators switching on POCO element classes, `ModelInfo.SearchParameters` bootstrap | `Ignixa.Search` (ported from microsoft/fhir-server): `SearchParameterDefinitionManager(IFhirSchemaProvider, ILogger<>)` + generated per-version definitions (`Generated/R4SearchParameterDefinitions.g.cs`, 15,507 lines); `SearchIndexerFactory.CreateInstance(...)` → `ISearchIndexer.Extract(IElement)` → `SearchIndexEntry`; `QueryParameterParser.Parse(string)` → `SearchOptionsBuilder(expressionParser, spManager).Build(resourceType, parameters, schema)` → `SearchOptions.Expression.AcceptVisitor(new SearchQueryInterpreter(), default)` → `SearchPredicate` over `(ResourceKey, IReadOnlyCollection<SearchIndexEntry>)` tuples (canonical wiring: `src/Application/.../SearchOptionsBuilderFactory.cs:104-120`; usage: `src/DataLayer/.../FileBasedSearchService.cs:75`) | (a) token `:above/:below/:in/:not-in` and reference `:identifier` throw "not supported" in Ignixa (`SearchValueExpressionBuilderHelper.cs:205-211`, ExpressionParser.cs:149) — candle supports `:in/:not-in` (via `VsContains`) and `:identifier` today, so shims needed; (b) `SearchQueryInterpreter` defers chained/`_has`/`_include` to the data layer — candle must implement those over its stores; (c) semantics differences (e.g. Ignixa date `eq` = overlap per MS-FHIR convention) must be validated against candle's existing JSON-based test suite; (d) runtime custom SearchParameters supported via `SearchParameterDefinitionManager.AddNewSearchParameters(IReadOnlyCollection<IElement>)`/`DeleteSearchParameter(url)` | **Partial — needs adapter code** (Tasks 5-7) |
| 7 | **Sorting** | `FhirSortComparer : IComparer<Resource>` invoking compiled FHIRPath per comparison, comparing via `IFhirValueProvider.FhirValue` POCO switch | No sort executor in `Ignixa.Search.InMemory` (SortExpression parsed into `SearchOptions.Sort` only) | Re-implement comparer over `IElement.Value` (bool/int/decimal/string primitives) | **Partial — needs adapter code** (Task 7) |
| 8 | **Validation ($validate)** | POCO attribute validation: `target.Validate(...)` → `IReadOnlyCollection<CodedValidationException>` (OpValidate.cs:202, `Hl7.Fhir.Validation`) | `Ignixa.Validation`: `StructureDefinitionSchemaBuilder`/`StructureDefinitionSchemaResolver` → `CachedValidationSchemaResolver` → `ProfileAwareValidationSchemaResolver.ResolveForElement(element)`; `ValidationSchema.Validate(IElement, ValidationSettings, ValidationState?)` → `ValidationResult` with **`ToOperationOutcome()`** returning `OperationOutcomeJsonNode`; profile-aware via `meta.profile`; IG profiles via `PackageBackedValidator.Create(PackageValidationOptions)` | Strictly more capable than today's attribute validation; depth settings (Minimal/Compatibility/Spec/Full) need choosing | **Direct replacement** (Task 13) |
| 9 | **Package management** | `Firely.Fhir.Packages` 5.0.2 (`PackageClient`, `IPackageServer`, `PackageReference`, `DiskPackageCache` at `~/.fhir`) + ~2,000 home-grown lines in `_ForPackages/` (`FhirCiClient : IPackageServer` for build.fhir.org, manifest workarounds); `PackageReference` leaks through `IFhirPackageService` | `Ignixa.PackageManagement`: `NpmPackageLoader` (`NpmPackageLoaderOptions.RegistryUrl`, default `https://packages.simplifier.net`), `CompositePackageLoader`, `PackageCacheManager` (flat `{id}_{version}.tgz` cache), `PackageExtractor` → `PackageExtractionResult { Manifest, IReadOnlyList<ExtractedResource> }`; feeds Specification/Validation via `PackageBackedValidator` and `SearchParameterDefinitionManager.AddNewSearchParameters` | **No build.fhir.org CI-build support, no packages2 fallback, no `~/.fhir` cache-layout compatibility, no `dev`/`current` directive semantics.** candle's `FhirCiClient` must be ported off Firely types (it fabricates `PackageListing/Versions` today); registry list is configurable so packages.fhir.org works | **Partial — needs adapter code** (Task 16) |
| 10 | **Terminology service** | `StoreTerminologyService : Hl7...ITerminologyService`: ValueSet flattening → `VsContains` (backs token `:in/:not-in` + FHIRPath `memberOf()`); everything else `NotImplementedException` (StoreTerminologyService.cs:227-278) | `Ignixa.Validation.Abstractions.ITerminologyService` (`ValidateCodeAsync`, `LookupCodeAsync`, `ExpandValueSetAsync`, `TranslateCodeAsync`, `SubsumesAsync`) + `InMemoryTerminologyService(IValueSetProvider, ICodeSystemProvider?)` + generated per-version `*ValueSetProvider` | Ignixa's service validates against *definition* ValueSets; candle needs its store-content flattening (tenant-uploaded ValueSets) preserved — small port of `StoreProcessValueSet` to `IElement`. FHIRPath `memberOf()` hook regression per row 5 | **Partial — needs adapter code** (Task 12) |
| 11 | **Subscriptions / Topics** | Per-version `SubscriptionConverter`/`TopicConverter`/`ConverterUtils` (R4: backport extensions on `Subscription.Channel`/`Basic`; R4B: native topic + R4B `SubscriptionStatus`; R5: fully native); trigger eval via compiled FHIRPath with `%current/%previous`; notification `Bundle` building | **None** — Ignixa has no subscription engine (confirmed: no Subscription processing in `src/Application`) | Entire subsystem is candle-owned logic over POCO shapes; must be rewritten over JsonNode/`IElement` with `switch (FhirVersion)` branching replacing the three per-assembly copies. `%current/%previous` maps cleanly to `EvaluationContext.WithEnvironmentVariable` | **No equivalent — port candle code to new model** (Task 14) |
| 12 | **Compartments** | `ParsedCompartment(Hl7...CompartmentDefinition)`; `CoreCompartmentSource` = embedded per-version CompartmentDefinition JSON deserialized to POCOs | `Ignixa.Search.Definition.CompartmentDefinitionManager(FhirVersion)`: `TryGetSearchParams(resourceType, CompartmentType, out HashSet<string>)`, `TryGetResourceTypes(...)`, backed by generated definitions; `CompartmentType` enum in `Ignixa.Specification.ValueSets.Normative` | Built-in generated definitions replace candle's embedded JSON for core compartments; runtime-registered custom CompartmentDefinitions (candle supports POSTing them) need `ParsedCompartment` ported to parse `IElement` | **Partial — needs adapter code** (Task 11) |
| 13 | **Narrative generation** | Not used anywhere in fhir-candle (confirmed by inventory) | `Ignixa.NarrativeGenerator` exists (`FhirNarrativeGenerator.Create(ISchema)`, beta) | — | **Out of scope — N/A** (candle never generated narratives; do not add) |
| 14 | **CapabilityStatement / conformance** | Hand-built POCO graph + version enums `FHIRVersion.N4_0_1/...` (VersionedFhirStore.cs:6771-6948) | None reusable (Ignixa's conformance feature lives in its out-of-scope Application layer) | Mechanical rewrite to JsonObject building; `FullVersion` string comes from `IFhirSchemaProvider.FullVersion` ("4.0.1" etc.) | **No equivalent — port candle code** (Task 10) |
| 15 | **Bundles / transactions** | `Bundle.EntryComponent` POCO graph; `Base.EnumerateElements()` recursive reference rewriting; `DeepCopy()` | `BundleJsonNode` (`Type`, `Total`, `Link`, `Entry` typed views over JsonObject); deep copy = `JsonNode.DeepClone()`; reference rewriting = recursive JsonNode walk | Mechanical; the JsonNode walk is simpler than `EnumerateElements` | **Partial — needs adapter code** (Task 9) |
| 16 | **MCP tools** | Zero effective usage — one unused `using Hl7.Fhir.Model.CdsHooks;` (CommonCandleMcp.cs:2); all 9 tools talk strings via `IFhirStore` | n/a | Delete one using | **Direct replacement (trivial)** (Task 17) |
| 17 | **Controllers / host** | `FhirController.cs:13` and `SmartController.cs:12` each one unused `using Hl7.Fhir.Rest;`; `FhirStoreManager` uses `extern alias candleR4/R4B/R5` + `Firely.Fhir.Packages.PackageReference` | n/a (strings + `IFhirStore` already; `FhirStore.Common` confirmed zero-Firely, and `FhirRequestContext`/`FhirResponseContext` type-erase via `object`) | The `object`-based type-erasure boundary in `FhirStore.Common` survives unchanged — the host barely notices the swap once aliases go | **Direct replacement** (Task 17) |
| 18 | **UI editor components** | `FhirEditor.razor` (BlazorMonaco) + `ResourcePicker.razor` are pure string/JSON — safe. Three `FhirCandle.Ui.R4` pages bind POCOs from store internals: `davinci-cdex/TaskTable.razor`, `davinci-pas/PasWalkthroughR4.razor` (78 refs, builds a full `Claim` in code), `Subscriptions/UsCoreHti2ContentsR4.razor`. `FhirCandle.Ui.Versioned.shproj` is dead (imported by nothing) | `IElement` projections / `ResourceJsonNode` construction | Three-page rewrite; dead shared project deletion | **Partial — needs adapter code** (Task 18) |
| 19 | **Test infrastructure** | `Hl7.Fhir.R4/R4B/R5/R6` 6.2.1 with NuGet `Aliases="coreR4..."`; POCO-coupled: subscription sections of `R4Tests`/`R4BTests`/`R5Tests` (~300-400 lines each) + `CompartmentTests`; the rest (FhirStoreTests' 50 tests, StrictSearchHandlingTests, FromIssues, Config/Auth/Mcp/Routing tests) is raw JSON/HTTP and survives with using/alias edits | `ResourceJsonNode` construction from JSON strings; single `FhirCandle.Engine` assembly removes all `extern alias` | The JSON-based majority becomes the behavioral regression spec for the whole migration | **Partial — needs adapter code** (Task 19) |
| 20 | **`CandleClient` / `FhirWebSerializer`** | `Client/CandleClient.cs` wraps `Hl7.Fhir.Rest.FhirClient` but is `<Compile Remove>`d from all three builds; `FhirWebSerializer.cs` likewise excluded | n/a | Dead code | **Out of scope — delete** (Task 20) |
| 21 | **Ignixa SqlOnFhir / DataLayer / Application layers** | n/a | `Ignixa.SqlOnFhir*` (SQL-on-FHIR v2 analytics), `src/DataLayer/*` (file/SQL/blob storage), `src/Application/*` (full reference server) | fhir-candle keeps its own in-memory `VersionedFhirStore`; these are Ignixa's own server stack ("internal component" per READMEs). `FileBasedSearchService.cs` is retained purely as the canonical wiring example for the search pipeline | **Out of scope** (one adoption: copy its predicate-application pattern) |
| 22 | **Ignixa.FhirMappingLanguage / DeId / TestScript* / FhirFakes / SqlOnFhir.Writers / Analyzers** | n/a | exist | No fhir-candle feature maps to them | **Out of scope — N/A** |

---

# Part 2 — Migration Plan

# Ignixa SDK Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace every Firely .NET SDK dependency (`Hl7.Fhir.R4/R4B/R5/R6` 6.2.1, `Firely.Fhir.Packages` 5.0.2) in fhir-candle with the Ignixa FHIR SDK, collapsing the three per-version store assemblies into one.

**Architecture:** A new single project `src/FhirCandle.Engine/` (namespaces preserved: `FhirCandle.Storage`, `FhirCandle.Serialization`, `FhirCandle.Search`, ...) replaces `FhirStore.CommonVersioned` + `FhirStore.R4/R4B/R5`. Resources are stored as `ResourceJsonNode`; each tenant's `VersionedFhirStore` holds a `FhirVersion` + `IFhirSchemaProvider` and delegates FHIRPath to `Ignixa.FhirPath`, search to `Ignixa.Search`'s index/expression/predicate pipeline (with candle-side shims for XML, `_summary`, sorting, chaining/includes, and token `:in/:not-in` + reference `:identifier`), and validation to `Ignixa.Validation`. The Firely-free `FhirStore.Common` contracts (`IFhirStore`, `FhirRequestContext`/`FhirResponseContext` with their `object` type-erasure) are unchanged, so controllers and MCP are untouched beyond deleting unused usings.

**Tech Stack:** OLD: Hl7.Fhir.R4/R4B/R5/R6 6.2.1, Firely.Fhir.Packages 5.0.2, net10/9/8. NEW: Ignixa.Abstractions, Ignixa.Serialization, Ignixa.FhirPath, Ignixa.Search, Ignixa.Specification, Ignixa.Validation, Ignixa.PackageManagement (all pinned to one `$(IgnixaVersion)`), net10.0;net9.0. Unchanged: xunit 2.9.3 + Shouldly 4.3.0, FluentUI 4.14.3, BlazorMonaco 3.5.0.

## Global Constraints

- **Zero references to `Hl7.Fhir.*` or `Firely.Fhir.Packages`** in any csproj, props, or source file at completion. Verification gate (Task 20): `grep -rn "Hl7.Fhir\|Firely" src --include=*.cs --include=*.csproj --include=*.props --include=*.razor` returns nothing.
- All Ignixa packages referenced through a single `<IgnixaVersion>` property in `src/fhir-candle.props`. At Task 1 Step 3, run `dotnet package search Ignixa.Serialization --exact-match` and pin the latest published version there; never float (`*`) — Ignixa is self-described research software.
- TFMs become `net10.0;net9.0` everywhere (`src/fhir-candle.props:7` currently `net10.0;net9.0;net8.0`) — Ignixa has no net8.0 target. This is a breaking change to the published dotnet tool; call it out in release notes.
- **Behavioral spec**: the JSON/HTTP-based test files (`FhirStoreTests.cs` 50 tests, `FhirStoreTestsR4/R4B/R5.cs`, `StrictSearchHandlingTests.cs`, `StrictModeStartupTests.cs`, `FromIssues.cs`, `ConfigTests.cs`, `AuthTests.cs`, `McpBasicTests.cs`, `ValidateRoutingTests.cs`) must pass with no assertion changes — only alias/using/plumbing edits are permitted in them. Where Ignixa search semantics differ (e.g. date `eq` overlap), candle behavior as asserted by these tests wins; add shims, don't relax tests.
- Search-section and subscription-section rewrites happen only in the four POCO-coupled test files (`R4Tests.cs`, `R4BTests.cs`, `R5Tests.cs`, `CompartmentTests.cs`).
- No Ignixa DataLayer/Application/SqlOnFhir packages may be referenced — core `Ignixa.*` libraries only.
- Commit after every task; keep `main` untouched; all work on `feature/ignixa-sdk-migration`.
- Known accepted regressions (document in README as part of Task 20, do not silently drop): FHIRPath `memberOf()` (Ignixa terminology hook not implemented), token `:above/:below` (was already a not-implemented stub in candle's SearchTester).

---

### Task 1: Spike — FhirCandle.Engine proves the model swap and single-assembly multi-version search

The riskiest architectural bets, proven first with real code: (a) `ResourceJsonNode` can serve as the storage type with candle's meta/versioning semantics, (b) one assembly can run the full Ignixa search pipeline for R4 and R5 simultaneously, (c) the pipeline construction order documented from `SearchOptionsBuilderFactory.cs:104-120` compiles and works against the published packages.

**Files:**
- Create: `src/FhirCandle.Engine/FhirCandle.Engine.csproj`
- Create: `src/FhirCandle.Engine/Schema/FhirSchemas.cs`
- Create: `src/FhirCandle.Engine.Tests/FhirCandle.Engine.Tests.csproj`
- Test: `src/FhirCandle.Engine.Tests/SpikeTests.cs`
- Modify: `fhir-candle.sln` (add both projects)

**Interfaces:**
- Consumes: `FhirCandle.Utils.FhirReleases.FhirSequenceCodes` (existing, `src/FhirStore.Common/Utils/FhirReleases.cs`)
- Produces: `FhirSchemas.Get(FhirSequenceCodes) : IFhirSchemaProvider` and `FhirSchemas.ToIgnixaVersion(FhirSequenceCodes) : FhirVersion` — every later task resolves schema providers through these two methods.

- [ ] Step 1: Write the failing test:

```csharp
// src/FhirCandle.Engine.Tests/SpikeTests.cs
using Ignixa.Abstractions;
using Ignixa.Search.Definition;
using Ignixa.Search.Expressions.Parsers;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.InMemory;
using Ignixa.Search.Parsing;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class SpikeTests
{
    private const string PatientJson =
        """{"resourceType":"Patient","id":"pat-1","active":true,"name":[{"use":"official","family":"Chalmers","given":["Peter"]}],"birthDate":"1974-12-25"}""";

    [Theory]
    [InlineData(FhirVersion.R4)]
    [InlineData(FhirVersion.R4B)]
    [InlineData(FhirVersion.R5)]
    public void ParseIndexSearchSerialize_SingleAssembly_AllVersions(FhirVersion version)
    {
        IFhirSchemaProvider schema = FhirCandle.Schema.FhirSchemas.GetByIgnixaVersion(version);

        ResourceJsonNode patient = JsonSourceNodeFactory.Parse(PatientJson);
        patient.Meta.VersionId = "1";
        patient.Meta.LastUpdated = DateTimeOffset.UtcNow;
        patient.ResourceType.ShouldBe("Patient");

        var spManager = new SearchParameterDefinitionManager(
            schema, NullLogger<SearchParameterDefinitionManager>.Instance);
        var indexer = SearchIndexerFactory.CreateInstance(schema, NullLoggerFactory.Instance, spManager);

        IElement element = patient.ToElement(schema);
        IReadOnlyCollection<SearchIndexEntry> index = indexer.Extract(element);
        index.ShouldNotBeEmpty();

        ISearchParameterDefinitionManager.SearchableSearchParameterDefinitionManagerResolver resolver =
            () => spManager;
        var expressionParser = new ExpressionParser(
            resolver,
            new SearchParameterExpressionParser(new ReferenceSearchValueParser(schema), schema),
            schema);
        var builder = new SearchOptionsBuilder(expressionParser, spManager);

        var query = new QueryParameterParser().Parse("birthdate=ge1970-01-01&name=Chalmers");
        var options = builder.Build("Patient", query, schema);
        options.Expression.ShouldNotBeNull();

        var predicate = options.Expression!.AcceptVisitor(new SearchQueryInterpreter(), default);
        var corpus = new[] { (new ResourceKey("Patient", "pat-1"), (IReadOnlyCollection<SearchIndexEntry>)index) };
        predicate(corpus).ShouldHaveSingleItem();

        var missQuery = new QueryParameterParser().Parse("birthdate=lt1970-01-01");
        var missOptions = builder.Build("Patient", missQuery, schema);
        var missPredicate = missOptions.Expression!.AcceptVisitor(new SearchQueryInterpreter(), default);
        missPredicate(corpus).ShouldBeEmpty();

        string roundTripped = patient.SerializeToString();
        roundTripped.ShouldContain("\"birthDate\":\"1974-12-25\"");
        roundTripped.ShouldContain("\"versionId\":\"1\"");
    }

    [Fact]
    public void SequenceCodeMapping_CoversR4R4BR5()
    {
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4).FullVersion.ShouldBe("4.0.1");
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4B).Version.ShouldBe(FhirVersion.R4B);
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R5).Version.ShouldBe(FhirVersion.R5);
    }
}
```

- [ ] Step 2: `dotnet test src/FhirCandle.Engine.Tests -f net10.0` — expect compile failure (`FhirCandle.Schema.FhirSchemas` does not exist yet; projects not created). Create the csprojs first so the failure is the missing class, not missing projects:

```xml
<!-- src/FhirCandle.Engine/FhirCandle.Engine.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\fhir-candle.props" />
  <ItemGroup>
    <PackageReference Include="Ignixa.Abstractions" Version="$(IgnixaVersion)" />
    <PackageReference Include="Ignixa.Serialization" Version="$(IgnixaVersion)" />
    <PackageReference Include="Ignixa.FhirPath" Version="$(IgnixaVersion)" />
    <PackageReference Include="Ignixa.Search" Version="$(IgnixaVersion)" />
    <PackageReference Include="Ignixa.Specification" Version="$(IgnixaVersion)" />
    <PackageReference Include="Ignixa.Validation" Version="$(IgnixaVersion)" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\FhirStore.Common\FhirCandle.Common.csproj" />
  </ItemGroup>
</Project>
```

(`FhirCandle.Engine.Tests.csproj`: xunit 2.9.3, Shouldly 4.3.0, ProjectReference to the engine; copy the pattern from `src/fhir-candle.Tests/fhir-candle.Tests.csproj` minus all Firely/alias content.)

- [ ] Step 3: Pin the version and implement the mapping. Run `dotnet package search Ignixa.Serialization --exact-match --format json`, take the newest version, add to `src/fhir-candle.props`: `<IgnixaVersion>X.Y.Z</IgnixaVersion>` (also change `<TargetFrameworks>` to `net10.0;net9.0` in the same edit — the engine can't target net8.0 and the props file is shared). Then:

```csharp
// src/FhirCandle.Engine/Schema/FhirSchemas.cs
using Ignixa.Abstractions;
using Ignixa.Specification.Extensions;
using FhirCandle.Utils;

namespace FhirCandle.Schema;

public static class FhirSchemas
{
    private static readonly Dictionary<FhirVersion, IFhirSchemaProvider> _providers =
        new()
        {
            [FhirVersion.R4] = FhirVersion.R4.GetSchemaProvider(),
            [FhirVersion.R4B] = FhirVersion.R4B.GetSchemaProvider(),
            [FhirVersion.R5] = FhirVersion.R5.GetSchemaProvider(),
        };

    public static FhirVersion ToIgnixaVersion(FhirReleases.FhirSequenceCodes sequence) => sequence switch
    {
        FhirReleases.FhirSequenceCodes.R4 => FhirVersion.R4,
        FhirReleases.FhirSequenceCodes.R4B => FhirVersion.R4B,
        FhirReleases.FhirSequenceCodes.R5 => FhirVersion.R5,
        _ => throw new NotSupportedException($"FHIR sequence {sequence} is not supported."),
    };

    public static IFhirSchemaProvider Get(FhirReleases.FhirSequenceCodes sequence) =>
        _providers[ToIgnixaVersion(sequence)];

    public static IFhirSchemaProvider GetByIgnixaVersion(FhirVersion version) => _providers[version];
}
```

- [ ] Step 4: `dotnet test src/FhirCandle.Engine.Tests -f net10.0` — expect `Passed! - Failed: 0, Passed: 4`. **Spike checkpoint**: if `SearchParameterDefinitionManager` needs an initialization call beyond its constructor, or predicate evaluation fails on the date prefix, fix it *here* and record the corrected wiring in this file — every later task copies this wiring. If a published package is broken vs. the source clone, stop and escalate the pin/vendor decision before proceeding.
- [ ] Step 5: `git add -A && git commit -m "Spike: FhirCandle.Engine single-assembly multi-version store core over Ignixa"`

---

### Task 2: SerializationUtils — JSON parse/serialize + OperationOutcome builders

**Files:**
- Create: `src/FhirCandle.Engine/Serialization/SerializationUtils.cs`
- Test: `src/FhirCandle.Engine.Tests/SerializationUtilsTests.cs`

**Interfaces:**
- Consumes: `FhirSchemas` (Task 1)
- Produces (later tasks call these exact signatures): `SerializationUtils.TryDeserializeFhir(string content, string format, out ResourceJsonNode? resource, out string exMessage) : HttpStatusCode`; `SerializationUtils.SerializeFhir(ResourceJsonNode instance, IFhirSchemaProvider schema, string format, bool pretty, string summaryFlag = "") : string`; `SerializationUtils.BuildOutcomeForRequest(HttpStatusCode sc, string message, OperationOutcomeJsonNode.IssueType issueType = OperationOutcomeJsonNode.IssueType.Processing) : OperationOutcomeJsonNode`

- [ ] Step 1: Write the failing test:

```csharp
// src/FhirCandle.Engine.Tests/SerializationUtilsTests.cs
using System.Net;
using FhirCandle.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;
using Shouldly;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class SerializationUtilsTests
{
    [Fact]
    public void TryDeserializeFhir_ValidJson_ReturnsOk()
    {
        var sc = SerializationUtils.TryDeserializeFhir(
            """{"resourceType":"Patient","id":"p1"}""", "application/fhir+json",
            out ResourceJsonNode? r, out _);
        sc.ShouldBe(HttpStatusCode.OK);
        r!.ResourceType.ShouldBe("Patient");
        r.Id.ShouldBe("p1");
    }

    [Fact]
    public void TryDeserializeFhir_Garbage_ReturnsUnsupportedMediaTypeMessage()
    {
        var sc = SerializationUtils.TryDeserializeFhir("not fhir", "application/fhir+json", out var r, out string msg);
        sc.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        r.ShouldBeNull();
        msg.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void BuildOutcome_ProducesIssueWithSeverityAndCode()
    {
        OperationOutcomeJsonNode oo = SerializationUtils.BuildOutcomeForRequest(
            HttpStatusCode.NotFound, "Resource not found",
            OperationOutcomeJsonNode.IssueType.NotFound);
        oo.ResourceType.ShouldBe("OperationOutcome");
        oo.Issue.Count.ShouldBe(1);
        oo.Issue[0].Code.ShouldBe(OperationOutcomeJsonNode.IssueType.NotFound);
        oo.Issue[0].Diagnostics.ShouldContain("Resource not found");
    }

    [Fact]
    public void SerializeFhir_Json_PrettyAndMinified()
    {
        SerializationUtils.TryDeserializeFhir("""{"resourceType":"Patient","id":"p1"}""",
            "json", out var r, out _);
        var schema = FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4);
        SerializationUtils.SerializeFhir(r!, schema, "application/fhir+json", pretty: false)
            .ShouldBe("""{"resourceType":"Patient","id":"p1"}""");
        SerializationUtils.SerializeFhir(r!, schema, "json", pretty: true).ShouldContain("\n");
    }
}
```

- [ ] Step 2: `dotnet test src/FhirCandle.Engine.Tests -f net10.0 --filter SerializationUtilsTests` — expect compile failure (class missing).
- [ ] Step 3: Implement. Port the format-sniffing/content-type mapping from the old `src/FhirStore.CommonVersioned/Serialization/SerializationUtils.cs:235-474` (keep its `format` literals: `json`, `xml`, `application/fhir+json`, `application/json`, `text/json`, and XML equivalents; sniff on leading `{` vs `<` as fallback, exactly as the old file does):

```csharp
// src/FhirCandle.Engine/Serialization/SerializationUtils.cs
using System.Net;
using Ignixa.Abstractions;
using Ignixa.Serialization;
using Ignixa.Serialization.Models;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Serialization;

public static class SerializationUtils
{
    public static HttpStatusCode TryDeserializeFhir(
        string content, string format, out ResourceJsonNode? resource, out string exMessage)
    {
        exMessage = string.Empty;
        switch (SniffFormat(format, content))
        {
            case "json":
                try
                {
                    resource = JsonSourceNodeFactory.Parse(content);
                    return string.IsNullOrEmpty(resource.ResourceType)
                        ? Fail(out resource, out exMessage, "Missing resourceType")
                        : HttpStatusCode.OK;
                }
                catch (Exception ex) { return Fail(out resource, out exMessage, ex.Message); }
            case "xml":
                // wired in Task 3 (FhirXml.Parse); until then:
                return Fail(out resource, out exMessage, "XML parsing not yet wired");
            default:
                return Fail(out resource, out exMessage, $"Unsupported format: {format}");
        }
    }

    public static string SerializeFhir(
        ResourceJsonNode instance, IFhirSchemaProvider schema, string format, bool pretty, string summaryFlag = "")
    {
        ResourceJsonNode toSerialize = instance;   // summaryFlag handling added in Task 4
        return SniffFormat(format, "{") switch
        {
            "json" => toSerialize.SerializeToString(pretty),
            "xml" => throw new NotSupportedException("wired in Task 3"),
            _ => toSerialize.SerializeToString(pretty),
        };
    }

    public static OperationOutcomeJsonNode BuildOutcomeForRequest(
        HttpStatusCode sc, string message,
        OperationOutcomeJsonNode.IssueType issueType = OperationOutcomeJsonNode.IssueType.Processing)
    {
        var oo = new OperationOutcomeJsonNode { Id = Guid.NewGuid().ToString() };
        var issue = new OperationOutcomeJsonNode.IssueComponent
        {
            Severity = ((int)sc >= 400)
                ? OperationOutcomeJsonNode.IssueSeverity.Error
                : OperationOutcomeJsonNode.IssueSeverity.Information,
            Code = issueType,
            Diagnostics = $"{message} (HTTP {(int)sc}: {sc})",
        };
        oo.Issue.Add(issue);
        return oo;
    }

    private static HttpStatusCode Fail(out ResourceJsonNode? resource, out string msg, string message)
    {
        resource = null;
        msg = message;
        return HttpStatusCode.UnsupportedMediaType;
    }

    private static string SniffFormat(string format, string content)
    {
        string f = format.Trim().ToLowerInvariant();
        if (f.Contains("json")) return "json";
        if (f.Contains("xml")) return "xml";
        return content.TrimStart().StartsWith('<') ? "xml" : "json";
    }
}
```

Also port `BuildOutcomeForStrictRule(s)` from the old file (same shapes, returning `OperationOutcomeJsonNode`) — the strict-mode tests exercise them via HTTP.
- [ ] Step 4: `dotnet test src/FhirCandle.Engine.Tests -f net10.0 --filter SerializationUtilsTests` — expect `Passed: 4`. (If `MutableJsonList<T>.Add` has a different name — verify against `Ignixa.Serialization/SourceNodes/MutableJsonList.cs` in the reference clone — adjust here.)
- [ ] Step 5: `git commit -m "Engine: JSON SerializationUtils and OperationOutcome builders over Ignixa"`

---

### Task 3: FhirXml — schema-guided XML ⇄ JsonObject converter

Ignixa has no XML support; fhir-candle's REST layer and test suite require `application/fhir+xml`. Build one converter using `IType` metadata (`IsCollection`, `Info.IsPrimitive`, `Info.IsResource`, `Children`, `Order`) from the schema provider.

**Files:**
- Create: `src/FhirCandle.Engine/Serialization/FhirXml.cs`
- Modify: `src/FhirCandle.Engine/Serialization/SerializationUtils.cs` (replace both Task-2 XML stubs)
- Test: `src/FhirCandle.Engine.Tests/FhirXmlTests.cs`

**Interfaces:**
- Produces: `FhirXml.Parse(string xml, IFhirSchemaProvider schema) : ResourceJsonNode`; `FhirXml.Serialize(ResourceJsonNode resource, IFhirSchemaProvider schema, bool pretty = false) : string`
- `SerializationUtils.TryDeserializeFhir` gains an optional trailing parameter: `IFhirSchemaProvider? schema = null` (required for XML content; JSON path ignores it). `SerializeFhir` already receives the schema.

- [ ] Step 1: Write the failing test:

```csharp
// src/FhirCandle.Engine.Tests/FhirXmlTests.cs
using FhirCandle.Serialization;
using Shouldly;
using System.Xml.Linq;
using Xunit;

namespace FhirCandle.Engine.Tests;

public class FhirXmlTests
{
    private static readonly Ignixa.Abstractions.IFhirSchemaProvider R4 =
        FhirCandle.Schema.FhirSchemas.Get(FhirCandle.Utils.FhirReleases.FhirSequenceCodes.R4);

    private const string PatientJson =
        """{"resourceType":"Patient","id":"p1","active":true,"name":[{"family":"Chalmers","given":["Peter","James"]}],"birthDate":"1974-12-25","text":{"status":"generated","div":"<div xmlns=\"http://www.w3.org/1999/xhtml\">ok</div>"}}""";

    [Fact]
    public void Serialize_ProducesFhirXmlShape()
    {
        SerializationUtils.TryDeserializeFhir(PatientJson, "json", out var patient, out _);
        string xml = FhirXml.Serialize(patient!, R4);
        XElement root = XElement.Parse(xml);
        XNamespace f = "http://hl7.org/fhir";
        root.Name.ShouldBe(f + "Patient");
        root.Element(f + "id")!.Attribute("value")!.Value.ShouldBe("p1");
        root.Element(f + "active")!.Attribute("value")!.Value.ShouldBe("true");
        root.Element(f + "name")!.Elements(f + "given").Count().ShouldBe(2);
        root.Element(f + "birthDate")!.Attribute("value")!.Value.ShouldBe("1974-12-25");
    }

    [Fact]
    public void RoundTrip_XmlToJsonToXml_IsStable()
    {
        SerializationUtils.TryDeserializeFhir(PatientJson, "json", out var patient, out _);
        string xml1 = FhirXml.Serialize(patient!, R4);
        var reparsed = FhirXml.Parse(xml1, R4);
        reparsed.ResourceType.ShouldBe("Patient");
        reparsed.Id.ShouldBe("p1");
        FhirXml.Serialize(reparsed, R4).ShouldBe(xml1);
    }

    [Fact]
    public void Parse_ContainedResourceAndChoiceType_Work()
    {
        const string obsXml =
            """<Observation xmlns="http://hl7.org/fhir"><id value="o1"/><contained><Patient><id value="cp"/></Patient></contained><status value="final"/><code><coding><system value="http://loinc.org"/><code value="8480-6"/></coding></code><valueQuantity><value value="120"/><unit value="mmHg"/></valueQuantity></Observation>""";
        var obs = FhirXml.Parse(obsXml, R4);
        string json = obs.SerializeToString();
        json.ShouldContain("\"valueQuantity\":{\"value\":120");
        json.ShouldContain("\"contained\":[{\"resourceType\":\"Patient\",\"id\":\"cp\"}]");
        json.ShouldContain("\"status\":\"final\"");
    }
}
```

- [ ] Step 2: `dotnet test src/FhirCandle.Engine.Tests -f net10.0 --filter FhirXmlTests` — compile failure (`FhirXml` missing).
- [ ] Step 3: Implement `FhirXml`. Core rules of the FHIR XML format this must honor (each corresponds to a branch below): namespace `http://hl7.org/fhir`; primitives as `<name value="..."/>`; primitive extensions from the JSON `_name` sibling merged onto the same XML element; arrays as repeated elements ordered per schema `IType.Order`; choice elements keep their suffixed name (`valueQuantity`) in both formats; `contained` wraps a nested resource element; `div` is raw XHTML in the `http://www.w3.org/1999/xhtml` namespace; on parse, `IType.IsCollection` decides JsonArray vs scalar and `Info.IsPrimitive` + primitive kind (`FhirPrimitive` on `TypeInfo`) decides JSON boolean/number/string. Skeleton (complete the symmetric branches):

```csharp
// src/FhirCandle.Engine/Serialization/FhirXml.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Ignixa.Abstractions;
using Ignixa.Serialization;
using Ignixa.Serialization.SourceNodes;

namespace FhirCandle.Serialization;

public static class FhirXml
{
    private static readonly XNamespace F = "http://hl7.org/fhir";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    public static string Serialize(ResourceJsonNode resource, IFhirSchemaProvider schema, bool pretty = false)
    {
        XElement root = WriteResource(resource.MutableNode, schema);
        return root.ToString(pretty ? SaveOptions.None : SaveOptions.DisableFormatting);
    }

    public static ResourceJsonNode Parse(string xml, IFhirSchemaProvider schema)
    {
        XElement root = XElement.Parse(xml);
        JsonObject obj = ReadResource(root, schema);
        return JsonSourceNodeFactory.Parse((JsonNode)obj);
    }

    private static XElement WriteResource(JsonObject obj, IFhirSchemaProvider schema)
    {
        string resourceType = obj["resourceType"]!.GetValue<string>();
        IType type = schema.GetTypeDefinition(resourceType)
            ?? throw new NotSupportedException($"Unknown resource type '{resourceType}'");
        var el = new XElement(F + resourceType);
        WriteChildren(el, obj, type, schema);
        return el;
    }

    private static void WriteChildren(XElement parent, JsonObject obj, IType type, IFhirSchemaProvider schema)
    {
        foreach (IType child in type.Children.OrderBy(c => c.Order))
        {
            string name = child.Info.Name;
            if (!obj.TryGetPropertyValue(name, out JsonNode? value) || value is null) continue;
            JsonObject? shadow = obj.TryGetPropertyValue("_" + name, out JsonNode? s) ? s as JsonObject : null;

            if (name == "contained" && value is JsonArray containedArr)
            {
                foreach (JsonNode? c in containedArr)
                    parent.Add(new XElement(F + "contained", WriteResource(c!.AsObject(), schema)));
                continue;
            }
            if (name == "div")
            {
                parent.Add(XElement.Parse(value.GetValue<string>()));
                continue;
            }

            IEnumerable<(JsonNode? Val, JsonNode? Shadow)> items = value is JsonArray arr
                ? arr.Select((v, i) => (v, (shadow as JsonNode as JsonArray)?[i]))
                : [(value, (JsonNode?)shadow)];

            foreach ((JsonNode? item, JsonNode? itemShadow) in items)
            {
                var el = new XElement(F + name);
                if (child.Info.IsPrimitive)
                {
                    if (item is not null) el.SetAttributeValue("value", JsonScalarToString(item));
                    if (itemShadow is JsonObject shObj) WriteChildren(el, shObj, ExtensionCarrier(schema), schema);
                }
                else if (item is JsonObject complex)
                {
                    IType childType = schema.GetTypeDefinition(complex["resourceType"]?.GetValue<string>() ?? child.Info.Name) ?? child;
                    WriteChildren(el, complex, childType, schema);
                }
                parent.Add(el);
            }
        }
    }
    // ReadResource / ReadChildren mirror the above: iterate XML children, look up the
    // element in IType.Children by local name (choice suffix included), IsCollection ->
    // JsonArray, primitive kind from child.Info.Primitive -> bool/number/string JsonValue,
    // value-attribute-plus-children primitives -> "_name" shadow object, <contained> ->
    // nested ReadResource, xhtml div -> raw string.
    // ExtensionCarrier(schema) returns schema.GetTypeDefinition("Element") (id + extension children).
    private static string JsonScalarToString(JsonNode n) =>
        n is JsonValue v && v.TryGetValue(out bool b) ? (b ? "true" : "false")
        : n is JsonValue v2 && v2.TryGetValue(out decimal d) ? d.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : n.GetValue<string>();
}
```

Complete `ReadResource`/`ReadChildren` per the comment (this is the bulk of the task — expect ~150 further lines; the test in Step 1 pins the required behavior for primitives, arrays, choice, contained, and div). Then in `SerializationUtils`: replace both XML stubs with `FhirXml.Parse(content, schema)` / `FhirXml.Serialize(toSerialize, schema, pretty)` and add the `IFhirSchemaProvider? schema = null` parameter to `TryDeserializeFhir` (throwing `ArgumentNullException` if XML content arrives without a schema).
- [ ] Step 4: `dotnet test src/FhirCandle.Engine.Tests -f net10.0 --filter FhirXmlTests` — expect `Passed: 3`.
- [ ] Step 5: `git commit -m "Engine: schema-guided FHIR XML converter (Ignixa has no XML support)"`

---

### Task 4: SummaryFilter — `_summary` / `_elements` support

**Files:**
- Create: `src/FhirCandle.Engine/Serialization/SummaryFilter.cs`
- Modify: `src/FhirCandle.Engine/Serialization/SerializationUtils.cs` (`SerializeFhir` summaryFlag branch)
- Test: `src/FhirCandle.Engine.Tests/SummaryFilterTests.cs`

**Interfaces:**
- Produces: `SummaryFilter.Apply(ResourceJsonNode resource, IFhirSchemaProvider schema, string summaryFlag) : ResourceJsonNode` — flags `true|text|data|count` exactly as the old `SerializationFilter.ForSummary/ForText/ForData/ForCount` mapping (SerializationUtils.cs:407-474 in the old shared project). Returns a filtered deep clone; original untouched.

- [ ] Step 1: Failing test: parse a Patient with `text`, `name`, `photo` (photo is not in-summary in R4); `Apply(p, R4, "true")` result JSON must contain `name`, must not contain `photo`, must contain `meta.tag` with code `SUBSETTED` (system `http://terminology.hl7.org/CodeSystem/v3-ObservationValue`); `Apply(p, R4, "text")` keeps only `text`+`id`+`meta`; `Apply(p, R4, "data")` removes `text`. Assert original object still has `photo` after all calls.
- [ ] Step 2: Run `--filter SummaryFilterTests`, confirm compile failure.
- [ ] Step 3: Implement: deep-clone via `resource.MutableNode.DeepClone().AsObject()`; walk the clone against `schema.GetTypeDefinition(resourceType).Children`, removing properties whose `IType.InSummary` is false (recurse only at the resource root — FHIR `_summary=true` is defined by top-level and nested `isSummary` flags, so recurse into kept complex children using their `IType.Children`); always keep `resourceType`, `id`, `meta`; append the SUBSETTED tag via `MetaJsonNode.Tags` (or direct JsonArray if `Tags` holds strings — verify `MetaJsonNode.cs` in the reference clone; tag is a Coding object so use `MutableNode` directly if so). Wire into `SerializeFhir`: non-empty `summaryFlag` (except `count`, handled at bundle level by the store) applies the filter before serializing.
- [ ] Step 4: Run `--filter SummaryFilterTests` — green.
- [ ] Step 5: `git commit -m "Engine: _summary/_elements filtering via IType.InSummary metadata"`

---

### Task 5: CandleSearchService — pipeline wrapper + unsupported-modifier shims

**Files:**
- Create: `src/FhirCandle.Engine/Search/CandleSearchService.cs`
- Create: `src/FhirCandle.Engine/Search/CustomModifierFilter.cs`
- Test: `src/FhirCandle.Engine.Tests/CandleSearchServiceTests.cs`

**Interfaces:**
- Consumes: `FhirSchemas` (Task 1); `StoreTerminologyService.VsContains` (Task 12 — until then the shim takes `Func<string?, string?, string?, bool> vsContains`).
- Produces:

```csharp
public sealed class CandleSearchService
{
    public CandleSearchService(IFhirSchemaProvider schema, ILoggerFactory loggerFactory);
    public SearchParameterDefinitionManager Definitions { get; }
    public IReadOnlyCollection<SearchIndexEntry> Index(IElement resource);
    public ParsedQuery ParseQuery(string? resourceType, string queryString);       // splits out custom filters + unknown params
    public SearchPredicate CompilePredicate(ParsedQuery query);
    public bool TestForMatch(ResourceKey key, IReadOnlyCollection<SearchIndexEntry> index, ParsedQuery query, IElement resource, Func<string?, string?, string?, bool> vsContains);
    public void AddPackageSearchParameters(IReadOnlyCollection<IElement> searchParameters); // -> Definitions.AddNewSearchParameters
    public void RemoveSearchParameter(string url);                                          // -> Definitions.DeleteSearchParameter
}
public sealed record CustomModifierFilter(string Code, string Modifier, string Value);      // Modifier in { "in", "not-in", "identifier" }
public sealed class ParsedQuery
{
    public required SearchOptions Options { get; init; }
    public required IReadOnlyList<CustomModifierFilter> CustomFilters { get; init; }
    public required IReadOnlyList<string> UnknownParameters { get; init; }                  // for strict-handling 400s
}
```

- [ ] Step 1: Failing tests: (a) `ParseQuery("Patient", "name=Chalmers&_count=5")` → `Options.MaxItemCount == 5`, no custom filters; (b) `ParseQuery("Observation", "code:in=http://example.org/vs/x")` → one `CustomModifierFilter("code","in","http://example.org/vs/x")` and `Options.Expression` null (or lacking the code clause); (c) `TestForMatch` on an indexed Observation with LOINC 8480-6 and a `vsContains` stub returning true for that code → true; stub returning false → false; (d) `ParseQuery("Patient", "bogusparam=1")` → `UnknownParameters` contains `bogusparam` (SearchOptionsBuilder surfaces these in `SearchOptions.UnsupportedParams` — assert pass-through).
- [ ] Step 2: Run `--filter CandleSearchServiceTests` — compile failure.
- [ ] Step 3: Implement. Constructor wires exactly the Task-1 spike chain (resolver → `ReferenceSearchValueParser` → `SearchParameterExpressionParser` → `ExpressionParser` → `SearchOptionsBuilder`) and `SearchIndexerFactory.CreateInstance(schema, loggerFactory, Definitions)`. `ParseQuery`: run `QueryParameterParser.Parse(queryString)`; partition out any `QueryParameter` whose name ends with `:in`, `:not-in`, or `:identifier` into `CustomFilters` (these throw inside Ignixa — `SearchValueExpressionBuilderHelper.cs:205-211`); `Build(...)` the remainder. `CompilePredicate`: `query.Options.Expression is null ? (input => input) : query.Options.Expression.AcceptVisitor(new SearchQueryInterpreter(), default)`. `TestForMatch`: apply predicate to the single-element corpus, then AND each custom filter: for `in`/`not-in`, scan `index` for entries where `entry.SearchParameter.Code == filter.Code && entry.Value is TokenSearchValue tsv` and test `vsContains(filter.Value, tsv.System, tsv.Code)` (negated for `not-in`); for `identifier`, `resource.Select(param.Expression)` → for each reference element, `FirstChild("identifier")` and compare `system|value` against `filter.Value` (this replicates `EvalTokenSearch.TestTokenIn*`/`EvalReferenceSearch.TestReferenceIdentifier` behavior from the old evaluators).
- [ ] Step 4: Run `--filter CandleSearchServiceTests` — green.
- [ ] Step 5: `git commit -m "Engine: CandleSearchService wrapping Ignixa search pipeline with :in/:not-in/:identifier shims"`

---

### Task 6: ResourceStore — storage, versioning, secondary indexes

**Files:**
- Create: `src/FhirCandle.Engine/Storage/ResourceStore.cs` (port of `src/FhirStore.CommonVersioned/Storage/ResourceStore.cs`, de-generified)
- Create: `src/FhirCandle.Engine/Storage/IVersionedResourceStore.cs` (port; `Resource` → `ResourceJsonNode`, `SearchParamDefinition` → `SearchParameterInfo`)
- Test: `src/FhirCandle.Engine.Tests/ResourceStoreTests.cs`

**Interfaces:**
- Consumes: `CandleSearchService` (Task 5), `SerializationUtils` (Task 2)
- Produces: `ResourceStore : IVersionedResourceStore` — non-generic, ctor `(VersionedFhirStore store, string resourceType, IFhirSchemaProvider schema, CandleSearchService search)`. Internal storage: `ConcurrentDictionary<string, ResourceJsonNode> _resourceStore` + `ConcurrentDictionary<string, IReadOnlyCollection<SearchIndexEntry>> _indexes` (recomputed on every write via `search.Index(resource.ToElement(schema))`). `InstanceCreate/Update/Delete` keep old signatures with `ResourceJsonNode` substituted; `TypeSearch(ParsedQuery query) : IEnumerable<ResourceJsonNode>`.

- [ ] Step 1: Failing tests: create (assigns id when absent via `Guid.NewGuid()`, sets `Meta.VersionId="1"` + `Meta.LastUpdated`); update (bumps VersionId to "2"; If-Match `W/"1"` succeeds, `W/"9"` returns 412); delete; canonical index (`TryGetByCanonical` on a stored `SearchParameter` with a `url`); identifier index (`TryResolveIdentifier("http://sys","val", out id)` on a Patient with that identifier); `TypeSearch` with `birthdate=ge1970-01-01` returns the match. All resources constructed from JSON string literals via `SerializationUtils.TryDeserializeFhir`.
- [ ] Step 2: Run `--filter ResourceStoreTests` — compile failure.
- [ ] Step 3: Port the old file. Mechanical substitutions (apply throughout — this table is reused by Tasks 8-15):

| Old (Firely) | New (Ignixa) |
|---|---|
| `T : Resource` type parameter + reflection instantiation per `ModelInfo.FhirTypeToCsType` | non-generic `ResourceStore`; `VersionedFhirStore` creates one per name in `schema.ResourceTypeNames` |
| `resource.TypeName` | `resource.ResourceType` |
| `resource.Meta.VersionId` / `.LastUpdated` | `resource.Meta.VersionId` / `.LastUpdated` (`MetaJsonNode`, lazily created — no null-guard needed) |
| `resource.DeepCopy()` | `JsonSourceNodeFactory.Parse((JsonNode)resource.MutableNode.DeepClone())` |
| `IConformanceResource.Url` probe | `resource.ToElement(schema)` → `FirstChild("url")?.Value as string`; capability probe = type has a `url` child in `IType.Children` |
| `IIdentifiable<Identifier>` probe | element `Children("identifier")` → `system`/`value` children |
| `resource.ToPocoNode()` | `resource.ToElement(schema)` (cached inside `ResourceJsonNode`; call `InvalidateCaches()` after any in-place `MutableNode` mutation) |
| `ModelInfo.IsKnownResource(name)` | `schema.ResourceTypeNames.Contains(name)` |
| `SearchParamDefinition` | `Ignixa.Search.Models.SearchParameterInfo` |
| `_searchTester.TestForMatch(pocoNode, parsedParams)` | `_search.TestForMatch(key, _indexes[id], parsedQuery, element, _store.Terminology.VsContains)` |

Keep: version-increment lock, events (`OnInstanceCreated/...`), `Basic`-wrapped-SubscriptionTopic and Observation-tagging special cases (now reading/writing `MutableNode` JSON directly), post-CRUD side effects for CompartmentDefinition/SearchParameter/ValueSet (they call into Tasks 11/5/12 services).
- [ ] Step 4: Run `--filter ResourceStoreTests` — green.
- [ ] Step 5: `git commit -m "Engine: ResourceStore over ResourceJsonNode with search-index cache"`

---

### Task 7: Search execution extras — sorting, `_include`/`_revinclude`, chained & `_has`

Ignixa's `SearchQueryInterpreter` intentionally defers chained/include evaluation to the data layer (confirmed in source and in `FileBasedSearchService`); candle implements them over its stores, replacing what `SearchTester.TestForMatch` chaining and `VersionedFhirStore` inclusion code did.

**Files:**
- Create: `src/FhirCandle.Engine/Search/FhirSortComparer.cs`
- Create: `src/FhirCandle.Engine/Search/SearchExecutor.cs`
- Test: `src/FhirCandle.Engine.Tests/SearchExecutorTests.cs`

**Interfaces:**
- Consumes: `ResourceStore` (Task 6), `CandleSearchService` (Task 5), `Ignixa.FhirPath.Evaluation.TypedElementExtensions.Select`
- Produces: `FhirSortComparer : IComparer<ResourceJsonNode>` ctor `(IFhirSchemaProvider schema, IReadOnlyList<SortExpression> sorts, SearchParameterDefinitionManager definitions)` — resolves each sort code to its `SearchParameterInfo.Expression`, evaluates `element.Select(expr)` first value, compares `IElement.Value` with numeric/string/date coercion (dates compare as ISO strings, which is ordinal-safe). `SearchExecutor` static methods: `ResolveIncludes(IEnumerable<ResourceJsonNode> matches, IReadOnlyList<IncludeExpression> includes, Func<string, ResourceStore?> storeResolver, IFhirSchemaProvider schema) : IEnumerable<ResourceJsonNode>` (walk each match's index `ReferenceSearchValue` entries for the include's parameter; resolve `Type/id` against target store); `ResolveRevIncludes(...)` (scan source-type store indexes for `ReferenceSearchValue` pointing at match keys); `EvaluateChained(Expression chainRoot, ResourceJsonNode candidate, ...) : bool` (for `ChainedExpression` nodes: read candidate's reference entries for the chain parameter, resolve target, recursively test the child expression against the target's index — reversed flag handles `_has` by scanning the source store). `CandleSearchService.ParseQuery` is extended in this task to also partition chained/`_has` parameters (any key containing `.` or starting `_has:`) into `ParsedQuery.ChainedExpressions : IReadOnlyList<Expression>` (parsed via `ExpressionParser.Parse` which supports them) so the flat predicate and the chained evaluator compose with AND.

- [ ] Step 1: Failing tests using two stores (Patient + Observation, JSON literals with `subject.reference = "Patient/pat-1"`): (a) `_include=Observation:subject` on an Observation match yields the Patient; (b) `_revinclude=Observation:subject` on the Patient yields the Observation; (c) chained `subject.name=Chalmers` on Observation matches; `subject.name=Nomatch` doesn't; (d) `_has:Observation:subject:code=http://loinc.org|8480-6` on Patient matches; (e) sort by `birthdate` orders two patients correctly both directions.
- [ ] Step 2: Run `--filter SearchExecutorTests` — compile failure.
- [ ] Step 3: Implement per the Produces spec. Reference values come from index entries: `entry.Value is ReferenceSearchValue` — read its resource type + id properties (verify exact member names in `src/Core/Ignixa.Search/Indexing/SearchValues/ReferenceSearchValue.cs` of the reference clone; `ReferenceSearchValueParser` builds them, so parse behavior for relative/absolute refs is already handled).
- [ ] Step 4: Run `--filter SearchExecutorTests` — green.
- [ ] Step 5: `git commit -m "Engine: sorting, includes, chained and _has evaluation over Ignixa indexes"`

---

### Task 8: VersionedFhirStore — core interactions (CRUD, search dispatch, metadata plumbing)

The 7,716-line centerpiece. Port `src/FhirStore.CommonVersioned/Storage/VersionedFhirStore.cs` into the engine in three tasks (8: CRUD+search+dispatch+load, 9: bundles, 10: capabilities). It stays `IFhirStore` (unchanged interface from `FhirStore.Common`).

**Files:**
- Create: `src/FhirCandle.Engine/Storage/VersionedFhirStore.cs`
- Test: `src/FhirCandle.Engine.Tests/VersionedFhirStoreTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-7; `FhirStore.Common` types unchanged (`FhirRequestContext`, `FhirResponseContext`, `TenantConfiguration`, `StoreInteractionCodes`).
- Produces: `FhirCandle.Storage.VersionedFhirStore : IFhirStore` with `Init(TenantConfiguration config)` deriving `_schema = FhirSchemas.Get(config.FhirVersion)`; internal members later tasks use: `IFhirSchemaProvider Schema { get; }`, `CandleSearchService Search { get; }`, `StoreTerminologyService Terminology { get; }` (stub until Task 12), `bool TryResolveAsElement(string reference, out IElement? element)` (the FHIRPath `resolve()` hook), `internal ResourceStore? GetStore(string resourceType)`.

- [ ] Step 1: Failing tests driving the store purely through `IFhirStore` (mirroring `FhirStoreTests.cs` patterns): Init an R4 store from a `TenantConfiguration`; `InstanceCreate` a Patient from serialized JSON in `FhirRequestContext` → 201 + `SerializedResource` contains versionId 1; `InstanceRead` → 200; conditional read If-None-Match → 304; `InstanceUpdate` → 200 versionId 2; `TypeSearch` `?birthdate=ge1970-01-01` → searchset Bundle JSON with total 1 and `entry[0].search.mode == "match"`; `InstanceDelete` → 200; read after delete → 404. Repeat one create/search pair with `FhirVersion.R5` config to lock in multi-version in one process.
- [ ] Step 2: `dotnet test src/FhirCandle.Engine.Tests -f net10.0 --filter VersionedFhirStoreTests` — compile failure.
- [ ] Step 3: Port, applying the Task-6 substitution table plus these VersionedFhirStore-specific mappings:
  - Bootstrap (old :241-289): `foreach (string rt in _schema.ResourceTypeNames)` skipping `Parameters`, `OperationOutcome`, `SubscriptionStatus` → `new ResourceStore(this, rt, _schema, _search)`. Search-parameter bootstrap disappears entirely — `SearchParameterDefinitionManager` ships the generated definitions.
  - FHIRPath compilation cache (old :62,81,888-904): **delete** — `TypedElementExtensions` has built-in AST + delegate caches; call sites become `element.Select(expr, ctx)` / `element.IsTrue(expr, ctx)`.
  - `FhirEvaluationContext { Resource, TerminologyService = _terminology, ElementResolver = Resolve }` (old :6198-6206) → `new Ignixa.FhirPath.Evaluation.FhirEvaluationContext { Resource = element, RootResource = element, ElementResolver = ResolveElement }` (terminology hook dropped — documented regression).
  - `ParsedSearchParameter.Parse(...)` / `SearchTester` (old search paths :4797-5246) → `_search.ParseQuery(resourceType, queryString)` + `SearchExecutor`; strict search handling reads `ParsedQuery.UnknownParameters` for the `Prefer: handling=strict` 400 path (assertion parity with `StrictSearchHandlingTests`).
  - Searchset Bundle building (old :5028-5079) → `BundleJsonNode` (`Type = BundleType.Searchset`, `Total`, `Entry` via `BundleComponentJsonNode` with `search.mode` set through `MutableNode`).
  - SMART-scope compartment filtering (old :5152-5246) → element-based: candidate `ToElement` + compartment param membership via Task 11.
  - Serialization at every boundary: `SerializationUtils` (Tasks 2-4) with `_schema` passed through.
  - Keep verbatim: interaction dispatch switch (:1161-1294), ETag/If-Match semantics, strict-mode id rules (regex at :69 is BCL), capacity eviction, `LoadPackage` directory walker (parses each `.json` via `TryDeserializeFhir`), protect-loaded-content, event raising.
- [ ] Step 4: Run `--filter VersionedFhirStoreTests` — green.
- [ ] Step 5: `git commit -m "Engine: VersionedFhirStore core interactions ported to Ignixa model"`

---

### Task 9: VersionedFhirStore — batch/transaction bundles

**Files:**
- Modify: `src/FhirCandle.Engine/Storage/VersionedFhirStore.cs` (port old :6950-7621 + `DoProcessBundle`)
- Test: `src/FhirCandle.Engine.Tests/BundleProcessingTests.cs`

**Interfaces:**
- Consumes: `BundleJsonNode`, Task 8 CRUD internals.
- Produces: `ProcessBundle` behavior identical to old; internal `static void RewriteReferences(JsonNode? node, IReadOnlyDictionary<string, string> map)` replacing `Base.EnumerateElements()`:

```csharp
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
```

- [ ] Step 1: Failing tests replicating `FromIssues.cs` issue-26 semantics plus: transaction bundle with two POSTs where entry 2 references entry 1's `urn:uuid:` fullUrl → response bundle `transaction-response`, both entries 201, stored entry-2 resource's reference rewritten to `Patient/{newId}`; batch with one bad entry → per-entry status, others succeed; conditional create via `ifNoneExist` honored.
- [ ] Step 2: Run `--filter BundleProcessingTests` — fails (methods missing).
- [ ] Step 3: Port `ProcessTransaction`/`ProcessBatch`/`DoProcessBundle`: `Bundle.EntryComponent` reads → `BundleJsonNode.Entry` (`BundleComponentJsonNode`; request `method/url/ifMatch/ifNoneExist/ifModifiedSince` via typed props or `MutableNode`), id-reassignment map built exactly as before, `RewriteReferences(entryResource.MutableNode, map)`, identifier-lookup pre-pass via `ResourceStore.TryResolveIdentifier`.
- [ ] Step 4: Run `--filter BundleProcessingTests` — green.
- [ ] Step 5: `git commit -m "Engine: batch/transaction processing with JsonNode reference rewriting"`

---

### Task 10: VersionedFhirStore — CapabilityStatement generation

**Files:**
- Modify: `src/FhirCandle.Engine/Storage/VersionedFhirStore.cs` (port old :6493-6948)
- Test: `src/FhirCandle.Engine.Tests/CapabilityStatementTests.cs`

**Interfaces:**
- Consumes: `Schema.FullVersion` ("4.0.1"/"4.3.0"/"5.0.0" — replaces `CommonToFirelyVersion` + `FHIRVersion.N4_0_1` enums), `ResourceStore.GetSearchParamDefinitions()` (`SearchParameterInfo` list), `SearchExecutor` includes support.
- Produces: `GetMetadata` returning a cached CapabilityStatement built as a raw `JsonObject` → `ResourceJsonNode` (no typed wrapper exists; build `rest[0].resource[]` arrays directly), regenerated when stale exactly as before. `$feature-query` port included (pure JSON building).

- [ ] Step 1: Failing test: `GetMetadata` on an Init'd R4 store → 200; parse `SerializedResource`; assert `resourceType == "CapabilityStatement"`, `fhirVersion == "4.0.1"`, `rest[0].mode == "server"`, a `Patient` entry exists under `rest[0].resource` with `interaction` containing `read` and `searchParam` containing `birthdate`, and `status == "active"`. Same for R5 → `fhirVersion == "5.0.0"`.
- [ ] Step 2: Run `--filter CapabilityStatementTests` — fails.
- [ ] Step 3: Port `generateCapabilities`: every `new CapabilityStatement.XComponent{...}` becomes a `JsonObject` literal; enums (`TypeRestfulInteraction.Read` etc.) become their wire strings (`"read"`) — the old code already knows the literals via `EnumUtility.GetLiteral`, here they're written directly. Cache + staleness logic unchanged.
- [ ] Step 4: Run `--filter CapabilityStatementTests` — green.
- [ ] Step 5: `git commit -m "Engine: CapabilityStatement generation as JSON documents"`

---

### Task 11: Compartments

**Files:**
- Create: `src/FhirCandle.Engine/Compartments/ParsedCompartment.cs` (port of `src/FhirStore.CommonVersioned/Compartments/ParsedCompartment.cs`)
- Create: `src/FhirCandle.Engine/Compartments/CoreCompartmentSource.cs`
- Modify: `src/FhirCandle.Engine/Storage/VersionedFhirStore.cs` (compartment search paths, old :2722, :5399, :5700)
- Test: `src/FhirCandle.Engine.Tests/CompartmentTests.cs`

**Interfaces:**
- Consumes: `Ignixa.Search.Definition.CompartmentDefinitionManager(FhirVersion)` — `TryGetSearchParams(resourceType, CompartmentType, out HashSet<string>)`, `TryGetResourceTypes(CompartmentType, out HashSet<string>)` for the five core compartments (replaces the three ~3,000-line embedded-JSON `CoreCompartmentSource` files); `IElement` parsing for runtime-POSTed CompartmentDefinitions.
- Produces: `ParsedCompartment(IElement compartmentDefinition)` with unchanged output shape (`IncludedResources` keyed by resource type with `SearchParamCodes`); `CoreCompartmentSource.GetCompartments(FhirVersion) : IEnumerable<ParsedCompartment>` adapting the Ignixa manager; `IFhirStore.RegisterCompartmentDefinition(object)` now expects a `ResourceJsonNode`.

- [ ] Step 1: Failing tests: (a) `CoreCompartmentSource.GetCompartments(FhirVersion.R4)` includes a Patient compartment whose `IncludedResources["Observation"].SearchParamCodes` contains `subject` and `patient`; (b) `ParsedCompartment` from the repo's existing test asset `src/fhir-candle.Tests/data/r4/CompartmentDefinition-patient.json` (parse via `SerializationUtils` + `ToElement`) yields the same; (c) compartment search `Patient/pat-1/Observation` through `IFhirStore.CompartmentTypeSearch` returns only observations referencing pat-1.
- [ ] Step 2: Run `--filter CompartmentTests` (engine test project) — fails.
- [ ] Step 3: Implement: `ParsedCompartment` walks `cd.Children("resource")` reading `code` + `param` children (`{def}` → `_id` mapping preserved from old :63-79). Compartment search: membership = any of the compartment's param codes for the candidate type having a `ReferenceSearchValue` index entry pointing at the compartment instance (reuses Task 7 machinery).
- [ ] Step 4: Green.
- [ ] Step 5: `git commit -m "Engine: compartments via Ignixa CompartmentDefinitionManager + IElement parsing"`

---

### Task 12: StoreTerminologyService

**Files:**
- Create: `src/FhirCandle.Engine/Storage/StoreTerminologyService.cs` (port of old 279-line file)
- Modify: `src/FhirCandle.Engine/Storage/VersionedFhirStore.cs`, `ResourceStore.cs` (ValueSet CRUD side effects; `TestForMatch` vsContains wiring)
- Test: `src/FhirCandle.Engine.Tests/TerminologyTests.cs`

**Interfaces:**
- Produces: `StoreTerminologyService` — no Firely interface; members: `void StoreProcessValueSet(IElement valueSet, bool remove = false)`; `bool VsContains(string? vsUrl, string? system, string? code)`; `ResourceJsonNode ValueSetValidateCode(ResourceJsonNode parameters)` (Parameters-in/Parameters-out port of old :154-220, reading `url`/`code`/`system`/`coding`/`codeableConcept` from the Parameters JSON). The Firely `ITerminologyService` registration and the six `NotImplementedException` stubs are dropped (nothing consumed them).

- [ ] Step 1: Failing tests: load a ValueSet JSON (with both `compose.include[].concept[]` and an `expansion.contains` variant incl. nested contains) → `VsContains(url, system, code)` true for member, false for non-member; remove → false; `ValueSetValidateCode` Parameters round-trip asserts `result` true/false and `message` on miss; token `:in` end-to-end through `ResourceStore.TypeSearch` (`code:in={vsUrl}`) matches the LOINC observation from Task 5's fixture.
- [ ] Step 2: Run `--filter TerminologyTests` — fails.
- [ ] Step 3: Port: the flattening walk translates 1:1 from POCO traversal to `IElement.Children("compose").Children("include")` / `Children("expansion").Children("contains")` recursion into the same `Codes`/`SystemAndCodes` hash sets.
- [ ] Step 4: Green.
- [ ] Step 5: `git commit -m "Engine: store terminology service (ValueSet flattening, VsContains, validate-code)"`

---

### Task 13: Operations (incl. $validate via Ignixa.Validation)

**Files:**
- Create: `src/FhirCandle.Engine/Operations/` — port all `Op*.cs` from `src/FhirStore.CommonVersioned/Operations/` + `IFhirOperation.cs`
- Test: `src/FhirCandle.Engine.Tests/OperationsTests.cs`

**Interfaces:**
- Consumes: `Ignixa.Validation`: `StructureDefinitionSchemaResolver(schemaProvider, terminologyService: null)` → `CachedValidationSchemaResolver` → `ProfileAwareValidationSchemaResolver.ResolveForElement(element)`; `ValidationSchema.Validate(IElement, ValidationSettings, ValidationState?)`; `ValidationResult.ToOperationOutcome()`.
- Produces: `IFhirOperation.DoOperation(FhirRequestContext, VersionedFhirStore, IVersionedResourceStore?, ResourceJsonNode? focusResource, ResourceJsonNode? bodyResource, out FhirResponseContext)`; `GetDefinition(FhirSequenceCodes)` returns `ResourceJsonNode` OperationDefinitions built as JSON literals (replacing POCO graphs — they're static content).

- [ ] Step 1: Failing tests: `$validate` on a valid Patient → OperationOutcome with an informational issue and no errors; `$validate` on a Patient with `"birthDate": "not-a-date"` → error-severity issue; `$reset-store` clears and reloads (existing semantics from `f4675d0`); `$subscription-events` returns a history bundle shape (fuller subscription assertions live in Task 14).
- [ ] Step 2: Run `--filter OperationsTests` — fails.
- [ ] Step 3: Port each operation. `OpValidate` core replacing old :202:

```csharp
var resolver = new ProfileAwareValidationSchemaResolver(
    new CachedValidationSchemaResolver(
        new StructureDefinitionSchemaResolver(store.Schema, terminologyService: null)));
ValidationSchema schema = resolver.ResolveForElement(element);
ValidationResult result = schema.Validate(element,
    new ValidationSettings { SkipTerminologyValidation = true });
OperationOutcomeJsonNode outcome = result.ToOperationOutcome();
```

(Verify the resolver ctor chain arity against `src/Core/Ignixa.Validation/Schema/*.cs` in the reference clone at implementation time; the composition order is documented in Ignixa's README and `ProfileAwareValidationSchemaResolver.cs:37`. Depth default: `ValidationDepth.Spec` — closest to today's attribute validation without terminology.) `OpSubscriptionStatus/Events/Hook`, `OpConvert`, `OpIsFhir`, `OpFeatureQuery`, `OpResetStore`: Parameters/Bundle handling moves to `ParametersJsonNode`/`BundleJsonNode`.
- [ ] Step 4: Green.
- [ ] Step 5: `git commit -m "Engine: operations ported; $validate now uses Ignixa profile-aware validation"`

---

### Task 14: Subscriptions & topics — converters and triggers

**Files:**
- Create: `src/FhirCandle.Engine/Subscriptions/TopicConverter.cs`, `SubscriptionConverter.cs`, `ConverterUtils.cs` (merging the three per-version copies into `switch (FhirVersion)` branches; sources: `src/FhirStore.R4/Subscriptions/*` 673+205+185 lines, R4B 628+158, R5 483+160)
- Create: `src/FhirCandle.Engine/Models/ExecutableSubscriptionInfo.cs` (port; `CompiledExpression` → expression string, evaluated via cached `TypedElementExtensions`)
- Modify: `src/FhirCandle.Engine/Storage/ResourceStore.cs` (`PerformSubscriptionTest`, old :1171-1627), `VersionedFhirStore.cs` (`StoreProcessSubscriptionTopic/Subscription`, old :2901-3554)
- Test: `src/FhirCandle.Engine.Tests/SubscriptionTests.cs`

**Interfaces:**
- Consumes: `EvaluationContext.WithEnvironmentVariable("current", element).WithEnvironmentVariable("previous", element)` (exact Ignixa API for `%current`/`%previous`); `element.IsTrue(fhirPathTrigger, ctx)`; `ParsedSubscription`/`ParsedSubscriptionTopic`/`ParsedSubscriptionStatus` from `FhirStore.Common` (unchanged).
- Produces: `TopicConverter.TryParse(ResourceJsonNode topic, FhirVersion version, out ParsedSubscriptionTopic parsed)`; `SubscriptionConverter.TryParse(ResourceJsonNode sub, FhirVersion version, out ParsedSubscription parsed)`, `.UpdateResourceStatus(...)`, `.BundleForSubscriptionEvents(...) : BundleJsonNode`, `.StatusForSubscription(...) : ResourceJsonNode` (Parameters for R4, SubscriptionStatus JSON for R4B/R5), constants `OffCode/ActiveCode/PayloadContentVsUrl` preserved.

- [ ] Step 1: Failing tests reproducing what `R4Tests`/`R5Tests` subscription sections assert (grounded in `R4Tests.cs:2299-2481` / `R5Tests.cs:1156-1319`, rewritten over JSON): create topic (R4: `Basic` with `fhir-types|SubscriptionTopic` code + R5 cross-extensions from the existing `fhirData/subscriptions` samples; R5: native SubscriptionTopic JSON) → `CurrentTopics` contains it; create R4 Subscription with backport extensions (`backport-filter-criteria` etc.) → parsed channel/filters correct; create matching Encounter (JSON literal, `status` changed to trigger) → one `SubscriptionEvent` raised; `BundleForSubscriptionEvents` → `history` bundle whose first entry is the notification status resource (Parameters for R4, SubscriptionStatus for R5) with correct `notificationEvent` count.
- [ ] Step 2: Run `--filter SubscriptionTests` — fails.
- [ ] Step 3: Port. Extension reading (old `ConverterUtils.ParseExtensions` over `Extension` POCOs) becomes JsonNode walking — representative pattern used throughout:

```csharp
private static string? GetExtensionValueString(JsonObject owner, string url, string valueProp = "valueString") =>
    owner["extension"] is JsonArray exts
        ? exts.OfType<JsonObject>()
              .FirstOrDefault(e => e["url"]?.GetValue<string>() == url)?[valueProp]?.GetValue<string>()
        : null;
```

Trigger evaluation in `ResourceStore.PerformSubscriptionTest`: build `EvaluationContext` per interaction — `new FhirEvaluationContext { Resource = currentElement, RootResource = currentElement, ElementResolver = _store.ResolveElement }` then `.WithEnvironmentVariable("current", currentElement)` and (updates/deletes) `.WithEnvironmentVariable("previous", previousElement)`; fire when `focus.IsTrue(trigger.FhirPathTrigger, ctx)`. Query triggers reuse `CandleSearchService.TestForMatch` against pre/post index snapshots (keep the previous-version index alongside during update, exactly as the old code kept `previous`/`previousPN`).
- [ ] Step 4: Green.
- [ ] Step 5: `git commit -m "Engine: subscriptions/topics as version-branched JSON converters with Ignixa FHIRPath triggers"`

---

### Task 15: R4 IG extras — CDex task hook, PAS operations

**Files:**
- Create: `src/FhirCandle.Engine/InteractionHooks/CDexTaskProcess.cs` (port of `src/FhirStore.R4/InteractionHooks/CDexTaskProcess.cs`, 265 lines)
- Create: `src/FhirCandle.Engine/Operations/OpPasClaimInquiry.cs`, `OpPasClaimSubmit.cs` (ports, 291 + 468 lines)
- Test: `src/FhirCandle.Engine.Tests/IgExtrasTests.cs`

**Interfaces:**
- Consumes: Task 8 hook pipeline (`IFhirInteractionHook` signature updated to `ResourceJsonNode`), Task 9 bundle machinery.
- Produces: same hook/operation registrations, now gated at runtime on `store.Schema.Version == FhirVersion.R4` (replacing assembly-level R4-only existence).

- [ ] Step 1: Failing tests: CDex — create a Task JSON matching the hook's trigger shape → hook mutates status per old behavior; PAS — `$submit` with a minimal PAS request bundle (adapt from `fhirData/hl7.fhir.us.davinci-cdex` samples / old test fixtures) → response bundle with ClaimResponse. Also assert the hook does not fire on an R5 store.
- [ ] Step 2: Run `--filter IgExtrasTests` — fails.
- [ ] Step 3: Port using the Task-6 substitution table; `Task`/`Claim` POCO property reads become `IElement` reads / `MutableNode` writes.
- [ ] Step 4: Green.
- [ ] Step 5: `git commit -m "Engine: DaVinci CDex hook and PAS operations ported, version-gated at runtime"`

---

### Task 16: Package management — Ignixa.PackageManagement + Firely-free CI client

**Files:**
- Modify: `src/fhir-candle/Services/FhirPackageService.cs` (rewrite, 765 lines), `src/fhir-candle/Services/IFhirPackageService.cs` (drop `Firely.Fhir.Packages` types from the interface)
- Modify: `src/fhir-candle/_ForPackages/FhirCiClient.cs` (de-Firely), delete `_ForPackages/DiskPackageCache.cs`, `JsonModels.cs` Firely subclassing (keep the CI DTOs `CiBranchRecord`/`FhirCiQaRecord` and converters — they're Newtonsoft-only)
- Modify: `src/fhir-candle/Services/FhirStoreManager.cs:554-600` (`PackageReference` → `InstalledPackage`)
- Modify: `src/fhir-candle/fhir-candle.csproj` (remove `Firely.Fhir.Packages`, add `Ignixa.PackageManagement`)
- Test: `src/fhir-candle.Tests/PackageServiceTests.cs` (new)

**Interfaces:**
- Consumes: `NpmPackageLoader` + `NpmPackageLoaderOptions { RegistryUrl }` (instantiate one per registry: `https://packages.fhir.org`, `https://packages2.fhir.org/packages`, plus config-supplied) composed via `CompositePackageLoader`; `PackageCacheManager(cacheDirectory, logger)`; `PackageExtractor.ExtractAsync(Stream, ct) : PackageExtractionResult { Manifest, Resources }`.
- Produces: `public sealed record InstalledPackage(string Id, string Version, string Directive, string ContentDirectory);` and `IFhirPackageService` reshaped: `Task<List<InstalledPackage>> InstallPackages(string[]? packageDirectives, string[]? ciLiterals, List<FhirReleases.FhirSequenceCodes>? fhirVersions)`, `string? GetPackageContentDirectory(InstalledPackage package)`, `void DeletePackage(string directive)` — `IFhirStore.LoadPackage(directive, directory, supplements, includeExamples)` is unchanged, so after download+extract the service writes `package/` content files to `{cacheRoot}/packages/{id}#{version}/package/` (preserving today's `~/.fhir`-style expanded layout so tenant data and docs keep working).

- [ ] Step 1: Failing tests (offline): directive normalization `name#version` ↔ `name@version` (port the old `:286-288` logic); `GetVersionHandlingType` semantics for `latest`/`dev`/`current`/`current$branch`/explicit (old `:575-604`) as a pure function; extraction of a fixture `.tgz` (add `src/fhir-candle.Tests/data/common/minimal.test.pkg.tgz` containing a package.json + one SearchParameter) lands files in the expected content directory and returns its manifest id/version.
- [ ] Step 2: `dotnet test src/fhir-candle.Tests -f net10.0 --filter PackageServiceTests` — fails.
- [ ] Step 3: Rewrite the service. Registry resolution: try each loader in order (packages.fhir.org → packages2 → configured), matching old `InstallPackage` fallback (`:449-482`). `latest` resolution: npm registry metadata via `NpmPackageSearchService.GetPackageDetailsAsync(packageId)` (dist-tags), replacing `IPackageServer.GetLatest`. CI (`current`) directives: `FhirCiClient` keeps its qas.json/version.info logic but returns candle-owned records — replace `IPackageServer`/`PackageListing`/`PackageRelease`/`Versions`/`PackageReference` with `InstalledPackage` + a private `record CiPackageVersion(string Version, string Branch, DateTimeOffset BuildDate)`; download URL composition is unchanged (it never used Firely for HTTP). FHIR-version-suffixed fallback (`foo.r4`) recursion ports as-is (`:363-416`), reading `FhirVersionList` from the kept manifest DTO.
- [ ] Step 4: Green. Then a manual smoke (documented, not CI): `dotnet run --project src/fhir-candle -- --fhir-package-cache <temp> --load-package hl7.fhir.us.core#6.1.0` starts and loads.
- [ ] Step 5: `git commit -m "Host: package service on Ignixa.PackageManagement; CI client de-Firely'd"`

---

### Task 17: Host wiring — single-assembly store, alias removal, trivial using cleanup

**Files:**
- Modify: `src/fhir-candle/Services/FhirStoreManager.cs` (drop `extern alias candleR4/R4B/R5` at :6-8; construct `FhirCandle.Storage.VersionedFhirStore` directly per tenant with its `TenantConfiguration.FhirVersion`)
- Modify: `src/fhir-candle/fhir-candle.csproj` — remove the `AddPackageAliases` target (:140-161), remove ProjectReferences to `FhirStore.R4/R4B/R5` (:104-106), add `<ProjectReference Include="..\FhirCandle.Engine\FhirCandle.Engine.csproj" />`; delete the `<Compile Remove>` blocks for `FhirWebSerializer` (:69-72) **and delete those two files**
- Modify: delete unused usings — `Controllers/FhirController.cs:13`, `Controllers/SmartController.cs:12`, `Services/SmartAuthManager.cs:12-13`, `Mcp/CommonCandleMcp.cs:2` (also the stray `using Org.BouncyCastle.Asn1.X500;` in `Mcp/FhirMcpTools.cs`), `Services/FhirPackageService.cs` leftover Hl7 usings
- Test: existing `src/fhir-candle.Tests` compile is deferred to Task 19; gate here is the host building and running

- [ ] Step 1: The "failing test" is the build: `dotnet build src/fhir-candle -f net10.0` currently fails once Engine replaces the three store references (extern aliases unresolved). Run it, capture the alias errors.
- [ ] Step 2: Apply all modifications above.
- [ ] Step 3: `dotnet build src/fhir-candle -f net10.0` — expect success with zero warnings about missing aliases.
- [ ] Step 4: Runtime smoke: `dotnet run --project src/fhir-candle -- --reference-implementation r4 --load-examples false` then `curl http://localhost:5826/fhir/r4/metadata` returns a CapabilityStatement, `curl -X POST http://localhost:5826/fhir/r4/Patient -H "Content-Type: application/fhir+json" -d "{\"resourceType\":\"Patient\"}"` returns 201, same POST with `Accept: application/fhir+xml` returns XML. All three default tenants (r4/r4b/r5) must appear on the landing page.
- [ ] Step 5: `git commit -m "Host: single-assembly engine wiring; remove extern alias machinery and dead serializer"`

---

### Task 18: UI — delete dead project, port the three POCO pages

**Files:**
- Delete: `src/FhirCandle.Ui.Versioned/` (entire shared project — imported by nothing, MudBlazor-era dead code) + its `.shproj` solution entry
- Modify: `src/FhirCandle.Ui.R4/davinci-cdex/TaskTable.razor`, `src/FhirCandle.Ui.R4/davinci-pas/PasWalkthroughR4.razor`, `src/FhirCandle.Ui.R4/Subscriptions/UsCoreHti2ContentsR4.razor`
- Modify: `src/FhirCandle.Ui.R4/FhirCandle.Ui.R4.csproj`, `...Ui.R4B...`, `...Ui.R5...` — ProjectReference `FhirStore.R4/R4B/R5` → `FhirCandle.Engine`
- Test: manual browser pass (Blazor pages; no unit harness exists for them)

**Interfaces:**
- Consumes: `IResourceStore` (unchanged), engine `ResourceStore` values now `ResourceJsonNode`; `IElement` projections.

- [ ] Step 1: Define the projection pattern once (used by all three pages) — local records materialized from elements, e.g. for `UsCoreHti2ContentsR4.razor`'s patient autocomplete:

```csharp
private sealed record PatientRow(string Id, string Display);
private static PatientRow ToRow(ResourceJsonNode p, IFhirSchemaProvider schema)
{
    var e = p.ToElement(schema);
    var name = e.FirstChild("name");
    string display = name is null ? p.Id
        : $"{string.Join(' ', name.Children("given").Select(g => g.Value))} {name.FirstChild("family")?.Value}".Trim();
    return new PatientRow(p.Id, display);
}
```

`TaskTable.razor`: grid binds `TaskRow(Id, Status, Code, For)` projections; the status-mutation flow becomes clone → `MutableNode["status"] = newStatus` → re-serialize through the store (replacing `EnumUtility.ParseLiteral<Task.TaskStatus>` + `DeepCopy`). `PasWalkthroughR4.razor`: the in-code `Claim`/`Bundle` construction (78 POCO refs) becomes a parameterized JSON template (C# raw-string with interpolation for patient/org ids) parsed via `JsonSourceNodeFactory.Parse` — semantically identical, drastically shorter.
- [ ] Step 2: `dotnet build src/FhirCandle.Ui.R4 -f net10.0` fails before edits (Hl7 types unresolved after csproj swap) — confirms the blast radius list is complete.
- [ ] Step 3: Apply ports; build all four UI projects clean.
- [ ] Step 4: Browser verification (`dotnet run --project src/fhir-candle`, default tenants): CDex task table renders and status change round-trips; PAS walkthrough completes its submit step; HTI-2 subscription page's patient picker populates and selection works; core pages (store browser, FhirEditor, ResourcePicker, subscription tour) regression-checked — they were already string-based and must be visually unchanged.
- [ ] Step 5: `git commit -m "UI: port R4 POCO pages to element projections; remove dead Ui.Versioned project"`

---

### Task 19: Test-suite migration

**Files:**
- Modify: `src/fhir-candle.Tests/fhir-candle.Tests.csproj` — remove `Hl7.Fhir.R4/R4B/R5/R6` PackageReferences (:20-23) and the alias target (:48-69); ProjectReference the engine
- Modify: `FhirStoreTests.cs`, `FhirStoreTestsR4/R4B/R5.cs`, `StrictSearchHandlingTests.cs`, `FromIssues.cs` — drop `extern alias` lines + unused Hl7 usings; `VersionedFhirStore` construction becomes unaliased (assertions untouched, per Global Constraints)
- Rewrite sections: `R4Tests.cs:1099-1117` (conditional-create id mutation → parse JSON via engine `SerializationUtils`, set `Id`, re-serialize) and `:2299-2481` (subscription POCO section), `R4BTests.cs:1051-1218`, `R5Tests.cs:1156-1319` — Encounter construction becomes JSON literals (`{"resourceType":"Encounter","status":"planned",...}` with the R4B/R5 status-code differences preserved: R5 uses `completed` where R4 uses `finished`); `ResourceStore<coreR4::...Encounter>` casts become the engine `ResourceStore`; `ParseNotificationBundle((Bundle)r)` becomes parsing the serialized bundle JSON
- Modify: `CompartmentTests.cs` — `coreR4::FhirJsonDeserializer.OSTRICH` → engine parse; the assertion `result type == "Hl7.Fhir.Model.Bundle"` becomes asserting the response resource's `ResourceType == "Bundle"`
- Delete: engine-superseded `UcumTests.cs` exclusion note stays as-is (already excluded)

- [ ] Step 1: `dotnet build src/fhir-candle.Tests -f net10.0` — capture the full error list; it must consist only of the files named above (if a different file errors, the inventory missed a usage — investigate before proceeding).
- [ ] Step 2: Apply plumbing edits to the survive-with-trivial-edits files first; build again — remaining errors confined to the four rewrite files.
- [ ] Step 3: Apply the section rewrites.
- [ ] Step 4: `dotnet test src/fhir-candle.Tests -f net10.0` — full suite green. This is the migration's main acceptance gate: ~130 pre-existing tests, the majority asserting HTTP/JSON behavior that must be bit-compatible. Triage any search-semantics failures as engine shims (Task 5/7 follow-ups), never as test edits.
- [ ] Step 5: `git commit -m "Tests: migrate suite off Firely; JSON-based assertions unchanged"`

---

### Task 20: Firely eradication — delete legacy projects, final sweep

**Files:**
- Delete: `src/FhirStore.R4/`, `src/FhirStore.R4B/`, `src/FhirStore.R5/`, `src/FhirStore.CommonVersioned/` (including `Client/CandleClient.cs` — was `<Compile Remove>`d everywhere)
- Modify: `fhir-candle.sln` (remove the four projects + `FhirCandle.Ui.Versioned.shproj` entry), `README.md` (Ignixa dependency + research-status note, net8.0 drop, known regressions: FHIRPath `memberOf()`, token `:above/:below`)

- [ ] Step 1: `git rm -r src/FhirStore.R4 src/FhirStore.R4B src/FhirStore.R5 src/FhirStore.CommonVersioned` + sln edits.
- [ ] Step 2: `dotnet build fhir-candle.sln` — clean across net10.0 and net9.0.
- [ ] Step 3: The gate greps (all must return empty):
  - `grep -rn "Hl7.Fhir\|Firely" src --include=*.cs --include=*.csproj --include=*.props --include=*.razor --include=*.projitems`
  - `grep -rn "extern alias\|AddPackageAliases\|coreR4\|candleR4" src`
- [ ] Step 4: `dotnet test fhir-candle.sln` — everything green (engine tests + migrated suite). Optional but recommended: a quick BenchmarkDotNet or stopwatch comparison of `TypeSearch` and serialize on a 1,000-Patient tenant against a pre-migration checkout, to document the performance win that motivated this whole effort.
- [ ] Step 5: `git commit -m "Remove Firely SDK: fhir-candle now runs entirely on Ignixa"`

---

## Self-review: gaps-table row → task mapping

| Row | Covered by |
|---|---|
| 1 Multi-version/POCO | Tasks 1, 6, 8, 17, 20 |
| 2 JSON serialization | Task 2 |
| 3 XML | Task 3 |
| 4 _summary/_elements | Task 4 |
| 5 FHIRPath | Tasks 1, 8, 14 (memberOf regression: Global Constraints + Task 20 README) |
| 6 Search evaluation | Tasks 5, 6, 8 |
| 7 Sorting | Task 7 |
| 8 Validation | Task 13 |
| 9 Package management | Task 16 |
| 10 Terminology | Task 12 |
| 11 Subscriptions | Task 14 |
| 12 Compartments | Task 11 |
| 13 Narrative | Descoped — never used; not added |
| 14 CapabilityStatement | Task 10 |
| 15 Bundles/transactions | Task 9 |
| 16 MCP | Task 17 |
| 17 Controllers/host | Task 17 |
| 18 UI | Task 18 |
| 19 Tests | Task 19 (+ engine tests throughout) |
| 20 CandleClient/FhirWebSerializer | Deleted in Tasks 17, 20 |
| 21 Ignixa server layers | Descoped — candle keeps its in-memory store; only the FileBasedSearchService wiring pattern was copied (Task 5) |
| 22 Mapping/DeId/TestScript/Fakes | Descoped — no corresponding candle feature |

Name-consistency check performed: `FhirSchemas.Get/ToIgnixaVersion` (T1) used in T2/T5/T8/T11; `SerializationUtils.TryDeserializeFhir/SerializeFhir/BuildOutcomeForRequest` (T2, XML+summary params added T3/T4) used in T6/T8/T13; `CandleSearchService.ParseQuery/CompilePredicate/TestForMatch/Index` + `ParsedQuery`/`CustomModifierFilter` (T5, extended T7) used in T6/T8/T14; `ResourceStore`/`IVersionedResourceStore` (T6) used in T7-T15; `SearchExecutor`/`FhirSortComparer` (T7) used in T8/T10/T11; `RewriteReferences` (T9) internal; `StoreTerminologyService.VsContains/StoreProcessValueSet` (T12) used in T5/T6/T8; `TopicConverter/SubscriptionConverter` (T14) used by T8's `StoreProcessSubscriptionTopic/Subscription`; `InstalledPackage`/`IFhirPackageService` (T16) used in `FhirStoreManager` (T16/T17).
