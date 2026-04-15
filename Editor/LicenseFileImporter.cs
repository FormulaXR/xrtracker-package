using UnityEngine;
using UnityEditor;
using UnityEditor.AssetImporters;
using System;
using System.IO;
using System.Text;

namespace IV.FormulaTracker.Editor
{
	[ScriptedImporter(1, "lic")]
	public class LicenseFileImporter : ScriptedImporter
	{
		public override void OnImportAsset(AssetImportContext ctx)
		{
			var text = new TextAsset(File.ReadAllText(ctx.assetPath));
			ctx.AddObjectToAsset("license", text);
			ctx.SetMainObject(text);
		}
	}

	[CustomEditor(typeof(LicenseFileImporter))]
	public class LicenseFileImporterEditor : ScriptedImporterEditor
	{
		private bool _parsed;
		private bool _parseError;
		private string _errorMessage;

		// Parsed fields
		private string _licenseType;
		private string _licenseId;
		private string _licenseeName;
		private string _licenseeEmail;
		private string _machineId;
		private string _issuedAt;
		private string _expiresAt;
		private int _maxTrackedBodies;
		private bool _hasSignature;
		private int _formatVersion;

		public override void OnEnable()
		{
			base.OnEnable();
			ParseLicense();
		}

		private void ParseLicense()
		{
			_parsed = false;
			_parseError = false;

			try
			{
				string assetPath = ((AssetImporter)target).assetPath;
				string json = File.ReadAllText(assetPath);

				var outer = JsonUtility.FromJson<SignedLicenseEditorData>(json);
				if (outer == null || string.IsNullOrEmpty(outer.payload))
				{
					_parseError = true;
					_errorMessage = "Invalid license format: missing payload.";
					return;
				}

				_formatVersion = outer.version;
				_hasSignature = !string.IsNullOrEmpty(outer.signature);

				byte[] payloadBytes = Convert.FromBase64String(outer.payload);
				string payloadJson = Encoding.UTF8.GetString(payloadBytes);

				var payload = JsonUtility.FromJson<LicensePayloadEditorData>(payloadJson);
				if (payload == null)
				{
					_parseError = true;
					_errorMessage = "Invalid license format: cannot parse payload.";
					return;
				}

				_licenseType = payload.licenseType ?? "unknown";
				_licenseId = payload.licenseId ?? "";
				_licenseeName = payload.licenseeName ?? "";
				_licenseeEmail = payload.licenseeEmail ?? "";
				_machineId = payload.machineId ?? "";
				_issuedAt = payload.issuedAt ?? "";
				_expiresAt = payload.expiresAt ?? "";
				_maxTrackedBodies = payload.maxTrackedBodies;

				_parsed = true;
			}
			catch (FormatException)
			{
				_parseError = true;
				_errorMessage = "Invalid license format: Base64 decode failed.";
			}
			catch (Exception e)
			{
				_parseError = true;
				_errorMessage = $"Failed to parse license: {e.Message}";
			}
		}

		public override void OnInspectorGUI()
		{
			if (_parseError)
			{
				EditorGUILayout.HelpBox(_errorMessage, MessageType.Error);
				base.ApplyRevertGUI();
				return;
			}

			if (!_parsed)
			{
				EditorGUILayout.HelpBox("Unable to read license file.", MessageType.Warning);
				base.ApplyRevertGUI();
				return;
			}

			// Tier badge
			string tierLabel = FormatTier(_licenseType);
			Color tierColor = GetTierColor(_licenseType);
			var oldBg = GUI.backgroundColor;
			GUI.backgroundColor = tierColor;
			var style = new GUIStyle(EditorStyles.helpBox)
			{
				fontSize = 14,
				fontStyle = FontStyle.Bold,
				alignment = TextAnchor.MiddleCenter,
				padding = new RectOffset(8, 8, 6, 6),
			};
			style.normal.textColor = Color.white;
			EditorGUILayout.LabelField(tierLabel, style, GUILayout.Height(30));
			GUI.backgroundColor = oldBg;

			EditorGUILayout.Space(6);

			// Licensee
			EditorGUILayout.LabelField("Licensee", EditorStyles.boldLabel);
			ReadOnlyField("Name", _licenseeName);
			if (!string.IsNullOrEmpty(_licenseeEmail))
				ReadOnlyField("Email", _licenseeEmail);

			EditorGUILayout.Space(4);

			// Dates
			EditorGUILayout.LabelField("Validity", EditorStyles.boldLabel);
			if (!string.IsNullOrEmpty(_issuedAt))
				ReadOnlyField("Issued", FormatDate(_issuedAt));

			if (!string.IsNullOrEmpty(_expiresAt))
			{
				string expiryDisplay = FormatDate(_expiresAt);
				bool expired = IsExpired(_expiresAt);
				if (expired)
					expiryDisplay += "  (EXPIRED)";
				ReadOnlyField("Expires", expiryDisplay);

				if (expired)
					EditorGUILayout.HelpBox("This license has expired.", MessageType.Warning);
			}
			else if (_licenseType != "free")
			{
				ReadOnlyField("Expires", "Never");
			}

			EditorGUILayout.Space(4);

			// Binding
			if (!string.IsNullOrEmpty(_machineId))
			{
				EditorGUILayout.LabelField("Binding", EditorStyles.boldLabel);
				ReadOnlyField("Machine ID", _machineId);
				EditorGUILayout.Space(4);
			}

			// Details
			EditorGUILayout.LabelField("Details", EditorStyles.boldLabel);
			if (!string.IsNullOrEmpty(_licenseId))
				ReadOnlyField("License ID", _licenseId);
			ReadOnlyField("Max Bodies", _maxTrackedBodies <= 0 ? "Unlimited" : _maxTrackedBodies.ToString());
			ReadOnlyField("Signed", _hasSignature ? "Yes (RSA-2048)" : "No");
			ReadOnlyField("Format Version", _formatVersion.ToString());

			base.ApplyRevertGUI();
		}

		private static void ReadOnlyField(string label, string value)
		{
			var rect = EditorGUILayout.GetControlRect();
			rect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
			EditorGUI.SelectableLabel(rect, value, EditorStyles.label);
		}

		private static string FormatTier(string licenseType)
		{
			switch (licenseType?.ToLowerInvariant())
			{
				case "free": return "Free License (Legacy)";
				case "trial": return "Trial License (Legacy)";
				case "developer": return "Developer License";
				case "commercial": return "Commercial License";
				case "oem": return "OEM License";
				default: return licenseType ?? "Unknown";
			}
		}

		private static Color GetTierColor(string licenseType)
		{
			switch (licenseType?.ToLowerInvariant())
			{
				case "free": return new Color(0.45f, 0.45f, 0.45f);
				case "trial": return new Color(0.85f, 0.55f, 0.1f);
				case "developer": return new Color(0.2f, 0.55f, 0.8f);
				case "commercial": return new Color(0.2f, 0.7f, 0.3f);
				case "oem": return new Color(0.55f, 0.3f, 0.7f);
				default: return new Color(0.5f, 0.5f, 0.5f);
			}
		}

		private static string FormatDate(string iso8601)
		{
			if (DateTime.TryParse(iso8601, null,
				System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
				return dt.ToString("yyyy-MM-dd");
			return iso8601;
		}

		private static bool IsExpired(string iso8601)
		{
			if (DateTime.TryParse(iso8601, null,
				System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
				return dt < DateTime.UtcNow;
			return false;
		}

		// JSON helper types (editor-only, mirrors license format)
		[Serializable]
		private class SignedLicenseEditorData
		{
			public string payload;
			public string signature;
			public int version;
		}

		[Serializable]
		private class LicensePayloadEditorData
		{
			public string licenseId;
			public string licenseType;
			public string licenseeName;
			public string licenseeEmail;
			public string machineId;
			public string issuedAt;
			public string expiresAt;
			public int maxTrackedBodies;
		}
	}
}
