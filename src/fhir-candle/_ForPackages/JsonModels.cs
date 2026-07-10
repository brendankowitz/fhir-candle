using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace fhir.candle._ForPackages
{
    /// <summary>Information about a CI branch, as returned from a branch query to the server.</summary>
    public class CiBranchRecord
    {
        /// <summary>The relative name for this record.</summary>
        [JsonProperty(PropertyName = "name")]
        public string? Name;

        /// <summary>The size of the directory or file.</summary>
        [JsonProperty(PropertyName = "size")]
        public long? Size;

        /// <summary>URL of the resource, relative to the current URL.</summary>
        [JsonProperty(PropertyName = "url")]
        public string? Url;

        /// <summary>The file/directory mode.</summary>
        /// <remarks>This looks like a flag, but I cannot find documentation on values.</remarks>
        [JsonProperty(PropertyName = "mode")]
        public long? ModeFlag;

        /// <summary>True if is directory, false if not.</summary>
        [JsonProperty(PropertyName = "is_dir")]
        public bool? IsDirectory;

        /// <summary>True if is symbolic link, false if not.</summary>
        [JsonProperty(PropertyName = "is_symlink")]
        public bool? IsSymbolicLink;
    }

    /// <summary>FHIR QA record from the CI server.</summary>
    public class FhirCiQaRecord
    {
        [JsonProperty(PropertyName = "url")]
        public string? Url { get; set; }

        [JsonProperty(PropertyName = "name")]
        public string? Name { get; set; }

        [JsonProperty(PropertyName = "title")]
        public string? Title { get; set; }

        [JsonProperty(PropertyName = "description")]
        public string? Description { get; set; }

        [JsonProperty(PropertyName = "status")]
        public string? Status { get; set; }

        [JsonProperty(PropertyName = "package-id")]
        public string? PackageId { get; set; }

        [JsonProperty(PropertyName = "ig-ver")]
        public string? PackageVersion { get; set; }

        [JsonProperty(PropertyName = "date")]
        public DateTimeOffset? BuildDate { get; set; }

        [JsonProperty(PropertyName = "dateISO8601")]
        public DateTimeOffset? BuildDateIso { get; set; }

        [JsonProperty(PropertyName = "errs")]
        public int? ErrorCount { get; set; }

        [JsonProperty(PropertyName = "warnings")]
        public int? WarningCount { get; set; }

        [JsonProperty(PropertyName = "hints")]
        public int? HintCount { get; set; }

        [JsonProperty(PropertyName = "suppressed-hints")]
        public int? SuppressedHintCount { get; set; }

        [JsonProperty(PropertyName = "suppressed-warnings")]
        public int? SuppressedWarningCount { get; set; }

        [JsonProperty(PropertyName = "version")]
        public string? FhirVersion { get; set; }

        [JsonProperty(PropertyName = "tool")]
        public string? ToolingVersion { get; set; }

        [JsonProperty(PropertyName = "maxMemory")]
        public long? MaxMemoryUsedToBuild { get; set; }

        [JsonProperty(PropertyName = "repo")]
        public string? RepositoryUrl { get; set; }
    }
}
