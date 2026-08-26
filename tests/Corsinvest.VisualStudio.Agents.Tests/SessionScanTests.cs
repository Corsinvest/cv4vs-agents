/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Sessions;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The .jsonl scan matches on strings for speed, which makes it whitespace-sensitive
/// unless it is written not to be. That rule is stated in CLAUDE.md and enforced by nothing:
/// a literal '"type":"x"' compiles, passes a manual check against the CLI's compact output, and
/// silently skips lines the day a pretty-printing writer produces the same JSON. These tests are
/// the enforcement.</summary>
public class SessionScanTests
{
    [Theory]
    // The CLI's own compact output.
    [InlineData("{\"isSidechain\":true}", "isSidechain", true)]
    // A pretty-printer's space after the colon — same JSON, and it must read the same.
    [InlineData("{\"isSidechain\": true}", "isSidechain", true)]
    // Present but false, and absent: both are "not flagged".
    [InlineData("{\"isSidechain\":false}", "isSidechain", false)]
    [InlineData("{\"isSidechain\": false}", "isSidechain", false)]
    [InlineData("{\"other\":true}", "isSidechain", false)]
    [InlineData("{}", "isSidechain", false)]
    public void IsFlagTrue_reads_the_flag_whatever_the_writer_did_with_spaces(
        string line, string key, bool expected)
        => Assert.Equal(expected, SessionManager.IsFlagTrue(line, key));

    [Theory]
    // The CLI writes compact; a pretty-printer writes the same JSON with a space after the colon.
    // IsType decides whether a line is read at all, so getting this wrong SKIPS records rather
    // than failing loudly - the exact bug the rule in CLAUDE.md exists to prevent.
    [InlineData(@"{""type"":""user""}", "user", true)]
    [InlineData(@"{""type"": ""user""}", "user", true)]
    // NOT covered, deliberately: a space BEFORE the colon ({"type" : "user"}). FindJsonStringValue
    // matches the two forms a writer actually emits, and no mainstream serializer produces that
    // third one - Newtonsoft's Indented, JSON.stringify and json.dumps all put the space after.
    // Widening the scan for a writer nobody uses costs a third IndexOf on every line of every read.
    // The type is not the first key: the scan must find it wherever the writer put it.
    [InlineData(@"{""uuid"":""u1"",""type"":""assistant""}", "assistant", true)]
    [InlineData(@"{""uuid"":""u1"",""type"": ""assistant""}", "assistant", true)]
    // Wrong type, and a line with no type at all.
    [InlineData(@"{""type"":""assistant""}", "user", false)]
    [InlineData(@"{""uuid"":""u1""}", "user", false)]
    // A value that merely CONTAINS the wanted one is a different type.
    [InlineData(@"{""type"":""user-something""}", "user", false)]
    public void IsType_reads_the_type_whatever_the_writer_did_with_spaces(
        string line, string type, bool expected)
        => Assert.Equal(expected, SessionManager.IsType(line, type));

    [Theory]
    // Plain ids: what a session or agent id actually looks like.
    [InlineData("e98457c0-1234-4abc-9def-000000000000", true)]
    [InlineData("asidequestion-a1b2c3", true)]
    [InlineData("abc_DEF-123", true)]
    // Traversal and separators. These reach the reader straight off a WebView DTO, so a false
    // here is what keeps Path.Combine from resolving out of the session folder.
    [InlineData("..", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData(@"..\..\windows\system32", false)]
    [InlineData("sub/dir", false)]
    [InlineData(@"sub\dir", false)]
    [InlineData("C:", false)]
    // Nothing to build a path from.
    [InlineData("", false)]
    [InlineData(null, false)]
    // A dot is not in the allowed set: ".jsonl" is appended by the caller, never carried in the id.
    [InlineData("session.jsonl", false)]
    public void IsSafePathToken_accepts_plain_ids_and_rejects_everything_that_walks(
        string token, bool expected)
        => Assert.Equal(expected, SessionManager.IsSafePathToken(token));
}
