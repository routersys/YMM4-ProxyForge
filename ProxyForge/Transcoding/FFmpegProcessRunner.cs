using System.Buffers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ProxyForge.Transcoding;

internal delegate void FFmpegLineHandler(ReadOnlySpan<char> line);

internal delegate Task FFmpegInputWriter(Stream destination, CancellationToken cancellationToken);

internal readonly record struct FFmpegProcessResult(int ExitCode, string Diagnostics)
{
    internal bool IsSuccess => ExitCode == 0;

    internal string FirstDiagnosticLine(int maximumLength)
    {
        var text = Diagnostics.AsSpan().Trim();
        if (text.IsEmpty)
            return string.Empty;

        var index = text.IndexOf('\n');
        var line = index < 0 ? text : text[..index];
        return new string(line.Length > maximumLength ? line[..maximumLength] : line);
    }
}

internal sealed class FFmpegDiagnosticsBuffer(int capacity)
{
    private readonly StringBuilder _builder = new();

    internal void Append(ReadOnlySpan<char> line)
    {
        var trimmed = line.Trim();
        if (trimmed.IsEmpty || _builder.Length >= capacity)
            return;

        if (_builder.Length != 0)
            _builder.Append('\n');

        var remaining = capacity - _builder.Length;
        _builder.Append(trimmed.Length > remaining ? trimmed[..remaining] : trimmed);
    }

    public override string ToString() => _builder.ToString();
}

internal static class FFmpegProcessRunner
{
    private const int LineBufferLength = 4096;
    private const int DiagnosticsCapacity = 4096;

    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);

    internal static async Task<FFmpegProcessResult> RunAsync(
        string executablePath,
        Action<Collection<string>> argumentWriter,
        string workingDirectory,
        FFmpegLineHandler? standardOutputHandler,
        FFmpegInputWriter? standardInputWriter,
        ProcessPriorityClass priority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = standardInputWriter is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = PipeEncoding,
            StandardErrorEncoding = PipeEncoding,
            WorkingDirectory = workingDirectory,
        };
        argumentWriter(startInfo.ArgumentList);

        var diagnostics = new FFmpegDiagnosticsBuffer(DiagnosticsCapacity);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                string.Concat("Failed to start the FFmpeg process: ", executablePath), ex);
        }

        try
        {
            process.PriorityClass = priority;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[FFmpegProcessRunner] Priority assignment failed: ", ex.Message));
        }

        var standardOutputTask = PumpLinesAsync(process.StandardOutput, standardOutputHandler);
        var standardErrorTask = PumpLinesAsync(process.StandardError, diagnostics.Append);

        var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static state => TryKill((Process)state!), process)
            : default;

        Exception? inputFailure = null;

        try
        {
            if (standardInputWriter is not null)
            {
                try
                {
                    await WriteStandardInputAsync(process, standardInputWriter, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    inputFailure = ex;
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await standardOutputTask.ConfigureAwait(false);
            await standardErrorTask.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (inputFailure is not null)
        {
            if (inputFailure is OperationCanceledException canceled)
                throw canceled;

            throw new InvalidOperationException(
                string.Concat("Failed to send frames to FFmpeg. ", diagnostics.ToString()), inputFailure);
        }

        return new FFmpegProcessResult(process.ExitCode, diagnostics.ToString());
    }

    private static async Task WriteStandardInputAsync(
        Process process, FFmpegInputWriter writer, CancellationToken cancellationToken)
    {
        var destination = process.StandardInput.BaseStream;
        try
        {
            await writer(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                destination.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Concat("[FFmpegProcessRunner] Closing stdin failed: ", ex.Message));
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Concat("[FFmpegProcessRunner] Kill failed: ", ex.Message));
        }
    }

    private static async Task PumpLinesAsync(TextReader reader, FFmpegLineHandler? handler)
    {
        var buffer = ArrayPool<char>.Shared.Rent(LineBufferLength);
        try
        {
            if (handler is null)
            {
                while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0)
                {
                }

                return;
            }

            var length = 0;
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(length)).ConfigureAwait(false);
                if (read == 0)
                    break;

                length += read;
                var consumed = 0;

                while (true)
                {
                    var pending = buffer.AsSpan(consumed, length - consumed);
                    var index = pending.IndexOf('\n');
                    if (index < 0)
                        break;

                    handler(TrimLineEnd(pending[..index]));
                    consumed += index + 1;
                }

                if (consumed > 0)
                {
                    buffer.AsSpan(consumed, length - consumed).CopyTo(buffer);
                    length -= consumed;
                }
                else if (length == buffer.Length)
                {
                    handler(buffer.AsSpan(0, length));
                    length = 0;
                }
            }

            if (length > 0)
                handler(TrimLineEnd(buffer.AsSpan(0, length)));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static ReadOnlySpan<char> TrimLineEnd(ReadOnlySpan<char> line) =>
        line.Length > 0 && line[^1] == '\r' ? line[..^1] : line;
}
