using Bop.Models;
using iTunesLib;

namespace Bop;

internal static class ITunesAppExtensions
{
	public static int? GetPlayerPositionInMilliseconds(this iTunesApp iTunes)
	{
		try
		{
			int position = iTunes.PlayerPositionMS;

			if (position < 0)
			{
				return null;
			}

			return position;
		}
		catch
		{
			return null;
		}
	}

	public static ProgressingTime? GetPlayerPositionInProgressingTime(this iTunesApp iTunes)
		=> iTunes.GetPlayerPositionInMilliseconds() is int ms ?
			ProgressingTime.FromMilliseconds(ms) :
			null;
}
