using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

/// <summary>
/// Pins the rule that decides whether gitignored content is regenerable or the only copy.
/// </summary>
/// <remarks>
/// The rule is deliberately narrow, and the tests exist as much to pin what it does <i>not</i>
/// match as what it does. Every false positive here grades a worktree Careful, and a warning that
/// fires on every row is how people learn to type DELETE without reading.
/// </remarks>
[TestClass]
public sealed class LocalOnlyContentTests
{
    [TestMethod]
    [DataRow(".env")]
    [DataRow(".env.local")]
    [DataRow(".env.production")]
    [DataRow("src/api/.env")]
    [DataRow("local.settings.json")]
    [DataRow("secrets.json")]
    [DataRow("appsettings.Development.local.json")]
    [DataRow(".npmrc")]
    [DataRow(".netrc")]
    [DataRow("certs/dev-signing.pfx")]
    [DataRow("build/app.snk")]
    [DataRow("server.pem")]
    [DataRow("private.key")]
    [DataRow(".ssh/")]
    [DataRow("tools/.aws/config")]
    [DataRow("id_rsa")]
    [DataRow("keys/id_ed25519")]
    public void ContentThatCannotBeRegeneratedIsRecognised(string path) =>
        Assert.IsTrue(LocalOnlyContent.IsIrreplaceable(path), path);

    /// <summary>
    /// Every one of these was observed as a real ignored entry across the 53 worktrees on the
    /// machine this rule was designed against. If any of them starts matching, the room grades
    /// dozens of worktrees Careful and the risk model stops meaning anything.
    /// </summary>
    [TestMethod]
    [DataRow("bin/")]
    [DataRow("obj/")]
    [DataRow("x64/")]
    [DataRow("node_modules/")]
    [DataRow("vcpkg_installed/")]
    [DataRow("src/cascadia/TerminalCore/lib/Generated Files/")]
    [DataRow("src/common/logger/logger/")]
    [DataRow("src/common/version/Version/")]
    [DataRow("build.debug.x64.trace.binlog")]
    [DataRow("build.debug.x64.errors.log")]
    [DataRow("src/settings-ui/Settings.UI/Assets/Settings/search.index.json")]
    [DataRow("Python/frozen_modules/importlib.machinery.h")]
    [DataRow("packages/")]
    [DataRow("TestResults/")]
    public void BuildOutputIsNotMistakenForSomethingPrecious(string path) =>
        Assert.IsFalse(LocalOnlyContent.IsIrreplaceable(path), path);

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("/")]
    public void NothingIsNotASecret(string? path) =>
        Assert.IsFalse(LocalOnlyContent.IsIrreplaceable(path));

    [TestMethod]
    public void MatchingIsCaseInsensitiveBecauseWindowsPathsAre() =>
        Assert.IsTrue(LocalOnlyContent.IsIrreplaceable("Certs/Dev.PFX"));

    [TestMethod]
    public void ADirectoryNamedLikeASecretFileIsStillJustADirectory() =>
        // Git marks collapsed directories with a trailing slash. "keys/" tells us nothing about
        // what is inside, and guessing would be the false-positive that breaks the feature.
        Assert.IsFalse(LocalOnlyContent.IsIrreplaceable("keys/"));

    [TestMethod]
    public void APathThatMerelyContainsTheWordEnvIsNotAnEnvFile()
    {
        Assert.IsFalse(LocalOnlyContent.IsIrreplaceable("src/Environment/Program.cs"));
        Assert.IsFalse(LocalOnlyContent.IsIrreplaceable("environment.ts"));
    }
}
