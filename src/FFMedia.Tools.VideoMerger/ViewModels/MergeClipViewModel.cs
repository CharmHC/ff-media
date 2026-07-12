using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using FFMedia.Tools.VideoMerger.Models;
using FFMedia.Tools.VideoMerger.Services;

namespace FFMedia.Tools.VideoMerger.ViewModels;

/// <summary>One row of the clip list: what the file is, whether it must be re-encoded to reach the
/// target, whether the user has pinned it to a position, and how far its encode has got.</summary>
public partial class MergeClipViewModel : ObservableObject
{
    private Conformance? _conformance;

    public MergeClipViewModel(MergeClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Clip = clip;
    }

    public MergeClip Clip { get; }

    public string SourcePath => Clip.SourcePath;

    public string FileName => Path.GetFileName(Clip.SourcePath);

    /// <summary>What the probe found: "1280x720 · 60 fps · vp9 · 0:32".</summary>
    /// <remarks>Invariant culture throughout. A German machine would otherwise render 59.94 fps as
    /// "59,94" — and these are technical stream parameters, not localized prose.</remarks>
    public string Details
    {
        get
        {
            var length = Clock(Clip.Info.Duration);
            var video = Clip.Info.Video;

            return video is null
                ? $"no video track · {length}"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{video.Width}x{video.Height} · {video.FrameRate.Value:0.###} fps · {video.CodecName} · {length}");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private double _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinTooltip))]
    private bool _isLocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinTooltip))]
    private int? _lockedIndex;

    /// <summary>What the pin toggle says it does — and it has to SAY it. This was a checkbox, which in
    /// every list UI on earth means "include this row"; the user reasonably read it as "merge this
    /// clip" when it actually only exempts the row from Shuffle. The control is a pin now, and the
    /// tooltip states the one thing it affects.</summary>
    public string PinTooltip => IsLocked && LockedIndex is int index
        ? $"Pinned to position {index + 1} — Shuffle will leave it there. Click to unpin."
        : "Pin this clip to its position so Shuffle won't move it. It is merged either way.";

    /// <summary>Releasing the lock must release the index with it. <see cref="Ordering.Shuffle"/>
    /// pins any row whose selector returns non-null, so a stale index on an unlocked row would keep
    /// it frozen in place while the user wonders why "shuffle" is ignoring it.</summary>
    partial void OnIsLockedChanged(bool value)
    {
        if (!value)
        {
            LockedIndex = null;
        }
    }

    public bool IsConforming => _conformance?.IsConforming ?? false;

    /// <summary>Empty until a target has been applied — the row makes no claim it cannot back up.</summary>
    public string Badge => _conformance is null
        ? ""
        : _conformance.IsConforming ? "Conforms" : "Re-encode";

    public string BadgeTooltip => _conformance is null
        ? ""
        : string.Join(Environment.NewLine, _conformance.Mismatches);

    /// <summary>A bar only makes sense where there is work to watch. A clip that already conforms is
    /// never encoded, so a bar for it would sit permanently full and mean nothing.</summary>
    public bool ShowProgressBar => _conformance is not null && !_conformance.IsConforming;

    /// <summary>The row's state in words.</summary>
    /// <remarks>Deliberately NOT derived from <see cref="Percent"/>. The engine reports a conforming
    /// clip at 100 % from its very first progress report — correctly, since the number means "this
    /// clip's own re-encode work" and such a clip has none. Labelling that "Done" would tell the user
    /// the clip was finished before a single byte of output had been written.</remarks>
    public string ProgressText
    {
        get
        {
            if (_conformance is null)
            {
                return "";
            }

            if (_conformance.IsConforming)
            {
                return "No re-encode needed";
            }

            return Percent switch
            {
                <= 0 => "Waiting to re-encode",
                >= 100 => "Re-encoded",
                var p => string.Create(CultureInfo.InvariantCulture, $"Re-encoding… {(int)p}%"),
            };
        }
    }

    /// <summary>Re-runs the conformance check against a (possibly just-edited) target. Cheap and
    /// synchronous: the probe is already in hand, so nothing is re-read from disk.</summary>
    public void ApplyTarget(MergeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        _conformance = ConformanceCheck.Evaluate(Clip.Info, target);

        OnPropertyChanged(nameof(IsConforming));
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(BadgeTooltip));
        OnPropertyChanged(nameof(ShowProgressBar));
        OnPropertyChanged(nameof(ProgressText));
    }

    /// <summary>Pins this row to <paramref name="index"/>, or releases it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A locked index below zero.</exception>
    public void SetLock(bool locked, int index)
    {
        if (locked)
        {
            // Validate before mutating: a rejected lock must leave the row exactly as it was.
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            IsLocked = true;
            LockedIndex = index;
            return;
        }

        IsLocked = false; // OnIsLockedChanged clears the index
    }

    /// <summary>m:ss, or h:mm:ss once past an hour. A duration, not a time of day — invariant.</summary>
    private static string Clock(TimeSpan span)
        => span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}
