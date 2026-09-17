namespace Photone.Ipc.Internal;

/// <summary>
/// Process-wide hooks used only by the test child to inject faults at precise protocol points (DESIGN §1). All default to <see langword="null"/>;
/// the library never sets them. Never touched on a hot path.
/// </summary>
internal static class TestHooks
{
    /// <summary>Runs right after a reader slot has been claimed (state <c>Claimed</c>) and before it becomes <c>Active</c>.</summary>
    public static Action? AfterClaim { get; set; }

    /// <summary>Runs in the creator immediately before <c>InitState</c> is set to 1.</summary>
    public static Action? BeforeInitState { get; set; }

    /// <summary>Runs in an opener after it has read a pooled buffer's alias record and before it opens the section the record names.</summary>
    public static Action? AfterAliasResolved { get; set; }

    /// <summary>Runs in the writer when the slow path of <c>GetBucket</c> leaves (also by an exception), just before it drops its local reference.</summary>
    public static Action? SlowGetBucketLeaving { get; set; }

    /// <summary>Runs in a commit with tags once its tags fit, just before they are copied into the log; the argument is the committing buffer.</summary>
    public static Action<object>? BeforeTagAppend { get; set; }
}
