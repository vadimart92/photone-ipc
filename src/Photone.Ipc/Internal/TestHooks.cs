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
}
