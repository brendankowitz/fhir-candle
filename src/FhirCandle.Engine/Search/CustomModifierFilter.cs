namespace FhirCandle.Search;

/// <summary>
/// A search modifier that Ignixa's expression parser does not support natively (it throws when it
/// encounters one), intercepted before parsing and applied afterwards against the indexed values.
/// </summary>
/// <param name="Code">The search parameter code, e.g. "code" or "subject" (without the modifier suffix).</param>
/// <param name="Modifier">One of "in", "not-in", "identifier".</param>
/// <param name="Value">The raw query value for this parameter.</param>
public sealed record CustomModifierFilter(string Code, string Modifier, string Value);
