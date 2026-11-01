using System.Collections;
using System.Text;
using System.Text.Json;
using DeliveryForge.Evidence;
using DeliveryForge.Tool.Claude;
using DeliveryForge.Tool.Codex;

namespace DeliveryForge.Tool.Tests;

// Managed rejection-boundary DATA controls, not native ownership or review proof.
public sealed class ClaudeReviewerObservationFailureTests
{
    const string Prefix = "DFCLI_FIRST_CAUGHT|";
    const string Secret = "private-account-output-and-configuration";

    [Theory]
    [InlineData("exit")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("root")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    [InlineData("quota")]
    [InlineData("owner")]
    [InlineData("native")]
    public void Production_capture_boundary_reports_fixed_facts_without_private_output(string scenario)
    {
        var capture = Capture(scenario);
        var error = Assert.Throws<IOException>(() => ClaudeIndependentReviewer.RequireSuccessfulObservation(ClaudeReviewerObservationOperation.AuthStatus, capture));
        Assert.IsType<IOException>(error); Assert.Equal("Actual reviewer observation is incomplete.", error.Message);
        Assert.Empty(error.Data);
        var facts = Assert.IsType<ClaudeReviewerObservationFacts>(ClaudeReviewerObservationFailure.FindSingle(error));
        Assert.Equal("CaptureIncomplete", facts.Code); Assert.Equal("AuthStatus", facts.Operation);
        Assert.Equal(capture.Command.ExitCode, facts.ExitCode);
        Assert.Equal(capture.Command.TimedOut, facts.TimedOut); Assert.Equal(capture.Command.Cancelled, facts.Cancelled);
        Assert.Equal(capture.RootExitObserved, facts.RootExitObserved);
        Assert.Equal(capture.Command.OwnedProcessQuiescent, facts.OwnedProcessQuiescent);
        Assert.Equal(capture.StandardOutputDrainCompleted, facts.StandardOutputDrainCompleted);
        Assert.Equal(capture.StandardErrorDrainCompleted, facts.StandardErrorDrainCompleted);
        Assert.Equal(capture.CaptureBoundExceeded, facts.CaptureBoundExceeded);
        Assert.Equal(Encoding.UTF8.GetByteCount(Secret), facts.StandardOutputBytes);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var line = Emit(life, new ThrowingTextException(error));
        using var json = JsonDocument.Parse(line[Prefix.Length..]);
        var diagnostic = json.RootElement.GetProperty("ReviewerObservation");
        Assert.Equal("CaptureIncomplete", diagnostic.GetProperty("Code").GetString());
        Assert.Equal("AuthStatus", diagnostic.GetProperty("Operation").GetString());
        Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
        Assert.DoesNotContain("Hash", line, StringComparison.Ordinal);
        Assert.False(diagnostic.TryGetProperty("Arguments", out _));
        Assert.False(diagnostic.TryGetProperty("Data", out _));
    }

    [Fact]
    public void Production_version_comparison_distinguishes_mismatch_from_capture_failure()
    {
        var capture = Capture("success", "selected-version\n");
        ClaudeIndependentReviewer.RequireSuccessfulObservation(ClaudeReviewerObservationOperation.Version, capture);
        ClaudeIndependentReviewer.RequireObservedVersion(capture, "selected-version");
        var error = Assert.Throws<IOException>(() => ClaudeIndependentReviewer.RequireObservedVersion(capture, Secret));
        Assert.IsType<IOException>(error); Assert.Equal("Actual reviewer version differs from its selected image profile.", error.Message);
        var facts = Assert.IsType<ClaudeReviewerObservationFacts>(ClaudeReviewerObservationFailure.FindSingle(error));
        Assert.Equal("VersionMismatch", facts.Code); Assert.Equal("Version", facts.Operation); Assert.Equal(0, facts.ExitCode);
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var line = Emit(life, error);
        Assert.Contains("\"Code\":\"VersionMismatch\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-version", line, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_one_exact_tagged_exception_in_the_bounded_caught_graph_supplies_facts()
    {
        var tagged = ClaudeReviewerObservationFailure.CaptureIncomplete(ClaudeReviewerObservationOperation.Version, Capture("exit"));
        var facts = Assert.IsType<ClaudeReviewerObservationFacts>(ClaudeReviewerObservationFailure.FindSingle(tagged));
        Assert.Null(ClaudeReviewerObservationFailure.FindSingle(new IOException(tagged.Message)));
        Assert.Same(facts, ClaudeReviewerObservationFailure.FindSingle(new AggregateException(tagged, new IOException(Secret, tagged))));
        var second = ClaudeReviewerObservationFailure.CaptureIncomplete(ClaudeReviewerObservationOperation.Version, Capture("exit"));
        Assert.Null(ClaudeReviewerObservationFailure.FindSingle(new AggregateException(tagged, second)));
        Exception chain = tagged;
        for (var i = 0; i < 15; i++) chain = new IOException(Secret, chain);
        Assert.Same(facts, ClaudeReviewerObservationFailure.FindSingle(chain));
        Assert.Null(ClaudeReviewerObservationFailure.FindSingle(new IOException(Secret, chain)));
        Assert.Null(ClaudeReviewerObservationFailure.FindSingle(new AggregateException(Enumerable.Repeat<Exception>(tagged, 17))));
        foreach (var error in new Exception[] { new IOException(Secret), new AggregateException(tagged, second) })
        {
            using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(DefaultLine(error, "independent-review-VersionObservation"), Emit(life, error));
        }
    }

    [Fact]
    public void Oversized_optional_reviewer_facts_keep_exact_default_bytes_and_original_limits()
    {
        var error = ClaudeReviewerObservationFailure.CaptureIncomplete(ClaudeReviewerObservationOperation.Version, Capture("exit"));
        var stage = new string('s', 1024 - Encoding.UTF8.GetByteCount(DefaultLine(error, "")));
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var started = life.OriginalStarted; var whole = life.WholeLimit; var work = life.WorkLimit;
        var expected = DefaultLine(error, stage); Assert.Equal(1024, Encoding.UTF8.GetByteCount(expected));
        var line = Emit(life, error, stage);
        Assert.Equal(expected, line); Assert.DoesNotContain("ReviewerObservation", line, StringComparison.Ordinal);
        Assert.Equal(started, life.OriginalStarted); Assert.Equal(whole, life.WholeLimit); Assert.Equal(work, life.WorkLimit);
    }

    [Fact]
    public void Existing_protocol_diagnostic_keeps_precedence_over_optional_reviewer_facts()
    {
        var protocol = CodexProtocolFailure.Create(CodexProtocolFailureKind.UnexpectedEof, Secret);
        var reviewer = ClaudeReviewerObservationFailure.CaptureIncomplete(ClaudeReviewerObservationOperation.Version, Capture("exit"));
        using var life = new ToolLifetime(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var line = Emit(life, new AggregateException(protocol, reviewer));
        Assert.Contains("\"Code\":\"UnexpectedEof\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ReviewerObservation", line, StringComparison.Ordinal);
    }

    static BoundedCommandCaptureResult Capture(string scenario, string output = Secret)
    {
        var command = new CommandResult(scenario == "exit" ? 17 : 0, output, Secret,
            scenario == "timeout", scenario == "cancel", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            scenario != "owner")
        {
            LifecycleDiagnostics = new(false, false, null, null, false, true, true, true, 0,
                new(true, false, false, output), new(true, false, false, Secret))
            { OwnedTree = new(true, true, 0), NativeFailure = scenario == "native" ? Secret : null }
        };
        return new(command, Encoding.UTF8.GetBytes(output), Encoding.UTF8.GetBytes(Secret), scenario == "quota",
            scenario != "root", scenario != "stdout", scenario != "stderr", ownershipKind: BoundedCaptureOwnershipKind.LinuxCreationOwnedScope);
    }

    static string Emit(ToolLifetime life, Exception error, string stage = "independent-review-VersionObservation")
    {
        using var errors = new StringWriter(); var attention = new ToolFailureAttention(life, errors); life.BindAttention(attention);
        life.RecordFirstCaught(stage, error); life.BeginFailureCleanup(); attention.EmitFirstCaughtOnce();
        var line = Assert.Single(errors.ToString().Split('\n'), value => value.StartsWith(Prefix, StringComparison.Ordinal)).TrimEnd('\r');
        Assert.True(Encoding.UTF8.GetByteCount(line) <= 1024);
        Assert.False(attention.Unknown); Assert.False(life.HasExplicitUnknown);
        return line;
    }

    static string DefaultLine(Exception error, string stage) => Prefix + JsonSerializer.Serialize(new
    {
        Stage = stage,
        Type = error.GetType().FullName,
        error.HResult,
        InnerType = error.InnerException?.GetType().FullName,
        CleanupAlreadyBegun = false,
        MarkerAlreadyEntered = false
    });

    sealed class ThrowingTextException(Exception inner) : IOException(Secret, inner)
    {
        public override string Message => throw new InvalidOperationException("Message was read");
        public override string? StackTrace => throw new InvalidOperationException("StackTrace was read");
        public override IDictionary Data => throw new InvalidOperationException("Data was read");
    }
}
