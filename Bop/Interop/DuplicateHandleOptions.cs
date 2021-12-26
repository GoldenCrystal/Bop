namespace Bop;

internal static partial class Interop
{
	internal static partial class Kernel32
	{
		public enum DuplicateHandleOptions : uint
		{
			None = 0,
			CloseSource = 1,
			SameAccess = 2,
		}
	}
}
