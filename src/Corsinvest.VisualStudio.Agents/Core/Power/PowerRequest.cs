/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Corsinvest.VisualStudio.Agents.Core.Power;

/// <summary>A Windows power request, held until disposed.
/// <para>Not SetThreadExecutionState: that is per-thread state on the VS UI thread, which we share
/// with the shell and every other extension, and anything in the process can reset it under us. A
/// power request is a handle we own, the OS drops it when the process dies, and
/// <c>powercfg /requests</c> shows the reason string — so a user whose machine stops sleeping can
/// see who is holding it.</para></summary>
internal sealed class PowerRequest : IDisposable
{
    private IntPtr _handle;

    /// <summary>Asks Windows to keep the system running. The reason is user-visible.</summary>
    public static IDisposable Create(string reason) => new PowerRequest(reason);

    private PowerRequest(string reason)
    {
        var context = new ReasonContext
        {
            Version = ContextVersion,
            Flags = ContextSimpleString,
            SimpleReasonString = reason,
        };

        var handle = PowerCreateRequest(ref context);
        if (handle == IntPtr.Zero || handle == InvalidHandle)
        {
            throw new InvalidOperationException($"PowerCreateRequest failed with error {Marshal.GetLastWin32Error()}.");
        }

        if (!PowerSetRequest(handle, SystemRequired))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(handle);
            throw new InvalidOperationException($"PowerSetRequest failed with error {error}.");
        }

        _handle = handle;
    }

    public void Dispose()
    {
        // Exchange rather than a null check: disposing twice must not clear the request twice.
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle == IntPtr.Zero) { return; }

        PowerClearRequest(handle, SystemRequired);
        CloseHandle(handle);
    }

    private const uint ContextVersion = 0;
    private const uint ContextSimpleString = 1;

    // POWER_REQUEST_TYPE (winnt.h) orders the enum DisplayRequired = 0, SystemRequired = 1. Asking
    // for SystemRequired alone is what still lets the screen blank on its own idle timer.
    private const int SystemRequired = 1;

    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
