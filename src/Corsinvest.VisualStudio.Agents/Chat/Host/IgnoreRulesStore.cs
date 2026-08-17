/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using System;
using System.IO;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>
/// The `@` picker's own ignore rules, kept as a `.gitignore` file in the app data folder
/// (<see cref="AppPaths.IgnoreRulesFile"/>) rather than in the VS settings store: the content is
/// a rule list with comments and sections, which is a thing to edit in a real editor and copy
/// between machines, not a preference to toggle.
/// <para>These apply only where the workspace's own ignore rules say nothing —
/// <c>FileSuggestions.IsIgnored</c> asks the .gitignore stack first. They exist for the project
/// that ships no rules at all: without them a repository with an unignored node_modules fills the
/// menu with 2,000 rows of dependencies.</para>
/// </summary>
internal static class IgnoreRulesStore
{
    /// <summary>The rules as written on disk, or <see cref="Defaults"/> when there is no file yet.
    /// Never throws: an unreadable file falls back to the defaults, because a picker that lists
    /// everything is worse than one using rules the user did not choose.</summary>
    public static string Read()
    {
        try
        {
            return File.Exists(AppPaths.IgnoreRulesFile)
                ? File.ReadAllText(AppPaths.IgnoreRulesFile)
                : Defaults;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IgnoreRulesStore.Read", ex);
            return Defaults;
        }
    }

    /// <summary>Write the defaults out if the file does not exist yet, and return its path. Called
    /// before opening the file for editing, so the user always lands on a commented starting point
    /// rather than an empty buffer. Returns null when the file cannot be created.</summary>
    public static string EnsureFile()
    {
        try
        {
            if (!File.Exists(AppPaths.IgnoreRulesFile))
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                File.WriteAllText(AppPaths.IgnoreRulesFile, Defaults);
            }
            return AppPaths.IgnoreRulesFile;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IgnoreRulesStore.EnsureFile", ex);
            return null;
        }
    }

    /// <summary>Timestamp used to tell a re-read from a cache hit. <see cref="DateTime.MinValue"/>
    /// when there is no file, which is a stable key for "the defaults".</summary>
    public static DateTime LastWriteUtc
    {
        get
        {
            try
            {
                return File.Exists(AppPaths.IgnoreRulesFile)
                    ? File.GetLastWriteTimeUtc(AppPaths.IgnoreRulesFile)
                    : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }
    }

    /// <summary>Shipped starting point, also the fallback when the file is missing or unreadable.
    /// Read by the .gitignore parser, so these ARE gitignore rules: a trailing `/` marks a
    /// directory, a leading `/` anchors to the workspace root, and `*.ext` is a glob rather than
    /// the bare extension an earlier shorthand accepted.</summary>
    public const string Defaults = """
        # Hidden from the @ file picker, on top of the workspace's own .gitignore.
        # Same syntax as a .gitignore: one rule per line, # for a comment, a trailing /
        # for a directory, a leading / to anchor at the workspace root, and * ? [Dd] !
        # as git defines them.
        #
        # These only apply where the workspace's rules say nothing, so a project that
        # ignores its own build output is already covered without touching this file.

        # Version control and IDE state
        .git/
        .vs/
        .vscode/
        .idea/
        .hg/
        .svn/

        # Dependencies and build output. Directory-only, so a source file named bin.cs stays.
        node_modules/
        bin/
        obj/
        dist/
        .next/
        .nuxt/
        .svelte-kit/
        __pycache__/
        .venv/
        venv/
        .pytest_cache/
        .gradle/
        .terraform/
        .cache/

        # Anchored to the workspace root: these are output there, but ordinary source folder
        # names deeper in a tree — this extension's own Mcp/Tools/Build/ holds four of them.
        /build/
        /out/
        /target/

        # Noise and binaries
        .DS_Store
        Thumbs.db
        .env
        *.exe
        *.dll
        *.pdb
        *.ilk
        *.suo
        *.user
        *.log
        *.tmp
        *.bak
        """;
}
