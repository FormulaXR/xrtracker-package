using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// Moves the scene's camera to where the frame the tracker just solved was taken from, so a body that is held in the
	/// recording's world frame is drawn through the viewpoint that recorded it.
	/// </summary>
	/// <remarks>
	/// <para>The tracker never moves a camera. On a device the AR session moves it every frame and the tracker only reads
	/// the result. A replay has no session, so without this component the camera stays where the scene placed it. The body
	/// is then solved correctly in the recording's world frame and drawn through a frozen viewpoint, which looks like a body
	/// stuck to the camera while the picture slides underneath.</para>
	/// <para>The pose it applies is the one the tracker was given for the frame it has just finished. It is read from the
	/// completed frame's own context, never from a second reading of the recording, so what is drawn is the viewpoint the body
	/// was solved against.</para>
	/// <para>With pose fusion off, no frame carries a camera pose, the completed context is not valid, and this component does
	/// nothing. That run works in each image's own camera frame, where the camera belongs where the scene put it.</para>
	/// <para>It also seats the scene in the recording's world frame once, before the body registers, which is the other half of
	/// standing in for a session that is not running. A body latches its initialization pose from where it stands when it is
	/// enabled. On a device the session has already put the camera at its real world pose by then, so the body's transform is a
	/// world pose. In a replay the camera is still at the scene's origin, so a camera-relative pose would be latched as a world
	/// pose and the run would start the object somewhere it has never been. Seating moves the camera onto the first frame's
	/// recorded pose and carries the body rigidly with it. The camera-relative pose the tracker is registered with does not
	/// change, and only the frame the pair sits in becomes the recording's own.</para>
	/// <para>The tracker adds this component by itself when a recording with camera poses plays under pose fusion and the scene
	/// has none, so a recording played in the editor needs no setup. A scene that carries one already is left alone.</para>
	/// </remarks>
	[DisallowMultipleComponent]
	[DefaultExecutionOrder(-100)]
	[AddComponentMenu("")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	public class RecordedCameraPoseDriver : MonoBehaviour
	{
		[SerializeField, Tooltip("The tracker whose completed frames are followed. " +
			"Left empty, the one on this object or in the scene is used.")]
		private XRTrackerManager _manager;

		[SerializeField, Tooltip("The camera to move. Left empty, the one the tracker " +
			"solves against is used, which is also the one the background is drawn behind.")]
		private Camera _camera;

		[SerializeField, Tooltip("Put the camera on the recording's first frame before the " +
			"body registers, and carry the body with it. Off, the body is initialized in a " +
			"world frame the recording never had.")]
		private bool _seatInRecordedWorldFrame = true;

		private bool _subscribed;
		private bool _seated;
		private int _seatFrame = -1;
		private Pose _seatPose = Pose.identity;
		private bool _seatChecked;
		private int _appliedFrames;
		private int _skippedFrames;
		private int _lastAppliedFrame = -1;

		/// <summary>How many completed frames have moved the camera.</summary>
		public int AppliedFrames => _appliedFrames;

		/// <summary>
		/// How many completed frames carried no camera pose and were passed over. A whole run of these is what a run with
		/// pose fusion off looks like.
		/// </summary>
		public int SkippedFrames => _skippedFrames;

		/// <summary>The last frame index whose pose was applied, or -1 if none was.</summary>
		public int LastAppliedFrame => _lastAppliedFrame;

		/// <summary>The camera this component moves, once it has resolved one.</summary>
		public Camera DrivenCamera => _camera;

		/// <summary>Whether the scene was moved into the recording's world frame.</summary>
		public bool Seated => _seated;

		/// <summary>Whether any scene object carries a camera pose driver, whether this class or one derived from it.</summary>
		internal static bool ExistsInScene() =>
			FindFirstObjectByType<RecordedCameraPoseDriver>(FindObjectsInactive.Include) != null;

		// Seating has to happen before TrackedBody.OnEnable, because that is where the body latches its initialization pose
		// from where it stands. Seating after it moves the body and leaves the latch holding the pose the body had before the
		// move. The execution order above puts this Awake ahead of that OnEnable.
		private void Awake()
		{
			Resolve();
			SeatSceneInRecordedWorldFrame();
		}

		private void OnEnable()
		{
			Resolve();
			if (_manager == null || _subscribed) return;
			_manager.OnTrackingLifecycleCompleted += OnTrackingLifecycleCompleted;
			_subscribed = true;
		}

		private void OnDisable()
		{
			if (!_subscribed || _manager == null) return;
			_manager.OnTrackingLifecycleCompleted -= OnTrackingLifecycleCompleted;
			_subscribed = false;
		}

		private void Resolve()
		{
			if (_manager == null && !TryGetComponent(out _manager))
				_manager = FindFirstObjectByType<XRTrackerManager>();
			if (_camera == null && _manager != null)
				_camera = _manager.MainCamera;
			if (_camera == null)
				_camera = Camera.main;
		}

		/// <summary>
		/// Moves the camera onto the recording's first frame and carries the body with it, so the pair starts in the frame the
		/// recording was made in.
		/// </summary>
		/// <remarks>
		/// The body is moved by the same rigid transform as the camera, chosen so its initial viewpoint keeps landing exactly on
		/// the camera. A run without pose fusion is not seated at all, because it has no world frame to be seated in and works
		/// correctly where the scene already put it. A tracker that has finished initializing has already latched its bodies, so
		/// a driver that is added after that point drives the camera and leaves the scene where it is.
		/// </remarks>
		private void SeatSceneInRecordedWorldFrame()
		{
			if (_seated || _manager == null || _camera == null) return;
			if (!RecordedCameraPoseDriverPolicy.MaySeat(_seatInRecordedWorldFrame, _manager.UseARPoseFusion,
				_manager.CurrentImageSource == ImageSource.Sequence, _manager.IsInitialized))
			{
				return;
			}

			if (!TryReadFirstRecordedCameraPose(_manager.SequenceDirectory,
				out int frame, out Pose worldFromCamera))
			{
				return;
			}

			TrackedBody body = FindSeatableBody();
			if (body == null)
			{
				Debug.LogWarning("[RecordedCameraPoseDriver] no body in this scene starts from a " +
					"viewpoint, so there is nothing to seat and the world frame is left alone.");
				return;
			}

			Transform viewpoint = body.InitialViewpoint;
			Quaternion turn = worldFromCamera.rotation * Quaternion.Inverse(viewpoint.rotation);
			Vector3 moved = worldFromCamera.position +
				turn * (body.transform.position - viewpoint.position);
			body.transform.SetPositionAndRotation(moved, turn * body.transform.rotation);
			_camera.transform.SetPositionAndRotation(
				worldFromCamera.position, worldFromCamera.rotation);

			_seated = true;
			_seatFrame = frame;
			_seatPose = worldFromCamera;
			Debug.Log($"[RecordedCameraPoseDriver] seated on frame {frame}: camera at " +
				$"{worldFromCamera.position}, body '{body.BodyId}' carried with it.");
		}

		/// <summary>The one body this scene starts from a placed viewpoint.</summary>
		private static TrackedBody FindSeatableBody()
		{
			TrackedBody found = null;
			foreach (TrackedBody body in FindObjectsByType<TrackedBody>(
				FindObjectsInactive.Include, FindObjectsSortMode.None))
			{
				if (body.InitialPoseSource != InitialPoseSource.Viewpoint ||
					body.InitialViewpoint == null)
				{
					continue;
				}
				if (found != null)
				{
					Debug.LogWarning("[RecordedCameraPoseDriver] this scene holds more than one " +
						"body starting from a viewpoint, so none was seated.");
					return null;
				}
				found = body;
			}
			return found;
		}

		private static readonly Regex PoseRow = new Regex(
			@"""frame""\s*:\s*(?<frame>-?\d+).*?""valid""\s*:\s*(?<valid>\d+)" +
			@".*?""t""\s*:\s*\[(?<t>[^\]]*)\].*?""q""\s*:\s*\[(?<q>[^\]]*)\]",
			RegexOptions.Compiled);

		/// <summary>The first frame of the recorded trajectory, in engine coordinates.</summary>
		/// <remarks>
		/// The rows are the device's own convention and the package converts them by conjugating with a flip of the vertical
		/// axis. The same conjugation is applied here rather than a second guess at the convention, and the first completed
		/// frame checks this pose against the one the tracker was actually given, so a disagreement is reported and not
		/// quietly drawn.
		/// </remarks>
		private static bool TryReadFirstRecordedCameraPose(
			string sequenceDirectory, out int frame, out Pose worldFromCamera)
		{
			frame = -1;
			worldFromCamera = Pose.identity;
			if (string.IsNullOrWhiteSpace(sequenceDirectory)) return false;
			string path = Path.Combine(sequenceDirectory, "extrinsics.jsonl");
			if (!File.Exists(path))
			{
				Debug.LogWarning("[RecordedCameraPoseDriver] this recording carries no " +
					"extrinsics.jsonl, so there is no world frame to seat in. " + path);
				return false;
			}

			try
			{
				foreach (string line in File.ReadLines(path))
				{
					Match match = PoseRow.Match(line);
					if (!match.Success || match.Groups["valid"].Value == "0") continue;
					if (!TryReadTriple(match.Groups["t"].Value, out Vector3 position) ||
						!TryReadQuaternion(match.Groups["q"].Value, out Quaternion rotation))
					{
						continue;
					}
					frame = int.Parse(match.Groups["frame"].Value, CultureInfo.InvariantCulture);
					Matrix4x4 flip = Matrix4x4.Scale(new Vector3(1f, -1f, 1f));
					Matrix4x4 converted =
						flip * Matrix4x4.TRS(position, rotation, Vector3.one) * flip;
					worldFromCamera = new Pose(converted.GetPosition(), converted.rotation);
					return true;
				}
			}
			catch (Exception exception)
			{
				Debug.LogWarning("[RecordedCameraPoseDriver] the recorded trajectory could not be " +
					"read. " + exception.Message);
			}
			return false;
		}

		private static bool TryReadTriple(string text, out Vector3 value)
		{
			value = Vector3.zero;
			string[] parts = text.Split(',');
			if (parts.Length != 3) return false;
			if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
				!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
				!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
			{
				return false;
			}
			value = new Vector3(x, y, z);
			return true;
		}

		private static bool TryReadQuaternion(string text, out Quaternion value)
		{
			value = Quaternion.identity;
			string[] parts = text.Split(',');
			if (parts.Length != 4) return false;
			var numbers = new float[4];
			for (int i = 0; i < 4; ++i)
			{
				if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture,
					out numbers[i]))
				{
					return false;
				}
			}
			value = new Quaternion(numbers[0], numbers[1], numbers[2], numbers[3]);
			return true;
		}

		private void OnTrackingLifecycleCompleted(
			XRTrackerManager.TrackingFrameInstrumentationContext context)
		{
			// An invalid context is the ordinary state of a run without pose fusion. The completed frame is built from the
			// fusion frame, and a run that submits none reports every frame invalid with an identity pose in it.
			if (!context.IsValid || _camera == null)
			{
				++_skippedFrames;
				return;
			}

			Matrix4x4 worldFromCamera = context.WorldFromCameraUnity;
			Vector3 position = worldFromCamera.GetPosition();
			Quaternion rotation = worldFromCamera.rotation;
			// A pose that is not finite would put the camera somewhere no picture can be taken from, and there would be no way
			// to tell that from a black frame.
			if (!IsFinite(position) || !IsFinite(rotation))
			{
				++_skippedFrames;
				Debug.LogError($"[RecordedCameraPoseDriver] frame {context.FrameIndex} carried a " +
					"camera pose that is not a finite transform, so the camera was left where it was.");
				return;
			}

			// The seat was read out of the recording by this component, and this is the same frame's pose as the tracker was
			// given. If the two disagree, the conversion applied when seating is wrong, and every later frame would be drawn
			// correctly while the body was initialized in the wrong place.
			if (_seated && !_seatChecked && context.FrameIndex == _seatFrame)
			{
				_seatChecked = true;
				float millimetres = Vector3.Distance(_seatPose.position, position) * 1000f;
				float degrees = Quaternion.Angle(_seatPose.rotation, rotation);
				if (millimetres > 1f || degrees > 0.1f)
				{
					Debug.LogError($"[RecordedCameraPoseDriver] the pose this scene was seated at " +
						$"and the one the tracker was given for frame {context.FrameIndex} differ " +
						$"by {millimetres:F1} mm and {degrees:F2} deg. The body was initialized in " +
						"a different world frame than it is being solved in.");
				}
			}

			_camera.transform.SetPositionAndRotation(position, rotation);
			++_appliedFrames;
			_lastAppliedFrame = context.FrameIndex;
		}

		private static bool IsFinite(Vector3 value) =>
			!float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
			!float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
			!float.IsNaN(value.z) && !float.IsInfinity(value.z);

		private static bool IsFinite(Quaternion value)
		{
			float squared = value.x * value.x + value.y * value.y +
				value.z * value.z + value.w * value.w;
			return !float.IsNaN(squared) && !float.IsInfinity(squared) && squared > 1e-6f;
		}
	}

	/// <summary>The decisions around the recorded camera pose driver, apart from the objects they act on.</summary>
	internal static class RecordedCameraPoseDriverPolicy
	{
		/// <summary>
		/// Whether the tracker adds a driver. It does when a recording with camera poses plays under pose fusion and the scene
		/// has no driver yet, so a scene that carries one is not driven twice.
		/// </summary>
		internal static bool ShouldAttach(bool posesPlayUnderFusion, bool driverPresent) =>
			posesPlayUnderFusion && !driverPresent;

		/// <summary>
		/// Whether the driver may seat the scene in the recording's world frame. It may while the tracker has not finished
		/// initializing, because the bodies latch their pose when they are enabled and a seat after that would move them
		/// away from the pose they hold.
		/// </summary>
		internal static bool MaySeat(bool seatRequested, bool fusionInForce, bool sequenceSource, bool trackerInitialized) =>
			seatRequested && fusionInForce && sequenceSource && !trackerInitialized;
	}
}
