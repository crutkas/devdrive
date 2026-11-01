using System.Text.Json.Serialization;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Serialization;

// Source-generated System.Text.Json metadata for every type this library reads or writes. Reflection-based
// serialization is unavailable under NativeAOT and unsafe under trimming, and Dev Config publishes this
// library with both. Each context mirrors one set of options the call sites used before they moved here, so
// the bytes on the wire and on disk are unchanged. Reads go through InitializerPreservingJson so members a
// payload omits keep their initializers, as they did under reflection. JsonWireFormatTests and
// SourceGeneratedJsonParityTests pin all of it.

/// <summary>
/// JSON exchanged with the elevated helper (<c>DevDriveManager.FilterProbe.exe</c>) and read back from the
/// resize and VHDX PowerShell scripts: PascalCase out, member names matched case-insensitively in.
/// </summary>
/// <remarks>
/// Mirrors <c>new JsonSerializerOptions { PropertyNameCaseInsensitive = true }</c>. The helper writes
/// PascalCase and PowerShell's <c>ConvertTo-Json</c> keeps whatever casing the script used, so reads must
/// stay case-insensitive.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ResizePlan))]
[JsonSerializable(typeof(ResizeFeasibility))]
[JsonSerializable(typeof(ResizeExecuteOutcome))]
[JsonSerializable(typeof(ResizeFinalState))]
[JsonSerializable(typeof(VhdProvisionPlan))]
[JsonSerializable(typeof(VhdRevertPlan))]
[JsonSerializable(typeof(VhdProvisionResult))]
[JsonSerializable(typeof(VhdFinalState))]
[JsonSerializable(typeof(VhdCapabilityState))]
internal sealed partial class HelperJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The free-space history file (<c>%LOCALAPPDATA%\DevDriveManager\free-space-history.json</c>): compact
/// PascalCase, member names matched case-sensitively.
/// </summary>
/// <remarks>Mirrors <c>new JsonSerializerOptions { WriteIndented = false }</c>, which is the default.</remarks>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(FreeSpaceSample[]))]
[JsonSerializable(typeof(IReadOnlyList<FreeSpaceSample>))]
internal sealed partial class FreeSpaceHistoryJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The reversibility journal (<c>%LOCALAPPDATA%\DevDriveManager\reversibility.json</c>): indented
/// PascalCase, member names matched case-sensitively.
/// </summary>
/// <remarks>
/// The journal was written with <c>new JsonSerializerOptions { WriteIndented = true }</c> and read with the
/// default options. Indentation only affects writing, so one context reproduces both.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReversibilityEntry[]))]
internal sealed partial class ReversibilityJsonContext : JsonSerializerContext
{
}
