using UnityEngine;
using UnityEngine.UI;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Renders the camera feed as a UI RawImage background.
	/// Alternative to FormulaBackgroundRenderer for setups where camera-based
	/// compositing doesn't work (e.g., URP with RenderTexture targets).
	/// Automatically enables/disables based on tracking state.
	/// </summary>
	public class TrackerBackgroundUI : MonoBehaviour
	{
		[SerializeField, Tooltip("RawImage to display the camera feed")]
		private RawImage _backgroundImage;

		[SerializeField, Tooltip("Reference RectTransform to match size against (e.g., viewport)")]
		private RectTransform _viewportRect;

		private XRTrackerManager Manager => XRTrackerManager.Instance;
		private RectTransform _backgroundRect;
		private bool _isSubscribed;
		private Vector2 _lastViewportSize;

		private void Awake()
		{
			if (_backgroundImage != null)
				_backgroundRect = _backgroundImage.rectTransform;
		}

		private void Start()
		{
			if (_backgroundImage == null)
			{
				Debug.LogError("[FormulaBackgroundUI] No background RawImage assigned!", this);
				enabled = false;
				return;
			}

			if (_viewportRect == null)
			{
				Debug.LogError("[FormulaBackgroundUI] No viewport RectTransform assigned!", this);
				enabled = false;
				return;
			}

			SubscribeToManager();

			// Check if tracking is already active (we may have missed the event)
			if (Manager != null && Manager.IsTrackingReady)
			{
				SetVisible(true);
				UpdateSize();
			}
			else
			{
				SetVisible(false);
			}
		}

		private void OnEnable()
		{
			if (!_isSubscribed)
				SubscribeToManager();

			// Sync visibility with current tracking state
			if (Manager != null && Manager.IsTrackingReady)
			{
				SetVisible(true);
				UpdateSize();
			}
		}

		private void OnDisable()
		{
			SetVisible(false);
		}

		private void LateUpdate()
		{
			// Check for viewport size changes (e.g., window resize)
			if (_viewportRect != null)
			{
				Vector2 currentSize = _viewportRect.rect.size;
				if (currentSize != _lastViewportSize)
				{
					_lastViewportSize = currentSize;
					UpdateSize();
				}
			}
		}

		private void OnDestroy()
		{
			UnsubscribeFromManager();
		}

		private void SubscribeToManager()
		{
			if (Manager == null || _isSubscribed)
				return;

			Manager.OnImage += UpdateTexture;
			Manager.OnCropFactorsChanged += UpdateSize;
			Manager.OnTrackingResumed += OnTrackingResumed;
			Manager.OnTrackingPaused += OnTrackingPaused;
			_isSubscribed = true;
		}

		private void UnsubscribeFromManager()
		{
			if (Manager == null || !_isSubscribed)
				return;

			Manager.OnImage -= UpdateTexture;
			Manager.OnCropFactorsChanged -= UpdateSize;
			Manager.OnTrackingResumed -= OnTrackingResumed;
			Manager.OnTrackingPaused -= OnTrackingPaused;
			_isSubscribed = false;
		}

		private void OnTrackingResumed()
		{
			SetVisible(true);
			UpdateSize();
		}

		private void OnTrackingPaused()
		{
			SetVisible(false);
		}

		private void SetVisible(bool visible)
		{
			if (_backgroundImage != null)
				_backgroundImage.enabled = visible;
		}

		private void UpdateTexture(Texture texture)
		{
			if (_backgroundImage == null)
				return;

			_backgroundImage.texture = texture;

			// Show on first image received (tracking is active)
			if (!_backgroundImage.enabled)
				SetVisible(true);
		}

		private void UpdateSize()
		{
			if (_backgroundRect == null || _viewportRect == null)
				return;

			float cropX = Manager != null ? Manager.CropFactorX : 0f;
			float cropY = Manager != null ? Manager.CropFactorY : 0f;

			// Ensure non-negative crop factors
			cropX = Mathf.Max(0f, cropX);
			cropY = Mathf.Max(0f, cropY);

			// Get viewport size and track for resize detection
			Vector2 viewportSize = _viewportRect.rect.size;
			_lastViewportSize = viewportSize;

			// Calculate background size (larger than viewport to allow cropping)
			float bgWidth = cropX > 0 ? viewportSize.x / (1f - cropX) : viewportSize.x;
			float bgHeight = cropY > 0 ? viewportSize.y / (1f - cropY) : viewportSize.y;

			// Apply size
			_backgroundRect.sizeDelta = new Vector2(bgWidth, bgHeight);

			// Center the background relative to viewport
			_backgroundRect.anchoredPosition = Vector2.zero;
		}

		/// <summary>
		/// Manually set the background RawImage at runtime.
		/// </summary>
		public void SetBackgroundImage(RawImage image)
		{
			_backgroundImage = image;
			_backgroundRect = image != null ? image.rectTransform : null;
			UpdateSize();
		}

		/// <summary>
		/// Manually set the viewport reference at runtime.
		/// </summary>
		public void SetViewportRect(RectTransform viewport)
		{
			_viewportRect = viewport;
			UpdateSize();
		}
	}
}
