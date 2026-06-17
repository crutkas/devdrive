using System.Reflection;
using System.Runtime.InteropServices;
using DevDriveCore.Platform;

namespace DevDriveCore.Tests;

/// <summary>
/// Regression guard for the P/Invoke constants and struct layouts in <see cref="NativeVhdApi"/>. The real
/// attach/create code is never exercised by a test (every other test injects a mock
/// <see cref="DevDriveCore.Abstractions.INativeVhdApi"/>), so these reflection-based asserts pin the
/// values that MUST match the public <c>virtdisk.h</c> — a wrong flag or a re-padded struct would
/// silently break real Dev Drive creation with no other test catching it.
/// </summary>
/// <remarks>
/// The constants and the parameter structs are <c>private</c> members of <see cref="NativeVhdApi"/>, so
/// the test reads them via reflection rather than widening their visibility just for testing.
/// </remarks>
[TestClass]
public sealed class NativeVhdApiFlagsTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags PrivateNested = BindingFlags.NonPublic;

    private static uint ConstUInt(string name)
    {
        FieldInfo field = typeof(NativeVhdApi).GetField(name, PrivateStatic)
            ?? throw new InvalidOperationException($"const '{name}' not found on NativeVhdApi.");
        return (uint)field.GetRawConstantValue()!;
    }

    private static Type NestedType(string name) =>
        typeof(NativeVhdApi).GetNestedType(name, PrivateNested)
        ?? throw new InvalidOperationException($"nested type '{name}' not found on NativeVhdApi.");

    [TestMethod]
    public void AttachFlags_MatchPublicVirtdiskValues()
    {
        // F2: NO_DRIVE_LETTER is 0x2 in the public virtdisk.h. 0x1 is READ_ONLY — using it would attach
        // the VHDX read-only AND let Windows auto-assign a letter (the exact opposite of intent).
        Assert.AreEqual(0x2u, ConstUInt("AttachVirtualDiskFlagNoDriveLetter"), "NO_DRIVE_LETTER must be 0x2.");
        Assert.AreEqual(0x4u, ConstUInt("AttachVirtualDiskFlagPermanentLifetime"), "PERMANENT_LIFETIME must be 0x4.");

        // They must be distinct, non-overlapping bits, and NO_DRIVE_LETTER must NOT be the READ_ONLY bit.
        Assert.AreNotEqual(0x1u, ConstUInt("AttachVirtualDiskFlagNoDriveLetter"), "NO_DRIVE_LETTER must not be the READ_ONLY bit (0x1).");
        Assert.AreEqual(
            0u,
            ConstUInt("AttachVirtualDiskFlagNoDriveLetter") & ConstUInt("AttachVirtualDiskFlagPermanentLifetime"),
            "NO_DRIVE_LETTER and PERMANENT_LIFETIME must be independent bits.");
    }

    [TestMethod]
    public void AttachParameters_AreExactlyEightBytes()
    {
        // ATTACH_VIRTUAL_DISK_PARAMETERS V1 == two uints (Version + Reserved) == 8 bytes. A re-padded or
        // extended struct would mis-marshal the attach call.
        Assert.AreEqual(8, Marshal.SizeOf(NestedType("ATTACH_VIRTUAL_DISK_PARAMETERS")));
    }

    [TestMethod]
    public void CreateParametersV1_HaveTheExpectedManagedLayout()
    {
        // CREATE_VIRTUAL_DISK_PARAMETERS_V1: Version + Guid + MaximumSize + BlockSize + SectorSize, then
        // two LPWStr pointers. The fixed prefix marshals to 40 bytes; the two pointers add 2 * IntPtr.Size
        // (56 on x64, 48 on x86). Asserting against IntPtr.Size keeps the guard architecture-robust while
        // still catching any field add/remove or re-ordering.
        int expected = 40 + (2 * IntPtr.Size);
        Assert.AreEqual(expected, Marshal.SizeOf(NestedType("CREATE_VIRTUAL_DISK_PARAMETERS_V1")));
    }
}
