using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;

namespace IV.FormulaTracker.Editor
{
	public class LicenseActivationWindow : EditorWindow
	{
		private const string API_URL = "https://api.xrtracker.net/license/generate";

		private string _invoiceId = "";
		private string _licenseeName = "";
		private string _licenseeEmail = "";
		private string _savePath = "Assets";

		private string _statusMessage;
		private MessageType _statusType;
		private bool _requesting;
		private UnityWebRequest _activeRequest;

		[MenuItem("XRTracker/Activate License")]
		public static void ShowWindow()
		{
			var window = GetWindow<LicenseActivationWindow>("Activate License");
			window.minSize = new Vector2(400, 300);
		}

		private void OnGUI()
		{
			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Activate XRTracker License", EditorStyles.boldLabel);
			EditorGUILayout.Space(4);
			EditorGUILayout.HelpBox(
				"Enter your Unity Asset Store invoice details to generate a Developer license (valid for 1 year).",
				MessageType.Info);
			EditorGUILayout.Space(8);

			using (new EditorGUI.DisabledScope(_requesting))
			{
				_invoiceId = EditorGUILayout.TextField(
					new GUIContent("Invoice ID", "Your Unity Asset Store invoice number (e.g. IN010101851214)"),
					_invoiceId);

				_licenseeName = EditorGUILayout.TextField(
					new GUIContent("Name", "Your name as it appears on the purchase"),
					_licenseeName);

				_licenseeEmail = EditorGUILayout.TextField(
					new GUIContent("Email", "Your email address"),
					_licenseeEmail);

				EditorGUILayout.Space(4);

				EditorGUILayout.BeginHorizontal();
				_savePath = EditorGUILayout.TextField("Save To", _savePath);
				if (GUILayout.Button("...", GUILayout.Width(30)))
				{
					string folder = EditorUtility.OpenFolderPanel("Save License File", _savePath, "");
					if (!string.IsNullOrEmpty(folder))
					{
						if (folder.StartsWith(Application.dataPath))
							_savePath = "Assets" + folder.Substring(Application.dataPath.Length);
						else
							_savePath = folder;
					}
				}
				EditorGUILayout.EndHorizontal();

				EditorGUILayout.Space(8);

				if (GUILayout.Button(_requesting ? "Generating..." : "Generate License", GUILayout.Height(30)))
				{
					if (!_requesting)
						RequestLicense();
				}
			}

			if (!string.IsNullOrEmpty(_statusMessage))
			{
				EditorGUILayout.Space(8);
				EditorGUILayout.HelpBox(_statusMessage, _statusType);
			}
		}

		private void RequestLicense()
		{
			if (string.IsNullOrWhiteSpace(_invoiceId))
			{
				SetStatus("Please enter an invoice ID.", MessageType.Warning);
				return;
			}
			if (string.IsNullOrWhiteSpace(_licenseeName))
			{
				SetStatus("Please enter your name.", MessageType.Warning);
				return;
			}
			if (string.IsNullOrWhiteSpace(_licenseeEmail))
			{
				SetStatus("Please enter your email.", MessageType.Warning);
				return;
			}

			_requesting = true;
			_statusMessage = null;

			var body = JsonUtility.ToJson(new LicenseRequestBody
			{
				invoice_id = _invoiceId.Trim(),
				licensee_name = _licenseeName.Trim(),
				licensee_email = _licenseeEmail.Trim(),
			});

			_activeRequest = new UnityWebRequest(API_URL, "POST");
			_activeRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
			_activeRequest.downloadHandler = new DownloadHandlerBuffer();
			_activeRequest.SetRequestHeader("Content-Type", "application/json");

			var op = _activeRequest.SendWebRequest();
			op.completed += OnRequestComplete;

			EditorApplication.update += WaitForRequest;
		}

		private void WaitForRequest()
		{
			if (_activeRequest != null && !_activeRequest.isDone)
			{
				Repaint();
				return;
			}
			EditorApplication.update -= WaitForRequest;
		}

		private void OnRequestComplete(AsyncOperation op)
		{
			_requesting = false;

			if (_activeRequest.result != UnityWebRequest.Result.Success)
			{
				string errorBody = _activeRequest.downloadHandler?.text ?? "";
				string errorMsg = _activeRequest.error;

				// Try to parse error JSON
				if (!string.IsNullOrEmpty(errorBody))
				{
					try
					{
						var err = JsonUtility.FromJson<ErrorResponse>(errorBody);
						if (!string.IsNullOrEmpty(err.error))
							errorMsg = err.error;
					}
					catch { }
				}

				SetStatus(errorMsg, MessageType.Error);
				_activeRequest.Dispose();
				_activeRequest = null;
				Repaint();
				return;
			}

			string licContent = _activeRequest.downloadHandler.text;
			_activeRequest.Dispose();
			_activeRequest = null;

			// Save the .lic file
			string nameSlug = _licenseeName.Trim().Replace(" ", "_");
			if (nameSlug.Length > 20) nameSlug = nameSlug.Substring(0, 20);
			string filename = $"FormulaTracker_developer_{nameSlug}.lic";
			string fullPath = Path.Combine(_savePath, filename);

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
				File.WriteAllText(fullPath, licContent);
				AssetDatabase.Refresh();

				// Auto-assign to XRTrackerManager if one exists in the scene
				string assetPath = fullPath;
				if (assetPath.StartsWith(Application.dataPath))
					assetPath = "Assets" + assetPath.Substring(Application.dataPath.Length);

				var licAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
				if (licAsset != null)
					TryAssignToManager(licAsset);

				SetStatus($"License saved to {assetPath}\nAssign it to XRTrackerManager > License File if not auto-assigned.", MessageType.Info);
			}
			catch (Exception e)
			{
				SetStatus($"Failed to save file: {e.Message}", MessageType.Error);
			}

			Repaint();
		}

		private static void TryAssignToManager(TextAsset licAsset)
		{
			var manager = FindAnyObjectByType<XRTrackerManager>();
			if (manager == null) return;

			var so = new SerializedObject(manager);
			var prop = so.FindProperty("_embeddedLicense");
			if (prop != null)
			{
				prop.objectReferenceValue = licAsset;
				so.ApplyModifiedProperties();
				EditorUtility.SetDirty(manager);
				Debug.Log($"[XRTracker] License auto-assigned to {manager.name}");
			}
		}

		private void SetStatus(string message, MessageType type)
		{
			_statusMessage = message;
			_statusType = type;
		}

		private void OnDestroy()
		{
			if (_activeRequest != null)
			{
				_activeRequest.Abort();
				_activeRequest.Dispose();
				_activeRequest = null;
			}
			EditorApplication.update -= WaitForRequest;
		}

		[Serializable]
		private struct LicenseRequestBody
		{
			public string invoice_id;
			public string licensee_name;
			public string licensee_email;
		}

		[Serializable]
		private struct ErrorResponse
		{
			public string error;
		}
	}
}
