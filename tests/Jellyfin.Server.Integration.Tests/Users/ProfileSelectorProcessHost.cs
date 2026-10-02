using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.System;

namespace Jellyfin.Server.Integration.Tests.Users;

internal sealed class ProfileSelectorProcessHost : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"jellyfin-selector-process-{Guid.NewGuid():N}");
    private readonly int _port;
    private Process? _process;

    public ProfileSelectorProcessHost()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(
            Path.Combine(_root, "config", "network.xml"),
            $"<NetworkConfiguration><InternalHttpPort>{_port.ToString(CultureInfo.InvariantCulture)}</InternalHttpPort>" +
            $"<PublicHttpPort>{_port.ToString(CultureInfo.InvariantCulture)}</PublicHttpPort>" +
            "<EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6>" +
            "<LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses></NetworkConfiguration>");
    }

    public HttpClient CreateClient()
        => new()
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_port.ToString(CultureInfo.InvariantCulture)}/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

    public async Task<int> StartAsync(CancellationToken cancellationToken, string? expectedServerId = null)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("The test server is already running.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = GetDotnetHostPath(),
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(typeof(Jellyfin.Server.Program).Assembly.Location);
        AddDirectoryArgument(startInfo, "--datadir", "data");
        AddDirectoryArgument(startInfo, "--configdir", "config");
        AddDirectoryArgument(startInfo, "--cachedir", "cache");
        AddDirectoryArgument(startInfo, "--logdir", "logs");
        startInfo.ArgumentList.Add("--nowebclient");
        startInfo.Environment["DOTNET_USE_POLLING_FILE_WATCHER"] = "1";
        startInfo.Environment["JELLYFIN_FFMPEG__NOVALIDATION"] = "true";

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("The test server did not start.");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        await WaitUntilReadyAsync(expectedServerId, cancellationToken).ConfigureAwait(false);
        return _process.Id;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process is null)
        {
            return;
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        _process.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await StopAsync(timeout.Token).ConfigureAwait(false);
        Directory.Delete(_root, recursive: true);
    }

    private static string GetDotnetHostPath()
    {
        var configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configuredHost))
        {
            return configuredHost;
        }

        var runtimeRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(runtimeRoot))
        {
            return Path.Combine(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        }

        return "dotnet";
    }

    private void AddDirectoryArgument(ProcessStartInfo startInfo, string option, string directoryName)
    {
        startInfo.ArgumentList.Add(option);
        startInfo.ArgumentList.Add(Path.Combine(_root, directoryName));
    }

    private async Task WaitUntilReadyAsync(string? expectedServerId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var client = CreateClient();

        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (_process!.HasExited)
            {
                throw new InvalidOperationException($"The test server exited with code {_process.ExitCode.ToString(CultureInfo.InvariantCulture)} before becoming ready.");
            }

            try
            {
                using var response = await client.GetAsync(
                    expectedServerId is null ? "Startup/User" : "System/Info/Public",
                    timeout.Token).ConfigureAwait(false);
                if (expectedServerId is null && response.StatusCode is HttpStatusCode.OK)
                {
                    return;
                }

                if (expectedServerId is not null && response.StatusCode is HttpStatusCode.OK)
                {
                    var info = await response.Content.ReadFromJsonAsync<PublicSystemInfo>(JsonDefaults.Options, timeout.Token).ConfigureAwait(false);
                    if (info?.StartupWizardCompleted is true && string.Equals(info.Id, expectedServerId, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token).ConfigureAwait(false);
        }
    }
}
