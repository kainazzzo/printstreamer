using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Common interface for video streaming implementations.
/// </summary>
internal interface IStreamer : IDisposable
{
	/// <summary>
	/// Task that completes when the stream ends.
	/// </summary>
	Task ExitTask { get; }

	/// <summary>
	/// Start streaming from source to destination.
	/// </summary>
	Task StartAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Stop the streaming process.
	/// </summary>
	void Stop();
}
