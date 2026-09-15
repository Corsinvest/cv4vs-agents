/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

// The outline of a file Roslyn does not have, through the project system instead: DTE's
// FileCodeModel, which is what draws the editor's own navigation dropdowns.
internal sealed partial class IdeNavigationService
{
    /// <summary><para>File outline from DTE, for a file the Roslyn workspace does not have. C++ is
    /// the case this exists for: a .vcxproj builds no Compilation, so it registers no Roslyn
    /// project at all, while DTE lists it like any other.</para>
    /// <para>Null (not empty) when the file has no code model, so the caller can tell "this
    /// language has no outline" from "this file has no symbols".</para>
    /// <para>A C++ member declared in a header reports the line of its definition in the .cpp, the
    /// way F12 navigates — so an outline of a header can hand back lines that belong to another
    /// file, and they are not wrong.</para></summary>
    private static async Task<DocSymbol[]> GetCodeModelSymbolsAsync(string filePath, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            var item = dte?.Solution?.FindProjectItem(filePath);

            // Reading this starts the language's parse, so besides returning null it also throws
            // while the parser is still coming up — C++ in particular.
            var codeModel = item?.FileCodeModel;
            if (codeModel?.CodeElements == null) { return null; }

            return MapCodeElements(codeModel.CodeElements, ct);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeNavigationService.GetCodeModelSymbolsAsync", ex);
            return null;
        }
    }

    /// <summary>Same ordering as the Roslyn path — line, then name — so an outline reads the same
    /// whichever side produced it.</summary>
    private static DocSymbol[] MapCodeElements(CodeElements elements, CancellationToken ct)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var symbols = new List<DocSymbol>();
        foreach (CodeElement element in elements)
        {
            ct.ThrowIfCancellationRequested();
            var symbol = MapCodeElement(element, ct);
            if (symbol != null) { symbols.Add(symbol); }
        }
        return [.. symbols.OrderBy(s => s.Line).ThenBy(s => s.Name, StringComparer.Ordinal)];
    }

    private static DocSymbol MapCodeElement(CodeElement element, CancellationToken ct)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var kind = MapElementKind(element.Kind);
            if (kind == null) { return null; }

            // Every crossing of StartPoint goes through COM, so read the line once.
            var line = element.StartPoint?.Line ?? 0;

            var children = GetMembers(element) is { } members ? MapCodeElements(members, ct) : [];

            return new DocSymbol { Name = element.Name ?? "", Kind = kind, Line = line, Children = children };
        }
        catch (Exception ex)
        {
            // A partially parsed file has elements whose members throw while the rest answer fine;
            // losing that one beats losing the outline.
            OutputWindowLogger.Global.Debug(() => $"[nav] code model element skipped: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>CodeElement itself has no Members: only CodeNamespace and CodeType declare one,
    /// and CodeType is the base of class, interface, struct, enum and delegate — so those two
    /// cases cover every container, including the C++ shapes with no C# equivalent to name.
    /// </summary>
    private static CodeElements GetMembers(CodeElement element)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return element switch
        {
            CodeNamespace ns => ns.Members,
            CodeType type => type.Members,
            _ => null,
        };
    }

    /// <summary>The same kind names the Roslyn path reports, so a caller cannot tell which side
    /// answered. Null for the enum's other 23 members, which are not declarations at all —
    /// statements inside a body, parameters, attributes, `#include` lines, the IDL family.
    /// </summary>
    private static string MapElementKind(vsCMElement kind)
        => kind switch
        {
            vsCMElement.vsCMElementNamespace => "Namespace",
            vsCMElement.vsCMElementClass => "Class",
            vsCMElement.vsCMElementInterface => "Interface",
            vsCMElement.vsCMElementStruct => "Struct",
            vsCMElement.vsCMElementEnum => "Enum",
            vsCMElement.vsCMElementDelegate => "Delegate",
            vsCMElement.vsCMElementFunction => "Method",
            vsCMElement.vsCMElementProperty => "Property",
            vsCMElement.vsCMElementVariable => "Field",
            vsCMElement.vsCMElementEvent => "Event",
            vsCMElement.vsCMElementModule => "Module",
            vsCMElement.vsCMElementUnion => "Struct",

            // Without these two a C++ header outlines as nearly empty, and C++ is what this path
            // is for.
            vsCMElement.vsCMElementTypeDef => "TypeDef",
            vsCMElement.vsCMElementMacro => "Macro",

            // VB's own declaration forms: a P/Invoke and a legacy Type…End Type.
            vsCMElement.vsCMElementDeclareDecl => "Method",
            vsCMElement.vsCMElementUDTDecl => "Struct",
            vsCMElement.vsCMElementEventsDeclaration => "Event",

            _ => null,
        };
}
