using System.Globalization;
using System.Text;

namespace AgentsWinBar.Services;

/// <summary>
/// A private, per-invocation line protocol. Payloads are base64 so command output
/// can never introduce frame boundaries, even when it has no final newline.
/// </summary>
internal static class WslBatchProtocol
{
    internal const int MaxFrameChars = ((WslOutputReader.CommandLimitBytes + 2) / 3) * 4 + 256;
    // At most 32 MiB of temporary stdout/stderr files, even if all jobs overflow together.
    internal const int MaxCommands = 4;
    internal static string CreateMarker() => "CBW_" + Guid.NewGuid().ToString("N");

    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    internal static string BuildScript(IReadOnlyList<WslBatchCommand> commands, string marker)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count > MaxCommands) throw new ArgumentOutOfRangeException(nameof(commands), "Too many WSL batch commands");
        if (!marker.StartsWith("CBW_", StringComparison.Ordinal) || marker.Length != 36 ||
            !Guid.TryParseExact(marker[4..], "N", out _))
            throw new ArgumentException("Invalid batch marker", nameof(marker));

        var script = new StringBuilder("""
            set +e +u +m
            command -v timeout >/dev/null && command -v flock >/dev/null && command -v base64 >/dev/null && command -v wc >/dev/null && command -v head >/dev/null && command -v cat >/dev/null || exit 125
            umask 077
            cbw_dir=$(mktemp -d) || exit 125
            cbw_pids=()
            cbw_cleanup() {
                trap - EXIT HUP INT TERM
                for cbw_file in "$cbw_dir"/*.pid; do
                    [ -f "$cbw_file" ] || continue
                    read -r cbw_job < "$cbw_file"
                    kill -KILL -- "-$cbw_job" 2>/dev/null || :
                done
                for cbw_pid in "${cbw_pids[@]}"; do kill "$cbw_pid" 2>/dev/null || :; done
                wait 2>/dev/null || :
                rm -rf -- "$cbw_dir"
            }
            trap cbw_cleanup EXIT
            trap 'exit 130' HUP INT TERM
            """.Replace("\r\n", "\n") + "\n");

        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            if (command.Timeout <= TimeSpan.Zero || !double.IsFinite(command.Timeout.TotalSeconds))
                throw new ArgumentOutOfRangeException(nameof(commands), "Timeout must be positive and finite");
            if (command.Command.Contains('\0'))
                throw new ArgumentException("Shell commands cannot contain NUL", nameof(commands));
            var seconds = command.Timeout.TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture);
            var timeoutThreshold = (Math.Ceiling(command.Timeout.TotalSeconds) - 1).ToString("0", CultureInfo.InvariantCulture);
            // GNU timeout supervises a separate process group, so shell children
            // are terminated too. Its background PID is also the process group ID.
            // Kill remaining descendants even if their immediate parent exits early.
            // Capture outside the command's process group. Drain excess bytes without storing them,
            // then wait for capture EOF after killing descendants, before inspecting the bounded files.
            script.Append("(\n cbw_start=$SECONDS\n exec 3> >(head -c ").Append(WslOutputReader.CommandLimitBytes)
                .Append(" >\"$cbw_dir/").Append(i).Append(".out\"; cat >/dev/null)\n cbw_out_pid=$!\n exec 4> >(head -c ")
                .Append(WslOutputReader.CommandLimitBytes).Append(" >\"$cbw_dir/").Append(i)
                .Append(".err\"; cat >/dev/null)\n cbw_err_pid=$!\n timeout --kill-after=2s ").Append(seconds)
                .Append("s bash -c 'exec >&3 2>&4; exec 3>&- 4>&-; exec bash -c \"$1\"' cbw ")
                .Append(Quote(command.Command)).Append(" &\n cbw_job=$!\n printf '%s\\n' \"$cbw_job\" >\"$cbw_dir/")
                .Append(i).Append(".pid\"\n wait \"$cbw_job\"\n cbw_exit=$?\n kill -KILL -- \"-$cbw_job\" 2>/dev/null || :\n rm -f -- \"$cbw_dir/")
                // A descendant that escaped the process group can keep the capture pipes open; give the drain 5 s, then keep what was written.
                .Append(i).Append(".pid\"\n exec 3>&- 4>&-\n cbw_grace=0\n while [ \"$cbw_grace\" -lt 50 ] && { kill -0 \"$cbw_out_pid\" 2>/dev/null || kill -0 \"$cbw_err_pid\" 2>/dev/null; }; do sleep 0.1; cbw_grace=$((cbw_grace + 1)); done\n")
                .Append(" kill \"$cbw_out_pid\" \"$cbw_err_pid\" 2>/dev/null || :\n cbw_elapsed=$((SECONDS - cbw_start))\n")
                .Append(" case \"$cbw_exit\" in 137|124|143) if [ \"$cbw_elapsed\" -ge ")
                .Append(timeoutThreshold).Append(" ]; then cbw_exit=124; fi ;; esac\n")
                .Append(" cbw_size=$(( $(wc -c <\"$cbw_dir/").Append(i).Append(".out\") + $(wc -c <\"$cbw_dir/")
                .Append(i).Append(".err\") ))\n")
                .Append(" if [ \"$cbw_exit\" -eq 153 ] || [ \"$cbw_size\" -ge ").Append(WslOutputReader.CommandLimitBytes)
                .Append(" ]; then cbw_exit=122; : >\"$cbw_dir/").Append(i).Append(".out\"; printf '%s' 'WSL command output limit exceeded (4 MiB)' >\"$cbw_dir/")
                .Append(i).Append(".err\"; fi\n")
                .Append(" cbw_out=$(base64 -w 0 <\"$cbw_dir/").Append(i).Append(".out\") || exit 125\n")
                .Append(" cbw_err=$(base64 -w 0 <\"$cbw_dir/").Append(i).Append(".err\") || exit 125\n")
                .Append(" ( flock -x 9 || exit 125\n printf '\\n%s|%s|%s|%s|%s\\n' ")
                .Append(Quote(marker)).Append(' ').Append(i).Append(" \"$cbw_exit\" \"$cbw_out\" \"$cbw_err\"\n")
                .Append(" ) 9>\"$cbw_dir/lock\"\n) &\ncbw_pids+=($!)\n");
        }
        script.Append("cbw_failed=0\nfor cbw_pid in \"${cbw_pids[@]}\"; do wait \"$cbw_pid\" || cbw_failed=1; done\ncbw_pids=()\nexit \"$cbw_failed\"\n");
        // Normalize the template above, never the quoted command: CRLF can be literal payload data.
        return script.ToString();
    }

    internal static bool TryParseLine(string line, string marker, int count, out int index, out WslResult? result)
    {
        index = -1;
        result = null;
        if (line.Length > MaxFrameChars) throw new WslOutputLimitException();
        if (!line.StartsWith(marker + "|", StringComparison.Ordinal)) return false;
        var parts = line.Split('|');
        if (parts.Length != 5 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out index) ||
            index < 0 || index >= count ||
            !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exit))
            throw new InvalidDataException("Invalid WSL batch frame header");
        try
        {
            // Check decoded lengths before allocating either payload.
            static int DecodedLength(string value) => value.Length / 4 * 3 -
                (value.EndsWith("==", StringComparison.Ordinal) ? 2 : value.EndsWith('=') ? 1 : 0);
            if ((long)DecodedLength(parts[3]) + DecodedLength(parts[4]) > WslOutputReader.CommandLimitBytes)
            {
                result = new WslResult { ExitCode = -1, Error = new WslOutputLimitException().Message };
                return true;
            }
            var outputBytes = Convert.FromBase64String(parts[3]);
            var errorBytes = Convert.FromBase64String(parts[4]);
            if ((long)outputBytes.Length + errorBytes.Length > WslOutputReader.CommandLimitBytes)
            {
                result = new WslResult { ExitCode = -1, Error = new WslOutputLimitException().Message };
                return true;
            }
            var output = Encoding.UTF8.GetString(outputBytes);
            var error = Encoding.UTF8.GetString(errorBytes);
            if (exit == 124)
                error = string.IsNullOrEmpty(error) ? "Command timed out" : error + "\nCommand timed out";
            result = new WslResult { Success = exit == 0, Output = output, Error = error, ExitCode = exit };
            return true;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Invalid WSL batch payload", ex);
        }
    }

    internal static async Task ReadResultsAsync(TextReader reader, string marker, WslResult?[] results,
        Action<int, WslResult>? onCompleted, CancellationToken ct, WslOutputReader.Budget? budget = null)
    {
        await foreach (var line in WslOutputReader.ReadLinesAsync(reader, MaxFrameChars,
            budget ?? new WslOutputReader.Budget(WslOutputReader.BatchLimitBytes), ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!TryParseLine(line, marker, results.Length, out var index, out var result)) continue;
            if (results[index] != null) throw new InvalidDataException("Duplicate WSL batch frame");
            results[index] = result!;
            onCompleted?.Invoke(index, result!);
        }
    }
}
