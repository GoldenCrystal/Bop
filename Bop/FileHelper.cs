using System.Buffers;
using Bop.Memory;

namespace Bop;

public static class FileHelper
{
	public static async ValueTask<MemoryManager<byte>> ReadToNativeMemoryAsync(string filename, CancellationToken cancellationToken)
	{
		using (var stream = File.OpenRead(filename))
		{
			int streamLength = checked((int)stream.Length);

			if (streamLength == 0) return EmptyMemoryManager<byte>.Instance;

			var memoryManager = new NativeMemoryManager(streamLength);

			var memory = memoryManager.Memory;

			try
			{
				while (memory.Length > 0)
				{
					int count = await stream.ReadAsync(memory, cancellationToken);
					if (count == 0) throw new EndOfStreamException();
					memory = memory.Slice(count);
				}
			}
			catch
			{
				((IDisposable)memoryManager).Dispose();
				throw;
			}

			return memoryManager;
		}
	}
}
