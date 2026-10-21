using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	/// <summary>
	/// Keeps the pose a tracked body is shown at in play mode and writes it to the Viewpoint of that body when play mode ends.
	/// </summary>
	/// <remarks>
	/// <para>A change made to a scene object in play mode is lost when play stops, so the pose is kept in the session state of the
	/// editor, which survives the reload of the scripts. When the editor is back in edit mode, the Viewpoint is placed so that the
	/// body, standing where the scene authors it, sees the pose that was kept. That is the inverse of the formula the body uses to
	/// turn its Viewpoint into its initial pose, so a body that starts from the Viewpoint starts at the pose that was shown.</para>
	/// <para>The pose is kept as the pose of the body seen from the camera, and not as a world pose. The world frame of a replay is
	/// the frame of its recording, and the Viewpoint of a body is often a child of that body, so a world pose of the Viewpoint
	/// taken in play mode would not mean the same thing in edit mode.</para>
	/// </remarks>
	[InitializeOnLoad]
	internal static class InitialViewpointCapture
	{
		private const string SESSION_KEY = "IV.FormulaTracker.InitialViewpointCapture";
		private const string UNDO_NAME = "Use Current Pose as Initial Viewpoint";
		private const float POSITION_TOLERANCE_METRES = 1e-4f;
		private const float ROTATION_TOLERANCE_DEGREES = 0.01f;
		private const int SCENE_OBJECT_IDENTIFIER_TYPE = 2;

		[Serializable]
		private sealed class Entry
		{
			public string BodyKey;
			public string BodyName;
			public Vector3 Position;
			public Quaternion Rotation = Quaternion.identity;
		}

		[Serializable]
		private sealed class Store
		{
			public List<Entry> Entries = new List<Entry>();
		}

		static InitialViewpointCapture()
		{
			EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
			EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

			// The event can arrive before this class is loaded again after play mode ends, so a pose that is still waiting is
			// applied once the editor has settled.
			EditorApplication.delayCall += () =>
			{
				if (!EditorApplication.isPlayingOrWillChangePlaymode)
					ApplyPending();
			};
		}

		/// <summary>
		/// Whether the button that keeps the current pose is offered. It is offered in play mode, while the body tracks, for a body
		/// that starts from a Viewpoint.
		/// </summary>
		internal static bool IsOffered(bool isPlaying, bool isTracking, InitialPoseSource source)
		{
			return isPlaying && isTracking && source == InitialPoseSource.Viewpoint;
		}

		/// <summary>Whether a pose kept for this body is waiting for play mode to end.</summary>
		internal static bool HasPending(TrackedBody body)
		{
			if (body == null || string.IsNullOrEmpty(SessionState.GetString(SESSION_KEY, string.Empty)))
				return false;
			if (!TryGetKey(body, out string key))
				return false;
			foreach (Entry entry in Load().Entries)
			{
				if (entry.BodyKey == key)
					return true;
			}
			return false;
		}

		/// <summary>
		/// Keeps the pose the body is shown at, seen from the camera of the tracker, to be written to its Viewpoint when play mode
		/// ends. A pose that was kept for the same body before is replaced.
		/// </summary>
		/// <param name="body">The tracked body.</param>
		/// <param name="message">What happened, in a sentence for the console.</param>
		/// <returns>Whether the pose was kept.</returns>
		internal static bool TryCapture(TrackedBody body, out string message)
		{
			if (body == null || !IsOffered(Application.isPlaying, body.IsTracking, body.InitialPoseSource))
			{
				message = "The pose is not kept because the body is not tracking in play mode with a Viewpoint.";
				return false;
			}

			string bodyName = body.name;
			if (body.InitialViewpoint == null)
			{
				message = $"The pose of body '{bodyName}' is not kept because the body has no Viewpoint to set.";
				return false;
			}

			XRTrackerManager manager = XRTrackerManager.Instance;
			Transform camera = manager != null ? manager.CameraTransform : null;
			if (camera == null)
			{
				message = $"The pose of body '{bodyName}' is not kept because the tracker has no camera.";
				return false;
			}

			if (!TryGetKey(body, out string key))
			{
				message = $"The pose of body '{bodyName}' is not kept because the body is not part of a saved scene.";
				return false;
			}

			TrackedBody.ViewpointFromBody(camera, body.transform.position, body.transform.rotation,
				out Vector3 position, out Quaternion rotation);
			if (!ConversionUtils.IsUsablePose(position, rotation))
			{
				message = $"The pose of body '{bodyName}' is not kept because it is not a usable pose.";
				return false;
			}

			Store store = Load();
			store.Entries.RemoveAll(entry => entry.BodyKey == key);
			store.Entries.Add(new Entry { BodyKey = key, BodyName = bodyName, Position = position, Rotation = rotation });
			SessionState.SetString(SESSION_KEY, JsonUtility.ToJson(store));

			message = $"The pose of body '{bodyName}' is kept. The Viewpoint is set to it when play mode ends.";
			return true;
		}

		private static void OnPlayModeStateChanged(PlayModeStateChange change)
		{
			if (change == PlayModeStateChange.EnteredEditMode)
				ApplyPending();
		}

		/// <summary>
		/// Writes every kept pose to the Viewpoint of its body, with an undo step, and marks the scene as changed.
		/// </summary>
		/// <returns>How many Viewpoints were set.</returns>
		internal static int ApplyPending()
		{
			string json = SessionState.GetString(SESSION_KEY, string.Empty);
			if (string.IsNullOrEmpty(json))
				return 0;

			// The entries are dropped before they are applied, so a failure cannot make the next reload apply them again.
			SessionState.EraseString(SESSION_KEY);

			Store store;
			try
			{
				store = JsonUtility.FromJson<Store>(json);
			}
			catch (ArgumentException)
			{
				return 0;
			}
			if (store == null)
				return 0;

			int applied = 0;
			foreach (Entry entry in store.Entries)
			{
				if (ApplyEntry(entry))
					++applied;
			}
			return applied;
		}

		private static bool ApplyEntry(Entry entry)
		{
			TrackedBody body = FindBody(entry.BodyKey);
			if (body == null)
			{
				Debug.LogWarning($"[XRTracker] The Viewpoint of body '{entry.BodyName}' is not set because the body is not in the open scenes.");
				return false;
			}

			Transform viewpoint = body.InitialViewpoint;
			if (viewpoint == null)
			{
				Debug.LogWarning($"[XRTracker] The Viewpoint of body '{entry.BodyName}' is not set because the body has no Viewpoint.");
				return false;
			}

			TrackedBody.ViewpointStandingFor(
				body.transform.position, body.transform.rotation,
				entry.Position, entry.Rotation, viewpoint.lossyScale,
				out Vector3 viewpointPosition, out Quaternion viewpointRotation);

			Undo.RecordObject(viewpoint, UNDO_NAME);
			viewpoint.SetPositionAndRotation(viewpointPosition, viewpointRotation);
			if (PrefabUtility.IsPartOfPrefabInstance(viewpoint))
				PrefabUtility.RecordPrefabInstancePropertyModifications(viewpoint);
			EditorUtility.SetDirty(viewpoint);
			EditorSceneManager.MarkSceneDirty(viewpoint.gameObject.scene);

			TrackedBody.ViewpointFromBody(viewpoint, body.transform.position, body.transform.rotation,
				out Vector3 resultPosition, out Quaternion resultRotation);
			float metresOff = Vector3.Distance(resultPosition, entry.Position);
			float degreesOff = Quaternion.Angle(resultRotation, entry.Rotation);
			string off = string.Format(CultureInfo.InvariantCulture,
				"It is off by {0:F3} millimetres and {1:F3} degrees.", metresOff * 1000f, degreesOff);
			if (metresOff > POSITION_TOLERANCE_METRES || degreesOff > ROTATION_TOLERANCE_DEGREES)
			{
				Debug.LogWarning($"[XRTracker] The Viewpoint of body '{entry.BodyName}' is set, but the body does not start exactly at the pose that was kept. {off} " +
					"A skewed scale on the Viewpoint or on a parent of it can cause this.");
			}
			else
			{
				Debug.Log($"[XRTracker] The Viewpoint of body '{entry.BodyName}' is set, so the body starts at the pose that was kept. {off} Save the scene to keep it.");
			}
			return true;
		}

		private static bool TryGetKey(TrackedBody body, out string key)
		{
			GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(body);
			bool isSceneObject = id.identifierType == SCENE_OBJECT_IDENTIFIER_TYPE;
			key = isSceneObject ? id.ToString() : string.Empty;
			return isSceneObject;
		}

		private static TrackedBody FindBody(string key)
		{
			if (!GlobalObjectId.TryParse(key, out GlobalObjectId id))
				return null;
			return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as TrackedBody;
		}

		private static Store Load()
		{
			string json = SessionState.GetString(SESSION_KEY, string.Empty);
			if (string.IsNullOrEmpty(json))
				return new Store();
			try
			{
				return JsonUtility.FromJson<Store>(json) ?? new Store();
			}
			catch (ArgumentException)
			{
				return new Store();
			}
		}
	}
}
