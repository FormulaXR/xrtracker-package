using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	/// <summary>
	/// Two triggers for license registration prompt:
	/// 1. First project open (EditorPrefs, persists across sessions)
	/// 2. Play Mode entry without license (cancels Play Mode if user opens registration)
	/// </summary>
	[InitializeOnLoad]
	internal static class LicenseFirstRunPrompt
	{
		private const string PREFS_KEY = "FormulaTracker_RegistrationWindowShown";
		private const string SESSION_KEY = "FormulaTracker_PlayModePromptShown";

		static LicenseFirstRunPrompt()
		{
			EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

			// First project open — show registration window if never opened before
			if (!EditorPrefs.GetBool(PREFS_KEY, false) && !HasLicense())
			{
				EditorPrefs.SetBool(PREFS_KEY, true);
				// Delay to let the Editor finish loading
				EditorApplication.delayCall += () => LicenseRegistrationWindow.ShowWindow();
			}
		}

		private static void OnPlayModeStateChanged(PlayModeStateChange state)
		{
			// Intercept before entering Play Mode to allow cancellation
			if (state != PlayModeStateChange.ExitingEditMode) return;
			if (SessionState.GetBool(SESSION_KEY, false)) return;
			if (HasLicense()) return;

			SessionState.SetBool(SESSION_KEY, true);

			bool openRegistration = EditorUtility.DisplayDialog(
				"XRTracker \u2014 No License",
				"No license found. Register for a free Developer license to start tracking.\n\n" +
				"The Developer license is free, valid for 1 year (renewable), and for development use only.",
				"Open Registration",
				"Continue without license");

			if (openRegistration)
			{
				// Cancel Play Mode and open registration window
				EditorApplication.isPlaying = false;
				EditorApplication.delayCall += () => LicenseRegistrationWindow.ShowWindow();
			}
		}

		private static bool HasLicense()
		{
			string path = System.IO.Path.Combine(Application.persistentDataPath, "FormulaTracker.lic");
			if (System.IO.File.Exists(path)) return true;

			string saPath = Application.streamingAssetsPath;
			if (System.IO.Directory.Exists(saPath) &&
			    System.IO.Directory.GetFiles(saPath, "*.lic").Length > 0)
				return true;

			return false;
		}
	}
}
