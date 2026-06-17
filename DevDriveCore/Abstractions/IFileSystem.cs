namespace DevDriveCore.Abstractions;

/// <summary>
/// Read/write filesystem seam used by the app's reversible mutation engines —
/// <see cref="DevDriveCore.Services.PackageCacheMover"/> and
/// <see cref="DevDriveCore.Services.VhdProvisioner"/> — both composed by the app and run only after an
/// explicit user confirmation.
/// </summary>
/// <remarks>
/// <para>This is deliberately distinct from the read-only <see cref="IFileSystemProbe"/>: that seam
/// only answers existence/size questions for detection, whereas this one <b>creates, copies and
/// deletes</b>. Every file mutation an engine performs flows through this interface so the engines
/// can be unit-tested against an in-memory filesystem (and, in integration tests, against a
/// throwaway temp directory) — never against arbitrary real-machine locations.</para>
/// <para>The real implementation is <see cref="DevDriveCore.Platform.SystemFileSystem"/>.</para>
/// </remarks>
public interface IFileSystem
{
    /// <summary>True when <paramref name="path"/> exists as a directory.</summary>
    bool DirectoryExists(string path);

    /// <summary>True when <paramref name="path"/> exists as a file.</summary>
    bool FileExists(string path);

    /// <summary>Creates <paramref name="path"/> (and any missing parents). No-op if it already exists.</summary>
    void CreateDirectory(string path);

    /// <summary>Deletes a directory. <paramref name="recursive"/> removes its contents too. No-op if absent.</summary>
    void DeleteDirectory(string path, bool recursive);

    /// <summary>Deletes a file. No-op if absent.</summary>
    void DeleteFile(string path);

    /// <summary>Enumerates every file beneath <paramref name="directory"/> recursively, returning full paths.</summary>
    IReadOnlyList<string> EnumerateFiles(string directory);

    /// <summary>Returns the length in bytes of <paramref name="path"/>.</summary>
    long GetFileLength(string path);

    /// <summary>
    /// Copies a single file, creating the destination's parent directory when needed. When
    /// <paramref name="overwrite"/> is <c>false</c> and the destination exists the implementation throws.
    /// </summary>
    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>Reads the full text of <paramref name="path"/> (UTF-8).</summary>
    string ReadAllText(string path);

    /// <summary>Writes <paramref name="contents"/> to <paramref name="path"/> (UTF-8), creating parents as needed.</summary>
    void WriteAllText(string path, string contents);

    /// <summary>
    /// Returns the lower-case hexadecimal SHA-256 of the file's bytes. Used to verify that a copied
    /// file is byte-identical to its source before the move is considered successful.
    /// </summary>
    string ComputeSha256(string path);
}
