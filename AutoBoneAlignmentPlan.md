# PoseKit Auto Bone Alignment Plan

## 1. Goal

Add optional automatic alignment for paired character animations by measuring configured bones on two rendered actors and applying a render-only root offset to one actor.

The initial implementation should improve alignment without rewriting either animation or directly posing individual joints. It should build on PoseKit's existing `OffsetEngine`, remain local-only, and fall back cleanly to manual offsets whenever bone data is unavailable.

## 2. Initial Scope

### Included

- Select a local source actor and a nearby target actor.
- Discover readable bones on both actors.
- Configure one source bone and one target bone per alignment preset.
- Read both bone transforms while an animation is active.
- Convert both transforms into the same coordinate space.
- Calculate a root-level translation correction.
- Optionally calculate a yaw correction.
- Add a manual calibration offset after automatic alignment.
- Support one-time snap and continuous-follow modes.
- Smooth continuous corrections and constrain unsafe or implausible movement.
- Save alignment settings alongside an animation preset.
- Display clear status and failure reasons in the UI.

### Not Included in the First Version

- Directly rotating or translating individual bones.
- Full-body inverse kinematics.
- Automatic limb, hand, foot, or facial retargeting.
- Network synchronization between players.
- Modifying server-authoritative character positions.
- Assuming a particular body or skeleton mod is installed.
- Automatically inferring anatomical meaning from arbitrary bone names.

## 3. User Experience

The feature should appear as an optional section in the **Offsets** tab.

Suggested workflow:

1. Start the paired animation.
2. Choose the target actor from nearby rendered characters.
3. Choose the source bone on the local character.
4. Choose the matching target bone on the other character.
5. Click **Preview Alignment**.
6. Adjust the manual X/Y/Z and rotation calibration if necessary.
7. Choose **Snap Once** or **Follow Continuously**.
8. Save the configuration with the current preset.

The UI should always provide:

- Current source and target actor names.
- Selected bone names and whether they are currently resolved.
- Distance between the two alignment frames.
- Current automatic correction.
- Manual correction layered on top.
- A prominent **Stop Alignment / Reset** control.
- A warning when an actor, skeleton, or bone becomes unavailable.

## 4. Alignment Model

Each alignment preset needs two attachment frames rather than only two unqualified points:

- **Source frame:** a bone on the actor PoseKit will move.
- **Target frame:** a bone on the reference actor.
- **Source calibration:** local position and rotation relative to the source bone.
- **Target calibration:** local position and rotation relative to the target bone.

The calibration transforms are important because a bone origin is rarely the exact visual contact point and custom skeletons may orient similar bones differently.

Conceptually:

```text
source bone world transform × source calibration
                         ↓
                 source contact frame

target bone world transform × target calibration
                         ↓
                 target contact frame

target contact frame × inverse(source contact frame)
                         ↓
              desired actor-root correction
```

For the first version, extract only translation and yaw from the desired root correction. Pitch and roll should remain disabled until they can be applied safely to the actor root without destabilizing normal animation rendering.

## 5. Coordinate Spaces

Bone alignment will only work reliably if all transforms are converted explicitly.

Expected transform chain:

```text
bone local transform
    × parent bone transforms
    × partial-skeleton/model transform
    × actor draw transform
    = bone world transform
```

Before implementation, verify what the current FFXIV structures expose:

- Whether the pose buffer contains local-space or model-space transforms.
- How parent indices are represented.
- Which transform represents the partial skeleton relative to the character model.
- Whether Havok and render skeleton data are updated at different points in the frame.
- How actor scale, race scaling, and model transforms affect the final coordinates.

Create debug rendering or numeric diagnostics to validate the transform chain before applying any offsets.

## 6. Proposed Data Model

```csharp
public enum BoneAlignmentMode
{
    Disabled,
    SnapOnce,
    Continuous,
}

public sealed class BoneAttachment
{
    public string BoneName { get; set; } = "";
    public Vector3 LocalPosition { get; set; }
    public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
}

public sealed class BoneAlignmentConfig
{
    public BoneAttachment Source { get; set; } = new();
    public BoneAttachment Target { get; set; } = new();
    public BoneAlignmentMode Mode { get; set; }

    public bool AlignPosition { get; set; } = true;
    public bool AlignYaw { get; set; }
    public bool LockX { get; set; }
    public bool LockY { get; set; }
    public bool LockZ { get; set; }

    public float PositionSmoothing { get; set; } = 0.15f;
    public float RotationSmoothing { get; set; } = 0.15f;
    public float PositionDeadZone { get; set; } = 0.002f;
    public float MaximumCorrection { get; set; } = 2.0f;
}
```

Runtime actor identity must not depend only on an object-table index because indices may be reused. The active session can track address and object ID together, but saved presets should describe the target role rather than persist a temporary pointer or index.

Possible saved target roles:

- Manually selected each time.
- Current target.
- Focus target.
- Nearest player, only after explicit user confirmation.

## 7. Proposed Components

### `SkeletonReader`

Responsibilities:

- Validate that an actor has a human draw object and skeleton.
- Enumerate all relevant partial skeletons, not only partial skeleton zero.
- Build a bone-name-to-handle lookup.
- Read current bone transforms without retaining unsafe pointers across frames.
- Return structured errors instead of throwing when models redraw.

### `BoneTransformResolver`

Responsibilities:

- Resolve a bone handle to its current model/world transform.
- Compose parent transforms correctly.
- Apply actor scale and draw transform.
- Cache stable metadata such as bone names and parent indices.
- Invalidate caches on actor redraw, address change, or skeleton change.

### `ActorSelectionService`

Responsibilities:

- List eligible nearby rendered player characters.
- Track the explicitly selected target for the current session.
- Detect despawns, redraws, zoning, and object replacement.
- Never silently switch to a different actor when an object index is reused.

### `BoneAlignmentSolver`

Responsibilities:

- Calculate source and target contact frames.
- Produce the desired translation and yaw correction.
- Apply axis locks, dead zones, and maximum-distance constraints.
- Reject non-finite matrices, invalid quaternions, and implausible results.

### `BoneAlignmentController`

Responsibilities:

- Own the alignment state machine.
- Handle preview, snap, continuous follow, pause, and reset.
- Smooth corrections using frame-time-aware interpolation.
- Combine automatic correction with the existing manual `PoseOffset`.
- Disable itself safely when actors, bones, or animations become invalid.

### `BoneAlignmentPanel`

Responsibilities:

- Actor and bone selection.
- Searchable bone lists.
- Calibration controls.
- Live diagnostics and error messages.
- Preview, save, stop, and reset actions.

## 8. Integration with `OffsetEngine`

Avoid having manual offsets and bone alignment write independently to the draw object. `OffsetEngine` should remain the single owner of the final render offset.

Refactor its inputs into layers:

```text
game base draw offset
    + saved/manual pose offset
    + automatic bone-alignment offset
    = final draw offset
```

For yaw:

```text
game base draw rotation
    + saved/manual yaw
    + automatic alignment yaw
    = final draw rotation
```

Suggested additions:

```csharp
public PoseOffset ManualOffset { get; set; }
public PoseOffset AlignmentOffset { get; set; }
public PoseOffset EffectiveOffset => Combine(ManualOffset, AlignmentOffset);
```

Do not write to `GameObject.Position`. All corrections must continue through the existing render-only path.

## 9. Update Timing

Bone transforms and root offsets may be evaluated at different stages of the render/update cycle. Applying the correction from a stale pose can cause one-frame oscillation.

Research and test:

- When animated skeleton transforms become valid each frame.
- Whether `IFramework.Update` observes the current or previous animation pose.
- Whether a draw-stage hook is necessary for current-frame bone values.
- Whether changing root offset invalidates or recomputes the bone world transforms being measured.

Start with framework updates for simplicity. Move sampling or correction to a more appropriate hook only if profiling demonstrates visible lag or feedback oscillation.

## 10. Continuous-Follow Stability

Continuous alignment must not apply the full measured error blindly every frame. Moving the root also moves the source bone, which creates a feedback loop.

Required safeguards:

- Calculate corrections relative to the unmodified or previously known root transform where possible.
- Use delta time rather than a fixed per-frame interpolation factor.
- Add a small positional and angular dead zone.
- Clamp correction magnitude and per-frame velocity.
- Reset smoothing state after actor redraws or large teleports.
- Detect alternating corrections that indicate oscillation.
- Allow a low-frequency update mode if per-frame correction is visually noisy.
- Pause while skeleton data is incomplete during loading or redraw.

Prefer critically damped smoothing or exponential decay over averaging a fixed number of frames, because frame rate can vary.

## 11. Bone Compatibility

Custom genital or anatomy bones are mod-dependent. PoseKit should treat all bone names as user-configured data and make no promise that a particular name exists.

Compatibility strategy:

- Enumerate bones actually present on the selected actor.
- Search by full and partial name.
- Show which partial skeleton owns each bone.
- Store bone names, not raw indices or pointers.
- Re-resolve names whenever a skeleton changes.
- Allow per-body or per-skeleton alignment profiles later.
- Provide optional user-authored aliases only after real naming patterns are collected.

If duplicate bone names exist, store a stable descriptor containing at least the partial-skeleton index and bone name. If that index proves unstable across compatible skeleton variants, add skeleton identity plus a fallback name search.

## 12. Safety and Failure Behavior

Alignment should immediately stop and clear its automatic layer when:

- The local player or selected target disappears.
- Either draw object changes or becomes invalid.
- Either selected bone can no longer be resolved.
- A transform contains `NaN`, infinity, or a degenerate rotation.
- The required correction exceeds the configured maximum.
- The local character leaves the expected animation, if the alignment is preset-bound.
- The player changes territory or logs out.

Failure must leave the manual preset intact while setting `AlignmentOffset` to zero. The UI should display the reason rather than repeatedly logging the same error every frame.

## 13. Privacy and Scope

- The system is visual and local to the client running PoseKit.
- It should not transmit actor, pose, or bone data.
- It should not imply that another player sees the same alignment.
- Actor selection should be explicit and visible.
- Debug logs should avoid recording character names unless verbose diagnostics are deliberately enabled.

## 14. Implementation Phases

### Phase 0: Structure Research

- Identify the current FFXIVClientStructs types for human skeletons, partial skeletons, bone names, parent indices, and pose transforms.
- Document pointer lifetimes and redraw invalidation behavior.
- Confirm the space and update timing of exposed transforms.
- Build a temporary developer-only skeleton diagnostic.

Exit criteria: PoseKit can print or display a chosen bone's stable model/world position while an animation plays.

### Phase 1: Bone Inspector

- Implement safe actor skeleton validation.
- Enumerate bones across partial skeletons.
- Add source and target actor selection.
- Add searchable bone selectors.
- Display live transforms and distance without moving either actor.

Exit criteria: two bones on two actors can be selected and their world-space relationship remains plausible throughout an animation loop.

### Phase 2: Translation Snap

- Implement contact-point calibration.
- Solve translation only.
- Apply one render-only correction through `OffsetEngine`.
- Add maximum-distance validation and reset behavior.

Exit criteria: **Snap Once** brings configured contact points together and manual Reset restores the original draw offset.

### Phase 3: Continuous Translation

- Add continuous measurement and correction.
- Add dead zone, smoothing, velocity limits, and invalidation.
- Test for oscillation and one-frame lag.

Exit criteria: alignment follows a looping animation without visible rapid jitter under normal frame-rate variation.

### Phase 4: Orientation

- Add calibrated attachment rotations.
- Solve yaw only.
- Handle 180-degree ambiguity and quaternion normalization.
- Add yaw smoothing and angular limits.

Exit criteria: compatible attachment frames align direction without sudden flips.

### Phase 5: Preset Integration

- Extend `NamedPose` with an optional `BoneAlignmentConfig`.
- Add configuration migration/default handling.
- Restore alignment settings when replaying a preset.
- Require target selection if the preset has no safe resolvable target role.

Exit criteria: older presets continue to load, while new presets can restore their bone and calibration settings.

### Phase 6: Polish and Compatibility

- Improve bone search and duplicate-name display.
- Add diagnostics export without unsafe pointer data.
- Test popular skeleton/body configurations using user-supplied bone mappings.
- Add tooltips explaining local-only behavior and failure states.

Exit criteria: missing or different custom skeletons fail clearly and never crash or move the wrong actor.

## 15. Test Matrix

### Actor Lifecycle

- Local player login/logout.
- Target entering and leaving object range.
- Territory changes.
- Penumbra redraw of either actor.
- Gear/body mod changes during alignment.
- Reuse of an object-table index by another actor.

### Skeletons

- Vanilla skeleton with no requested custom bone.
- Custom bone present on the base partial skeleton.
- Custom bone present on another partial skeleton.
- Duplicate bone names.
- Skeleton swap while the UI is open.
- Different races, clans, sexes, heights, and model scales.

### Animation and Solver

- Static pose.
- Looping paired animation.
- Source and target moving in opposite phases.
- Animation restart and manual emote resync.
- Contact frames initially far apart.
- 180-degree yaw difference.
- Low, variable, and high frame rates.
- Snap followed by continuous mode.
- Manual offset adjusted while automatic alignment is active.

### Failure Cases

- Null draw object.
- Bone lookup failure.
- Invalid matrix or quaternion.
- Correction over maximum distance.
- Rotation hook unavailable.
- `OffsetEngine` position hook unavailable.

## 16. Performance Requirements

- Do not enumerate or allocate a full bone list every frame.
- Cache bone metadata per live skeleton identity.
- Resolve only the two active transforms during alignment.
- Avoid LINQ and per-frame strings in the hot path.
- Rate-limit repeated warnings.
- Measure update cost with continuous alignment active.

Initial performance target: the active solver should perform constant work per frame after metadata resolution and produce no routine managed allocations.

## 17. Open Research Questions

- Which FFXIV structure provides the final animated pose most safely?
- Are custom bone names retained and accessible at runtime for every modded skeleton format?
- How are bone transforms distributed across partial skeletons?
- Is the actor draw transform available in a form that includes all race and model scaling?
- Can the source world transform be measured before PoseKit's own root correction to avoid feedback?
- Does continuous root correction need a draw-stage hook to prevent a one-frame delay?
- Is yaw-only sufficient for the majority of paired animations?
- What stable skeleton identity can be used for cache invalidation and compatibility profiles?

These questions should be answered during Phase 0 before committing preset formats or user-facing compatibility guarantees.

## 18. Definition of Done for Version 1

Version 1 is complete when a user can:

- Select another rendered player explicitly.
- Select one existing bone on each character.
- Calibrate the desired contact point.
- Snap or continuously align the local character using render-only translation.
- Optionally align yaw when the rotation hook is available.
- Combine automatic and manual offsets predictably.
- Save and replay the configuration with a pose preset.
- Stop or reset alignment immediately.
- Receive a clear, non-destructive error when actors or bones become unavailable.

Full IK and direct bone manipulation should remain a separate future project unless root alignment proves insufficient for a well-defined set of animations.
