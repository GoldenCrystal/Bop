using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static Bop.Interop;
using static Bop.Interop.Kernel32;

namespace Bop;

internal static class ITunesProcessHelper
{
	/// <summary>Locates the iTunes executable according to Windows registry.</summary>
	/// <remarks>This method hasn't been tested on non-store iTunes.</remarks>
	/// <param name="path">The path to iTunes, if found.</param>
	/// <returns><see langword="true"/> if the path to iTunes.exe was found; otherwise <see langword="false"/>.</returns>
	public static bool TryLocateITunesBinary(out string? path)
	{
		using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\iTunes.exe", false))
		{
			if (key is not null)
			{
				path = key.GetValue(null) as string;
				if (path is not null)
				{
					return true;
				}
			}
		}

		path = null;
		return false;
	}

	// Refresh the ITunes path after at least 10 minutes have ellapsed. (This is mainly useful for when the path is not found)
	private const long PathRefreshDelayInMinutes = 10;
	private static Tuple<string?, DateTime> _lastKnownITunesPath = Tuple.Create(null as string, DateTime.UnixEpoch);

	/// <summary>Wait for an active iTunes.exe process.</summary>
	/// <remarks>
	/// This method uses polling to find an iTunes process, with the caveats mentioned for <see cref="FindITunesProcess"/>.
	/// WMI Win32_ProcessStartTrace would be the best way to detect process creation, but sadly, this API is not accessible without admin rights.
	/// Even if requiring admin rights would be somewhat acceptable, Windows Store iTunes is *NOT* accessible with admin rights because it is wrapped with Packaged COM.
	/// </remarks>
	/// <param name="cancellationToken"></param>
	/// <returns></returns>
	public static ValueTask<SafeProcessHandle> WaitForITunesProcessAsync(CancellationToken cancellationToken)
		=> WaitForITunesProcessAsync(1024, cancellationToken);

	private static async ValueTask<SafeProcessHandle> WaitForITunesProcessAsync(int expectedProcessCountUpperBound, CancellationToken cancellationToken)
	{
		var pool = ArrayPool<int>.Shared;
		var array = pool.Rent(expectedProcessCountUpperBound); // This could be not enough, but there doesn't seem to be a very good way to detect this.

		const int MaxDelay = 60_000;
		int delay = 1_000;

		try
		{
			while (true)
			{
				if (FindITunesProcess(array) is not null and var handle) return handle;

				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

				if (delay < MaxDelay)
				{
					delay = Math.Min(delay * 2, MaxDelay);
				}
			}
		}
		finally
		{
			pool.Return(array);
		}
	}

	/// <summary>Find an active iTunes.exe process.</summary>
	/// <remarks>
	/// We detect an active iTunes process by scanning the list of active processes and matching an "iTunes.exe" process.
	/// This has a few caveats:
	/// <list type="bullet">
	/// <description>Executable names can easily be spoofed. You should not run into this issue when running the tool in normal conditions.</description>
	/// <description>
	/// The number of processes that can run on the system is not limited, and this method uses a limited-length buffer.
	/// If too many processes are running on the host, this method could miss the iTunes process.
	/// </description>
	/// </list>
	/// </remarks>
	/// <returns>A process handle if an iTunes.exe process was found; otherwise <see langword="null"/>.</returns>
	public static SafeProcessHandle? FindITunesProcess()
		=> FindITunesProcess(1024);

	private static SafeProcessHandle? FindITunesProcess(int expectedProcessCountUpperBound)
	{
		var pool = ArrayPool<int>.Shared;
		var array = pool.Rent(expectedProcessCountUpperBound); // This could be not enough, but there doesn't seem to be a very good way to detect this.

		try
		{
			return FindITunesProcess(array);
		}
		finally
		{
			pool.Return(array);
		}
	}

	private static SafeProcessHandle? FindITunesProcess(int[] array)
	{
		if (!EnumProcesses(array, sizeof(int) * array.Length, out int length))
		{
			Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
		}
		var processIds = array.AsSpan(0, length / sizeof(int));

		foreach (var processId in processIds)
		{
			var process = OpenProcess(ProcessAccessRights.QueryLimitedInformation | ProcessAccessRights.Synchronize, false, processId);
			bool isITunesProcess = false;
			try
			{
				// OpenProcess can fail in various cases, but we shouldn't care about it.
				if (process.IsInvalid)
				{
					continue;
				}

				isITunesProcess = IsITunesProcess(process);
			}
			finally
			{
				if (!isITunesProcess)
				{
					process.Dispose();
				}
			}

			if (isITunesProcess)
			{
				return process;
			}
		}

		return null;
	}

	private static bool IsITunesProcess(SafeProcessHandle process)
		=> IsITunesProcessStackAlloc(process) ?? IsITunesProcessArrayPool(process);

	private static bool? IsITunesProcessStackAlloc(SafeProcessHandle process)
	{
		Span<char> span = stackalloc char[512];
		int length = span.Length;

		if (!QueryFullProcessImageName(process, 0, ref MemoryMarshal.GetReference(span), ref length))
		{
			int error = Marshal.GetHRForLastWin32Error();

			if (error != ErrorInsufficientBuffer)
			{
				Marshal.ThrowExceptionForHR(error);
			}

			return null;
		}

		return IsITunesProcessPath(span[..length]);
	}

	private static bool IsITunesProcessArrayPool(SafeProcessHandle process)
	{
		var pool = ArrayPool<char>.Shared;
		int requestedLength = 1024;
		var array = pool.Rent(requestedLength);
		int length = array.Length;

		while (!QueryFullProcessImageName(process, 0, ref array[0], ref length))
		{
			while (checked(requestedLength *= 2) < array.Length) ;

			pool.Return(array);
			int error = Marshal.GetHRForLastWin32Error();

			if (error != ErrorInsufficientBuffer)
			{
				Marshal.ThrowExceptionForHR(error);
			}

			array = pool.Rent(requestedLength);
		}

		bool result = IsITunesProcessPath(array.AsSpan(0, length));
		pool.Return(array);
		return result;
	}

	/// <summary>Identify a process that is or could be iTunes.</summary>
	/// <remarks>
	/// The conditions for a match are as follows:
	/// <list type="bullet">
	/// <description>The process name must be <c>iTunes.exe</c>.</description>
	/// <description>If the path to iTunes was found in the registry, there must be an exact match.</description>
	/// <description>If the path to iTunes was not found in the registry, or the path doesn't match, the cached path to iTunes is refreshed on a 10 min delay.</description>
	/// <description>If the path to iTunes was not found in the registry despite an up-to date cache, any iTunes.exe process will match.</description>
	/// </list>
	/// </remarks>
	/// <param name="path">The path to check.</param>
	/// <returns></returns>
	public static bool IsITunesProcessPath(Span<char> path)
	{
		if (path.EndsWith(@"\iTunes.exe"))
		{
			var (lastKnownPath, dateTime) = _lastKnownITunesPath;

			if (lastKnownPath is not null && path.SequenceEqual(lastKnownPath))
			{
				return true;
			}

			var now = DateTime.UtcNow;

			// Refresh the cached path every 10 minutes.
			if ((now - dateTime).Ticks > 10 * TimeSpan.TicksPerMinute)
			{
				TryLocateITunesBinary(out lastKnownPath);
				Volatile.Write(ref _lastKnownITunesPath, Tuple.Create(lastKnownPath, now));

				return lastKnownPath is null || path.SequenceEqual(lastKnownPath);
			}
			else if (lastKnownPath is null)
			{
				return true;
			}
		}

		return false;
	}
}
