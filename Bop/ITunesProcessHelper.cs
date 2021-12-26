using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Bop.Interop;
using static Bop.Interop.Kernel32;

namespace Bop;
internal static class ITunesProcessHelper
{
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

		const int maxDelay = 60_000;
		int delay = 1_000;

		try
		{
			while (true)
			{
				if (FindITunesProcess(array) is not null and var handle) return handle;

				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

				if (delay < maxDelay)
				{
					delay = Math.Min(delay * 2, maxDelay);
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

		return span[..length].EndsWith(@"\iTunes.exe");
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

		bool result = array.AsSpan(0, length).EndsWith(@"\iTunes.exe");
		pool.Return(array);
		return result;
	}
}
