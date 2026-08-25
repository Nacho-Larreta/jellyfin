using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Tests.Logging;

internal sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>
{
}
