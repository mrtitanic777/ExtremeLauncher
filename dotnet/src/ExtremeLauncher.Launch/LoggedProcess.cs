// SPDX-License-Identifier: GPL-3.0-only
/*
 *  Extreme Launcher - Minecraft Launcher
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, version 3.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <https://www.gnu.org/licenses/>.
 *
 * Ported from launcher/LoggedProcess.{h,cpp} and launcher/MessageLevel.{h,cpp}.
 *
 * The game process, with its output turned into tagged log lines. Everything the user sees in the log
 * window comes through here, and so does the crash-versus-clean-exit distinction.
 *
 * LINE REASSEMBLY IS THE POINT. A process writes bytes, not lines: a single read can end mid-line, and
 * a line can arrive split across several reads. The leftover fragment is carried forward so the log
 * never shows a half line — which matters because the level prefix sits at the *start* of a line, and
 * a split there would lose the tag.
 *
 * THE !![Level]! PREFIX is the launcher's own convention. Minecraft does not emit it; the launcher
 * wrapper does, so its own messages can be levelled while the game's plain output is tagged StdOut or
 * StdErr.
 */

using System.Diagnostics;
using System.Text;

namespace ExtremeLauncher.Launch;

/// <summary>How a log line should be presented.</summary>
public enum MessageLevel
{
    /// <summary>No idea what this is or where it came from.</summary>
    Unknown,

    StdOut,
    StdErr,

    /// <summary>The launcher speaking, rather than the game.</summary>
    Launcher,

    Debug,
    Info,
    Message,
    Warning,
    Error,
    Fatal,
}

public static class MessageLevels
{
    /// <remarks>
    /// StdOut, StdErr and Unknown are deliberately absent: they describe where a line came from, not
    /// something a process may claim about itself.
    /// </remarks>
    public static MessageLevel FromName(string name)
        => name switch
        {
            "Launcher" => MessageLevel.Launcher,
            "Debug" => MessageLevel.Debug,
            "Info" => MessageLevel.Info,
            "Message" => MessageLevel.Message,
            "Warning" => MessageLevel.Warning,
            "Error" => MessageLevel.Error,
            "Fatal" => MessageLevel.Fatal,
            _ => MessageLevel.Unknown,
        };

    /// <summary>
    /// Strips a leading <c>!![Level]!</c> marker, returning the level it named.
    /// </summary>
    /// <param name="line">Stripped of the marker when one was found.</param>
    public static MessageLevel FromLine(ref string line)
    {
        const string prefix = "!![";
        const string terminator = "]!";

        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return MessageLevel.Unknown;
        }

        var end = line.IndexOf(terminator, StringComparison.Ordinal);

        if (end == -1)
        {
            return MessageLevel.Unknown;
        }

        var level = FromName(line[prefix.Length..end]);
        line = line[(end + terminator.Length)..];

        return level;
    }
}

public sealed class LogLinesEventArgs : EventArgs
{
    public LogLinesEventArgs(IReadOnlyList<string> lines, MessageLevel level)
    {
        Lines = lines;
        Level = level;
    }

    public IReadOnlyList<string> Lines { get; }

    public MessageLevel Level { get; }
}

public sealed class LoggedProcess : IDisposable
{
    public enum ProcessState
    {
        NotRunning,
        Starting,
        FailedToStart,
        Running,
        Finished,
        Crashed,
        Aborted,
    }

    private readonly Process _process = new();
    private readonly LineAssembler _stdout = new();
    private readonly LineAssembler _stderr = new();

    private Task _pump = Task.CompletedTask;
    private bool _isAborting;
    private bool _disposed;

    public LoggedProcess(Encoding? outputEncoding = null)
    {
        _process.StartInfo.RedirectStandardOutput = true;
        _process.StartInfo.RedirectStandardError = true;
        _process.StartInfo.UseShellExecute = false;
        _process.StartInfo.CreateNoWindow = true;

        if (outputEncoding is not null)
        {
            _process.StartInfo.StandardOutputEncoding = outputEncoding;
            _process.StartInfo.StandardErrorEncoding = outputEncoding;
        }

        _process.EnableRaisingEvents = true;
    }

    public event EventHandler<LogLinesEventArgs>? Log;

    public event EventHandler<ProcessState>? StateChanged;

    public ProcessState State { get; private set; } = ProcessState.NotRunning;

    public int ExitCode { get; private set; }

    /// <summary>Whether the process should outlive the launcher.</summary>
    public bool IsDetachable { get; set; }

    public void Start(string program, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
    {
        _process.StartInfo.FileName = program;
        _process.StartInfo.WorkingDirectory = workingDirectory;

        _process.StartInfo.ArgumentList.Clear();

        foreach (var argument in arguments)
        {
            _process.StartInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                _process.StartInfo.Environment[key] = value;
            }
        }

        ChangeState(ProcessState.Starting);

        try
        {
            _process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            EmitLog([$"Failed to start process: {e.Message}"], MessageLevel.Fatal);
            ChangeState(ProcessState.FailedToStart);
            return;
        }

        ChangeState(ProcessState.Running);

        // Raw reads rather than BeginOutputReadLine: that helper delivers pre-split lines, which
        // would bypass the reassembly and lose a final line with no trailing newline.
        _pump = Task.WhenAll(
            PumpAsync(_process.StandardOutput, _stdout, MessageLevel.StdOut),
            PumpAsync(_process.StandardError, _stderr, MessageLevel.StdErr));
    }

    /// <summary>Waits for exit and settles the final state.</summary>
    public async Task<ProcessState> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (State is ProcessState.FailedToStart or ProcessState.NotRunning)
        {
            return State;
        }

        try
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
            throw;
        }

        // Drain the readers before flushing, or the tail of the output races the exit.
        await _pump.ConfigureAwait(false);

        // Anything still buffered without a trailing newline would otherwise be lost.
        FlushRemainder();

        ExitCode = _process.ExitCode;
        OnExit();

        return State;
    }

    /// <remarks>
    /// .NET has no equivalent of QProcess::NormalExit, so a non-zero exit code is treated as a crash.
    /// That matches what the launcher does with it — the distinction only ever drives the message and
    /// whether the log window stays open.
    /// </remarks>
    private void OnExit()
    {
        if (_isAborting)
        {
            EmitLog(["Process was killed by user."], MessageLevel.Error);
            ChangeState(ProcessState.Aborted);
            return;
        }

        if (ExitCode == 0)
        {
            EmitLog([$"Process exited with code {ExitCode}."], MessageLevel.Launcher);
            ChangeState(ProcessState.Finished);
            return;
        }

        EmitLog(
            ExitCode == -1 ? ["Process crashed."] : [$"Process crashed with exitcode {ExitCode}."],
            MessageLevel.Launcher);

        ChangeState(ProcessState.Crashed);
    }

    /// <summary>Terminates the process. Equivalent to kill -9.</summary>
    public void Kill()
    {
        _isAborting = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private async Task PumpAsync(StreamReader reader, LineAssembler assembler, MessageLevel defaultLevel)
    {
        var buffer = new char[4096];

        while (true)
        {
            int read;

            try
            {
                read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            EmitLines(assembler.Accept(new string(buffer, 0, read)), defaultLevel);
        }
    }

    private void FlushRemainder()
    {
        EmitLines(_stdout.Flush(), MessageLevel.StdOut);
        EmitLines(_stderr.Flush(), MessageLevel.StdErr);
    }

    /// <summary>
    /// Splits lines by level, so a run of same-level lines is reported in one batch.
    /// </summary>
    private void EmitLines(IReadOnlyList<string> lines, MessageLevel defaultLevel)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var batch = new List<string>();
        var batchLevel = defaultLevel;

        foreach (var raw in lines)
        {
            var line = raw;
            var level = MessageLevels.FromLine(ref line);

            if (level == MessageLevel.Unknown)
            {
                level = defaultLevel;
            }

            if (level != batchLevel && batch.Count != 0)
            {
                EmitLog(batch, batchLevel);
                batch = [];
            }

            batchLevel = level;
            batch.Add(line);
        }

        if (batch.Count != 0)
        {
            EmitLog(batch, batchLevel);
        }
    }

    private void EmitLog(IReadOnlyList<string> lines, MessageLevel level)
        => Log?.Invoke(this, new LogLinesEventArgs(lines, level));

    private void ChangeState(ProcessState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!IsDetachable)
        {
            Kill();
        }

        _process.Dispose();
    }

    /// <summary>
    /// Turns a byte stream into whole lines, carrying an unterminated fragment across reads.
    /// </summary>
    /// <remarks>
    /// .NET's OutputDataReceived already splits on newlines, so this mostly normalises line endings
    /// and preserves upstream's shape. The Flush at exit is what stops a final line without a
    /// trailing newline — which is exactly how a crash message often arrives — from being dropped.
    /// </remarks>
    internal sealed class LineAssembler
    {
        private string _leftover = string.Empty;

        public IReadOnlyList<string> Accept(string data)
        {
            var text = _leftover + data;
            _leftover = string.Empty;

            // Strip CR so CRLF input does not leave a stray carriage return on every line.
            var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');

            // The trailing element is ALWAYS held back. With no newline in this chunk that is the
            // whole fragment, and emitting it now would split a line still arriving -- losing the
            // !![Level]! prefix whenever a read happens to land inside it.
            _leftover = lines[^1];
            return lines[..^1];
        }

        public IReadOnlyList<string> Flush()
        {
            if (_leftover.Length == 0)
            {
                return [];
            }

            var remainder = _leftover;
            _leftover = string.Empty;

            return [remainder];
        }
    }
}
