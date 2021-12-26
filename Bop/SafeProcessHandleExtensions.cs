using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Bop.Interop.Kernel32;

namespace Bop;

public static class SafeProcessHandleExtensions
{
	public static int? GetExitCode(this SafeProcessHandle process)
	{
		if (!GetExitCodeProcess(process, out uint exitCode))
		{
			Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
		}

		return exitCode != ExitCodeStillActive ? (int)exitCode : null;
	}
}