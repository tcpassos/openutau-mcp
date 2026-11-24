using OpenUtauMcp.Server.Safety;
using OpenUtauMcp.Server.Tools;

namespace OpenUtauMcp.Server.Tests;

public sealed class PathPolicyTests : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "openutau-mcp-tests", Guid.NewGuid().ToString("N"));
    readonly string other = Path.Combine(Path.GetTempPath(), "openutau-mcp-tests-other");

    public PathPolicyTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Check_InsideAllowedDir_ReturnsFullPath() {
        var policy = new PathPolicy([root]);

        string full = policy.Check(Path.Combine(root, "sub", "..", "song.ustx"), null, [".ustx"], write: true);

        Assert.Equal(Path.Combine(root, "song.ustx"), full);
    }

    [Fact]
    public void Check_ProjectFolderIsAllowed() {
        var policy = new PathPolicy([]);

        string full = policy.Check(Path.Combine(other, "mix.wav"), Path.Combine(other, "song.ustx"), [".wav"], write: true);

        Assert.Equal(Path.Combine(other, "mix.wav"), full);
    }

    [Fact]
    public void Check_OutsideAllowedDirs_Throws() {
        var policy = new PathPolicy([root]);

        var e = Assert.Throws<ToolInputException>(() => policy.Check(Path.Combine(other, "x.ustx"), null, [".ustx"], write: false));

        Assert.Equal(PathPolicy.NotAllowed, e.Code);
    }

    [Fact]
    public void Check_PrefixOfAllowedDirIsNotInside() {
        var policy = new PathPolicy([root]);

        var e = Assert.Throws<ToolInputException>(() => policy.Check(root + "-evil\\x.ustx", null, [".ustx"], write: false));

        Assert.Equal(PathPolicy.NotAllowed, e.Code);
    }

    [Fact]
    public void Check_ExistingFileWithoutOverwrite_Throws() {
        var policy = new PathPolicy([root]);
        string file = Path.Combine(root, "song.ustx");
        File.WriteAllText(file, "x");

        Assert.Equal(PathPolicy.AlreadyExists, Assert.Throws<ToolInputException>(() => policy.Check(file, null, [".ustx"], write: true)).Code);
        Assert.Equal(file, policy.Check(file, null, [".ustx"], write: true, overwrite: true));
    }

    [Fact]
    public void Check_WrongExtension_Throws() {
        var policy = new PathPolicy([root]);

        var e = Assert.Throws<ToolInputException>(() => policy.Check(Path.Combine(root, "song.exe"), null, [".ustx"], write: true));

        Assert.Equal("UNSUPPORTED_FORMAT", e.Code);
    }
}
