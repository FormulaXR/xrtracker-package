# Changelog

All notable changes to this package are recorded here.
This file follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
the package uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-10-21

This entry holds every change since 1.0.3, the last public release. It changes how the tracker decides that it has
found an object and when it lets go of it, it adds automatic recovery after a loss, and it adds macOS. Public API was
removed and changed, so read **Breaking changes** before updating a project that is on 1.0.3.

### Breaking changes

This section comes first so that it stands out. It lists every public member that a project on 1.0.3 can lose or see
change, with what to use instead.

| Member | What changed and what to use instead |
|--------|--------------------------------------|
| `TrackedBody.TrackingStatus` and the `TrackingStatus` enum | They are removed. Read `TrackedBody.Confidence` (`Tracked`, `Degraded` or `Lost`) with `ConfidenceReason`, and read `IsTracking` for the switch of the tracking pipeline. |
| `TrackedBody.NiceQualityThreshold`, `UseCustomNiceQuality` and `CustomNiceQualityThreshold` | They are removed. Read `Confidence`, or compare `TrackingQuality` with `QualityToStartTracking` and `QualityToStopTracking`. |
| `TrackedBody.EdgeTrackingQuality` and `InstantEdgeQuality` | They are removed. Read `TrackingQuality` and `InstantQuality`, which read the score of the method the body tracks by. |
| `TrackedBody.EdgeCoverageAverage`, `EdgeResidualAverage`, `ProjectionErrorAverage` and `LastStatus` | They are hidden from the public surface and have no replacement. |
| `TrackedBody.EdgeResidualQuality` and `EdgeProjectionQuality` | They are internal now. Read `TrackingQuality`. |
| `TrackedBody.Visibility` | It is renamed `SilhouetteVisibility`. It is the share of valid silhouette lines, so it reads zero for a body that tracks by the edge method. |
| The serialized events `_onBecomeValid` and `_onBecomeInvalid` of `TrackedBody` | They are removed. Nothing read them. A scene that stores them keeps working, and the stored lines are ignored. |
| `EdgeTrackingSettings.ProbeSearchRadiusScale`, `ProbeMaxProjectionErrorDeg`, `ProbeMaxResidualPx` and `UsePerSiteThreshold` | They are internal now and no longer serialized. The per-site contrast threshold is always on, and the Per-Site Contrast Threshold row of the inspector is gone. |
| `TrackedBody.ResetTracking`, `TrackedBodyManager.ResetAll` and `TrackedBodyManager.ResetBody(TrackedBody)` | Each now exists in two versions, the parameterless version that 1.0.3 had and a version with the new `forgetLearned` parameter, so an event wired in the inspector to the old method keeps binding. A reset now also returns the body to the pose it was placed at (see **Changed**). |
| The keyframe defaults of `EdgeTrackingSettings` | The rotation limit is 20 degrees where it was 3, and the translation limit stays 0.03 m. A scene or prefab keeps the values it stored, except that a rotation limit below 10 degrees is read as 10 (see **Changed**), so only a body that has never set a rotation limit takes the new default. |
| `TrackedBodyManager.TryGetBodyFusionInfo`, `BodyFusionInfo` and `BodyFusionState` | They are removed, and `BodyFusionInfo.InAnchorSpace` goes with them. Read `Confidence`, `ConfidenceReason` and `TrackingQuality` of the body. |
| `TrackedBodyManager.GlobalHistogramPeak`, `BodiesInARMode`, `IsBodyInARMode` and `ManagedBodyCount` | They are hidden from the public surface and have no replacement. `IsARPoseFusionActive` says whether pose fusion is in play. |
| `XRTrackerManager.UseAnchor` | It is removed. Pose fusion holds the pose in the AR session's world frame, so no anchor setting is left. |
| `XRTrackerManager.AfterTrackingStep()` and `BeforeTrackingStep()` | `AfterTrackingStep()` is removed, and `BeforeTrackingStep()` returns `bool` where it returned `void`. A custom camera feeder that calls either has to be updated. |
| The AR Tracker entry of the GameObject menu | It no longer adds an `ARAnchorManager`, an `ARPlaneManager` or an `ARRaycastManager`. A project that used the plane or raycast manager for its own placement code adds them itself. |
| `FTBridge.FT_SetBodyPose`, `FT_SetDetectorPose`, `FT_SetOccluderPose` and `FT_SetCameraWorldPose` | The first three return `int` where they returned `bool`, and `FT_SetCameraWorldPose` returns `int` where it returned `void`. A caller that uses the plugin directly has to read the result. |
| The `FTBodyConfig` fields `tikhonov_rotation` and `tikhonov_translation` | They are renamed `rotation_regularization` and `translation_regularization` with the same layout. The parameter names of `FTBridge.FT_SetStabilityParameters` changed too, which only breaks a caller that passes named arguments. |
| `FTHistogramCompareMethod.Bhattacharyya` | It is renamed `HistogramOverlap` with the same value (3), so a saved validation zone keeps its metric. |
| `SmoothingSettings` with its ten serialized fields, `SmoothingMode`, `PoseKalmanFilter`, `VelocityKalmanFilter` and the editor class `SmoothingSettingsDrawer` | They are removed. No component declared a field of that type and the pose filter was never constructed, so none of those settings ever changed a displayed pose. Output smoothing is, and always was, `TrackedBody.SmoothTime`. A script that names one of these types no longer compiles, and there is no behaviour to migrate. |
| `TrackingDiagnosticsPanel` | It is removed. A scene that holds it shows a missing script, and the component can be deleted. |
| Other diagnostic and development members | The per-frame evidence and health read-backs on `TrackedBody` and `TrackedBodyManager`, the development switches of the manager and the edge diagnostics are internal now. None of them is needed to track an object. |
| The other calls of `FTBridge` and every call of `ValidationBridge` | They are internal now. The four pose calls above, `FT_SetStabilityParameters` and the recording calls `FT_StartRecording`, `FT_StopRecording`, `FT_IsRecording` and `FT_GetRecordedFrameCount` stay public. Use `XRTrackerManager`, `TrackedBody`, `OccluderBody` and `ValidationZone` instead, which call the plugin at the right moment of the tracking step. |
| `TrackedBodyUtils`, `MeshCombiner` with `CombinedMeshData`, `ConversionUtils`, `SilhouetteModelGeneratorCore`, `TrackedBody.BeforeTrackingStep()` and `TrackedBody.AfterTrackingStep()` | They are internal now. The tracker and the editor tools call them, and nothing in them is needed to track an object. The `FTBridge` structs that only the internal calls use (`FTBodyStatus` and `FTCorrespondenceLine` among them) are internal too. |

### Added

- **macOS support** (Apple silicon, macOS 14 or later), as a native plugin bundle next to the Windows, iOS and Android
  plugins.
- **Depth tracking on Android (experimental).** The AR Foundation camera feeder passes the depth image of an Android device that has one
  to the tracker, as it already did for LiDAR on iOS, so **Depth Tracking** on a body now takes effect on Android. It needs
  an `AROcclusionManager` on the AR Camera. Android gives depth only once the camera has moved a little, so when a body in
  the scene uses depth the feeder waits at start for the first depth image, for up to **Depth Wait Seconds** (3 by
  default), and tracking starts without depth when none arrives. The Android depth image covers a centred crop of the
  colour image that matches the camera texture, and the feeder logs the crop it uses once at start. Android depth is
  experimental in 2.0.
- **Automatic recovery after a loss.** A body keeps the views it was tracked well in, and after a loss it tries those
  views against the current camera image and starts again from the one that fits. A candidate becomes tracking only
  through the ordinary start, so a recovery can never display a pose the tracker has not verified. The number of kept
  views is the **Saved View Recovery Capacity** setting on the Tracked Body Manager (8 by default, and 0 turns recovery
  off).
- **Recovery without AR pose fusion.** On desktop and in any setup without pose fusion, a lost body also tries the
  camera-relative poses it was accepted at before, through the same verified start.
- **Tracking confidence.** `TrackedBody.Confidence` reports `Tracked`, `Degraded` or `Lost`, with a reason in
  `ConfidenceReason`, and `OnConfidenceChanged` fires when they change. It reads the same score and the same start,
  confirm and stop bars that decide `IsTracking`. `Lost` is a report and the tracker does not reset itself, so what to
  do about a lost body is the decision of the application. `IsTracking` stays and is the switch of the tracking
  pipeline. It is true while the solver runs, including while a pose is held under pose fusion, and it is not a verdict
  on quality.
- **Recording and playback with AR data.** A recording now carries the camera extrinsics, and playing it back replays
  AR pose fusion as it ran live.
- **Accept Steady Fits.** A stationary body has a setting (`TrackedBody.AcceptSteadyFits`, on by default) that decides
  whether the tracker, under AR pose fusion, also accepts a fit that the strict registration check does not clear. It
  accepts such a fit when the coverage score clears the start bar and the fit stays close to where AR predicts the
  object. That helps soft or low-contrast real images keep the lock and read Tracked. A body that turns it off accepts
  only the fits that the strict check passes. The inspector shows the setting in the Edge Tracking section, for a body
  without a parent, and greys it while Stationary is off. It is read only by a body that tracks by the edge method.
  A scene that already saved the body keeps the value it saved, and a body that never saved the field reads on.
- **Each hold of a stationary body has its own toggle.** Accept Steady Fits switches on three holds of a stationary edge body under
  AR pose fusion. They are the view dip hold (the shown pose stays still while the view health stands below 0.8 of the usual level
  for the view), the fast motion hold (no new fit is stored while the object moves more than 6 pixels per frame on screen, counted for a
  camera with a focal length of 450 pixels) and the first accept guard (the first fit is taken only after 3 solves in a row agree within
  8 millimeters and 2 degrees). `TrackedBody.HoldDuringViewDip`, `HoldDuringFastMotion` and `FirstAcceptGuard`, on by default, switch
  each of them off on its own, and each one acts only while Accept Steady Fits is on. The inspector shows them as Hold During View Dips,
  Hold During Fast Motion and First Accept Guard, indented under Accept Steady Fits and greyed while it is off. The tracker reads them on
  every step, so a change applies at once. A scene that was saved before the toggles reads all three on, so it behaves as it did.
- **A recording played in the editor with pose fusion moves the camera along its recorded poses.** The tracker never
  moved a camera, so a replay drew the body through a camera that stayed where the scene had put it, and a body that
  was placed before the recording's world frame existed started where the recording never had it. When a recording
  with camera poses plays under pose fusion and the scene has no camera pose driver, the tracker now adds one to its own
  object. The driver puts the camera on the pose of every finished frame and, before the bodies are enabled, seats the
  scene on the first recorded pose. A scene that carries a driver already is left as it is, so the camera is never
  driven twice.
- **Reset Edge Settings to Defaults.** A context menu item on `TrackedBody` and a button in the Edge Tracking section
  of the inspector put every edge setting of the body back to the package default. The change can be undone, and it
  works on an instance of a prefab, where the values become overrides of the instance. The silhouette, score and
  confirm settings of the body are left as they are.
- **A confirm score on `TrackedBody`.** It is the score the last frame of a start must reach, and it can be set per
  body in the inspector for a body that tracks by the edge method and reads the coverage score. When none is set the
  confirm score is the start score, so a start needs the start score on five frames in a row and nothing more. A
  value at or below the start score withholds nothing. No default above the start score is used, because a confirm score
  above the start score delayed right starts and did not hold back wrong ones.
- **A whole-shape start search on `TrackedBody`, on by default.** With `SearchBeforeStart` on, which is the default, the plugin searches the
  whole camera image for the shape of an edge body without a parent before the body's first start. It scores a bank of
  prerendered outline views of the body (105 by default, which are 7 views in a cone of 5 degrees around the guide's view at
  5 degrees apart, three distances of 0.92, 1.0 and 1.09 times the guide's and the turns of -10, -5, 0, 5 and 10 degrees in the
  image plane),
  and it keeps the best hypothesis (the winner) with its score and its margin, which is the score of the winner divided by the
  score of the best hypothesis that stands far from it. It looks only at a window of the image, which is the box of the guide's
  outline grown by 96 pixels of the search image (its longer side is at most 640 pixels) and clipped to the image, and a
  template is placed only where its own outline box lies inside the window, so an object within about 90 pixels of the guide
  is found. A template may stand partly outside the picture, where the window reaches the edge of the image, as long as at
  least 0.6 of its points fall inside, and only the points inside the picture are scored, so an object that is larger than
  the picture is found too. The cone of 5 degrees is
  enough, because the tracker's own refinement takes the last few degrees from the winner. The cone half angle, the spacing, the range of the turns, the margin of the
  window and the number of distances are settings (`StartSearchConeHalfAngle`, `StartSearchConeSpacing`,
  `StartSearchRollRange`, `StartSearchWindowMargin` and `StartSearchDistanceCount`), and a cone of 10 degrees, a margin of 0
  (the whole image) and five distances give a larger search. The turns are zero and plus and minus each step of the
  spacing out to the range, so the zero turn is always held, whatever the spacing. `StartSearchScaleDistances` (off by
  default) makes the other distances by scaling the outline of one drawing of each view in place of drawing every distance. It
  draws a third of the views and starts a body a little sooner, but it can lose a start that drawing every distance
  keeps. A finer stage that scores the views within
  `StartSearchFineHalfAngle` of the winner at 2.5 degrees is off by default (a value of 0), because the winner of the bank
  already stands close enough for the tracker's own refinement to finish the fit. Each new
  winner is measured against the guide, as the median on-screen displacement of the model's points in pixels. A guide that
  stands within `StartSearchReachPixels` (16 by default) of the winner is in reach, and then the search changes nothing. The
  winner is not used, the guide stays the pose the tracker refines from, and the body starts exactly as it does without the
  search, on the same step. The same holds while no winner has been measured,
  which includes the steps during which the plugin is still drawing its views. A winner that stands farther is the pose the
  refinement starts from in place of the guide, and the body settles from it before it starts. The settle takes three steps
  in a row that move the model's outline by no more than 1 pixel (the median on-screen displacement of the model's points),
  and a refined pose that stands more than 16 pixels from the winner sends the refinement back to its seed and starts the
  count again, so the drift bound is measured from the winner and not from the guide. The path switches only
  on a latch. The far path is entered after two winners in a row stand farther than 1.25 times the reach (20 pixels at the
  default) and each reaches the margin bar and the score floor, so a weak winner leaves the body on its own start, and it is left for the guide in reach after two winners in a row stand within the reach, so a winner that hovers
  around the reach does not switch the path back and forth. On the far path the start is withheld until the same winner was
  found on three searches in a row (two winners are the same when the median on-screen displacement between them is at most 8
  pixels), and until the winner reaches a margin bar (`StartSearchMarginBar`, 1.4 by default) and a score floor
  (`StartSearchScoreFloor`, 0.6 by default). A value of 0 sets no bar or no floor, and a reach of 0 treats every guide as out
  of reach. A search made while the object is away from the picture reads a low margin and a low score, and a winner that
  stands on the object reads a high margin and a high score. The guide the
  person placed stays where it is and stays drawn until the start. `StartSearchPeriodSteps` (2 by default) says how many
  tracking steps pass between two searches. A search takes about 10 milliseconds on a desktop. The plugin draws the bank over
  as many steps as it takes (a budget of 40 milliseconds a step, which is about a quarter of a second for the default bank), and a
  guide that is out of reach is started from the winner only after that, which takes 10 to 13 steps in all (0.4 to 0.7 seconds
  on a desktop), while a guide in reach starts as it always did in the meantime, though its first steps take about 75
  milliseconds each while the bank is drawn.
  Under pose fusion the camera moves while the guide stays in the world, so when the object is seen from a side or a
  distance the views no longer cover (a turn of more than 12 degrees about the line of sight, or a change of more than 10
  percent in distance), the views are drawn again around the guide as the camera sees it then, and the body starts as it does
  without the search until the first winner after that. A body that was started from a winner is shown at the pose the tracker solved until a pose has
  been accepted, and not at the guide it was placed at, which can stand far from the object.
  `TrackedBody.TryGetStartSearchReading` returns what the latest search found and what the gate decided on it, and
  `TrackedBody.StartSearchRoute` and `TrackedBody.StartSearchGuidePixels` return which start the body takes and how far the
  guide stands from the latest winner. The search needs the plugin exports `FT_SetStartSearch`, `FT_GetStartSearchReading`
  and `FT_SetStartSearchSeat`. A plugin without them is detected, logged once, and the body keeps the start it always had. A
  plugin without `FT_SetStartSearchBank` keeps the bank it was built with, and a plugin without
  `FT_SetStartSearchRenderEachDistance` draws every distance.
  The search is on by default. A scene or prefab that saved the `SearchBeforeStart` field keeps its saved value, so only a
  body that has never stored it starts with the search on, and a body that must start as it did without the search sets
  the field off. The search proposes a pose and nothing more, so the verification stays with the existing start
  bars. It starts a body from a guide that stands well away from the object, but the
  pose it starts from can still stand a few degrees off, and the tracker does not always correct that, so a body can start
  with a wrong tilt or depth. Switch it off on a body that starts wrongly with it.
- **The guide of a body that starts from its viewpoint follows the camera until the first start.** Under AR pose fusion on a
  live camera, an edge body without a parent whose initial pose comes from its viewpoint keeps its guide at the authored
  pose in front of the camera on every tracking step until the body first starts, and the guide is drawn at that pose on
  every rendered frame, so the person lines the object up with an outline that moves with the device. A guide that the
  application moves takes up the new pose in front of the camera and follows from there. A recording keeps the body's own
  world transform, a body that starts from its scene position stays where it was placed, and after the first start the
  guide stays where the body started. With the start search on, the plugin looks for the object around the guide it was
  sent, which stays where it was in the world, so the views are drawn again around the guide only once that guide has
  fallen more than 32 pixels behind the guide on screen, measured as the median displacement of the model's points. A guide
  that does not follow the camera asks again after a turn of 12 degrees or a change of 10 percent in distance, as before.
- **`forgetLearned` on `TrackedBody.ResetTracking`, `TrackedBodyManager.ResetBody` and `TrackedBodyManager.ResetAll`.**
  With `false`, the default, the body keeps what it learned while it tracked, which is its saved-view bank, the poses it
  was accepted at and what its views normally achieve. With `true` it forgets all three and starts again with nothing
  but the pose it was placed at. The Tracked Body inspector shows a button for each, Reset and Reset and forget. Each of
  the three methods exists in two versions, one without parameters, which is the version 1.0.3 had, and one with the
  `forgetLearned` parameter. Because the parameterless versions exist, an event wired in the inspector to the old method
  keeps binding.
- **Manual attach for a part in assembly mode.** A part with Assembly Mode on can join its parent's solve on the
  application's word and not on its own score. With **Attach Manually** on, the part is scored but never joins the
  parent's solve until the application calls `TrackedBody.Attach()`, and it leaves the solve again when the application
  calls `Detach()`. `IsAttached` reads the last request. A reset of the body does not change it, because it says whether
  the part is mounted and not what the tracker holds. Such a part does not use Quality to Confirm, and it is taken out of
  the solve when it registers, so no tracking step includes it before the call. A part with Attach Manually off keeps the
  behaviour it had.
- **The free check for an assembly part.** With **Free Check** on, a rigid part with Attach Manually that is not attached
  settles its own pose a few millimetres and degrees around its seat on every solve step, from its own evidence only.
  The parent's solve never includes it, so the parent never moves for it, and the part is scored where it settled.
  `SettledDistance` and `SettledAngle` read how far it settled, and `IsInSeat` reads false once it settled further than
  `FreeCheckDistanceTolerance` (0.005 m by default) or `FreeCheckAngleTolerance` (3 degrees by default). Once attached,
  the part is rigid at its seat again and joins the parent's solve. It needs the plugin exports `FT_SetChildSettling` and
  `FT_GetChildSeatOffset`. A plugin without them is detected, logged once, and the part is checked at its seat as before.
  The free check is off by default.
- **A check view and a presence reading for an assembly part.** A part can carry a **Check View**, a transform like the
  start viewpoint, with a distance and an angle tolerance, and a stillness requirement over a short window.
  `TrackedBody.Presence` reads `PartPresence.Unknown`, `Present` or `Absent`. It judges the part only while the camera is
  near the check view and still, from the evidence of a whole hold (`PresenceHold`, 1 s by default), on which a step
  passes when `TrackingQuality` reaches the method's present bar and the part is in its seat. Away from the view, while
  the camera moves, or before a hold is complete, it reads `Unknown`. A part without a check view always reads
  `Unknown`, and `TrackingQuality` stays the per-frame score.
- **Standard deviations of the depth correspondences.** `DepthTrackingSettings.StandardDeviations` sets the measurement
  noise of the depth correspondences in meters, one value for each correspondence iteration from coarse to fine, and the
  last value is used for any later iteration. The defaults are 0.05, 0.03 and 0.02, which are the values the plugin used
  before they could be set, so nothing changes until someone sets them. They reach the tracker once the body has
  registered with depth, and a change while the scene plays reaches it at the next tracking step, together with the
  distance tolerance. That needs a plugin build with `FT_SetDepthParameters`. An older build reports it once and keeps the
  values the body registered with.
- **Hold Start Frame on the Sequence Player Feeder.** It repeats the start frame of the range on every step and loops on
  it, so the tracker refines the pose on that one image again and again until it settles. Every field the feeder had
  before keeps its name and its meaning, so a saved scene plays as it did.
- **Use Current Pose as Initial Viewpoint on the Tracked Body inspector.** In play mode, while a body that starts from a
  Viewpoint is tracking, the button keeps the pose the body is shown at, seen from the camera. When play mode ends, the
  Viewpoint is moved to the place from which the body starts at that pose, with an undo step, and the scene is marked as
  changed so that it can be saved.

### Changed

- **The Tracking Model Asset entry of the Create menu** is under XRTracker, where it was under FormulaTracker.
- **With AR, the first fit of a stationary body is accepted only after it has settled.** The tracker waits until the solve has stood
  still for three steps in a row (at most 30 steps after the tracking started), so a fit that is still moving is not locked in as the
  pose the body is held at. It follows the Accept Steady Fits setting of the body.
- **`TrackedBody.SearchBeforeStart` is on by default.** A body that has never stored the field, which includes every body
  added after this change, searches the whole camera image for its shape before its first start (see **Added**). A scene or
  prefab that saved the field keeps the value it saved. Set the field off on a body that must start as it did without the search.
- **The quality number and its bars read on a 0 to 1 scale that depends on the method.** `TrackingQuality` is the
  averaged score that the stop decision reads, and `InstantQuality` is the score of the current step, which the start
  decision reads on five frames in a row. A body that tracks by the edge method without pose fusion reads the share of
  sites that find a matching edge, divided by 0.70 and clipped to 1, with a start bar of 0.65, a confirm bar of 0.65
  (the start bar unless the body sets its own) and a stop bar of 0.50. Under pose fusion it reads the registration margin, mapped to 0 to 1 by a straight line through
  zero that sends the margin's start bar (0.988) to 0.65 and clipped to 1, with a start bar of 0.65, a confirm bar of
  0.65 and a stop bar of 0.558. Only the readout is mapped. Every decision except the start still reads the unmapped
  margin against its unmapped bars (the start of that body reads the coverage score, see below). A silhouette body reads the histogram score, with a start and confirm
  bar of 0.3 and a stop bar of 0.4. Compare the number with the bars (`QualityToStartTracking`, `QualityToConfirmStart` and
  `QualityToStopTracking`) and never with a fixed number.
- **`QualityToStartTracking` reports the start bar that is enforced.** An edge body configured with a start bar below
  its stop bar is raised to the stop bar, so it reads the stop bar. A silhouette body is not raised and starts on its
  own bar of 0.3, as it did in 1.0.3.
- **The keyframe camera spacing of a body is 0.03 m by default** (see **Breaking changes** for the rotation limit).
  A body that never set the spacing refreshes its edge points after 3 cm of camera travel, as it did in 1.0.3. The
  outline holds steadier at 3 cm than at 1 cm for most objects, though some objects hold better with a narrower
  spacing. A scene or prefab keeps the spacing it has serialized, and only a body that has never set one takes the default.
- **The rotation limit of the edge keyframe runs from 10 to 20 degrees.** A limit below 10 draws the edge points again on every small
  turn of the object, at whatever pose the tracker has reached, so a body that meets a hard stretch may not recover from it. A scene or prefab that saved a
  smaller limit now reads 10, and the saved value is left as it is. The translation limit is unchanged.
- While recovery tries a candidate, the body is drawn where it stood, and it moves only when tracking starts again.
- **Pose fusion no longer uses an AR anchor.** It holds the body's pose in the AR session's world frame and composes it
  with the AR camera pose each frame, so a scene no longer needs an `ARAnchorManager`, and a recording made earlier that
  carries an anchor history still plays back.
- **The world-frame coast is on by default.** A stationary object that leaves the camera's view is held in the world
  frame instead of being treated as a tracking failure. A scene that already sets the option keeps its value.
- **A body that tracks by the edge method, with no pose fusion in play, now starts, holds and recovers with the
  following settings and nothing has to be set.**
  - Coverage decides start, stop and recovery, with a start bar of 0.65 and a stop bar of 0.50. The coverage map now
    reads one at a coverage share of 0.70, where it read one at 0.55 before, so a right pose needs 45.5 percent of the
    sites to find an agreeing edge to clear the start bar.
  - The pose a body was placed at stays where it is while the body searches. Each search step refines a copy of it
    from the current frame and judges the copy, and the body starts from the copy only when it clears the start bar
    for five steps in a row.
  - A start needs the coverage score on five steps in a row, and the confirm score on the fifth when the body sets
    one above the start score, and it reads nothing else. There is no second gate on the registration margin, because on a
    real camera such a gate delayed the start of a right pose and did not hold back a wrong one. A frame with no matched site cannot start a body, because coverage
    reads zero.
  - A body that lost tracking keeps trying to recover for as long as the application lets it. It tries the poses it was
    accepted at, judges a candidate on its refined coverage score three steps after the candidate is seated, and
    carries its refinement from step to step from the pose it holds, for at most 60 steps and only while the refined
    pose stays within 24 pixels of the pose it holds. The kept-pose attempts are at least 5 steps apart.
    There is no automatic return to the pose the body was placed at. An application that decides recovery will not
    succeed calls `ResetTracking`, with `true` to forget what the body learned (the reset described above).
  - The plugin keeps the sites of the poses that recovery evaluates and puts them back when it evaluates the same pose
    again. A hit is exact, because the pose, the poses of every body the renderer draws, the camera and the plugin's
    configuration are all compared bit for bit, so no decision changes. A plugin build that does not offer this (the
    Android, iOS and macOS builds that were made before it) is asked once, one line is logged, and recovery goes on
    without the cache.
- **A body that tracks by the silhouette method keeps the start, the stop and the reset it had in 1.0.3, and a body under
  pose fusion keeps what it did.** The silhouette score has a start bar of 0.3 and a stop bar of 0.4, read as they are
  set. The body is judged at the pose it is placed at and is never refined before its start, its guide is neither
  walked nor replaced, tracking stops when the average score stays under the stop bar for five steps, and the stop
  returns the tracker to the guide. It has no recovery, no confirm score, no hold release and no
  world-frame coast, and the settings above do not reach it. A body under pose fusion keeps the registration margin and
  its bars for its acceptance, hold and give-up, and the settings above do not reach those decisions either.
- **The acceptance, hold and give-up decisions of an edge-tracked body under pose fusion read a registration score.**
  The score measures how well the model's edges line up with the image, so a pose that covers the object but sits in
  the wrong place no longer counts as found.
- **The start of an edge-tracked body under pose fusion reads the coverage score with the held pose.** It starts as a
  body without pose fusion does, on the coverage score at a start bar of 0.65 on five steps in a row, judged on the
  copy of the held pose that each step refines, with no confirm score above the start bar. The registration margin
  stays the score of its acceptance, hold and give-up. The margin's residual bar reads a
  right pose on a soft camera picture as a poor start, so on a phone camera the start came late or never and the body
  could stay off the object. The coverage start comes promptly and corrects the seat. A test or
  replay run can start the body on the margin again through its run override. `TrackingQuality` and `InstantQuality`
  still show the margin, so the new read-only `TrackedBody.StartScore` gives the score the start reads, on the same
  scale and against `QualityToStartTracking`. The Runtime Status of the Tracked Body Inspector shows the two scores as
  Start Score and Hold Confidence, each with its bar, under pose fusion, and one Tracking Quality row otherwise.
- A body that tracks by the edge method under pose fusion now re-checks the pose it holds against the image every few
  steps and takes the lock back only after five steps in a row that each clear the start bar and the registration margin
  gate.
- **A stationary object under AR pose fusion lets go of a solve that has left it.** The body is held at its last
  verified pose and takes the lock back only after the image confirms it again, so the augmentation no longer follows
  the tracker onto the background.
- **A stationary object under AR pose fusion no longer lets the tracker wander off while no solve is verified.** On a soft
  camera picture with clutter behind the object no solve reached the verification bar, so nothing ever put the tracker
  back and its solve left the object and drifted ever farther, even with an exact AR pose. When a solve that is not verified stands further from the pose the body is held at than the AR pose can
  explain (30 millimetres or 8 degrees, plus twice the distance and the angle the AR camera moved in the step), the
  tracker is put back at that pose on the same step. A verified solve is never touched by this, so the settled pose still
  catches up with a moved object as before.
- **A stationary object under AR pose fusion on a soft camera picture is accepted and reads Tracked.** On a soft picture
  the registration margin seldom reaches the bar that a solve has to clear to be accepted, so a body that had started
  stayed at the pose it was placed at, was given up on after a short time, and read Lost although the tracker was on the
  object. A solve that the margin does not clear is now also accepted when the coverage score clears the start bar on
  three steps in a row and the solve lies inside the prediction gate. The body goes on with that run for as long as the
  coverage score stays at or above the stop bar, as the start and the stop of a body do. An accepted solve of this kind
  keeps the lock in the give-up loop and lets the body read Tracked. It is stored as the pose the body is held at only
  when the margin has not accepted a solve for 30 steps, because on a picture where the margin does accept, the margin keeps
  the stored pose. Until the first solve is accepted, the prediction gate stands three times wider, since the pose the
  body is held at is then only the guide that was placed, and the guide can stand tens of millimetres from the object.
  A body turns this off with its Accept Steady Fits setting.
- **With AR, the coverage score also decides when an edge body accepts a fit and when it stops.** A stationary edge body under
  pose fusion now also accepts and stores a fit when the coverage score clears the start bar and the fit lies inside the
  prediction gate, on the same bars as a body without pose fusion, so the outline keeps correcting on soft or low-contrast
  pictures. It keeps its lock while the averaged coverage score stays at or above the stop bar or the registration margin
  still holds. A body that is not stationary reads Tracked and stops on the coverage score as it does without AR, where it
  kept stopping on the margin on a soft picture. A body without pose fusion behaves as before. A stationary body turns this off with its Accept Steady Fits setting.
- **With AR, a stationary object keeps its pose still while its view dips.** A stationary edge body stores no new fit while
  the share of its sites that agree stands below 0.8 of what its view normally reaches, for example while a hand covers part
  of the object, so the held pose cannot slide along the directions that the rest of the object leaves free. A dip begins
  only after 30 steps in a row at or above that share and ends after 5 steps at or above it. It follows the Accept Steady Fits
  setting of the body, and it does not reach a silhouette body, a body that is not stationary, a child or a replay without AR.
- **With AR, a stationary object keeps its pose still while the camera moves fast.** A stationary edge body stores no new fit on a step
  in which the AR camera moves the picture of the object by more than 6 pixels. The motion is the largest on-screen displacement of the
  centre and the corners of the bounds of the model (the corners drawn in to 0.15 m of the centre), counted in pixels of a camera whose
  focal length is 450 pixels. A fit from a blurred step is the least reliable kind of fit, and the pose the body is held at is
  carried by the AR camera until the camera slows down. The first fit and the fits of the first 30 steps after the start are never held
  back. It follows the Accept Steady Fits setting of the body, and it does not reach a silhouette body, a body that is not stationary, a
  child or a replay without AR.
- **The displayed pose of a stationary object is steadier.** It settles on the average of recent verified solves instead
  of following the frame-to-frame scatter of each one.
- **The displayed pose of a stationary object catches up at once when the object is moved.** The settled pose that the
  display holds takes in each verified solve at a pull of 0.05, which hides the scatter of the solve but made the display
  trail an object that a person had moved. The pull now depends on how far the new solve stands from the settled pose on
  screen, as the median displacement of the model's points in pixels. Up to 1.5 pixels (`STATIONARY_SETTLE_STILL_PIXELS`) it
  stays at 0.05, from 4 pixels (`STATIONARY_SETTLE_MOVE_PIXELS`) the settled pose is seated on the solve, and between the
  two the pull grows in proportion. For an object that never moves, the displayed outline is as steady as before and
  stands as close to the solve as before.
- **A body keeps one custom start and stop value for the edge method and another for the silhouette method.** Until
  now one custom value served both, so a stop of 0.50 set for an edge body kept a silhouette body that read 0.42 from
  starting. The four existing fields (`UseCustomStartThreshold`, `CustomQualityToStart`, `UseCustomStopThreshold` and
  `CustomQualityToStop`) are now the values of a body that tracks by the edge method, with the same names and the same
  serialized data, so every existing scene loads with the same values. Four new fields
  (`UseCustomSilhouetteStartThreshold`, `CustomSilhouetteQualityToStart`, `UseCustomSilhouetteStopThreshold` and
  `CustomSilhouetteQualityToStop`) are the values of a body that tracks by the silhouette method, and they start off.
  A silhouette body that was saved with a custom start or stop value keeps it, as in 1.0.3. The body copies the value
  to its own fields when it loads, and the copy reaches the file when the scene or prefab is saved again. An instance of
  a prefab that switches the value on, off or to another number keeps it as well. A body that tracks by the edge
  method is not changed. The Tracked Body inspector shows the pair of the method the body tracks by.
- A coverage map whose full-scale share is not above its floor reads zero, where it could read a perfect score for a
  coverage of zero.
- The Tracked Body inspector no longer shows the experimental edge rows and debug readings.
- `TrackedBody.ResetTracking()`, `TrackedBodyManager.ResetBody` and `TrackedBodyManager.ResetAll` now also return the
  body to the pose it was placed at, as well as stopping its tracking and starting its lifecycle over, and a body that
  has no placed pose kept stays where it is.
- **A body that tracks by the edge method decides on its edge score alone.** With depth tracking on, the start, hold and
  stop of an edge body read the larger of its edge score and its depth score, and the readout and the confidence followed.
  They read the edge score now, so depth only helps the fit. A silhouette body keeps the larger of its silhouette score and
  its depth score, because a body can have a depth model and no usable silhouette.

### Removed

- Every public member of 1.0.3 that is gone is listed, with what to use instead, under **Breaking changes**.
- The Force Start Tracking button at the bottom of the Runtime Status of the Tracked Body Inspector. The Force Start
  button of the Lifecycle row stays, beside Reset and Reset and forget, and `TrackedBody.ForceStartTracking()` is
  unchanged.
- **The Use Keyframe toggle of the edge settings is removed, and the edge keyframe is always on.** A scene or prefab that saved it
  off now tracks with it on. The edge points refresh after the translation or rotation threshold has passed or when too few of them
  still find an edge. The `EdgeTrackingSettings.UseKeyframe` property stays so that a script still compiles, but it is obsolete and its
  setter does nothing.

### Fixed

- **A stationary body under pose fusion no longer copies its mesh every frame.** The view check read the bounds of the
  model by copying every vertex of every mesh on each tracking step, which made garbage in proportion to the size of the
  model. The bounds are now computed once and again only when the geometry changes.
- **The camera background of the webcam setup draws in a built player.** It used a built-in shader that a player build
  leaves out unless something else uses it. The package now ships its own background shader.
- **The 64-bit Windows plugin is no longer included in Linux and 32-bit Windows builds.** The 32-bit Windows player uses
  the plugin in `Plugins/x86`.
- **The usage metrics report the package version 2.0.0**, where they reported 1.0.0.
- **A call that changes the tracker no longer runs in the middle of a tracking step.** The step runs on a worker thread,
  and the plugin had no lock around the state it reads. A call made on the main thread while a step was in flight could
  change that state under it, and on Android one such overlap crashed a device inside the colour histograms of a
  silhouette body. The plugin now holds a lock around the step and around every call that changes the tracker, so such
  a call waits for the step to return, which lasts at most one step and happens only when a step is in flight. The
  package also waits before those calls itself, among them the public body calls (`UpdateEdgeTrackingParameters`,
  `UpdateGeometry`, `AddOccluder`, `SetAttachmentPose`, `ForceStartTracking` and `ResetTracking`), the occlusion and
  detection set up after a body registers, `OccluderBody` registration, the sequence player controls and starting or
  stopping a recording. The reads of the Correspondence Visualizer and the checks of a Validation Zone wait for a step
  in flight in the same way. An `OccluderBody` writes its pose right before each step instead of in `LateUpdate`, where
  it overlapped the step every frame. A colour histogram that is not set up yet now reads as no colour evidence instead
  of crashing.
- **Depth lines up with the colour image in every screen orientation.** The plugin turns the colour image to the screen
  before tracking and fed the depth image as the sensor gives it, so with the device in portrait or in landscape right the
  depth points were measured a quarter or a half turn away from the image. The depth camera is now turned about its
  optical axis whenever the screen rotation changes, with the plugin call that sets its pose.
- **A frame without depth no longer reuses old depth.** The plugin keeps the last depth image it was given, so a frame
  whose depth image was missing was tracked against depth taken from an earlier camera pose. The feeder now sends an empty
  depth image for such a frame, and zero reads as no measurement.
- **An edge setting changed on an instance of a prefab in a scene is now applied when the scene loads.** In every 1.x release the
  scene ignored such a change when it loaded, for example a Depth Edge Threshold of 0.1 set on an instance of a prefab that holds 0.01,
  and the tracker registered with the value of the prefab. The change is applied now, so a scene that carried such a change tracks
  with it after the upgrade.
- **An edge setting changed while the scene plays is no longer silently ignored.** The tracker reads the edge settings when a
  body registers, and `UpdateEdgeTrackingParameters` (which the inspector calls on every edit during Play) sent only the
  silhouette settings. A new Depth Edge Threshold typed in during Play therefore never reached the tracker, which went on
  with the value it registered with. The depth edge threshold, the search radius, the minimum gradient, the sample step and
  the crease edges now reach the tracker at its next step (this needs a plugin build with `FT_SetEdgeParameters`, and an
  older build reports it and keeps the registered values). Every other edge setting is read when the body registers, so it
  takes effect after the component is disabled and enabled again, and a warning in the console says so. The console also
  states the edge settings each body registered with, and the Edge Tracking section shows them while the scene plays.
- **A child is a part of its parent, never a body of its own.** A part that is set to move along only some directions of its
  parent, for example translation x and z, used to run the lifecycle of a body of its own. It started, stopped and
  recovered by itself, it could be tracked while its parent was not, and it was drawn at its own solve. The tracker refines a
  child inside the solve of its parent only, so the start of a child meant nothing to the tracker and only set a flag. A child
  is now tracked exactly while its parent is tracked, it has no start, stop, re-entry or recovery of its own, and it cannot be
  started by hand. A part in assembly mode is only checked, at the pose its parent gives it, until the application attaches it or
  its averaged score reaches its confirm score, and it never pulls the parent before that.
- **A child that moves along only some directions of its parent stays on those directions.** The part was drawn at its own
  solve while its parent was drawn at the pose it is held and settled at, or at its placed pose before it has started, so the
  part turned and moved in every direction against a parent that it may only slide along. The part is now drawn at the pose
  its parent is drawn at, moved by the pose the tracker solved for it relative to the parent. While the parent is not
  tracked, the part stays where its parent carries it, at its last pose relative to the parent. When the tracker is given
  a stationary parent's pose back, the poses of its children are given back with it, each where it stood relative to the
  parent. A child that slides along x and z no longer turns against its parent or moves along y, and only x and z
  change. The lifecycle of the parent does not change.
- **A second edge outline no longer changes how the first one draws.** An edge outline now draws with a material that
  matches its own `ShowInternalEdges`, so the setting of one outline no longer decides how another is drawn.
- **A body that is not stationary no longer walks its guide onto whatever fits before its first start under AR pose
  fusion.** Before its first start, an edge body without a parent now stays drawn at the guide the person placed, whether
  or not it is stationary, as a stationary body and a body without pose fusion already did. The pose that the tracker
  refined from the guide was drawn and then taken up as the next guide, so the guide moved a little further on every step
  onto whatever fitted, and the body started there, far from the object. A guide that stands far from the object now does
  not start a body that is not stationary, like a stationary body, and a guide that stands close to the object starts on
  the object for both. A body that was lost
  draws what it did before, and a body that tracks by the silhouette method is not touched.
- **A body tracked by its silhouette alone starts tracking.** It previously never reached the start threshold.
- **A frame is dropped when the tracker refuses a required write.** A refused write previously let a tracking step run
  on a new image with the previous frame's camera pose.
- **Display smoothing advances once per rendered frame.** It previously advanced a second time on any frame in which a
  tracking step completed, so the effective time constant depended on the tracker's throughput. `SmoothTime` now means
  what its tooltip says.
- **Display smoothing runs on the object's pose in the AR session's frame**, and the composition to world happens
  after it. It previously ran on the composed world pose and had to snap whenever the session moved its anchor.
- **`OnStopTracking` fires once on a tracking loss caused by low quality.** It previously fired twice.
- **A tracking session no longer starts against a mismatched native plugin.** The package checks the native status and
  configuration layout before it initializes, and refuses to start with an error that names both sizes.
- **A rotation written to the tracker is read as the rotation it names.** A rotation whose length is not one
  previously entered the tracker as a rotation combined with a scale.
- **A child body with free motion in assembly mode waits for its confirmation.** A child with any tracked motion set
  previously skipped the assembly check and joined its parent's solve from the start, so a part that was not there
  pulled the parent off. It now stays out until its score confirms it, or until `Attach` with manual attach on.
- **Pose fusion is ignored for a recording without a usable camera pose.** A desktop recording writes its camera pose
  file with every row marked invalid. Replaying it with pose fusion on previously rejected every frame with an error, and
  now fusion is ignored for it with one log line, as it already was for a recording without the file.
- **The depth camera follows the AR camera.** Under pose fusion the plugin moved the colour camera with each AR pose but
  left its depth camera where it started, so the depth correspondences were measured from a camera that no longer stood
  where the real one did. The depth camera now keeps its pose in the colour camera and moves with it, and a recording made
  while it follows stores its pose in the colour camera and not in the world. This needs the plugin build that carries the
  change.
- **Generating a depth model no longer overloads the graphics card.** The generator drew with as many threads as the
  computer has cores and created two graphics devices for each thread, which could reset the display driver on a large
  model. It draws on one device with at most four threads now, and writes the same file as before.
- **Generating a silhouette model no longer overloads the graphics card.** The generator drew with as many threads as the
  computer has cores and created a graphics device for every renderer it set up, which could reset the display driver on a
  large model. It draws on one device with at most four threads now, as the depth model generator does, and writes the same
  file as before.
- **The Android, iOS and macOS plugins are rebuilt from the current source, and a Windows x86 plugin is added.** The
  Android (arm64-v8a), iOS (arm64) and macOS (Apple silicon) plugins had been built before the live edge and depth
  setting calls (`FT_SetEdgeParameters` and `FT_SetDepthParameters`), the child guard, the depth camera that follows
  the AR camera and the single device model generators, so those changes did not reach those platforms. The rebuilt
  plugins carry all of them and the tracker lock described above, and every plugin, the Windows x86_64 one included,
  exports the same 162 `FT_` functions. The new plugin in
  `Plugins/x86` is the same source built for 32-bit Windows standalone players, and it is never loaded by the editor.
- **The camera image of the Sequence Alignment window lines up with the Scene view on a display with scaling above 100
  percent.** It was drawn too large there, because the window sized it from the camera size in pixels while the Scene
  view draws in points.
- **A sequence that loops no longer stops tracking at the end of its range.** Under pose fusion, the loop started the
  range again and the recorded time went back to the first frame, which the completed-frame clock refused as a fault. The
  console showed "Completed-frame clock rejected the step" and tracking stopped for the rest of the play. A range of one
  frame was refused in the same way, because its recorded time never advanced. The tracker now treats the start of every
  pass of the loop as it treats a seek to the start of the range, so the clock starts the pass fresh, the step is
  accepted and tracking goes on. A recorded time that goes back inside one pass is still refused.
- **An AR session that moves its world no longer leaves the tracked bodies behind.** After a relocalization the camera pose of
  the session can jump by far more than a hand can cover in one frame, while the session still reports tracking. A body under
  pose fusion holds its pose in the world of the session, so the held pose stayed in the old world, projected behind the
  camera and read `Degraded` without an outline for the rest of the session, with the object in plain view. The tracker now
  compares the camera pose of every step with the step before it. A step counts as a jump when the camera moved by more than
  0.25 meters or 5 meters per second, or turned by more than 45 degrees or 900 degrees per second, whichever limit is larger
  for the time between the two steps. On such a step it carries every pose it holds for every body to the new world before
  the step runs. That covers the pose the tracker solves from, the stored fit, the settled pose, the pose of the display, the
  solves that the first accept compares, the seat and the guide of the body, and the body itself, so the object stays where
  it was in front of the camera. The jump counts as no camera motion for the fast motion hold or the prediction gate. The
  console shows one line for each jump, "The AR session moved its world, so the tracked bodies were carried over to it".
  The tracker manager has a setting for it, `XRTrackerManager.FollowArWorldJumps`, which is on by default. The inspector shows
  it as Follow AR World Jumps, indented under the pose fusion row, and only while pose fusion is on. With the setting off, the
  tracker still compares every step with the step before it but carries no body, so a body can lose the object after a jump, as
  it did before this fix. The tracker reads the setting on every step, so a change applies at once, and a camera pose from
  before the setting was turned on is never read as a jump. A scene that was saved before the setting reads it on, so its
  bodies are carried.
