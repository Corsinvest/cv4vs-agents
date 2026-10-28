/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Microsoft.VisualStudio.Copilot;
using Microsoft.VisualStudio.Copilot.EditingStateTracking;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

// Stand-ins with the names and members of the parts of GitHub Copilot in Visual Studio 18 that the review is reached through.
// Copilot's assemblies cannot be referenced (they are not on every Visual Studio, and what they expose is not a documented API),
// which is why the review reaches them by reflection, and why the reflection can be tried here against types of the same names.
namespace Microsoft.VisualStudio.Copilot
{
    public readonly struct CopilotClientId
    {
        public CopilotClientId(string value) => Value = value;
        public string Value { get; }
    }

    public readonly struct CopilotSessionId
    {
        public CopilotSessionId(CopilotClientId clientId) => ClientId = clientId;
        public CopilotClientId ClientId { get; }
    }

    public readonly struct CopilotInteractionId
    {
        public CopilotInteractionId(CopilotSessionId sessionId) => SessionId = sessionId;
        public CopilotSessionId SessionId { get; }
    }
}

namespace Microsoft.VisualStudio.Copilot.EditingStateTracking
{
    public class BaselineDiffState
    {
        public BaselineDiffState(int additions = 0, int deletions = 0, int changes = 0)
        {
            Additions = additions;
            Deletions = deletions;
            Changes = changes;
        }

        public int Additions { get; }
        public int Deletions { get; }
        public int Changes { get; }
    }

    public interface IFileBaselineState
    {
        string FilePath { get; }
        string BaselineFilePath { get; }
        BaselineDiffState DiffState { get; }
        event EventHandler<BaselineDiffState> BaselineDiffChanged;
        event EventHandler<EventArgs> BaselineChanged;
        event EventHandler DismissRequested;
        object GetBaselineSnapshot();
        void RequestDismissal();
        void UpdateDiffState(BaselineDiffState diffState);
    }

    public interface IBaselineStateRegistry
    {
        bool TryGetActiveBaselineState(string filePath, out IFileBaselineState state);
        Task<IFileBaselineState> GetOrCreateBaselineStateAsync(string filePath, CopilotInteractionId originatingInteractionId, CancellationToken cancellationToken);
        Task ReleaseBaselineStateAsync(string filePath, CancellationToken cancellation);
    }

    public interface IAiChangesMarker
    {
        Task MarkForAiChangesAsync(object textView, CopilotInteractionId originatingInteractionId, CancellationToken cancellationToken);
        void MarkAiChangesCompleted(object textView, CancellationToken cancellationToken);
    }
}

namespace Corsinvest.VisualStudio.Agents.Tests
{
    public class CopilotReviewShapeTests
    {
        private sealed class FakeState : IFileBaselineState
        {
            public string FilePath { get; set; }
            public string BaselineFilePath { get; set; } = @"C:\temp\CopilotBaseline\~a.txt";
            public BaselineDiffState DiffState { get; private set; } = new();
            public object Snapshot = "the baseline";
            public int Dismissals;
            public event EventHandler<BaselineDiffState> BaselineDiffChanged;
            public event EventHandler<EventArgs> BaselineChanged;
            public event EventHandler DismissRequested;

            public object GetBaselineSnapshot() => Snapshot;
            public void RequestDismissal() => Dismissals++;
            public void UpdateDiffState(BaselineDiffState diffState) => DiffState = diffState;

            public void RaiseDiff(int changes) => BaselineDiffChanged?.Invoke(this, new BaselineDiffState(1, 0, changes));
            public void RaiseBaselineChanged() => BaselineChanged?.Invoke(this, EventArgs.Empty);
            public void RaiseDismiss() => DismissRequested?.Invoke(this, EventArgs.Empty);
            public int Listeners => (BaselineDiffChanged?.GetInvocationList().Length ?? 0)
                                    + (BaselineChanged?.GetInvocationList().Length ?? 0)
                                    + (DismissRequested?.GetInvocationList().Length ?? 0);
        }

        private sealed class FakeRegistry : IBaselineStateRegistry
        {
            public readonly Dictionary<string, FakeState> States = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<CopilotInteractionId> Ids = new();
            public readonly List<string> Released = new();
            public Exception Fails;

            public bool TryGetActiveBaselineState(string filePath, out IFileBaselineState state)
            {
                var found = States.TryGetValue(filePath, out var held);
                state = held;
                return found;
            }

            public Task<IFileBaselineState> GetOrCreateBaselineStateAsync(string filePath, CopilotInteractionId id, CancellationToken cancellationToken)
            {
                if (Fails is not null) { throw Fails; }
                Ids.Add(id);
                if (!States.TryGetValue(filePath, out var state)) { States[filePath] = state = new FakeState { FilePath = filePath }; }
                return Task.FromResult<IFileBaselineState>(state);
            }

            public Task ReleaseBaselineStateAsync(string filePath, CancellationToken cancellation)
            {
                Released.Add(filePath);
                States.Remove(filePath);
                return Task.CompletedTask;
            }
        }

        private sealed class FakeMarker : IAiChangesMarker
        {
            public readonly List<(object View, CopilotInteractionId Id)> Marked = new();
            public readonly List<object> Completed = new();

            public Task MarkForAiChangesAsync(object textView, CopilotInteractionId id, CancellationToken cancellationToken)
            {
                Marked.Add((textView, id));
                return Task.CompletedTask;
            }

            public void MarkAiChangesCompleted(object textView, CancellationToken cancellationToken) => Completed.Add(textView);
        }

        private readonly FakeMarker _marker = new();
        private readonly FakeRegistry _registry = new();

        private CopilotReviewShape Resolve()
        {
            var shape = CopilotReviewShape.TryResolve(_marker, _registry, out var why);
            Assert.NotNull(shape);
            Assert.Null(why);
            return shape;
        }

        [Fact]
        public void Parts_of_the_expected_shape_are_resolved_and_described()
        {
            var shape = Resolve();

            Assert.Contains(nameof(FakeMarker), shape.Description);
            Assert.Contains(nameof(FakeRegistry), shape.Description);
        }

        [Fact]
        public void Something_that_is_not_a_part_of_Copilot_is_not_resolved_and_says_which()
        {
            Assert.Null(CopilotReviewShape.TryResolve(new object(), _registry, out var why));
            Assert.Contains("does not implement " + CopilotReviewShape.MarkerContract, why);

            Assert.Null(CopilotReviewShape.TryResolve(_marker, new object(), out why));
            Assert.Contains("does not implement " + CopilotReviewShape.RegistryContract, why);

            Assert.Null(CopilotReviewShape.TryResolve(null, _registry, out why));
            Assert.Contains("no marker", why);

            Assert.Null(CopilotReviewShape.TryResolve(_marker, null, out why));
            Assert.Contains("no registry", why);
        }

        [Fact]
        public void The_interaction_id_is_built_from_a_client_name_through_the_types_of_the_marker()
        {
            var shape = Resolve();

            var id = shape.NewInteractionId("cv4vs.claude");

            var typed = Assert.IsType<CopilotInteractionId>(id);
            Assert.Equal("cv4vs.claude", typed.SessionId.ClientId.Value);
        }

        [Fact]
        public void A_diff_state_is_built_from_three_counts()
        {
            var shape = Resolve();

            var state = Assert.IsType<BaselineDiffState>(shape.NewDiffState(2, 3, 4));

            Assert.Equal((2, 3, 4), (state.Additions, state.Deletions, state.Changes));
        }

        [Fact]
        public async Task A_state_is_found_made_and_released_through_the_registry()
        {
            var shape = Resolve();
            var id = shape.NewInteractionId("cv4vs.claude");

            Assert.Null(shape.TryGetState(@"C:\work\a.txt"));

            var made = await shape.GetOrCreateAsync(@"C:\work\a.txt", id);

            Assert.Equal(@"C:\work\a.txt", Assert.IsType<FakeState>(made).FilePath);
            Assert.Same(made, shape.TryGetState(@"C:\work\A.TXT"));
            Assert.Equal(id, Assert.Single(_registry.Ids));

            await shape.ReleaseAsync(@"C:\work\a.txt");

            Assert.Null(shape.TryGetState(@"C:\work\a.txt"));
            Assert.Equal(@"C:\work\a.txt", Assert.Single(_registry.Released));
        }

        [Fact]
        public async Task A_view_is_marked_and_the_marking_is_completed_with_the_same_object()
        {
            var shape = Resolve();
            var id = shape.NewInteractionId("cv4vs.claude");
            var view = new object();

            await shape.MarkAsync(view, id);
            shape.MarkCompleted(view);

            var marked = Assert.Single(_marker.Marked);
            Assert.Same(view, marked.View);
            Assert.Equal(id, marked.Id);
            Assert.Same(view, Assert.Single(_marker.Completed));
        }

        [Fact]
        public void A_state_is_read_and_driven_through_its_interface()
        {
            var shape = Resolve();
            var state = new FakeState { Snapshot = "text of the baseline" };

            Assert.Equal(@"C:\temp\CopilotBaseline\~a.txt", shape.BaselineFilePath(state));
            Assert.Equal("text of the baseline", shape.BaselineSnapshot(state));

            shape.UpdateDiffState(state, shape.NewDiffState(0, 0, 1));
            shape.RequestDismissal(state);

            Assert.Equal(1, state.DiffState.Changes);
            Assert.Equal(1, state.Dismissals);
        }

        [Fact]
        public void The_three_events_of_a_state_are_relayed_with_what_they_carry_and_stop_when_disposed()
        {
            var shape = Resolve();
            var state = new FakeState();
            var counts = new List<int>();
            var changed = 0;
            var dismissed = 0;

            var subscriptions = new[]
            {
                shape.OnDiffChanged(state, counts.Add),
                shape.OnBaselineChanged(state, () => changed++),
                shape.OnDismissRequested(state, () => dismissed++),
            };
            state.RaiseDiff(3);
            state.RaiseDiff(0);
            state.RaiseBaselineChanged();
            state.RaiseDismiss();

            Assert.Equal(new[] { 3, 0 }, counts);
            Assert.Equal(1, changed);
            Assert.Equal(1, dismissed);
            Assert.Equal(3, state.Listeners);

            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
                // Twice is as good as once: a review is ended from more than one place.
                subscription.Dispose();
            }
            state.RaiseDiff(5);
            state.RaiseBaselineChanged();
            state.RaiseDismiss();

            Assert.Equal(new[] { 3, 0 }, counts);
            Assert.Equal(1, changed);
            Assert.Equal(1, dismissed);
            Assert.Equal(0, state.Listeners);
        }

        [Fact]
        public async Task A_failure_in_a_part_of_Copilot_comes_back_as_itself_not_as_a_reflection_wrapper()
        {
            var shape = Resolve();
            var failure = new InvalidOperationException("no baseline today");
            _registry.Fails = failure;

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => shape.GetOrCreateAsync(@"C:\work\a.txt", shape.NewInteractionId("cv4vs.claude")));

            Assert.Same(failure, thrown);
            // The stack still names the part that threw, not only the helper that called it.
            Assert.Contains("GetOrCreateBaselineStateAsync", thrown.StackTrace);
        }
    }
}
