using System.Security.Cryptography;
using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>
/// Real <see cref="IFileSystem"/> over <see cref="System.IO"/> + <see cref="SHA256"/>.
/// </summary>
/// <remarks>
/// Used by the app's reversible mutation engines (<see cref="Services.PackageCacheMover"/> and
/// <see cref="Services.VhdProvisioner"/>), which the app composes and runs only after an explicit user
/// confirmation. In tests this type is exercised solely against throwaway temp directories (see the
/// <c>Integration</c>-categorised tests); unit tests use an in-memory filesystem instead.
/// </remarks>
public sealed class SystemFileSystem : IFileSystem
{
    /// <inheritdoc />
    public bool DirectoryExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    /// <inheritdoc />
    public bool FileExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
    }

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList()
            : Array.Empty<string>();

    /// <inheritdoc />
    public long GetFileLength(string path) => new FileInfo(path).Length;

    /// <inheritdoc />
    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.Copy(sourcePath, destinationPath, overwrite);
    }

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public void WriteAllText(string path, string contents)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, contents);
    }

    /// <inheritdoc />
    public string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }
}
