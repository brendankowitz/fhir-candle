// <copyright file="PackageServiceTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
//     Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// </copyright>

using System.Formats.Tar;
using System.IO.Compression;
using fhir.candle.Services;
using FhirCandle.Models;
using Shouldly;

namespace fhir.candle.Tests;

public class PackageServiceTests
{
    [Theory]
    [InlineData("hl7.fhir.us.core#6.1.0", "hl7.fhir.us.core@6.1.0")]
    [InlineData("hl7.fhir.us.core@6.1.0", "hl7.fhir.us.core@6.1.0")]
    [InlineData("hl7.fhir.us.core", "hl7.fhir.us.core")]
    [InlineData("hl7.fhir.us.core#current", "hl7.fhir.us.core@current")]
    [InlineData("hl7.fhir.us.core#current$branch-name", "hl7.fhir.us.core@current$branch-name")]
    [InlineData("pkg@1.0.0#extra", "pkg@1.0.0#extra")]
    public void NormalizeDirectiveMatchesLegacyBehavior(string input, string expected)
    {
        FhirPackageService.NormalizeDirective(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "Latest")]
    [InlineData("", "Latest")]
    [InlineData("latest", "Latest")]
    [InlineData("current", "ContinuousIntegration")]
    [InlineData("current$my-branch", "ContinuousIntegration")]
    [InlineData("dev", "Local")]
    [InlineData("dev$my-branch", "Local")]
    [InlineData("6.1.0", "Passthrough")]
    [InlineData("4.0.x", "Passthrough")]
    [InlineData("currentX", "Passthrough")]
    [InlineData("devX", "Passthrough")]
    public void GetVersionHandlingTypeMatchesLegacyBehavior(string? version, string expected)
    {
        FhirPackageService.GetVersionHandlingType(version).ToString().ShouldBe(expected);
    }

    [Fact]
    public void ExtractPackageTarballWritesContentAndReturnsManifest()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "data", "common", "minimal.test.pkg.tgz");
        File.Exists(fixture).ShouldBeTrue($"missing test fixture: {fixture}");

        string packageRoot = Path.Combine(Path.GetTempPath(), $"candle-pkg-test-{Guid.NewGuid():N}");

        try
        {
            FhirNpmPackageDetails details;
            using (FileStream fs = File.OpenRead(fixture))
            {
                details = FhirPackageService.ExtractPackageTarball(fs, packageRoot);
            }

            details.Name.ShouldBe("minimal.test.pkg");
            details.Version.ShouldBe("0.0.1");
            details.FhirVersions.ShouldHaveSingleItem().ShouldBe("4.0.1");

            string contentDir = Path.Combine(packageRoot, "package");
            File.Exists(Path.Combine(contentDir, "package.json")).ShouldBeTrue();

            string extractedSearchParameter = Path.Combine(contentDir, "SearchParameter-test.json");
            File.Exists(extractedSearchParameter).ShouldBeTrue();
            File.ReadAllBytes(extractedSearchParameter).ShouldBe(ReadTarEntryBytes(fixture, "package/SearchParameter-test.json"));
        }
        finally
        {
            if (Directory.Exists(packageRoot))
            {
                Directory.Delete(packageRoot, true);
            }
        }
    }

    [Fact]
    public void ExtractPackageTarballWithoutManifestThrows()
    {
        using MemoryStream tgz = new();
        using (GZipStream gz = new(tgz, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter writer = new(gz))
        {
            PaxTarEntry entry = new(TarEntryType.RegularFile, "package/not-a-manifest.json")
            {
                DataStream = new MemoryStream("{}"u8.ToArray()),
            };
            writer.WriteEntry(entry);
        }

        tgz.Position = 0;

        string packageRoot = Path.Combine(Path.GetTempPath(), $"candle-pkg-test-{Guid.NewGuid():N}");

        try
        {
            Should.Throw<Exception>(() => FhirPackageService.ExtractPackageTarball(tgz, packageRoot));
        }
        finally
        {
            if (Directory.Exists(packageRoot))
            {
                Directory.Delete(packageRoot, true);
            }
        }
    }

    private static byte[] ReadTarEntryBytes(string tgzPath, string entryName)
    {
        using FileStream fs = File.OpenRead(tgzPath);
        using GZipStream gz = new(fs, CompressionMode.Decompress);
        using TarReader reader = new(gz);

        while (reader.GetNextEntry() is { } entry)
        {
            if ((entry.Name == entryName) && (entry.DataStream is not null))
            {
                using MemoryStream ms = new();
                entry.DataStream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        throw new InvalidOperationException($"entry {entryName} not found in {tgzPath}");
    }
}
