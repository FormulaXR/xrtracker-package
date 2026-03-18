using UnityEngine;

namespace IV.FormulaTracker.Validation
{
	/// <summary>
	/// Debug UI component for visualizing validation zone results using OnGUI.
	/// Displays the sampled image, template, readiness status, and validation results.
	/// </summary>
	public class ValidationZoneDebugUI : MonoBehaviour
	{
		[Header("Validation Zone")]
		[Tooltip("The validation zone to visualize. If not set, will search on this GameObject.")]
		[SerializeField]
		private ValidationZone _validationZone;

		[Header("Display Settings")]
		[Tooltip("Position of the debug panel on screen")]
		[SerializeField]
		private Vector2 _position = new Vector2(10, 10);

		[Tooltip("Width of displayed images as percentage of screen width (0-1)")]
		[SerializeField]
		[Range(0.05f, 0.5f)]
		private float _displayWidthPercent = 0.1f;

		[Tooltip("Show the template image alongside sampled image")]
		[SerializeField]
		private bool _showTemplate = true;

		[Header("Colors")]
		[SerializeField]
		private Color _passColor = new Color(0.2f, 0.8f, 0.2f, 1f);

		[SerializeField]
		private Color _failColor = new Color(0.8f, 0.2f, 0.2f, 1f);

		[SerializeField]
		private Color _notReadyColor = new Color(0.6f, 0.6f, 0.6f, 1f);

		// Cached styles
		private GUIStyle _boxStyle;
		private GUIStyle _labelStyle;
		private GUIStyle _headerStyle;
		private GUIStyle _resultStyle;
		private Texture2D _backgroundTex;
		private Texture2D _passBackgroundTex;
		private Texture2D _failBackgroundTex;
		private Texture2D _notReadyBackgroundTex;

		private void Start()
		{
			if (_validationZone == null)
				_validationZone = GetComponent<ValidationZone>();
		}

		private void OnDestroy()
		{
			if (_backgroundTex != null) Destroy(_backgroundTex);
			if (_passBackgroundTex != null) Destroy(_passBackgroundTex);
			if (_failBackgroundTex != null) Destroy(_failBackgroundTex);
			if (_notReadyBackgroundTex != null) Destroy(_notReadyBackgroundTex);
		}

		private void InitStyles()
		{
			if (_boxStyle != null) return;

			// Create background textures
			_backgroundTex = MakeTex(1, 1, new Color(0.1f, 0.1f, 0.1f, 0.9f));
			_passBackgroundTex = MakeTex(1, 1, _passColor);
			_failBackgroundTex = MakeTex(1, 1, _failColor);
			_notReadyBackgroundTex = MakeTex(1, 1, _notReadyColor);

			_boxStyle = new GUIStyle(GUI.skin.box)
			{
				normal = { background = _backgroundTex }
			};

			_labelStyle = new GUIStyle(GUI.skin.label)
			{
				fontSize = 12,
				normal = { textColor = Color.white }
			};

			_headerStyle = new GUIStyle(GUI.skin.label)
			{
				fontSize = 14,
				fontStyle = FontStyle.Bold,
				normal = { textColor = Color.white }
			};

			_resultStyle = new GUIStyle(GUI.skin.box)
			{
				fontSize = 16,
				fontStyle = FontStyle.Bold,
				alignment = TextAnchor.MiddleCenter,
				normal = { textColor = Color.white }
			};
		}

		private Texture2D MakeTex(int width, int height, Color color)
		{
			var pixels = new Color[width * height];
			for (int i = 0; i < pixels.Length; i++)
				pixels[i] = color;

			var tex = new Texture2D(width, height);
			tex.SetPixels(pixels);
			tex.Apply();
			return tex;
		}

		private void OnGUI()
		{
			if (_validationZone == null) return;

			InitStyles();

			float x = _position.x;
			float y = _position.y;
			float padding = 8;

			// Get sampled image first to determine aspect ratio
			var sampledTex = _validationZone.GetSampledImage();

			// Use screen percentage for width, calculate height from aspect ratio
			float aspectRatio = 2f; // default 2:1 if no texture
			if (sampledTex != null && sampledTex.width > 0 && sampledTex.height > 0)
				aspectRatio = (float)sampledTex.width / sampledTex.height;

			float scaledWidth = Screen.width * Mathf.Clamp(_displayWidthPercent, 0.05f, 0.5f);
			float scaledHeight = Mathf.Max(scaledWidth / aspectRatio, 1f);

			// Calculate panel size
			float panelWidth = scaledWidth + padding * 2;
			if (_showTemplate && _validationZone.TemplateTexture != null)
				panelWidth = scaledWidth * 2 + padding * 3;

			float panelHeight = scaledHeight + 122 + padding * 2;

			// Draw background panel
			GUI.Box(new Rect(x, y, panelWidth, panelHeight), GUIContent.none, _boxStyle);

			float contentX = x + padding;
			float contentY = y + padding;

			// Header
			string zoneId = _validationZone.ZoneId;
			if (string.IsNullOrEmpty(zoneId))
				zoneId = _validationZone.gameObject.name;
			GUI.Label(new Rect(contentX, contentY, panelWidth - padding * 2, 20), $"Zone: {zoneId}", _headerStyle);
			contentY += 22;

			// Images row
			float imagesY = contentY;

			// Sampled image (already retrieved above)
			if (sampledTex != null)
			{
				GUI.DrawTexture(new Rect(contentX, imagesY, scaledWidth, scaledHeight), sampledTex, ScaleMode.ScaleToFit);
			}
			else
			{
				GUI.Box(new Rect(contentX, imagesY, scaledWidth, scaledHeight), "No Image", _labelStyle);
			}

			// Template image
			if (_showTemplate && _validationZone.TemplateTexture != null)
			{
				float templateX = contentX + scaledWidth + padding;
				GUI.DrawTexture(new Rect(templateX, imagesY, scaledWidth, scaledHeight), _validationZone.TemplateTexture, ScaleMode.ScaleToFit);
			}

			contentY = imagesY + scaledHeight + padding;

			// Status and result
			var result = _validationZone.LastValidationResult;

			// Readiness status
			string statusText = "No validation yet";
			if (result != null)
			{
				statusText = result.Readiness switch
				{
					FTValidationReadiness.Ready => $"Ready (Visibility: {result.VisibilityScore:P0})",
					FTValidationReadiness.ZoneNotVisible => "Zone Not Visible - Rotate object",
					FTValidationReadiness.ZoneOutOfFrame => "Zone Out of Frame - Move closer",
					FTValidationReadiness.TrackingUnstable => "Tracking Unstable - Hold steady",
					FTValidationReadiness.TrackingLost => "Tracking Lost",
					_ => "Unknown"
				};
			}
			GUI.Label(new Rect(contentX, contentY, panelWidth - padding * 2, 20), statusText, _labelStyle);
			contentY += 22;

			// Offset info
			if (result != null && result.ValidationAttempted && result.ValidatorResults != null && result.ValidatorResults.Length > 0)
			{
				var vr = result.ValidatorResults[0];
				string offsetText = $"Offset: ({vr.OffsetX:F1}, {vr.OffsetY:F1})";
				GUI.Label(new Rect(contentX, contentY, panelWidth - padding * 2, 20), offsetText, _labelStyle);
				contentY += 22;
			}

			// Result box
			string resultText = "---";
			Texture2D resultBg = _notReadyBackgroundTex;

			if (result != null && result.ValidationAttempted)
			{
				float confidence = 0f;
				if (result.ValidatorResults != null && result.ValidatorResults.Length > 0)
					confidence = result.ValidatorResults[0].Confidence;

				if (result.Passed)
				{
					resultText = $"OK ({confidence:P0})";
					resultBg = _passBackgroundTex;
				}
				else
				{
					resultText = $"NG ({confidence:P0})";
					resultBg = _failBackgroundTex;
				}
			}

			_resultStyle.normal.background = resultBg;
			GUI.Box(new Rect(contentX, contentY, panelWidth - padding * 2, 30), resultText, _resultStyle);
		}

		/// <summary>
		/// Set the validation zone to visualize at runtime.
		/// </summary>
		public void SetValidationZone(ValidationZone zone)
		{
			_validationZone = zone;
		}
	}
}
