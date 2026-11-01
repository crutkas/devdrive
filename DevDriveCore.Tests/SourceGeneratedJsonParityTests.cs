using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using DevDriveCore.Models;
using DevDriveCore.Serialization;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Proves each source-generated context reads and writes exactly what the reflection-based serializer
/// did with the options the call sites used before the move. The reflection side of every comparison
/// IS the old code path: same <see cref="JsonSerializerOptions"/>, same types. The generated side reads
/// through <see cref="InitializerPreservingJson"/>, as every production call site does.
/// </summary>
/// <remarks>
/// Covers the shapes that differ most easily between the two: members missing from the input (their
/// initializers must survive, e.g. <see cref="ResizeFeasibility.IsReadOnlyProbe"/>), member-name casing,
/// unknown members, explicit nulls, numbers written as strings, escaping of non-ASCII and HTML-sensitive
/// text, offsets and fractional seconds on timestamps, and the extremes of unsigned sizes.
/// </remarks>
[TestClass]
public sealed partial class SourceGeneratedJsonParityTests
{
    private const string AwkwardText = "a \"quoted\" <tag> & 'apostrophe' \\ / é ✓ — \u0001 \t end";

    // The exact options each call site passed before it moved to a source-generated context.
    private static readonly JsonSerializerOptions LegacyHelperOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions LegacyHistoryOptions = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions LegacyJournalWriteOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions LegacyJournalReadOptions = new();

    [TestMethod]
    public void VolumeResizerOptionsAreTheHelperContext()
    {
        Assert.AreSame(HelperJsonContext.Default, VolumeResizer.JsonOptions.TypeInfoResolver);
        Assert.IsTrue(VolumeResizer.JsonOptions.PropertyNameCaseInsensitive);
        Assert.IsFalse(VolumeResizer.JsonOptions.WriteIndented);
        Assert.IsNull(VolumeResizer.JsonOptions.PropertyNamingPolicy);
    }

    [TestMethod]
    public void ResizePlanMatchesReflection() => AssertHelperParity(
        HelperJsonContext.Default.ResizePlan,
        new ResizePlan(),
        new ResizePlan
        {
            SourceVolumeLetter = 'E',
            ShrinkBytes = ulong.MaxValue,
            NewDriveLetter = '<',
            Label = AwkwardText,
            ExpectedDiskNumber = -1,
            ExpectedDiskUniqueId = "SCSI\\Disk&Ven_NVMe",
            ExpectedPartitionNumber = int.MaxValue,
            ExpectedPartitionOffsetBytes = 0,
            ExpectedPartitionGuid = "{11111111-2222-3333-4444-555555555555}",
            ExpectedAlignedShrinkBytes = 53_687_091_200,
            ExecuteAuthorized = true,
        });

    [TestMethod]
    public void ResizeFeasibilityMatchesReflection() => AssertHelperParity(
        HelperJsonContext.Default.ResizeFeasibility,
        new ResizeFeasibility(),
        new ResizeFeasibility
        {
            CanProceed = true,
            Reason = AwkwardText,
            SourceVolumeLetter = 'C',
            NewDriveLetter = 'D',
            RequestedShrinkBytes = 1,
            AlignedShrinkBytes = 2,
            ReclaimableBytes = 3,
            SourceSizeBytesBefore = 4,
            SourceSizeBytesAfter = ulong.MaxValue,
            DiskNumber = 7,
            DiskUniqueId = "disk",
            PartitionNumber = 3,
            PartitionOffsetBytes = 1_048_576,
            PartitionGuid = "{guid}",
            Steps = ["Shrink C:", AwkwardText],
            IsReadOnlyProbe = false,
        });

    [TestMethod]
    public void ResizeExecuteOutcomeMatchesReflection() => AssertHelperParity(
        HelperJsonContext.Default.ResizeExecuteOutcome,
        new ResizeExecuteOutcome(),
        new ResizeExecuteOutcome
        {
            Success = true,
            Executed = true,
            StateUnknown = true,
            Message = AwkwardText,
            SourceVolumeLetter = 'C',
            NewDriveLetter = 'D',
            DevDriveBytes = ulong.MaxValue,
            DiskNumber = 0,
            PartitionNumber = 4,
            FileSystem = "ReFS",
            IsDevDrive = true,
            CompletedSteps = ["one", "two"],
        });

    [TestMethod]
    public void ResizeFinalStateMatchesReflection() => AssertHelperParity(
        HelperJsonContext.Default.ResizeFinalState,
        new ResizeFinalState(),
        new ResizeFinalState
        {
            FinalDriveLetter = 'd',
            FinalPartitionSizeBytes = 107_374_182_400,
            FinalFileSystem = "ReFS",
            FinalDiskNumber = 7,
            FinalPartitionNumber = 4,
            IsDevDrive = true,
        });

    [TestMethod]
    public void VhdProvisionPlanMatchesReflection() => AssertHelperParity(
        HelperJsonContext.Default.VhdProvisionPlan,
        new VhdProvisionPlan(),
        new VhdProvisionPlan
        {
            FilePath = @"C:\Dev Drives\ünïcode\dev.vhdx",
            MaximumSizeBytes = ulong.MaxValue,
            VolumeSizeBytes = 68_719_476_736,
            DynamicallyExpanding = false,
            DriveLetter = 'Z',
            Label = AwkwardText,
            ExecuteAuthorized = true,
        });

    [TestMethod]
    public void VhdRevertPlanMatchesReflection() => AssertHelperParity(
        HelperJsonContext.Default.VhdRevertPlan,
        new VhdRevertPlan(),
        new VhdRevertPlan { FilePath = @"\\?\C:\DevDrives\dev.vhdx", ExecuteAuthorized = true });

    [TestMethod]
    public void VhdProvisionResultMatchesReflection() => AssertHelperParity(
        HelperJsonContext.Default.VhdProvisionResult,
        new VhdProvisionResult(),
        new VhdProvisionResult
        {
            Success = true,
            Executed = true,
            StateUnknown = true,
            RolledBack = true,
            Message = AwkwardText,
            FilePath = @"C:\DevDrives\dev.vhdx",
            PhysicalPath = @"\\.\PhysicalDrive7",
            DiskNumber = 7,
            DriveLetter = 'V',
            SizeBytes = ulong.MaxValue,
            FileSystem = "ReFS",
            ReversibilityId = "vhd:c:\\devdrives\\dev.vhdx",
        });

    [TestMethod]
    public void VhdFinalStateMatchesReflection() => AssertHelperParity(
        HelperJsonContext.Default.VhdFinalState,
        new VhdFinalState(),
        new VhdFinalState { DriveLetter = 'V', SizeBytes = 68_719_476_736, FileSystem = "ReFS", IsDevDrive = true });

    [TestMethod]
    public void VhdCapabilityStateMatchesReflection() => AssertHelperParity(
        HelperJsonContext.Default.VhdCapabilityState,
        new VhdCapabilityState(),
        new VhdCapabilityState { Supported = true, Build = 26100, Revision = 4351, HasDevDriveParameter = true });

    [TestMethod]
    public void FreeSpaceHistoryMatchesReflection()
    {
        FreeSpaceSample[] samples =
        [
            new(),
            new()
            {
                TakenAtUtc = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 891, TimeSpan.FromHours(-7)).AddTicks(1234),
                VolumeId = AwkwardText,
                TotalBytes = long.MaxValue,
                FreeBytes = long.MinValue,
            },
            new()
            {
                TakenAtUtc = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
                VolumeId = "G:",
                TotalBytes = 1000,
                FreeBytes = 2000,
            },
        ];

        AssertWriteParity(samples, FreeSpaceHistoryJsonContext.Default.FreeSpaceSampleArray, LegacyHistoryOptions);
        AssertWriteParity<IReadOnlyList<FreeSpaceSample>>(
            samples.ToList(),
            FreeSpaceHistoryJsonContext.Default.IReadOnlyListFreeSpaceSample,
            LegacyHistoryOptions);
        AssertReadParity(
            json => InitializerPreservingJson.DeserializeArray(
                json,
                FreeSpaceHistoryJsonContext.Default.FreeSpaceSampleArray,
                FreeSpaceHistoryJsonContext.Default.FreeSpaceSample),
            LegacyHistoryOptions,
            ArrayInputs(samples, LegacyHistoryOptions));
    }

    [TestMethod]
    public void ReversibilityJournalMatchesReflection()
    {
        ReversibilityEntry[] entries =
        [
            new(),
            new()
            {
                Id = "package-cache:npm_config_cache",
                Kind = ReversibilityKinds.PackageCacheMove,
                TimestampUtc = new DateTimeOffset(2024, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(5.5)),
                ToolName = AwkwardText,
                EnvironmentVariable = "npm_config_cache",
                EnvironmentValueWasSet = true,
                PriorEnvironmentValue = @"D:\old;%USERPROFILE%",
                SourcePath = @"C:\Users\dév\AppData\Local\npm-cache",
                TargetPath = @"G:\packages\npm",
                SourceDeleted = true,
            },
        ];

        AssertWriteParity(entries, ReversibilityJsonContext.Default.ReversibilityEntryArray, LegacyJournalWriteOptions);
        AssertReadParity(
            json => InitializerPreservingJson.DeserializeArray(
                json,
                ReversibilityJsonContext.Default.ReversibilityEntryArray,
                ReversibilityJsonContext.Default.ReversibilityEntry),
            LegacyJournalReadOptions,
            ArrayInputs(entries, LegacyJournalWriteOptions));
    }

    [TestMethod]
    public void RawGeneratedMetadataStillDropsInitializers()
    {
        // Why InitializerPreservingJson exists: on .NET 10 the generated metadata assigns default to every
        // init-only member the payload omits (dotnet/runtime#84484). When this starts failing, the
        // runtime has fixed it and the helper can go.
        ResizeFeasibility raw = JsonSerializer.Deserialize("{}", HelperJsonContext.Default.ResizeFeasibility)!;
        Assert.IsFalse(raw.IsReadOnlyProbe);
        Assert.IsNull(raw.Reason);

        ResizeFeasibility preserved = InitializerPreservingJson.Deserialize("{}", HelperJsonContext.Default.ResizeFeasibility)!;
        Assert.IsTrue(preserved.IsReadOnlyProbe);
        Assert.AreEqual(string.Empty, preserved.Reason);
    }

    private static void AssertHelperParity<T>(JsonTypeInfo<T> generated, T empty, T populated)
        where T : class, new()
    {
        AssertWriteParity(empty, generated, LegacyHelperOptions);
        AssertWriteParity(populated, generated, LegacyHelperOptions);

        string emptyJson = JsonSerializer.Serialize(empty, LegacyHelperOptions);
        string populatedJson = JsonSerializer.Serialize(populated, LegacyHelperOptions);
        AssertReadParity(
            json => InitializerPreservingJson.Deserialize(json, generated),
            LegacyHelperOptions,
            [.. ObjectInputs(emptyJson), .. ObjectInputs(populatedJson)]);
    }

    private static void AssertWriteParity<T>(T value, JsonTypeInfo<T> generated, JsonSerializerOptions legacy)
    {
        string expected = JsonSerializer.Serialize(value, legacy);
        Assert.AreEqual(expected, JsonSerializer.Serialize(value, generated), $"write of {typeof(T).Name}");

        using var stream = new MemoryStream();
        JsonSerializer.Serialize(stream, value, generated);
        Assert.AreEqual(expected, System.Text.Encoding.UTF8.GetString(stream.ToArray()), $"UTF-8 write of {typeof(T).Name}");
    }

    private static void AssertReadParity<T>(Func<string, T?> read, JsonSerializerOptions legacy, IEnumerable<string> inputs)
    {
        foreach (string input in inputs)
        {
            string expected = Outcome(() => JsonSerializer.Deserialize<T>(input, legacy));
            string actual = Outcome(() => read(input));
            Assert.AreEqual(expected, actual, $"read of {typeof(T).Name} from {input}");
        }

        // A read result is compared by writing it back out with the legacy options, so every member
        // (collections included) takes part. A throw is compared by exception type and message, because
        // one caller shows the message to the user. The message's JSON path, line and byte position must
        // match exactly. Its "could not be converted to <type>" wording is normalized: for a wrongly typed
        // value-type member the generated metadata names the record (it reads init-only members through
        // a constructor delegate, the same limitation InitializerPreservingJson works around) where
        // reflection named the member's type.
        string Outcome(Func<T?> read)
        {
            try
            {
                return "value:" + JsonSerializer.Serialize(read(), legacy);
            }
            catch (Exception ex)
            {
                return $"throws:{ex.GetType().FullName}:{ConvertedToType().Replace(ex.Message, "could not be converted to <type>.")}";
            }
        }
    }

    // Variants of one object's JSON that probe casing, unknown and missing members, nulls, number
    // handling, malformed text, and type errors in a payload that is also missing members.
    private static IEnumerable<string> ObjectInputs(string pascalJson)
    {
        yield return pascalJson;
        yield return "{}";
        yield return " \r\n { \r\n } \r\n ";
        yield return "null";
        yield return "[]";
        yield return "42";
        yield return "{";
        yield return "not json";
        yield return "{} {}";
        yield return "{\"a\":1,}";
        yield return "{/* comment */}";
        yield return "{\"Unknown\":{\"Nested\":[1,2,3]},\"Other\":null}";
        yield return "{\"Unknown\":\"\\uD800 lone surrogate\"}";
        yield return "{\"\\uD800\":1}";
        yield return PropertyName().Replace(pascalJson, m => $"\"{char.ToLowerInvariant(m.Groups[1].Value[0])}{m.Groups[1].Value[1..]}\":");
        yield return PropertyName().Replace(pascalJson, m => $"\"{m.Groups[1].Value.ToUpperInvariant()}\":");
        yield return PropertyName().Replace(pascalJson, m => $"\"{m.Groups[1].Value}\":null,\"{m.Groups[1].Value}\":");
        yield return pascalJson.Insert(1, "\"Unexpected\":true,");
        yield return NumberValue().Replace(pascalJson, m => $":\"{m.Groups[1].Value}\"");

        foreach (Match member in PropertyName().Matches(pascalJson))
        {
            string name = member.Groups[1].Value;
            yield return $"{{\"{name}\":null}}";
            yield return $"{{ \"{name.ToLowerInvariant()}\" : null }}";
            yield return $"{{\r\n  \"Unknown\": 1,\r\n  \"{name}\" : [ 1, {{ }} ]\r\n}}";
        }
    }

    private static IEnumerable<string> ArrayInputs<T>(T[] values, JsonSerializerOptions legacy)
    {
        string json = JsonSerializer.Serialize(values, legacy);
        yield return json;
        yield return "[]";
        yield return "null";
        yield return "{}";
        yield return "[{}]";
        yield return "[null]";
        yield return "[{}, null, 1]";
        yield return "[{},";
        yield return " [ { } ,\r\n { \"Unknown\": true } ] ";
        foreach (string variant in ObjectInputs(JsonSerializer.Serialize(values[^1])))
        {
            yield return $"[{variant}]";
            yield return $"[{json[1..^1]}, {variant}]";
        }
    }

    [GeneratedRegex("\"([A-Za-z]+)\":")]
    private static partial Regex PropertyName();

    [GeneratedRegex(":(-?[0-9]+)(?=[,}])")]
    private static partial Regex NumberValue();

    [GeneratedRegex(@"could not be converted to \S+\.")]
    private static partial Regex ConvertedToType();
}
