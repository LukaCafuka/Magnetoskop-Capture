using System.IO.Ports;
using Magnetoskop.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Serial;

/// <summary>
/// <see cref="ISerialTransport"/> implementation over <see cref="SerialPort"/>.
/// Contains no protocol knowledge; settings (38400/8/odd/1 for Sony 9-pin)
/// are supplied by the caller.
/// </summary>
public sealed class SerialPortTransport : ISerialTransport
{
    private readonly ILogger<SerialPortTransport> _logger;
    private SerialPort? _port;

    public SerialPortTransport(ILogger<SerialPortTransport> logger)
    {
        _logger = logger;
    }

    public bool IsOpen => _port?.IsOpen ?? false;

    public Task OpenAsync(SerialSettings settings, CancellationToken cancellationToken = default)
    {
        if (IsOpen)
        {
            throw new InvalidOperationException($"Port {_port!.PortName} is already open.");
        }

        var port = new SerialPort(settings.PortName)
        {
            BaudRate = settings.BaudRate,
            DataBits = settings.DataBits,
            Parity = settings.Parity switch
            {
                SerialParity.Odd => Parity.Odd,
                SerialParity.Even => Parity.Even,
                _ => Parity.None,
            },
            StopBits = settings.StopBits switch
            {
                2 => StopBits.Two,
                _ => StopBits.One,
            },
            ReadTimeout = (int)settings.ReadTimeout.TotalMilliseconds,
            WriteTimeout = (int)settings.WriteTimeout.TotalMilliseconds,
            Handshake = Handshake.None,
        };

        try
        {
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            port.Dispose();
            throw new VtrCommunicationException(
                $"Cannot open serial port {settings.PortName}: {ex.Message}", ex);
        }

        _port = port;
        _logger.LogInformation(
            "Opened {Port} at {Baud} baud, {DataBits}{Parity}{StopBits}",
            settings.PortName, settings.BaudRate, settings.DataBits,
            settings.Parity.ToString()[0], settings.StopBits);
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_port is not null)
        {
            var name = _port.PortName;
            try
            {
                if (_port.IsOpen) _port.Close();
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Error closing {Port}", name);
            }
            _port.Dispose();
            _port = null;
            _logger.LogInformation("Closed {Port}", name);
        }
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var port = RequireOpenPort();
        try
        {
            // Write the whole block in one call so the ≤10 ms inter-byte rule is
            // satisfied by the UART hardware pacing at 38400 baud (~0.29 ms/byte).
            await port.BaseStream.WriteAsync(buffer, cancellationToken);
            await port.BaseStream.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            throw new VtrCommunicationException($"Serial write failed: {ex.Message}", ex);
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var port = RequireOpenPort();
        try
        {
            return await port.BaseStream.ReadAsync(buffer, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            throw new VtrCommunicationException($"Serial read failed: {ex.Message}", ex);
        }
    }

    public void DiscardInput()
    {
        try
        {
            if (IsOpen) _port!.DiscardInBuffer();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "DiscardInBuffer failed");
        }
    }

    private SerialPort RequireOpenPort()
        => _port is { IsOpen: true } port
            ? port
            : throw new VtrCommunicationException("Serial port is not open.");

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }
}

/// <summary>Lists COM ports present on the machine.</summary>
public sealed class SerialPortEnumerator : ISerialPortEnumerator
{
    public IReadOnlyList<string> GetPortNames()
        => SerialPort.GetPortNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
}