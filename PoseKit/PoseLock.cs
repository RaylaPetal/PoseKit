using System;
using Dalamud.Game.ClientState.Conditions;
using PoseKit.Camera;

namespace PoseKit;

/// <summary>
/// Holds the character still while a pose PoseKit played is going, until the player unlocks it or the
/// pose ends. Unlocking frees movement only; the next movement key ends the pose as usual.
/// </summary>
public sealed class PoseLock : IDisposable
{
    private const string Owner = "pose-lock";

    /// A pose change this soon after a PoseKit play is that play landing (cpose steps, seat placement),
    /// not the player taking over. Same window as the play context's freshness.
    private const long PlayGraceMs = 8000;

    private readonly Configuration configuration;
    private readonly MovementSuppression suppression;
    private readonly PoseTrigger poseTrigger;
    private readonly FreeCamService freeCam;

    /// Set by a PoseKit play until the pose is seen (then locked) or the grace runs out.
    private long? armedAt;
    private long lastPlayAt = long.MinValue;
    /// The pose being held; null while a standing idle is held.
    private PoseIdentifier? lockedPose;
    private bool hinted;

    public bool IsLocked { get; private set; }

    public PoseLock(Configuration configuration, MovementSuppression suppression, PoseTrigger poseTrigger, FreeCamService freeCam)
    {
        this.configuration = configuration;
        this.suppression = suppression;
        this.poseTrigger = poseTrigger;
        this.freeCam = freeCam;
        poseTrigger.Played += OnPlayed;
        Plugin.Condition.ConditionChange += OnConditionChange;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        Plugin.ClientState.Logout += OnLogout;
    }

    private void OnPlayed()
    {
        lastPlayAt = Environment.TickCount64;
        if (configuration.LockMovementWhilePosing && !Plugin.Condition[ConditionFlag.InCombat])
            armedAt = lastPlayAt;
    }

    /// Releases the lock without ending the pose. False when nothing was locked.
    public bool Unlock()
    {
        var wasLocked = IsLocked;
        Release();
        return wasLocked;
    }

    /// Call after PoseTrigger.Tick, with the pose read at the start of this frame.
    public void Tick(PoseIdentifier? currentPose)
    {
        if (!configuration.LockMovementWhilePosing)
        {
            Release();
            return;
        }

        var now = Environment.TickCount64;
        var inPose = currentPose != null || poseTrigger.HoldsIdle;

        if (!IsLocked)
        {
            if (armedAt is not { } armed) return;
            if (inPose && !poseTrigger.IsCycling) Engage(currentPose);
            else if (!poseTrigger.IsCycling && now - armed > PlayGraceMs) armedAt = null;
            return;
        }

        // Never strand the player: any sign the PoseKit pose is over lets them move again.
        if (!inPose && !poseTrigger.IsCycling)
        {
            Release();
            return;
        }

        if (currentPose != lockedPose && !poseTrigger.IsCycling)
        {
            if (now - lastPlayAt < PlayGraceMs) lockedPose = currentPose;
            else if (currentPose != null)
            {
                // The player typed a pose of their own over PoseKit's.
                Release();
                return;
            }
        }

        if (!hinted && !freeCam.Enabled && !FreeCamService.NavigationPaused() && suppression.IsMovementKeyDown())
        {
            hinted = true;
            Plugin.ChatGui.Print("[PoseKit] Movement is locked so your pose doesn't end. " +
                                 "Press Unlock in PoseKit or type /posekit unlock, then move to end the pose.");
        }
    }

    private void Engage(PoseIdentifier? currentPose)
    {
        armedAt = null;
        if (!suppression.Acquire(Owner)) return;
        IsLocked = true;
        lockedPose = currentPose;
        hinted = false;
    }

    private void Release()
    {
        armedAt = null;
        lockedPose = null;
        if (!IsLocked) return;
        IsLocked = false;
        suppression.Release(Owner);
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag == ConditionFlag.InCombat && value) Release();
    }

    private void OnTerritoryChanged(uint _) => Release();
    private void OnLogout(int _, int __) => Release();

    public void Dispose()
    {
        poseTrigger.Played -= OnPlayed;
        Plugin.Condition.ConditionChange -= OnConditionChange;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Plugin.ClientState.Logout -= OnLogout;
        Release();
    }
}
