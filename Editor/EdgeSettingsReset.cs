using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	/// <summary>
	/// Puts every edge setting of a <see cref="TrackedBody"/> back to the value a new
	/// <see cref="EdgeTrackingSettings"/> holds. It is offered in the component's
	/// context menu and by a button in the Edge Tracking section of the inspector.
	/// </summary>
	/// <remarks>
	/// <para>Only the serialized edge settings are written. The silhouette, score,
	/// confirm and every other setting of the body stay as they are, and so do the
	/// edge options that are not serialized.</para>
	/// <para>The change can be undone, it marks the body dirty, and on an instance of a
	/// prefab it is recorded as an override of the instance, so it works there too.
	/// A body that is playing has already read its edge settings, so nothing is reset
	/// while the editor is in play mode.</para>
	/// </remarks>
	public static class EdgeSettingsReset
	{
		/// <summary>Where the item sits in the context menu of the component.</summary>
		public const string MENU_PATH = "CONTEXT/TrackedBody/Reset Edge Settings to Defaults";

		/// <summary>The name the change takes in the Undo history.</summary>
		public const string UNDO_NAME = "Reset Edge Settings to Defaults";

		[MenuItem(MENU_PATH, false)]
		private static void ResetFromMenu(MenuCommand command)
		{
			if (command.context is TrackedBody body)
				Reset(body);
		}

		[MenuItem(MENU_PATH, true)]
		private static bool CanResetFromMenu(MenuCommand command)
		{
			return CanReset(command.context as TrackedBody);
		}

		/// <summary>Whether the edge settings of this body can be reset now.</summary>
		public static bool CanReset(TrackedBody body)
		{
			return body != null && !EditorApplication.isPlayingOrWillChangePlaymode;
		}

		/// <summary>
		/// Sets every serialized edge setting of the body to the package default.
		/// Returns false, and changes nothing, when <see cref="CanReset"/> says no.
		/// </summary>
		public static bool Reset(TrackedBody body)
		{
			if (!CanReset(body))
				return false;

			Undo.RecordObject(body, UNDO_NAME);
			string defaults = JsonUtility.ToJson(new EdgeTrackingSettings());
			JsonUtility.FromJsonOverwrite(defaults, body.EdgeTracking);
			EditorUtility.SetDirty(body);
			if (PrefabUtility.IsPartOfPrefabInstance(body))
				PrefabUtility.RecordPrefabInstancePropertyModifications(body);
			return true;
		}
	}
}
