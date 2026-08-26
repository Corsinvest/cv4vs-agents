/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The path shapes handed to the CLI.
/// <para>These matter because the CLI compares strings, not paths: it matches its own
/// <c>process.cwd()</c> against the lock file's <c>workspaceFolders</c> case-sensitively, so an
/// upper-case drive letter does not fail loudly — IDE discovery just never happens.</para></summary>
public class PathHelpersTests
{
    [Theory]
    [InlineData(@"C:\proj\demo", @"C:\proj\demo\src\Program.cs", "src/Program.cs")]
    // Either separator, mixed, and a trailing slash on the root: same answer.
    [InlineData(@"C:/proj/demo", @"C:\proj\demo\src\Program.cs", "src/Program.cs")]
    [InlineData(@"C:\proj\demo\", @"C:\proj\demo/src\Program.cs", "src/Program.cs")]
    // Case-insensitive, because Windows is.
    [InlineData(@"c:\proj\demo", @"C:\PROJ\Demo\src\Program.cs", "src/Program.cs")]
    // The path IS the root.
    [InlineData(@"C:\proj\demo", @"C:\proj\demo", "")]
    [InlineData(@"C:\proj\demo", @"C:\proj\demo\", "")]
    // Outside the root: handed back whole, only re-slashed. NOT walked up with "..".
    [InlineData(@"C:\proj\demo", @"C:\other\File.cs", "C:/other/File.cs")]
    // A sibling whose name merely STARTS with the root's: not inside it.
    [InlineData(@"C:\proj\demo", @"C:\proj\demo2\File.cs", "C:/proj/demo2/File.cs")]
    // Nothing to work with.
    [InlineData(@"C:\proj\demo", "", "")]
    [InlineData(@"C:\proj\demo", null, "")]
    [InlineData("", @"C:\proj\demo\File.cs", "C:/proj/demo/File.cs")]
    public void Relative_answers_in_forward_slashes(string root, string path, string expected)
        => Assert.Equal(expected, PathHelpers.Relative(root, path));

    [Theory]
    [InlineData(@"C:\proj\demo", @"c:\proj\demo")]
    [InlineData(@"K:\source\repos", @"k:\source\repos")]
    // Already lower-case, and a path with no drive at all: untouched.
    [InlineData(@"c:\proj\demo", @"c:\proj\demo")]
    [InlineData(@"\\server\share\file.cs", @"\\server\share\file.cs")]
    [InlineData("relative/path.cs", "relative/path.cs")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void LowercaseDrive_lowers_only_the_drive_letter(string path, string expected)
        => Assert.Equal(expected, PathHelpers.LowercaseDrive(path));

    [Theory]
    [InlineData(@"C:\proj\demo\File.cs", "file:///c:/proj/demo/File.cs")]
    [InlineData(@"c:\proj\demo\File.cs", "file:///c:/proj/demo/File.cs")]
    // Only the drive letter is lowered — the rest of the path keeps its case.
    [InlineData(@"C:\Proj\Demo\MyFile.cs", "file:///c:/Proj/Demo/MyFile.cs")]
    public void ToFileUri_produces_the_shape_the_CLI_expects(string path, string expected)
        => Assert.Equal(expected, PathHelpers.ToFileUri(path));

    [Fact]
    public void ToFileUri_round_trips_through_FromFileUri()
    {
        const string path = @"C:\proj\demo\src\Program.cs";

        var uri = PathHelpers.ToFileUri(path);

        // Back as a Windows path, with the drive lowered on the way out and left that way.
        Assert.Equal(@"c:\proj\demo\src\Program.cs", PathHelpers.FromFileUri(uri));
    }

    [Theory]
    // The same file written the two ways the two sides of the wire write it.
    [InlineData(@"C:\proj\demo\File.cs", @"c:\proj\demo\File.cs", true)]
    [InlineData("file:///c:/proj/demo/File.cs", @"C:\proj\demo\File.cs", true)]
    [InlineData("file:///C:/proj/demo/File.cs", "file:///c:/proj/demo/File.cs", true)]
    // Genuinely different files.
    [InlineData(@"C:\proj\demo\File.cs", @"C:\proj\demo\Other.cs", false)]
    public void UrisEquivalent_ignores_the_spelling_not_the_file(string a, string b, bool expected)
        => Assert.Equal(expected, PathHelpers.UrisEquivalent(a, b));
}
