using System.Buffers;

namespace Bop.Memory;

internal sealed class EmptyMemoryManager<T> : MemoryManager<T>
{
	public static readonly EmptyMemoryManager<T> Instance = new();

	private EmptyMemoryManager() { }

	protected override void Dispose(bool disposing) { }

	public override Memory<T> Memory => new();
	public override Span<T> GetSpan() => new();
	public override MemoryHandle Pin(int elementIndex = 0) => new();
	public override void Unpin() { }
}
