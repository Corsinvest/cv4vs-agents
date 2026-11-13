/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Text.Differencing;
using Xunit;

// Stand-ins with the names and members of the Visual Studio 18 types that the window is made of. The SDK this builds
// against has none of them, which is why they are reached by reflection, and why the reflection can be tried here against
// types of the same names.
namespace Microsoft.VisualStudio.Text.Differencing
{
    public interface IMultiDiffService
    {
        IMultiDiffSession CreateToolWindowSession(MultiDiffInput input);
    }

    public interface IMultiDiffSession
    {
        void AddDiff(DiffEntry diff);
    }

    public class MultiDiffInput
    {
        public string DisplayTitle { get; set; }
    }

    public class DiffEntry
    {
        public DiffableTextInfo Left { get; set; }
        public DiffableTextInfo Right { get; set; }
        public string DisplayTitle { get; set; }
    }

    public class DiffableTextInfo
    {
        public Uri TextUri { get; set; }
        public object TextBuffer { get; set; }
        private bool _readOnly = true;

        // True until it is set, as the real one reads; and whether the code set it, which a default cannot show.
        public bool ReadOnly { get => _readOnly; set { _readOnly = value; ReadOnlyWasSet = true; } }
        public bool ReadOnlyWasSet { get; private set; }
    }
}

namespace Corsinvest.VisualStudio.Agents.Tests
{
    public class MultiDiffShapeTests
    {
        private sealed class FakeSession : IMultiDiffSession
        {
            public readonly List<DiffEntry> Entries = new();
            public void AddDiff(DiffEntry diff) => Entries.Add(diff);
        }

        private sealed class FakeService : IMultiDiffService
        {
            public readonly List<MultiDiffInput> Inputs = new();
            public readonly FakeSession Session = new();
            public Exception Fails;

            public IMultiDiffSession CreateToolWindowSession(MultiDiffInput input)
            {
                if (Fails is not null) { throw Fails; }
                Inputs.Add(input);
                return Session;
            }
        }

        [Fact]
        public void A_service_of_the_expected_shape_is_resolved()
        {
            var shape = MultiDiffShape.TryResolve(new FakeService(), out var why);

            Assert.NotNull(shape);
            Assert.Null(why);
            Assert.Equal(typeof(object), shape.BufferType);
        }

        [Fact]
        public void Something_that_is_not_the_window_s_service_is_not_resolved()
        {
            Assert.Null(MultiDiffShape.TryResolve(new object(), out var why));
            Assert.Contains("does not implement", why);

            Assert.Null(MultiDiffShape.TryResolve(null, out why));
            Assert.Contains("no service", why);
        }

        [Fact]
        public void A_session_and_its_entries_are_built_through_the_service()
        {
            var service = new FakeService();
            var shape = MultiDiffShape.TryResolve(service, out _);

            var session = shape.CreateSession("Changes by Claude (1)");
            var entry = shape.NewEntry(@"C:\work\a.cs", "the baseline", @"C:\work\a.cs");
            shape.AddDiff(session, entry);

            Assert.Equal("Changes by Claude (1)", Assert.Single(service.Inputs).DisplayTitle);
            Assert.Same(service.Session, session);
            var added = Assert.Single(service.Session.Entries);
            Assert.Equal(@"C:\work\a.cs", added.DisplayTitle);
        }

        [Fact]
        public void The_baseline_side_is_a_read_only_buffer_and_the_file_side_is_the_file_and_can_be_edited()
        {
            var service = new FakeService();
            var shape = MultiDiffShape.TryResolve(service, out _);

            var entry = (DiffEntry)shape.NewEntry("a.cs", "the baseline", @"C:\work\a.cs");

            Assert.Equal("the baseline", entry.Left.TextBuffer);
            Assert.True(entry.Left.ReadOnly);
            Assert.True(entry.Left.ReadOnlyWasSet);
            Assert.Null(entry.Left.TextUri);
            Assert.Equal(new Uri(@"C:\work\a.cs"), entry.Right.TextUri);
            Assert.False(entry.Right.ReadOnly);
            Assert.True(entry.Right.ReadOnlyWasSet);
            Assert.Null(entry.Right.TextBuffer);
        }

        [Fact]
        public void A_failure_in_the_service_comes_back_as_itself_not_as_a_reflection_wrapper()
        {
            var failure = new ArgumentException("no tool window today");
            var service = new FakeService { Fails = failure };
            var shape = MultiDiffShape.TryResolve(service, out _);

            var thrown = Assert.Throws<ArgumentException>(() => shape.CreateSession("x"));

            Assert.Same(failure, thrown);
            // The stack still names the service that threw, not only the helper that called it.
            Assert.Contains("CreateToolWindowSession", thrown.StackTrace);
        }
    }
}
