using System;

namespace PrusaConnect.Core.PrusaConnect;

/// <summary>
/// A still from a Prusa Connect camera's "last snapshot" endpoint, plus its
/// capture time (the Last-Modified header) so callers can show an "X old" label.
/// </summary>
public sealed record CameraSnapshot(byte[] Jpeg, DateTimeOffset? CapturedAt);
