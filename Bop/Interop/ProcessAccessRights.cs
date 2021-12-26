namespace Bop;

internal static partial class Interop
{
	internal static partial class Kernel32
	{
		public enum ProcessAccessRights : uint
		{
			QueryLimitedInformation = 0x1000,
			Synchronize = 0x00100000,
		}
	}
}