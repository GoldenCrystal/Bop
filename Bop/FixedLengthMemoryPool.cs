using System.Buffers;

namespace Bop;

internal class FixedLengthMemoryPool : MemoryPool<byte>
{
	private sealed class MemoryOwner : IMemoryOwner<byte>
	{
		private readonly ArrayPool<byte> _pool;
		private byte[]? _array;

		public MemoryOwner(ArrayPool<byte> pool, int length)
		{
			_pool = pool;
			_array = pool.Rent(length);
		}

		public Memory<byte> Memory => new Memory<byte>(_array);

		public void Dispose()
		{
			byte[]? array = _array;
			if (array != null)
			{
				_array = null;
				_pool.Return(array);
			}
		}
	}

	public FixedLengthMemoryPool(int length, int maxArraysPerBucket)
	{
		_arrayPool = ArrayPool<byte>.Create(length, maxArraysPerBucket);
		MaxBufferSize = length;
	}

	private readonly ArrayPool<byte> _arrayPool;

	public override int MaxBufferSize { get; }

	protected override void Dispose(bool disposing) { }

	public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
	{
		if ((uint)minBufferSize > MaxBufferSize && minBufferSize != -1)
			throw new ArgumentOutOfRangeException(nameof(minBufferSize));

		return new MemoryOwner(_arrayPool, MaxBufferSize);
	}
}
