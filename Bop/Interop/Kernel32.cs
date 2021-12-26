using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace Bop;

internal static partial class Interop
{
	[SuppressUnmanagedCodeSecurity]
	internal static partial class Kernel32
	{
		public const int ExitCodeStillActive = 0x00000103;

		[DllImport("kernel32", EntryPoint = "K32EnumProcesses", SetLastError = true)]
		public static extern bool EnumProcesses(int[] processIds, int size, out int requiredSize);

		[DllImport("kernel32", SetLastError = true)]
		public static extern SafeProcessHandle OpenProcess(ProcessAccessRights desiredAccess, bool inheritHandle, int processId);

		[DllImport("kernel32", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, ref char exeName, ref int size);

		[DllImport("kernel32", SetLastError = true)]
		public static extern IntPtr GetCurrentProcess();

		[DllImport("kernel32", SetLastError = true)]
		public static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

		[DllImport("kernel32", SetLastError = true)]
		public static extern bool DuplicateHandle
		(
			IntPtr sourceProcess,
			SafeHandle source,
			IntPtr targetProcess,
			out SafeWaitHandle target,
			uint desiredAccess,
			bool inheritHandle,
			DuplicateHandleOptions options
		);
	}
}
