namespace FFMedia.Core.Updates;

/// <summary>UI-agnostic description of an available app update (Velopack details stay in the App layer).</summary>
public sealed record AppUpdateInfo(string TargetVersion);
