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

		[Tooltip("Repeats the start frame of the range on every step and loops on it, so the tracker refines the pose " +
			"on that one image again and again. The end of the range and the Loop setting are not used while this is on.")]
		[SerializeField] private bool _holdStartFrame = false;

		[SerializeField] private bool _useCustomStart = false;
		[SerializeField] private int _rangeStart = 0;
		[SerializeField] private bool _useCustomEnd = false;
		[SerializeField] private int _rangeEnd = 0;

		private bool _isPlaying;
		private bool _isInitialized;
		private bool _holdApplied;

		#region Public Properties

		public bool IsPlaying => _isPlaying;
		public int CurrentFrame => FTBridge.FT_GetSequenceCurrentFrame();
		public int FrameCount => FTBridge.FT_GetSequenceFrameCount();
		public int StartIndex => FTBridge.FT_GetSequenceStartIndex();
		public bool Loop
		{
			get => _loop;
			set
			{
				_loop = value;
				if (!_isInitialized) return;
				XRTrackerManager.WaitForTrackingStepToReturn();
				FTBridge.FT_SequenceSetLoop(EffectiveLoop);
			}
		}

		/// <summary>
		/// Whether every step runs on the start frame of the range. Turning it on while the sequence plays puts the player on that
		/// frame at once.
		/// </summary>
		public bool HoldStartFrame
		{
			get => _holdStartFrame;
			set
			{
				_holdStartFrame = value;
				ApplySettingsToPlayer();
			}
		}

		public bool UseCustomStart { get => _useCustomStart; set => _useCustomStart = value; }
		public int RangeStart { get => _rangeStart; set => _rangeStart = value; }
		public bool UseCustomEnd { get => _useCustomEnd; set => _useCustomEnd = value; }
		public int RangeEnd { get => _rangeEnd; set => _rangeEnd = value; }

		/// <summary>Whether the player loops. Holding the start frame loops on that frame whatever <see cref="Loop"/> says.</summary>
		public bool EffectiveLoop => _loop || _holdStartFrame;

		public int EffectiveStartFrame => _useCustomStart
			? Mathf.Clamp(_rangeStart, StartIndex, StartIndex + FrameCount - 1)
			: StartIndex;

		public int EffectiveEndFrame => _holdStartFrame
			? EffectiveStartFrame
			: _useCustomEnd
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
			if (!Application.isPlaying) return;
			ApplySettingsToPlayer();
		}

		private void OnTrackerReady()
		{
			var manager = XRTrackerManager.Instance;
			if (manager == null || manager.CurrentImageSource != ImageSource.Sequence)
				return;

			_isInitialized = true;

			// The first step is queued before this event fires, so it may still be in flight.
			XRTrackerManager.WaitForTrackingStepToReturn();

			// Apply settings
			FTBridge.FT_SequenceSetLoop(EffectiveLoop);

			// Apply playback range if custom start or end is set, or if one frame is held
			if (_useCustomStart || _useCustomEnd || _holdStartFrame)
				FTBridge.FT_SequenceSetPlaybackRange(EffectiveStartFrame, EffectiveEndFrame);

			// Seek to the start of the range if a custom start is set or if one frame is held
			if (_useCustomStart || _holdStartFrame)
				FTBridge.FT_SequenceSeek(EffectiveStartFrame);
			_holdApplied = _holdStartFrame;

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
			XRTrackerManager.WaitForTrackingStepToReturn();
			FTBridge.FT_SequencePlay();
		}

		public void Pause()
		{
			if (!_isInitialized) return;
			_isPlaying = false;
			XRTrackerManager.WaitForTrackingStepToReturn();
			FTBridge.FT_SequencePause();
		}

		public void Step(int delta = 1)
		{
			if (!_isInitialized) return;
			_isPlaying = false;
			XRTrackerManager.WaitForTrackingStepToReturn();
			FTBridge.FT_SequenceStep(delta);
		}

		public void Seek(int frameIndex)
		{
			if (!_isInitialized) return;
			int clamped = Mathf.Clamp(frameIndex, EffectiveStartFrame, EffectiveEndFrame);
			XRTrackerManager.WaitForTrackingStepToReturn();
			FTBridge.FT_SequenceSeek(clamped);
		}

		#endregion

		/// <summary>
		/// Sends the loop and the range to the running player, and puts the player on the start frame when the hold of that frame
		/// has just been turned on.
		/// </summary>
		private void ApplySettingsToPlayer()
		{
			if (!_isInitialized) return;
			XRTrackerManager.WaitForTrackingStepToReturn();
			FTBridge.FT_SequenceSetLoop(EffectiveLoop);
			FTBridge.FT_SequenceSetPlaybackRange(EffectiveStartFrame, EffectiveEndFrame);
			if (_holdStartFrame && !_holdApplied)
				FTBridge.FT_SequenceSeek(EffectiveStartFrame);
			_holdApplied = _holdStartFrame;
		}
	}
}
