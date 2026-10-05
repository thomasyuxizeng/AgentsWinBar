using System.Globalization;
using System.Text;

namespace CodexBarWin.Services;

/// <summary>
/// A private, per-invocation line protocol. Payloads are base64 so command output
/// can never introduce frame boundaries, even when it has no final newline.
/// </summary>
internal static class WslBatchProtocol
{
    internal static string CreateMarker() => "CBW_" + Guid.NewGuid().ToString("N");

    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    internal static string BuildScript(IReadOnlyList<WslBatchCommand> commands, string marker)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (!marker.StartsWith("CBW_", StringComparison.Ordinal) || marker.Length != 36 ||
            !Guid.TryParseExact(marker[4..], "N", out _))
            throw new ArgumentException("Invalid batch marker", nameof(marker));

        var script = new StringBuilder("""
            set +e +u +m
            command -v timeout >/dev/null && command -v flock >/dev/null && command -v base64 >/dev/null && command -v grep >/dev/null || exit 125
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
            """ + "\n");

        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            if (command.Timeout <= TimeSpan.Zero || !double.IsFinite(command.Timeout.TotalSeconds))
                throw new ArgumentOutOfRangeException(nameof(commands), "Timeout must be positive and finite");
            if (command.Command.Contains('\0'))
                throw new ArgumentException("Shell commands cannot contain NUL", nameof(commands));
            var seconds = command.Timeout.TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture);
            // GNU timeout supervises a separate process group, so shell children
            // are terminated too. --kill-after bounds TERM-resistant commands.
            script.Append("(\n LC_MESSAGES=C timeout --verbose --kill-after=2s ").Append(seconds)
                .Append("s bash -c 'exec >\"$1\" 2>\"$2\"; exec bash -c \"$3\"' cbw \"$cbw_dir/")
                .Append(i).Append(".out\" \"$cbw_dir/").Append(i).Append(".err\" ")
                .Append(Quote(command.Command)).Append(" 2>\"$cbw_dir/").Append(i)
                .Append(".timeout\" &\n cbw_job=$!\n printf '%s\\n' \"$cbw_job\" >\"$cbw_dir/")
                .Append(i).Append(".pid\"\n wait \"$cbw_job\"\n cbw_exit=$?\n rm -f -- \"$cbw_dir/")
                .Append(i).Append(".pid\"\n if [ \"$cbw_exit\" -eq 137 ] && grep -q '^timeout: sending signal ' \"$cbw_dir/")
                .Append(i).Append(".timeout\"; then cbw_exit=124; fi\n")
                .Append(" if [ \"$cbw_exit\" -ne 124 ]; then cat \"$cbw_dir/").Append(i)
                .Append(".timeout\" >>\"$cbw_dir/").Append(i).Append(".err\"; fi\n")
                .Append(" cbw_out=$(base64 -w 0 <\"$cbw_dir/").Append(i).Append(".out\") || exit 125\n")
                .Append(" cbw_err=$(base64 -w 0 <\"$cbw_dir/").Append(i).Append(".err\") || exit 125\n")
                .Append(" ( flock -x 9 || exit 125\n printf '\\n%s|%s|%s|%s|%s\\n' ")
                .Append(Quote(marker)).Append(' ').Append(i).Append(" \"$cbw_exit\" \"$cbw_out\" \"$cbw_err\"\n")
                .Append(" ) 9>\"$cbw_dir/lock\"\n) &\ncbw_pids+=($!)\n");
        }
        script.Append("cbw_failed=0\nfor cbw_pid in \"${cbw_pids[@]}\"; do wait \"$cbw_pid\" || cbw_failed=1; done\ncbw_pids=()\nexit \"$cbw_failed\"\n");
        return script.ToString();
    }

    internal static bool TryParseLine(string line, string marker, int count, out int index, out WslResult? result)
    {
        index = -1;
        result = null;
        if (!line.StartsWith(marker + "|", StringComparison.Ordinal)) return false;
        var parts = line.Split('|');
        if (parts.Length != 5 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out index) ||
            index < 0 || index >= count ||
            !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exit))
            throw new InvalidDataException("Invalid WSL batch frame header");
        try
        {
            var output = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3]));
            var error = Encoding.UTF8.GetString(Convert.FromBase64String(parts[4]));
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
        Action<int, WslResult>? onCompleted, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryParseLine(line, marker, results.Length, out var index, out var result)) continue;
            if (results[index] != null) throw new InvalidDataException("Duplicate WSL batch frame");
            results[index] = result!;
            onCompleted?.Invoke(index, result!);
        }
    }
}
