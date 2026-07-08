namespace FFMedia.Core.Processes;

/// <summary>Outcome of a finished child process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
