using System.Diagnostics;

namespace Bop.Models;

/// <summary>Keeps track of a progressing time value.</summary>
/// <remarks>
/// <para>This is a relatively simple abstraction used to provide the expected value of the point at an arbitrary point in time.</para>
/// <para>The main usecase is to update the predicted value of a music player's position based on current time.</para>
/// </remarks>
/// <param name="ReferenceTimestamp">Reference timestamp in <see cref="Stopwatch"/> ticks.</param>
/// <param name="TimeSpan">The relative time to be tracked.</param>
public record struct ProgressingTime(long ReferenceTimestamp, TimeSpan TimeSpan)
{
	private static readonly double TimeSpanTicksPerStopwatchTick = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

	public static ProgressingTime FromMilliseconds(int milliseconds) => new(new TimeSpan(milliseconds * TimeSpan.TicksPerMillisecond));

	public ProgressingTime(TimeSpan timeSpan) : this(Stopwatch.GetTimestamp(), timeSpan) { }

	public TimeSpan GetRebasedTimeSpan(long referenceTimestamp) => new(TimeSpan.Ticks + (long)((referenceTimestamp - ReferenceTimestamp) * TimeSpanTicksPerStopwatchTick));

	/// <summary>Gets <see cref="TimeSpan"/> rebased to current time.</summary>
	public TimeSpan GetRebasedTimeSpan() => GetRebasedTimeSpan(Stopwatch.GetTimestamp());

	public ProgressingTime Rebase(long referenceTimestamp) => new(referenceTimestamp, GetRebasedTimeSpan(referenceTimestamp));

	/// <summary>Rebases this value to the current time.</summary>
	public ProgressingTime Rebase() => Rebase(Stopwatch.GetTimestamp());
}
