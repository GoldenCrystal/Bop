using System.Buffers;
using System.Runtime.InteropServices;

namespace Bop.Memory;

internal unsafe sealed class NativeMemoryManager : MemoryManager<byte>
{
	private const int DisposedBeacon = unchecked((int)0xC0000000);

	private IntPtr _memory;
	private int _length;
	private int _referenceCount;

	private int Length => Volatile.Read(ref _length) & ~int.MinValue;

	public NativeMemoryManager(int length)
	{
		_memory = (IntPtr)NativeMemory.Alloc(checked((nuint)length));
		GC.AddMemoryPressure(length);
		_length = length;
	}

#pragma warning disable CA2015
	~NativeMemoryManager() => Dispose(false);
#pragma warning restore CA2015

	protected override void Dispose(bool disposing)
	{
		var length = Volatile.Read(ref _length);

		if (length > 0 && Interlocked.CompareExchange(ref _length, length | int.MinValue, length) == length)
		{
			// NB: There can still be memory leaks if Pin/Unpin are misused, as the base Dispose method will call GC.SuppressFinalize(this).
			TryFreeMemory(disposing);
		}
	}

	public override Memory<byte> Memory => CreateMemory(Length);

	public override Span<byte> GetSpan() => new((void*)_memory, Length);

	public override MemoryHandle Pin(int elementIndex = 0)
	{
		if (Interlocked.Increment(ref _referenceCount) < 0)
		{
			Interlocked.Decrement(ref _referenceCount);
			throw new ObjectDisposedException(nameof(NativeMemoryManager));
		}

		return new MemoryHandle((void*)_memory, pinnable: this);
	}

	public override void Unpin()
	{
		int refCount = Interlocked.Decrement(ref _referenceCount);
		if (refCount == 0 && Volatile.Read(ref _length) < 0)
		{
			TryFreeMemory(false);
		}
		else if (refCount < 0)
		{
			Interlocked.Increment(ref refCount);
		}
	}

	private void TryFreeMemory(bool disposing)
	{
		// If called in the finalizer, force-free the memory.
		if (Interlocked.CompareExchange(ref _referenceCount, DisposedBeacon, 0) == 0 || !disposing)
		{
			int length = Length;
			// First set the length to zero, so that GetSpan() would return an empty span in worst case.
			Volatile.Write(ref _length, int.MinValue);
			NativeMemory.Free((void*)Interlocked.Exchange(ref _memory, IntPtr.Zero));
			GC.RemoveMemoryPressure(length);
		}
	}
}
