using UnityEngine;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// Thin playback controller for sequence mode.
	/// Tracking is driven by TrackingCoroutine (same as native mode) —
	/// this component only controls play/pause/seek/step/loop.
	/// </summary>
	public class SequencePlayerFeeder : MonoBehaviour
	{
		[Tooltip("Auto-play on tracker initialization")]
		[SerializeField] private bool _autoPlay = true;

		[SerializeField] private bool _loop = false;

		[SerializeField] private bool _useCustomStart = false;
		[SerializeField] private int _rangeStart = 0;
		[SerializeField] private bool _useCustomEnd = false;
		[SerializeField] private int _rangeEnd = 0;

		private bool _isPlaying;
		private bool _isInitialized;

		#region Public Properties

		public bool IsPlaying => _isPlaying;
		public int CurrentFrame => FTBridge.FT_GetSequenceCurrentFrame();
		public int FrameCount => FTBridge.FT_GetSequenceFrameCount();
		public int StartIndex => FTBridge.FT_GetSequenceStartIndex();
		public bool Loop { get => _loop; set { _loop = value; if (_isInitialized) FTBridge.FT_SequenceSetLoop(value); } }

		public bool UseCustomStart { get => _useCustomStart; set => _useCustomStart = value; }
		public int RangeStart { get => _rangeStart; set => _rangeStart = value; }
		public bool UseCustomEnd { get => _useCustomEnd; set => _useCustomEnd = value; }
		public int RangeEnd { get => _rangeEnd; set => _rangeEnd = value; }

		public int EffectiveStartFrame => _useCustomStart
			? Mathf.Clamp(_rangeStart, StartIndex, StartIndex + FrameCount - 1)
			: StartIndex;

		public int EffectiveEndFrame => _useCustomEnd
			? Mathf.Clamp(_rangeEnd, EffectiveStartFrame, StartIndex + FrameCount - 1)
			: StartIndex + FrameCount - 1;

		#endregion

		#region Unity Lifecycle

		private void OnEnable()
		{
			XRTrackerManager.OnTrackerInitialized += OnTrackerReady;
		}

		private void OnDisable()
		{
			XRTrackerManager.OnTrackerInitialized -= OnTrackerReady;
		}

		private void OnValidate()
		{
			if (!Application.isPlaying || !_isInitialized) return;
			FTBridge.FT_SequenceSetLoop(_loop);
			FTBridge.FT_SequenceSetPlaybackRange(EffectiveStartFrame, EffectiveEndFrame);
		}

		private void OnTrackerReady()
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null || manager.CurrentImageSource != ImageSource.Sequence)
				return;

			_isInitialized = true;

			// Apply settings
			FTBridge.FT_SequenceSetLoop(_loop);

			// Apply playback range if custom start or end is set
			if (_useCustomStart || _useCustomEnd)
				FTBridge.FT_SequenceSetPlaybackRange(EffectiveStartFrame, EffectiveEndFrame);

			// Seek to custom start if set
			if (_useCustomStart)
				FTBridge.FT_SequenceSeek(EffectiveStartFrame);

			if (_autoPlay)
				Play();
			else
				Pause();
		}

		#endregion

		#region Public API

		public void Play()
		{
			if (!_isInitialized) return;
			_isPlaying = true;
			FTBridge.FT_SequencePlay();
		}

		public void Pause()
		{
			if (!_isInitialized) return;
			_isPlaying = false;
			FTBridge.FT_SequencePause();
		}

		public void Step(int delta = 1)
		{
			if (!_isInitialized) return;
			_isPlaying = false;
			FTBridge.FT_SequenceStep(delta);
		}

		public void Seek(int frameIndex)
		{
			if (!_isInitialized) return;
			int clamped = Mathf.Clamp(frameIndex, EffectiveStartFrame, EffectiveEndFrame);
			FTBridge.FT_SequenceSeek(clamped);
		}

		#endregion
	}
}
