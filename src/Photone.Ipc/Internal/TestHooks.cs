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

    /// <summary>
    /// Runs in the writer when a <c>GetBucket</c> that took the slow path has found space and dropped its local reference, just before it publishes its
    /// reservation; the argument is the buffer.
    /// </summary>
    public static Action<object>? AfterSpaceWait { get; set; }

    /// <summary>
    /// Runs in the writer when a <c>GetBucket</c> that took the slow path has published its reservation behind a full fence, just before it checks
    /// whether the writer was closed meanwhile; the argument is the buffer.
    /// </summary>
    public static Action<object>? AfterReservationPublished { get; set; }
}
