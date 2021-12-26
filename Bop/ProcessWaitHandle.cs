using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Bop.Interop.Kernel32;

namespace Bop;

public class ProcessWaitHandle : WaitHandle
{
	public ProcessWaitHandle(SafeProcessHandle processHandle)
	{
		IntPtr currentProcess = GetCurrentProcess();

		bool success = DuplicateHandle
		(
			currentProcess,
			processHandle,
			currentProcess,
			out SafeWaitHandle waitHandle,
			0,
			false,
			DuplicateHandleOptions.SameAccess
		);
		
		if (!success)
		{
			int error = Marshal.GetHRForLastWin32Error();
			waitHandle.Dispose();
			Marshal.ThrowExceptionForHR(error);
		}

		SafeWaitHandle = waitHandle;
	}
}
