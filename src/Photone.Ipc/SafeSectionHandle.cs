using Microsoft.Win32.SafeHandles;
using Photone.Ipc.Internal;

namespace Photone.Ipc;

/// <summary>
/// Owns a Win32 section (file-mapping) handle. The section name stays alive as long as at least one handle to it
/// is open in any process; the memory stays alive as long as any handle or view exists anywhere.
/// </summary>
public sealed class SafeSectionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Creates an invalid, owning handle (required by the P/Invoke return marshaller).</summary>
    public SafeSectionHandle() : base(ownsHandle: true)
    {
    }

    /// <summary>Wraps a raw section handle obtained via <c>DuplicateHandle</c> or inheritance.</summary>
    /// <param name="handle">The raw handle value.</param>
    /// <param name="ownsHandle"><see langword="true"/> to close the handle when this object is disposed or finalized.</param>
    public SafeSectionHandle(nint handle, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(handle);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle() => Kernel.CloseHandle(handle);
}
