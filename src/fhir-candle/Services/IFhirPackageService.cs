// <copyright file="IFhirPackageService.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using FhirCandle.Utils;
using Microsoft.Extensions.Hosting;

namespace fhir.candle.Services;

/// <summary>A package that has been resolved and installed into the local package cache.</summary>
/// <param name="Id">              The package id (e.g., hl7.fhir.us.core).</param>
/// <param name="Version">         The installed version or CI tag (e.g., 6.1.0, current, current$branch).</param>
/// <param name="Directive">       The FHIR-style directive for this package (id#version).</param>
/// <param name="ContentDirectory">The expanded package content directory (the package/ folder).</param>
public sealed record InstalledPackage(string Id, string Version, string Directive, string ContentDirectory);

/// <summary>Interface for FHIR package service.</summary>
public interface IFhirPackageService : IHostedService
{
    /// <summary>Occurs when On Changed.</summary>
    event EventHandler<EventArgs>? OnChanged;

    /// <summary>Gets a value indicating whether this service is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Gets a value indicating whether the package service is ready.</summary>
    bool IsReady { get; }

    /// <summary>Deletes the package described by packageDirective.</summary>
    /// <param name="packageDirective">The package directive.</param>
    void DeletePackage(string packageDirective);

    /// <summary>Installs packages based on directives or CI literals.</summary>
    /// <param name="packageDirectives">The package directives.</param>
    /// <param name="ciLiterals">       The ci literals.</param>
    /// <param name="fhirVersions">     The FHIR versions.</param>
    /// <returns>An asynchronous result that yields a List&lt;InstalledPackage&gt;</returns>
    Task<List<InstalledPackage>> InstallPackages(
        string[]? packageDirectives,
        string[]? ciLiterals,
        List<FhirReleases.FhirSequenceCodes>? fhirVersions);

    /// <summary>
    /// Retrieves the FHIR versions supported by a package.
    /// </summary>
    /// <param name="package">The installed package.</param>
    /// <returns>A list of FHIR sequence codes representing the supported versions.</returns>
    Task<List<FhirReleases.FhirSequenceCodes>?> InstalledPackageFhirVersions(InstalledPackage package);

    /// <summary>
    /// Gets the content directory for a specific package.
    /// </summary>
    /// <param name="package">The installed package.</param>
    /// <returns>The content directory for the package, or null if the cache is not configured.</returns>
    string? GetPackageContentDirectory(InstalledPackage package);

    /// <summary>Initializes the FhirPackageService.</summary>
    void Init();

    /// <summary>State has changed.</summary>
    void StateHasChanged();
}
