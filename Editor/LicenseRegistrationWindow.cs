using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace IV.FormulaTracker.Editor
{
	/// <summary>
	/// Editor window for registering a free Developer license.
	/// Menu: Tools > XRTracker > License Registration
	/// </summary>
	public class LicenseRegistrationWindow : EditorWindow
	{
		private const string API_URL = "https://api.xrtracker.net/v1/register";
		private const string TERMS_URL = "https://docs.xrtracker.net/legal/terms";
		private const string PRIVACY_URL = "https://docs.xrtracker.net/legal/privacy";

		private string _name = "";
		private string _email = "";
		private string _company = "";
		private bool _acceptedTerms;
		private string _statusMessage = "";
		private MessageType _statusType = MessageType.None;
		private bool _registering;
		private UnityWebRequest _activeRequest;

		[MenuItem("Tools/XRTracker/License Registration")]
		public static void ShowWindow()
		{
			var window = GetWindow<LicenseRegistrationWindow>("XRTracker License");
			window.minSize = new Vector2(400, 380);
		}

		private Vector2 _scrollPosition;

		private void OnGUI()
		{
			_scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Welcome to XRTracker", EditorStyles.boldLabel);
			EditorGUILayout.Space(4);
			EditorGUILayout.LabelField(
				"XRTracker is a markerless 3D object tracking SDK for Unity. " +
				"Import your CAD model or 3D mesh and track real-world objects with " +
				"6DoF pose estimation and sub-centimeter precision \u2014 no markers, scanning, or cloud training required.",
				EditorStyles.wordWrappedLabel);
			EditorGUILayout.Space(8);

			EditorGUILayout.LabelField("Register for a Free Developer License", EditorStyles.boldLabel);
			EditorGUILayout.Space(4);
			EditorGUILayout.HelpBox(
				"The Developer license is free, valid for 1 year (renewable), and includes a visible watermark. " +
				"For development and evaluation only \u2014 production use requires a Commercial license.\n\n" +
				"Features included:\n" +
				"\u2022 Silhouette, Edge, and Depth tracking modalities\n" +
				"\u2022 AR Foundation integration (ARKit / ARCore)\n" +
				"\u2022 Windows, iOS, and Android support\n" +
				"\u2022 Built-in model generation from any mesh\n" +
				"\u2022 No time limits \u2014 full functionality",
				MessageType.Info);

			EditorGUILayout.Space(8);

			// Registration fields
			_name = EditorGUILayout.TextField("Name *", _name);
			_email = EditorGUILayout.TextField("Email *", _email);
			_company = EditorGUILayout.TextField("Company", _company);


			EditorGUILayout.Space(8);

			// Terms acceptance
			EditorGUILayout.BeginHorizontal();
			_acceptedTerms = EditorGUILayout.Toggle(_acceptedTerms, GUILayout.Width(14));
			if (GUILayout.Button("I accept the Terms of Use", EditorStyles.linkLabel))
				Application.OpenURL(TERMS_URL);
			EditorGUILayout.EndHorizontal();

			// Legal links
			EditorGUILayout.BeginHorizontal();
			GUILayout.Space(18);
			if (GUILayout.Button("Terms of Use", EditorStyles.linkLabel))
				Application.OpenURL(TERMS_URL);
			GUILayout.Label("|", GUILayout.ExpandWidth(false));
			if (GUILayout.Button("Privacy Policy", EditorStyles.linkLabel))
				Application.OpenURL(PRIVACY_URL);
			GUILayout.FlexibleSpace();
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.Space(8);

			// Register button
			using (new EditorGUI.DisabledScope(_registering))
			{
				if (GUILayout.Button(_registering ? "Registering..." : "Register", GUILayout.Height(30)))
					Register();
			}

			// Status message
			if (!string.IsNullOrEmpty(_statusMessage))
			{
				EditorGUILayout.Space(4);
				EditorGUILayout.HelpBox(_statusMessage, _statusType);
			}

			// Separator
			EditorGUILayout.Space(16);
			var rect = EditorGUILayout.GetControlRect(false, 1);
			EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.3f));
			EditorGUILayout.Space(8);

			// Import existing license
			EditorGUILayout.LabelField("Already have a license?", EditorStyles.boldLabel);
			if (GUILayout.Button("Import .lic File"))
				ImportLicenseFile();

			// Poll active request
			if (_registering && _activeRequest != null && _activeRequest.isDone)
				OnRequestComplete();

			EditorGUILayout.Space(8);
			EditorGUILayout.EndScrollView();
		}

		private void Register()
		{
			if (string.IsNullOrWhiteSpace(_name))
			{
				_statusMessage = "Name is required.";
				_statusType = MessageType.Error;
				return;
			}
			if (string.IsNullOrWhiteSpace(_email) || !_email.Contains("@"))
			{
				_statusMessage = "A valid email is required.";
				_statusType = MessageType.Error;
				return;
			}
			if (!_acceptedTerms)
			{
				_statusMessage = "You must accept the Terms of Use.";
				_statusType = MessageType.Error;
				return;
			}

			_registering = true;
			_statusMessage = "Sending registration request...";
			_statusType = MessageType.Info;

			string json = JsonUtility.ToJson(new RegistrationRequest
			{
				name = _name.Trim(),
				email = _email.Trim(),
				company = _company?.Trim() ?? ""
			});

			_activeRequest = new UnityWebRequest(API_URL, "POST");
			byte[] body = Encoding.UTF8.GetBytes(json);
			_activeRequest.uploadHandler = new UploadHandlerRaw(body);
			_activeRequest.downloadHandler = new DownloadHandlerBuffer();
			_activeRequest.SetRequestHeader("Content-Type", "application/json");
			_activeRequest.SendWebRequest();

			// OnGUI polls _activeRequest.isDone
		}

		private void OnRequestComplete()
		{
			var request = _activeRequest;
			_activeRequest = null;
			_registering = false;

			if (request.result != UnityWebRequest.Result.Success)
			{
				_statusMessage = $"Registration failed: {request.error}\n\n" +
				                 "You can also register manually at docs.xrtracker.net";
				_statusType = MessageType.Error;
				request.Dispose();
				return;
			}

			// Parse response — expect { "license": "..." }
			string responseText = request.downloadHandler.text;
			request.Dispose();

			var response = JsonUtility.FromJson<RegistrationResponse>(responseText);
			if (string.IsNullOrEmpty(response?.license))
			{
				_statusMessage = "Invalid response from server. Please try again or register at docs.xrtracker.net";
				_statusType = MessageType.Error;
				return;
			}

			SaveLicenseToProject(response.license);
		}

		private void ImportLicenseFile()
		{
			string path = EditorUtility.OpenFilePanel("Select License File", "", "lic");
			if (string.IsNullOrEmpty(path)) return;

			try
			{
				string licenseJson = System.IO.File.ReadAllText(path);
				string filename = System.IO.Path.GetFileName(path);
				SaveLicenseToProject(licenseJson, "Assets/" + filename);
			}
			catch (Exception e)
			{
				_statusMessage = $"Failed to import license: {e.Message}";
				_statusType = MessageType.Error;
			}
		}

		/// <summary>
		/// Save license JSON into the project and assign to XRTrackerManager.
		/// If assetPath is null, prompts the user for a save location.
		/// </summary>
		private void SaveLicenseToProject(string licenseJson, string assetPath = null)
		{
			string dataPath = Application.dataPath.Replace('\\', '/');

			if (assetPath == null)
			{
				string savePath = EditorUtility.SaveFilePanel(
					"Save License File", "Assets", "XRTracker_License", "lic");

				if (string.IsNullOrEmpty(savePath)) return;

				savePath = savePath.Replace('\\', '/');
				if (!savePath.StartsWith(dataPath))
				{
					_statusMessage = "License must be saved inside the Assets folder.";
					_statusType = MessageType.Error;
					return;
				}

				assetPath = "Assets" + savePath.Substring(dataPath.Length);
			}

			try
			{
				string fullPath = dataPath + assetPath.Substring("Assets".Length);
				System.IO.File.WriteAllText(fullPath, licenseJson);

				AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

				// Assign to XRTrackerManager in scene
				var manager = UnityEngine.Object.FindFirstObjectByType<XRTrackerManager>();
				if (manager != null)
				{
					var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
					if (textAsset != null)
					{
						var so = new SerializedObject(manager);
						so.FindProperty("_embeddedLicense").objectReferenceValue = textAsset;
						so.ApplyModifiedProperties();
						EditorUtility.SetDirty(manager);
						_statusMessage = $"License saved to {assetPath} and assigned to XRTrackerManager.\n\nPress Play to activate.";
					}
					else
					{
						_statusMessage = $"License saved to {assetPath}.\nDrag it into the License File field on XRTrackerManager.";
					}
				}
				else
				{
					_statusMessage = $"License saved to {assetPath}.\nAdd an XRTrackerManager to your scene and assign the license file.";
				}

				_statusType = MessageType.Info;
				Debug.Log($"[XRTracker] License saved to {assetPath}");
			}
			catch (Exception e)
			{
				_statusMessage = $"Failed to save license: {e.Message}";
				_statusType = MessageType.Error;
			}
		}

		private void OnDestroy()
		{
			_activeRequest?.Dispose();
		}

		[Serializable]
		private class RegistrationRequest
		{
			public string name;
			public string email;
			public string company;
		}

		[Serializable]
		private class RegistrationResponse
		{
			public string license;
		}
	}
}
