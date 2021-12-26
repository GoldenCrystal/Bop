using System.Runtime.CompilerServices;

namespace Bop;

public static class WaitHandleExtensions
{
	public static Task<bool> WaitAsync(this WaitHandle waitHandle, int millisecondsTimeOutInterval)
	{
		var tcs = new TaskCompletionSource<bool>();

		ThreadPool.RegisterWaitForSingleObject
		(
			waitHandle,
			static (state, timedOut) => Unsafe.As<TaskCompletionSource<bool>>(state!).TrySetResult(!timedOut),
			tcs,
			millisecondsTimeOutInterval,
			true
		);

		return tcs.Task;
	}

	private sealed class CancellableWaitState
	{
		// There is a possible race between cancellation and wait handle registration.
		// This beacon is used to indicate when cancellation has been requested before.
		private static readonly object ShouldUnregisterWaitHandleBeacon = new();
		// This beacon is used to indicate that the wait handle was unregistered.
		private static readonly object WaitHandleUnregisteredBeacon = new();

		public TaskCompletionSource<bool> TaskCompletionSource { get; } = new TaskCompletionSource<bool>();

		public CancellationToken CancellationToken { get; }

		public CancellationTokenRegistration CancellationTokenRegistration { get; set; }

		private object? _registeredWaitHandle;

		// TODO: Verify how to simplify the conditions below (we may not need the WaitHandleUnregisteredBeacon value)

		public void SetOrUnregisterRegisteredWaitHandle(RegisteredWaitHandle registeredWaitHandle)
		{
			if (Interlocked.CompareExchange(ref _registeredWaitHandle, registeredWaitHandle, null) == ShouldUnregisterWaitHandleBeacon &&
				Interlocked.CompareExchange(ref _registeredWaitHandle, WaitHandleUnregisteredBeacon, ShouldUnregisterWaitHandleBeacon) == ShouldUnregisterWaitHandleBeacon)
			{
				registeredWaitHandle.Unregister(null);
			}
		}

		public void UnregisterWaitHandle()
		{
			if (Interlocked.CompareExchange(ref _registeredWaitHandle, ShouldUnregisterWaitHandleBeacon, null) is RegisteredWaitHandle registeredWaitHandle &&
				Interlocked.CompareExchange(ref _registeredWaitHandle, WaitHandleUnregisteredBeacon, registeredWaitHandle) == ShouldUnregisterWaitHandleBeacon)
			{
				registeredWaitHandle.Unregister(null);
			}
		}

		public CancellableWaitState(CancellationToken cancellationToken)
		{
			CancellationToken = cancellationToken;
		}
	}

	public static Task<bool> WaitAsync(this WaitHandle waitHandle, int millisecondsTimeOutInterval, CancellationToken cancellationToken)
	{
		var waitState = new CancellableWaitState(cancellationToken);

		waitState.CancellationTokenRegistration = cancellationToken.Register
		(
			static state =>
			{
				var waitState = Unsafe.As<CancellableWaitState>(state!);
				if (waitState.TaskCompletionSource.TrySetCanceled(waitState.CancellationToken))
				{
					waitState.CancellationTokenRegistration.Dispose();
					waitState.UnregisterWaitHandle();
				}
			},
			waitState,
			false
		);

		waitState.SetOrUnregisterRegisteredWaitHandle
		(
			ThreadPool.RegisterWaitForSingleObject
			(
				waitHandle,
				static (state, timedOut) =>
				{
					var waitState = Unsafe.As<CancellableWaitState>(state!);

					if (waitState.TaskCompletionSource.TrySetResult(!timedOut))
					{
						waitState.CancellationTokenRegistration.Dispose();
						waitState.UnregisterWaitHandle();
					}
				},
				waitState,
				millisecondsTimeOutInterval,
				true
			)
		);

		return waitState.TaskCompletionSource.Task;
	}
}
