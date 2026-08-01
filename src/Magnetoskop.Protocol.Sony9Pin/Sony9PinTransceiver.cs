using Magnetoskop.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Magnetoskop.Protocol.Sony9Pin;

/// <summary>
/// Wire-level request/response exchange over an <see cref="ISerialTransport"/>:
/// serializes exactly one outstanding command (the protocol forbids overlap),
/// enforces the response timeout, validates checksums, applies the retry policy,
/// and logs every frame in hex at Trace level.
/// </summary>
public sealed class Sony9PinTransceiver
{
    private readonly ISerialTransport _transport;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _exchangeLock = new(1, 1);
    private readonly byte[] _readBuffer = new byte[64];

    /// <summary>Response deadline. Spec says 9 ms; default is generous for USB adapters.</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Retries applied to timeouts and transmission-error NAKs (not to undefined-command NAKs).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Wait after a transmission-error NAK before retrying (spec: ≥ 10 ms).</summary>
    public TimeSpan NakRetryDelay { get; set; } = TimeSpan.FromMilliseconds(10);

    public Sony9PinTransceiver(ISerialTransport transport, ILogger logger)
    {
        _transport = transport;
        _logger = logger;
    }

    /// <summary>
    /// Sends a command and returns the classified response.
    /// Retries on timeout and transmission-error NAK; undefined-command NAKs are returned
    /// to the caller (they indicate the deck does not support the command, not a comms fault).
    /// </summary>
    /// <exception cref="VtrCommunicationException">No valid response after all retries.</exception>
    public async Task<Sony9PinResponse> ExchangeAsync(CommandBlock command, CancellationToken cancellationToken)
    {
        await _exchangeLock.WaitAsync(cancellationToken);
        try
        {
            Exception? lastError = null;

            for (var attempt = 0; attempt <= MaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var response = await ExchangeOnceAsync(command, cancellationToken);

                    if (response is Sony9PinResponse.Nak { IsUndefinedCommand: false } nak)
                    {
                        // Transmission error: per spec wait >= 10 ms, discard, retry.
                        _logger.LogWarning("NAK with error {Error} on attempt {Attempt} for {Command}",
                            nak.Error, attempt + 1, command);
                        lastError = new VtrCommunicationException($"Device NAK: {nak.Error}");
                        await Task.Delay(NakRetryDelay, cancellationToken);
                        _transport.DiscardInput();
                        continue;
                    }

                    return response;
                }
                catch (TimeoutException ex)
                {
                    _logger.LogWarning("Response timeout on attempt {Attempt} for {Command}",
                        attempt + 1, command);
                    lastError = ex;
                    _transport.DiscardInput();
                }
                catch (ChecksumException ex)
                {
                    _logger.LogWarning(ex, "Corrupt response on attempt {Attempt} for {Command}",
                        attempt + 1, command);
                    lastError = ex;
                    await Task.Delay(NakRetryDelay, cancellationToken);
                    _transport.DiscardInput();
                }
            }

            throw new VtrCommunicationException(
                $"No valid response to {command} after {MaxRetries + 1} attempts.",
                lastError ?? new TimeoutException());
        }
        finally
        {
            _exchangeLock.Release();
        }
    }

    private async Task<Sony9PinResponse> ExchangeOnceAsync(CommandBlock command, CancellationToken cancellationToken)
    {
        var bytes = command.ToBytes();
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("TX {Hex}", Convert.ToHexString(bytes));
        }

        _transport.DiscardInput();
        await _transport.WriteAsync(bytes, cancellationToken);

        var block = await ReadBlockAsync(cancellationToken);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("RX {Hex}", Convert.ToHexString(block.ToBytes()));
        }

        return Sony9PinResponseParser.Classify(block);
    }

    private async Task<CommandBlock> ReadBlockAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ResponseTimeout);

        var filled = 0;
        try
        {
            while (true)
            {
                var read = await _transport.ReadAsync(
                    _readBuffer.AsMemory(filled, _readBuffer.Length - filled), timeoutCts.Token);
                if (read == 0)
                {
                    throw new VtrCommunicationException("Serial transport closed while awaiting a response.");
                }
                filled += read;

                var block = CommandBlock.TryParse(_readBuffer.AsSpan(0, filled), out _);
                if (block is not null)
                {
                    return block;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No complete response within {ResponseTimeout.TotalMilliseconds:F0} ms " +
                $"(received {filled} byte(s): {Convert.ToHexString(_readBuffer.AsSpan(0, filled))}).");
        }
    }
}